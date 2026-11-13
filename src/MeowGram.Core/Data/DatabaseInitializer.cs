using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace MeowGram.Core.Data;

/// <summary>Применяет миграции нашей БД при старте. Регистрируется первым hosted-сервисом.</summary>
internal sealed class DatabaseInitializer(IDbContextFactory<AppDbContext> dbFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
