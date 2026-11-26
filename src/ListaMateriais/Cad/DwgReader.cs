namespace ListaMateriais.Cad;

/// <summary>
/// Reads the drawing inside a DWG file: its layers, blocks, and what is drawn in the model (lines, polylines,
/// arcs, circles, ellipses, splines, points, texts and block references). Works for AutoCAD 2000 to 2018 files.
/// </summary>
internal static class DwgReader
{
    // Object type numbers of the DWG format.
    private const int TypeText = 1;
    private const int TypeBlock = 4;
    private const int TypeInsert = 7;
    private const int TypeVertex2D = 10;
    private const int TypeVertex3D = 11;
    private const int TypePolyline2D = 15;
    private const int TypePolyline3D = 16;
    private const int TypeArc = 17;
    private const int TypeCircle = 18;
    private const int TypeLine = 19;
    private const int TypePoint = 27;
    private const int TypeEllipse = 35;
    private const int TypeSpline = 36;
    private const int TypeMText = 44;
    private const int TypeBlockHeader = 49;
    private const int TypeLayer = 51;
    private const int TypeLwPolyline = 77;

    public static CadDrawing Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = Files.ReadAllBytesShared(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AppException($"Não foi possível abrir \"{Path.GetFileName(path)}\".\n\nDetalhe: {e.Message}", e);
        }
        return Read(bytes);
    }

    public static CadDrawing Read(byte[] bytes)
    {
        var file = DwgFile.Read(bytes);
        var state = new State(file) { Classes = ReadClasses(file) };
        foreach (var (handle, location) in ReadObjectMap(file.Handles, state))
        {
            try
            {
                ReadObject(state, handle, location);
            }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                // One damaged object does not stop the others; the page tells how many were lost.
                state.Damaged++;
            }
        }
        var result = Assemble(state);
        result.Unreadable = state.Damaged;
        return result;
    }

    // ------------------------------------------------------------------------------------------ classes

    private static Dictionary<int, string> ReadClasses(DwgFile file)
    {
        var classes = new Dictionary<int, string>();
        var data = file.Classes;
        try
        {
            if (file.Version == DwgVersion.R2000)
            {
                // Sentinel (16 bytes), size (4), the classes, 2 bytes of CRC, sentinel.
                var size = BitConverter.ToInt32(data, 16);
                var reader = new BitReader(data, 20 * 8L, (20L + size) * 8);
                while (reader.Position < reader.EndPosition - 8)
                {
                    var number = reader.BS();
                    reader.BS();
                    reader.TV(file.Encoding);
                    reader.TV(file.Encoding);
                    var dxf = reader.TV(file.Encoding);
                    reader.B();
                    reader.BS();
                    classes[number] = dxf;
                }
            }
            else
            {
                ReadClasses2004(file, data, classes);
            }
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            // Without the class names only the entities of fixed type are read (not the LWPOLYLINE ones).
        }
        return classes;
    }

    private static void ReadClasses2004(DwgFile file, byte[] data, Dictionary<int, string> classes)
    {
        var modern = file.Version >= DwgVersion.R2010;
        // Layouts differ by one 32-bit field between releases of AutoCAD 2010; try the likely one first, then the other.
        foreach (var hasSecondSize in modern ? new[] { file.Maintenance > 3 || file.Version >= DwgVersion.R2018, file.Maintenance <= 3 } : [false])
        {
            var found = new Dictionary<int, string>();
            try
            {
                var reader = new BitReader(data, 16 * 8L, data.Length * 8L);
                reader.RL();                                     // size of the class data
                if (hasSecondSize)
                    reader.RL();
                var sizeFieldStart = reader.Position;
                var bitSize = modern ? reader.RL() : 0;
                var max = reader.BS();
                reader.RC();
                reader.RC();
                reader.B();
                var count = max - 499;
                if (count < 0 || count > 5000)
                    continue;

                BitReader? text = null;
                if (modern)
                {
                    // The class names are in a separate stream at the end of the class data.
                    text = StringStreamAt(data, sizeFieldStart + bitSize - 1);
                    if (text is null)
                        continue;
                }
                for (var i = 0; i < count; i++)
                {
                    var number = reader.BS();
                    reader.BS();
                    string dxf;
                    if (modern)
                    {
                        text!.TU();
                        text.TU();
                        dxf = text.TU();
                    }
                    else
                    {
                        reader.TV(file.Encoding);
                        reader.TV(file.Encoding);
                        dxf = reader.TV(file.Encoding);
                    }
                    reader.B();
                    reader.BS();
                    reader.BL();
                    reader.BL();
                    reader.BL();
                    reader.BL();
                    reader.BL();
                    found[number] = dxf;
                }
            }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                continue;
            }
            if (found.Values.Any(n => n.Equals("LWPOLYLINE", StringComparison.OrdinalIgnoreCase)) || found.Count > 0)
            {
                foreach (var (k, v) in found)
                    classes[k] = v;
                return;
            }
        }
    }

    /// <summary>
    /// The text stream of an R2007+ data block: its last bit says whether there is one, the 16 (or 32) bits
    /// before say how long it is, and it sits right before those.
    /// </summary>
    private static BitReader? StringStreamAt(byte[] data, long flagBit)
    {
        if (flagBit < 33 || flagBit >= (long)data.Length * 8)
            return null;
        var probe = new BitReader(data, flagBit, (long)data.Length * 8);
        if (!probe.B())
            return null;
        var position = flagBit - 16;
        var sizeReader = new BitReader(data, position, (long)data.Length * 8);
        long size = sizeReader.RS() & 0xFFFF;
        if ((size & 0x8000) != 0)
        {
            position -= 16;
            var high = new BitReader(data, position, (long)data.Length * 8).RS() & 0xFFFF;
            size = (size & 0x7FFF) | ((long)high << 15);
        }
        var start = position - size;
        return start < 0 ? null : new BitReader(data, start, position);
    }

    // ------------------------------------------------------------------------------------- object map

    /// <summary>The handle and the position of every object in the file.</summary>
    private static List<(ulong Handle, long Location)> ReadObjectMap(byte[] map, State state)
    {
        var entries = new List<(ulong, long)>();
        var position = 0;
        try
        {
            while (position + 2 <= map.Length)
            {
                var size = (map[position] << 8) | map[position + 1];
                if (size <= 2)
                    break;
                var end = Math.Min(position + size, map.Length);
                var reader = new BitReader(map, (position + 2) * 8L, end * 8L);
                long handle = 0;
                long location = 0;
                while (reader.Position < reader.EndPosition)
                {
                    handle += (long)Unsigned(reader);
                    location += Signed(reader);
                    entries.Add(((ulong)handle, location));
                }
                position += size + 2;
            }
        }
        catch (InvalidDataException)
        {
            // A damaged table: what was read before it is still used.
            state.Damaged++;
        }
        if (entries.Count == 0)
            throw new AppException("O arquivo DWG está danificado ou tem uma estrutura que o programa não entende.");
        return entries;
    }

    private static ulong Unsigned(BitReader reader)
    {
        ulong value = 0;
        for (var shift = 0; shift < 56; shift += 7)
        {
            var b = reader.RC();
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
        }
        return value;
    }

    private static long Signed(BitReader reader)
    {
        long value = 0;
        for (var shift = 0; shift < 56; shift += 7)
        {
            var b = reader.RC();
            if ((b & 0x80) == 0)
            {
                value |= (long)(b & 0x3F) << shift;
                return (b & 0x40) != 0 ? -value : value;
            }
            value |= (long)(b & 0x7F) << shift;
        }
        return value;
    }

    // ----------------------------------------------------------------------------------------- objects

    private sealed class State(DwgFile file)
    {
        public DwgFile File { get; } = file;

        public Dictionary<int, string> Classes { get; set; } = [];

        public Dictionary<ulong, (string Name, int Index, int? Rgb, bool Hidden)> Layers { get; } = [];

        public Dictionary<ulong, string> BlockNames { get; } = [];

        public Dictionary<ulong, CadPoint> BlockBases { get; } = [];

        public List<Raw> Entities { get; } = [];

        public int Damaged { get; set; }

        public int Skipped { get; set; }
    }

    /// <summary>An entity as read, before the layer and block names are looked up.</summary>
    private sealed class Raw
    {
        public ulong Handle;
        public int EntMode;
        public ulong Owner;
        public ulong LayerHandle;
        public ulong BlockHandle;
        public CadEntity? Entity;
        public bool IsPolyline;
        public bool IsVertex;
        public int VertexFlag;
        public double Bulge;
        public ulong[] Owned = [];
        public ulong FirstVertex;
        public bool Polyline3D;
        public int PolylineFlag;
    }

    /// <summary>The streams of one object: its data, its text (R2007+) and its handles.</summary>
    private sealed class Obj
    {
        public required BitReader Data { get; init; }

        public required BitReader Handles { get; init; }

        public required DwgFile File { get; init; }

        public required ulong Handle { get; set; }

        public BitReader? Strings { get; init; }

        public string Text() => File.Version >= DwgVersion.R2007
            ? (Strings ?? throw new InvalidDataException("Texto ausente.")).TU()
            : Data.TV(File.Encoding);
    }

    private static void ReadObject(State state, ulong mapHandle, long location)
    {
        var file = state.File;
        var objects = file.Objects;
        if (location < 0 || location >= objects.Length)
            return;
        var version = file.Version;

        var head = new BitReader(objects, location * 8, objects.Length * 8L);
        var size = head.MS();
        long handleBits = 0;
        if (version >= DwgVersion.R2010)
            handleBits = (long)head.UMC();
        var start = head.Position;
        var end = start + size * 8L;
        if (size <= 0 || end > objects.Length * 8L)
            throw new InvalidDataException("Objeto fora do arquivo.");
        var data = new BitReader(objects, start, end);

        var type = version >= DwgVersion.R2010 ? data.BOT() : data.BS();
        long bitSize;
        if (version >= DwgVersion.R2010)
            bitSize = size * 8L - handleBits;
        else
            bitSize = data.RL();
        if (bitSize <= 0 || bitSize > size * 8L)
            throw new InvalidDataException("Tamanho de objeto inválido.");

        var wanted = type switch
        {
            TypeLayer or TypeBlockHeader => true,
            TypeText or TypeInsert or TypeVertex2D or TypeVertex3D or TypePolyline2D or TypePolyline3D or TypeArc or TypeCircle
                or TypeLine or TypePoint or TypeEllipse or TypeSpline or TypeMText => true,
            _ => false,
        };
        string? className = null;
        if (type >= 500)
        {
            state.Classes.TryGetValue(type, out className);
            if (string.Equals(className, "LWPOLYLINE", StringComparison.OrdinalIgnoreCase))
                type = TypeLwPolyline;
            else
                wanted = false;
        }
        wanted |= type == TypeLwPolyline;

        // Entity kinds this reader does not draw are only counted, so the page can say something was left out.
        var countOnly = !wanted && IsDrawingEntity(type, className);
        if (!wanted && !countOnly)
            return;

        BitReader? strings = version >= DwgVersion.R2007 ? StringStreamAt(objects, start + bitSize - 1, start) : null;
        var obj = new Obj
        {
            Data = data,
            Handles = new BitReader(objects, start + bitSize, end),
            File = file,
            Handle = mapHandle,
            Strings = strings,
        };
        obj.Handle = data.Handle(out _);
        // Extended entity data: skipped.
        for (var guard = 0; guard < 10000; guard++)
        {
            var length = data.BS();
            if (length == 0)
                break;
            data.Handle(out _);
            data.Skip(length * 8L);
        }

        switch (type)
        {
            case TypeLayer:
                ReadLayer(state, obj);
                return;
            case TypeBlockHeader:
                ReadBlockHeader(state, obj);
                return;
        }

        var raw = new Raw { Handle = obj.Handle };
        var common = ReadCommon(obj);
        raw.EntMode = common.EntMode;
        if (common.Invisible && wanted)
            return;
        if (countOnly)
        {
            if (common.EntMode == 2)
                state.Skipped++;
            return;
        }

        var entity = type == TypeVertex2D || type == TypeVertex3D ? null : ReadEntityData(obj, type, className, raw);
        if (type == TypeVertex2D)
            ReadVertex2D(obj, raw);
        else if (type == TypeVertex3D)
            ReadVertex3D(obj, raw);
        if (entity is not null)
            raw.Entity = entity;
        ReadCommonHandles(obj, common, raw);
        ReadSpecificHandles(obj, type, raw);

        if (entity is not null)
        {
            entity.ColorIndex = common.ColorIndex;
            entity.Rgb = common.Rgb;
        }
        state.Entities.Add(raw);
    }

    /// <summary>Entity types that are real drawing (not table or structure objects) and are not drawn by this reader.</summary>
    private static bool IsDrawingEntity(int type, string? className) =>
        type switch
        {
            2 or 3 or 8 => true,
            >= 20 and <= 26 => true,       // dimensions
            28 or 29 or 30 or 31 or 32 or 33 => true,
            37 or 38 or 39 or 40 or 41 => true,
            45 or 46 or 47 => true,
            >= 500 => className is not null && !string.Equals(className, "LWPOLYLINE", StringComparison.OrdinalIgnoreCase)
                && (className.Equals("HATCH", StringComparison.OrdinalIgnoreCase) || className.Equals("IMAGE", StringComparison.OrdinalIgnoreCase)
                    || className.Equals("MLEADER", StringComparison.OrdinalIgnoreCase) || className.Equals("WIPEOUT", StringComparison.OrdinalIgnoreCase)
                    || className.Equals("3DSOLID", StringComparison.OrdinalIgnoreCase) || className.Contains("DIMENSION", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };

    private static BitReader? StringStreamAt(byte[] data, long flagBit, long objectStart)
    {
        var reader = StringStreamAt(data, flagBit);
        return reader is null || reader.Position < objectStart ? null : reader;
    }

    // ----------------------------------------------------------------------------------- common parts

    private sealed record Common(int EntMode, int NumReactors, bool XdicMissing, bool NoLinks, int ColorIndex, int? Rgb, int ColorFlags,
        int LtypeFlags, int MaterialFlags, int PlotFlags, int ShadowFlags, bool FullStyle, bool FaceStyle, bool EdgeStyle, bool Invisible);

    private static Common ReadCommon(Obj o)
    {
        var d = o.Data;
        var version = o.File.Version;
        if (d.B())                                                // a preview picture is stored
        {
            var length = version >= DwgVersion.R2010 ? d.BLL() : (uint)d.RL();
            d.Skip(length * 8);
        }
        var entMode = d.BB();
        var reactors = (int)d.BL();
        var xdicMissing = false;
        var noLinks = false;
        if (version >= DwgVersion.R2004)
            xdicMissing = d.B();
        else
            noLinks = d.B();
        if (version >= DwgVersion.R2013)
            d.B();                                                // has binary data for 3D solids

        int colorIndex;
        int? rgb = null;
        var colorFlags = 0;
        if (version >= DwgVersion.R2004)
        {
            var raw = d.BS();
            colorFlags = raw >> 8;
            colorIndex = raw & 0x1FF;
            if ((colorFlags & 0x40) == 0 && (colorFlags & 0x80) != 0)
                rgb = (int)(d.BL() & 0xFFFFFF);
            if ((colorFlags & 0x20) != 0)
                d.BL();
            if ((colorFlags & 0x41) == 0x41)
                o.Text();
            if ((colorFlags & 0x42) == 0x42)
                o.Text();
        }
        else
        {
            colorIndex = d.BS();
        }
        d.BD();                                                   // linetype scale
        var ltype = d.BB();
        var plot = 0;
        var material = 0;
        var shadow = 0;
        if (version >= DwgVersion.R2007)
        {
            material = d.BB();
            plot = d.BB();
            shadow = d.RC();
        }
        else
        {
            plot = d.BB();
        }
        bool full = false, face = false, edge = false;
        if (version >= DwgVersion.R2010)
        {
            full = d.B();
            face = d.B();
            edge = d.B();
        }
        var invisible = (d.BS() & 1) != 0;
        d.RC();                                                   // line weight
        return new Common(entMode, reactors, xdicMissing, noLinks, colorIndex, rgb, colorFlags, ltype, material, plot, shadow, full, face, edge, invisible);
    }

    private static void ReadCommonHandles(Obj o, Common c, Raw raw)
    {
        var h = o.Handles;
        var version = o.File.Version;
        if (c.EntMode == 0)
            raw.Owner = h.HandleRef(o.Handle);
        for (var i = 0; i < c.NumReactors; i++)
            h.HandleRef(o.Handle);
        if (version < DwgVersion.R2004 || !c.XdicMissing)
            h.HandleRef(o.Handle);
        if (version < DwgVersion.R2004 && !c.NoLinks)
        {
            h.HandleRef(o.Handle);
            h.HandleRef(o.Handle);
        }
        if (version >= DwgVersion.R2004 && (c.ColorFlags & 0x40) != 0)
            h.HandleRef(o.Handle);
        raw.LayerHandle = h.HandleRef(o.Handle);
        if (c.LtypeFlags == 3)
            h.HandleRef(o.Handle);
        if (version >= DwgVersion.R2007)
        {
            if (c.MaterialFlags == 3)
                h.HandleRef(o.Handle);
            if (c.ShadowFlags == 3)
                h.HandleRef(o.Handle);
        }
        if (c.PlotFlags == 3)
            h.HandleRef(o.Handle);
        if (version >= DwgVersion.R2010)
        {
            if (c.FullStyle)
                h.HandleRef(o.Handle);
            if (c.FaceStyle)
                h.HandleRef(o.Handle);
            if (c.EdgeStyle)
                h.HandleRef(o.Handle);
        }
    }

    private static void ReadSpecificHandles(Obj o, int type, Raw raw)
    {
        var h = o.Handles;
        var version = o.File.Version;
        switch (type)
        {
            case TypeInsert:
                raw.BlockHandle = h.HandleRef(o.Handle);
                break;
            case TypePolyline2D or TypePolyline3D:
                if (version < DwgVersion.R2004)
                {
                    raw.FirstVertex = h.HandleRef(o.Handle);
                }
                else
                {
                    raw.Owned = new ulong[raw.Owned.Length];
                    for (var i = 0; i < raw.Owned.Length; i++)
                        raw.Owned[i] = h.HandleRef(o.Handle);
                }
                break;
        }
    }

    // ------------------------------------------------------------------------------------ tables

    private static void ReadLayer(State state, Obj o)
    {
        var d = o.Data;
        var version = state.File.Version;
        d.BL();                                                   // reactors
        if (version >= DwgVersion.R2004)
            d.B();
        if (version >= DwgVersion.R2013)
            d.B();
        var name = o.Text();
        d.B();                                                    // xref dependent
        d.BS();
        d.B();
        var flags = d.BS();
        var index = d.BS();
        int? rgb = null;
        if (version >= DwgVersion.R2004)
        {
            var value = d.BL();
            var flag = d.RC();
            // The top byte says how the color is given: 0xC2 by RGB, 0xC3 by AutoCAD color index.
            if ((value >> 24) == 0xC2)
                rgb = (int)(value & 0xFFFFFF);
            else if ((value >> 24) == 0xC3 && (index is 0 or > 255))
                index = (int)(value & 0xFF);
            if (flag < 4)
            {
                if ((flag & 1) != 0)
                    o.Text();
                if ((flag & 2) != 0)
                    o.Text();
            }
        }
        var signed = index > 255 && index < 0x8000 ? index : (short)index;
        var off = (flags & 2) != 0 || signed < 0;
        var frozen = (flags & 1) != 0;
        state.Layers[o.Handle] = (name, Math.Abs(signed), rgb, off || frozen);
    }

    private static void ReadBlockHeader(State state, Obj o)
    {
        var d = o.Data;
        var version = state.File.Version;
        d.BL();
        if (version >= DwgVersion.R2004)
            d.B();
        if (version >= DwgVersion.R2013)
            d.B();
        state.BlockNames[o.Handle] = o.Text();
        d.B();                                                    // xref reference
        if (version < DwgVersion.R2007)
            d.BS();
        d.B();
        d.B();                                                    // anonymous
        d.B();                                                    // has attributes
        var isXref = d.B();
        var overlaid = d.B();
        d.B();                                                    // xref loaded
        if (version >= DwgVersion.R2004 && !isXref && !overlaid)
            d.BL();
        var (x, y, _) = d.BD3();
        state.BlockBases[o.Handle] = new CadPoint(x, y);
    }

    // --------------------------------------------------------------------------------- entities

    private static CadEntity? ReadEntityData(Obj o, int type, string? className, Raw raw)
    {
        var d = o.Data;
        var version = o.File.Version;
        switch (type)
        {
            case TypeLine:
            {
                var zIsZero = d.B();
                var sx = d.RD();
                var ex = d.DD(sx);
                var sy = d.RD();
                var ey = d.DD(sy);
                if (!zIsZero)
                {
                    var sz = d.RD();
                    d.DD(sz);
                }
                d.BT();
                d.BE();
                var line = new CadEntity { Kind = CadKind.Path };
                line.Points.Add(new CadPoint(sx, sy));
                line.Points.Add(new CadPoint(ex, ey));
                return line;
            }
            case TypeCircle:
            {
                var (cx, cy, _) = d.BD3();
                var radius = d.BD();
                d.BT();
                var normal = d.BE();
                var circle = new CadEntity { Kind = CadKind.Path, Closed = true };
                CadGeometry.Arc(circle.Points, (x, y) => CadGeometry.Ocs(normal, x, y), cx, cy, radius, 0, 2 * Math.PI);
                return circle;
            }
            case TypeArc:
            {
                var (cx, cy, _) = d.BD3();
                var radius = d.BD();
                d.BT();
                var normal = d.BE();
                var start = d.BD();
                var end = d.BD();
                var arc = new CadEntity { Kind = CadKind.Path };
                CadGeometry.Arc(arc.Points, (x, y) => CadGeometry.Ocs(normal, x, y), cx, cy, radius, start, end);
                return arc;
            }
            case TypePoint:
            {
                var (x, y, _) = d.BD3();
                d.BT();
                d.BE();
                d.BD();
                var point = new CadEntity { Kind = CadKind.Point };
                point.Points.Add(new CadPoint(x, y));
                return point;
            }
            case TypeText:
                return ReadText(o);
            case TypeMText:
            {
                var (x, y, _) = d.BD3();
                d.BD3();                                          // extrusion
                d.BD3();                                          // direction of the text
                d.BD();                                           // rectangle width
                if (version >= DwgVersion.R2007)
                    d.BD();                                       // rectangle height
                d.BD();                                           // text height
                d.BS();
                d.BS();
                d.BD();
                d.BD();
                var text = o.Text();
                var mtext = new CadEntity { Kind = CadKind.Text, Text = DxfText.Plain(text), Rotation = 0 };
                mtext.Points.Add(new CadPoint(x, y));
                return mtext;
            }
            case TypeEllipse:
            {
                var (cx, cy, _) = d.BD3();
                var (mx, my, _) = d.BD3();
                d.BD3();
                var ratio = d.BD();
                var start = d.BD();
                var end = d.BD();
                var ellipse = new CadEntity { Kind = CadKind.Path };
                if (Math.Abs(end - start - 2 * Math.PI) < 1e-9 || end == start)
                {
                    start = 0;
                    end = 2 * Math.PI;
                    ellipse.Closed = true;
                }
                CadGeometry.Ellipse(ellipse.Points, cx, cy, mx, my, ratio, start, end);
                return ellipse;
            }
            case TypeSpline:
                return ReadSpline(o);
            case TypeInsert:
            {
                var (x, y, _) = d.BD3();
                var flag = d.BB();
                double sx = 1, sy = 1;
                switch (flag)
                {
                    case 0:
                        sx = d.RD();
                        sy = d.DD(sx);
                        d.DD(sx);
                        break;
                    case 1:
                        d.DD(1.0);
                        sy = d.DD(1.0);
                        d.DD(1.0);
                        break;
                    case 2:
                        sx = d.RD();
                        sy = sx;
                        break;
                }
                var rotation = d.BD();
                d.BD3();
                var hasAttributes = d.B();
                if (hasAttributes && version >= DwgVersion.R2004)
                    raw.Owned = new ulong[Count(d)];
                var insert = new CadEntity { Kind = CadKind.Insert, Rotation = rotation, ScaleX = sx, ScaleY = sy };
                insert.Points.Add(new CadPoint(x, y));
                return insert;
            }
            case TypePolyline2D:
            {
                raw.IsPolyline = true;
                raw.PolylineFlag = d.BS();
                d.BS();
                d.BD();
                d.BD();
                d.BT();
                d.BD();
                d.BE();
                if (version >= DwgVersion.R2004)
                    raw.Owned = new ulong[Count(d)];
                return new CadEntity { Kind = CadKind.Path, Closed = (raw.PolylineFlag & 1) != 0 };
            }
            case TypePolyline3D:
            {
                raw.IsPolyline = true;
                raw.Polyline3D = true;
                d.RC();
                var closed = d.RC();
                raw.PolylineFlag = closed;
                if (version >= DwgVersion.R2004)
                    raw.Owned = new ulong[Count(d)];
                return new CadEntity { Kind = CadKind.Path, Closed = (closed & 1) != 0 };
            }
            case TypeLwPolyline:
                return ReadLightPolyline(o);
        }
        return null;
    }

    /// <summary>Reads a count of things still to come, refusing one that could not fit in what is left of the object.</summary>
    private static int Count(BitReader d)
    {
        var count = d.BL();
        if (count < 0 || count > d.EndPosition - d.Position)
            throw new InvalidDataException("Quantidade inválida no desenho.");
        return (int)count;
    }

    private static CadEntity ReadText(Obj o)
    {
        var d = o.Data;
        var flags = d.RC();
        if ((flags & 1) == 0)
            d.RD();
        var (x, y) = d.RD2();
        if ((flags & 2) == 0)
        {
            d.DD(x);
            d.DD(y);
        }
        d.BE();
        d.BT();
        if ((flags & 4) == 0)
            d.RD();
        var rotation = (flags & 8) == 0 ? d.RD() : 0.0;
        d.RD();                                                   // height
        if ((flags & 0x10) == 0)
            d.RD();
        var text = o.Text();
        var entity = new CadEntity { Kind = CadKind.Text, Text = text.Trim(), Rotation = rotation };
        entity.Points.Add(new CadPoint(x, y));
        return entity;
    }

    private static CadEntity ReadLightPolyline(Obj o)
    {
        var d = o.Data;
        var version = o.File.Version;
        var flag = d.BS();
        if ((flag & 4) != 0)
            d.BD();
        if ((flag & 8) != 0)
            d.BD();
        if ((flag & 2) != 0)
            d.BD();
        var normal = (flag & 1) != 0 ? d.BD3() : (0.0, 0.0, 1.0);
        var count = Count(d);
        var bulgeCount = 0;
        var idCount = 0;
        var widthCount = 0;
        if ((flag & 16) != 0)
            bulgeCount = (int)d.BL();
        if (version >= DwgVersion.R2010 && (flag & 1024) != 0)
            idCount = (int)d.BL();
        if ((flag & 32) != 0)
            widthCount = (int)d.BL();
        if (count > 2_000_000 || bulgeCount > count + 1 || widthCount > count + 1 || bulgeCount < 0 || widthCount < 0 || idCount < 0)
            throw new InvalidDataException("Polilinha inválida.");

        var xs = new double[count];
        var ys = new double[count];
        for (var i = 0; i < count; i++)
        {
            if (i == 0)
            {
                xs[0] = d.RD();
                ys[0] = d.RD();
            }
            else
            {
                xs[i] = d.DD(xs[i - 1]);
                ys[i] = d.DD(ys[i - 1]);
            }
        }
        var bulges = new double[count];
        for (var i = 0; i < bulgeCount; i++)
        {
            var value = d.BD();
            if (i < count)
                bulges[i] = value;
        }
        for (var i = 0; i < idCount; i++)
            d.BL();
        for (var i = 0; i < widthCount; i++)
        {
            d.BD();
            d.BD();
        }

        var closed = (flag & 512) != 0;
        var entity = new CadEntity { Kind = CadKind.Path, Closed = closed };
        CadPoint World(double x, double y) => CadGeometry.Ocs(normal, x, y);
        if (count > 0)
            entity.Points.Add(World(xs[0], ys[0]));
        for (var i = 1; i < count; i++)
        {
            CadGeometry.BulgeSegment(entity.Points, xs[i - 1], ys[i - 1], xs[i], ys[i], bulges[i - 1], World);
            CadGeometry.CheckSize(entity.Points);
        }
        if (closed && count > 1)
            CadGeometry.BulgeSegment(entity.Points, xs[count - 1], ys[count - 1], xs[0], ys[0], bulges[count - 1], World);
        return entity;
    }

    private static CadEntity ReadSpline(Obj o)
    {
        var d = o.Data;
        var version = o.File.Version;
        var scenario = d.BL();
        if (version >= DwgVersion.R2013)
        {
            var flags = d.BL();
            var knotParameter = d.BL();
            if ((flags & 1) != 0)
                scenario = 2;
            if (knotParameter == 15)
                scenario = 1;
        }
        var degree = (int)d.BL();
        var spline = new CadEntity { Kind = CadKind.Path };
        if (scenario == 1)
        {
            d.B();
            var closed = d.B();
            d.B();
            d.BD();
            d.BD();
            var knotCount = Count(d);
            var controlCount = Count(d);
            var weighted = d.B();
            if (knotCount < 0 || knotCount > 100000 || controlCount < 0 || controlCount > 100000)
                throw new InvalidDataException("Spline inválida.");
            var knots = new double[knotCount];
            for (var i = 0; i < knotCount; i++)
                knots[i] = d.BD();
            var controls = new List<CadPoint>(controlCount);
            var weights = new List<double>(controlCount);
            for (var i = 0; i < controlCount; i++)
            {
                var (x, y, _) = d.BD3();
                controls.Add(new CadPoint(x, y));
                weights.Add(weighted ? d.BD() : 1.0);
            }
            spline.Points.AddRange(CadGeometry.Spline(degree, knots, controls, weighted ? weights : null));
            spline.Closed = closed;
        }
        else
        {
            d.BD();
            d.BD3();
            d.BD3();
            var fitCount = Count(d);
            if (fitCount < 0 || fitCount > 100000)
                throw new InvalidDataException("Spline inválida.");
            for (var i = 0; i < fitCount; i++)
            {
                var (x, y, _) = d.BD3();
                spline.Points.Add(new CadPoint(x, y));
            }
        }
        return spline;
    }

    private static void ReadVertex2D(Obj o, Raw raw)
    {
        var d = o.Data;
        raw.IsVertex = true;
        raw.VertexFlag = d.RC();
        var (x, y, _) = d.BD3();
        var startWidth = d.BD();
        if (startWidth >= 0)
            d.BD();
        raw.Bulge = d.BD();
        if (o.File.Version >= DwgVersion.R2010)
            d.BL();
        d.BD();
        raw.Entity = new CadEntity { Kind = CadKind.Point };
        raw.Entity.Points.Add(new CadPoint(x, y));
    }

    private static void ReadVertex3D(Obj o, Raw raw)
    {
        var d = o.Data;
        raw.IsVertex = true;
        raw.VertexFlag = d.RC();
        var (x, y, _) = d.BD3();
        raw.Entity = new CadEntity { Kind = CadKind.Point };
        raw.Entity.Points.Add(new CadPoint(x, y));
    }

    // ------------------------------------------------------------------------------------ assembly

    private static CadDrawing Assemble(State state)
    {
        var drawing = new CadDrawing { Version = DwgFile.Describe(state.File.Version), Skipped = state.Skipped };
        foreach (var (handle, name) in state.BlockNames)
        {
            if (state.BlockBases.TryGetValue(handle, out var basePoint))
                drawing.BlockBases[name] = basePoint;
        }
        foreach (var (name, index, rgb, hidden) in state.Layers.Values)
            drawing.Layers[name] = new CadLayer(name, index, rgb, hidden);

        var modelHandle = state.BlockNames.FirstOrDefault(p => p.Value.Equals("*Model_Space", StringComparison.OrdinalIgnoreCase)).Key;
        var paperHandles = state.BlockNames.Where(p => p.Value.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key).ToHashSet();

        // Vertices belong to the polyline that owns them.
        var verticesByOwner = state.Entities.Where(r => r.IsVertex).GroupBy(r => r.Owner).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Handle).ToList());
        var byHandle = new Dictionary<ulong, Raw>();
        foreach (var r in state.Entities)
            byHandle.TryAdd(r.Handle, r);

        foreach (var raw in state.Entities)
        {
            if (raw.IsVertex || raw.Entity is null)
                continue;
            var entity = raw.Entity;
            if (raw.IsPolyline)
            {
                List<Raw> vertices;
                if (raw.Owned.Length > 0)
                    vertices = [.. raw.Owned.Where(byHandle.ContainsKey).Select(h => byHandle[h]).Where(r => r.IsVertex)];
                else
                    vertices = verticesByOwner.TryGetValue(raw.Handle, out var list) ? list : [];
                BuildPolyline(entity, vertices, raw);
            }
            if (entity.Kind == CadKind.Insert)
                entity.Block = state.BlockNames.TryGetValue(raw.BlockHandle, out var blockName) ? blockName : string.Empty;
            entity.Layer = state.Layers.TryGetValue(raw.LayerHandle, out var layer) ? layer.Name : "0";
            if (entity.Kind == CadKind.Path && entity.Points.Count < 2)
                continue;

            if (raw.EntMode == 2 || (raw.EntMode == 0 && raw.Owner == modelHandle && modelHandle != 0))
            {
                drawing.Model.Add(entity);
            }
            else if (raw.EntMode == 0 && !paperHandles.Contains(raw.Owner) && state.BlockNames.TryGetValue(raw.Owner, out var owner)
                     && !owner.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase))
            {
                var block = state.BlockNames[raw.Owner];
                if (!drawing.Blocks.TryGetValue(block, out var list))
                    drawing.Blocks[block] = list = [];
                list.Add(entity);
            }
        }
        return drawing;
    }

    private static void BuildPolyline(CadEntity entity, List<Raw> vertices, Raw raw)
    {
        var points = vertices.Where(v => (v.VertexFlag & 16) == 0 || raw.Polyline3D).ToList();
        if (points.Count == 0)
            return;
        entity.Points.Clear();
        entity.Points.Add(points[0].Entity!.Points[0]);
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1].Entity!.Points[0];
            var b = points[i].Entity!.Points[0];
            CadGeometry.BulgeSegment(entity.Points, a.X, a.Y, b.X, b.Y, points[i - 1].Bulge, (x, y) => new CadPoint(x, y));
            if (entity.Points.Count > CadGeometry.MaxPoints)
                break;
        }
        if (entity.Closed && points.Count > 2)
        {
            var a = points[^1].Entity!.Points[0];
            var b = points[0].Entity!.Points[0];
            CadGeometry.BulgeSegment(entity.Points, a.X, a.Y, b.X, b.Y, points[^1].Bulge, (x, y) => new CadPoint(x, y));
        }
    }
}
