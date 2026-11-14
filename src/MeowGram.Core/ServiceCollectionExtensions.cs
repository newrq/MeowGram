using MeowGram.Core.Accounts;
using MeowGram.Core.Data;
using MeowGram.Core.Messaging;
using MeowGram.Core.Proxies;
using MeowGram.Core.Security;
using MeowGram.Core.Settings;
using MeowGram.Core.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeowGram.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует сервисы ядра: пути, нашу БД (с миграциями при старте), настройки, аккаунты TDLib и прокси.
    /// <see cref="TelegramOptions"/> настраивает вызывающий код (из конфигурации).
    /// </summary>
    public static IServiceCollection AddMeowGramCore(this IServiceCollection services, AppPaths? paths = null)
    {
        paths ??= AppPaths.Default();

        services.AddSingleton(paths);
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite($"Data Source={paths.DatabasePath}"));

        services.AddOptions<TelegramOptions>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<AccountManager>();
        services.AddSingleton<ProxyService>();

        // Поток состояния чатов: приложение подменяет на UI-поток, без UI — выполнение на месте.
        services.TryAddSingleton<IMainThread>(ImmediateMainThread.Instance);
        services.AddSingleton<MessagingHub>();

        // Порядок важен: сначала миграции БД, затем запуск аккаунтов.
        services.AddHostedService<DatabaseInitializer>();
        services.AddHostedService<AccountManagerHostedService>();

        return services;
    }
}
