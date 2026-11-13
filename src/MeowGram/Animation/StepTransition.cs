using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowGram.Controls;
using MeowGram.Services;
using MeowGram.ViewModels.Login;

namespace MeowGram.Animation;

/// <summary>
/// Переход между шагами карточки входа.
/// QR → номер: половинки QR улетают за края, форма номера поднимается из щели, поля (класс <c>rise</c>) — по очереди через 40 мс.
/// Остальные шаги: старый уходит влево на 24 px и гаснет, новый приходит справа; назад — зеркально.
/// </summary>
public sealed class StepTransition : IPageTransition
{
    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        var anchor = to ?? from;
        if (anchor is null)
            return;

        if (to is not null)
        {
            to.IsVisible = true;
            to.Opacity = 0;
        }

        var qr = from?.GetVisualDescendants().OfType<QrCodeView>().FirstOrDefault();
        var risers = to?.GetVisualDescendants().OfType<Control>().Where(c => c.Classes.Contains("rise")).ToArray() ?? [];

        try
        {
            if (qr is not null && to is ContentPresenter { Content: PhoneStep })
                await QrToPhoneAsync(anchor, from!, to, qr, risers, cancellationToken);
            else
                await SlideAsync(anchor, from, to, risers, forward ? 1 : -1, cancellationToken);
        }
        finally
        {
            // Переход отменили (новый шаг пришёл раньше, чем этот доиграл): презентеры уже отданы новому
            // переходу — один из них показывает новый шаг. Трогать их нельзя, иначе карточка опустеет.
            if (!cancellationToken.IsCancellationRequested)
            {
                if (from is not null)
                {
                    from.IsVisible = false;
                    from.Opacity = 1;
                    from.RenderTransform = null;
                }

                if (to is not null)
                {
                    to.Opacity = 1;
                    to.RenderTransform = null;
                }
            }

            foreach (var riser in risers)
            {
                riser.Opacity = 1;
                riser.RenderTransform = null;
            }
        }
    }

    private static async Task QrToPhoneAsync(Visual anchor, Visual from, Visual to, QrCodeView qr, Control[] risers, CancellationToken ct)
    {
        qr.IsSeparated = true; // половинки улетают (450 мс, внутри QrCodeView)

        var total = Motion.Ms(450);
        await UiAnimation.RunAsync(anchor, total, t =>
        {
            // Остальное содержимое QR-шага гаснет быстрее, чем улетают половинки.
            from.Opacity = 1 - Ease.Clamp01(t * 1.6);

            var rise = Ease.OutCubic(Ease.Clamp01((t - 0.3) / 0.7));
            to.Opacity = rise;
            to.RenderTransform = Motion.AllowsMovement ? new TranslateTransform(0, 8 * (1 - rise)) : null;
            Stagger(risers, t, total, startAt: 0.3);
        }, ct);
    }

    private static Task SlideAsync(Visual anchor, Visual? from, Visual? to, Control[] risers, int direction, CancellationToken ct)
    {
        var total = Motion.Ms(300);
        return UiAnimation.RunAsync(anchor, total, t =>
        {
            var e = Ease.OutCubic(t);
            if (from is not null)
            {
                from.Opacity = 1 - Ease.Clamp01(t * 1.5);
                from.RenderTransform = Motion.AllowsMovement ? new TranslateTransform(-24 * direction * e, 0) : null;
            }

            if (to is not null)
            {
                to.Opacity = e;
                to.RenderTransform = Motion.AllowsMovement ? new TranslateTransform(24 * direction * (1 - e), 0) : null;
            }

            Stagger(risers, t, total, startAt: 0);
        }, ct);
    }

    /// <summary>Поля нового шага появляются по очереди: шаг 40 мс, каждое — проявление и подъём на 8 px.</summary>
    private static void Stagger(Control[] risers, double t, TimeSpan total, double startAt)
    {
        if (total <= TimeSpan.Zero)
            return;

        var ms = t * total.TotalMilliseconds;
        var start = startAt * total.TotalMilliseconds;
        for (var i = 0; i < risers.Length; i++)
        {
            var local = Ease.OutCubic(Ease.Clamp01((ms - start - Motion.Ms(40 * i).TotalMilliseconds) / Motion.Ms(220).TotalMilliseconds));
            risers[i].Opacity = local;
            risers[i].RenderTransform = Motion.AllowsMovement ? new TranslateTransform(0, 8 * (1 - local)) : null;
        }
    }
}

/// <summary>Анимация «вручную» по кадрам: apply(t) на каждый кадр, t от 0 до 1.</summary>
public static class UiAnimation
{
    public static Task RunAsync(Visual anchor, TimeSpan duration, Action<double> apply, CancellationToken ct)
    {
        if (duration <= TimeSpan.Zero || TopLevel.GetTopLevel(anchor) is not { } top)
        {
            apply(1);
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource();
        var start = FrameClock.Now;

        void Frame(TimeSpan _)
        {
            if (ct.IsCancellationRequested)
            {
                // Без финального кадра: элементы уже принадлежат следующей анимации.
                done.TrySetResult();
                return;
            }

            var t = Ease.Progress(FrameClock.Now, start, duration);
            apply(t);
            if (t >= 1)
                done.TrySetResult();
            else
                top.RequestAnimationFrame(Frame);
        }

        apply(0);
        top.RequestAnimationFrame(Frame);
        return done.Task;
    }
}
