using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ListaMateriais.Kmz;

/// <summary>Draws the placemark pictures: a shape in the color the user picked, or a picture of their own.</summary>
internal static class MarkerIcon
{
    /// <summary>The shapes are designed on a 64 × 64 grid and scaled to the size asked for.</summary>
    private const float Grid = 64;

    /// <summary>Size of the picture stored in the KMZ: twice what the map shows, so it stays sharp.</summary>
    private const int FilePixels = 64;

    private const int MaxCustomPixels = 128;

    public static readonly IReadOnlyList<(MarkerShape Shape, string Name)> Shapes =
    [
        (MarkerShape.Pin, "Pino"),
        (MarkerShape.Circle, "Círculo"),
        (MarkerShape.Square, "Quadrado"),
        (MarkerShape.Triangle, "Triângulo"),
        (MarkerShape.Diamond, "Losango"),
        (MarkerShape.Star, "Estrela"),
    ];

    public static MapIcon Create(MarkerShape shape, Color color, double scale)
    {
        using var bitmap = Draw(shape, color, FilePixels);
        return new MapIcon(Png(bitmap), AnchorBottom: shape == MarkerShape.Pin, scale);
    }

    /// <summary>A picture chosen by the user, shrunk to fit the map; it marks the spot with its center.</summary>
    public static MapIcon FromFile(string path, double scale)
    {
        try
        {
            using var bitmap = Load(path);
            return new MapIcon(Png(bitmap), AnchorBottom: false, scale);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or ExternalException or OutOfMemoryException)
        {
            throw new AppException($"Não foi possível usar \"{Path.GetFileName(path)}\" como ícone.\n\n" +
                                   "Escolha uma imagem PNG, JPG ou BMP.", e);
        }
    }

    /// <summary>The picture of a file as it goes into the KMZ; the caller disposes it.</summary>
    public static Bitmap Load(string path)
    {
        using var source = Image.FromStream(new MemoryStream(Files.ReadAllBytesShared(path)));
        var ratio = Math.Min(1.0, (double)MaxCustomPixels / Math.Max(source.Width, source.Height));
        var bitmap = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * ratio)),
            Math.Max(1, (int)Math.Round(source.Height * ratio)), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, bitmap.Width, bitmap.Height);
        return bitmap;
    }

    /// <summary>The marker on a transparent square of <paramref name="pixels"/>; the caller disposes it.</summary>
    public static Bitmap Draw(MarkerShape shape, Color color, int pixels)
    {
        var bitmap = new Bitmap(pixels, pixels, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ScaleTransform(pixels / Grid, pixels / Grid);

        using var outline = Outline(shape);
        // A light halo keeps the marker readable over a dark satellite picture; a light marker gets a dark one.
        var light = color.GetBrightness() > 0.8f;
        using (var halo = new Pen(light ? Color.FromArgb(0x40, 0x40, 0x40) : Color.White, 5) { LineJoin = LineJoin.Round })
            g.DrawPath(halo, outline);
        using (var fill = new SolidBrush(Color.FromArgb(255, color)))
            g.FillPath(fill, outline);
        using (var edge = new Pen(Color.FromArgb(90, 0, 0, 0), 1) { LineJoin = LineJoin.Round })
            g.DrawPath(edge, outline);

        if (shape == MarkerShape.Pin)
        {
            using var dot = new SolidBrush(light ? Color.FromArgb(0x40, 0x40, 0x40) : Color.White);
            g.FillEllipse(dot, PinCenterX - 7, PinCenterY - 7, 14, 14);
        }
        return bitmap;
    }

    private const float PinCenterX = 32;
    private const float PinCenterY = 24;
    private const float PinRadius = 20;
    private const float PinTipY = 61;

    private static GraphicsPath Outline(MarkerShape shape)
    {
        var path = new GraphicsPath();
        switch (shape)
        {
            case MarkerShape.Pin:
                // A circle whose sides run straight down to the tip: the lines leave the circle where they touch it.
                var touch = Math.Acos(PinRadius / (PinTipY - PinCenterY)) * 180 / Math.PI;
                var start = (float)(90 + touch);
                path.AddArc(PinCenterX - PinRadius, PinCenterY - PinRadius, PinRadius * 2, PinRadius * 2, start,
                    (float)(360 - 2 * touch));
                path.AddLine(path.GetLastPoint(), new PointF(PinCenterX, PinTipY));
                break;
            case MarkerShape.Circle:
                path.AddEllipse(7, 7, 50, 50);
                break;
            case MarkerShape.Square:
                path.AddRectangle(new RectangleF(9, 9, 46, 46));
                break;
            case MarkerShape.Triangle:
                path.AddPolygon(new PointF[] { new(32, 7), new(58, 55), new(6, 55) });
                break;
            case MarkerShape.Diamond:
                path.AddPolygon(new PointF[] { new(32, 5), new(59, 32), new(32, 59), new(5, 32) });
                break;
            case MarkerShape.Star:
                path.AddPolygon(Star(32, 34, 27, 11.5f));
                break;
        }
        path.CloseFigure();
        return path;
    }

    /// <summary>Five points, the first one straight up.</summary>
    private static PointF[] Star(float centerX, float centerY, float outer, float inner)
    {
        var points = new PointF[10];
        for (var i = 0; i < points.Length; i++)
        {
            var angle = -Math.PI / 2 + i * Math.PI / 5;
            var radius = i % 2 == 0 ? outer : inner;
            points[i] = new PointF(centerX + (float)(radius * Math.Cos(angle)), centerY + (float)(radius * Math.Sin(angle)));
        }
        return points;
    }

    private static byte[] Png(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
