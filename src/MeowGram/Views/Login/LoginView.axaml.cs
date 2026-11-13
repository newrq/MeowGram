using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowGram.ViewModels.Login;

namespace MeowGram.Views.Login;

public partial class LoginView : UserControl
{
    private int _lastPanelIndex = -1;

    public LoginView()
    {
        InitializeComponent();

        // Клик мимо панели и кнопок — закрыть панель; Esc — тоже.
        AddHandler(PointerPressedEvent, OnPointerPressedAnywhere, RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is LoginViewModel { Panels.IsOpen: true } vm)
            {
                vm.Panels.Close();
                e.Handled = true;
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LoginViewModel vm)
        {
            vm.Panels.Proxy.ReadClipboard = async () =>
                TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        }
    }

    /// <summary>Перед открытием/сменой панели: откуда ей вырастать и с какой стороны въезжать содержимому.</summary>
    private void OnPanelButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } button || !int.TryParse(tag, out var index))
            return;

        if (button.TranslatePoint(default, Popup) is { } topLeft)
            Popup.Origin = new Rect(topLeft, button.Bounds.Size);

        Popup.SlideDirection = _lastPanelIndex < 0 ? 0 : Math.Sign(index - _lastPanelIndex);
        _lastPanelIndex = index;
    }

    private void OnPointerPressedAnywhere(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not LoginViewModel { Panels.IsOpen: true } vm || e.Source is not Visual source)
            return;

        var insidePopup = source == Popup || Popup.IsVisualAncestorOf(source);
        var onButtons = source == PanelButtons || PanelButtons.IsVisualAncestorOf(source);
        if (!insidePopup && !onButtons)
            vm.Panels.Close();
    }
}
