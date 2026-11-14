using TdLib;

namespace MeowGram.Core.Messaging;

/// <summary>Событие о сообщениях чата <see cref="ChatId"/>; окна чатов фильтруют по нему.</summary>
public abstract record MessageEvent(long ChatId);

public sealed record MessageAdded(TdApi.Message Message) : MessageEvent(Message.ChatId);

/// <summary>Отправка завершилась (успешно или с ошибкой): временное сообщение <see cref="OldMessageId"/> заменяется.</summary>
public sealed record MessageReplaced(long OldMessageId, TdApi.Message Message) : MessageEvent(Message.ChatId);

public sealed record MessageContentChanged(long ChatId, long MessageId, TdApi.MessageContent Content) : MessageEvent(ChatId);

public sealed record MessageEdited(long ChatId, long MessageId, int EditDate) : MessageEvent(ChatId);

public sealed record MessagesDeleted(long ChatId, IReadOnlyList<long> MessageIds) : MessageEvent(ChatId);

public sealed record MessageInteractionChanged(long ChatId, long MessageId, TdApi.MessageInteractionInfo? Info) : MessageEvent(ChatId);

public sealed record MessagePinnedChanged(long ChatId, long MessageId, bool IsPinned) : MessageEvent(ChatId);

public sealed record MessageContentOpened(long ChatId, long MessageId) : MessageEvent(ChatId);

/// <summary>
/// Сообщения: история, отправка, правка, удаление, закреп, пересылка, реакции, прочтение, черновики.
/// <see cref="Changed"/> приходит в <see cref="IMainThread"/>.
/// </summary>
public sealed class MessageService(ITdExecutor td)
{
    public event Action<MessageEvent>? Changed;

    /// <summary>Переводит обновление TDLib в событие о сообщениях. Вызывать в <see cref="IMainThread"/>.</summary>
    public void Apply(TdApi.Update update)
    {
        MessageEvent? e = update switch
        {
            TdApi.Update.UpdateNewMessage u => new MessageAdded(u.Message),
            TdApi.Update.UpdateMessageSendSucceeded u => new MessageReplaced(u.OldMessageId, u.Message),
            TdApi.Update.UpdateMessageSendFailed u => new MessageReplaced(u.OldMessageId, u.Message),
            TdApi.Update.UpdateMessageContent u => new MessageContentChanged(u.ChatId, u.MessageId, u.NewContent),
            TdApi.Update.UpdateMessageEdited u => new MessageEdited(u.ChatId, u.MessageId, u.EditDate),
            // FromCache — TDLib просто выгрузил сообщения из памяти, удалять их из окна не нужно.
            TdApi.Update.UpdateDeleteMessages { FromCache: false } u => new MessagesDeleted(u.ChatId, u.MessageIds),
            TdApi.Update.UpdateMessageInteractionInfo u => new MessageInteractionChanged(u.ChatId, u.MessageId, u.InteractionInfo),
            TdApi.Update.UpdateMessageIsPinned u => new MessagePinnedChanged(u.ChatId, u.MessageId, u.IsPinned),
            TdApi.Update.UpdateMessageContentOpened u => new MessageContentOpened(u.ChatId, u.MessageId),
            _ => null,
        };

        if (e is not null)
            Changed?.Invoke(e);
    }

    /// <summary>
    /// Страница истории. <paramref name="offset"/> по правилам TDLib: 0 — сообщения старше <paramref name="fromMessageId"/>
    /// (включая его), отрицательный — захватить и более новые. Возвращает по возрастанию id (сверху вниз в ленте).
    /// TDLib может отдать меньше <paramref name="limit"/>; пустой ответ — истории больше нет.
    /// </summary>
    public async Task<IReadOnlyList<TdApi.Message>> GetHistoryAsync(long chatId, long fromMessageId, int offset, int limit)
    {
        var page = await td.ExecuteAsync(new TdApi.GetChatHistory
        {
            ChatId = chatId,
            FromMessageId = fromMessageId,
            Offset = offset,
            Limit = limit,
            OnlyLocal = false,
        }).ConfigureAwait(false);

        return (page.Messages_ ?? []).Where(m => m is not null).OrderBy(m => m.Id).ToArray();
    }

    public Task<TdApi.Message> GetMessageAsync(long chatId, long messageId) =>
        td.ExecuteAsync(new TdApi.GetMessage { ChatId = chatId, MessageId = messageId });

    public async Task<TdApi.Message?> GetPinnedAsync(long chatId)
    {
        try
        {
            return await td.ExecuteAsync(new TdApi.GetChatPinnedMessage { ChatId = chatId }).ConfigureAwait(false);
        }
        catch (TdException ex) when (ex.Error.Code == 404)
        {
            return null;
        }
    }

