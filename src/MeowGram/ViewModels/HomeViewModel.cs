using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeowGram.Core.Accounts;

namespace MeowGram.ViewModels;

/// <summary>Временный домашний экран после входа (чаты — следующий этап): имя аккаунта, выход, добавить аккаунт.</summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly AccountManager _accounts;
    private readonly MainViewModel _main;

    public HomeViewModel(AccountManager accounts, MainViewModel main)
    {
        _accounts = accounts;
        _main = main;
        Refresh();
    }

    [ObservableProperty]
    public partial string AccountName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Phone { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public void Refresh()
    {
        var account = _accounts.Accounts.FirstOrDefault(a => a.Id == _accounts.ActiveSession?.AccountId);
        AccountName = account?.DisplayName ?? string.Empty;
        Phone = string.IsNullOrEmpty(account?.PhoneNumber) ? string.Empty : "+" + account.PhoneNumber;
    }

    [RelayCommand]
    private async Task LogOutAsync()
    {
        if (_accounts.ActiveSession is not { } session)
            return;

        IsBusy = true;
        try
        {
            await _accounts.LogOutAsync(session.AccountId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddAccount() => _main.ShowLogin();
}
