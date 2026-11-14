using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeowGram.Core.Messaging;
using MeowGram.Localization;
using MeowGram.Services;
using TdLib;

namespace MeowGram.ViewModels.Messenger;

/// <summary>Кусок текста с отметкой «совпало с запросом» — для подсветки (mark в макете).</summary>
public sealed record TextSegment(string Text, bool IsMatch);

/// <summary>Вкладка поиска.</summary>
public enum SearchTab
{
    All,
    Chats,
    Media,
    Links,
    Files,
}

/// <summary>Найденный чат: строка + подпись + подсвеченное название.</summary>
public sealed class SearchChatItem(ChatRowViewModel row, string subtitle, IReadOnlyList<TextSegment> title)
{
    public ChatRowViewModel Row { get; } = row;

    public string Subtitle { get; } = subtitle;

    public IReadOnlyList<TextSegment> TitleSegments { get; } = title;
}

/// <summary>Найденное сообщение: чат, время, отправитель и фрагмент текста вокруг совпадения.</summary>
public sealed class SearchMessageItem(ChatRowViewModel row, long messageId, string time, string prefix, IReadOnlyList<TextSegment> snippet)
{
    public ChatRowViewModel Row { get; } = row;

    public long MessageId { get; } = messageId;

    public string Time { get; } = time;

    public string Prefix { get; } = prefix;

    public bool HasPrefix => Prefix.Length > 0;

    public IReadOnlyList<TextSegment> Snippet { get; } = snippet;
}

