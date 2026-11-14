using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using MeowGram.Animation;
using MeowGram.Services;

namespace MeowGram.Controls;

/// <summary>
/// Переключатель с бегунком на два положения: слева (<see cref="IsChecked"/> = false) и справа (true).
/// Бегунок можно тянуть: при отпускании он доезжает к ближайшей стороне, ровно середина — влево.
/// Клик без перетаскивания, пробел и Enter переключают, ←/→ ставят влево/вправо.
/// Положение бегунка <see cref="Progress"/> (0..1) и <see cref="IsInteracting"/> видны снаружи —
/// по ним, например, смешиваются цвета тем, пока ползунок в движении.
/// </summary>
/// <remarks>
/// Части шаблона: PART_Thumb — бегунок (обязательно; сдвигается RenderTransform, ход — ширина его родителя минус он сам
/// и его отступы); PART_OffContent / PART_OnContent — содержимое краёв (яркое у бегунка, <see cref="InactiveOpacity"/> у другого);
/// PART_OnLayer — слой «включено», его прозрачность равна <see cref="Progress"/>.
/// </remarks>
[TemplatePart("PART_Thumb", typeof(Control), IsRequired = true)]
[TemplatePart("PART_OffContent", typeof(Control))]
[TemplatePart("PART_OnContent", typeof(Control))]
[TemplatePart("PART_OnLayer", typeof(Control))]
[PseudoClasses(":checked", ":dragging")]
public sealed class SlideSwitch : TemplatedControl
{
    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<SlideSwitch, bool>(nameof(IsChecked), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<object?> OffContentProperty =
        AvaloniaProperty.Register<SlideSwitch, object?>(nameof(OffContent));

    public static readonly StyledProperty<object?> OnContentProperty =
        AvaloniaProperty.Register<SlideSwitch, object?>(nameof(OnContent));

    public static readonly StyledProperty<double> InactiveOpacityProperty =
        AvaloniaProperty.Register<SlideSwitch, double>(nameof(InactiveOpacity), 0.5);

    public static readonly DirectProperty<SlideSwitch, double> ProgressProperty =
        AvaloniaProperty.RegisterDirect<SlideSwitch, double>(nameof(Progress), o => o.Progress);

    public static readonly DirectProperty<SlideSwitch, bool> IsInteractingProperty =
        AvaloniaProperty.RegisterDirect<SlideSwitch, bool>(nameof(IsInteracting), o => o.IsInteracting);

    /// <summary>На сколько пикселей сдвинуть указатель, чтобы нажатие стало перетаскиванием.</summary>
    private const double DragThreshold = 3;

    private readonly Tween _settle = new();
    private readonly FrameClock _clock;
    private readonly TranslateTransform _thumbOffset = new();
    private Control? _thumb;
    private Control? _offContent;
    private Control? _onContent;
    private Control? _onLayer;
    private double _progress;
    private bool _isInteracting;

    private bool _pressed;
    private bool _dragging;
    private Point _pressPoint;
    private double _pressProgress;
    private bool _pressTarget;

    private bool _settling;
    private bool _settleTarget;
    private bool _committing;

    static SlideSwitch()
    {
        FocusableProperty.OverrideDefaultValue<SlideSwitch>(true);
    }

    public SlideSwitch()
    {
        _clock = new FrameClock(this, () => _settling, OnSettleFrame);
    }

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public object? OffContent
    {
        get => GetValue(OffContentProperty);
        set => SetValue(OffContentProperty, value);
    }

    public object? OnContent
    {
        get => GetValue(OnContentProperty);
        set => SetValue(OnContentProperty, value);
    }

    /// <summary>Прозрачность содержимого края, на котором бегунка нет.</summary>
    public double InactiveOpacity
    {
        get => GetValue(InactiveOpacityProperty);
        set => SetValue(InactiveOpacityProperty, value);
    }

    /// <summary>Положение бегунка: 0 — слева, 1 — справа.</summary>
    public double Progress => _progress;

    /// <summary>Бегунок нажат, тянется или доезжает; <see cref="IsChecked"/> меняется в конце, до сброса этого флага.</summary>
    public bool IsInteracting => _isInteracting;

    /// <summary>Сторона, к которой переключатель придёт (с учётом доводки, которая ещё идёт).</summary>
    private bool CurrentTarget => _settling ? _settleTarget : IsChecked;

    private double Travel
    {
        get
        {
            if (_thumb?.Parent is not Visual track)
                return 0;
            return Math.Max(0, track.Bounds.Width - _thumb.Bounds.Width - _thumb.Margin.Left - _thumb.Margin.Right);
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _thumb = e.NameScope.Find<Control>("PART_Thumb");
        _offContent = e.NameScope.Find<Control>("PART_OffContent");
        _onContent = e.NameScope.Find<Control>("PART_OnContent");
        _onLayer = e.NameScope.Find<Control>("PART_OnLayer");
        if (_thumb is not null)
            _thumb.RenderTransform = _thumbOffset;
        UpdateVisuals();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsCheckedProperty)
        {
            PseudoClasses.Set(":checked", IsChecked);

            // Снаружи (загрузка настроек, другой экран) — бегунок сразу встаёт на место.
            if (!_committing && !_isInteracting)
                SetProgress(IsChecked ? 1 : 0);
        }
        else if (change.Property == InactiveOpacityProperty)
        {
            UpdateVisuals();
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        UpdateVisuals();
        return size;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_pressed || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        e.Handled = true;
        Focus(NavigationMethod.Pointer);
        _pressTarget = CurrentTarget;
        _settling = false; // бегунок останавливается там, где его схватили
        _pressed = true;
        _dragging = false;
        _pressPoint = e.GetPosition(this);
        _pressProgress = _progress;
        SetInteracting(true);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed)
            return;

        var dx = e.GetPosition(this).X - _pressPoint.X;
        if (!_dragging)
        {
            if (Math.Abs(dx) < DragThreshold)
                return;
            _dragging = true;
            PseudoClasses.Set(":dragging", true);
        }

        e.Handled = true;
        if (Travel > 0)
            SetProgress(Math.Clamp(_pressProgress + dx / Travel, 0, 1));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed)
            return;

        e.Handled = true;
        var dragged = _dragging;
        EndPress();
        e.Pointer.Capture(null);

        // Тянули — к ближайшей стороне (середина — влево); просто клик — на другую сторону.
        SettleTo(dragged ? _progress > 0.5 : !_pressTarget);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_pressed)
            return;

