using Microsoft.EntityFrameworkCore;

namespace MeowGram.Core.Data;

/// <summary>
/// Наша локальная БД: аккаунты, прокси, настройки кастомизации.
/// Чаты и сообщения хранит TDLib в базах аккаунтов.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();

    public DbSet<SettingEntity> Settings => Set<SettingEntity>();

    public DbSet<ProxyEntity> Proxies => Set<ProxyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountEntity>(e =>
        {
            e.ToTable("Accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(32);
            e.Property(x => x.FirstName).HasMaxLength(256);
            e.Property(x => x.LastName).HasMaxLength(256);
            e.Property(x => x.PhoneNumber).HasMaxLength(32);
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<SettingEntity>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
        });

        modelBuilder.Entity<ProxyEntity>(e =>
        {
            e.ToTable("Proxies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Server).HasMaxLength(255);
            e.Property(x => x.Username).HasMaxLength(255);
        });
    }
}
