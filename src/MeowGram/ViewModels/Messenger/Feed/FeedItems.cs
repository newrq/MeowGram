using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MeowGram.Core.Messaging;
using MeowGram.Localization;
using MeowGram.Services;
using TdLib;

namespace MeowGram.ViewModels.Messenger.Feed;

/// <summary>Элемент ленты: сообщение, плашка дня, «Непрочитанные сообщения» или служебное сообщение.</summary>
public abstract class FeedItemViewModel : ObservableObject;

/// <summary>Плашка дня по центру: «Сегодня», «12 ноября».</summary>
public sealed class DayHeaderViewModel(string text) : FeedItemViewModel
{
    public string Text { get; } = text;
}

/// <summary>Полоса «Непрочитанные сообщения» во всю ширину ленты.</summary>
public sealed class UnreadSeparatorViewModel : FeedItemViewModel;

/// <summary>Служебное сообщение плашкой: «Кирилл Волков закрепил сообщение».</summary>
public sealed partial class ServiceMessageViewModel(long id, string text) : FeedItemViewModel
{
    public long Id { get; } = id;

    [ObservableProperty]
    public partial string Text { get; set; } = text;
}

/// <summary>Аватарка отправителя в группе (у последнего сообщения серии).</summary>
public sealed partial class SenderAvatarViewModel : ObservableObject
{
    private static readonly Color[] Colors =
        [Color.Parse("#5E5E5E"), Color.Parse("#3B3B3B"), Color.Parse("#6B6B6B"), Color.Parse("#4A4A4A"), Color.Parse("#565656")];

    public SenderAvatarViewModel(MessagingContext context, TdApi.MessageSender sender)
    {
        var store = context.Store;
        var (id, name, photo) = sender switch
        {
            TdApi.MessageSender.MessageSenderUser u => (u.UserId, ChatText.UserName(store.GetUser(u.UserId)), store.GetUser(u.UserId)?.ProfilePhoto?.Small),
            TdApi.MessageSender.MessageSenderChat c => (c.ChatId, store.GetChat(c.ChatId)?.Title ?? string.Empty, store.GetChat(c.ChatId)?.Photo?.Small),
            _ => (0L, string.Empty, (TdApi.File?)null),
        };

        Name = name;
        Initials = ChatText.Initials(name);
        Background = new SolidColorBrush(Colors[(int)(Math.Abs(id) % Colors.Length)]);
        if (photo is not null)
            ImageCache.LoadInto(context.Files, photo, FileService.PriorityAvatar, 80, bitmap => Photo = bitmap);
    }

    public string Name { get; }

    public string Initials { get; }

    public IBrush Background { get; }

    [ObservableProperty]
    public partial Bitmap? Photo { get; private set; }
}

/// <summary>Цитата ответа внутри пузыря и над полем ввода.</summary>
public sealed record ReplyPreviewViewModel(long MessageId, string Name, string Text);

/// <summary>Реакция под сообщением: эмодзи, счётчик, выбрана ли мной (тогда цвет акцента).</summary>
public sealed record ReactionViewModel(string Emoji, int Count, bool IsChosen)
{
    public bool IsHeart => Emoji is "❤" or "❤️";
}

/// <summary>Сообщение-пузырь. Место в серии, имя и аватарку проставляет лента при пересборке.</summary>
public sealed partial class MessageViewModel : FeedItemViewModel, IDisposable
{
    private readonly MessagingContext _context;

    public MessageViewModel(MessagingContext context, TdApi.Message message, bool isGroup)
    {
        _context = context;
        IsGroup = isGroup;
        Update(message);
    }

    public TdApi.Message Message { get; private set; } = null!;

    public long Id => Message.Id;

    public long ChatId => Message.ChatId;

    public bool IsOutgoing => Message.IsOutgoing;

    public bool IsIncoming => !Message.IsOutgoing;

    public bool IsGroup { get; }

    public DateTimeOffset Date => ChatText.FromUnix(Message.Date);

    /// <summary>Ключ отправителя для серий: id пользователя или −id чата.</summary>
    public long SenderKey => Message.SenderId switch
    {
        TdApi.MessageSender.MessageSenderUser u => u.UserId,
        TdApi.MessageSender.MessageSenderChat c => -c.ChatId,
        _ => 0,
    };

    [ObservableProperty]
    public partial MessageContentViewModel Content { get; private set; } = null!;

