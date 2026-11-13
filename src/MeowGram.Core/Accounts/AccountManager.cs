using System.Collections.Concurrent;
using MeowGram.Core.Data;
using MeowGram.Core.Settings;
using MeowGram.Core.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TdLib;

namespace MeowGram.Core.Accounts;

/// <summary>Вошедший аккаунт Telegram.</summary>
public sealed record AccountInfo(string Id, long UserId, string FirstName, string LastName, string PhoneNumber)
{
    public string DisplayName => string.IsNullOrWhiteSpace(LastName) ? FirstName : $"{FirstName} {LastName}";
}

/// <summary>
/// Несколько аккаунтов: у каждого своя папка и база TDLib (<see cref="AppPaths.ForAccount"/>).
/// Новый вход идёт через <see cref="PendingSession"/>; после AuthorizationStateReady аккаунт сохраняется и становится активным.
/// События приходят с фоновых потоков.
/// </summary>
public sealed class AccountManager : IAsyncDisposable
{
    private static readonly TimeSpan LogOutTimeout = TimeSpan.FromSeconds(15);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppPaths _paths;
    private readonly TelegramOptions _options;
    private readonly ISettingsStore _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly ConcurrentDictionary<string, TelegramSession> _sessions = new();
    private readonly ConcurrentDictionary<string, byte> _closingIntentionally = new();
    private volatile IReadOnlyList<AccountInfo> _accounts = [];

    public AccountManager(
        IDbContextFactory<AppDbContext> dbFactory,
        AppPaths paths,
        IOptions<TelegramOptions> options,
        ISettingsStore settings,
        ILoggerFactory loggerFactory)
    {
        _dbFactory = dbFactory;
        _paths = paths;
        _options = options.Value;
        _settings = settings;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AccountManager>();
    }

    /// <summary>Заданы ли api_id/api_hash. Без них TDLib не запускается.</summary>
    public bool IsTelegramConfigured => _options.IsConfigured;

    /// <summary>Вошедшие аккаунты в порядке добавления.</summary>
    public IReadOnlyList<AccountInfo> Accounts => _accounts;

    public TelegramSession? ActiveSession { get; private set; }

    /// <summary>Сессия, через которую сейчас идёт вход (экран входа / «Добавить аккаунт»).</summary>
    public TelegramSession? PendingSession { get; private set; }

    /// <summary>Сессии, с которыми можно работать до входа (прокси, пинг): ожидающая и все вошедшие.</summary>
    public IEnumerable<TelegramSession> Sessions => _sessions.Values;

    public event EventHandler? AccountsChanged;

    public event EventHandler? ActiveSessionChanged;

    /// <summary>Сессия приняла параметры TDLib — можно применять прокси.</summary>
    public event EventHandler<TelegramSession>? SessionInitialized;

    /// <summary>Загружает аккаунты, удаляет недовершённые входы, запускает TDLib для вошедших.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var entities = await db.Accounts.OrderBy(a => a.SortOrder).ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var abandoned in entities.Where(a => !a.IsAuthorized))
            {
                DeleteAccountFolder(abandoned.Id);
                db.Accounts.Remove(abandoned);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var authorized = entities.Where(a => a.IsAuthorized).ToList();
            _accounts = authorized.Select(ToInfo).ToArray();

            if (!IsTelegramConfigured)
            {
                if (authorized.Count > 0)
                    _logger.LogWarning("Telegram api_id/api_hash are not configured; saved accounts are not started.");
                return;
            }

            foreach (var account in authorized)
                await StartSessionAsync(account.Id, cancellationToken).ConfigureAwait(false);

            var activeId = await _settings.GetAsync<string>(SettingKeys.ActiveAccountId, cancellationToken).ConfigureAwait(false);
            ActiveSession = (activeId is not null && _sessions.TryGetValue(activeId, out var active))
                ? active
                : authorized.Select(a => _sessions.GetValueOrDefault(a.Id)).FirstOrDefault(s => s is not null);
        }
        finally
        {
            _mutex.Release();
        }

