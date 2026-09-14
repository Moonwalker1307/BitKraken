using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BitKraken.Controls;

/// <summary>
/// Live transfer-rate chart: smooth area fills with gradient and glowing lines for download and upload.
/// Bind fresh arrays each tick; any change to either sample set triggers a redraw.
/// </summary>
public sealed class SpeedGraph : Control
{
    public static readonly StyledProperty<double[]?> DownloadSamplesProperty =
        AvaloniaProperty.Register<SpeedGraph, double[]?>(nameof(DownloadSamples));

    public static readonly StyledProperty<double[]?> UploadSamplesProperty =
        AvaloniaProperty.Register<SpeedGraph, double[]?>(nameof(UploadSamples));

    public static readonly StyledProperty<Color> DownloadColorProperty =
        AvaloniaProperty.Register<SpeedGraph, Color>(nameof(DownloadColor), Color.Parse("#A78BFA"));

    public static readonly StyledProperty<Color> UploadColorProperty =
        AvaloniaProperty.Register<SpeedGraph, Color>(nameof(UploadColor), Color.Parse("#22D3EE"));

    public double[]? DownloadSamples { get => GetValue(DownloadSamplesProperty); set => SetValue(DownloadSamplesProperty, value); }
    public double[]? UploadSamples { get => GetValue(UploadSamplesProperty); set => SetValue(UploadSamplesProperty, value); }
    public Color DownloadColor { get => GetValue(DownloadColorProperty); set => SetValue(DownloadColorProperty, value); }
    public Color UploadColor { get => GetValue(UploadColorProperty); set => SetValue(UploadColorProperty, value); }

    static SpeedGraph()
    {
        AffectsRender<SpeedGraph>(DownloadSamplesProperty, UploadSamplesProperty, DownloadColorProperty, UploadColorProperty);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 120 : availableSize.Height);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var down = DownloadSamples ?? [];
        var up = UploadSamples ?? [];
        var count = Math.Max(down.Length, up.Length);

        var max = Math.Max(down.DefaultIfEmpty(0).Max(), up.DefaultIfEmpty(0).Max());
        if (max <= 0) max = 1;
        max *= 1.15; // headroom so the peak doesn't kiss the top edge

        // Grid lines
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1, DashStyle.Dash);
        for (var i = 1; i < 4; i++)
        {
            var y = h * i / 4.0;
            context.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        if (count < 2) return;

        DrawSeries(context, up, count, max, w, h, UploadColor);
        DrawSeries(context, down, count, max, w, h, DownloadColor);

        // Scale label (top-left)
        if (max <= 1.15) return;
        var text = new FormattedText(Format.Speed((long)(max / 1.15)), System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 10, new SolidColorBrush(Color.FromArgb(0x99, 0xB4, 0xA9, 0xD6)));
        context.DrawText(text, new Point(4, 2));
    }

    private static void DrawSeries(DrawingContext context, double[] samples, int count, double max, double w, double h, Color color)
    {
        if (samples.Length < 2) return;

        var stepX = w / (count - 1);
        var offset = count - samples.Length;
        var points = new Point[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var x = (offset + i) * stepX;
            var y = h - Math.Clamp(samples[i] / max, 0, 1) * (h - 4) - 2;
            points[i] = new Point(x, y);
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            lc.BeginFigure(points[0], false);
            ac.BeginFigure(new Point(points[0].X, h), true);
            ac.LineTo(points[0]);

            for (var i = 1; i < points.Length; i++)
            {
                // Catmull-Rom-ish smoothing via midpoints & cubic beziers
                var p0 = points[i - 1];
                var p1 = points[i];
                var cx = (p0.X + p1.X) / 2;
                lc.CubicBezierTo(new Point(cx, p0.Y), new Point(cx, p1.Y), p1);
                ac.CubicBezierTo(new Point(cx, p0.Y), new Point(cx, p1.Y), p1);
            }

            ac.LineTo(new Point(points[^1].X, h));
            ac.EndFigure(true);
            lc.EndFigure(false);
        }

        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x66, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1),
            },
        };
        context.DrawGeometry(fill, null, area);

        // Glow: wide translucent stroke under the crisp line
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B)), 6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);

        // Bright dot at the latest sample
        var last = points[^1];
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B)), null, last, 6, 6);
        context.DrawEllipse(new SolidColorBrush(color), null, last, 3, 3);
    }
}
