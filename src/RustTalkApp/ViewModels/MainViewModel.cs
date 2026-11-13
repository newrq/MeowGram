using CommunityToolkit.Mvvm.ComponentModel;

namespace RustTalkApp.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string Greeting { get; set; } = "Welcome to Avalonia!";
}
