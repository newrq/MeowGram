using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeowGram.Core.Accounts;
using MeowGram.Core.Proxies;
using MeowGram.Core.Settings;
using MeowGram.Core.Telegram;
using MeowGram.Localization;
using MeowGram.Services;

namespace MeowGram.ViewModels.Login;

public enum LoginPanel
{
    None,
    Language,
    Proxy,
    Theme,
    Accounts,
}

/// <summary>Панель (2) экрана входа и кнопки (3) под ней: язык, прокси, тема, аккаунты.</summary>
public sealed partial class LoginPanelsViewModel : ViewModelBase
{
    private readonly AccountManager _accounts;

    public LoginPanelsViewModel(ThemeService theme, ProxyService proxies, AccountManager accounts, ISettingsStore settings)
    {
        _accounts = accounts;
        Language = new LanguagePanelModel(settings);
        Proxy = new ProxyPanelModel(proxies);
        Theme = new ThemePanelModel(theme);
        Accounts = new AccountsPanelModel(accounts);
        _accounts.AccountsChanged += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(HasAccounts)));
    }

    public LanguagePanelModel Language { get; }

    public ProxyPanelModel Proxy { get; }

    public ThemePanelModel Theme { get; }

    public AccountsPanelModel Accounts { get; }

    /// <summary>Кнопка «Аккаунты» видна, только если уже есть вошедшие аккаунты.</summary>
    public bool HasAccounts => _accounts.Accounts.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(Content), nameof(IsLanguageActive), nameof(IsProxyActive), nameof(IsThemeActive), nameof(IsAccountsActive))]
    public partial LoginPanel Active { get; private set; }

    public bool IsOpen => Active != LoginPanel.None;

    public bool IsLanguageActive => Active == LoginPanel.Language;

    public bool IsProxyActive => Active == LoginPanel.Proxy;

    public bool IsThemeActive => Active == LoginPanel.Theme;

    public bool IsAccountsActive => Active == LoginPanel.Accounts;

    public object? Content => Active switch
    {
        LoginPanel.Language => Language,
        LoginPanel.Proxy => Proxy,
        LoginPanel.Theme => Theme,
        LoginPanel.Accounts => Accounts,
        _ => null,
    };

    /// <summary>Повторный клик по той же кнопке закрывает панель.</summary>
    [RelayCommand]
    private void Toggle(LoginPanel panel)
    {
        Active = Active == panel ? LoginPanel.None : panel;
        if (Active == LoginPanel.Proxy)
            _ = Proxy.LoadAsync();
        if (Active == LoginPanel.Accounts)
            Accounts.Refresh();
    }

    [RelayCommand]
    public void Close() => Active = LoginPanel.None;

    internal void AttachSession(TelegramSession session) => Proxy.HasSession = true;
}

public sealed partial class LanguagePanelModel(ISettingsStore settings) : ObservableObject
{
    public IReadOnlyList<LanguageOption> Options { get; } =
    [
        new("ru", "Русский"),
        new("en", "English"),
    ];

    public string Current => Loc.Instance.Language;

    [RelayCommand]
    private async Task SelectAsync(string language)
    {
        await Loc.SetAndSaveAsync(settings, language);
        OnPropertyChanged(nameof(Current));
        foreach (var option in Options)
            option.Refresh();
    }
}

public sealed partial class LanguageOption(string code, string title) : ObservableObject
{
    public string Code { get; } = code;

    public string Title { get; } = title;

    public bool IsSelected => Loc.Instance.Language == Code;

    public void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

public sealed partial class ThemePanelModel : ObservableObject
{
    private readonly ThemeService _theme;
    private bool _themeInteracting;

