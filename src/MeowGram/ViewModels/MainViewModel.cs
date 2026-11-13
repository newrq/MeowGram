using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MeowGram.Core.Accounts;
using MeowGram.ViewModels.Login;
using Microsoft.Extensions.DependencyInjection;

namespace MeowGram.ViewModels;

/// <summary>Главное окно: экран входа, пока нет вошедшего аккаунта, иначе — домашний экран.</summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly AccountManager? _accounts;
    private readonly IServiceProvider? _services;

    /// <summary>Для дизайнера XAML.</summary>
    public MainViewModel()
    {
    }

    public MainViewModel(AccountManager accounts, IServiceProvider services)
    {
        _accounts = accounts;
        _services = services;
        _accounts.ActiveSessionChanged += (_, _) => Dispatcher.UIThread.Post(OnActiveSessionChanged);
    }

    [ObservableProperty]
    public partial ViewModelBase? Current { get; private set; }

    public void Initialize()
    {
        // Сохранённые аккаунты уже есть — сразу домой (TDLib может ещё подключаться).
        if (_accounts is { ActiveSession: not null, Accounts.Count: > 0 })
            ShowHome();
        else
            ShowLogin();
    }

    public void ShowLogin()
    {
        if (_services is null)
            return;

        var login = _services.GetRequiredService<LoginViewModel>();
        SetCurrent(login);
        _ = login.InitializeAsync();
    }

    private void ShowHome()
    {
        if (_accounts is null)
            return;

        SetCurrent(new HomeViewModel(_accounts, this));
    }

    private void OnActiveSessionChanged()
    {
        if (_accounts is null)
            return;

        var signedIn = _accounts.PendingSession is null && _accounts.ActiveSession is not null;
        if (signedIn && Current is not HomeViewModel)
            ShowHome();
        else if (signedIn && Current is HomeViewModel home)
            home.Refresh();
        else if (_accounts.ActiveSession is null && Current is not LoginViewModel)
            ShowLogin();
    }

    private void SetCurrent(ViewModelBase next)
    {
        if (Current is IAsyncDisposable disposable)
            _ = disposable.DisposeAsync().AsTask();
        Current = next;
    }
}
