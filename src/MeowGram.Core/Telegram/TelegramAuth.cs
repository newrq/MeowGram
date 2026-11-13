using TdLib;

namespace MeowGram.Core.Telegram;

/// <summary>
/// Шаги входа в Telegram. Какой шаг сейчас нужен — смотрим по <see cref="State"/>.
/// Ошибки TDLib превращаются в <see cref="AuthException"/>.
/// </summary>
public interface ITelegramAuth
{
    TdApi.AuthorizationState? State { get; }

    /// <summary>Фоновый поток.</summary>
    event EventHandler<TdApi.AuthorizationState>? StateChanged;

    /// <summary>Перейти ко входу по QR. Ссылка придёт в состоянии WaitOtherDeviceConfirmation.</summary>
    Task RequestQrCodeAsync();

    Task SetPhoneNumberAsync(string phoneNumber);

    Task CheckCodeAsync(string code);

    Task ResendCodeAsync();

    Task CheckPasswordAsync(string password);

    /// <summary>Отправить код восстановления облачного пароля на почту.</summary>
    Task RequestPasswordRecoveryAsync();

    Task CheckPasswordRecoveryCodeAsync(string recoveryCode);

    Task RecoverPasswordAsync(string recoveryCode, string? newPassword, string? newHint);

    Task SetEmailAddressAsync(string emailAddress);

    Task CheckEmailCodeAsync(string code);

    Task RegisterAsync(string firstName, string lastName);

    Task<IReadOnlyList<TdApi.CountryInfo>> GetCountriesAsync();

    /// <summary>Страна и форматирование номера по введённому началу номера.</summary>
    Task<TdApi.PhoneNumberInfo> GetPhoneNumberInfoAsync(string phoneNumberPrefix);
}

internal sealed class TelegramAuth : ITelegramAuth
{
    private readonly TelegramSession _session;

    public TelegramAuth(TelegramSession session)
    {
        _session = session;
        _session.AuthorizationStateChanged += (_, state) => StateChanged?.Invoke(this, state);
    }

    public TdApi.AuthorizationState? State => _session.AuthorizationState;

    public event EventHandler<TdApi.AuthorizationState>? StateChanged;

    public Task RequestQrCodeAsync() =>
        RunAsync(new TdApi.RequestQrCodeAuthentication { OtherUserIds = [] });

    public Task SetPhoneNumberAsync(string phoneNumber) =>
        RunAsync(new TdApi.SetAuthenticationPhoneNumber
        {
            PhoneNumber = phoneNumber,
            Settings = new TdApi.PhoneNumberAuthenticationSettings { AuthenticationTokens = [] },
        });

    public Task CheckCodeAsync(string code) =>
        RunAsync(new TdApi.CheckAuthenticationCode { Code = code });

    public Task ResendCodeAsync() =>
        RunAsync(new TdApi.ResendAuthenticationCode { Reason = new TdApi.ResendCodeReason.ResendCodeReasonUserRequest() });

    public Task CheckPasswordAsync(string password) =>
        RunAsync(new TdApi.CheckAuthenticationPassword { Password = password });

    public Task RequestPasswordRecoveryAsync() =>
        RunAsync(new TdApi.RequestAuthenticationPasswordRecovery());

    public Task CheckPasswordRecoveryCodeAsync(string recoveryCode) =>
        RunAsync(new TdApi.CheckAuthenticationPasswordRecoveryCode { RecoveryCode = recoveryCode });

    public Task RecoverPasswordAsync(string recoveryCode, string? newPassword, string? newHint) =>
        RunAsync(new TdApi.RecoverAuthenticationPassword
        {
            RecoveryCode = recoveryCode,
            NewPassword = newPassword ?? string.Empty,
            NewHint = newHint ?? string.Empty,
        });

    public Task SetEmailAddressAsync(string emailAddress) =>
        RunAsync(new TdApi.SetAuthenticationEmailAddress { EmailAddress = emailAddress });

    public Task CheckEmailCodeAsync(string code) =>
        RunAsync(new TdApi.CheckAuthenticationEmailCode
        {
            Code = new TdApi.EmailAddressAuthentication.EmailAddressAuthenticationCode { Code = code },
        });

    public Task RegisterAsync(string firstName, string lastName) =>
        RunAsync(new TdApi.RegisterUser { FirstName = firstName, LastName = lastName, DisableNotification = false });

    public async Task<IReadOnlyList<TdApi.CountryInfo>> GetCountriesAsync()
    {
        var countries = await RunAsync(new TdApi.GetCountries()).ConfigureAwait(false);
        return countries.Countries_.Where(c => !c.IsHidden).ToArray();
    }

    public Task<TdApi.PhoneNumberInfo> GetPhoneNumberInfoAsync(string phoneNumberPrefix) =>
        RunAsync(new TdApi.GetPhoneNumberInfo { PhoneNumberPrefix = phoneNumberPrefix });

    private async Task<T> RunAsync<T>(TdApi.Function<T> function) where T : TdApi.Object
    {
        try
        {
            return await _session.Client.ExecuteAsync(function).ConfigureAwait(false);
        }
        catch (TdException ex)
        {
            throw AuthException.From(ex.Error);
        }
    }
}
