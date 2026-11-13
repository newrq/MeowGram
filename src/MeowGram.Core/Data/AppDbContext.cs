using Microsoft.EntityFrameworkCore;

namespace MeowGram.Core.Data;

/// <summary>
/// Локальный кэш клиента (чаты, сообщения, настройки кастомизации).
/// Сущности и миграции добавляются по мере появления функциональности.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
