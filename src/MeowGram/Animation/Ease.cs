namespace MeowGram.Animation;

/// <summary>Функции сглаживания для анимаций, которые рисуются вручную (t ∈ [0, 1]).</summary>
public static class Ease
{
    public static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t;

    public static double OutCubic(double t)
    {
        t = Clamp01(t);
        var p = 1 - t;
        return 1 - p * p * p;
    }

    public static double InCubic(double t)
    {
        t = Clamp01(t);
        return t * t * t;
    }

    public static double InOutCubic(double t)
    {
        t = Clamp01(t);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>С небольшим перелётом в конце (приземление с отскоком).</summary>
    public static double OutBack(double t, double overshoot = 1.70158)
    {
        t = Clamp01(t);
        var c3 = overshoot + 1;
        var p = t - 1;
        return 1 + c3 * p * p * p + overshoot * p * p;
    }

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Доля прошедшего времени анимации с задержкой; 1 — если длительность нулевая (анимации выключены).</summary>
    public static double Progress(TimeSpan now, TimeSpan start, TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 1 : Clamp01((now - start).TotalMilliseconds / duration.TotalMilliseconds);
}

/// <summary>Плавный переход числа к цели за заданное время (для состояний, которые можно прервать на полпути).</summary>
public sealed class Tween(double value = 0)
{
    private double _from = value;
    private double _to = value;
    private TimeSpan _start;
    private TimeSpan _duration;
    private Func<double, double> _easing = Ease.OutCubic;

    public double Target => _to;

    public bool IsRunning(TimeSpan now) => now - _start < _duration && _from != _to;

    public void To(double target, TimeSpan now, TimeSpan duration, Func<double, double>? easing = null)
    {
        if (target == _to)
            return;

        _from = Value(now);
        _to = target;
        _start = now;
        _duration = duration;
        _easing = easing ?? Ease.OutCubic;
    }

    public void Snap(double value)
    {
        _from = _to = value;
        _duration = TimeSpan.Zero;
    }

    public double Value(TimeSpan now) => Ease.Lerp(_from, _to, _easing(Ease.Progress(now, _start, _duration)));
}
