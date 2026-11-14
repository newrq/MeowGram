using TdLib;

namespace MeowGram.Core.Messaging;

/// <summary>Тип чата для папок рейла и оформления строки.</summary>
public enum ChatCategory
{
    Private,
    Saved,
    Bot,
    Group,
    Channel,
}

/// <summary>Папка рейла: локальный фильтр по типу поверх основного списка, «Архив» — архив TDLib.</summary>
public enum ChatFolder
{
    All,
    Personal,
    Groups,
    Channels,
    Bots,
    Archive,
}

public static class ChatFolders
{
    public static ChatListKind ListOf(ChatFolder folder) => folder == ChatFolder.Archive ? ChatListKind.Archive : ChatListKind.Main;

    public static bool Matches(ChatFolder folder, ChatCategory category) => folder switch
    {
        ChatFolder.Personal => category is ChatCategory.Private or ChatCategory.Saved,
        ChatFolder.Groups => category == ChatCategory.Group,
        ChatFolder.Channels => category == ChatCategory.Channel,
        ChatFolder.Bots => category == ChatCategory.Bot,
        _ => true,
    };
}

/// <summary>Кто что делает в чате («печатает», «записывает голосовое»).</summary>
public sealed record ChatActionInfo(TdApi.MessageSender Sender, TdApi.ChatAction Action, DateTimeOffset At);

/// <summary>
/// Состояние чатов одного аккаунта, собранное из обновлений TDLib: чаты, пользователи, группы, позиции в списках,
/// настройки уведомлений и действия «печатает». Объекты TDLib обновляются на месте (как советует документация TDLib).
/// Все изменения и события — в <see cref="IMainThread"/>, поэтому читать можно без блокировок из того же потока.
/// </summary>
public sealed class ChatStore
{
    /// <summary>Действие без отмены считается закончившимся через столько (TDLib повторяет его каждые ~5 с).</summary>
    public static readonly TimeSpan ActionLifetime = TimeSpan.FromSeconds(6);

    private readonly Dictionary<long, TdApi.Chat> _chats = [];
    private readonly Dictionary<long, TdApi.User> _users = [];
    private readonly Dictionary<long, TdApi.BasicGroup> _basicGroups = [];
    private readonly Dictionary<long, TdApi.Supergroup> _supergroups = [];
    private readonly Dictionary<long, List<ChatActionInfo>> _actions = [];
    private readonly Dictionary<Type, int> _scopeMuteFor = [];
    private readonly Dictionary<long, int> _onlineCounts = [];
    private readonly Dictionary<ChatListKind, long[]> _sorted = [];
    private readonly TimeProvider _time;

