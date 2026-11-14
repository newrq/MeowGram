using System.Globalization;
using MeowGram.Core.Messaging;
using MeowGram.Localization;
using TdLib;

namespace MeowGram.Services;

/// <summary>Короткое описание сообщения для строки списка и цитат: кто, что, иконка.</summary>
public enum PreviewIcon
{
    None,
    Voice,
    Photo,
    File,
}

public sealed record MessagePreview(string Text, PreviewIcon Icon = PreviewIcon.None);

/// <summary>Тексты интерфейса чатов из данных TDLib: время, статусы, превью, служебные сообщения, склонения.</summary>
public static class ChatText
{
    private static Loc L => Loc.Instance;

    private static CultureInfo Culture => L.Culture;

    public static DateTimeOffset FromUnix(int seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();

    /// <summary>Инициалы для аватарки: первые буквы двух первых слов (часть до « · », дефис разделяет слова).</summary>
    public static string Initials(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "?";

        var main = title.Split(" · ")[0];
        var words = main.Split([' ', '-', '_', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.FirstOrDefault(char.IsLetterOrDigit))
            .Where(c => c != default)
            .Take(2)
            .Select(c => char.ToUpper(c, Culture))
            .ToArray();

        return words.Length == 0 ? title.Trim()[..1].ToUpper(Culture) : new string(words);
    }

    /// <summary>Время в строке списка: сегодня — 11:03, вчера — «Вчера», на неделе — «Вт», дальше — «12 нояб.».</summary>
    public static string ListTime(DateTimeOffset date, DateTimeOffset now)
    {
        var day = date.Date;
        var today = now.Date;
        if (day == today)
            return date.ToString("HH:mm", Culture);
        if (day == today.AddDays(-1))
            return L["Time_Yesterday"];
        if (day > today.AddDays(-7))
            return Capitalize(Culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek));
        if (day.Year == today.Year)
            return date.ToString(L["Time_ShortDateFormat"], Culture);
        return date.ToString("dd.MM.yy", Culture);
    }

    public static string MessageTime(DateTimeOffset date) => date.ToString("HH:mm", Culture);

    /// <summary>Плашка дня в ленте: «Сегодня», «Вчера», «12 ноября», «12 ноября 2024».</summary>
    public static string DayHeader(DateTimeOffset date, DateTimeOffset now)
    {
        var day = date.Date;
        if (day == now.Date)
            return L["Time_Today"];
        if (day == now.Date.AddDays(-1))
            return L["Time_Yesterday"];
        return date.ToString(day.Year == now.Year ? L["Time_DayMonthFormat"] : L["Time_DayMonthYearFormat"], Culture);
    }

    /// <summary>Подпись под именем собеседника: «в сети», «был(а) в 09:50», «был(а) недавно»...</summary>
    public static string UserStatus(TdApi.User? user, DateTimeOffset now)
    {
        if (user is null)
            return string.Empty;
        if (user.Type is TdApi.UserType.UserTypeBot)
            return L["Status_Bot"];
        if (user.IsSupport)
            return L["Status_Support"];

        return user.Status switch
        {
            TdApi.UserStatus.UserStatusOnline => L["Status_Online"],
            TdApi.UserStatus.UserStatusOffline off => LastSeen(FromUnix(off.WasOnline), now),
            TdApi.UserStatus.UserStatusRecently => L["Status_Recently"],
            TdApi.UserStatus.UserStatusLastWeek => L["Status_LastWeek"],
            TdApi.UserStatus.UserStatusLastMonth => L["Status_LastMonth"],
            _ => L["Status_LongAgo"],
        };
    }

    public static bool IsOnline(TdApi.User? user) =>
        user is { Status: TdApi.UserStatus.UserStatusOnline, Type: not TdApi.UserType.UserTypeBot };

    private static string LastSeen(DateTimeOffset wasOnline, DateTimeOffset now)
    {
        var minutes = (now - wasOnline).TotalMinutes;
        if (minutes < 1)
            return L["Status_JustNow"];
        if (wasOnline.Date == now.Date)
            return L.Format("Status_LastSeenAt", MessageTime(wasOnline));
        if (wasOnline.Date == now.Date.AddDays(-1))
            return L.Format("Status_LastSeenYesterday", MessageTime(wasOnline));
        return L.Format("Status_LastSeenDate", wasOnline.ToString("dd.MM.yy", Culture));
    }

    /// <summary>Подпись под названием чата в шапке и инфо-панели.</summary>
    public static string ChatSubtitle(ChatStore store, long chatId, DateTimeOffset now)
    {
        var category = store.GetCategory(chatId);
        switch (category)
        {
            case ChatCategory.Saved:
                return string.Empty;
            case ChatCategory.Private or ChatCategory.Bot:
                return UserStatus(store.GetPrivateUserId(chatId) is { } uid ? store.GetUser(uid) : null, now);
            case ChatCategory.Channel:
            {
                var count = store.GetMemberCount(chatId);
                return count > 0 ? Count(count, "Plural_Subscriber") : L["Chat_Channel"];
            }

            default:
            {
                var count = store.GetMemberCount(chatId);
                var online = store.GetOnlineMemberCount(chatId);
                var members = count > 0 ? Count(count, "Plural_Member") : L["Chat_Group"];
                return online > 1 ? L.Format("Chat_MembersOnline", members, online) : members;
            }
        }
    }

    /// <summary>«5 участников», «1 участник» — склонение по ключу с формами «одна|несколько|много».</summary>
    public static string Count(int n, string pluralKey)
    {
        var forms = L[pluralKey].Split('|');
        var form = forms.Length < 3 ? forms[^1] : forms[PluralIndex(n)];
        return string.Format(Culture, form, n.ToString("N0", Culture));
    }

    /// <summary>Индекс формы: русские правила (1 / 2–4 / 5–20), для английского — 1 / остальные.</summary>
    public static int PluralIndex(int n)
    {
        if (L.Language != "ru")
            return n == 1 ? 0 : 2;

        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod10 == 1 && mod100 != 11)
            return 0;
        if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14)
            return 1;
        return 2;
    }

    public static string Duration(int seconds) =>
        seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";

    public static string FileSize(long bytes)
    {
        string[] units = [L["Size_B"], L["Size_KB"], L["Size_MB"], L["Size_GB"]];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : value.ToString(value < 10 ? "0.#" : "0", Culture) + " " + units[unit];
    }

    public static string SenderName(ChatStore store, TdApi.MessageSender? sender) => sender switch
    {
        TdApi.MessageSender.MessageSenderUser u => UserName(store.GetUser(u.UserId)),
        TdApi.MessageSender.MessageSenderChat c => store.GetChat(c.ChatId)?.Title ?? string.Empty,
        _ => string.Empty,
    };

    public static string UserName(TdApi.User? user)
    {
        if (user is null)
            return L["Chat_Unknown"];
        if (user.Type is TdApi.UserType.UserTypeDeleted)
            return L["Chat_DeletedAccount"];
        return string.IsNullOrWhiteSpace(user.LastName) ? user.FirstName : $"{user.FirstName} {user.LastName}";
    }

    public static string FirstName(ChatStore store, TdApi.MessageSender? sender) => sender switch
    {
        TdApi.MessageSender.MessageSenderUser u when store.GetUser(u.UserId) is { } user => user.FirstName,
        _ => SenderName(store, sender),
    };

    /// <summary>Однострочное описание содержимого: текст, «Фото», «Голосовое сообщение · 0:41», имя файла...</summary>
    public static MessagePreview Preview(ChatStore store, TdApi.Message message)
    {
        var content = message.Content;
        return content switch
        {
            TdApi.MessageContent.MessageText t => new(OneLine(t.Text?.Text)),
            TdApi.MessageContent.MessagePhoto p => new(WithCaption(L["Content_Photo"], p.Caption), PreviewIcon.Photo),
            TdApi.MessageContent.MessageVideo v => new(WithCaption(L["Content_Video"], v.Caption), PreviewIcon.Photo),
            TdApi.MessageContent.MessageAnimation a => new(WithCaption(L["Content_Gif"], a.Caption)),
            TdApi.MessageContent.MessageVoiceNote v => new(L.Format("Content_Voice", Duration(v.VoiceNote?.Duration ?? 0)), PreviewIcon.Voice),
            TdApi.MessageContent.MessageVideoNote => new(L["Content_VideoNote"]),
            TdApi.MessageContent.MessageDocument d => new(WithCaption(d.Document?.FileName is { Length: > 0 } name ? name : L["Content_File"], d.Caption), PreviewIcon.File),
            TdApi.MessageContent.MessageAudio a => new(AudioTitle(a.Audio), PreviewIcon.File),
            TdApi.MessageContent.MessageSticker s => new(L.Format("Content_Sticker", s.Sticker?.Emoji ?? string.Empty).Trim()),
            TdApi.MessageContent.MessagePoll p => new(L.Format("Content_Poll", OneLine(p.Poll?.Question?.Text))),
            TdApi.MessageContent.MessageContact => new(L["Content_Contact"]),
            TdApi.MessageContent.MessageLocation or TdApi.MessageContent.MessageVenue => new(L["Content_Location"]),
            TdApi.MessageContent.MessageCall => new(L["Content_Call"]),
            TdApi.MessageContent.MessageUnsupported => new(L["Content_Unsupported"]),
            _ when IsService(content) => new(ServiceText(store, message)),
            _ => new(L["Content_Message"]),
        };
    }

    private static string AudioTitle(TdApi.Audio? audio)
    {
        if (audio is null)
            return L["Content_Audio"];
        var title = string.Join(" — ", new[] { audio.Performer, audio.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return title.Length > 0 ? title : audio.FileName ?? L["Content_Audio"];
    }

    private static string WithCaption(string kind, TdApi.FormattedText? caption) =>
        string.IsNullOrWhiteSpace(caption?.Text) ? kind : $"{kind}, {OneLine(caption.Text)}";

    public static string OneLine(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    /// <summary>Служебное сообщение (вступил, закрепил, сменил название...) — плашкой по центру ленты.</summary>
    public static bool IsService(TdApi.MessageContent? content) => content is
        TdApi.MessageContent.MessageChatAddMembers or TdApi.MessageContent.MessageChatJoinByLink
        or TdApi.MessageContent.MessageChatJoinByRequest or TdApi.MessageContent.MessageChatDeleteMember
        or TdApi.MessageContent.MessageChatChangeTitle or TdApi.MessageContent.MessageChatChangePhoto
        or TdApi.MessageContent.MessageChatDeletePhoto or TdApi.MessageContent.MessagePinMessage
        or TdApi.MessageContent.MessageBasicGroupChatCreate or TdApi.MessageContent.MessageSupergroupChatCreate
        or TdApi.MessageContent.MessageContactRegistered or TdApi.MessageContent.MessageScreenshotTaken
        or TdApi.MessageContent.MessageChatUpgradeTo or TdApi.MessageContent.MessageChatUpgradeFrom
        or TdApi.MessageContent.MessageChatSetMessageAutoDeleteTime or TdApi.MessageContent.MessageCustomServiceAction
        or TdApi.MessageContent.MessageChatSetTheme or TdApi.MessageContent.MessageChatSetBackground
        or TdApi.MessageContent.MessageForumTopicCreated or TdApi.MessageContent.MessageGiftedPremium
        or TdApi.MessageContent.MessageGift or TdApi.MessageContent.MessageVideoChatStarted
        or TdApi.MessageContent.MessageVideoChatEnded or TdApi.MessageContent.MessageVideoChatScheduled
        or TdApi.MessageContent.MessageChatBoost or TdApi.MessageContent.MessageBotWriteAccessAllowed;

    public static string ServiceText(ChatStore store, TdApi.Message message)
    {
        var who = message.IsOutgoing ? L["Service_You"] : SenderName(store, message.SenderId);
        return message.Content switch
        {
            TdApi.MessageContent.MessageChatAddMembers add when add.MemberUserIds is [var one] && message.SenderId is TdApi.MessageSender.MessageSenderUser s && s.UserId == one
                => L.Format("Service_Joined", who),
            TdApi.MessageContent.MessageChatAddMembers add
                => L.Format("Service_Added", who, string.Join(", ", (add.MemberUserIds ?? []).Select(id => UserName(store.GetUser(id))))),
            TdApi.MessageContent.MessageChatJoinByLink or TdApi.MessageContent.MessageChatJoinByRequest => L.Format("Service_Joined", who),
            TdApi.MessageContent.MessageChatDeleteMember del when message.SenderId is TdApi.MessageSender.MessageSenderUser s && s.UserId == del.UserId
                => L.Format("Service_Left", who),
            TdApi.MessageContent.MessageChatDeleteMember del => L.Format("Service_Removed", who, UserName(store.GetUser(del.UserId))),
            TdApi.MessageContent.MessageChatChangeTitle t => L.Format("Service_Title", who, t.Title),
            TdApi.MessageContent.MessageChatChangePhoto => L.Format("Service_Photo", who),
            TdApi.MessageContent.MessageChatDeletePhoto => L.Format("Service_PhotoRemoved", who),
            TdApi.MessageContent.MessagePinMessage => L.Format("Service_Pinned", who),
            TdApi.MessageContent.MessageBasicGroupChatCreate c => L.Format("Service_Created", who, c.Title),
            TdApi.MessageContent.MessageSupergroupChatCreate c => L.Format("Service_Created", who, c.Title),
            TdApi.MessageContent.MessageContactRegistered => L.Format("Service_Registered", who),
            TdApi.MessageContent.MessageScreenshotTaken => L.Format("Service_Screenshot", who),
            TdApi.MessageContent.MessageCustomServiceAction a => a.Text,
            TdApi.MessageContent.MessageVideoChatStarted => L["Service_VideoChatStarted"],
            TdApi.MessageContent.MessageVideoChatEnded => L["Service_VideoChatEnded"],
            _ => L["Service_Other"],
        };
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], Culture) + s[1..].TrimEnd('.');
}
