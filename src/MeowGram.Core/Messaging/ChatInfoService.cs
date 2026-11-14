using TdLib;

namespace MeowGram.Core.Messaging;

/// <summary>Роль участника группы для подписи в списке.</summary>
public enum MemberRole
{
    Member,
    Admin,
    Owner,
}

public sealed record GroupMember(long UserId, MemberRole Role);

/// <summary>Сведения для инфо-панели группы или канала.</summary>
public sealed record GroupInfo(string Description, int MemberCount, IReadOnlyList<GroupMember> Members, string? InviteLink);

/// <summary>Сведения для инфо-панели личного чата.</summary>
public sealed record UserProfile(string Bio, string? Username);

/// <summary>Профили, группы, участники, уведомления чата.</summary>
public sealed class ChatInfoService(ITdExecutor td, ChatStore store)
{
    /// <summary>«Навсегда» для TDLib — любое значение больше года.</summary>
    private const int MuteForever = int.MaxValue;

    public async Task<UserProfile> GetUserProfileAsync(long userId)
    {
        var full = await td.ExecuteAsync(new TdApi.GetUserFullInfo { UserId = userId }).ConfigureAwait(false);
        var user = store.GetUser(userId);
        var username = user?.Usernames?.ActiveUsernames is { Length: > 0 } names ? names[0] : null;
        return new UserProfile(full.Bio?.Text ?? full.BotInfo?.ShortDescription ?? string.Empty, username);
    }

    public async Task<GroupInfo?> GetGroupInfoAsync(long chatId, int memberLimit = 50)
    {
        switch (store.GetChat(chatId)?.Type)
        {
            case TdApi.ChatType.ChatTypeBasicGroup basic:
            {
                var full = await td.ExecuteAsync(new TdApi.GetBasicGroupFullInfo { BasicGroupId = basic.BasicGroupId }).ConfigureAwait(false);
                var members = (full.Members ?? []).Select(ToMember).OfType<GroupMember>().ToArray();
                return new GroupInfo(full.Description ?? string.Empty, members.Length, Sort(members), full.InviteLink?.InviteLink);
            }

            case TdApi.ChatType.ChatTypeSupergroup super:
            {
                var full = await td.ExecuteAsync(new TdApi.GetSupergroupFullInfo { SupergroupId = super.SupergroupId }).ConfigureAwait(false);
                IReadOnlyList<GroupMember> members = [];
                if (full.CanGetMembers && !super.IsChannel)
                {
                    try
                    {
                        var page = await td.ExecuteAsync(new TdApi.GetSupergroupMembers
                        {
                            SupergroupId = super.SupergroupId,
                            Filter = new TdApi.SupergroupMembersFilter.SupergroupMembersFilterRecent(),
                            Offset = 0,
                            Limit = memberLimit,
                        }).ConfigureAwait(false);
                        members = Sort((page.Members ?? []).Select(ToMember).OfType<GroupMember>().ToArray());
                    }
                    catch (TdException)
                    {
                        // Список участников скрыт — покажем только число.
                    }
                }

                var count = full.MemberCount > 0 ? full.MemberCount : store.GetSupergroup(super.SupergroupId)?.MemberCount ?? 0;
                var link = full.InviteLink?.InviteLink;
                if (link is null && store.GetSupergroup(super.SupergroupId)?.Usernames?.ActiveUsernames is { Length: > 0 } names)
                    link = "https://t.me/" + names[0];
                return new GroupInfo(full.Description ?? string.Empty, count, members, link);
            }

            default:
                return null;
        }
    }

    /// <summary>Включает или выключает уведомления чата, остальные настройки остаются как были.</summary>
    public Task SetMutedAsync(long chatId, bool muted)
    {
        var current = store.GetChat(chatId)?.NotificationSettings ?? new TdApi.ChatNotificationSettings
        {
            UseDefaultSound = true,
            UseDefaultShowPreview = true,
            UseDefaultMuteStories = true,
            UseDefaultStorySound = true,
            UseDefaultShowStoryPoster = true,
            UseDefaultDisablePinnedMessageNotifications = true,
            UseDefaultDisableMentionNotifications = true,
        };

        return td.ExecuteAsync(new TdApi.SetChatNotificationSettings
        {
            ChatId = chatId,
            NotificationSettings = new TdApi.ChatNotificationSettings
            {
                UseDefaultMuteFor = false,
                MuteFor = muted ? MuteForever : 0,
                UseDefaultSound = current.UseDefaultSound,
                SoundId = current.SoundId,
                UseDefaultShowPreview = current.UseDefaultShowPreview,
                ShowPreview = current.ShowPreview,
                UseDefaultMuteStories = current.UseDefaultMuteStories,
                MuteStories = current.MuteStories,
                UseDefaultStorySound = current.UseDefaultStorySound,
                StorySoundId = current.StorySoundId,
                UseDefaultShowStoryPoster = current.UseDefaultShowStoryPoster,
                ShowStoryPoster = current.ShowStoryPoster,
                UseDefaultDisablePinnedMessageNotifications = current.UseDefaultDisablePinnedMessageNotifications,
                DisablePinnedMessageNotifications = current.DisablePinnedMessageNotifications,
                UseDefaultDisableMentionNotifications = current.UseDefaultDisableMentionNotifications,
                DisableMentionNotifications = current.DisableMentionNotifications,
            },
        });
    }

    private static GroupMember? ToMember(TdApi.ChatMember member)
    {
        if (member.MemberId is not TdApi.MessageSender.MessageSenderUser user)
            return null;

        var role = member.Status switch
        {
            TdApi.ChatMemberStatus.ChatMemberStatusCreator => MemberRole.Owner,
            TdApi.ChatMemberStatus.ChatMemberStatusAdministrator => MemberRole.Admin,
            TdApi.ChatMemberStatus.ChatMemberStatusLeft or TdApi.ChatMemberStatus.ChatMemberStatusBanned => (MemberRole?)null,
            _ => MemberRole.Member,
        };

        return role is { } r ? new GroupMember(user.UserId, r) : null;
    }

    /// <summary>Владелец, затем админы, затем остальные — как в макете.</summary>
    private static GroupMember[] Sort(IEnumerable<GroupMember> members) =>
        members.OrderByDescending(m => m.Role).ToArray();
}
