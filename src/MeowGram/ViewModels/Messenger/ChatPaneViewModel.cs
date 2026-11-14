using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeowGram.Core.Messaging;
using MeowGram.Localization;
using MeowGram.Services;
using MeowGram.ViewModels.Messenger.Feed;
using TdLib;

namespace MeowGram.ViewModels.Messenger;

/// <summary>Что делает кнопка справа от поля ввода.</summary>
public enum ComposerAction
{
    /// <summary>Пусто — кнопка записи голосового (пока неактивна).</summary>
    Record,
    Send,
    Save,
}

/// <summary>Действия окна, которые выполняет главная модель (сплит, пересылка, инфо-панель).</summary>
public interface IPaneHost
{
    void Focus(ChatPaneViewModel pane);

    void Close(ChatPaneViewModel pane);

    void Forward(ChatPaneViewModel pane, MessageViewModel message);

    void ToggleInfo(ChatPaneViewModel pane);

    bool IsInfoOpenFor(ChatPaneViewModel pane);
}

/// <summary>
/// Окно чата: шапка, закреплённое сообщение, лента (история страницами, серии, разделители, прочтение),
/// поле ввода (ответ, редактирование, черновик), меню сообщения, кнопка «вниз», поиск по чату.
/// </summary>
public sealed partial class ChatPaneViewModel : ObservableObject, IAsyncDisposable
{
    private const int PageSize = 50;

    private readonly MessagingContext _context;
    private readonly IPaneHost _host;
    private readonly List<MessageViewModel> _messages = [];
    private readonly Dictionary<long, MessageViewModel> _byId = [];
    private readonly Dictionary<DateOnly, DayHeaderViewModel> _dayHeaders = [];
    private readonly Dictionary<long, ServiceMessageViewModel> _services = [];
    private readonly UnreadSeparatorViewModel _unreadSeparator = new();
    private readonly HashSet<long> _viewed = [];
    private readonly HashSet<long> _hiddenPins = [];
    private readonly bool _isGroup;
    private long _unreadAfterId;
    private string _initialDraft = string.Empty;
    private long _initialDraftReply;
    private bool _loadingOlder;
    private bool _loadingNewer;
    private bool _disposed;
    private IReadOnlyList<long> _searchHits = [];

    public ChatPaneViewModel(MessagingContext context, ChatRows rows, long chatId, IPaneHost host)
    {
        _context = context;
        _host = host;
        ChatId = chatId;
        Row = rows.Get(chatId);
        _isGroup = context.Store.GetCategory(chatId) is ChatCategory.Group or ChatCategory.Channel;
        IsChannel = context.Store.GetCategory(chatId) == ChatCategory.Channel;

        _context.Messages.Changed += OnMessageEvent;
        _context.Store.ChatChanged += OnChatChanged;
        Row.PropertyChanged += OnRowChanged;
    }

    public long ChatId { get; }

    /// <summary>Название, аватарка, подпись, «печатает», номер окна — общая модель со строкой списка.</summary>
    public ChatRowViewModel Row { get; }

    public bool IsChannel { get; }

    public bool IsGroup => _isGroup;

    public ObservableCollection<FeedItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Окон больше одного: компактная шапка сплита с номером окна и крестиком.</summary>
    [ObservableProperty]
    public partial bool IsCompact { get; set; }

    /// <summary>Номер окна (1–4).</summary>
    [ObservableProperty]
    public partial int Slot { get; set; } = 1;

    public bool IsWide => !IsCompact;

    /// <summary>Обводка акцентом: активное окно, когда окон несколько.</summary>
    public bool ShowRing => IsActive && IsCompact;

