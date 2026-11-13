namespace RustTalkApp.Core.Telegram;

/// <summary>
/// Настройки подключения к Telegram. Секция "Telegram" в appsettings.Local.json
/// или переменные окружения RUSTTALK_Telegram__ApiId / RUSTTALK_Telegram__ApiHash.
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// <summary>api_id с https://my.telegram.org.</summary>
    public int ApiId { get; set; }

    /// <summary>api_hash с https://my.telegram.org.</summary>
    public string ApiHash { get; set; } = string.Empty;

    /// <summary>Подключаться к тестовым серверам Telegram.</summary>
    public bool UseTestDc { get; set; }

    /// <summary>Уровень логов TDLib: 0 — только фатальные, 1 — ошибки, 2 — предупреждения, 3+ — отладка.</summary>
    public int LogVerbosity { get; set; } = 1;

    public bool IsConfigured => ApiId > 0 && !string.IsNullOrWhiteSpace(ApiHash);
}