        AccountsChanged?.Invoke(this, EventArgs.Empty);
        ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Начинает вход нового аккаунта. Повторный вызов возвращает уже начатую сессию.</summary>
    public async Task<TelegramSession> BeginAddAccountAsync(CancellationToken cancellationToken = default)
    {
        if (!IsTelegramConfigured)
            throw new InvalidOperationException("Telegram api_id/api_hash are not configured.");

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (PendingSession is not null)
                return PendingSession;

            var id = AppPaths.NewAccountId();
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                db.Accounts.Add(new AccountEntity { Id = id, IsAuthorized = false, AddedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            PendingSession = await StartSessionAsync(id, cancellationToken).ConfigureAwait(false);
            return PendingSession;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Отменяет начатый вход: закрывает TDLib и удаляет его данные.</summary>
    public async Task CancelAddAccountAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (PendingSession is not { } pending)
                return;

            PendingSession = null;
            await RemoveSessionAsync(pending, logOut: false).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SwitchToAsync(string accountId)
    {
        if (!_sessions.TryGetValue(accountId, out var session) || !session.IsAuthorized)
            throw new InvalidOperationException($"Account {accountId} is not signed in.");

        ActiveSession = session;
        await _settings.SetAsync(SettingKeys.ActiveAccountId, accountId).ConfigureAwait(false);
        ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Выходит из аккаунта на сервере и удаляет его локальные данные.</summary>
    public async Task LogOutAsync(string accountId)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(accountId, out var session))
                return;

            await RemoveSessionAsync(session, logOut: true).ConfigureAwait(false);
            await RefreshAccountsAsync().ConfigureAwait(false);
            await EnsureActiveSessionAsync(accountId).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<TelegramSession> StartSessionAsync(string accountId, CancellationToken cancellationToken)
    {
        var session = new TelegramSession(
            accountId,
            _paths.ForAccount(accountId),
            _options,
            _paths.TdlibLogPath,
            _loggerFactory.CreateLogger<TelegramSession>());

        session.AuthorizationStateChanged += OnSessionAuthorizationStateChanged;
        session.Initialized += OnSessionInitialized;
        _sessions[accountId] = session;

        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    private void OnSessionInitialized(object? sender, EventArgs e)
    {
        if (sender is TelegramSession session)
            SessionInitialized?.Invoke(this, session);
    }

    private void OnSessionAuthorizationStateChanged(object? sender, TdApi.AuthorizationState state)
    {
        if (sender is not TelegramSession session)
            return;

        var isPending = ReferenceEquals(session, PendingSession);

        switch (state)
        {
            case TdApi.AuthorizationState.AuthorizationStateReady when isPending:
                _ = RunSafeAsync(() => CompleteAddAccountAsync(session), "complete sign-in");
                break;

            // Вошедший аккаунт снова просит номер/QR — авторизацию отозвали (например, с другого устройства).
            case TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber when !isPending:
            case TdApi.AuthorizationState.AuthorizationStateClosed when !isPending
                                                                     && !_closingIntentionally.ContainsKey(session.AccountId):
                _ = RunSafeAsync(() => HandleSessionRevokedAsync(session), "handle revoked session");
                break;
        }
    }

    private async Task CompleteAddAccountAsync(TelegramSession session)
    {
        var me = await session.Client.ExecuteAsync(new TdApi.GetMe()).ConfigureAwait(false);

        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(session, PendingSession))
                return;

            PendingSession = null;

            var duplicate = _accounts.FirstOrDefault(a => a.UserId == me.Id);
            if (duplicate is not null)
            {
                // Вошли в уже добавленный аккаунт — лишнюю сессию закрываем, переключаемся на существующую.
                await RemoveSessionAsync(session, logOut: true).ConfigureAwait(false);
                ActiveSession = _sessions.GetValueOrDefault(duplicate.Id) ?? ActiveSession;
            }
            else
            {
                await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
                var entity = await db.Accounts.FindAsync(session.AccountId).ConfigureAwait(false)
                    ?? db.Accounts.Add(new AccountEntity { Id = session.AccountId, AddedAt = DateTimeOffset.UtcNow }).Entity;

                entity.UserId = me.Id;
                entity.FirstName = me.FirstName ?? string.Empty;
                entity.LastName = me.LastName ?? string.Empty;
                entity.PhoneNumber = me.PhoneNumber ?? string.Empty;
                entity.IsAuthorized = true;
                entity.SortOrder = (await db.Accounts.Where(a => a.IsAuthorized).MaxAsync(a => (int?)a.SortOrder).ConfigureAwait(false) ?? -1) + 1;
                await db.SaveChangesAsync().ConfigureAwait(false);

                ActiveSession = session;
            }

            if (ActiveSession is not null)
                await _settings.SetAsync(SettingKeys.ActiveAccountId, ActiveSession.AccountId).ConfigureAwait(false);

            await RefreshAccountsAsync().ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        AccountsChanged?.Invoke(this, EventArgs.Empty);
        ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task HandleSessionRevokedAsync(TelegramSession session)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_sessions.ContainsKey(session.AccountId))
                return;

            _logger.LogInformation("Account {AccountId} was signed out remotely", session.AccountId);
            await RemoveSessionAsync(session, logOut: false).ConfigureAwait(false);
            await RefreshAccountsAsync().ConfigureAwait(false);
            await EnsureActiveSessionAsync(session.AccountId).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        AccountsChanged?.Invoke(this, EventArgs.Empty);
        ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Закрывает сессию, удаляет папку и запись аккаунта. Вызывать под <see cref="_mutex"/>.</summary>
    private async Task RemoveSessionAsync(TelegramSession session, bool logOut)
    {
        _closingIntentionally[session.AccountId] = 0;
        session.AuthorizationStateChanged -= OnSessionAuthorizationStateChanged;
        session.Initialized -= OnSessionInitialized;
        _sessions.TryRemove(session.AccountId, out _);

        try
        {
            if (logOut)
                await session.LogOutAsync(LogOutTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Log out timed out for {AccountId}", session.AccountId);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
            _closingIntentionally.TryRemove(session.AccountId, out _);
        }

        DeleteAccountFolder(session.AccountId);

        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        await db.Accounts.Where(a => a.Id == session.AccountId).ExecuteDeleteAsync().ConfigureAwait(false);
    }

    private async Task RefreshAccountsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        _accounts = (await db.Accounts.AsNoTracking()
                .Where(a => a.IsAuthorized)
                .OrderBy(a => a.SortOrder)
                .ToListAsync()
                .ConfigureAwait(false))
            .Select(ToInfo)
            .ToArray();
    }

    private async Task EnsureActiveSessionAsync(string removedAccountId)
    {
        if (ActiveSession is not null && ActiveSession.AccountId != removedAccountId)
            return;

        ActiveSession = _accounts.Select(a => _sessions.GetValueOrDefault(a.Id)).FirstOrDefault(s => s is not null);

        if (ActiveSession is null)
            await _settings.RemoveAsync(SettingKeys.ActiveAccountId).ConfigureAwait(false);
        else
            await _settings.SetAsync(SettingKeys.ActiveAccountId, ActiveSession.AccountId).ConfigureAwait(false);
    }

    private void DeleteAccountFolder(string accountId)
    {
        var root = _paths.ForAccount(accountId).RootDirectory;
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete account folder {Folder}", root);
        }
    }

    private async Task RunSafeAsync(Func<Task> action, string what)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to {What}", what);
        }
    }

    private static AccountInfo ToInfo(AccountEntity e) =>
        new(e.Id, e.UserId ?? 0, e.FirstName, e.LastName, e.PhoneNumber);

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            _closingIntentionally[session.AccountId] = 0;
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
        PendingSession = null;
        ActiveSession = null;
        _mutex.Dispose();
    }
}

/// <summary>Запускает сохранённые аккаунты вместе с хостом и закрывает TDLib при выходе.</summary>
internal sealed class AccountManagerHostedService(AccountManager accounts) : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => accounts.InitializeAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) => await accounts.DisposeAsync().ConfigureAwait(false);
}
