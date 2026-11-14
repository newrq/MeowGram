using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MeowGram.Core.Messaging;
using MeowGram.Services;
using TdLib;

namespace MeowGram.ViewModels.Messenger;

/// <summary>Статус последнего своего сообщения в строке списка.</summary>
public enum OutgoingState
{
    None,
    Sending,
    Sent,
    Read,
    Failed,
}

/// <summary>
/// Чат в строке списка (и в шапке окна, и в результатах поиска): одна модель на чат, обновляется из <see cref="ChatStore"/>.
/// Хранит и состояние сплита: номер окна, где чат открыт, активно ли окно, тащат ли сейчас этот чат.
/// </summary>
public sealed partial class ChatRowViewModel : ObservableObject
{
    private static readonly Color[] AvatarColors =
        [Color.Parse("#5E5E5E"), Color.Parse("#3B3B3B"), Color.Parse("#6B6B6B"), Color.Parse("#4A4A4A"), Color.Parse("#565656")];

    private readonly MessagingContext _context;
    private int _photoFileId;

    public ChatRowViewModel(MessagingContext context, long chatId)
    {
        _context = context;
        ChatId = chatId;
        AvatarBackground = new SolidColorBrush(AvatarColors[(int)(Math.Abs(chatId) % AvatarColors.Length)]);
        Refresh();
    }

    public long ChatId { get; }

