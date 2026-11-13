using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using MeowGram.Core.Security;
using Microsoft.Extensions.Logging;
using TdLib;

namespace MeowGram.Core.Telegram;

/// <summary>
/// Один аккаунт = один <see cref="TdClient"/> со своей зашифрованной базой.
/// Сам отправляет параметры TDLib и раздаёт обновления; запросы — через <see cref="Client"/>.
/// </summary>
public sealed class TelegramSession : IAsyncDisposable
{
    private static readonly Lock LogGate = new();
    private static bool _logConfigured;

    private readonly AccountPaths _paths;
    private readonly TelegramOptions _options;
    private readonly string _tdlibLogPath;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TdClient? _client;
    private int _parametersSent;
    private int _initializedRaised;

    public TelegramSession(string accountId, AccountPaths paths, TelegramOptions options, string tdlibLogPath, ILogger logger)
    {
        AccountId = accountId;
        _paths = paths;
        _options = options;
        _tdlibLogPath = tdlibLogPath;
        _logger = logger;
        Auth = new TelegramAuth(this);
    }

    public string AccountId { get; }

    /// <summary>Клиент TDLib. Доступен после <see cref="StartAsync"/>.</summary>
    public TdClient Client => _client ?? throw new InvalidOperationException("TDLib client is not started.");

    public bool IsStarted => _client is not null;

    public TdApi.AuthorizationState? AuthorizationState { get; private set; }

    public bool IsAuthorized => AuthorizationState is TdApi.AuthorizationState.AuthorizationStateReady;

    /// <summary>Шаги входа: QR, телефон, код, пароль, регистрация.</summary>
    public ITelegramAuth Auth { get; }

    /// <summary>Все обновления TDLib. Фоновый поток — в UI маршалить через Dispatcher.</summary>
    public event EventHandler<TdApi.Update>? UpdateReceived;

    /// <summary>Смена состояния авторизации. Фоновый поток.</summary>
    public event EventHandler<TdApi.AuthorizationState>? AuthorizationStateChanged;

    /// <summary>TDLib принял параметры: можно настраивать прокси и выполнять запросы до входа.</summary>
    public event EventHandler? Initialized;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        TdClient client;
        lock (_gate)
        {
            if (_client is not null)
                return;

            client = new TdClient();
            ConfigureLogging(client);
            // Сначала запоминаем клиента, потом подписываемся: первое обновление приходит с фонового потока сразу.
            _client = client;
            client.UpdateReceived += OnUpdateReceived;
        }

        // Первое UpdateAuthorizationState может прийти до подписки — запрашиваем состояние явно.
        // ConfigureAwait(false): хост стартует синхронно из UI-потока.
        var state = await client.ExecuteAsync(new TdApi.GetAuthorizationState())
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        HandleAuthorizationState(state);
    }

    /// <summary>Выход из аккаунта на сервере. TDLib перейдёт в LoggingOut → Closed.</summary>
    public async Task LogOutAsync(TimeSpan timeout)
    {
        if (_client is null)
            return;

        try
        {
            await _client.ExecuteAsync(new TdApi.LogOut()).ConfigureAwait(false);
        }
        catch (TdException ex)
        {
            _logger.LogWarning("LogOut failed for {AccountId}: {Error}", AccountId, ex.Error.Message);
        }

        await _closed.Task.WaitAsync(timeout).ConfigureAwait(false);
    }

    private void ConfigureLogging(TdClient client)
    {
        lock (LogGate)
        {
            if (_logConfigured)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_tdlibLogPath)!);
            client.Execute(new TdApi.SetLogVerbosityLevel { NewVerbosityLevel = _options.LogVerbosity });
            client.Execute(new TdApi.SetLogStream
            {
                LogStream = new TdApi.LogStream.LogStreamFile { Path = _tdlibLogPath, MaxFileSize = 10 * 1024 * 1024 },
            });
            _logConfigured = true;
        }
    }

    private void OnUpdateReceived(object? sender, TdApi.Update update)
    {
        if (update is TdApi.Update.UpdateAuthorizationState authUpdate)
            HandleAuthorizationState(authUpdate.AuthorizationState);

        UpdateReceived?.Invoke(this, update);
    }

    private void HandleAuthorizationState(TdApi.AuthorizationState state)
    {
        AuthorizationState = state;

        switch (state)
        {
            case TdApi.AuthorizationState.AuthorizationStateWaitTdlibParameters
                when Interlocked.Exchange(ref _parametersSent, 1) == 0:
                _ = SendTdlibParametersAsync();
                break;

            case TdApi.AuthorizationState.AuthorizationStateWaitTdlibParameters:
                break;

            case TdApi.AuthorizationState.AuthorizationStateClosed:
                _closed.TrySetResult();
                break;

            case TdApi.AuthorizationState.AuthorizationStateClosing:
            case TdApi.AuthorizationState.AuthorizationStateLoggingOut:
                break;

            default:
                if (Interlocked.Exchange(ref _initializedRaised, 1) == 0)
                    Initialized?.Invoke(this, EventArgs.Empty);
                break;
        }

        AuthorizationStateChanged?.Invoke(this, state);
    }

    private async Task SendTdlibParametersAsync()
    {
        // Сессию могли уже закрыть — тогда параметры не нужны.
        if (_client is not { } client)
            return;

        try
        {
            Directory.CreateDirectory(_paths.TdlibDatabaseDirectory);
            Directory.CreateDirectory(_paths.TdlibFilesDirectory);

            await client.ExecuteAsync(new TdApi.SetTdlibParameters
            {
                UseTestDc = _options.UseTestDc,
                DatabaseDirectory = _paths.TdlibDatabaseDirectory,
                FilesDirectory = _paths.TdlibFilesDirectory,
                DatabaseEncryptionKey = new DatabaseKeyStore(_paths.TdlibKeyPath).GetOrCreateKey(),
                UseFileDatabase = true,
                UseChatInfoDatabase = true,
                UseMessageDatabase = true,
                UseSecretChats = true,
                ApiId = _options.ApiId,
                ApiHash = _options.ApiHash,
                SystemLanguageCode = string.IsNullOrEmpty(CultureInfo.CurrentUICulture.Name) ? "en" : CultureInfo.CurrentUICulture.Name,
                DeviceModel = "Desktop",
                SystemVersion = RuntimeInformation.OSDescription,
                ApplicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0",
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _parametersSent, 0);
            _logger.LogError(ex, "Failed to send TDLib parameters for {AccountId}", AccountId);
        }
    }

    /// <summary>Корректно закрывает TDLib (база должна быть закрыта до освобождения клиента).</summary>
    public async ValueTask DisposeAsync()
    {
        TdClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
        }

        if (client is null)
            return;

        if (AuthorizationState is not TdApi.AuthorizationState.AuthorizationStateClosed)
        {
            try
            {
                await client.ExecuteAsync(new TdApi.Close()).ConfigureAwait(false);
                await _closed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TdException or TimeoutException)
            {
                _logger.LogWarning("TDLib did not close cleanly for {AccountId}: {Message}", AccountId, ex.Message);
            }
        }

        client.UpdateReceived -= OnUpdateReceived;
        client.Dispose();
    }
}