    partial void OnIsCompactChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWide));
        OnPropertyChanged(nameof(ShowRing));
    }

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(ShowRing));

    /// <summary>Отправить выбранные файлы (кнопка-скрепка).</summary>
    public async Task SendFilesAsync(IEnumerable<string> paths)
    {
        var reply = ReplyTo?.MessageId ?? 0;
        ReplyTo = null;
        if (HasNewer)
        {
            ClearMessages();
            await Safe(LoadLatestAsync);
        }

        foreach (var path in paths)
        {
            await Safe(() => _context.Messages.SendFileAsync(ChatId, path, reply));
            reply = 0;
        }

        ScrollRequested?.Invoke(null);
    }

    /// <summary>Вставить эмодзи в позицию курсора поля ввода.</summary>
    public int InsertText(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, ComposerText.Length);
        ComposerText = ComposerText.Insert(caret, text);
        return caret + text.Length;
    }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    public bool HasOlder { get; private set; } = true;

    /// <summary>Открыты не самые свежие сообщения (переход к старому): новые не дописываются, догружаются снизу.</summary>
    public bool HasNewer { get; private set; }

    // ── Шапка ─────────────────────────────────────────────────────────

    public string HeaderStatus => Row.IsTyping && !_isGroup ? Row.TypingText : Row.Subtitle;

    public bool HeaderStatusIsAccent => Row.IsTyping && !_isGroup;

    public bool HeaderStatusIsOnline => Row.ShowOnline && !Row.IsTyping;

    public bool IsInfoOpen => _host.IsInfoOpenFor(this);

    // ── Закреп ────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial long PinnedMessageId { get; private set; }

    [ObservableProperty]
    public partial string PinnedText { get; private set; } = string.Empty;

    public bool ShowPinned => PinnedMessageId != 0 && !_hiddenPins.Contains(PinnedMessageId);

    // ── Поле ввода ────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string ComposerText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ReplyPreviewViewModel? ReplyTo { get; private set; }

    [ObservableProperty]
    public partial MessageViewModel? Editing { get; private set; }

    public ComposerAction ComposerAction =>
        Editing is not null ? ComposerAction.Save : ComposerText.Trim().Length > 0 ? ComposerAction.Send : ComposerAction.Record;

    public bool HasComposerContext => ReplyTo is not null || Editing is not null;

    public bool IsEditingMode => Editing is not null;

    public string ComposerContextTitle => Editing is not null
        ? Loc.Instance["Composer_Editing"]
        : ReplyTo is not null ? Loc.Instance.Format("Composer_ReplyTo", ReplyTo.Name) : string.Empty;

    public string ComposerContextText => Editing?.PlainText ?? ReplyTo?.Text ?? string.Empty;

    /// <summary>В канале без прав писать поле ввода не показывается.</summary>
    public bool CanWrite => _context.Store.GetChat(ChatId)?.Permissions?.CanSendBasicMessages != false || !IsChannel;

    // ── Кнопка «вниз» ─────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool IsAtBottom { get; set; } = true;

    public int UnreadCount => _context.Store.GetChat(ChatId)?.UnreadCount ?? 0;

    public bool ShowScrollDown => !IsAtBottom || HasNewer;

    public bool HasUnreadBadge => UnreadCount > 0;

    // ── Поиск по чату ─────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SearchIndex { get; private set; }

    public string SearchStatus => _searchHits.Count == 0
        ? (SearchQuery.Trim().Length == 0 ? string.Empty : Loc.Instance["Search_NothingInChat"])
        : Loc.Instance.Format("Search_InChatPosition", SearchIndex + 1, _searchHits.Count);

    /// <summary>Просьба к ленте: прокрутить к элементу (null — в самый низ).</summary>
    public event Action<FeedItemViewModel?>? ScrollRequested;

    /// <summary>Просьба поставить фокус в поле ввода.</summary>
    public event Action? ComposerFocusRequested;

    /// <summary>Скопировать текст (буфер обмена знает только окно).</summary>
    public event Action<string>? CopyRequested;

    /// <summary>Загрузить начальную страницу: с непрочитанного, с нужного сообщения или самые свежие.</summary>
    public async Task OpenAsync(long focusMessageId = 0)
    {
        var chat = _context.Store.GetChat(ChatId);
        await Safe(() => _context.Messages.OpenChatAsync(ChatId));

        LoadDraft(chat);
        _ = RefreshPinnedAsync();

        _unreadAfterId = chat is { UnreadCount: > 0, LastReadInboxMessageId: > 0 } ? chat.LastReadInboxMessageId : 0;
        try
        {
            if (focusMessageId != 0)
                await LoadAroundAsync(focusMessageId, highlight: true);
            else if (_unreadAfterId != 0)
                await LoadAroundAsync(_unreadAfterId, highlight: false, scrollTo: _unreadSeparator);
            else
                await LoadLatestAsync();
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            HasOlder = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Лента прокручена к верху — старые сообщения.</summary>
    public async Task LoadOlderAsync()
    {
        if (_loadingOlder || !HasOlder || _messages.Count == 0)
            return;

        _loadingOlder = true;
        try
        {
            var oldest = _messages[0].Id;
            var page = await _context.Messages.GetHistoryAsync(ChatId, oldest, 0, PageSize);
            var added = AddMessages(page.Where(m => m.Id < oldest));
            if (added == 0)
                HasOlder = false;
            RebuildFeed();
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            HasOlder = false;
        }
        finally
        {
            _loadingOlder = false;
        }
    }

    /// <summary>Лента прокручена к низу, а открыта не самая свежая часть — догрузить новее.</summary>
    public async Task LoadNewerAsync()
    {
        if (_loadingNewer || !HasNewer || _messages.Count == 0)
            return;

        _loadingNewer = true;
        try
        {
            var newest = _messages[^1].Id;
            var page = await _context.Messages.GetHistoryAsync(ChatId, newest, -(PageSize - 1), PageSize);
            var added = AddMessages(page.Where(m => m.Id > newest));
            var lastId = _context.Store.GetChat(ChatId)?.LastMessage?.Id ?? 0;
            if (added == 0 || _messages[^1].Id >= lastId)
                SetHasNewer(false);
            RebuildFeed();
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            SetHasNewer(false);
        }
        finally
        {
            _loadingNewer = false;
        }
    }

    /// <summary>Показанные на экране сообщения — отметить прочитанными.</summary>
    public void MarkVisible(IEnumerable<MessageViewModel> visible)
    {
        var lastRead = _context.Store.GetChat(ChatId)?.LastReadInboxMessageId ?? 0;
        var ids = visible.Where(m => m.IsIncoming && m.Id > lastRead && _viewed.Add(m.Id)).Select(m => m.Id).ToArray();
        if (ids.Length > 0)
            _ = Safe(() => _context.Messages.ViewAsync(ChatId, ids));
    }

    /// <summary>Перейти к сообщению (цитата, закреп, результат поиска): прокрутить или загрузить вокруг него.</summary>
    public async Task JumpToAsync(long messageId)
    {
        if (_byId.TryGetValue(messageId, out var loaded))
        {
            Highlight(loaded);
            ScrollRequested?.Invoke(loaded);
            return;
        }

        try
        {
            ClearMessages();
            await LoadAroundAsync(messageId, highlight: true);
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            // Сообщение удалено — остаёмся где были.
        }
    }

    [RelayCommand]
    private async Task ScrollDownAsync()
    {
        if (HasNewer)
        {
            ClearMessages();
            _unreadAfterId = 0;
            await Safe(LoadLatestAsync);
        }

        ScrollRequested?.Invoke(null);
    }

    [RelayCommand]
    private void FocusPane() => _host.Focus(this);

    [RelayCommand]
    private void ClosePane() => _host.Close(this);

    [RelayCommand]
    private void ToggleInfo() => _host.ToggleInfo(this);

    public void RaiseInfoChanged() => OnPropertyChanged(nameof(IsInfoOpen));

    // ── Поле ввода ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = ComposerText.Trim();
        if (text.Length == 0)
            return;

        if (Editing is { } editing)
        {
            ComposerText = string.Empty;
            Editing = null;
            await Safe(() => _context.Messages.EditTextAsync(ChatId, editing.Id, text));
            return;
        }

        var reply = ReplyTo?.MessageId ?? 0;
        ComposerText = string.Empty;
        ReplyTo = null;
        _initialDraft = string.Empty;
        _initialDraftReply = 0;
        if (HasNewer)
        {
            ClearMessages();
            await Safe(LoadLatestAsync);
        }

        await Safe(() => _context.Messages.SendTextAsync(ChatId, text, reply));
        ScrollRequested?.Invoke(null);
    }

    [RelayCommand]
    private void CancelComposerContext()
    {
        if (Editing is not null)
            ComposerText = string.Empty;
        Editing = null;
        ReplyTo = null;
    }

    /// <summary>Стрелка вверх в пустом поле — редактировать последнее своё сообщение.</summary>
    public bool EditLastOutgoing()
    {
        if (ComposerText.Length > 0)
            return false;

        var last = _messages.LastOrDefault(m => m.CanEdit);
        if (last is null)
            return false;

        Edit(last);
        return true;
    }

    // ── Меню сообщения ────────────────────────────────────────────────

    [RelayCommand]
    private void Reply(MessageViewModel? message)
    {
        if (message is null)
            return;

        Editing = null;
        ReplyTo = new ReplyPreviewViewModel(message.Id, NameOf(message.Message), message.PlainText.Length > 0 ? message.PlainText : ChatText.Preview(_context.Store, message.Message).Text);
        ComposerFocusRequested?.Invoke();
    }

    [RelayCommand]
    private void Edit(MessageViewModel? message)
    {
        if (message is not { CanEdit: true })
            return;

        ReplyTo = null;
        Editing = message;
        ComposerText = message.PlainText;
        ComposerFocusRequested?.Invoke();
    }

    [RelayCommand]
    private void Copy(MessageViewModel? message)
    {
        var text = message?.PlainText;
        if (!string.IsNullOrEmpty(text))
            CopyRequested?.Invoke(text);
    }

    [RelayCommand]
    private void Forward(MessageViewModel? message)
    {
        if (message is not null)
            _host.Forward(this, message);
    }

    [RelayCommand]
    private Task TogglePinAsync(MessageViewModel? message) => message is null
        ? Task.CompletedTask
        : Safe(() => message.IsPinned
            ? _context.Messages.UnpinAsync(ChatId, message.Id)
            : _context.Messages.PinAsync(ChatId, message.Id));

    [RelayCommand]
    private async Task DeleteAsync(MessageViewModel? message)
    {
        if (message is null)
            return;

        try
        {
            await _context.Messages.DeleteAsync(ChatId, [message.Id], forEveryone: true);
        }
        catch (InvalidOperationException)
        {
            // Сессия закрыта.
        }
        catch (TdException)
        {
            // Удалить у всех нельзя (чужое сообщение, старое) — удаляем у себя.
            await Safe(() => _context.Messages.DeleteAsync(ChatId, [message.Id], forEveryone: false));
        }
    }

    [RelayCommand]
    private Task RetryAsync(MessageViewModel? message) =>
        message is null ? Task.CompletedTask : Safe(() => _context.Messages.ResendAsync(ChatId, message.Id));

    [RelayCommand]
    private Task ToggleReactionAsync(object? parameter)
    {
        if (parameter is not ReactionCommandArgs args)
            return Task.CompletedTask;
        return Safe(() => _context.Messages.SetReactionAsync(ChatId, args.Message.Id, args.Reaction.Emoji, !args.Reaction.IsChosen));
    }

    [RelayCommand]
    private Task OpenReplyAsync(MessageViewModel? message) =>
        message?.Reply is { } reply ? JumpToAsync(reply.MessageId) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenPinnedAsync() => PinnedMessageId != 0 ? JumpToAsync(PinnedMessageId) : Task.CompletedTask;

    /// <summary>Крестик на закрепе — скрыть его в этом окне (как в Telegram), не открепляя у всех.</summary>
    [RelayCommand]
    private void HidePinned()
    {
        _hiddenPins.Add(PinnedMessageId);
        OnPropertyChanged(nameof(ShowPinned));
    }

    // ── Поиск по чату ─────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearching = !IsSearching;
        if (!IsSearching)
        {
            SearchQuery = string.Empty;
            _searchHits = [];
            OnPropertyChanged(nameof(SearchStatus));
        }
    }

    [RelayCommand]
    private Task SearchNextAsync() => MoveSearch(+1);

    [RelayCommand]
    private Task SearchPreviousAsync() => MoveSearch(-1);

    partial void OnSearchQueryChanged(string value) => _ = RunSearchAsync(value.Trim());

    private async Task RunSearchAsync(string query)
    {
        if (query.Length == 0)
        {
            _searchHits = [];
            OnPropertyChanged(nameof(SearchStatus));
            return;
        }

        await Task.Delay(300);
        if (query != SearchQuery.Trim())
            return;

        try
        {
            var page = await _context.Search.SearchChatMessagesAsync(ChatId, query, MessageSearchKind.All, limit: 100);
            if (query != SearchQuery.Trim())
                return;
            _searchHits = page.Messages.Select(m => m.Id).ToArray();
            SearchIndex = 0;
            OnPropertyChanged(nameof(SearchStatus));
            if (_searchHits.Count > 0)
                await JumpToAsync(_searchHits[0]);
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            _searchHits = [];
            OnPropertyChanged(nameof(SearchStatus));
        }
    }

    private async Task MoveSearch(int delta)
    {
        if (_searchHits.Count == 0)
            return;

        // Результаты идут от новых к старым: «вверх» — дальше по списку.
        SearchIndex = Math.Clamp(SearchIndex + delta, 0, _searchHits.Count - 1);
        OnPropertyChanged(nameof(SearchStatus));
        await JumpToAsync(_searchHits[SearchIndex]);
    }

    // ── Загрузка ──────────────────────────────────────────────────────

    private async Task LoadLatestAsync()
    {
        SetHasNewer(false);
        HasOlder = true;
        // TDLib с первого раза может отдать только последнее сообщение из кэша — повторяем, пока не наберётся страница.
        for (var attempt = 0; attempt < 4 && _messages.Count < 30; attempt++)
        {
            var from = _messages.Count > 0 ? _messages[0].Id : 0;
            var page = await _context.Messages.GetHistoryAsync(ChatId, from, 0, PageSize);
            if (AddMessages(page) == 0)
            {
                if (attempt > 0)
                    HasOlder = false;
                if (from != 0)
                    break;
            }
        }

        RebuildFeed();
        ScrollRequested?.Invoke(null);
    }

    private async Task LoadAroundAsync(long messageId, bool highlight, FeedItemViewModel? scrollTo = null)
    {
        HasOlder = true;
        var page = await _context.Messages.GetHistoryAsync(ChatId, messageId, -(PageSize / 2), PageSize);
        AddMessages(page);
        var lastId = _context.Store.GetChat(ChatId)?.LastMessage?.Id ?? 0;
        SetHasNewer(_messages.Count > 0 && _messages[^1].Id < lastId);
        RebuildFeed();

        if (_byId.TryGetValue(messageId, out var target) && highlight)
            Highlight(target);

        var item = scrollTo is not null && Items.Contains(scrollTo) ? scrollTo : target;
        ScrollRequested?.Invoke(item);
    }

    private int AddMessages(IEnumerable<TdApi.Message> page)
    {
        var added = 0;
        foreach (var message in page)
        {
            if (message.ChatId != ChatId || _byId.ContainsKey(message.Id))
                continue;

            var vm = new MessageViewModel(_context, message, _isGroup);
            Insert(vm);
            added++;
        }

        return added;
    }

    private void Insert(MessageViewModel vm)
    {
        var index = _messages.BinarySearch(vm, Comparer<MessageViewModel>.Create((a, b) => a.Id.CompareTo(b.Id)));
        _messages.Insert(index < 0 ? ~index : index, vm);
        _byId[vm.Id] = vm;
        ResolveReply(vm);
    }

    private void ClearMessages()
    {
        foreach (var vm in _messages)
            vm.Dispose();
        _messages.Clear();
        _byId.Clear();
        Items.Clear();
    }

    private void SetHasNewer(bool value)
    {
        HasNewer = value;
        OnPropertyChanged(nameof(HasNewer));
        OnPropertyChanged(nameof(ShowScrollDown));
    }

    /// <summary>Пересобрать ленту: серии, скругления, имена, аватарки, плашки дней и «Непрочитанные».</summary>
    private void RebuildFeed()
    {
        var zone = TimeZoneInfo.Local;
        var now = DateTimeOffset.Now;
        var inputs = _messages.Select(m => new GroupingItem(m.Id, m.SenderKey, m.IsOutgoing, ChatText.IsService(m.Message.Content), m.Date)).ToArray();
        var results = MessageGrouping.Compute(inputs, _unreadAfterId, zone);

        var target = new List<FeedItemViewModel>(_messages.Count + 8);
        for (var i = 0; i < _messages.Count; i++)
        {
            var vm = _messages[i];
            var r = results[i];
            if (r.DateSeparatorBefore)
                target.Add(DayHeader(vm.Date, now));
            if (r.UnreadSeparatorBefore)
                target.Add(_unreadSeparator);

            if (inputs[i].IsService)
            {
                target.Add(Service(vm));
                continue;
            }

            var corners = MessageGrouping.Corners(r.Position, vm.IsOutgoing);
            vm.Corners = new CornerRadius(corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft);
            vm.ShowSenderName = vm.HasAvatarColumn && r.StartsSeries && !vm.IsBare;
            vm.ShowAvatar = vm.HasAvatarColumn && r.EndsSeries;
            vm.EndsSeries = r.EndsSeries;
            target.Add(vm);
        }

        SyncItems(target);
    }

    private DayHeaderViewModel DayHeader(DateTimeOffset date, DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(date.DateTime);
        if (!_dayHeaders.TryGetValue(day, out var header))
            _dayHeaders[day] = header = new DayHeaderViewModel(ChatText.DayHeader(date, now));
        return header;
    }

    private ServiceMessageViewModel Service(MessageViewModel vm)
    {
        var text = ChatText.ServiceText(_context.Store, vm.Message);
        if (!_services.TryGetValue(vm.Id, out var service))
            _services[vm.Id] = service = new ServiceMessageViewModel(vm.Id, text);
        else
            service.Text = text;
        return service;
    }

    private void SyncItems(List<FeedItemViewModel> target)
    {
        for (var i = 0; i < target.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], target[i]))
                continue;

            var existing = -1;
            for (var j = i + 1; j < Items.Count; j++)
            {
                if (ReferenceEquals(Items[j], target[i]))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
                Items.Move(existing, i);
            else
                Items.Insert(i, target[i]);
        }

        while (Items.Count > target.Count)
            Items.RemoveAt(Items.Count - 1);
    }

    private void ResolveReply(MessageViewModel vm)
    {
        if (vm.Message.ReplyTo is not TdApi.MessageReplyTo.MessageReplyToMessage reply || reply.MessageId == 0)
            return;

        if (reply.ChatId == ChatId && _byId.TryGetValue(reply.MessageId, out var original))
        {
            vm.SetReply(new ReplyPreviewViewModel(original.Id, NameOf(original.Message), ChatText.Preview(_context.Store, original.Message).Text));
            return;
        }

        _ = LoadReplyAsync(vm, reply);
    }

    private async Task LoadReplyAsync(MessageViewModel vm, TdApi.MessageReplyTo.MessageReplyToMessage reply)
    {
        try
        {
            var original = await _context.Messages.GetMessageAsync(reply.ChatId == 0 ? ChatId : reply.ChatId, reply.MessageId);
            vm.SetReply(new ReplyPreviewViewModel(original.Id, NameOf(original), ChatText.Preview(_context.Store, original).Text));
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            if (reply.Content is { } content)
            {
                var fake = new TdApi.Message { Content = content, ChatId = ChatId };
                vm.SetReply(new ReplyPreviewViewModel(reply.MessageId, Loc.Instance["Message_ReplyUnknown"], ChatText.Preview(_context.Store, fake).Text));
            }
        }
    }

    private string NameOf(TdApi.Message message) =>
        message.IsOutgoing && !_isGroup ? Loc.Instance["Message_You"] : ChatText.SenderName(_context.Store, message.SenderId) is { Length: > 0 } name ? name : Row.Title;

    private async void Highlight(MessageViewModel vm)
    {
        vm.IsHighlighted = true;
        await Task.Delay(1600);
        vm.IsHighlighted = false;
    }

    private void LoadDraft(TdApi.Chat? chat)
    {
        if (chat?.DraftMessage is not { } draft)
            return;

        _initialDraft = draft.Content is TdApi.DraftMessageContent.DraftMessageContentText { Text.Text: { } text } ? text : string.Empty;
        _initialDraftReply = draft.ReplyTo is TdApi.InputMessageReplyTo.InputMessageReplyToMessage r ? r.MessageId : 0;
        ComposerText = _initialDraft;
        if (_initialDraftReply != 0)
            _ = LoadDraftReplyAsync(_initialDraftReply);
    }

    private async Task LoadDraftReplyAsync(long messageId)
    {
        try
        {
            var original = await _context.Messages.GetMessageAsync(ChatId, messageId);
            ReplyTo = new ReplyPreviewViewModel(original.Id, NameOf(original), ChatText.Preview(_context.Store, original).Text);
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            // Сообщение для ответа удалили.
        }
    }

    private async Task RefreshPinnedAsync()
    {
        try
        {
            var pinned = await _context.Messages.GetPinnedAsync(ChatId);
            PinnedMessageId = pinned?.Id ?? 0;
            PinnedText = pinned is null ? string.Empty : ChatText.Preview(_context.Store, pinned).Text;
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            PinnedMessageId = 0;
        }

        OnPropertyChanged(nameof(ShowPinned));
    }

    // ── События ядра ──────────────────────────────────────────────────

    private void OnMessageEvent(MessageEvent e)
    {
        if (e.ChatId != ChatId || _disposed)
            return;

        switch (e)
        {
            case MessageAdded added when !HasNewer && !_byId.ContainsKey(added.Message.Id):
                Insert(new MessageViewModel(_context, added.Message, _isGroup));
                RebuildFeed();
                if (added.Message.IsOutgoing || IsAtBottom)
                    ScrollRequested?.Invoke(null);
                break;

            case MessageReplaced replaced when _byId.Remove(replaced.OldMessageId, out var vm):
                _messages.Remove(vm);
                vm.Update(replaced.Message);
                if (!_byId.ContainsKey(vm.Id))
                    Insert(vm);
                RebuildFeed();
                break;

            case MessageContentChanged changed when _byId.TryGetValue(changed.MessageId, out var vm):
                vm.SetContent(changed.Content);
                RebuildFeed();
                break;

            case MessageEdited edited when _byId.TryGetValue(edited.MessageId, out var vm):
                vm.SetEdited(edited.EditDate);
                break;

            case MessagesDeleted deleted:
                var any = false;
                foreach (var id in deleted.MessageIds)
                {
                    if (_byId.Remove(id, out var gone))
                    {
                        _messages.Remove(gone);
                        gone.Dispose();
                        any = true;
                    }
                }

                if (any)
                    RebuildFeed();
                break;

            case MessageInteractionChanged interaction when _byId.TryGetValue(interaction.MessageId, out var vm):
                vm.SetInteraction(interaction.Info);
                break;

            case MessagePinnedChanged pinned:
                if (_byId.TryGetValue(pinned.MessageId, out var pinnedVm))
                    pinnedVm.SetPinned(pinned.IsPinned);
                _ = RefreshPinnedAsync();
                break;

            case MessageContentOpened opened when _byId.TryGetValue(opened.MessageId, out var vm)
                                              && vm.Content is VoiceContentViewModel voice:
                voice.IsListened = true;
                break;
        }
    }

    private void OnChatChanged(long chatId)
    {
        if (chatId != ChatId)
            return;

        var lastReadOutbox = _context.Store.GetChat(ChatId)?.LastReadOutboxMessageId ?? 0;
        foreach (var vm in _messages)
        {
            if (vm.IsOutgoing)
                vm.RefreshStatus(lastReadOutbox);
        }

        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(HasUnreadBadge));
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatRowViewModel.Subtitle) or nameof(ChatRowViewModel.IsTyping)
            or nameof(ChatRowViewModel.TypingText) or nameof(ChatRowViewModel.IsOnline))
        {
            OnPropertyChanged(nameof(HeaderStatus));
            OnPropertyChanged(nameof(HeaderStatusIsAccent));
            OnPropertyChanged(nameof(HeaderStatusIsOnline));
        }
    }

    partial void OnComposerTextChanged(string value) => OnPropertyChanged(nameof(ComposerAction));

    partial void OnEditingChanged(MessageViewModel? value) => RaiseComposerContext();

    partial void OnReplyToChanged(ReplyPreviewViewModel? value) => RaiseComposerContext();

    partial void OnIsAtBottomChanged(bool value) => OnPropertyChanged(nameof(ShowScrollDown));

    private void RaiseComposerContext()
    {
        OnPropertyChanged(nameof(ComposerAction));
        OnPropertyChanged(nameof(HasComposerContext));
        OnPropertyChanged(nameof(IsEditingMode));
        OnPropertyChanged(nameof(ComposerContextTitle));
        OnPropertyChanged(nameof(ComposerContextText));
    }

    private static async Task Safe(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (TdException ex)
        {
            System.Diagnostics.Debug.WriteLine($"TDLib: {ex.Error.Code} {ex.Error.Message}");
        }
        catch (InvalidOperationException ex)
        {
            // Сессию уже закрыли (выход, смена аккаунта) — запрос больше не нужен.
            System.Diagnostics.Debug.WriteLine($"TDLib session closed: {ex.Message}");
        }
    }

    /// <summary>Сохранить черновик, закрыть чат в TDLib, отписаться.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _context.Messages.Changed -= OnMessageEvent;
        _context.Store.ChatChanged -= OnChatChanged;
        Row.PropertyChanged -= OnRowChanged;

        var text = Editing is null ? ComposerText : string.Empty;
        var reply = ReplyTo?.MessageId ?? 0;
        if (text != _initialDraft || reply != _initialDraftReply)
            await Safe(() => _context.Messages.SetDraftAsync(ChatId, text, reply));
        await Safe(() => _context.Messages.CloseChatAsync(ChatId));

        foreach (var vm in _messages)
            vm.Dispose();
    }
}

/// <summary>Параметр команды реакции: на каком сообщении какая реакция.</summary>
public sealed record ReactionCommandArgs(MessageViewModel Message, ReactionViewModel Reaction);
