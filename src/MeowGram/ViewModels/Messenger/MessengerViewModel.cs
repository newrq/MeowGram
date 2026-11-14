using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeowGram.Core.Accounts;
using MeowGram.Core.Messaging;
using MeowGram.Core.Settings;
using MeowGram.Services;
using MeowGram.ViewModels.Messenger.Feed;

namespace MeowGram.ViewModels.Messenger;

/// <summary>Аккаунт в меню аватара: имя, телефон, активен ли.</summary>
public sealed class AccountItemViewModel(AccountInfo account, bool isActive, Func<AccountInfo, Task> switchTo)
{
    public string Name { get; } = account.DisplayName;

    public string Phone { get; } = string.IsNullOrEmpty(account.PhoneNumber) ? string.Empty : "+" + account.PhoneNumber;

    public string Initials { get; } = ChatText.Initials(account.DisplayName);

    public bool IsActive { get; } = isActive;

    public IAsyncRelayCommand SwitchCommand { get; } = new AsyncRelayCommand(() => isActive ? Task.CompletedTask : switchTo(account));
}

/// <summary>
/// Главное окно после входа: рейл (аккаунт, папки), список чатов или поиск, сплит до 4 окон, инфо-панель активного
/// окна, выбор чата для пересылки. Пересоздаёт всё при смене аккаунта.
/// </summary>
public sealed partial class MessengerViewModel : ViewModelBase, IPaneHost, IAsyncDisposable
{
    private readonly MessagingHub _hub;
    private readonly AccountManager _accounts;
    private readonly ISettingsStore _settings;
    private readonly MainViewModel _main;

    // Растёт при каждой смене аккаунта: подключает контекст только последний обработчик.
    private int _attachVersion;
    private bool _disposed;

    public MessengerViewModel(MessagingHub hub, AccountManager accounts, ISettingsStore settings, MainViewModel main)
    {
        _hub = hub;
        _accounts = accounts;
        _settings = settings;
        _main = main;
        _hub.CurrentChanged += OnCurrentChanged;
        _accounts.AccountsChanged += OnAccountsChanged;
        Attach(_hub.Current);
    }

    public MessagingContext? Context { get; private set; }

    [ObservableProperty]
    public partial ChatRows? Rows { get; private set; }

    [ObservableProperty]
    public partial ChatListViewModel? ChatList { get; private set; }

    [ObservableProperty]
    public partial SearchViewModel? Search { get; private set; }

    [ObservableProperty]
    public partial SplitViewModel? Split { get; private set; }

    [ObservableProperty]
    public partial bool IsSearchOpen { get; private set; }

    [ObservableProperty]
    public partial InfoPanelViewModel? Info { get; private set; }

    [ObservableProperty]
    public partial ForwardPickerViewModel? ForwardPicker { get; private set; }

    public bool IsInfoOpen => Info is not null;

    // ── Аккаунт ───────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string AccountName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountInitials { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial Bitmap? AccountPhoto { get; private set; }

    public IBrush AccountBackground { get; } = new SolidColorBrush(Color.Parse("#4A4A4A"));

    [ObservableProperty]
    public partial bool IsAccountMenuOpen { get; set; }

