using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;

namespace MeowGram.Animation;

/// <summary>
/// Кадровый цикл для контролов, которые рисуют анимацию сами: пока <c>isAnimating()</c> истинно,
/// на каждый кадр вызывается <c>onFrame</c> (если задан) и InvalidateVisual.
/// В покое кадры не запрашиваются (процессор не нагружается).
/// </summary>
public sealed class FrameClock(Visual owner, Func<bool> isAnimating, Action? onFrame = null)
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private bool _scheduled;

    /// <summary>Общее время для всех анимаций.</summary>
    public static TimeSpan Now => Clock.Elapsed;

    /// <summary>Перерисовать и продолжать, пока идёт анимация.</summary>
    public void Kick()
    {
        onFrame?.Invoke();
        owner.InvalidateVisual();
        if (_scheduled)
            return;

        if (TopLevel.GetTopLevel(owner) is not { } top)
            return;

        _scheduled = true;
        top.RequestAnimationFrame(_ => OnFrame());
    }

    private void OnFrame()
    {
        _scheduled = false;
        onFrame?.Invoke();
        owner.InvalidateVisual();
        if (isAnimating())
            Kick();
    }
}
