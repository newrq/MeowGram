using System.Text.RegularExpressions;
using TdLib;

namespace MeowGram.Core.Telegram;

/// <summary>Понятная UI причина ошибки входа (текст для пользователя берётся из локализации по этому значению).</summary>
public enum AuthErrorKind
{
    Unknown,
    PhoneNumberInvalid,
    PhoneNumberBanned,
    CodeInvalid,
    CodeExpired,
    PasswordInvalid,
    EmailInvalid,
    NameInvalid,
    TooManyRequests,
    ApiIdInvalid,
}

/// <summary>Ошибка шага входа. <see cref="RetryAfter"/> задан для TooManyRequests, если сервер сообщил время.</summary>
public sealed class AuthException(AuthErrorKind kind, TimeSpan? retryAfter, string rawMessage)
    : Exception($"{kind}: {rawMessage}")
{
    public AuthErrorKind Kind { get; } = kind;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public string RawMessage { get; } = rawMessage;

    public static AuthException From(TdApi.Error error)
    {
        var (kind, retryAfter) = AuthErrorMapper.Map(error.Code, error.Message);
        return new AuthException(kind, retryAfter, error.Message);
    }
}

public static partial class AuthErrorMapper
{
    public static (AuthErrorKind Kind, TimeSpan? RetryAfter) Map(int code, string? message)
    {
        var text = message ?? string.Empty;

        var retry = RetryAfterPattern().Match(text);
        if (retry.Success)
            return (AuthErrorKind.TooManyRequests, TimeSpan.FromSeconds(int.Parse(retry.Groups["s"].Value)));

        if (code == 429)
            return (AuthErrorKind.TooManyRequests, null);

        var kind = text.ToUpperInvariant() switch
        {
            var t when t.Contains("PHONE_NUMBER_INVALID") => AuthErrorKind.PhoneNumberInvalid,
            var t when t.Contains("PHONE_NUMBER_BANNED") => AuthErrorKind.PhoneNumberBanned,
            var t when t.Contains("PHONE_NUMBER_FLOOD") => AuthErrorKind.TooManyRequests,
            var t when t.Contains("PHONE_CODE_EXPIRED") => AuthErrorKind.CodeExpired,
            var t when t.Contains("PHONE_CODE_INVALID") || t.Contains("PHONE_CODE_EMPTY")
                       || t.Contains("EMAIL_CODE_INVALID") || t.Contains("CODE_INVALID") => AuthErrorKind.CodeInvalid,
            var t when t.Contains("PASSWORD_HASH_INVALID") => AuthErrorKind.PasswordInvalid,
            var t when t.Contains("EMAIL_INVALID") || t.Contains("EMAIL_ADDRESS_INVALID") => AuthErrorKind.EmailInvalid,
            var t when t.Contains("FIRSTNAME_INVALID") || t.Contains("LASTNAME_INVALID") => AuthErrorKind.NameInvalid,
            var t when t.Contains("API_ID_INVALID") || t.Contains("API_ID_PUBLISHED_FLOOD") => AuthErrorKind.ApiIdInvalid,
            _ => AuthErrorKind.Unknown,
        };

        return (kind, null);
    }

    // "FLOOD_WAIT_123" (MTProto) или "Too Many Requests: retry after 123" (TDLib).
    [GeneratedRegex(@"(?:FLOOD_WAIT_|retry after\s+)(?<s>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RetryAfterPattern();
}