    public ChatStore(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Id своего пользователя (опция TDLib my_id), 0 — ещё неизвестен.</summary>
    public long MyUserId { get; private set; }

    /// <summary>Изменились данные чата (название, фото, последнее сообщение, счётчики, черновик...).</summary>
    public event Action<long>? ChatChanged;

    /// <summary>Изменился состав или порядок списка.</summary>
    public event Action<ChatListKind>? ListChanged;

    /// <summary>Изменился пользователь или его статус «в сети».</summary>
    public event Action<long>? UserChanged;

    /// <summary>Изменилась базовая группа или супергруппа (число участников и т.п.). Аргумент — id группы.</summary>
    public event Action<long>? GroupChanged;

    /// <summary>Изменились действия «печатает» в чате.</summary>
    public event Action<long>? ActionsChanged;

    public TdApi.Chat? GetChat(long chatId) => _chats.GetValueOrDefault(chatId);

    public TdApi.User? GetUser(long userId) => _users.GetValueOrDefault(userId);

    public TdApi.BasicGroup? GetBasicGroup(long id) => _basicGroups.GetValueOrDefault(id);

    public TdApi.Supergroup? GetSupergroup(long id) => _supergroups.GetValueOrDefault(id);

    public IEnumerable<TdApi.Chat> Chats => _chats.Values;

    /// <summary>Чаты списка в порядке TDLib (закреплённые сверху).</summary>
    public IReadOnlyList<long> GetOrderedChats(ChatListKind list)
    {
        if (_sorted.TryGetValue(list, out var cached))
            return cached;

        var sorted = _chats.Values
            .Select(c => (c.Id, Order: GetPosition(c, list)?.Order ?? 0))
            .Where(x => x.Order != 0)
            .OrderByDescending(x => x.Order)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .ToArray();

        _sorted[list] = sorted;
        return sorted;
    }

    public TdApi.ChatPosition? GetPosition(long chatId, ChatListKind list) =>
        _chats.TryGetValue(chatId, out var chat) ? GetPosition(chat, list) : null;

    public ChatCategory GetCategory(long chatId) => _chats.TryGetValue(chatId, out var chat) ? GetCategory(chat) : ChatCategory.Private;

    public ChatCategory GetCategory(TdApi.Chat chat) => chat.Type switch
    {
        TdApi.ChatType.ChatTypePrivate p when p.UserId == MyUserId && MyUserId != 0 => ChatCategory.Saved,
        TdApi.ChatType.ChatTypePrivate p when _users.GetValueOrDefault(p.UserId)?.Type is TdApi.UserType.UserTypeBot => ChatCategory.Bot,
        TdApi.ChatType.ChatTypeSupergroup { IsChannel: true } => ChatCategory.Channel,
        TdApi.ChatType.ChatTypeSupergroup or TdApi.ChatType.ChatTypeBasicGroup => ChatCategory.Group,
        _ => ChatCategory.Private,
    };

    /// <summary>Сколько участников группы в сети (TDLib присылает только для открытых чатов), 0 — неизвестно.</summary>
    public int GetOnlineMemberCount(long chatId) => _onlineCounts.GetValueOrDefault(chatId);

    /// <summary>Число участников группы или подписчиков канала, 0 — неизвестно.</summary>
    public int GetMemberCount(long chatId) => GetChat(chatId)?.Type switch
    {
        TdApi.ChatType.ChatTypeBasicGroup b => GetBasicGroup(b.BasicGroupId)?.MemberCount ?? 0,
        TdApi.ChatType.ChatTypeSupergroup s => GetSupergroup(s.SupergroupId)?.MemberCount ?? 0,
        _ => 0,
    };

    /// <summary>Чат группы (базовой или супергруппы) с этим id группы — для пересылки GroupChanged в строки списка.</summary>
    public IEnumerable<long> ChatsOfGroup(long groupId) => _chats.Values
        .Where(c => c.Type is TdApi.ChatType.ChatTypeBasicGroup b && b.BasicGroupId == groupId
                    || c.Type is TdApi.ChatType.ChatTypeSupergroup s && s.SupergroupId == groupId)
        .Select(c => c.Id);

    /// <summary>Собеседник личного или секретного чата.</summary>
    public long? GetPrivateUserId(long chatId) => GetChat(chatId)?.Type switch
    {
        TdApi.ChatType.ChatTypePrivate p => p.UserId,
        TdApi.ChatType.ChatTypeSecret s => s.UserId,
        _ => null,
    };

    /// <summary>Уведомления выключены: своя настройка чата или общая для его типа.</summary>
    public bool IsMuted(long chatId)
    {
        if (!_chats.TryGetValue(chatId, out var chat) || chat.NotificationSettings is not { } settings)
            return false;

        if (!settings.UseDefaultMuteFor)
            return settings.MuteFor > 0;

        var scope = chat.Type switch
        {
            TdApi.ChatType.ChatTypeSupergroup { IsChannel: true } => typeof(TdApi.NotificationSettingsScope.NotificationSettingsScopeChannelChats),
            TdApi.ChatType.ChatTypeSupergroup or TdApi.ChatType.ChatTypeBasicGroup => typeof(TdApi.NotificationSettingsScope.NotificationSettingsScopeGroupChats),
            _ => typeof(TdApi.NotificationSettingsScope.NotificationSettingsScopePrivateChats),
        };
        return _scopeMuteFor.GetValueOrDefault(scope) > 0;
    }

    /// <summary>
    /// Можно ли писать в чат: в канале — владельцу и админам с правом публикации, в группе — по своим
    /// ограничениям или общим правам участников, в личке — всегда.
    /// </summary>
    public bool CanSendMessages(long chatId)
    {
        if (!_chats.TryGetValue(chatId, out var chat))
            return true;

        return chat.Type switch
        {
            TdApi.ChatType.ChatTypeSupergroup s => CanSendAs(GetSupergroup(s.SupergroupId)?.Status, chat.Permissions, s.IsChannel),
            TdApi.ChatType.ChatTypeBasicGroup b => CanSendAs(GetBasicGroup(b.BasicGroupId)?.Status, chat.Permissions, isChannel: false),
            _ => true,
        };
    }

    private static bool CanSendAs(TdApi.ChatMemberStatus? status, TdApi.ChatPermissions? defaults, bool isChannel) => status switch
    {
        // Группа ещё не пришла от TDLib: в группе поле не прячем, в канале не показываем.
        null => !isChannel,
        TdApi.ChatMemberStatus.ChatMemberStatusCreator => true,
        TdApi.ChatMemberStatus.ChatMemberStatusAdministrator admin => !isChannel || admin.Rights?.CanPostMessages == true,
        _ when isChannel => false,
        TdApi.ChatMemberStatus.ChatMemberStatusRestricted restricted => restricted.Permissions?.CanSendBasicMessages == true,
        TdApi.ChatMemberStatus.ChatMemberStatusLeft or TdApi.ChatMemberStatus.ChatMemberStatusBanned => false,
        _ => defaults?.CanSendBasicMessages != false,
    };

    /// <summary>Действия, которые ещё идут (старше <see cref="ActionLifetime"/> отбрасываются).</summary>
    public IReadOnlyList<ChatActionInfo> GetActions(long chatId)
    {
        if (!_actions.TryGetValue(chatId, out var list))
            return [];

        var now = _time.GetUtcNow();
        list.RemoveAll(a => now - a.At > ActionLifetime);
        return list.ToArray();
    }

    /// <summary>Применяет обновление TDLib. Вызывать в <see cref="IMainThread"/>.</summary>
    public void Apply(TdApi.Update update)
    {
        switch (update)
        {
            case TdApi.Update.UpdateOption { Name: "my_id", Value: TdApi.OptionValue.OptionValueInteger id }:
                MyUserId = id.Value;
                break;

            case TdApi.Update.UpdateNewChat u:
                _chats[u.Chat.Id] = u.Chat;
                u.Chat.Positions ??= [];
                InvalidateLists(u.Chat.Positions);
                ChatChanged?.Invoke(u.Chat.Id);
                break;

            case TdApi.Update.UpdateChatTitle u:
                Mutate(u.ChatId, c => c.Title = u.Title);
                break;

            case TdApi.Update.UpdateChatPhoto u:
                Mutate(u.ChatId, c => c.Photo = u.Photo);
                break;

            case TdApi.Update.UpdateChatLastMessage u:
                Mutate(u.ChatId, c =>
                {
                    c.LastMessage = u.LastMessage;
                    SetPositions(c, u.Positions);
                });
                break;

            case TdApi.Update.UpdateChatPosition u:
                Mutate(u.ChatId, c => SetPosition(c, u.Position));
                break;

            case TdApi.Update.UpdateChatAddedToList or TdApi.Update.UpdateChatRemovedFromList:
                // Позиции приходят отдельным UpdateChatPosition; здесь только пересортировка.
                var list = ChatListKinds.FromTd(update is TdApi.Update.UpdateChatAddedToList a ? a.ChatList : ((TdApi.Update.UpdateChatRemovedFromList)update).ChatList);
                if (list is { } kind)
                    InvalidateList(kind);
                break;

            case TdApi.Update.UpdateChatReadInbox u:
                Mutate(u.ChatId, c =>
                {
                    c.LastReadInboxMessageId = u.LastReadInboxMessageId;
                    c.UnreadCount = u.UnreadCount;
                });
                break;

            case TdApi.Update.UpdateChatReadOutbox u:
                Mutate(u.ChatId, c => c.LastReadOutboxMessageId = u.LastReadOutboxMessageId);
                break;

            case TdApi.Update.UpdateChatUnreadMentionCount u:
                Mutate(u.ChatId, c => c.UnreadMentionCount = u.UnreadMentionCount);
                break;

            case TdApi.Update.UpdateMessageMentionRead u:
                Mutate(u.ChatId, c => c.UnreadMentionCount = u.UnreadMentionCount);
                break;

            case TdApi.Update.UpdateChatIsMarkedAsUnread u:
                Mutate(u.ChatId, c => c.IsMarkedAsUnread = u.IsMarkedAsUnread);
                break;

            case TdApi.Update.UpdateChatPermissions u:
                Mutate(u.ChatId, c => c.Permissions = u.Permissions);
                break;

            case TdApi.Update.UpdateChatNotificationSettings u:
                Mutate(u.ChatId, c => c.NotificationSettings = u.NotificationSettings);
                break;

            case TdApi.Update.UpdateScopeNotificationSettings u:
                _scopeMuteFor[u.Scope.GetType()] = u.NotificationSettings.MuteFor;
                foreach (var id in _chats.Keys.ToArray())
                    ChatChanged?.Invoke(id);
                break;

            case TdApi.Update.UpdateChatDraftMessage u:
                Mutate(u.ChatId, c =>
                {
                    c.DraftMessage = u.DraftMessage;
                    SetPositions(c, u.Positions);
                });
                break;

            case TdApi.Update.UpdateChatAction u:
                ApplyAction(u);
                break;

            case TdApi.Update.UpdateChatOnlineMemberCount u:
                _onlineCounts[u.ChatId] = u.OnlineMemberCount;
                if (_chats.ContainsKey(u.ChatId))
                    ChatChanged?.Invoke(u.ChatId);
                break;

            case TdApi.Update.UpdateUser u:
                _users[u.User.Id] = u.User;
                UserChanged?.Invoke(u.User.Id);
                NotifyPrivateChatsOf(u.User.Id);
                break;

            case TdApi.Update.UpdateUserStatus u when _users.TryGetValue(u.UserId, out var user):
                user.Status = u.Status;
                UserChanged?.Invoke(u.UserId);
                break;

            case TdApi.Update.UpdateBasicGroup u:
                _basicGroups[u.BasicGroup.Id] = u.BasicGroup;
                GroupChanged?.Invoke(u.BasicGroup.Id);
                break;

            case TdApi.Update.UpdateSupergroup u:
                _supergroups[u.Supergroup.Id] = u.Supergroup;
                GroupChanged?.Invoke(u.Supergroup.Id);
                break;
        }
    }

    private static TdApi.ChatPosition? GetPosition(TdApi.Chat chat, ChatListKind list) =>
        chat.Positions?.FirstOrDefault(p => ChatListKinds.FromTd(p.List) == list);

    private void Mutate(long chatId, Action<TdApi.Chat> change)
    {
        if (!_chats.TryGetValue(chatId, out var chat))
            return;

        change(chat);
        ChatChanged?.Invoke(chatId);
    }

    /// <summary>UpdateChatLastMessage/DraftMessage несут полный набор позиций в основных списках.</summary>
    private void SetPositions(TdApi.Chat chat, TdApi.ChatPosition[]? positions)
    {
        if (positions is null)
            return;

        var old = chat.Positions ?? [];
        // Позиции в папках (ChatListFolder) этими обновлениями не передаются — сохраняем их.
        chat.Positions = [.. old.Where(p => ChatListKinds.FromTd(p.List) is null), .. positions];
        InvalidateLists(old);
        InvalidateLists(positions);
    }

    private void SetPosition(TdApi.Chat chat, TdApi.ChatPosition position)
    {
        var kind = ChatListKinds.FromTd(position.List);
        var rest = (chat.Positions ?? []).Where(p => !SameList(p.List, position.List));
        chat.Positions = position.Order == 0 ? [.. rest] : [.. rest, position];
        if (kind is { } k)
            InvalidateList(k);
    }

    private static bool SameList(TdApi.ChatList a, TdApi.ChatList b) => a switch
    {
        TdApi.ChatList.ChatListFolder fa => b is TdApi.ChatList.ChatListFolder fb && fa.ChatFolderId == fb.ChatFolderId,
        _ => a.GetType() == b.GetType(),
    };

    private void InvalidateLists(IEnumerable<TdApi.ChatPosition> positions)
    {
        foreach (var kind in positions.Select(p => ChatListKinds.FromTd(p.List)).OfType<ChatListKind>().Distinct())
            InvalidateList(kind);
    }

    private void InvalidateList(ChatListKind kind)
    {
        _sorted.Remove(kind);
        ListChanged?.Invoke(kind);
    }

    private void ApplyAction(TdApi.Update.UpdateChatAction update)
    {
        if (update.TopicId is not null and not TdApi.MessageTopic.MessageTopicSavedMessages)
            return;

        if (!_actions.TryGetValue(update.ChatId, out var list))
            _actions[update.ChatId] = list = [];

        list.RemoveAll(a => SameSender(a.Sender, update.SenderId));
        if (update.Action is not TdApi.ChatAction.ChatActionCancel)
            list.Add(new ChatActionInfo(update.SenderId, update.Action, _time.GetUtcNow()));

        ActionsChanged?.Invoke(update.ChatId);
    }

    public static bool SameSender(TdApi.MessageSender? a, TdApi.MessageSender? b) => (a, b) switch
    {
        (TdApi.MessageSender.MessageSenderUser ua, TdApi.MessageSender.MessageSenderUser ub) => ua.UserId == ub.UserId,
        (TdApi.MessageSender.MessageSenderChat ca, TdApi.MessageSender.MessageSenderChat cb) => ca.ChatId == cb.ChatId,
        _ => false,
    };

    private void NotifyPrivateChatsOf(long userId)
    {
        foreach (var chat in _chats.Values)
        {
            if (chat.Type is TdApi.ChatType.ChatTypePrivate p && p.UserId == userId
                || chat.Type is TdApi.ChatType.ChatTypeSecret s && s.UserId == userId)
                ChatChanged?.Invoke(chat.Id);
        }
    }
}
