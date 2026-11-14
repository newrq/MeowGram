using System.Numerics;

namespace MeowGram.Core.Messaging;

/// <summary>Параметры «желейной» карточки. Единицы — пиксели, секунды, радианы.</summary>
public sealed record JellyOptions
{
    /// <summary>Жёсткость пружины, которая тянет карточку к курсору.</summary>
    public float Stiffness { get; init; } = 600f;

    /// <summary>Затухание пружины позиции (меньше 1 — с перелётом).</summary>
    public float DampingRatio { get; init; } = 0.55f;

    /// <summary>Длина «нити» маятника: чем длиннее, тем медленнее качается.</summary>
    public float PendulumLength { get; init; } = 140f;

    public float Gravity { get; init; } = 2400f;

    /// <summary>Затухание маятника.</summary>
    public float AngularDampingRatio { get; init; } = 0.3f;

    public float MaxAngle { get; init; } = 25f * MathF.PI / 180f;

    /// <summary>Наибольшее растяжение вдоль скорости (0.18 — на 18%).</summary>
    public float MaxStretch { get; init; } = 0.18f;

    /// <summary>Скорость, при которой растяжение максимальное.</summary>
    public float StretchSpeed { get; init; } = 2600f;

    public float StretchStiffness { get; init; } = 320f;

    /// <summary>Затухание растяжения: маленькое — карточка «дрожит» после остановки.</summary>
    public float StretchDampingRatio { get; init; } = 0.28f;

    /// <summary>Без физики: карточка жёстко идёт за целью (анимации выключены).</summary>
    public bool Rigid { get; init; }
}

/// <summary>
/// Карточка, которую тащат курсором: позиция догоняет цель недодемпфированной пружиной, угол — маятник,
/// который раскачивает ускорение точки захвата, а растяжение вдоль скорости со своей пружиной даёт «желе».
/// Координаты экранные: y вниз, положительный угол — по часовой стрелке (низ карточки уходит влево).
/// </summary>
public sealed class JellyBody
{
    private const float MaxStep = 1f / 240f;
    private const float MaxFrame = 0.05f;

    public JellyBody(Vector2 position, JellyOptions? options = null)
    {
        Options = options ?? new JellyOptions();
        Position = position;
        Target = position;
    }

    public JellyOptions Options { get; set; }

    /// <summary>Где сейчас точка захвата карточки.</summary>
    public Vector2 Position { get; private set; }

    public Vector2 Velocity { get; private set; }

    /// <summary>Куда тянет пружина (курсор или центр слота).</summary>
    public Vector2 Target { get; set; }

    public float Angle { get; private set; }

    public float AngularVelocity { get; private set; }

    /// <summary>Растяжение вдоль <see cref="StretchAxis"/>; поперёк — 1/Stretch (объём сохраняется).</summary>
    public float Stretch { get; private set; } = 1f;

    public float StretchVelocity { get; private set; }

    /// <summary>Направление растяжения (угол скорости).</summary>
    public float StretchAxis { get; private set; }

    /// <summary>Мгновенно поставить карточку в точку, без движения.</summary>
    public void Reset(Vector2 position)
    {
        Position = position;
        Target = position;
        Velocity = Vector2.Zero;
        Angle = 0;
        AngularVelocity = 0;
        Stretch = 1;
        StretchVelocity = 0;
    }

    /// <summary>Шлепок: карточка сплющивается по вертикали (шире на <paramref name="amount"/>) и отпружинивает.</summary>
    public void Squash(float amount)
    {
        if (Options.Rigid)
            return;

        StretchAxis = 0;
        Stretch = 1 + amount;
        StretchVelocity = 0;
    }

