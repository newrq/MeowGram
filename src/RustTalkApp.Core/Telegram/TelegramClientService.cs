using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RustTalkApp.Core.Security;
using TdLib;

namespace RustTalkApp.Core.Telegram;

/// <summary>
/// Владеет единственным <see cref="TdClient"/>: запускает TDLib, передаёт ему параметры
/// и раздаёт обновления. Запросы к API выполняются напрямую через <see cref="Client"/> и типы <see cref="TdApi"/>.
/// </summary>
public sealed class TelegramClientService(
    IOptions<TelegramOptions> options,
    AppPaths paths,
    DatabaseKeyStore keyStore,
    ILogger<TelegramClientService> logger) : IDisposable
{
    private readonly TelegramOptions _options = options.Value;
    private readonly Lock _gate = new();
    private TdClient? _client;
    private int _parametersSent;

    /// <summary>Клиент TDLib. Доступен после успешного <see cref="StartAsync"/>.</summary>
    public TdClient Client => _client ?? throw new InvalidOperationException("TDLib client is not started.");

    public bool IsStarted => _client is not null;

    public TdApi.AuthorizationState? AuthorizationState { get; private set; }

    /// <summary>Все обновления TDLib. Приходят с фонового потока — в UI маршалить через Dispatcher.</summary>
    public event EventHandler<TdApi.Update>? UpdateReceived;

    /// <summary>Смена состояния авторизации (ввод телефона, кода, пароля, готово...). Фоновый поток.</summary>
    public event EventHandler<TdApi.AuthorizationState>? AuthorizationStateChanged;

    /// <summary>Запускает TDLib. Возвращает false, если не заданы api_id/api_hash.</summary>
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            logger.LogWarning("Telegram api_id/api_hash are not configured; TDLib is not started. See appsettings.Local.example.json.");
            return false;
        }

        TdClient client;
        lock (_gate)
        {
            if (_client is not null)
                return true;

            Directory.CreateDirectory(Path.GetDirectoryName(paths.TdlibLogPath)!);

            client = new TdClient();
            client.Execute(new TdApi.SetLogVerbosityLevel { NewVerbosityLevel = _options.LogVerbosity });
            client.Execute(new TdApi.SetLogStream
            {
                LogStream = new TdApi.LogStream.LogStreamFile { Path = paths.TdlibLogPath, MaxFileSize = 10 * 1024 * 1024 },
            });
            client.UpdateReceived += OnUpdateReceived;
            _client = client;
        }

        // Первое UpdateAuthorizationState может прийти до подписки на событие — запрашиваем состояние явно.
        // ConfigureAwait(false): хост стартует синхронно из UI-потока, захват его контекста дал бы взаимоблокировку.
        var state = await client.ExecuteAsync(new TdApi.GetAuthorizationState())
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        HandleAuthorizationState(state);

        logger.LogInformation("TDLib started");
        return true;
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

        if (state is TdApi.AuthorizationState.AuthorizationStateWaitTdlibParameters
            && Interlocked.Exchange(ref _parametersSent, 1) == 0)
        {
            _ = SendTdlibParametersAsync();
        }

        AuthorizationStateChanged?.Invoke(this, state);
    }

    private async Task SendTdlibParametersAsync()
    {
        try
        {
            Directory.CreateDirectory(paths.TdlibDatabaseDirectory);
            Directory.CreateDirectory(paths.TdlibFilesDirectory);

            await Client.ExecuteAsync(new TdApi.SetTdlibParameters
            {
                UseTestDc = _options.UseTestDc,
                DatabaseDirectory = paths.TdlibDatabaseDirectory,
                FilesDirectory = paths.TdlibFilesDirectory,
                DatabaseEncryptionKey = keyStore.GetOrCreateKey(),
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
            logger.LogError(ex, "Failed to send TDLib parameters");
        }
    }

    public void Dispose()
    {
        TdClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
        }

        if (client is null)
            return;

        client.UpdateReceived -= OnUpdateReceived;
        client.Dispose();
    }
}
