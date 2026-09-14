using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace BitKraken.Controls;

/// <summary>
/// Visualises a torrent's piece bitfield as a strip of glowing cells. Pieces are bucketed into columns that fit the
/// available width; each column's fill height reflects how complete that bucket is. Newly completed pieces flash.
/// </summary>
public sealed class PieceMap : Control
{
    public static readonly StyledProperty<bool[]?> PiecesProperty =
        AvaloniaProperty.Register<PieceMap, bool[]?>(nameof(Pieces));

    public static readonly StyledProperty<Color> CompleteColorProperty =
        AvaloniaProperty.Register<PieceMap, Color>(nameof(CompleteColor), Color.Parse("#A78BFA"));

    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<PieceMap, Color>(nameof(AccentColor), Color.Parse("#D946EF"));

    public bool[]? Pieces { get => GetValue(PiecesProperty); set => SetValue(PiecesProperty, value); }
    public Color CompleteColor { get => GetValue(CompleteColorProperty); set => SetValue(CompleteColorProperty, value); }
    public Color AccentColor { get => GetValue(AccentColorProperty); set => SetValue(AccentColorProperty, value); }

    private const double CellWidth = 6;
    private const double Gap = 2;
    private const double FlashSeconds = 0.9;

    private readonly DispatcherTimer _timer;
    private bool[]? _previous;
    private readonly Dictionary<int, DateTime> _flashes = new();

    public PieceMap()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            var now = DateTime.UtcNow;
            foreach (var key in _flashes.Where(kv => (now - kv.Value).TotalSeconds > FlashSeconds).Select(kv => kv.Key).ToList())
                _flashes.Remove(key);
            if (_flashes.Count == 0) _timer?.Stop();
            InvalidateVisual();
        });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PiecesProperty)
        {
            var next = Pieces;
            if (next is not null && _previous is not null && next.Length == _previous.Length)
            {
                var now = DateTime.UtcNow;
                for (var i = 0; i < next.Length; i++)
                    if (next[i] && !_previous[i]) _flashes[i] = now;
                if (_flashes.Count > 0 && !_timer.IsEnabled) _timer.Start();
            }
            else
            {
                _flashes.Clear();
            }
            _previous = next;
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 48 : availableSize.Height);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var pieces = Pieces;
        var columns = Math.Max(1, (int)Math.Floor((w + Gap) / (CellWidth + Gap)));
        var emptyBrush = new SolidColorBrush(Color.Parse("#241C3B"));

        if (pieces is null || pieces.Length == 0)
        {
            for (var c = 0; c < columns; c++)
                context.DrawRectangle(emptyBrush, null, new RoundedRect(new Rect(c * (CellWidth + Gap), 0, CellWidth, h), 2));
            return;
        }

        var now = DateTime.UtcNow;
        var perColumn = (double)pieces.Length / columns;

        for (var c = 0; c < columns; c++)
        {
            var start = (int)Math.Floor(c * perColumn);
            var end = Math.Min(pieces.Length, Math.Max(start + 1, (int)Math.Floor((c + 1) * perColumn)));
            var have = 0;
            var flash = 0.0;
            for (var i = start; i < end; i++)
            {
                if (pieces[i]) have++;
                if (_flashes.TryGetValue(i, out var at))
                    flash = Math.Max(flash, 1 - (now - at).TotalSeconds / FlashSeconds);
            }

            var ratio = (double)have / (end - start);
            var x = c * (CellWidth + Gap);
            context.DrawRectangle(emptyBrush, null, new RoundedRect(new Rect(x, 0, CellWidth, h), 2));

            if (ratio <= 0) continue;

            var fillH = Math.Max(2, h * ratio);
            var color = Lerp(CompleteColor, AccentColor, ratio);
            if (flash > 0)
                color = Lerp(color, Colors.White, flash * 0.85);

            var brush = new SolidColorBrush(color);
            var rect = new Rect(x, h - fillH, CellWidth, fillH);
            context.DrawRectangle(brush, null, new RoundedRect(rect, 2));

            if (ratio >= 1 || flash > 0)
            {
                var glowAlpha = (byte)(flash > 0 ? 40 + 120 * flash : 40);
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(glowAlpha, color.R, color.G, color.B)), null,
                    new RoundedRect(rect.Inflate(1.5), 3));
            }
        }
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