    /// <summary>Продвинуть физику на <paramref name="dt"/> секунд (длинные паузы обрезаются, шаг дробится).</summary>
    public void Step(float dt)
    {
        if (Options.Rigid)
        {
            Reset(Target);
            return;
        }

        dt = Math.Clamp(dt, 0, MaxFrame);
        while (dt > 1e-6f)
        {
            var h = MathF.Min(dt, MaxStep);
            Integrate(h);
            dt -= h;
        }
    }

    /// <summary>Всё успокоилось: карточка в цели, не качается и не дрожит.</summary>
    public bool IsSettled =>
        Vector2.Distance(Position, Target) < 0.5f && Velocity.Length() < 5f
        && MathF.Abs(Angle) < 0.002f && MathF.Abs(AngularVelocity) < 0.02f
        && MathF.Abs(Stretch - 1) < 0.002f && MathF.Abs(StretchVelocity) < 0.02f;

    /// <summary>
    /// Матрица карточки: <paramref name="pivot"/> — точка захвата в координатах карточки. Порядок: растяжение вокруг
    /// точки захвата вдоль скорости, поворот-маятник, перенос в <see cref="Position"/>.
    /// </summary>
    public Matrix3x2 GetTransform(Vector2 pivot) =>
        Matrix3x2.CreateTranslation(-pivot)
        * Matrix3x2.CreateRotation(-StretchAxis)
        * Matrix3x2.CreateScale(Stretch, 1 / Stretch)
        * Matrix3x2.CreateRotation(StretchAxis)
        * Matrix3x2.CreateRotation(Angle)
        * Matrix3x2.CreateTranslation(Position);

    private void Integrate(float h)
    {
        var o = Options;

        // Пружина позиции: a = k·(цель − x) − c·v.
        var damping = 2 * o.DampingRatio * MathF.Sqrt(o.Stiffness);
        var accel = o.Stiffness * (Target - Position) - damping * Velocity;
        Velocity += accel * h;
        Position += Velocity * h;

        // Маятник на точке захвата в её системе отсчёта: θ'' = (aₓ·cosθ − (g − a_y)·sinθ) / L − 2ζω·θ'.
        var omega = MathF.Sqrt(o.Gravity / o.PendulumLength);
        var angularAccel = (accel.X * MathF.Cos(Angle) - (o.Gravity - accel.Y) * MathF.Sin(Angle)) / o.PendulumLength
                           - 2 * o.AngularDampingRatio * omega * AngularVelocity;
        AngularVelocity += angularAccel * h;
        Angle += AngularVelocity * h;
        if (MathF.Abs(Angle) > o.MaxAngle)
        {
            Angle = MathF.CopySign(o.MaxAngle, Angle);
            if (MathF.Sign(AngularVelocity) == MathF.Sign(Angle))
                AngularVelocity = 0;
        }

        // Растяжение вдоль скорости со своей пружиной: после остановки проскакивает 1 и дрожит.
        var speed = Velocity.Length();
        if (speed > 40f)
        {
            // Ось симметрична (θ и θ+π одинаковы), поэтому поворачиваем по кратчайшему пути в пределах ±π/2.
            var diff = WrapHalfPi(MathF.Atan2(Velocity.Y, Velocity.X) - StretchAxis);
            StretchAxis += diff * MathF.Min(1, h * 12);
        }

        var targetStretch = 1 + o.MaxStretch * MathF.Min(speed / o.StretchSpeed, 1);
        var stretchDamping = 2 * o.StretchDampingRatio * MathF.Sqrt(o.StretchStiffness);
        StretchVelocity += (o.StretchStiffness * (targetStretch - Stretch) - stretchDamping * StretchVelocity) * h;
        Stretch = Math.Clamp(Stretch + StretchVelocity * h, 1 - o.MaxStretch * 1.5f, 1 + o.MaxStretch * 1.5f);
    }

    private static float WrapHalfPi(float angle)
    {
        while (angle > MathF.PI / 2)
            angle -= MathF.PI;
        while (angle <= -MathF.PI / 2)
            angle += MathF.PI;
        return angle;
    }
}