    public Task<TdApi.Message> SendTextAsync(long chatId, string text, long replyToMessageId = 0) =>
        td.ExecuteAsync(new TdApi.SendMessage
        {
            ChatId = chatId,
            ReplyTo = replyToMessageId == 0 ? null : new TdApi.InputMessageReplyTo.InputMessageReplyToMessage { MessageId = replyToMessageId },
            InputMessageContent = Text(text, clearDraft: true),
        });

    public Task<TdApi.Message> EditTextAsync(long chatId, long messageId, string text) =>
        td.ExecuteAsync(new TdApi.EditMessageText
        {
            ChatId = chatId,
            MessageId = messageId,
            InputMessageContent = Text(text, clearDraft: false),
        });

    public Task DeleteAsync(long chatId, IReadOnlyList<long> messageIds, bool forEveryone) =>
        td.ExecuteAsync(new TdApi.DeleteMessages { ChatId = chatId, MessageIds = [.. messageIds], Revoke = forEveryone });

    public Task PinAsync(long chatId, long messageId) =>
        td.ExecuteAsync(new TdApi.PinChatMessage { ChatId = chatId, MessageId = messageId, DisableNotification = false });

    public Task UnpinAsync(long chatId, long messageId) =>
        td.ExecuteAsync(new TdApi.UnpinChatMessage { ChatId = chatId, MessageId = messageId });

    public Task ForwardAsync(long toChatId, long fromChatId, IReadOnlyList<long> messageIds) =>
        td.ExecuteAsync(new TdApi.ForwardMessages
        {
            ChatId = toChatId,
            FromChatId = fromChatId,
            MessageIds = [.. messageIds],
            SendCopy = false,
            RemoveCaption = false,
        });

    public Task ResendAsync(long chatId, long messageId) =>
        td.ExecuteAsync(new TdApi.ResendMessages { ChatId = chatId, MessageIds = [messageId] });

    public Task SetReactionAsync(long chatId, long messageId, string emoji, bool chosen) => chosen
        ? td.ExecuteAsync(new TdApi.AddMessageReaction
        {
            ChatId = chatId,
            MessageId = messageId,
            ReactionType = new TdApi.ReactionType.ReactionTypeEmoji { Emoji = emoji },
            IsBig = false,
            UpdateRecentReactions = true,
        })
        : td.ExecuteAsync(new TdApi.RemoveMessageReaction
        {
            ChatId = chatId,
            MessageId = messageId,
            ReactionType = new TdApi.ReactionType.ReactionTypeEmoji { Emoji = emoji },
        });

    /// <summary>Чат на экране: TDLib присылает для него больше обновлений (печатает, прочтения).</summary>
    public Task OpenChatAsync(long chatId) => td.ExecuteAsync(new TdApi.OpenChat { ChatId = chatId });

    public Task CloseChatAsync(long chatId) => td.ExecuteAsync(new TdApi.CloseChat { ChatId = chatId });

    /// <summary>Отметить показанные сообщения прочитанными.</summary>
    public Task ViewAsync(long chatId, IReadOnlyList<long> messageIds) =>
        td.ExecuteAsync(new TdApi.ViewMessages
        {
            ChatId = chatId,
            MessageIds = [.. messageIds],
            Source = new TdApi.MessageSource.MessageSourceChatHistory(),
            ForceRead = true,
        });

    /// <summary>Голосовое прослушано, видеосообщение просмотрено.</summary>
    public Task OpenContentAsync(long chatId, long messageId) =>
        td.ExecuteAsync(new TdApi.OpenMessageContent { ChatId = chatId, MessageId = messageId });

    /// <summary>Черновик чата; пустой текст удаляет черновик.</summary>
    public Task SetDraftAsync(long chatId, string text, long replyToMessageId = 0) =>
        td.ExecuteAsync(new TdApi.SetChatDraftMessage
        {
            ChatId = chatId,
            DraftMessage = string.IsNullOrWhiteSpace(text) && replyToMessageId == 0
                ? null
                : new TdApi.DraftMessage
                {
                    ReplyTo = replyToMessageId == 0 ? null : new TdApi.InputMessageReplyTo.InputMessageReplyToMessage { MessageId = replyToMessageId },
                    Content = new TdApi.DraftMessageContent.DraftMessageContentText
                    {
                        Text = new TdApi.FormattedText { Text = text, Entities = [] },
                    },
                },
        });

    private static TdApi.InputMessageContent.InputMessageText Text(string text, bool clearDraft) => new()
    {
        Text = new TdApi.FormattedText { Text = text, Entities = [] },
        ClearDraft = clearDraft,
    };
}
