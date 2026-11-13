using Avalonia.Controls;

namespace MeowGram.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Свой заголовок окна (TitleBar). Windows/Linux — только рамка без системного заголовка;
        // macOS — системные «светофоры» поверх нашего содержимого.
        WindowDecorations = OperatingSystem.IsMacOS() ? WindowDecorations.Full : WindowDecorations.BorderOnly;
    }
}
