using TdLib;

namespace MeowGram.Core.Messaging;

/// <summary>Выполняет запросы TDLib. Реализует <see cref="Telegram.TelegramSession"/>; в тестах — подделка.</summary>
public interface ITdExecutor
{
    Task<T> ExecuteAsync<T>(TdApi.Function<T> function) where T : TdApi.Object;
}

/// <summary>
/// Поток, в котором живёт состояние чатов: обновления TDLib приходят с фонового потока и переносятся сюда,
/// поэтому <see cref="ChatStore"/> и события сервисов не требуют блокировок. В приложении — UI-поток.
/// </summary>
public interface IMainThread
{
    void Post(Action action);
}

/// <summary>Выполняет сразу в вызывающем потоке (тесты, код без UI).</summary>
public sealed class ImmediateMainThread : IMainThread
{
    public static ImmediateMainThread Instance { get; } = new();

    public void Post(Action action) => action();
}

/// <summary>Список чатов TDLib, который показывает клиент.</summary>
public enum ChatListKind
{
    Main,
    Archive,
}

internal static class ChatListKinds
{
    public static TdApi.ChatList ToTd(this ChatListKind kind) => kind switch
    {
        ChatListKind.Archive => new TdApi.ChatList.ChatListArchive(),
        _ => new TdApi.ChatList.ChatListMain(),
    };

    public static ChatListKind? FromTd(TdApi.ChatList? list) => list switch
    {
        TdApi.ChatList.ChatListMain => ChatListKind.Main,
        TdApi.ChatList.ChatListArchive => ChatListKind.Archive,
        _ => null,
    };
}
