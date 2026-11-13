using Microsoft.Extensions.Hosting;

namespace MeowGram.Core.Telegram;

/// <summary>Запускает TDLib вместе с хостом приложения и закрывает при остановке.</summary>
internal sealed class TelegramHostedService(TelegramClientService telegram) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => telegram.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        telegram.Dispose();
        return Task.CompletedTask;
    }
}
