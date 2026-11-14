using TdLib;

namespace MeowGram.Core.Messaging;

/// <summary>Вкладка поиска: что искать среди сообщений.</summary>
public enum MessageSearchKind
{
    All,
    Media,
    Links,
    Files,
    Voice,
}

/// <summary>Страница найденных сообщений; <see cref="NextOffset"/> пустой — больше нет.</summary>
public sealed record MessageSearchPage(IReadOnlyList<TdApi.Message> Messages, int TotalCount, string NextOffset);

/// <summary>Страница сообщений одного чата; <see cref="NextFromMessageId"/> 0 — больше нет.</summary>
public sealed record ChatMessagePage(IReadOnlyList<TdApi.Message> Messages, int TotalCount, long NextFromMessageId);

/// <summary>Поиск чатов (свои и глобальные) и сообщений (везде или в одном чате).</summary>
public sealed class SearchService(ITdExecutor td)
{
    /// <summary>Свои чаты и контакты: локальный поиск TDLib, затем серверный, без повторов.</summary>
    public async Task<IReadOnlyList<long>> SearchChatsAsync(string query, int limit = 30)
    {
        var local = await td.ExecuteAsync(new TdApi.SearchChats { Query = query, Limit = limit }).ConfigureAwait(false);
        var ids = new List<long>(local.ChatIds ?? []);
        if (string.IsNullOrWhiteSpace(query))
            return ids;

        try
        {
            var server = await td.ExecuteAsync(new TdApi.SearchChatsOnServer { Query = query, Limit = limit }).ConfigureAwait(false);
            ids.AddRange((server.ChatIds ?? []).Where(id => !ids.Contains(id)));
        }
        catch (TdException)
        {
            // Нет сети — хватит локальных результатов.
        }

        return ids;
    }

    /// <summary>Публичные чаты и каналы по имени (глобальный поиск).</summary>
    public async Task<IReadOnlyList<long>> SearchPublicChatsAsync(string query)
    {
        if (query.Trim().Length < 2)
            return [];

        try
        {
            var found = await td.ExecuteAsync(new TdApi.SearchPublicChats { Query = query }).ConfigureAwait(false);
            return found.ChatIds ?? [];
        }
        catch (TdException)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<long>> GetContactChatIdsAsync()
    {
        var users = await td.ExecuteAsync(new TdApi.GetContacts()).ConfigureAwait(false);
        var ids = new List<long>();
        foreach (var userId in users.UserIds ?? [])
        {
            try
            {
                var chat = await td.ExecuteAsync(new TdApi.CreatePrivateChat { UserId = userId, Force = false }).ConfigureAwait(false);
                ids.Add(chat.Id);
            }
            catch (TdException)
            {
                // Пользователь недоступен — пропускаем.
            }
        }

        return ids;
    }

    /// <summary>Сообщения во всех чатах.</summary>
    public async Task<MessageSearchPage> SearchMessagesAsync(string query, MessageSearchKind kind, string offset = "", int limit = 30)
    {
        var found = await td.ExecuteAsync(new TdApi.SearchMessages
        {
            ChatList = null,
            Query = query,
            Offset = offset,
            Limit = limit,
            Filter = ToFilter(kind),
        }).ConfigureAwait(false);

        return new MessageSearchPage(found.Messages ?? [], found.TotalCount, found.NextOffset ?? string.Empty);
    }

    /// <summary>Сообщения одного чата: поиск в чате и вкладки «Медиа / Файлы / Ссылки» инфо-панели.</summary>
    public async Task<ChatMessagePage> SearchChatMessagesAsync(long chatId, string query, MessageSearchKind kind, long fromMessageId = 0, int limit = 30)
    {
        var found = await td.ExecuteAsync(new TdApi.SearchChatMessages
        {
            ChatId = chatId,
            Query = query,
            FromMessageId = fromMessageId,
            Offset = 0,
            Limit = limit,
            Filter = ToFilter(kind),
        }).ConfigureAwait(false);

        return new ChatMessagePage(found.Messages ?? [], found.TotalCount, found.NextFromMessageId);
    }

    public static TdApi.SearchMessagesFilter ToFilter(MessageSearchKind kind) => kind switch
    {
        MessageSearchKind.Media => new TdApi.SearchMessagesFilter.SearchMessagesFilterPhotoAndVideo(),
        MessageSearchKind.Links => new TdApi.SearchMessagesFilter.SearchMessagesFilterUrl(),
        MessageSearchKind.Files => new TdApi.SearchMessagesFilter.SearchMessagesFilterDocument(),
        MessageSearchKind.Voice => new TdApi.SearchMessagesFilter.SearchMessagesFilterVoiceNote(),
        _ => new TdApi.SearchMessagesFilter.SearchMessagesFilterEmpty(),
    };
}