    public ObservableCollection<AccountItemViewModel> AccountItems { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Скопировать текст (ссылка-приглашение); буфер обмена у окна.</summary>
    public event Action<string>? CopyRequested;

    // ── Команды рейла и списка ────────────────────────────────────────

    [RelayCommand]
    private void SelectFolder(FolderViewModel? folder)
    {
        if (folder is null || ChatList is null)
            return;
        CloseSearch();
        ChatList.SelectFolder(folder.Folder);
    }

    /// <summary>Ctrl+1–6.</summary>
    public void SelectFolderAt(int index)
    {
        if (ChatList is { } list && index >= 0 && index < list.Folders.Count)
            SelectFolder(list.Folders[index]);
    }

    [RelayCommand]
    private void OpenSearch()
    {
        Search?.Begin(contactsMode: false);
        IsSearchOpen = true;
    }

    [RelayCommand]
    private void NewMessage()
    {
        Search?.Begin(contactsMode: true);
        IsSearchOpen = true;
    }

    [RelayCommand]
    private void CloseSearch() => IsSearchOpen = false;

    [RelayCommand]
    private Task OpenChat(ChatRowViewModel? row) => row is null || Split is null ? Task.CompletedTask : Split.OpenAsync(row.ChatId);

    [RelayCommand]
    private void ToggleAccountMenu()
    {
        if (!IsAccountMenuOpen)
            RefreshAccounts();
        IsAccountMenuOpen = !IsAccountMenuOpen;
    }

    [RelayCommand]
    private void AddAccount()
    {
        IsAccountMenuOpen = false;
        _main.ShowLogin();
    }

    [RelayCommand]
    private async Task LogOutAsync()
    {
        IsAccountMenuOpen = false;
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

    /// <summary>Esc: меню → пересылка → поиск в чате → поиск → активное окно.</summary>
    public bool HandleEscape()
    {
        if (IsAccountMenuOpen)
        {
            IsAccountMenuOpen = false;
            return true;
        }

        if (ForwardPicker is not null)
        {
            ForwardPicker = null;
            return true;
        }

        if (Split?.ActivePane is { IsSearching: true } searching)
        {
            searching.ToggleSearchCommand.Execute(null);
            return true;
        }

        if (Split?.ActivePane is { HasComposerContext: true } composing)
        {
            composing.CancelComposerContextCommand.Execute(null);
            return true;
        }

        if (IsSearchOpen)
        {
            CloseSearch();
            return true;
        }

        if (Split?.ActivePane is { } active)
        {
            Close(active);
            return true;
        }

        return false;
    }

    /// <summary>Alt+1–4.</summary>
    public void FocusSlot(int index) => _ = Split?.FocusSlotAsync(index);

    // ── IPaneHost ─────────────────────────────────────────────────────

    public void Focus(ChatPaneViewModel pane) => _ = Split?.FocusAsync(pane.ChatId);

    public void Close(ChatPaneViewModel pane) => _ = Split?.CloseAsync(pane.ChatId);

    public void Forward(ChatPaneViewModel pane, MessageViewModel message)
    {
        if (Context is null || Rows is null)
            return;
        ForwardPicker = new ForwardPickerViewModel(Context, Rows, message, () => ForwardPicker = null);
    }

    public void ToggleInfo(ChatPaneViewModel pane)
    {
        if (Info?.ChatId == pane.ChatId)
        {
            SetInfo(null);
        }
        else
        {
            Focus(pane);
            // Панель следует за активным окном: фокус мог уже открыть её для этого чата.
            if (Info?.ChatId != pane.ChatId)
                SetInfo(CreateInfo(pane));
        }

        RaiseInfo();
    }

    public bool IsInfoOpenFor(ChatPaneViewModel pane) => Info?.ChatId == pane.ChatId;

    [RelayCommand]
    private void CloseInfo()
    {
        SetInfo(null);
        RaiseInfo();
    }

    /// <summary>Сменить инфо-панель, освободив прежнюю (она подписана на загрузки файлов).</summary>
    private void SetInfo(InfoPanelViewModel? next)
    {
        var previous = Info;
        Info = next;
        if (previous is not null && !ReferenceEquals(previous, next))
            previous.Dispose();
    }

    // ── Жизненный цикл ────────────────────────────────────────────────

    private void Attach(MessagingContext? context)
    {
        Context = context;
        if (context is null)
            return;

        var rows = new ChatRows(context);
        Rows = rows;
        ChatList = new ChatListViewModel(context, rows);
        Search = new SearchViewModel(context, rows, (chatId, messageId) =>
        {
            IsSearchOpen = false;
            _ = Split?.OpenAsync(chatId, messageId);
        });
        var split = new SplitViewModel(context, rows, _settings, chatId => new ChatPaneViewModel(context, rows, chatId, this));
        split.PropertyChanged += OnSplitChanged;
        rows.RowCreated += split.SyncRow;
        Split = split;
        context.Store.UserChanged += OnUserChanged;

        RefreshAccount();
        _ = StartAsync(split);
    }

    private async Task StartAsync(SplitViewModel split)
    {
        if (ChatList is { } list)
            await list.EnsureLoadedAsync();
        await split.RestoreAsync();
    }

    /// <summary>
    /// Отключить контекст: сначала синхронно отвязать всё от экрана и событий ядра, потом сохранить черновики
    /// окон. Повторный вызов во время ожидания уже ничего не находит и не освобождает дважды.
    /// </summary>
    private async Task DetachAsync()
    {
        SetInfo(null);
        OnPropertyChanged(nameof(IsInfoOpen));
        ForwardPicker = null;
        IsSearchOpen = false;
        if (Context is { } context)
            context.Store.UserChanged -= OnUserChanged;

        var panes = Split?.Panes.ToArray() ?? [];
        if (Split is { } split)
            split.PropertyChanged -= OnSplitChanged;

        ChatList?.Dispose();
        Rows?.Dispose();
        Split = null;
        Search = null;
        ChatList = null;
        Rows = null;
        Context = null;
        AccountPhoto = null;

        foreach (var pane in panes)
            await pane.DisposeAsync();
    }

    private async void OnCurrentChanged(object? sender, EventArgs e)
    {
        var version = ++_attachVersion;
        if (_disposed || ReferenceEquals(_hub.Current, Context))
            return;

        await DetachAsync();

        // Пока окна сохраняли черновики, аккаунт могли сменить ещё раз или уйти с экрана.
        if (version != _attachVersion || _disposed)
            return;
        Attach(_hub.Current);
    }

    private void OnAccountsChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAccounts);

    private void OnSplitChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Инфо-панель следует за активным окном.
        if (e.PropertyName == nameof(SplitViewModel.ActivePane) && Info is not null)
        {
            SetInfo(Split?.ActivePane is { } active ? (Info.ChatId == active.ChatId ? Info : CreateInfo(active)) : null);
            RaiseInfo();
        }
    }