/// <summary>
/// Поиск вместо списка чатов (Ctrl+K): свои чаты и контакты, глобальный поиск, сообщения; вкладки фильтруют сообщения.
/// Режим «Новое сообщение» — пустой запрос показывает контакты.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private const int SnippetRadius = 28;

    private readonly MessagingContext _context;
    private readonly ChatRows _rows;
    private readonly Action<long, long> _open;
    private CancellationTokenSource? _cts;
    private string _nextOffset = string.Empty;

    /// <param name="open">Открыть чат (id чата, id сообщения или 0).</param>
    public SearchViewModel(MessagingContext context, ChatRows rows, Action<long, long> open)
    {
        _context = context;
        _rows = rows;
        _open = open;
    }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SearchTab Tab { get; set; } = SearchTab.All;

    /// <summary>Открыт из «Новое сообщение»: без запроса показываются контакты.</summary>
    [ObservableProperty]
    public partial bool IsContactsMode { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public ObservableCollection<SearchChatItem> Chats { get; } = [];

    public ObservableCollection<SearchChatItem> Global { get; } = [];

    public ObservableCollection<SearchMessageItem> Messages { get; } = [];

    [ObservableProperty]
    public partial int MessageCount { get; private set; }

    public bool ShowChats => Chats.Count > 0 && Tab is SearchTab.All or SearchTab.Chats;

    public bool ShowGlobal => Global.Count > 0 && Tab is SearchTab.All or SearchTab.Chats;

    public bool ShowMessages => Messages.Count > 0 && Tab != SearchTab.Chats;

    public bool IsNothingFound => !IsBusy && !ShowChats && !ShowGlobal && !ShowMessages && (Query.Trim().Length > 0 || Tab != SearchTab.All);

    public string ChatsHeader => IsContactsMode && Query.Trim().Length == 0 ? Loc.Instance["Search_Contacts"] : Loc.Instance["Search_ChatsAndContacts"];

    public string MessagesHeader => Loc.Instance.Format("Search_MessagesCount", MessageCount);

    public void Begin(bool contactsMode)
    {
        IsContactsMode = contactsMode;
        Tab = SearchTab.All;
        if (Query.Length > 0)
            Query = string.Empty;
        else
            Restart();
    }

    [RelayCommand]
    private void SetTab(SearchTab tab) => Tab = tab;

    [RelayCommand]
    private void Clear() => Query = string.Empty;

    [RelayCommand]
    private void OpenChat(SearchChatItem? item)
    {
        if (item is not null)
            _open(item.Row.ChatId, 0);
    }

    [RelayCommand]
    private void OpenMessage(SearchMessageItem? item)
    {
        if (item is not null)
            _open(item.Row.ChatId, item.MessageId);
    }

    /// <summary>Enter в поле поиска: первый найденный чат.</summary>
    public bool OpenFirst()
    {
        var first = Chats.FirstOrDefault() ?? Global.FirstOrDefault();
        if (first is null)
            return false;
        _open(first.Row.ChatId, 0);
        return true;
    }

    /// <summary>Прокрутили результаты вниз — ещё сообщения.</summary>
    public async Task LoadMoreMessagesAsync()
    {
        if (IsBusy || _nextOffset.Length == 0 || Tab == SearchTab.Chats)
            return;

        var token = _cts?.Token ?? CancellationToken.None;
        var query = Query.Trim();
        try
        {
            IsBusy = true;
            var page = await _context.Search.SearchMessagesAsync(query, KindOf(Tab), _nextOffset);
            if (token.IsCancellationRequested)
                return;
            _nextOffset = page.NextOffset;
            AddMessages(page.Messages, query);
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            _nextOffset = string.Empty;
        }
        finally
        {
            IsBusy = false;
            RaiseSections();
        }
    }

    partial void OnQueryChanged(string value) => Restart();

    partial void OnTabChanged(SearchTab value) => Restart();

    private void Restart()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = RunAsync(Query.Trim(), Tab, _cts.Token);
    }

    private async Task RunAsync(string query, SearchTab tab, CancellationToken token)
    {
        try
        {
            if (query.Length > 0)
                await Task.Delay(250, token);

            IsBusy = true;
            RaiseSections();

            var chatIds = query.Length == 0
                ? IsContactsMode ? await _context.Search.GetContactChatIdsAsync() : []
                : tab is SearchTab.All or SearchTab.Chats ? await _context.Search.SearchChatsAsync(query) : [];
            token.ThrowIfCancellationRequested();
            Replace(Chats, chatIds.Select(id => ChatItem(id, query, isGlobal: false)));

            var globalIds = tab is SearchTab.All or SearchTab.Chats && query.Length > 0
                ? (await _context.Search.SearchPublicChatsAsync(query)).Where(id => !chatIds.Contains(id)).ToArray()
                : [];
            token.ThrowIfCancellationRequested();
            Replace(Global, globalIds.Select(id => ChatItem(id, query, isGlobal: true)));

            Messages.Clear();
            MessageCount = 0;
            _nextOffset = string.Empty;
            if (tab != SearchTab.Chats && (query.Length > 0 || tab != SearchTab.All))
            {
                var page = await _context.Search.SearchMessagesAsync(query, KindOf(tab));
                token.ThrowIfCancellationRequested();
                MessageCount = page.TotalCount;
                _nextOffset = page.NextOffset;
                AddMessages(page.Messages, query);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            // Сеть пропала посреди поиска — показываем, что успели найти.
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsBusy = false;
                RaiseSections();
            }
        }
    }

    private static MessageSearchKind KindOf(SearchTab tab) => tab switch
    {
        SearchTab.Media => MessageSearchKind.Media,
        SearchTab.Links => MessageSearchKind.Links,
        SearchTab.Files => MessageSearchKind.Files,
        _ => MessageSearchKind.All,
    };

    private SearchChatItem ChatItem(long chatId, string query, bool isGlobal)
    {
        var row = _rows.Get(chatId);
        return new SearchChatItem(row, ChatSubtitle(chatId, isGlobal), Highlight(row.Title, query));
    }

    private string ChatSubtitle(long chatId, bool isGlobal)
    {
        var L = Loc.Instance;
        var store = _context.Store;
        var now = DateTimeOffset.Now;
        var category = store.GetCategory(chatId);
        var username = Username(chatId);
        var count = store.GetMemberCount(chatId);

        if (isGlobal)
        {
            var members = count > 0 ? ChatText.Count(count, category == ChatCategory.Channel ? "Plural_Subscriber" : "Plural_Member") : null;
            return string.Join(" · ", new[] { username is null ? null : "@" + username, members }.OfType<string>());
        }

        return category switch
        {
            ChatCategory.Group => count > 0 ? $"{L["Chat_GroupLower"]} · {ChatText.Count(count, "Plural_Member")}" : L["Chat_GroupLower"],
            ChatCategory.Channel => count > 0 ? $"{L["Chat_ChannelLower"]} · {ChatText.Count(count, "Plural_Subscriber")}" : L["Chat_ChannelLower"],
            ChatCategory.Saved => string.Empty,
            _ => username is not null ? "@" + username : ChatText.ChatSubtitle(store, chatId, now),
        };
    }

    private string? Username(long chatId)
    {
        var store = _context.Store;
        var names = store.GetChat(chatId)?.Type switch
        {
            TdApi.ChatType.ChatTypePrivate p => store.GetUser(p.UserId)?.Usernames,
            TdApi.ChatType.ChatTypeSupergroup s => store.GetSupergroup(s.SupergroupId)?.Usernames,
            _ => null,
        };
        return names?.ActiveUsernames is { Length: > 0 } active ? active[0] : null;
    }

    private void AddMessages(IEnumerable<TdApi.Message> messages, string query)
    {
        var store = _context.Store;
        var now = DateTimeOffset.Now;
        foreach (var message in messages)
        {
            var row = _rows.Get(message.ChatId);
            var text = ChatText.Preview(store, message).Text;
            var prefix = store.GetCategory(message.ChatId) == ChatCategory.Group
                ? (message.IsOutgoing ? Loc.Instance["Chat_YouPrefix"] : ChatText.FirstName(store, message.SenderId) + ":")
                : string.Empty;
            Messages.Add(new SearchMessageItem(
                row,
                message.Id,
                ChatText.ListTime(ChatText.FromUnix(message.Date), now),
                prefix,
                Highlight(Snippet(text, query), query)));
        }
    }

    /// <summary>Фрагмент вокруг первого совпадения: «…по дизайну входа всё ок, только кнопки…».</summary>
    public static string Snippet(string text, string query)
    {
        if (query.Length == 0)
            return text;

        var at = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0 || at <= SnippetRadius)
            return text;

        var start = text.LastIndexOf(' ', Math.Max(0, at - SnippetRadius / 2));
        start = start < 0 ? at - SnippetRadius / 2 : start + 1;
        return "…" + text[start..];
    }

    /// <summary>Разбить текст на куски по совпадениям с запросом (без учёта регистра).</summary>
    public static IReadOnlyList<TextSegment> Highlight(string text, string query)
    {
        if (query.Length == 0 || text.Length == 0)
            return [new TextSegment(text, false)];

        var result = new List<TextSegment>();
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        var pos = 0;
        while (pos < text.Length)
        {
            // Длина совпадения по правилам культуры может отличаться от длины запроса (ß ↔ ss, мягкие переносы).
            var found = compare.IndexOf(text.AsSpan(pos), query.AsSpan(), CompareOptions.IgnoreCase, out var length);
            if (found < 0 || length == 0)
                break;
            var at = pos + found;
            if (at > pos)
                result.Add(new TextSegment(text[pos..at], false));
            result.Add(new TextSegment(text.Substring(at, length), true));
            pos = at + length;
        }

        if (pos < text.Length)
            result.Add(new TextSegment(text[pos..], false));
        return result;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private void RaiseSections()
    {
        OnPropertyChanged(nameof(ShowChats));
        OnPropertyChanged(nameof(ShowGlobal));
        OnPropertyChanged(nameof(ShowMessages));
        OnPropertyChanged(nameof(IsNothingFound));
        OnPropertyChanged(nameof(ChatsHeader));
        OnPropertyChanged(nameof(MessagesHeader));
    }
}