    public ThemePanelModel(ThemeService theme)
    {
        _theme = theme;
        Accents = AccentPreset.All.Select(a => new AccentOption(a, this)).ToArray();
        _theme.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsLight));
            OnPropertyChanged(nameof(AnimationsEnabled));
            foreach (var accent in Accents)
                accent.Refresh();
        };
    }

    public IReadOnlyList<AccentOption> Accents { get; }

    public AccentPreset CurrentAccent => _theme.Accent;

    /// <summary>Ползунок темы справа — светлая. Меняется, когда бегунок доехал до края.</summary>
    public bool IsLight
    {
        get => _theme.Mode == ThemeMode.Light;
        set
        {
            if (value != IsLight)
                _ = _theme.SetModeAsync(value ? ThemeMode.Light : ThemeMode.Dark);
        }
    }

    /// <summary>
    /// Положение бегунка темы (0 — тёмная, 1 — светлая), только из контрола в модель: пока бегунок в движении,
    /// цвета приложения смешиваются. При выключенных анимациях не смешиваются — тема сменится в конце, сразу.
    /// </summary>
    public double ThemeProgress
    {
        get => IsLight ? 1 : 0;
        set
        {
            if (_themeInteracting && !Motion.IsOff)
                _theme.PreviewBlend(value);
        }
    }

    /// <summary>Бегунок темы нажат/тянется/доезжает. По окончании смешанные цвета убираются (тема уже применена).</summary>
    public bool IsThemeInteracting
    {
        get => _themeInteracting;
        set
        {
            _themeInteracting = value;
            if (!value)
                _theme.EndPreview();
        }
    }

    public bool AnimationsEnabled
    {
        get => _theme.Animations == AnimationLevel.Full;
        set
        {
            if (value != AnimationsEnabled)
                _ = _theme.SetAnimationsAsync(value ? AnimationLevel.Full : AnimationLevel.Off);
        }
    }

    internal void SelectAccent(AccentPreset accent) => _ = _theme.SetAccentAsync(accent);
}

public sealed partial class AccentOption(AccentPreset preset, ThemePanelModel owner) : ObservableObject
{
    public AccentPreset Preset { get; } = preset;

    /// <summary>Заливка кружка (цвет или градиент); у монохрома кружок рисуется двухцветным в разметке.</summary>
    public Avalonia.Media.IBrush? Brush { get; } = preset.IsMono ? null : preset.CreateBrush();

    public bool IsMono => Preset.IsMono;

    public string Title => Loc.Instance[$"Accent_{Preset.Id}"];

    public bool IsSelected => owner.CurrentAccent == Preset;

    [RelayCommand]
    private void Select() => owner.SelectAccent(Preset);

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(Title));
    }
}

public sealed partial class ProxyPanelModel(ProxyService proxies) : ObservableObject
{
    public ObservableCollection<ProxyItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    [ObservableProperty]
    public partial bool NoProxy { get; private set; } = true;

    [ObservableProperty]
    public partial bool HasSession { get; set; }

