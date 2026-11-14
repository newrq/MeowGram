using TdLib;

namespace MeowGram.Core.Messaging;

/// <summary>Догрузка списков чатов: TDLib присылает чаты обновлениями (UpdateNewChat/UpdateChatPosition) в ответ на LoadChats.</summary>
public sealed class ChatListService(ITdExecutor td)
{
    private readonly HashSet<ChatListKind> _complete = [];
    private readonly Lock _gate = new();

    public bool IsComplete(ChatListKind list)
    {
        lock (_gate)
            return _complete.Contains(list);
    }

    /// <summary>Догружает ещё <paramref name="limit"/> чатов. false — список загружен целиком.</summary>
    public async Task<bool> LoadMoreAsync(ChatListKind list, int limit = 50)
    {
        if (IsComplete(list))
            return false;

        try
        {
            await td.ExecuteAsync(new TdApi.LoadChats { ChatList = list.ToTd(), Limit = limit }).ConfigureAwait(false);
            return true;
        }
        catch (TdException ex) when (ex.Error.Code == 404)
        {
            // 404 — все чаты списка уже загружены.
            lock (_gate)
                _complete.Add(list);
            return false;
        }
    }
}
