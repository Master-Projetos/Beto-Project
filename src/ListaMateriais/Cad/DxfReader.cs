using System.Globalization;
using System.Text;

namespace ListaMateriais.Cad;

/// <summary>Reads the drawing inside an ASCII DXF file (the text format every CAD program can export).</summary>
internal static class DxfReader
{
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
        if (bytes.Length > 22 && Encoding.ASCII.GetString(bytes, 0, 22) == "AutoCAD Binary DXF\r\n\u001a")
            throw new AppException("Este DXF é binário. No programa de desenho, salve-o como DXF em texto (ASCII), ou use o DWG.");
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (ArgumentException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding(1252).GetString(bytes);
        }
        return Parse(text);
    }

    private sealed record Group(int Code, string Value);

    private static List<Group> Tokenize(string text)
    {
        var groups = new List<Group>(text.Length / 12);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } codeLine)
        {
            var valueLine = reader.ReadLine();
            if (valueLine is null)
                break;
            if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                throw new AppException("Este arquivo não parece ser um DXF em texto.");
            groups.Add(new Group(code, valueLine.Trim()));
        }
        return groups;
    }

    private static double Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static CadDrawing Parse(string text)
    {
        var groups = Tokenize(text);
        var drawing = new CadDrawing { Version = "DXF" };
        var at = 0;
        // Sections: 0 SECTION / 2 NAME ... 0 ENDSEC
        while (at < groups.Count)
        {
            if (groups[at] is { Code: 0, Value: "SECTION" } && at + 1 < groups.Count)
            {
                var name = groups[at + 1].Value;
                var end = at + 2;
                while (end < groups.Count && !(groups[end].Code == 0 && groups[end].Value == "ENDSEC"))
                    end++;
                var body = groups.GetRange(at + 2, end - at - 2);
                switch (name)
                {
                    case "TABLES":
                        ReadTables(body, drawing);
                        break;
                    case "BLOCKS":
                        ReadBlocks(body, drawing);
                        break;
                    case "ENTITIES":
                        foreach (var entity in ReadEntities(body, drawing))
                            drawing.Model.Add(entity);
                        break;
                }
                at = end + 1;
            }
            else
            {
                at++;
            }
        }
        if (drawing.Model.Count == 0 && drawing.Blocks.Count == 0 && drawing.Layers.Count == 0)
            throw new AppException("Este arquivo não parece ser um DXF em texto.");
        return drawing;
    }

    /// <summary>Splits a run of groups into records: each starts at a group with code 0.</summary>
    private static List<(string Type, List<Group> Data)> Records(List<Group> groups)
    {
        var records = new List<(string, List<Group>)>();
        List<Group>? current = null;
        foreach (var group in groups)
        {
            if (group.Code == 0)
            {
                current = [];
                records.Add((group.Value, current));
            }
            else
            {
                current?.Add(group);
            }
        }
        return records;
    }

    private static void ReadTables(List<Group> body, CadDrawing drawing)
    {
        foreach (var (type, data) in Records(body))
        {
            if (type != "LAYER")
                continue;
            var name = data.FirstOrDefault(g => g.Code == 2)?.Value ?? "0";
            var color = (int)Number(data.FirstOrDefault(g => g.Code == 62)?.Value ?? "7");
            var flags = (int)Number(data.FirstOrDefault(g => g.Code == 70)?.Value ?? "0");
            int? rgb = data.FirstOrDefault(g => g.Code == 420) is { } t ? (int)Number(t.Value) & 0xFFFFFF : null;
            drawing.Layers[name] = new CadLayer(name, Math.Abs(color), rgb, color < 0 || (flags & 1) != 0);
        }
    }

    private static void ReadBlocks(List<Group> body, CadDrawing drawing)
    {
        string? current = null;
        var content = new List<Group>();
        foreach (var group in body)
        {
            if (group is { Code: 0, Value: "BLOCK" })
            {
                current = string.Empty;
                content = [group];
            }
            else if (group is { Code: 0, Value: "ENDBLK" })
            {
                if (current is not null)
                {
                    var records = Records(content);
                    var name = records.Count > 0 ? records[0].Data.FirstOrDefault(g => g.Code == 2)?.Value ?? string.Empty : string.Empty;
                    if (records.Count > 0)
                        drawing.BlockBases[name] = new CadPoint(Get(records[0].Data, 10), Get(records[0].Data, 20));
                    var inner = content.Skip(1).ToList();
                    // Skip the BLOCK record's own groups (name, base point...) up to the first entity.
                    var firstEntity = inner.FindIndex(g => g.Code == 0);
                    var entities = firstEntity < 0 ? [] : inner.GetRange(firstEntity, inner.Count - firstEntity);
                    drawing.Blocks[name] = [.. ReadEntities(entities, drawing)];
                }
                current = null;
            }
            else if (current is not null)
            {
                content.Add(group);
            }
        }
    }

    private static IEnumerable<CadEntity> ReadEntities(List<Group> body, CadDrawing drawing)
    {
        var records = Records(body);
        for (var i = 0; i < records.Count; i++)
        {
            var (type, data) = records[i];
            CadEntity? entity = null;
            switch (type)
            {
                case "POLYLINE":
                {
                    var vertices = new List<List<Group>>();
                    while (i + 1 < records.Count && records[i + 1].Type == "VERTEX")
                        vertices.Add(records[++i].Data);
                    if (i + 1 < records.Count && records[i + 1].Type == "SEQEND")
                        i++;
                    entity = Polyline(data, vertices);
                    break;
                }
                case "INSERT":
                {
                    entity = Convert(type, data);
                    // The values filled in this reference follow it, closed by SEQEND.
                    while (i + 1 < records.Count && records[i + 1].Type == "ATTRIB")
                    {
                        var attribute = records[++i].Data;
                        var tag = attribute.FirstOrDefault(g => g.Code == 2)?.Value ?? string.Empty;
                        var value = attribute.FirstOrDefault(g => g.Code == 1)?.Value ?? string.Empty;
                        if (value.Length > 0)
                            entity?.Attributes.Add((tag, value));
                    }
                    if (i + 1 < records.Count && records[i + 1].Type == "SEQEND")
                        i++;
                    break;
                }
                default:
                    try
                    {
                        entity = Convert(type, data);
                    }
                    catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
                    {
                        drawing.Skipped++;
                    }
                    if (entity is null && IsUnsupportedDrawing(type))
                        drawing.Skipped++;
                    break;
            }
            if (entity is null)
                continue;
            entity.Layer = data.FirstOrDefault(g => g.Code == 8)?.Value ?? "0";
            if (data.FirstOrDefault(g => g.Code == 62) is { } color)
                entity.ColorIndex = (int)Number(color.Value);
            if (data.FirstOrDefault(g => g.Code == 420) is { } rgb)
                entity.Rgb = (int)Number(rgb.Value) & 0xFFFFFF;
            if (data.Any(g => g.Code == 60 && Number(g.Value) != 0))
                continue;
            if (entity.Kind == CadKind.Path && entity.Points.Count < 2)
                continue;
            yield return entity;
        }
    }

    private static bool IsUnsupportedDrawing(string type) =>
        type is "HATCH" or "DIMENSION" or "SOLID" or "3DFACE" or "3DSOLID" or "IMAGE" or "LEADER" or "MLEADER" or "WIPEOUT" or "REGION"
            or "TRACE" or "MLINE" or "XLINE" or "RAY" or "MULTILEADER";

    private static double Get(List<Group> data, int code, double fallback = 0) =>
        data.FirstOrDefault(g => g.Code == code) is { } g ? Number(g.Value) : fallback;

    private static (double X, double Y, double Z) Normal(List<Group> data) =>
        (Get(data, 210), Get(data, 220), Get(data, 230, 1));

    private static CadEntity? Convert(string type, List<Group> data)
    {
        var normal = Normal(data);
        CadPoint World(double x, double y) => CadGeometry.Ocs(normal, x, y);
        switch (type)
        {
            case "LINE":
            {
                var line = new CadEntity { Kind = CadKind.Path };
                line.Points.Add(new CadPoint(Get(data, 10), Get(data, 20)));
                line.Points.Add(new CadPoint(Get(data, 11), Get(data, 21)));
                return line;
            }
            case "CIRCLE":
            {
                var circle = new CadEntity { Kind = CadKind.Path, Closed = true };
                CadGeometry.Arc(circle.Points, World, Get(data, 10), Get(data, 20), Get(data, 40), 0, 2 * Math.PI);
                return circle;
            }
            case "ARC":
            {
                var arc = new CadEntity { Kind = CadKind.Path };
                CadGeometry.Arc(arc.Points, World, Get(data, 10), Get(data, 20), Get(data, 40),
                    Get(data, 50) * Math.PI / 180, Get(data, 51) * Math.PI / 180);
                return arc;
            }
            case "POINT":
            {
                var point = new CadEntity { Kind = CadKind.Point };
                point.Points.Add(new CadPoint(Get(data, 10), Get(data, 20)));
                return point;
            }
            case "TEXT" or "MTEXT":
            {
                var value = string.Concat(data.Where(g => g.Code == 3).Select(g => g.Value)) + (data.FirstOrDefault(g => g.Code == 1)?.Value ?? string.Empty);
                var text = new CadEntity { Kind = CadKind.Text, Text = type == "MTEXT" ? DxfText.Plain(value) : value.Trim() };
                text.Points.Add(new CadPoint(Get(data, 10), Get(data, 20)));
                return text.Text.Length == 0 ? null : text;
            }
            case "INSERT":
            {
                var insert = new CadEntity
                {
                    Kind = CadKind.Insert, Block = data.FirstOrDefault(g => g.Code == 2)?.Value ?? string.Empty,
                    ScaleX = Get(data, 41, 1), ScaleY = Get(data, 42, Get(data, 41, 1)), Rotation = Get(data, 50) * Math.PI / 180,
                };
                insert.Points.Add(new CadPoint(Get(data, 10), Get(data, 20)));
                return insert;
            }
            case "LWPOLYLINE":
            {
                var xs = new List<double>();
                var ys = new List<double>();
                var bulges = new List<double>();
                foreach (var g in data)
                {
                    switch (g.Code)
                    {
                        case 10:
                            xs.Add(Number(g.Value));
                            bulges.Add(0);
                            break;
                        case 20:
                            ys.Add(Number(g.Value));
                            break;
                        case 42 when bulges.Count > 0:
                            bulges[^1] = Number(g.Value);
                            break;
                    }
                }
                var closed = ((int)Get(data, 70) & 1) != 0;
                var polyline = new CadEntity { Kind = CadKind.Path, Closed = closed };
                var count = Math.Min(xs.Count, ys.Count);
                if (count > 0)
                    polyline.Points.Add(World(xs[0], ys[0]));
                for (var i = 1; i < count; i++)
                {
                    CadGeometry.BulgeSegment(polyline.Points, xs[i - 1], ys[i - 1], xs[i], ys[i], bulges[i - 1], World);
                    CadGeometry.CheckSize(polyline.Points);
                }
                if (closed && count > 1)
                    CadGeometry.BulgeSegment(polyline.Points, xs[count - 1], ys[count - 1], xs[0], ys[0], bulges[count - 1], World);
                return polyline;
            }
            case "ELLIPSE":
            {
                var ellipse = new CadEntity { Kind = CadKind.Path };
                var start = Get(data, 41);
                var end = Get(data, 42, 2 * Math.PI);
                ellipse.Closed = Math.Abs(end - start - 2 * Math.PI) < 1e-9;
                CadGeometry.Ellipse(ellipse.Points, Get(data, 10), Get(data, 20), Get(data, 11), Get(data, 21), Get(data, 40, 1), start, end);
                return ellipse;
            }
            case "SPLINE":
            {
                var spline = new CadEntity { Kind = CadKind.Path };
                var controls = new List<CadPoint>();
                var knots = data.Where(g => g.Code == 40).Select(g => Number(g.Value)).ToList();
                var weights = data.Where(g => g.Code == 41).Select(g => Number(g.Value)).ToList();
                var xs = data.Where(g => g.Code == 10).Select(g => Number(g.Value)).ToList();
                var ys = data.Where(g => g.Code == 20).Select(g => Number(g.Value)).ToList();
                for (var i = 0; i < Math.Min(xs.Count, ys.Count); i++)
                    controls.Add(new CadPoint(xs[i], ys[i]));
                if (controls.Count > 0)
                {
                    spline.Points.AddRange(CadGeometry.Spline((int)Get(data, 71, 3), knots, controls, weights.Count == controls.Count ? weights : null));
                }
                else
                {
                    var fx = data.Where(g => g.Code == 11).Select(g => Number(g.Value)).ToList();
                    var fy = data.Where(g => g.Code == 21).Select(g => Number(g.Value)).ToList();
                    for (var i = 0; i < Math.Min(fx.Count, fy.Count); i++)
                        spline.Points.Add(new CadPoint(fx[i], fy[i]));
                }
                return spline;
            }
        }
        return null;
    }

    private static CadEntity Polyline(List<Group> header, List<List<Group>> vertices)
    {
        var flags = (int)Get(header, 70);
        var closed = (flags & 1) != 0;
        var points = new List<(double X, double Y, double Bulge)>();
        foreach (var vertex in vertices)
        {
            if (((int)Get(vertex, 70) & 16) != 0 && (flags & 8) == 0)
                continue;
            if (((int)Get(vertex, 70) & 16) != 0)
                continue;
            points.Add((Get(vertex, 10), Get(vertex, 20), Get(vertex, 42)));
        }
        var entity = new CadEntity { Kind = CadKind.Path, Closed = closed };
        if (points.Count > 0)
            entity.Points.Add(new CadPoint(points[0].X, points[0].Y));
        for (var i = 1; i < points.Count; i++)
            CadGeometry.BulgeSegment(entity.Points, points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y, points[i - 1].Bulge,
                (x, y) => new CadPoint(x, y));
        if (closed && points.Count > 2)
            CadGeometry.BulgeSegment(entity.Points, points[^1].X, points[^1].Y, points[0].X, points[0].Y, points[^1].Bulge,
                (x, y) => new CadPoint(x, y));
        return entity;
    }
}
