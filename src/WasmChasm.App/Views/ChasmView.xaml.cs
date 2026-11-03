using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WasmChasm.Core;

namespace WasmChasm.App.Views;

/// <summary>A narrow vertical pit in line art receding into darkness, with a depth gauge of MB queued.</summary>
public partial class ChasmView : UserControl
{
    /// <summary>Gauge full scale: 10 GB, logarithmic so small selections still register.</summary>
    private const double FullScaleMb = 10240;

    private long _queuedBytes;
    private Rectangle? _gaugeFill;

    public ChasmView()
    {
        InitializeComponent();
    }

    public long QueuedBytes
    {
        get => _queuedBytes;
        set
        {
            _queuedBytes = Math.Max(0, value);
            DepthText.Text = ByteFormat.Megabytes(_queuedBytes) + " QUEUED";
            UpdateGauge();
        }
    }

    /// <summary>Centre of the pit's mouth, in this control's coordinates.</summary>
    public Point MouthPoint
    {
        get
        {
            var p = Pit.TranslatePoint(new Point(Pit.ActualWidth / 2, MouthY(Pit.ActualHeight)), this);
            return p;
        }
    }

    /// <summary>Half-width of the pit's mouth.</summary>
    public double MouthHalfWidth => Pit.ActualWidth * 0.30;

    /// <summary>Where falling characters should end up: deep in the pit.</summary>
    public Point FloorPoint => Pit.TranslatePoint(new Point(Pit.ActualWidth / 2, Pit.ActualHeight * 0.92), this);

    private static double MouthY(double h) => h * 0.10;

    private Brush Phosphor => (Brush)FindResource("Phosphor");
    private Brush Dim => (Brush)FindResource("Dim");

    /// <summary>Redraws with the current palette (e.g. after a high-contrast change).</summary>
    public void Redraw()
    {
        DrawPit();
        DrawGauge();
    }

    private void OnPitSizeChanged(object sender, SizeChangedEventArgs e) => DrawPit();
    private void OnGaugeSizeChanged(object sender, SizeChangedEventArgs e) => DrawGauge();

    private void DrawPit()
    {
        Pit.Children.Clear();
        double w = Pit.ActualWidth, h = Pit.ActualHeight;
        if (w < 10 || h < 10) return;

        double cx = w / 2;
        double top = MouthY(h);
        double half = w * 0.30;
        var vanish = new Point(cx, h * 1.35);

        // Ground on either side of the mouth, with a little broken edge.
        AddLine(0, top, cx - half, top, Phosphor, 2);
        AddLine(cx + half, top, w, top, Phosphor, 2);
        AddLine(cx - half, top, cx - half + 6, top + 5, Phosphor, 1.5);
        AddLine(cx + half, top, cx + half - 6, top + 5, Phosphor, 1.5);
        for (int i = 0; i < 6; i++)
        {
            double x = 8 + i * (cx - half - 16) / 6;
            AddLine(x, top + 6, x + 6, top + 6, Dim, 1);
            AddLine(w - x, top + 6, w - x - 6, top + 6, Dim, 1);
        }

        // Walls converging on a vanishing point below the screen.
        Point WallAt(double y, int side)
        {
            double t = (y - top) / (vanish.Y - top);
            return new Point(cx + side * half * (1 - t), y);
        }

        var bottom = h;
        var lb = WallAt(bottom, -1);
        var rb = WallAt(bottom, +1);
        AddLine(cx - half, top, lb.X, bottom, Phosphor, 1.5);
        AddLine(cx + half, top, rb.X, bottom, Phosphor, 1.5);

        // Strata rungs, closer together and fainter with depth.
        double y = top;
        double gap = (h - top) * 0.16;
        int n = 0;
        while (y < h && n < 40)
        {
            y += gap;
            gap *= 0.8;
            n++;
            var l = WallAt(y, -1);
            var r = WallAt(y, +1);
            var rung = AddLine(l.X, y, r.X, y, Phosphor, 1);
            rung.Opacity = Math.Max(0.04, 1 - n * 0.12);
            // A couple of rock ticks on the walls.
            if (n % 2 == 1)
            {
                var tick = AddLine(l.X, y, l.X + 8, y - gap * 0.5, Dim, 1);
                tick.Opacity = rung.Opacity;
                var tick2 = AddLine(r.X, y, r.X - 8, y - gap * 0.4, Dim, 1);
                tick2.Opacity = rung.Opacity;
            }
        }

        // Darkness swallowing the bottom of the shaft.
        var bg = (FindResource("ScreenBg") as SolidColorBrush)?.Color ?? Colors.Black;
        var dark = new Rectangle
        {
            Width = w,
            Height = h - top,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, bg.R, bg.G, bg.B), 0.25),
                    new GradientStop(bg, 0.9),
                },
            },
            IsHitTestVisible = false,
        };
        Canvas.SetTop(dark, top + 2);
        Pit.Children.Add(dark);
    }

    private Line AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        var line = new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = brush,
            StrokeThickness = thickness,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
        };
        Pit.Children.Add(line);
        return line;
    }

    private void DrawGauge()
    {
        Gauge.Children.Clear();
        double w = Gauge.ActualWidth, h = Gauge.ActualHeight;
        if (w < 4 || h < 10) return;

        double barX = 4, barW = 12;
        var frame = new Rectangle { Width = barW, Height = h, Stroke = Phosphor, StrokeThickness = 1 };
        Canvas.SetLeft(frame, barX);
        Gauge.Children.Add(frame);

        // Ticks with labels: 0, 10 MB, 100 MB, 1 GB, 10 GB (log scale), depth increasing downward.
        (double mb, string label)[] marks = { (0, "0"), (10, "10M"), (100, "100M"), (1024, "1G"), (10240, "10G") };
        foreach (var (mb, label) in marks)
        {
            double y = Fraction(mb) * h;
            var tick = new Line { X1 = barX + barW, X2 = barX + barW + 5, Y1 = y, Y2 = y, Stroke = Dim, StrokeThickness = 1 };
            Gauge.Children.Add(tick);
            var text = new TextBlock { Text = label, Style = (Style)FindResource("TermDim"), FontSize = 14 };
            Canvas.SetLeft(text, barX + barW + 6);
            Canvas.SetTop(text, Math.Min(h - 16, Math.Max(0, y - 8)));
            Gauge.Children.Add(text);
        }

        _gaugeFill = new Rectangle { Width = barW - 4, Fill = Phosphor };
        Canvas.SetLeft(_gaugeFill, barX + 2);
        Canvas.SetTop(_gaugeFill, 2);
        Gauge.Children.Add(_gaugeFill);
        UpdateGauge();
    }

    private static double Fraction(double mb) =>
        Math.Clamp(Math.Log10(1 + mb) / Math.Log10(1 + FullScaleMb), 0, 1);

    private void UpdateGauge()
    {
        if (_gaugeFill is null) return;
        double h = Gauge.ActualHeight;
        double mb = _queuedBytes / (1024d * 1024d);
        _gaugeFill.Height = Math.Max(0, Fraction(mb) * (h - 4));
    }
}
