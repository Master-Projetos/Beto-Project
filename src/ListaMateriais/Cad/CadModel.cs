namespace ListaMateriais.Cad;

/// <summary>A point of a drawing, in the drawing's own units.</summary>
internal readonly record struct CadPoint(double X, double Y);

internal enum CadKind { Path, Point, Text, Insert }

/// <summary>
/// One thing drawn in a CAD file, reduced to what a map needs: a line (curves already cut into short
/// segments), a point, a text label, or a block reference.
/// </summary>
internal sealed class CadEntity
{
    public CadKind Kind { get; init; }

    public string Layer { get; set; } = "0";

    /// <summary>AutoCAD color index: 0 = by block, 256 = by layer, otherwise 1 to 255.</summary>
    public int ColorIndex { get; set; } = 256;

    /// <summary>24-bit RGB when the entity has a true color of its own.</summary>
    public int? Rgb { get; set; }

    /// <summary>Vertices of a <see cref="CadKind.Path"/>, or the single position of a point, text or insert.</summary>
    public List<CadPoint> Points { get; } = [];

    public bool Closed { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>For an insert: the name of the block it draws.</summary>
    public string Block { get; set; } = string.Empty;

    public double Rotation { get; set; }

    public double ScaleX { get; set; } = 1;

    public double ScaleY { get; set; } = 1;
}

/// <summary>A layer of the drawing.</summary>
internal sealed record CadLayer(string Name, int ColorIndex, int? Rgb, bool Hidden);

/// <summary>What was read from a DWG or DXF file.</summary>
internal sealed class CadDrawing
{
    public List<CadEntity> Model { get; } = [];

    /// <summary>Block definitions by name (case-insensitive); their points are relative to the block's origin.</summary>
    public Dictionary<string, List<CadEntity>> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Base point of each block: the point of its drawing that lands on the insertion point of a reference.</summary>
    public Dictionary<string, CadPoint> BlockBases { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, CadLayer> Layers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Entities of the file that this reader does not draw (hatches, dimensions, 3D solids...).</summary>
    public int Skipped { get; set; }

    /// <summary>Objects of the file that could not be read (damaged, or laid out in a way this reader does not know).</summary>
    public int Unreadable { get; set; }

    /// <summary>Name of the version the file was saved by, for the messages (e.g. "AutoCAD 2018").</summary>
    public string Version { get; set; } = string.Empty;
}
