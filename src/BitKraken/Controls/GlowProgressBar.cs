using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace BitKraken.Controls;

/// <summary>
/// A rounded progress bar with a gradient fill, soft outer glow and an animated shimmer sweep while active.
/// Value changes are eased rather than snapped so downloads feel fluid.
/// </summary>
public sealed class GlowProgressBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<GlowProgressBar, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<GlowProgressBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<GlowProgressBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<Color> GlowColorProperty =
        AvaloniaProperty.Register<GlowProgressBar, Color>(nameof(GlowColor), Color.Parse("#8B5CF6"));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<GlowProgressBar, bool>(nameof(IsActive));

    public static readonly StyledProperty<double> BarHeightProperty =
        AvaloniaProperty.Register<GlowProgressBar, double>(nameof(BarHeight), 6);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Color GlowColor { get => GetValue(GlowColorProperty); set => SetValue(GlowColorProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double BarHeight { get => GetValue(BarHeightProperty); set => SetValue(BarHeightProperty, value); }

    private readonly DispatcherTimer _timer;
    private double _displayValue;
    private double _shimmer;

    public GlowProgressBar()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
        ClipToBounds = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var target = Math.Clamp(Value, 0, 100);
        var animatingValue = Math.Abs(_displayValue - target) > 0.05;
        if (animatingValue)
            _displayValue += (target - _displayValue) * 0.12;
        else
            _displayValue = target;

        if (IsActive)
        {
            _shimmer += 0.012;
            if (_shimmer > 1.6) _shimmer = -0.6;
        }

        if (!animatingValue && !IsActive)
            _timer.Stop();

        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _displayValue = Math.Clamp(Value, 0, 100);
        EnsureTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == IsActiveProperty)
            EnsureTimer();
        else if (change.Property == FillProperty || change.Property == TrackBrushProperty || change.Property == GlowColorProperty)
            InvalidateVisual();
    }

    private void EnsureTimer()
    {
        if (!_timer.IsEnabled && (IsActive || Math.Abs(_displayValue - Value) > 0.05))
            _timer.Start();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width, BarHeight);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = BarHeight;
        if (w <= 0 || h <= 0) return;

        var top = (Bounds.Height - h) / 2;
        var radius = h / 2;
        var track = new RoundedRect(new Rect(0, top, w, h), radius);
        context.DrawRectangle(TrackBrush ?? new SolidColorBrush(Color.Parse("#2A2145")), null, track);

        var fillWidth = Math.Max(0, w * _displayValue / 100.0);
        if (fillWidth <= 0.5) return;

        var fillRect = new Rect(0, top, Math.Max(fillWidth, h), h);
        var glow = GlowColor;

        // Outer glow: a few stacked translucent rounded rects gives a soft halo without a blur effect.
        for (var i = 3; i >= 1; i--)
        {
            var spread = i * 2.0;
            var alpha = (byte)(IsActive ? 26 / i : 14 / i);
            var glowBrush = new SolidColorBrush(Color.FromArgb(alpha, glow.R, glow.G, glow.B));
            context.DrawRectangle(glowBrush, null, new RoundedRect(fillRect.Inflate(spread), radius + spread));
        }

        var fill = Fill ?? new SolidColorBrush(glow);
        context.DrawRectangle(fill, null, new RoundedRect(fillRect, radius));

        if (IsActive)
        {
            using var clip = context.PushClip(new RoundedRect(fillRect, radius));
            var bandWidth = Math.Max(60, fillRect.Width * 0.35);
            var x = fillRect.Width * _shimmer - bandWidth / 2;
            var shimmer = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(90, 255, 255, 255), 0.5),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                },
            };
            context.DrawRectangle(shimmer, null, new Rect(x, top, bandWidth, h));
        }
    }
}
