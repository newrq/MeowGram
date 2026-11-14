using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeowGram.Core.Messaging;
using MeowGram.Localization;
using MeowGram.Services;
using MeowGram.ViewModels.Messenger.Feed;
using TdLib;

namespace MeowGram.ViewModels.Messenger;

/// <summary>Вкладка вложений инфо-панели.</summary>
public enum AttachmentTab
{
    Media,
    Files,
    Links,
}

/// <summary>Участник группы в инфо-панели.</summary>
public sealed partial class MemberViewModel : ObservableObject
{
    public MemberViewModel(MessagingContext context, GroupMember member)
    {
        var user = context.Store.GetUser(member.UserId);
        UserId = member.UserId;
        Name = ChatText.UserName(user);
        Status = ChatText.UserStatus(user, DateTimeOffset.Now);
        IsOnline = ChatText.IsOnline(user);
        Role = member.Role switch
        {
            MemberRole.Owner => Loc.Instance["Info_Owner"],
            MemberRole.Admin => Loc.Instance["Info_Admin"],
            _ => string.Empty,
        };
        Avatar = new SenderAvatarViewModel(context, new TdApi.MessageSender.MessageSenderUser { UserId = member.UserId });
    }

    public long UserId { get; }

    public string Name { get; }

    public string Status { get; }

    public bool IsOnline { get; }

    public string Role { get; }

    public bool HasRole => Role.Length > 0;

    public SenderAvatarViewModel Avatar { get; }
}

/// <summary>Вложение в списке «Медиа / Файлы / Ссылки».</summary>
public sealed partial class AttachmentViewModel : ObservableObject
{
    public AttachmentViewModel(string badge, string title, string subtitle, Func<Task> open)
    {
        Badge = badge;
        Title = title;
        Subtitle = subtitle;
        OpenCommand = new AsyncRelayCommand(open);
    }

    /// <summary>Плашка слева: расширение файла или «URL» / «IMG».</summary>
    public string Badge { get; }

    public string Title { get; }

    public string Subtitle { get; }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    public IAsyncRelayCommand OpenCommand { get; }
}

/// <summary>
/// Инфо-панель активного окна: профиль собеседника (имя пользователя, о себе, уведомления) или группа
/// (описание, участники), кнопки быстрых действий и вкладки вложений.
/// </summary>
public sealed partial class InfoPanelViewModel : ObservableObject
{
    private readonly MessagingContext _context;
    private readonly Action<long> _jumpTo;
    private readonly Action _searchInChat;
    private int _loadVersion;

    public InfoPanelViewModel(MessagingContext context, ChatRowViewModel row, Action<long> jumpTo, Action searchInChat, Func<string, Task> copy)
    {
        _context = context;
        Row = row;
        _jumpTo = jumpTo;
        _searchInChat = searchInChat;
        CopyLinkCommand = new AsyncRelayCommand(() => InviteLink is { } link ? copy(link) : Task.CompletedTask);
        IsGroup = row.IsGroupLike;
        IsMuted = row.IsMuted;
        _ = LoadAsync();
        _ = LoadAttachmentsAsync();
    }

    public ChatRowViewModel Row { get; }

    public long ChatId => Row.ChatId;

    public bool IsGroup { get; }

    public bool IsPrivate => !IsGroup;

    public string PanelTitle => IsGroup
        ? Loc.Instance[Row.IsChannel ? "Info_AboutChannel" : "Info_AboutGroup"]
        : Loc.Instance["Info_Profile"];

    [ObservableProperty]
    public partial string Username { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Bio { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? InviteLink { get; private set; }

    [ObservableProperty]
    public partial int MemberCount { get; private set; }

    [ObservableProperty]
    public partial bool IsMuted { get; private set; }

    /// <summary>Тумблер «Уведомления» — обратное к «без звука».</summary>
    public bool NotificationsOn
    {
        get => !IsMuted;
        set => _ = SetMutedAsync(!value);
    }

    public ObservableCollection<MemberViewModel> Members { get; } = [];

    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    [ObservableProperty]
    public partial AttachmentTab Tab { get; set; } = AttachmentTab.Files;

    [ObservableProperty]
    public partial bool IsLoadingAttachments { get; private set; }

    public bool HasUsername => Username.Length > 0;

    public bool HasBio => Bio.Length > 0;

    public bool HasDescription => Description.Length > 0;

    public bool HasMembers => Members.Count > 0;

    public bool HasNoAttachments => !IsLoadingAttachments && Attachments.Count == 0;

    public string MembersHeader => Loc.Instance.Format("Info_MembersHeader", MemberCount);

    public string GroupSubtitle => Row.IsChannel
        ? $"{Loc.Instance["Chat_ChannelLower"]} · {ChatText.Count(MemberCount, "Plural_Subscriber")}"
        : $"{Loc.Instance["Chat_GroupLower"]} · {ChatText.Count(MemberCount, "Plural_Member")}";

    public string MuteLabel => Loc.Instance[IsMuted ? "Info_Unmute" : "Info_Mute"];

    public IAsyncRelayCommand CopyLinkCommand { get; }

    [RelayCommand]
    private Task ToggleMuteAsync() => SetMutedAsync(!IsMuted);

    [RelayCommand]
    private void Search() => _searchInChat();

    [RelayCommand]
    private void SetTab(AttachmentTab tab) => Tab = tab;

    partial void OnTabChanged(AttachmentTab value) => _ = LoadAttachmentsAsync();

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(NotificationsOn));
        OnPropertyChanged(nameof(MuteLabel));
    }

