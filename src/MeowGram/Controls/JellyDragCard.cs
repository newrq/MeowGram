using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeowGram.Animation;
using MeowGram.Core.Messaging;

namespace MeowGram.Controls;

/// <summary>
/// Карточка перетаскиваемого чата в оверлее над окном. Висит на точке захвата и догоняет курсор по физике
/// <see cref="JellyBody"/>: пружина, маятник от рывков, растяжение по скорости. Отпустили на цель — летит в центр ячейки,
/// шлёпается (сплющивается с отскоком) и тает; мимо — возвращается к месту, откуда её взяли.
/// Должна лежать в Canvas в точке (0, 0): всё положение — в RenderTransform.
/// </summary>
public sealed class JellyDragCard : Decorator
{
    private const float LiftScale = 1.04f;
    private static readonly JellyOptions DragOptions = new();
    private static readonly JellyOptions FlyOptions = new() { Stiffness = 380f, DampingRatio = 0.62f };
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);

    private JellyBody? _body;
    private Vector2 _pivot;
    private bool _frameRequested;
    private TimeSpan _lastFrame;
    private Phase _phase = Phase.Idle;
    private TimeSpan _fadeStart;
    private Action? _onDone;
    private float _lift = 1;

    public JellyDragCard()
    {
        IsHitTestVisible = false;
        IsVisible = false;
        RenderTransformOrigin = RelativePoint.TopLeft;
    }

    public bool IsActive => _phase != Phase.Idle;

    /// <summary>Поднять карточку: <paramref name="pointer"/> — курсор в координатах оверлея, <paramref name="grab"/> — точка захвата в карточке.</summary>
    public void Begin(Point pointer, Point grab)
    {
        _pivot = new Vector2((float)grab.X, (float)grab.Y);
        var options = Services.Motion.IsOff ? DragOptions with { Rigid = true } : DragOptions;
        _body = new JellyBody(ToVector(pointer), options);
        _phase = Phase.Dragging;
        _lift = 1;
        Opacity = 1;
        IsVisible = true;
        _lastFrame = FrameClock.Now;
        Apply();
        RequestFrame();
    }

    public void Move(Point pointer)
    {
        if (_body is null || _phase != Phase.Dragging)
            return;

        _body.Target = ToVector(pointer);
        RequestFrame();
    }

    /// <summary>Бросок принят: долететь до <paramref name="target"/> (центр ячейки в координатах оверлея) и шлёпнуться.</summary>
    public void Land(Point target, Action? onDone = null) => Fly(target, Phase.Landing, onDone);

    /// <summary>Бросок не принят: вернуться к <paramref name="origin"/> (где карточку взяли) и исчезнуть.</summary>
    public void Return(Point origin, Action? onDone = null) => Fly(origin, Phase.Returning, onDone);

    /// <summary>Убрать сразу (смена аккаунта, отмена Esc).</summary>
    public void Cancel()
    {
        _phase = Phase.Idle;
        IsVisible = false;
        RenderTransform = null;
        var done = _onDone;
        _onDone = null;
        done?.Invoke();
    }

    private void Fly(Point to, Phase phase, Action? onDone)
    {
        if (_body is null || Services.Motion.IsOff)
        {
            _onDone = onDone;
            Cancel();
            return;
        }

        _onDone = onDone;
        _phase = phase;
        _body.Options = FlyOptions;
        // Цель — центр карточки, а тело описывает точку захвата: сдвигаем цель на отступ от центра.
        var size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        _body.Target = ToVector(to) + (_pivot - size / 2);
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameRequested || TopLevel.GetTopLevel(this) is not { } top)
            return;

        _frameRequested = true;
        top.RequestAnimationFrame(_ => OnFrame());
    }

    private void OnFrame()
    {
        _frameRequested = false;
        if (_body is null || _phase == Phase.Idle)
            return;

        var now = FrameClock.Now;
        var dt = (float)(now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        _body.Step(dt);

        // Подъём 1 → 1.04 при захвате, обратно — при посадке.
        var liftTarget = _phase == Phase.Dragging ? LiftScale : 1f;
        _lift += (liftTarget - _lift) * MathF.Min(1, dt * 14);

        switch (_phase)
        {
            case Phase.Landing when Vector2.Distance(_body.Position, _body.Target) < 22:
                _body.Squash(0.12f);
                StartFade(now);
                break;
            case Phase.Returning when Vector2.Distance(_body.Position, _body.Target) < 10:
                StartFade(now);
                break;
            case Phase.Fading:
                var t = Ease.Clamp01((now - _fadeStart).TotalMilliseconds / FadeDuration.TotalMilliseconds);
                Opacity = 1 - Ease.InCubic(t);
                if (t >= 1)
                {
                    Cancel();
                    return;
                }

                break;
        }

        Apply();
        if (_phase == Phase.Dragging && _body.IsSettled)
            return;
        RequestFrame();
    }

    private void StartFade(TimeSpan now)
    {
        _phase = Phase.Fading;
        _fadeStart = now;
    }

    private void Apply()
    {
        if (_body is null)
            return;

        var m = _body.GetTransform(_pivot);
        // Подъём — масштаб вокруг точки захвата, до физики.
        var lift = Matrix3x2.CreateTranslation(-_pivot) * Matrix3x2.CreateScale(_lift) * Matrix3x2.CreateTranslation(_pivot);
        var full = lift * m;
        RenderTransform = new MatrixTransform(new Matrix(full.M11, full.M12, full.M21, full.M22, full.M31, full.M32));
    }

    private static Vector2 ToVector(Point p) => new((float)p.X, (float)p.Y);

    private enum Phase
    {
        Idle,
        Dragging,
        Landing,
        Returning,
        Fading,
    }
}