        var dragged = _dragging;
        EndPress();
        SettleTo(dragged ? _progress > 0.5 : _pressTarget);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_pressed || e.Handled)
            return;

        bool? target = e.Key switch
        {
            Key.Space or Key.Enter => !CurrentTarget,
            Key.Left => false,
            Key.Right => true,
            _ => null,
        };

        if (target is { } value)
        {
            e.Handled = true;
            SettleTo(value);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // Без визуального дерева кадров не будет: доводку и перетаскивание завершаем сразу, иначе состояние зависнет.
        if (_pressed)
        {
            var target = _dragging ? _progress > 0.5 : _pressTarget;
            EndPress();
            _settleTarget = target;
            Commit();
        }
        else if (_settling)
        {
            Commit();
        }
    }

    private void EndPress()
    {
        _pressed = false;
        _dragging = false;
        PseudoClasses.Set(":dragging", false);
    }

    private void SettleTo(bool target)
    {
        _settleTarget = target;
        _settling = true;
        SetInteracting(true);

        var to = target ? 1.0 : 0.0;
        _settle.Snap(_progress);
        _settle.To(to, FrameClock.Now, Motion.Ms(120 + 140 * Math.Abs(to - _progress)), Ease.OutCubic);
        _clock.Kick();
    }

    private void OnSettleFrame()
    {
        if (!_settling)
            return;

        var now = FrameClock.Now;
        if (_settle.IsRunning(now))
            SetProgress(_settle.Value(now));
        else
            Commit();
    }

    /// <summary>Конец движения: бегунок на краю, затем <see cref="IsChecked"/> (уходит в привязку), затем сброс <see cref="IsInteracting"/>.</summary>
    private void Commit()
    {
        _settling = false;
        SetProgress(_settleTarget ? 1 : 0);

        _committing = true;
        try
        {
            SetCurrentValue(IsCheckedProperty, _settleTarget);
        }
        finally
        {
            _committing = false;
        }

        SetInteracting(false);
    }

    private void SetProgress(double value)
    {
        SetAndRaise(ProgressProperty, ref _progress, value);
        UpdateVisuals();
    }

    private void SetInteracting(bool value) => SetAndRaise(IsInteractingProperty, ref _isInteracting, value);

    private void UpdateVisuals()
    {
        _thumbOffset.X = _progress * Travel;

        var inactive = InactiveOpacity;
        if (_offContent is not null)
            _offContent.Opacity = Ease.Lerp(1, inactive, _progress);
        if (_onContent is not null)
            _onContent.Opacity = Ease.Lerp(inactive, 1, _progress);
        if (_onLayer is not null)
            _onLayer.Opacity = _progress;
    }
}
