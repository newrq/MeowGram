using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowGram.Controls;
using MeowGram.ViewModels.Messenger;

namespace MeowGram.Views.Messenger;

/// <summary>
/// Главное окно: горячие клавиши, перетаскивание чатов (строки списка и шапки окон) с «желейной» карточкой,
/// накладки над окнами при перетаскивании, инфо-панель поверх в узком окне.
/// </summary>
public partial class MessengerView : UserControl
{
    private const double DragThreshold = 6;
    private const double MinChatWidth = 480;

    private MessengerViewModel? _vm;
    private DragCandidate? _candidate;
    private bool _dragging;
    private TopLevel? _topLevel;
    private readonly List<(Rectangle Stroke, Rectangle Fill, Border Label, TextBlock Text)> _overlays = [];

    public MessengerView()
    {
        InitializeComponent();

        Root.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Root.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Root.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Root.AddHandler(PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        Root.AddHandler(Button.ClickEvent, OnAnyClick);

        SearchView.Back.Click += (_, _) => _vm?.CloseSearchCommand.Execute(null);
        InfoView.Close.Click += (_, _) => _vm?.CloseInfoCommand.Execute(null);
        LayoutGrid.SizeChanged += (_, _) => UpdateInfoPlacement();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.CopyRequested -= Copy;
        }

        _vm = DataContext as MessengerViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.CopyRequested += Copy;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MessengerViewModel.IsInfoOpen):
                UpdateInfoPlacement();
                break;
            case nameof(MessengerViewModel.IsSearchOpen) when _vm?.IsSearchOpen == true:
                SearchView.FocusQuery();
                break;
        }
    }

    private async void Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    // ── Горячие клавиши ───────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || !IsVisible)
            return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var digit = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;

        if (e.Key == Key.Escape)
        {
            if (_dragging)
            {
                CancelDrag();
                e.Handled = true;
            }
            else
            {
                e.Handled = _vm.HandleEscape();
            }
        }
        else if (ctrl && e.Key == Key.K)
        {
            _vm.OpenSearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && digit is >= 0 and < 6)
        {
            _vm.SelectFolderAt(digit);
            e.Handled = true;
        }
        else if (alt && digit is >= 0 and < 4)
        {
            _vm.FocusSlot(digit);
            e.Handled = true;
        }
    }

    // ── Клики строк ───────────────────────────────────────────────────

    private void OnAnyClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { DataContext: ChatRowViewModel row } button && button.Classes.Contains("chatRow"))
        {
            if (!DragGuard.ConsumeClick())
                _vm?.OpenChatCommand.Execute(row);
            e.Handled = true;
        }
    }

    // ── Перетаскивание ────────────────────────────────────────────────

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_dragging || !e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed || e.Source is not Visual source)
            return;

        // Строка списка или шапка окна сплита.
        var button = source.FindAncestorOfType<Button>(includeSelf: true);
        while (button is not null && !button.Classes.Contains("chatRow") && !button.Classes.Contains("paneHeader"))
            button = button.FindAncestorOfType<Button>();

        ChatRowViewModel? row = button?.DataContext switch
        {
            ChatRowViewModel r => r,
            ChatPaneViewModel pane => pane.Row,
            _ => null,
        };

        if (button is null || row is null)
        {
            _candidate = null;
            return;
        }

        _candidate = new DragCandidate(row, button, e.GetPosition(Root), e.GetPosition(button), e.Pointer);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_candidate is not { } c)
            return;

        var point = e.GetPosition(Root);
        if (!_dragging)
        {
            if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
            {
                _candidate = null;
                return;
            }

            if (Math.Abs(point.X - c.Start.X) + Math.Abs(point.Y - c.Start.Y) < DragThreshold || _vm?.Split is null)
                return;

            BeginDrag(c, e.Pointer, point);
        }

        DragCard.Move(ToCanvas(point));
        UpdateHover(point);
        e.Handled = true;
    }

    private void BeginDrag(DragCandidate c, IPointer pointer, Point point)
    {
        _dragging = true;
        _vm!.Split!.BeginDrag(c.Row.ChatId);
        pointer.Capture(Root);

        DragContent.Content = c.Row;
        DragCard.Measure(Size.Infinity);
        var size = DragCard.DesiredSize;
        // Точка захвата в карточке: там же, где курсор держал строку (в пределах карточки).
        var grab = new Point(Math.Clamp(c.Grab.X, 16, Math.Max(16, size.Width - 16)), Math.Clamp(c.Grab.Y, 10, Math.Max(10, size.Height - 10)));
        DragCard.Begin(ToCanvas(point), grab);

        // Пустой слот появится в сетке после раскладки — накладки строим следом.
        Dispatcher.UIThread.Post(BuildOverlays, DispatcherPriority.Loaded);
    }

    private async void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var candidate = _candidate;
        _candidate = null;
        if (!_dragging || candidate is null || _vm?.Split is not { } split)
            return;

        _dragging = false;
        e.Pointer.Capture(null);
        DragGuard.MarkDragEnded();
        UpdateHover(e.GetPosition(Root));
        ClearOverlays();

        var chatId = candidate.Row.ChatId;
        var accepted = await split.DropAsync();
        if (!accepted)
        {
            DragCard.Return(CenterOf(candidate.Source));
            return;
        }

        // Ждём раскладку сетки с новым окном и сажаем карточку в его центр.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var target = split.Panes.FirstOrDefault(p => p.ChatId == chatId) is { } pane && Cells.ContainerFromItem(pane) is Control container
            ? CenterOf(container)
            : CenterOf(Center);
        DragCard.Land(target);
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragging && e.Source == Root)
            CancelDrag();
    }

    private void CancelDrag()
    {
        var candidate = _candidate;
        _candidate = null;
        _dragging = false;
        DragGuard.MarkDragEnded();
        _vm?.Split?.EndDrag();
        ClearOverlays();
        if (candidate is not null)
            DragCard.Return(CenterOf(candidate.Source));
        else
            DragCard.Cancel();
    }

    /// <summary>Что под курсором: окно (его индекс) или пустой слот.</summary>
    private void UpdateHover(Point rootPoint)
    {
        if (_vm?.Split is not { } split)
            return;

        DropTarget? target = null;
        for (var i = 0; i < split.Cells.Count; i++)
        {
            if (Cells.ContainerFromIndex(i) is not Control container || !RectIn(container, Root).Contains(rootPoint))
                continue;

            target = split.Cells[i] switch
            {
                ChatPaneViewModel pane => new DropTarget.Pane(split.Panes.IndexOf(pane)),
                SplitGhostViewModel => new DropTarget.Ghost(),
                _ => null,
            };
            break;
        }

        split.SetHover(target);
        UpdateOverlays();
    }

    // ── Накладки над окнами ───────────────────────────────────────────

    private void BuildOverlays()
    {
        ClearOverlays();
        if (!_dragging || _vm?.Split is not { } split)
            return;

        foreach (var pane in split.Panes)
        {
            if (Cells.ContainerFromItem(pane) is not Control container)
                continue;

            var rect = RectIn(container, DropLayer);
            var fill = new Rectangle { RadiusX = 24, RadiusY = 24, Width = rect.Width, Height = rect.Height };
            var stroke = new Rectangle
            {
                RadiusX = 24,
                RadiusY = 24,
                Width = rect.Width,
                Height = rect.Height,
                StrokeThickness = 2,
                StrokeDashArray = [4, 3],
            };
            var text = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var label = new Border
            {
                Padding = new Thickness(16, 10),
                CornerRadius = new CornerRadius(14),
                BoxShadow = BoxShadows.Parse("0 8 24 0 #4D000000"),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new SvgIcon { Source = "avares://MeowGram/Assets/icons/swap.svg", Width = 16, Height = 16 },
                        text,
                    },
                },
            };

            fill.Bind(Shape.FillProperty, this.GetResourceObservable("Mg.BgBrush"));
            label.Bind(Border.BackgroundProperty, this.GetResourceObservable("Mg.SurfaceBrush"));
            foreach (var shape in new Control[] { fill, stroke, label })
                DropLayer.Children.Add(shape);
            Canvas.SetLeft(fill, rect.X);
            Canvas.SetTop(fill, rect.Y);
            Canvas.SetLeft(stroke, rect.X);
            Canvas.SetTop(stroke, rect.Y);
            label.Measure(Size.Infinity);
            label.Tag = rect;
            _overlays.Add((stroke, fill, label, text));
        }

        UpdateOverlays();
    }

    private void UpdateOverlays()
    {
        if (_vm?.Split is not { } split)
            return;

        for (var i = 0; i < _overlays.Count; i++)
        {
            var (stroke, fill, label, text) = _overlays[i];
            var hot = split.Hover is DropTarget.Pane p && p.Index == i;
            stroke.Bind(Shape.StrokeProperty, this.GetResourceObservable(hot ? "Mg.AccentBrush" : "Mg.LineBrush"));
            fill.Opacity = hot ? 0.72 : 0.38;
            label.Opacity = hot ? 1 : 0.75;
            text.Text = split.DropLabel(i);
            label.Measure(Size.Infinity);
            if (label.Tag is Rect rect)
            {
                Canvas.SetLeft(label, rect.X + (rect.Width - label.DesiredSize.Width) / 2);
                Canvas.SetTop(label, rect.Y + (rect.Height - label.DesiredSize.Height) / 2);
            }
        }
    }

    private void ClearOverlays()
    {
        DropLayer.Children.Clear();
        _overlays.Clear();
    }

    // ── Инфо-панель: сбоку или поверх ─────────────────────────────────

    private void UpdateInfoPlacement()
    {
        var chatWidth = LayoutGrid.Bounds.Width - 76 - 12 - 340 - 12;
        var overlay = _vm?.IsInfoOpen == true && chatWidth - InfoHost.Width < MinChatWidth;
        Grid.SetColumn(InfoHost, overlay ? 4 : 5);
        InfoHost.Classes.Set("overlay", overlay);
        InfoHost.ZIndex = overlay ? 10 : 0;
    }

    // ── Координаты ────────────────────────────────────────────────────

    private Point ToCanvas(Point rootPoint) => Root.TranslatePoint(rootPoint, DragCanvas) ?? rootPoint;

    private Point CenterOf(Control control)
    {
        var rect = RectIn(control, DragCanvas);
        return rect.Center;
    }

    /// <summary>Прямоугольник элемента по раскладке (без анимационных трансформаций) в координатах <paramref name="target"/>.</summary>
    private static Rect RectIn(Control control, Visual target)
    {
        var parent = control.GetVisualParent() as Visual;
        var origin = parent?.TranslatePoint(control.Bounds.Position, target) ?? control.TranslatePoint(default, target) ?? default;
        return new Rect(origin, control.Bounds.Size);
    }

    private sealed record DragCandidate(ChatRowViewModel Row, Control Source, Point Start, Point Grab, IPointer Pointer);
}