    // Форма добавления
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMtProto), nameof(IsSocksOrHttp))]
    public partial ProxyKind Kind { get; set; } = ProxyKind.Socks5;

    public bool IsMtProto => Kind == ProxyKind.MtProto;

    public bool IsSocksOrHttp => Kind != ProxyKind.MtProto;

    public bool IsSocks
    {
        get => Kind == ProxyKind.Socks5;
        set { if (value) Kind = ProxyKind.Socks5; }
    }

    public bool IsHttp
    {
        get => Kind == ProxyKind.Http;
        set { if (value) Kind = ProxyKind.Http; }
    }

    public bool IsMtProtoSelected
    {
        get => Kind == ProxyKind.MtProto;
        set { if (value) Kind = ProxyKind.MtProto; }
    }

    [ObservableProperty]
    public partial string Server { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Port { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Secret { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    /// <summary>Вставка ссылки из буфера обмена делается во view (там есть доступ к Clipboard).</summary>
    public Func<Task<string?>>? ReadClipboard { get; set; }

    partial void OnKindChanged(ProxyKind value)
    {
        OnPropertyChanged(nameof(IsSocks));
        OnPropertyChanged(nameof(IsHttp));
        OnPropertyChanged(nameof(IsMtProtoSelected));
    }

    public async Task LoadAsync()
    {
        var all = await proxies.GetAllAsync();
        Items.Clear();
        foreach (var proxy in all)
            Items.Add(new ProxyItem(proxy, this));
        NoProxy = all.All(p => !p.IsEnabled);
    }

    [RelayCommand]
    private async Task DisableAsync()
    {
        await proxies.EnableAsync(null);
        await LoadAsync();
    }

    internal async Task EnableAsync(int id)
    {
        await proxies.EnableAsync(id);
        await LoadAsync();
    }

    internal async Task RemoveAsync(int id)
    {
        await proxies.RemoveAsync(id);
        await LoadAsync();
    }

    [RelayCommand]
    private void StartAdd()
    {
        Kind = ProxyKind.Socks5;
        Server = Port = Username = Password = Secret = Status = string.Empty;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelAdd()
    {
        IsEditing = false;
        Status = string.Empty;
    }

    [RelayCommand]
    private async Task PasteLinkAsync()
    {
        var text = ReadClipboard is null ? null : await ReadClipboard();
        if (!ProxyLinkParser.TryParse(text, out var settings) || settings is null)
        {
            SetStatus(Loc.Instance["Proxy_LinkInvalid"], error: true);
            return;
        }

        Kind = settings.Kind;
        Server = settings.Server;
        Port = settings.Port.ToString(CultureInfo.InvariantCulture);
        Username = settings.Username ?? string.Empty;
        Password = settings.Password ?? string.Empty;
        Secret = settings.Secret ?? string.Empty;
        SetStatus(string.Empty, error: false);
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (!TryBuild(out var settings))
            return;

        if (!HasSession)
        {
            SetStatus(Loc.Instance["Proxy_NeedSession"], error: true);
            return;
        }

        SetStatus(Loc.Instance["Proxy_Testing"], error: false);
        try
        {
            var ping = await proxies.PingAsync(settings);
            SetStatus(Loc.Instance.Format("Proxy_TestOk", (int)ping.TotalMilliseconds), error: false);
        }
        catch (Exception ex) when (ex is ProxyException or InvalidOperationException or TimeoutException)
        {
            SetStatus(Loc.Instance.Format("Proxy_TestFailed", ex.Message), error: true);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!TryBuild(out var settings))
            return;

        await proxies.AddAsync(settings, enable: true);
        IsEditing = false;
        Status = string.Empty;
        await LoadAsync();
    }

    private bool TryBuild(out ProxySettings settings)
    {
        settings = null!;
        if (string.IsNullOrWhiteSpace(Server) || !int.TryParse(Port, out var port) || port is < 1 or > 65535)
        {
            SetStatus(Loc.Instance["Proxy_FieldsRequired"], error: true);
            return false;
        }

        if (Kind == ProxyKind.MtProto && string.IsNullOrWhiteSpace(Secret))
        {
            SetStatus(Loc.Instance["Proxy_SecretRequired"], error: true);
            return false;
        }

        settings = new ProxySettings(Kind, Server.Trim(), port,
            string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
            string.IsNullOrEmpty(Password) ? null : Password,
            string.IsNullOrWhiteSpace(Secret) ? null : Secret.Trim());
        return true;
    }

    private void SetStatus(string text, bool error)
    {
        Status = text;
        StatusIsError = error;
    }
}

public sealed partial class ProxyItem(SavedProxy proxy, ProxyPanelModel owner) : ObservableObject
{
    public string Title { get; } = $"{proxy.Settings.Server}:{proxy.Settings.Port}";

    public string KindName { get; } = proxy.Settings.Kind switch
    {
        ProxyKind.Socks5 => "SOCKS5",
        ProxyKind.Http => "HTTP",
        _ => "MTProto",
    };

    public bool IsEnabled { get; } = proxy.IsEnabled;

    [RelayCommand]
    private Task EnableAsync() => owner.EnableAsync(proxy.Id);

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(proxy.Id);
}

public sealed partial class AccountsPanelModel(AccountManager accounts) : ObservableObject
{
    public ObservableCollection<AccountInfo> Items { get; } = [];

    public void Refresh()
    {
        Items.Clear();
        foreach (var account in accounts.Accounts)
            Items.Add(account);
    }

    /// <summary>Вернуться к уже вошедшему аккаунту: начатый вход отменяется.</summary>
    [RelayCommand]
    private async Task SwitchAsync(AccountInfo account)
    {
        await accounts.CancelAddAccountAsync();
        await accounts.SwitchToAsync(account.Id);
    }
}
