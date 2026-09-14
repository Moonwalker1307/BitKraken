using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace BitKraken.Controls;

/// <summary>
/// Slowly drifting, softly glowing purple "aurora" blobs plus a sprinkle of twinkling stars.
/// Cheap to render: only radial gradients, no blur effects. Set <see cref="IsAnimated"/> to false to freeze.
/// </summary>
public sealed class AuroraBackground : Control
{
    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<AuroraBackground, bool>(nameof(IsAnimated), true);

    public bool IsAnimated
    {
        get => GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    private readonly record struct Blob(Color Color, double BaseX, double BaseY, double Radius, double SpeedX, double SpeedY, double Phase);
    private readonly record struct Star(double X, double Y, double Size, double Phase, double Speed);

    private static readonly Blob[] Blobs =
    [
        new(Color.Parse("#7C3AED"), 0.15, 0.20, 0.55, 0.11, 0.07, 0.0),
        new(Color.Parse("#C026D3"), 0.85, 0.30, 0.45, 0.08, 0.13, 1.7),
        new(Color.Parse("#4F46E5"), 0.50, 0.90, 0.60, 0.06, 0.09, 3.1),
        new(Color.Parse("#0EA5E9"), 0.90, 0.85, 0.35, 0.10, 0.05, 4.4),
    ];

    private readonly Star[] _stars;
    private readonly DispatcherTimer _timer;
    private readonly DateTime _start = DateTime.UtcNow;

    public AuroraBackground()
    {
        IsHitTestVisible = false;
        var rng = new Random(1337);
        _stars = Enumerable.Range(0, 70)
            .Select(_ => new Star(rng.NextDouble(), rng.NextDouble(), 0.6 + rng.NextDouble() * 1.4, rng.NextDouble() * Math.Tau, 0.4 + rng.NextDouble() * 1.2))
            .ToArray();

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => InvalidateVisual());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsAnimated) _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsAnimatedProperty)
        {
            if (IsAnimated) _timer.Start(); else _timer.Stop();
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var t = IsAnimated ? (DateTime.UtcNow - _start).TotalSeconds : 0;
        var w = bounds.Width;
        var h = bounds.Height;
        var minDim = Math.Min(w, h);

        foreach (var blob in Blobs)
        {
            var x = (blob.BaseX + Math.Sin(t * blob.SpeedX + blob.Phase) * 0.12) * w;
            var y = (blob.BaseY + Math.Cos(t * blob.SpeedY + blob.Phase) * 0.12) * h;
            var r = blob.Radius * minDim * (1 + 0.08 * Math.Sin(t * 0.37 + blob.Phase));

            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x38, blob.Color.R, blob.Color.G, blob.Color.B), 0),
                    new GradientStop(Color.FromArgb(0x14, blob.Color.R, blob.Color.G, blob.Color.B), 0.45),
                    new GradientStop(Color.FromArgb(0x00, blob.Color.R, blob.Color.G, blob.Color.B), 1),
                },
            };

            context.DrawEllipse(brush, null, new Point(x, y), r, r);
        }

        foreach (var star in _stars)
        {
            var twinkle = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(t * star.Speed + star.Phase));
            var alpha = (byte)(twinkle * 140);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0xE9, 0xD5, 0xFF));
            context.DrawEllipse(brush, null, new Point(star.X * w, star.Y * h), star.Size, star.Size);
        }
    }
}
