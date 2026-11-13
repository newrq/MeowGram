using MeowGram.Services;

namespace MeowGram.Animation;

/// <summary>
/// Движение одного символа при печати (общий расчёт для полей ввода и ячеек кода):
/// появление — падает сверху с наклоном и приземляется с лёгким перелётом; удаление — проваливается вниз и гаснет.
/// </summary>
public static class GlyphMotion
{
    public static TimeSpan AppearDuration => Motion.Ms(260);

    public static TimeSpan DisappearDuration => Motion.Ms(160);

    public static TimeSpan FlipDuration => Motion.Ms(200);

    /// <summary>Шаг каскада при вставке: 15 мс, но вся вставка не дольше 300 мс.</summary>
    public static TimeSpan StaggerStep(int count) =>
        count <= 1 ? TimeSpan.Zero : Motion.Ms(Math.Min(15.0, 300.0 / (count - 1)));

    /// <summary>Состояние появляющегося символа: смещение по Y (в долях размера шрифта), наклон (градусы), прозрачность.</summary>
    public static (double OffsetY, double TiltDegrees, double Opacity) Appear(double t, double tiltDegrees)
    {
        if (!Motion.AllowsMovement)
            return (0, 0, Ease.Clamp01(t));

        var landing = Ease.OutBack(t, 2.0);
        return (-0.7 * (1 - landing), tiltDegrees * (1 - Ease.OutCubic(t)), Ease.Clamp01(t * 2.5));
    }

    /// <summary>Состояние исчезающего символа: смещение вниз (доли размера шрифта) и прозрачность.</summary>
    public static (double OffsetY, double Opacity) Disappear(double t) =>
        Motion.AllowsMovement ? (0.4 * Ease.InCubic(t), 1 - Ease.Clamp01(t)) : (0, 1 - Ease.Clamp01(t));

    /// <summary>Переворот символа (* ↔ буква): масштаб по вертикали и показывать ли уже новый символ.</summary>
    public static (double ScaleY, bool ShowNew) Flip(double t)
    {
        t = Ease.Clamp01(t);
        if (!Motion.AllowsMovement)
            return (1, t >= 0.5);

        return (Math.Abs(Math.Cos(Math.PI * t)), t >= 0.5);
    }

    /// <summary>Разница строк: общий префикс и суффикс → удалённый и вставленный участки.</summary>
    public static (int Start, int RemovedCount, int InsertedCount) Diff(string oldText, string newText)
    {
        var prefix = 0;
        var max = Math.Min(oldText.Length, newText.Length);
        while (prefix < max && oldText[prefix] == newText[prefix])
            prefix++;

        var suffix = 0;
        while (suffix < max - prefix && oldText[^(suffix + 1)] == newText[^(suffix + 1)])
            suffix++;

        return (prefix, oldText.Length - prefix - suffix, newText.Length - prefix - suffix);
    }
}