    [ObservableProperty]
    public partial string Time { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial OutgoingState Status { get; private set; }

    [ObservableProperty]
    public partial string ForwardedFrom { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial ReplyPreviewViewModel? Reply { get; private set; }

    public ObservableCollection<ReactionViewModel> Reactions { get; } = [];

    [ObservableProperty]
    public partial bool IsPinned { get; private set; }

    // ── Проставляет лента ──────────────────────────────────────────────

    [ObservableProperty]
    public partial CornerRadius Corners { get; set; } = new(18);

    [ObservableProperty]
    public partial bool ShowSenderName { get; set; }

    [ObservableProperty]
    public partial bool ShowAvatar { get; set; }

    /// <summary>Последнее в серии: отступ 14px после, иначе 3px.</summary>
    [ObservableProperty]
    public partial bool EndsSeries { get; set; } = true;

    /// <summary>Сообщение подсвечено после перехода к нему (поиск, цитата, закреп).</summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    public string SenderName => IsGroup && IsIncoming ? ChatText.SenderName(_context.Store, Message.SenderId) : string.Empty;

    /// <summary>В группах у входящих слева колонка под аватарку (34px + 10).</summary>
    public bool HasAvatarColumn => IsGroup && IsIncoming;

    private SenderAvatarViewModel? _avatar;

    public SenderAvatarViewModel? Avatar => HasAvatarColumn ? _avatar ??= new SenderAvatarViewModel(_context, Message.SenderId) : null;

    public bool IsForwarded => ForwardedFrom.Length > 0;

    public bool HasReply => Reply is not null;

    public bool HasReactions => Reactions.Count > 0;

    public bool IsFailed => Status == OutgoingState.Failed;

    public bool IsSending => Status == OutgoingState.Sending;

    public bool IsSent => Status == OutgoingState.Sent;

    public bool IsRead => Status == OutgoingState.Read;

    /// <summary>Фото/видео: пузырь с отступом 4 и временем поверх картинки.</summary>
    public bool IsMedia => Content is PhotoContentViewModel;

    public bool IsBare => Content.IsBare;

    public bool CanEdit => IsOutgoing && Content is TextContentViewModel { IsPlaceholder: false } && Status is not (OutgoingState.Sending or OutgoingState.Failed);

    public string PlainText => Content.PlainText;

    /// <summary>Применить новое состояние сообщения (после отправки, правки, реакции).</summary>
    public void Update(TdApi.Message message)
    {
        var contentChanged = Message is null || !ReferenceEquals(Message.Content, message.Content);
        Message = message;
        if (contentChanged)
            SetContent(message);

        IsPinned = message.IsPinned;
        ForwardedFrom = ForwardName(message.ForwardInfo);
        RefreshTime();
        RefreshStatus(_context.Store.GetChat(message.ChatId)?.LastReadOutboxMessageId ?? 0);
        RefreshReactions(message.InteractionInfo);
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(IsOutgoing));
        OnPropertyChanged(nameof(IsIncoming));
        OnPropertyChanged(nameof(CanEdit));
    }

    public void SetContent(TdApi.MessageContent content)
    {
        Message.Content = content;
        SetContent(Message);
    }

    public void SetEdited(int editDate)
    {
        Message.EditDate = editDate;
        RefreshTime();
    }

    public void SetPinned(bool pinned)
    {
        Message.IsPinned = pinned;
        IsPinned = pinned;
    }

    public void SetInteraction(TdApi.MessageInteractionInfo? info)
    {
        Message.InteractionInfo = info;
        RefreshReactions(info);
    }

    public void SetReply(ReplyPreviewViewModel? reply) => Reply = reply;

    /// <summary>Галочки: прочитано, если id не больше последнего прочитанного собеседником.</summary>
    public void RefreshStatus(long lastReadOutbox)
    {
        Status = !IsOutgoing
            ? OutgoingState.None
            : Message.SendingState switch
            {
                TdApi.MessageSendingState.MessageSendingStatePending => OutgoingState.Sending,
                TdApi.MessageSendingState.MessageSendingStateFailed => OutgoingState.Failed,
                _ => Id <= lastReadOutbox ? OutgoingState.Read : OutgoingState.Sent,
            };
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsSending));
        OnPropertyChanged(nameof(IsSent));
        OnPropertyChanged(nameof(IsRead));
        OnPropertyChanged(nameof(CanEdit));
    }

    private void SetContent(TdApi.Message message)
    {
        (Content as IDisposable)?.Dispose();
        var content = MessageContentViewModel.Create(_context, message);
        content.Owner = this;
        Content = content;
        OnPropertyChanged(nameof(IsBare));
        OnPropertyChanged(nameof(IsMedia));
        OnPropertyChanged(nameof(PlainText));
    }

    private void RefreshTime()
    {
        // Как в Telegram: у изменённого — «изменено» и время отправки.
        var time = ChatText.MessageTime(Date);
        Time = Message.EditDate > 0 ? Loc.Instance.Format("Message_Edited", time) : time;
    }

    private string ForwardName(TdApi.MessageForwardInfo? info) => info?.Origin switch
    {
        TdApi.MessageOrigin.MessageOriginUser u => ChatText.UserName(_context.Store.GetUser(u.SenderUserId)),
        TdApi.MessageOrigin.MessageOriginHiddenUser h => h.SenderName,
        TdApi.MessageOrigin.MessageOriginChat c => _context.Store.GetChat(c.SenderChatId)?.Title ?? string.Empty,
        TdApi.MessageOrigin.MessageOriginChannel ch => _context.Store.GetChat(ch.ChatId)?.Title ?? string.Empty,
        _ => string.Empty,
    };

    private void RefreshReactions(TdApi.MessageInteractionInfo? info)
    {
        Reactions.Clear();
        foreach (var r in info?.Reactions?.Reactions ?? [])
        {
            if (r.Type is TdApi.ReactionType.ReactionTypeEmoji emoji)
                Reactions.Add(new ReactionViewModel(emoji.Emoji, r.TotalCount, r.IsChosen));
        }

        OnPropertyChanged(nameof(HasReactions));
    }

    partial void OnForwardedFromChanged(string value) => OnPropertyChanged(nameof(IsForwarded));

    partial void OnReplyChanged(ReplyPreviewViewModel? value) => OnPropertyChanged(nameof(HasReply));

    public void Dispose() => (Content as IDisposable)?.Dispose();
}
