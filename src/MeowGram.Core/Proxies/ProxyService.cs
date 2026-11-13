using MeowGram.Core.Accounts;
using MeowGram.Core.Data;
using MeowGram.Core.Security;
using MeowGram.Core.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TdLib;

namespace MeowGram.Core.Proxies;

/// <summary>
/// Прокси уровня приложения: список хранится в нашей БД, включённый прокси применяется ко всем сессиям TDLib
/// (в TDLib каждой сессии держим не больше одного прокси — включённого).
/// </summary>
public sealed class ProxyService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SecretProtector _protector;
    private readonly AccountManager _accounts;
    private readonly ILogger<ProxyService> _logger;

    public ProxyService(
        IDbContextFactory<AppDbContext> dbFactory,
        SecretProtector protector,
        AccountManager accounts,
        ILogger<ProxyService> logger)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _accounts = accounts;
        _logger = logger;
        _accounts.SessionInitialized += (_, session) => _ = ApplySafeAsync(session);
    }

    /// <summary>Список изменился (добавили, удалили, включили). Фоновый поток.</summary>
    public event EventHandler? ProxiesChanged;

    public async Task<IReadOnlyList<SavedProxy>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entities = await db.Proxies.AsNoTracking().OrderBy(p => p.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(ToSaved).ToArray();
    }

    public async Task<SavedProxy> AddAsync(ProxySettings settings, bool enable, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (enable)
            await db.Proxies.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsEnabled, false), cancellationToken).ConfigureAwait(false);

        var entity = new ProxyEntity
        {
            Kind = settings.Kind,
            Server = settings.Server,
            Port = settings.Port,
            Username = settings.Username,
            ProtectedPassword = _protector.Protect(settings.Password),
            ProtectedSecret = _protector.Protect(settings.Secret),
            HttpOnly = settings.HttpOnly,
            IsEnabled = enable,
            AddedAt = DateTimeOffset.UtcNow,
        };
        db.Proxies.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await ApplyToAllAsync().ConfigureAwait(false);
        ProxiesChanged?.Invoke(this, EventArgs.Empty);
        return ToSaved(entity);
    }

    public async Task RemoveAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Proxies.Where(p => p.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        await ApplyToAllAsync().ConfigureAwait(false);
        ProxiesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Включает прокси <paramref name="id"/> (остальные выключаются); null — работать без прокси.</summary>
    public async Task EnableAsync(int? id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Proxies.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsEnabled, p => p.Id == id), cancellationToken).ConfigureAwait(false);

        await ApplyToAllAsync().ConfigureAwait(false);
        ProxiesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Пинг до серверов Telegram через прокси. Нужна хотя бы одна запущенная сессия (на экране входа — ожидающая).</summary>
    public async Task<TimeSpan> PingAsync(ProxySettings settings, CancellationToken cancellationToken = default)
    {
        var session = AnySession();
        var result = await ExecuteAsync(session, new TdApi.PingProxy { Proxy = settings.ToTdProxy() }).WaitAsync(cancellationToken).ConfigureAwait(false);
        return TimeSpan.FromSeconds(result.Seconds_ ?? 0);
    }

    /// <summary>Проверяет, что через прокси доступен дата-центр Telegram.</summary>
    public async Task TestAsync(ProxySettings settings, CancellationToken cancellationToken = default)
    {
        var session = AnySession();
        await ExecuteAsync(session, new TdApi.TestProxy { Proxy = settings.ToTdProxy(), DcId = 2, Timeout = 10 })
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyToAllAsync()
    {
        foreach (var session in _accounts.Sessions.Where(s => s.IsStarted))
            await ApplySafeAsync(session).ConfigureAwait(false);
    }

    private async Task ApplySafeAsync(TelegramSession session)
    {
        try
        {
            await ApplyAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply proxy to account {AccountId}", session.AccountId);
        }
    }

    private async Task ApplyAsync(TelegramSession session)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var enabled = await db.Proxies.AsNoTracking().FirstOrDefaultAsync(p => p.IsEnabled).ConfigureAwait(false);

        var existing = await session.Client.ExecuteAsync(new TdApi.GetProxies()).ConfigureAwait(false);
        foreach (var proxy in existing.Proxies)
            await session.Client.ExecuteAsync(new TdApi.RemoveProxy { ProxyId = proxy.Id }).ConfigureAwait(false);

        if (enabled is null)
        {
            await session.Client.ExecuteAsync(new TdApi.DisableProxy()).ConfigureAwait(false);
            return;
        }

        await session.Client.ExecuteAsync(new TdApi.AddProxy
        {
            Proxy = ToSaved(enabled).Settings.ToTdProxy(),
            Enable = true,
            Comment = string.Empty,
        }).ConfigureAwait(false);
    }

    private TelegramSession AnySession() =>
        _accounts.PendingSession ?? _accounts.ActiveSession ?? _accounts.Sessions.FirstOrDefault(s => s.IsStarted)
        ?? throw new InvalidOperationException("No running Telegram session to test the proxy with.");

    private static async Task<T> ExecuteAsync<T>(TelegramSession session, TdApi.Function<T> function) where T : TdApi.Object
    {
        try
        {
            return await session.Client.ExecuteAsync(function).ConfigureAwait(false);
        }
        catch (TdException ex)
        {
            throw new ProxyException(ex.Error.Message);
        }
    }

    private SavedProxy ToSaved(ProxyEntity e) => new(
        e.Id,
        new ProxySettings(e.Kind, e.Server, e.Port, e.Username, _protector.Unprotect(e.ProtectedPassword), _protector.Unprotect(e.ProtectedSecret), e.HttpOnly),
        e.IsEnabled);
}

/// <summary>Прокси недоступен или настроен неверно.</summary>
public sealed class ProxyException(string message) : Exception(message);
