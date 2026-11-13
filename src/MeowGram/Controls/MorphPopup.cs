using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeowGram.Animation;
using MeowGram.Services;

namespace MeowGram.Controls;

/// <summary>
/// Панель (2) экрана входа: вырастает из нажатой круглой кнопки и сворачивается обратно в неё.
/// Фон рисуется сам: прямоугольник плавно переходит от кружка кнопки (<see cref="Origin"/>) к панели,
/// скругление — от круга к <see cref="CornerRadius"/>; содержимое проявляется следом.
/// Содержимое прижато к низу; при смене раздела (<see cref="ContentKey"/>) высота плавно подстраивается,
/// а новое содержимое въезжает со стороны нажатой кнопки (<see cref="SlideDirection"/>).
/// </summary>
public sealed class MorphPopup : Decorator
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<MorphPopup, bool>(nameof(IsOpen));

    /// <summary>Прямоугольник кнопки-источника в координатах этой панели.</summary>
    public static readonly StyledProperty<Rect> OriginProperty =
        AvaloniaProperty.Register<MorphPopup, Rect>(nameof(Origin));

    public static readonly StyledProperty<object?> ContentKeyProperty =
        AvaloniaProperty.Register<MorphPopup, object?>(nameof(ContentKey));

    /// <summary>-1 — новое содержимое въезжает слева, +1 — справа.</summary>
    public static readonly StyledProperty<int> SlideDirectionProperty =
        AvaloniaProperty.Register<MorphPopup, int>(nameof(SlideDirection));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<MorphPopup, IBrush?>(nameof(Background));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<MorphPopup, double>(nameof(CornerRadius), 28);

    private readonly Tween _open = new();
    private readonly Tween _height = new();
    private readonly Tween _contentIn = new(1);
    private readonly FrameClock _clock;
    private Rect _current;
    private double _radius;
    private double _openValue;

    public MorphPopup()
    {
        ClipToBounds = false;
        _clock = new FrameClock(this, IsAnimating, UpdateFrame);
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public Rect Origin
    {
        get => GetValue(OriginProperty);
        set => SetValue(OriginProperty, value);
    }

    public object? ContentKey
    {
        get => GetValue(ContentKeyProperty);
        set => SetValue(ContentKeyProperty, value);
    }

    public int SlideDirection
    {
        get => GetValue(SlideDirectionProperty);
        set => SetValue(SlideDirectionProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Открыта или ещё сворачивается — для обработки кликов мимо.</summary>
    public bool IsVisuallyOpen => _openValue > 0.001;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clock.Kick(); // закрытая панель сразу прячет содержимое
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var now = FrameClock.Now;

        if (change.Property == ChildProperty)
        {
            _clock.Kick();
        }
        else if (change.Property == IsOpenProperty)
        {
            if (IsOpen)
            {
                _height.Snap(Child?.DesiredSize.Height ?? 0);
                _contentIn.Snap(1);
                _open.To(1, now, Motion.Ms(280), Ease.OutCubic);
            }
            else
            {
                _open.To(0, now, Motion.Ms(220), Ease.InCubic);
            }

            _clock.Kick();
        }
        else if (change.Property == ContentKeyProperty && IsOpen)
        {
            _contentIn.Snap(0);
            _contentIn.To(1, now, Motion.Ms(220), Ease.OutCubic);
            _clock.Kick();
        }
        else if (change.Property == BackgroundProperty)
        {
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Child?.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(
            double.IsInfinity(availableSize.Width) ? Child?.DesiredSize.Width ?? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? Child?.DesiredSize.Height ?? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            var height = Math.Min(child.DesiredSize.Height, finalSize.Height);
            child.Arrange(new Rect(0, finalSize.Height - height, finalSize.Width, height));

            var now = FrameClock.Now;
            if (_openValue <= 0.001)
                _height.Snap(height);
            else if (Math.Abs(_height.Target - height) > 0.5)
            {
                _height.To(height, now, Motion.Ms(220), Ease.OutCubic);
                _clock.Kick();
            }
        }

        return finalSize;
    }

    private bool IsAnimating()
    {
        var now = FrameClock.Now;
        return _open.IsRunning(now) || _height.IsRunning(now) || _contentIn.IsRunning(now);
    }

    /// <summary>Расчёт кадра: прямоугольник фона и состояние содержимого (вне Render).</summary>
    private void UpdateFrame()
    {
        var now = FrameClock.Now;
        _openValue = _open.Value(now);
        var height = _height.Value(now);
        var target = new Rect(0, Bounds.Height - height, Bounds.Width, height);
        var origin = Origin.Width > 0 ? Origin : new Rect(target.Right - 44, Bounds.Height + 16, 44, 44);

        var p = Ease.Clamp01(_openValue);
        _current = new Rect(
            Ease.Lerp(origin.X, target.X, p),
            Ease.Lerp(origin.Y, target.Y, p),
            Ease.Lerp(origin.Width, target.Width, p),
            Ease.Lerp(origin.Height, target.Height, p));
        _radius = Ease.Lerp(origin.Height / 2, CornerRadius, p);

        if (Child is not { } child)
            return;

        // Содержимое — когда фон почти раскрылся; при смене раздела — въезд со стороны кнопки.
        var contentIn = _contentIn.Value(now);
        child.Opacity = Ease.Clamp01((p - 0.55) / 0.45) * contentIn;
        child.IsHitTestVisible = p > 0.9;
        child.RenderTransform = Motion.AllowsMovement && contentIn < 1
            ? new TranslateTransform(SlideDirection * 12 * (1 - contentIn), 0)
            : null;

        var local = _current.Translate(-(Vector)child.Bounds.Position);
        child.Clip = new RectangleGeometry(local) { RadiusX = _radius, RadiusY = _radius };
    }

    public override void Render(DrawingContext context)
    {
        if (_openValue <= 0.001 || Background is null)
            return;

        var shadow = new BoxShadows(new BoxShadow
        {
            OffsetY = 16 * _openValue,
            Blur = 48 * _openValue,
            Color = Color.FromArgb((byte)(0x40 * Ease.Clamp01(_openValue)), 0x12, 0x0E, 0x1A),
        });
        context.DrawRectangle(Background, null, new RoundedRect(_current, _radius), shadow);
    }
}
