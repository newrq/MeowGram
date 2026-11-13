using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RustTalkApp.Core.Data;

namespace RustTalkApp.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Регистрирует сервисы ядра: пути к данным и локальную БД SQLite.</summary>
    public static IServiceCollection AddRustTalkCore(this IServiceCollection services, AppPaths? paths = null)
    {
        paths ??= AppPaths.Default();

        services.AddSingleton(paths);
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite($"Data Source={paths.DatabasePath}"));

        return services;
    }
}
