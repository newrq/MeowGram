using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RustTalkApp.Core.Data;
using RustTalkApp.Core.Security;
using RustTalkApp.Core.Telegram;

namespace RustTalkApp.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует сервисы ядра: пути к данным, локальную БД и TDLib.
    /// <see cref="TelegramOptions"/> настраивает вызывающий код (из конфигурации).
    /// </summary>
    public static IServiceCollection AddRustTalkCore(this IServiceCollection services, AppPaths? paths = null)
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
