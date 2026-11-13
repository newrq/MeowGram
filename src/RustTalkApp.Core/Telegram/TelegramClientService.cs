using WTelegram;

namespace RustTalkApp.Core.Telegram;

/// <summary>
/// Тонкая обёртка над WTelegram.Client — единая точка доступа к Telegram Client API.
/// </summary>
public sealed class TelegramClientService : IAsyncDisposable
{
    private readonly Client _client;

    public TelegramClientService(Func<string, string?> configProvider)
    {
        _client = new Client(configProvider);
    }

    public Client Client => _client;

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
