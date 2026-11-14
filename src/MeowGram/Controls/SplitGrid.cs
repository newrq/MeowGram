using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeowGram.Animation;
using MeowGram.Core.Messaging;
using MeowGram.Services;

namespace MeowGram.Controls;

/// <summary>
/// Сетка окон чатов: 1 — на всю ширину, 2 — две колонки, 3 — первое окно на две строки, 4 — 2×2 (<see cref="SplitLayout.Arrange"/>).
/// При смене раскладки окна плавно переезжают в новые ячейки (FLIP: раскладка сразу финальная, а RenderTransform
/// ведёт из старого прямоугольника в новый), новое окно проявляется с лёгким увеличением.
/// </summary>
public sealed class SplitGrid : Panel
{
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<SplitGrid, double>(nameof(Gap), 12);

    private static readonly TimeSpan MoveDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan AppearDuration = TimeSpan.FromMilliseconds(320);

    private readonly Dictionary<Control, Rect> _rects = [];
    private readonly Dictionary<Control, Motion> _motions = [];
    private bool _frameRequested;

    static SplitGrid()
    {
        AffectsArrange<SplitGrid>(GapProperty);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cells = Cells(availableSize);
        var visible = Visible();
        for (var i = 0; i < visible.Count; i++)
            visible[i].Measure(cells.Count > i ? cells[i].Size : availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cells = Cells(finalSize);
        var visible = Visible();
        var now = FrameClock.Now;

        foreach (var gone in _rects.Keys.Where(c => !visible.Contains(c)).ToArray())
        {
            _rects.Remove(gone);
            _motions.Remove(gone);
        }

        for (var i = 0; i < visible.Count; i++)
        {
            var child = visible[i];
            var rect = cells[i];
            child.Arrange(rect);

            if (_rects.TryGetValue(child, out var old))
            {
                if (old != rect && !MotionSettings.IsOff && old.Width > 0 && rect.Width > 0)
                    _motions[child] = new Motion(old, rect, now, MoveDuration, Appear: false);
            }
            else if (!MotionSettings.IsOff && _rects.Count > 0)
            {
                _motions[child] = new Motion(rect, rect, now, AppearDuration, Appear: true);
            }

            _rects[child] = rect;
        }

        ApplyMotions();
        return finalSize;
    }

    private List<Control> Visible() => Children.Where(c => c.IsVisible).ToList();

    private List<Rect> Cells(Size size)
    {
        var count = Children.Count(c => c.IsVisible);
        var grid = SplitLayout.Arrange(count);
        var gap = Gap;
        var w = double.IsInfinity(size.Width) ? 800 : size.Width;
        var h = double.IsInfinity(size.Height) ? 600 : size.Height;
        var cellW = Math.Max(0, (w - gap * (grid.Columns - 1)) / grid.Columns);
        var cellH = Math.Max(0, (h - gap * (grid.Rows - 1)) / grid.Rows);

        return grid.Cells.Select(c => new Rect(
            c.Column * (cellW + gap),
            c.Row * (cellH + gap),
            cellW,
            cellH * c.RowSpan + gap * (c.RowSpan - 1))).ToList();
    }

    private void ApplyMotions()
    {
        var now = FrameClock.Now;
        foreach (var (child, motion) in _motions.ToArray())
        {
            var t = Ease.Clamp01((now - motion.Start).TotalMilliseconds / motion.Duration.TotalMilliseconds);
            if (t >= 1)
            {
                child.RenderTransform = null;
                child.Opacity = 1;
                _motions.Remove(child);
                continue;
            }

            child.RenderTransformOrigin = RelativePoint.TopLeft;
            if (motion.Appear)
            {
                var k = Ease.OutBack(t, 1.2);
                var scale = Ease.Lerp(0.94, 1, k);
                var r = motion.To;
                // Масштаб вокруг центра ячейки.
                child.RenderTransform = new MatrixTransform(
                    Matrix.CreateTranslation(-r.Width / 2, -r.Height / 2)
                    * Matrix.CreateScale(scale, scale)
                    * Matrix.CreateTranslation(r.Width / 2, r.Height / 2));
                child.Opacity = Ease.OutCubic(Math.Min(1, t * 1.6));
            }
            else
            {
                var k = Ease.OutCubic(t);
                var from = motion.From;
                var to = motion.To;
                var sx = Ease.Lerp(from.Width / to.Width, 1, k);
                var sy = Ease.Lerp(from.Height / to.Height, 1, k);
                var dx = Ease.Lerp(from.X - to.X, 0, k);
                var dy = Ease.Lerp(from.Y - to.Y, 0, k);
                child.RenderTransform = new MatrixTransform(Matrix.CreateScale(sx, sy) * Matrix.CreateTranslation(dx, dy));
            }
        }

        if (_motions.Count > 0)
            RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameRequested || TopLevel.GetTopLevel(this) is not { } top)
            return;

        _frameRequested = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            ApplyMotions();
        });
    }

    private sealed record Motion(Rect From, Rect To, TimeSpan Start, TimeSpan Duration, bool Appear);
}

/// <summary>Короткий доступ к настройке «анимации выключены».</summary>
internal static class MotionSettings
{
    public static bool IsOff => Services.Motion.IsOff;
}
