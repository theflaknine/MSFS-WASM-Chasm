using System.Windows;
using System.Windows.Media;

namespace WasmChasm.App.Crt;

/// <summary>
/// Light wear on the monitor's plastic: dull patches, rubbed corners and edges, fine scratches and a few small nicks.
/// It is drawn once from a fixed seed, so it looks the same on every launch and costs nothing afterwards.
/// </summary>
internal static class BezelWear
{
    private const double Width = 1036;
    private const double Height = 796;

    // The visible band of plastic around the recessed screen.
    private const double TopBand = 28;
    private const double SideBand = 32;
    private const double BottomBand = 48;

    private static readonly Color Pale = Color.FromRgb(0xD8, 0xCF, 0xC0);
    private static readonly Color Dark = Color.FromRgb(0x05, 0x04, 0x03);

    public static ImageSource Create()
    {
        var random = new Random(1978);
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            DullPatches(dc, random);
            RubbedCorners(dc);
            RubbedEdges(dc, random);
            Scratches(dc, random);
            ScratchClusters(dc, random);
            Nicks(dc, random);
        }

        // Soft patches spill past the edge. Without this clip the drawing grows, gets squeezed to fit the bezel,
        // and the marks slide inward and hide behind the screen.
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, Width, Height));
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>Plastic that has dulled or picked up grime unevenly.</summary>
    private static void DullPatches(DrawingContext dc, Random random)
    {
        for (int i = 0; i < 26; i++)
        {
            var at = PointOnBand(random);
            bool grime = random.NextDouble() < 0.55;
            Soft(dc, at, 30 + random.NextDouble() * 80, grime ? Dark : Pale, (byte)(grime ? 26 + random.Next(26) : 7 + random.Next(9)));
        }
    }

    /// <summary>Corners take the most handling, so the surface is shinier there.</summary>
    private static void RubbedCorners(DrawingContext dc)
    {
        Soft(dc, new Point(24, 22), 64, Pale, 26);
        Soft(dc, new Point(Width - 24, 22), 58, Pale, 20);
        Soft(dc, new Point(26, Height - 22), 74, Pale, 30);
        Soft(dc, new Point(Width - 26, Height - 22), 80, Pale, 34);
    }

    /// <summary>Broken, uneven pale lines just inside the outer edge, heaviest along the bottom lip.</summary>
    private static void RubbedEdges(DrawingContext dc, Random random)
    {
        void Along(int count, Func<double, double, Point> at, double span, int minAlpha, int maxAlpha)
        {
            for (int i = 0; i < count; i++)
            {
                double start = 46 + random.NextDouble() * (span - 92);
                double length = 8 + random.NextDouble() * 54;
                double inset = 2 + random.NextDouble() * 3.5;
                var pen = Pen(Pale, (byte)random.Next(minAlpha, maxAlpha), 0.7 + random.NextDouble() * 0.9);
                dc.DrawLine(pen, at(start, inset), at(Math.Min(start + length, span - 46), inset));
            }
        }

        Along(22, (t, inset) => new Point(t, inset), Width, 12, 34);
        Along(32, (t, inset) => new Point(t, Height - inset), Width, 16, 46);
        Along(14, (t, inset) => new Point(inset, t), Height, 12, 32);
        Along(14, (t, inset) => new Point(Width - inset, t), Height, 12, 32);
    }

    /// <summary>Fine scratches in no particular direction, mostly faint.</summary>
    private static void Scratches(DrawingContext dc, Random random)
    {
        for (int i = 0; i < 60; i++)
        {
            var from = PointOnBand(random);
            double angle = random.NextDouble() * Math.PI;
            double length = 4 + random.NextDouble() * random.NextDouble() * 40;
            var to = new Point(from.X + Math.Cos(angle) * length, from.Y + Math.Sin(angle) * length);
            byte alpha = (byte)random.Next(10, 40);
            double thickness = 0.45 + random.NextDouble() * 0.45;
            if (OverLabel(from) || OverLabel(to)) continue;
            dc.DrawLine(Pen(Pale, alpha, thickness), from, to);
        }
    }

    /// <summary>A few groups of near-parallel scrapes, as if something was dragged across the case.</summary>
    private static void ScratchClusters(DrawingContext dc, Random random)
    {
        foreach (var (x, y, angle) in new[]
                 {
                     (318.0, 772.0, -0.10),
                     (612.0, 770.0, 0.14),
                     (1012.0, 300.0, 1.42),
                     (19.0, 544.0, 1.68),
                     (806.0, 14.0, 0.06),
                 })
        {
            for (int i = 0; i < 7; i++)
            {
                var from = new Point(x + random.NextDouble() * 26 - 13, y + random.NextDouble() * 10 - 5);
                double a = angle + (random.NextDouble() - 0.5) * 0.12;
                double length = 12 + random.NextDouble() * 40;
                var to = new Point(from.X + Math.Cos(a) * length, from.Y + Math.Sin(a) * length);
                dc.DrawLine(Pen(Pale, (byte)random.Next(14, 44), 0.45 + random.NextDouble() * 0.45), from, to);
            }
        }
    }

    /// <summary>Tiny chips: a dark mark with a pale catch-light on its lower edge.</summary>
    private static void Nicks(DrawingContext dc, Random random)
    {
        for (int i = 0; i < 16; i++)
        {
            var at = PointOnBand(random);
            double size = 0.7 + random.NextDouble() * 1.2;
            double squash = 0.6 + random.NextDouble() * 0.4;
            if (OverLabel(at)) continue;
            dc.DrawEllipse(Solid(Dark, 130), null, at, size, size * squash);
            dc.DrawEllipse(Solid(Pale, 40), null, new Point(at.X + 0.5, at.Y + size * 0.8), size * 0.8, size * 0.35);
        }
    }

    /// <summary>The maker plate and power lamp stay clear, so no mark looks like a line through the text.</summary>
    private static bool OverLabel(Point p) =>
        p.Y > Height - 44 && p.Y < Height - 6 && ((p.X > 48 && p.X < 250) || (p.X > 890 && p.X < 990));

    private static Point PointOnBand(Random random)
    {
        // Weighted by each side's area, so marks spread evenly over the plastic.
        double top = Width * TopBand;
        double bottom = Width * BottomBand;
        double side = Height * SideBand;
        double pick = random.NextDouble() * (top + bottom + side * 2);

        if ((pick -= top) < 0) return new Point(random.NextDouble() * Width, random.NextDouble() * TopBand);
        if ((pick -= bottom) < 0) return new Point(random.NextDouble() * Width, Height - random.NextDouble() * BottomBand);
        if (pick - side < 0) return new Point(random.NextDouble() * SideBand, random.NextDouble() * Height);
        return new Point(Width - random.NextDouble() * SideBand, random.NextDouble() * Height);
    }

    private static void Soft(DrawingContext dc, Point at, double radius, Color color, byte alpha)
    {
        var brush = new RadialGradientBrush(Color.FromArgb(alpha, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B));
        brush.Freeze();
        dc.DrawEllipse(brush, null, at, radius, radius * 0.72);
    }

    private static Pen Pen(Color color, byte alpha, double thickness)
    {
        var pen = new Pen(Solid(color, alpha), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush Solid(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
