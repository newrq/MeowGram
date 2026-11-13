using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using MeowGram.Localization;

namespace MeowGram.Controls;

public partial class TitleBar : UserControl
{
    private Window? _window;

    public TitleBar()
    {
        InitializeComponent();

        // macOS: системные «светофоры» остаются, свои кнопки не нужны.
        WindowButtons.IsVisible = !OperatingSystem.IsMacOS();

        // Клики — запасной путь: если система обработала кнопку сама (роль элемента), сюда не дойдёт.
        MinimizeButton.Click += (_, _) => { if (_window is not null) _window.WindowState = WindowState.Minimized; };
        MaximizeButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => _window?.Close();
        Loc.Instance.LanguageChanged += (_, _) => UpdateMaximizeState();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
            UpdateMaximizeState();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
            _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
            UpdateMaximizeState();
    }

    private void ToggleMaximize()
    {
        if (_window is null)
            return;

        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateMaximizeState()
    {
        var maximized = _window?.WindowState == WindowState.Maximized;
        var key = maximized ? "Icon.Restore" : "Icon.Square";
        if (this.TryFindResource(key, out var geometry) && geometry is Geometry g)
            MaximizeIcon.Data = g;

        var label = Loc.Instance[maximized ? "Window_Restore" : "Window_Maximize"];
        ToolTip.SetTip(MaximizeButton, label);
        Avalonia.Automation.AutomationProperties.SetName(MaximizeButton, label);
    }
}