    private InfoPanelViewModel? CreateInfo(ChatPaneViewModel pane) => Context is null
        ? null
        : new InfoPanelViewModel(
            Context,
            pane.Row,
            messageId => _ = pane.JumpToAsync(messageId),
            () =>
            {
                if (!pane.IsSearching)
                    pane.ToggleSearchCommand.Execute(null);
            },
            link =>
            {
                CopyRequested?.Invoke(link);
                return Task.CompletedTask;
            });

    private void RaiseInfo()
    {
        OnPropertyChanged(nameof(IsInfoOpen));
        if (Split is not null)
        {
            foreach (var pane in Split.Panes)
                pane.RaiseInfoChanged();
        }
    }

    private void OnUserChanged(long userId)
    {
        if (Context is { } context && userId == context.Store.MyUserId)
            RefreshAccount();
    }

    private void RefreshAccount()
    {
        var account = _accounts.Accounts.FirstOrDefault(a => a.Id == _accounts.ActiveSession?.AccountId);
        AccountName = account?.DisplayName ?? string.Empty;
        AccountInitials = ChatText.Initials(AccountName);
        if (Context is { } context && context.Store.GetUser(context.Store.MyUserId)?.ProfilePhoto?.Small is { } photo)
        {
            // Фото приходит асинхронно — не ставим его, если за это время переключились на другой аккаунт.
            ImageCache.LoadInto(context.Files, photo, FileService.PriorityAvatar, 96, bitmap =>
            {
                if (ReferenceEquals(Context, context))
                    AccountPhoto = bitmap;
            });
        }
        else
        {
            AccountPhoto = null;
        }
    }

    private void RefreshAccounts()
    {
        AccountItems.Clear();
        var activeId = _accounts.ActiveSession?.AccountId;
        foreach (var account in _accounts.Accounts)
            AccountItems.Add(new AccountItemViewModel(account, account.Id == activeId, SwitchAsync));
        RefreshAccount();
    }

    private async Task SwitchAsync(AccountInfo account)
    {
        IsAccountMenuOpen = false;
        try
        {
            await _accounts.SwitchToAsync(account.Id);
        }
        catch (InvalidOperationException ex)
        {
            // Аккаунт ещё подключается или уже вышел.
            System.Diagnostics.Debug.WriteLine($"Switch account failed: {ex.Message}");
        }
    }

    /// <summary>Уход с экрана (выход, вход в другой аккаунт): закрыть окна и сохранить черновики.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _hub.CurrentChanged -= OnCurrentChanged;
        _accounts.AccountsChanged -= OnAccountsChanged;
        await DetachAsync();
    }
}