    public IBrush AvatarBackground { get; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Initials { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial Bitmap? Photo { get; private set; }

    [ObservableProperty]
    public partial ChatCategory Category { get; private set; }

    [ObservableProperty]
    public partial bool IsOnline { get; private set; }

    [ObservableProperty]
    public partial bool IsMuted { get; private set; }

    [ObservableProperty]
    public partial bool IsPinned { get; private set; }

    [ObservableProperty]
    public partial string Time { get; private set; } = string.Empty;

    /// <summary>Префикс превью: «Кирилл:», «Вы:», «Черновик:».</summary>
    [ObservableProperty]
    public partial string PreviewPrefix { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PreviewText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial PreviewIcon PreviewIcon { get; private set; }

    /// <summary>Префикс — «Черновик:» (цвет ошибки по макету).</summary>
    [ObservableProperty]
    public partial bool IsDraft { get; private set; }

    /// <summary>Вместо превью — «Лена печатает…» цветом акцента.</summary>
    [ObservableProperty]
    public partial bool IsTyping { get; private set; }

    [ObservableProperty]
    public partial string TypingText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int UnreadCount { get; private set; }

    [ObservableProperty]
    public partial bool HasMention { get; private set; }

    [ObservableProperty]
    public partial OutgoingState Outgoing { get; private set; }

    /// <summary>Подпись в шапке окна: статус собеседника или число участников.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = string.Empty;

    /// <summary>Номер окна сплита, где открыт чат (1–4), 0 — не открыт.</summary>
    [ObservableProperty]
    public partial int OpenSlot { get; set; }

    [ObservableProperty]
    public partial bool IsActiveSlot { get; set; }

    /// <summary>Строку сейчас тащат: в списке она полупрозрачная.</summary>
    [ObservableProperty]
    public partial bool IsDragged { get; set; }

    public bool IsSaved => Category == ChatCategory.Saved;

    public bool IsChannel => Category == ChatCategory.Channel;

    public bool IsGroupLike => Category is ChatCategory.Group or ChatCategory.Channel;

    public bool IsOpen => OpenSlot > 0;

    public bool HasUnread => UnreadCount > 0;

    public bool HasPreviewPrefix => PreviewPrefix.Length > 0;

    public bool HasPhoto => Photo is not null;

    public bool ShowOutgoing => Outgoing is OutgoingState.Sent or OutgoingState.Read && !IsTyping;

    public bool ShowRead => Outgoing == OutgoingState.Read && !IsTyping;

    public bool ShowSent => Outgoing == OutgoingState.Sent && !IsTyping;

    public bool ShowUnreadAccent => HasUnread && !IsMuted;

    public bool ShowUnreadMuted => HasUnread && IsMuted;

    public bool ShowVoiceIcon => PreviewIcon == PreviewIcon.Voice && !IsTyping;

    public bool ShowPlainPreview => !IsTyping;

    public bool ShowPin => IsPinned && !HasUnread && !HasMention;

    /// <summary>Онлайн-точку не показываем для «Избранного» и групп.</summary>
    public bool ShowOnline => IsOnline && Category is ChatCategory.Private;

    public string UnreadText => UnreadCount > 999 ? "999+" : UnreadCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Перечитать всё из хранилища (вызывает список на ChatChanged/UserChanged).</summary>
    public void Refresh(ChatListKind list = ChatListKind.Main)
    {
        var store = _context.Store;
        if (store.GetChat(ChatId) is not { } chat)
            return;

        var now = DateTimeOffset.Now;
        Category = store.GetCategory(chat);
        Title = Category == ChatCategory.Saved ? Localization.Loc.Instance["Chat_Saved"] : chat.Title;
        Initials = ChatText.Initials(chat.Title);
        IsMuted = store.IsMuted(ChatId);
        IsPinned = store.GetPosition(ChatId, list)?.IsPinned ?? false;
        UnreadCount = chat.UnreadCount > 0 ? chat.UnreadCount : chat.IsMarkedAsUnread ? 1 : 0;
        HasMention = chat.UnreadMentionCount > 0;

        var userId = store.GetPrivateUserId(ChatId);
        IsOnline = userId is { } uid && ChatText.IsOnline(store.GetUser(uid));
        Subtitle = ChatText.ChatSubtitle(store, ChatId, now);

        RefreshPreview(store, chat, now);
        RefreshTyping();
        LoadPhoto(chat.Photo?.Small);
        RaiseDerived();
    }

    /// <summary>Только «печатает» (дёргается чаще остального).</summary>
    public void RefreshTyping()
    {
        var store = _context.Store;
        var actions = store.GetActions(ChatId);
        IsTyping = actions.Count > 0;
        TypingText = actions.Count == 0 ? string.Empty : TypingDescription(store, actions);
        OnPropertyChanged(nameof(ShowOutgoing));
        RaiseTypingDerived();
    }

    private string TypingDescription(ChatStore store, IReadOnlyList<ChatActionInfo> actions)
    {
        var L = Localization.Loc.Instance;
        var first = actions[0];
        var verb = first.Action switch
        {
            TdApi.ChatAction.ChatActionRecordingVoiceNote => L["Typing_Voice"],
            TdApi.ChatAction.ChatActionUploadingPhoto => L["Typing_Photo"],
            TdApi.ChatAction.ChatActionUploadingDocument => L["Typing_File"],
            TdApi.ChatAction.ChatActionChoosingSticker => L["Typing_Sticker"],
            TdApi.ChatAction.ChatActionRecordingVideoNote or TdApi.ChatAction.ChatActionRecordingVideo => L["Typing_Video"],
            _ => L["Typing_Text"],
        };

        if (!IsGroupLike)
            return verb + "…";
        if (actions.Count > 1)
            return L.Format("Typing_Many", ChatText.Count(actions.Count, "Plural_Person"));
        return $"{ChatText.FirstName(store, first.Sender)} {verb}…";
    }

    private void RefreshPreview(ChatStore store, TdApi.Chat chat, DateTimeOffset now)
    {
        var L = Localization.Loc.Instance;
        var last = chat.LastMessage;
        var draft = chat.DraftMessage?.Content is TdApi.DraftMessageContent.DraftMessageContentText { Text.Text: { Length: > 0 } draftText }
            ? draftText
            : null;

        var date = draft is not null && chat.DraftMessage!.Date > (last?.Date ?? 0) ? chat.DraftMessage.Date : last?.Date ?? 0;
        Time = date > 0 ? ChatText.ListTime(ChatText.FromUnix(date), now) : string.Empty;

        if (draft is not null)
        {
            IsDraft = true;
            PreviewPrefix = L["Chat_DraftPrefix"];
            PreviewText = ChatText.OneLine(draft);
            PreviewIcon = PreviewIcon.None;
            Outgoing = OutgoingState.None;
            return;
        }

        IsDraft = false;
        if (last is null)
        {
            PreviewPrefix = string.Empty;
            PreviewText = string.Empty;
            PreviewIcon = PreviewIcon.None;
            Outgoing = OutgoingState.None;
            return;
        }

        var preview = ChatText.Preview(store, last);
        var service = ChatText.IsService(last.Content);
        PreviewText = preview.Text;
        PreviewIcon = preview.Icon;
        PreviewPrefix = service || Category is ChatCategory.Saved or ChatCategory.Channel
            ? string.Empty
            : last.IsOutgoing
                ? L["Chat_YouPrefix"]
                : Category == ChatCategory.Group ? ChatText.FirstName(store, last.SenderId) + ":" : string.Empty;

        Outgoing = !last.IsOutgoing || Category == ChatCategory.Saved
            ? OutgoingState.None
            : last.SendingState switch
            {
                TdApi.MessageSendingState.MessageSendingStatePending => OutgoingState.Sending,
                TdApi.MessageSendingState.MessageSendingStateFailed => OutgoingState.Failed,
                _ => last.Id <= chat.LastReadOutboxMessageId ? OutgoingState.Read : OutgoingState.Sent,
            };
    }

    private void LoadPhoto(TdApi.File? small)
    {
        if (small is null)
        {
            _photoFileId = 0;
            Photo = null;
            return;
        }

        if (small.Id == _photoFileId && Photo is not null)
            return;

        _photoFileId = small.Id;
        var path = _context.Files.TryGetLocalPath(small);
        if (ImageCache.TryGet(path, 120) is { } ready)
        {
            Photo = ready;
            return;
        }

        var fileId = small.Id;
        ImageCache.LoadInto(_context.Files, small, FileService.PriorityAvatar, 120, bitmap =>
        {
            if (_photoFileId == fileId)
                Photo = bitmap;
        });
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsSaved));
        OnPropertyChanged(nameof(IsChannel));
        OnPropertyChanged(nameof(IsGroupLike));
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(UnreadText));
        OnPropertyChanged(nameof(HasPreviewPrefix));
        OnPropertyChanged(nameof(ShowOutgoing));
        OnPropertyChanged(nameof(ShowPin));
        OnPropertyChanged(nameof(ShowOnline));
        RaiseTypingDerived();
    }

    private void RaiseTypingDerived()
    {
        OnPropertyChanged(nameof(ShowRead));
        OnPropertyChanged(nameof(ShowSent));
        OnPropertyChanged(nameof(ShowUnreadAccent));
        OnPropertyChanged(nameof(ShowUnreadMuted));
        OnPropertyChanged(nameof(ShowVoiceIcon));
        OnPropertyChanged(nameof(ShowPlainPreview));
    }

    partial void OnPhotoChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPhoto));

    partial void OnOpenSlotChanged(int value) => OnPropertyChanged(nameof(IsOpen));
}
