using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeowGram.Core.Data;
using MeowGram.Core.Security;
using MeowGram.Core.Telegram;

namespace MeowGram.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует сервисы ядра: пути к данным, локальную БД и TDLib.
    /// <see cref="TelegramOptions"/> настраивает вызывающий код (из конфигурации).
    /// </summary>
    public static IServiceCollection AddMeowGramCore(this IServiceCollection services, AppPaths? paths = null)
    {
        paths ??= AppPaths.Default();

        services.AddSingleton(paths);
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite($"Data Source={paths.DatabasePath}"));

        services.AddOptions<TelegramOptions>();
        services.AddSingleton(new DatabaseKeyStore(paths.TdlibKeyPath));
        services.AddSingleton<TelegramClientService>();
        services.AddHostedService<TelegramHostedService>();

        return services;
    }
}
