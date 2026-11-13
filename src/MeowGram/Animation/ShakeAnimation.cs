using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media;
using MeowGram.Services;

namespace MeowGram.Animation;

/// <summary>Покачивание при ошибке: три затухающих колебания ±6px за 350 мс. Без движения при уменьшенных анимациях.</summary>
public static class ShakeAnimation
{
    private static readonly ConditionalWeakTable<Control, Tween> Running = new();

    public static void Run(Control control)
    {
        if (!Motion.AllowsMovement)
            return;

        var tween = new Tween(0);
        tween.To(1, FrameClock.Now, TimeSpan.FromMilliseconds(350), t => t);
        Running.AddOrUpdate(control, tween);
        Schedule(control, tween);
    }

    private static void Schedule(Control control, Tween tween)
    {
        if (TopLevel.GetTopLevel(control) is not { } top)
            return;

        top.RequestAnimationFrame(_ =>
        {
            if (!Running.TryGetValue(control, out var current) || !ReferenceEquals(current, tween))
                return;

            var t = tween.Value(FrameClock.Now);
            control.RenderTransform = new TranslateTransform(Math.Sin(t * Math.PI * 6) * 6 * (1 - t), 0);

            if (t < 1)
            {
                Schedule(control, tween);
            }
            else
            {
                control.RenderTransform = null;
                Running.Remove(control);
            }
        });
    }
}
