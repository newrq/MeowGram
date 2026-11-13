using MeowGram.Core.Proxies;

namespace MeowGram.Core.Data;

/// <summary>Аккаунт Telegram. Пока вход не завершён, <see cref="IsAuthorized"/> = false.</summary>
public sealed class AccountEntity
{
    public required string Id { get; set; }

    public long? UserId { get; set; }

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string PhoneNumber { get; set; } = string.Empty;

    public bool IsAuthorized { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>Настройка приложения: ключ → JSON-значение.</summary>
public sealed class SettingEntity
{
    public required string Key { get; set; }

    public required string Value { get; set; }
}

/// <summary>Прокси уровня приложения (применяется ко всем аккаунтам). Пароль и секрет защищены средствами ОС.</summary>
public sealed class ProxyEntity
{
    public int Id { get; set; }

    public ProxyKind Kind { get; set; }

    public required string Server { get; set; }

    public int Port { get; set; }

    public string? Username { get; set; }

    public string? ProtectedPassword { get; set; }

    public string? ProtectedSecret { get; set; }

    public bool HttpOnly { get; set; }

    public bool IsEnabled { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}