    private async Task SetMutedAsync(bool muted)
    {
        IsMuted = muted;
        try
        {
            await _context.Info.SetMutedAsync(ChatId, muted);
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            IsMuted = _context.Store.IsMuted(ChatId);
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            if (IsGroup)
            {
                var info = await _context.Info.GetGroupInfoAsync(ChatId);
                if (info is null)
                    return;
                Description = info.Description;
                InviteLink = info.InviteLink;
                MemberCount = info.MemberCount;
                Members.Clear();
                foreach (var member in info.Members)
                    Members.Add(new MemberViewModel(_context, member));
            }
            else if (_context.Store.GetPrivateUserId(ChatId) is { } userId)
            {
                var profile = await _context.Info.GetUserProfileAsync(userId);
                Username = profile.Username is { } u ? "@" + u : string.Empty;
                Bio = profile.Bio;
            }
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            // Без подробностей остаются имя и статус.
        }
        finally
        {
            OnPropertyChanged(nameof(HasUsername));
            OnPropertyChanged(nameof(HasBio));
            OnPropertyChanged(nameof(HasDescription));
            OnPropertyChanged(nameof(HasMembers));
            OnPropertyChanged(nameof(MembersHeader));
            OnPropertyChanged(nameof(GroupSubtitle));
        }
    }

    private async Task LoadAttachmentsAsync()
    {
        var version = ++_loadVersion;
        IsLoadingAttachments = true;
        Attachments.Clear();
        OnPropertyChanged(nameof(HasNoAttachments));
        try
        {
            var kind = Tab switch
            {
                AttachmentTab.Media => MessageSearchKind.Media,
                AttachmentTab.Links => MessageSearchKind.Links,
                _ => MessageSearchKind.Files,
            };
            var page = await _context.Search.SearchChatMessagesAsync(ChatId, string.Empty, kind, limit: 30);
            if (version != _loadVersion)
                return;

            foreach (var message in page.Messages)
            {
                if (ToAttachment(message) is { } item)
                    Attachments.Add(item);
            }
        }
        catch (Exception ex) when (ex is TdException or InvalidOperationException)
        {
            // Пусто — покажем «нет вложений».
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoadingAttachments = false;
                OnPropertyChanged(nameof(HasNoAttachments));
            }
        }
    }

    private AttachmentViewModel? ToAttachment(TdApi.Message message)
    {
        var date = ChatText.FromUnix(message.Date);
        var when = ChatText.DayHeader(date, DateTimeOffset.Now).ToLower(Loc.Instance.Culture);
        Task Jump()
        {
            _jumpTo(message.Id);
            return Task.CompletedTask;
        }

        switch (message.Content)
        {
            case TdApi.MessageContent.MessageDocument { Document: { } doc }:
            {
                var file = new FileContentViewModel(_context, doc.Document_, doc.FileName, null);
                return new AttachmentViewModel(file.Extension, file.FileName, $"{file.Size} · {when}", () => file.OpenCommand.ExecuteAsync(null));
            }

            case TdApi.MessageContent.MessageAudio { Audio: { } audio }:
            {
                var file = new FileContentViewModel(_context, audio.Audio_, audio.FileName, null);
                return new AttachmentViewModel(file.Extension, file.FileName, $"{file.Size} · {when}", () => file.OpenCommand.ExecuteAsync(null));
            }

            case TdApi.MessageContent.MessagePhoto { Photo: { } photo }:
            {
                var item = new AttachmentViewModel("IMG", Loc.Instance["Content_Photo"], when, Jump);
                var small = photo.Sizes?.OrderBy(s => s.Width).FirstOrDefault(s => s.Width >= 90) ?? photo.Sizes?.LastOrDefault();
                ImageCache.LoadInto(_context.Files, small?.Photo, FileService.PriorityThumbnail, 96, bitmap => item.Thumbnail = bitmap);
                return item;
            }

            case TdApi.MessageContent.MessageVideo:
                return new AttachmentViewModel("VID", Loc.Instance["Content_Video"], when, Jump);

            case TdApi.MessageContent.MessageText text:
            {
                var url = text.LinkPreview?.Url ?? FirstUrl(text.Text);
                var title = text.LinkPreview?.Title is { Length: > 0 } t ? t : url ?? ChatText.OneLine(text.Text?.Text);
                return new AttachmentViewModel("URL", title, url is null ? when : $"{url} · {when}", Jump);
            }

            default:
                return null;
        }
    }

    private static string? FirstUrl(TdApi.FormattedText? text) =>
        TextContentViewModel.Parse(text).FirstOrDefault(s => s.IsLink)?.Url;
}
