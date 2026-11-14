using Avalonia.Threading;
using MeowGram.Core.Messaging;

namespace MeowGram.ViewModels.Messenger;

/// <summary>
/// Модели строк по id чата — одна на чат для списка, поиска и шапок окон. Держит их в актуальном состоянии
/// по событиям <see cref="ChatStore"/> и раз в пару секунд гасит «печатает», для которого не пришла отмена.
/// </summary>
public sealed class ChatRows : IDisposable
{
    private readonly MessagingContext _context;
    private readonly Dictionary<long, ChatRowViewModel> _rows = [];
    private readonly DispatcherTimer _typingTimer;

    public ChatRows(MessagingContext context)
    {
        _context = context;
        var store = context.Store;
        store.ChatChanged += OnChatChanged;
        store.UserChanged += OnUserChanged;
        store.GroupChanged += OnGroupChanged;
        store.ActionsChanged += OnActionsChanged;

        _typingTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => ExpireTyping());
        _typingTimer.Start();
    }

    /// <summary>Список, в котором показываются закреплённые (Main или Archive).</summary>
    public ChatListKind PinList { get; set; } = ChatListKind.Main;

    /// <summary>Создана новая строка (чтобы сплит проставил ей номер окна).</summary>
    public event Action<ChatRowViewModel>? RowCreated;

    public ChatRowViewModel Get(long chatId)
    {
        if (!_rows.TryGetValue(chatId, out var row))
        {
            _rows[chatId] = row = new ChatRowViewModel(_context, chatId);
            RowCreated?.Invoke(row);
        }

        return row;
    }

    public ChatRowViewModel? TryGet(long chatId) => _rows.GetValueOrDefault(chatId);

    public IEnumerable<ChatRowViewModel> All => _rows.Values;

    private void OnChatChanged(long chatId) => TryGet(chatId)?.Refresh(PinList);

    private void OnUserChanged(long userId)
    {
        foreach (var row in _rows.Values)
        {
            if (_context.Store.GetPrivateUserId(row.ChatId) == userId)
                row.Refresh(PinList);
        }
    }

    private void OnGroupChanged(long groupId)
    {
        foreach (var chatId in _context.Store.ChatsOfGroup(groupId))
            TryGet(chatId)?.Refresh(PinList);
    }

    private void OnActionsChanged(long chatId) => TryGet(chatId)?.RefreshTyping();

    private void ExpireTyping()
    {
        foreach (var row in _rows.Values)
        {
            if (row.IsTyping)
                row.RefreshTyping();
        }
    }

    public void Dispose()
    {
        _typingTimer.Stop();
        var store = _context.Store;
        store.ChatChanged -= OnChatChanged;
        store.UserChanged -= OnUserChanged;
        store.GroupChanged -= OnGroupChanged;
        store.ActionsChanged -= OnActionsChanged;
    }
}
