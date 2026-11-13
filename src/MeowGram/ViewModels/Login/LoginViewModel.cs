using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using MeowGram.Core.Accounts;
using MeowGram.Core.Telegram;
using MeowGram.Localization;
using TdLib;

namespace MeowGram.ViewModels.Login;

/// <summary>
/// Экран входа: ведёт пользователя по шагам TDLib (QR → номер → код → облачный пароль, почта, регистрация).
/// Сначала показывается QR; «Войти по номеру» переключает на номер. Ошибки — понятным текстом под полем.
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AccountManager _accounts;
    private readonly DispatcherTimer _resendTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TelegramSession? _session;
    private bool _wantsPhone;
    private bool _qrRequested;
    private int _resendLeft;
    private TdApi.AuthenticationCodeType? _nextCodeType;
    private int _phoneLookupVersion;
    private IReadOnlyList<TdApi.CountryInfo> _countries = [];

    public LoginViewModel(AccountManager accounts, LoginPanelsViewModel panels)
    {
        _accounts = accounts;
        Panels = panels;
        CurrentStep = new QrStep(this); // пока нет ссылки — «бумага» QR пульсирует, потом QR вырастает на месте
        _resendTimer.Tick += (_, _) => TickResend();
        Loc.Instance.LanguageChanged += (_, _) => RefreshLocalizedTexts();
    }

    public LoginPanelsViewModel Panels { get; }

    [ObservableProperty]
    public partial LoginStep CurrentStep { get; private set; }

    /// <summary>Переход назад (для направления анимации).</summary>
    [ObservableProperty]
    public partial bool IsBack { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    // QR
    [ObservableProperty]
    public partial string? QrLink { get; private set; }

    // Номер
    [ObservableProperty]
    public partial string PhoneNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CountryName { get; private set; } = string.Empty;

    // Код
    [ObservableProperty]
    public partial int CodeLength { get; private set; } = 5;

    [ObservableProperty]
    public partial bool IsNumericCode { get; private set; } = true;

    [ObservableProperty]
    public partial string CodeDelivery { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Code { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResendText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanResend { get; private set; }

    // Облачный пароль
    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PasswordHint { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RecoveryEmailPattern { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RecoveryCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewHint { get; set; } = string.Empty;

    private bool _hasRecoveryEmail;

    // Почта
    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmailCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int EmailCodeLength { get; private set; } = 6;

    [ObservableProperty]
    public partial string EmailCodeSubtitle { get; private set; } = string.Empty;

    // Регистрация
    [ObservableProperty]
    public partial string FirstName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastName { get; set; } = string.Empty;

    private TdApi.AuthenticationCodeInfo? _codeInfo;
    private TdApi.EmailAddressAuthenticationCodeInfo? _emailCodeInfo;

    /// <summary>Запускает TDLib для входа (или показывает, что нет ключей).</summary>
    public async Task InitializeAsync()
    {
        if (!_accounts.IsTelegramConfigured)
        {
            Navigate(new NotConfiguredStep(this), back: false);
            return;
        }

        try
        {
            _session = await _accounts.BeginAddAccountAsync();
            _session.Auth.StateChanged += OnAuthStateChanged;
            Panels.AttachSession(_session);
            if (_session.AuthorizationState is { } state)
                HandleState(state);

            _countries = await _session.Auth.GetCountriesAsync();
            if (string.IsNullOrEmpty(PhoneNumber))
                PhoneNumber = DefaultPhonePrefix();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private ITelegramAuth Auth => _session?.Auth ?? throw new InvalidOperationException("Sign-in has not started.");

    private void OnAuthStateChanged(object? sender, TdApi.AuthorizationState state) =>
        Dispatcher.UIThread.Post(() => HandleState(state));

    private void HandleState(TdApi.AuthorizationState state)
    {
        switch (state)
        {
            case TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber:
                if (_wantsPhone)
                {
                    Navigate(new PhoneStep(this), back: CurrentStep.Order > 2);
                }
                else if (!_qrRequested)
                {
                    _qrRequested = true;
                    _ = RunAsync(() => Auth.RequestQrCodeAsync(), showBusy: false);
                }

                break;

            case TdApi.AuthorizationState.AuthorizationStateWaitOtherDeviceConfirmation qr:
                QrLink = qr.Link;
                if (!_wantsPhone)
                    Navigate(new QrStep(this), back: CurrentStep.Order > 1);
                break;

            case TdApi.AuthorizationState.AuthorizationStateWaitCode code:
                ApplyCodeInfo(code.CodeInfo);
                Code = string.Empty;
                Navigate(new CodeStep(this), back: false);
                break;

            case TdApi.AuthorizationState.AuthorizationStateWaitPassword password:
                PasswordHint = string.IsNullOrWhiteSpace(password.PasswordHint) ? string.Empty : Loc.Instance.Format("Login_PasswordHint", password.PasswordHint);
                _hasRecoveryEmail = password.HasRecoveryEmailAddress;
                RecoveryEmailPattern = password.RecoveryEmailAddressPattern ?? string.Empty;
                Navigate(new PasswordStep(this), back: CurrentStep is PasswordRecoveryStep);
                break;

            case TdApi.AuthorizationState.AuthorizationStateWaitEmailAddress:
                Navigate(new EmailStep(this), back: false);
                break;

            case TdApi.AuthorizationState.AuthorizationStateWaitEmailCode emailCode:
                _emailCodeInfo = emailCode.CodeInfo;
                EmailCodeLength = emailCode.CodeInfo?.Length is > 0 and var l ? l : 6;
                RefreshLocalizedTexts();
                EmailCode = string.Empty;
                Navigate(new EmailCodeStep(this), back: false);
                break;

            case TdApi.AuthorizationState.AuthorizationStateWaitRegistration:
                Navigate(new RegistrationStep(this), back: false);
                break;

            case TdApi.AuthorizationState.AuthorizationStateWaitPremiumPurchase:
                Navigate(new PremiumStep(this), back: false);
                break;
        }
    }

    private void Navigate(LoginStep step, bool back)
    {
        if (CurrentStep.GetType() == step.GetType())
            return;

        ErrorText = null;
        IsBack = back;
        CurrentStep = step;
    }

    [RelayCommand]
    private void UsePhone()
    {
        _wantsPhone = true;
        Navigate(new PhoneStep(this), back: false);
    }

    [RelayCommand]
    private async Task UseQrAsync()
    {
        _wantsPhone = false;
        if (QrLink is not null && Auth.State is TdApi.AuthorizationState.AuthorizationStateWaitOtherDeviceConfirmation)
        {
            Navigate(new QrStep(this), back: true);
            return;
        }

        QrLink = null;
        Navigate(new QrStep(this), back: true);
        await RunAsync(() => Auth.RequestQrCodeAsync(), showBusy: false);
    }

    [RelayCommand]
    private Task SubmitPhoneAsync() => RunAsync(async () =>
    {
        var phone = PhoneNumber.Trim();
        try
        {
            await Auth.SetPhoneNumberAsync(phone);
        }
        catch (AuthException ex) when (ex.Kind == AuthErrorKind.Unknown && ex.RawMessage.Contains("unexpected", StringComparison.OrdinalIgnoreCase))
        {
            // TDLib не разрешает задать номер в режиме QR — начинаем вход заново и сразу задаём номер.
            await RestartSessionAsync();
            await Auth.SetPhoneNumberAsync(phone);
        }
    });

    [RelayCommand]
    private Task SubmitCodeAsync() => RunAsync(() => Auth.CheckCodeAsync(Code.Trim()), onError: () => Code = string.Empty);

    [RelayCommand]
    private Task ResendAsync() => RunAsync(() => Auth.ResendCodeAsync());

    [RelayCommand]
    private void ChangeNumber()
    {
        _wantsPhone = true;
        Navigate(new PhoneStep(this), back: true);
    }

    [RelayCommand]
    private Task SubmitPasswordAsync() => RunAsync(() => Auth.CheckPasswordAsync(Password));

    [RelayCommand]
    private Task ForgotPasswordAsync()
    {
        if (!_hasRecoveryEmail)
        {
            ErrorText = Loc.Instance["Login_NoRecoveryEmail"];
            return Task.CompletedTask;
        }

        return RunAsync(async () =>
        {
            await Auth.RequestPasswordRecoveryAsync();
            RecoveryCode = NewPassword = NewHint = string.Empty;
            Navigate(new PasswordRecoveryStep(this), back: false);
        });
    }

    [RelayCommand]
    private Task SubmitRecoveryAsync() => RunAsync(async () =>
    {
        await Auth.CheckPasswordRecoveryCodeAsync(RecoveryCode.Trim());
        await Auth.RecoverPasswordAsync(RecoveryCode.Trim(), NewPassword, NewHint);
    });

    [RelayCommand]
    private void BackToPassword() => Navigate(new PasswordStep(this), back: true);

    [RelayCommand]
    private Task SubmitEmailAsync() => RunAsync(() => Auth.SetEmailAddressAsync(Email.Trim()));

    [RelayCommand]
    private Task SubmitEmailCodeAsync() => RunAsync(() => Auth.CheckEmailCodeAsync(EmailCode.Trim()), onError: () => EmailCode = string.Empty);

    [RelayCommand]
    private Task SubmitRegistrationAsync() => RunAsync(() => Auth.RegisterAsync(FirstName.Trim(), LastName.Trim()));

    partial void OnPhoneNumberChanged(string value)
    {
        if (CurrentStep is PhoneStep && HasError)
            ErrorText = null;
        _ = LookupCountryAsync(value);
    }

    partial void OnPasswordChanged(string value)
    {
        if (HasError)
            ErrorText = null;
    }

    private async Task LookupCountryAsync(string phone)
    {
        var version = ++_phoneLookupVersion;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0 || _session is null)
        {
            CountryName = string.Empty;
            return;
        }

        try
        {
            var info = await Auth.GetPhoneNumberInfoAsync(digits);
            if (version == _phoneLookupVersion)
                CountryName = info.Country?.Name ?? Loc.Instance["Login_CountryUnknown"];
        }
        catch (AuthException)
        {
            if (version == _phoneLookupVersion)
                CountryName = Loc.Instance["Login_CountryUnknown"];
        }
    }

    private string DefaultPhonePrefix()
    {
        try
        {
            var region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            var country = _countries.FirstOrDefault(c => string.Equals(c.CountryCode, region, StringComparison.OrdinalIgnoreCase));
            return country?.CallingCodes is { Length: > 0 } codes ? $"+{codes[0]} " : "+";
        }
        catch (ArgumentException)
        {
            return "+";
        }
    }

    private void ApplyCodeInfo(TdApi.AuthenticationCodeInfo info)
    {
        _codeInfo = info;
        (CodeLength, IsNumericCode) = info.Type switch
        {
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeTelegramMessage t => (t.Length, true),
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeSms t => (t.Length, true),
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeCall t => (t.Length, true),
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeMissedCall t => (t.Length, true),
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeFragment t => (t.Length, true),
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeFirebaseAndroid t => (t.Length, true),
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeFirebaseIos t => (t.Length, true),
            _ => (CodeLength, false), // SmsWord / SmsPhrase / FlashCall — вводится текстом
        };
        if (CodeLength is <= 0 or > 12)
            CodeLength = 5;

        _nextCodeType = info.NextType;
        _resendLeft = info.Timeout;
        RefreshLocalizedTexts();

        if (_nextCodeType is not null && _resendLeft > 0)
            _resendTimer.Start();
    }

    private void TickResend()
    {
        if (_resendLeft > 0)
            _resendLeft--;
        if (_resendLeft <= 0)
            _resendTimer.Stop();
        UpdateResendText();
    }

    private void UpdateResendText()
    {
        CanResend = _nextCodeType is not null && _resendLeft <= 0;
        ResendText = _nextCodeType is null
            ? string.Empty
            : _resendLeft > 0
                ? Loc.Instance.Format("Login_ResendIn", TimeSpan.FromSeconds(_resendLeft).ToString(@"m\:ss", CultureInfo.InvariantCulture))
                : Loc.Instance[_nextCodeType switch
                {
                    TdApi.AuthenticationCodeType.AuthenticationCodeTypeSms => "Login_ResendSms",
                    TdApi.AuthenticationCodeType.AuthenticationCodeTypeCall => "Login_ResendCall",
                    _ => "Login_Resend",
                }];
    }

    private void RefreshLocalizedTexts()
    {
        if (_codeInfo is { } info)
        {
            var phone = info.PhoneNumber ?? string.Empty;
            CodeDelivery = info.Type switch
            {
                TdApi.AuthenticationCodeType.AuthenticationCodeTypeTelegramMessage => Loc.Instance["Login_CodeViaApp"],
                TdApi.AuthenticationCodeType.AuthenticationCodeTypeSms => Loc.Instance.Format("Login_CodeViaSms", phone),
                TdApi.AuthenticationCodeType.AuthenticationCodeTypeCall => Loc.Instance.Format("Login_CodeViaCall", phone),
                TdApi.AuthenticationCodeType.AuthenticationCodeTypeFlashCall or
                    TdApi.AuthenticationCodeType.AuthenticationCodeTypeMissedCall => Loc.Instance.Format("Login_CodeViaFlashCall", phone),
                TdApi.AuthenticationCodeType.AuthenticationCodeTypeFragment => Loc.Instance.Format("Login_CodeViaFragment", phone),
                _ => Loc.Instance.Format("Login_CodeViaOther", phone),
            };
            UpdateResendText();
        }

        if (_emailCodeInfo is { } email)
            EmailCodeSubtitle = Loc.Instance.Format("Login_EmailCodeSubtitle", email.EmailAddressPattern ?? string.Empty);
    }

    private async Task RestartSessionAsync()
    {
        if (_session is not null)
            _session.Auth.StateChanged -= OnAuthStateChanged;

        await _accounts.CancelAddAccountAsync();
        _session = await _accounts.BeginAddAccountAsync();
        _session.Auth.StateChanged += OnAuthStateChanged;
        Panels.AttachSession(_session);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (_session.AuthorizationState is not TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber && DateTime.UtcNow < deadline)
            await Task.Delay(100);
    }

    /// <summary>Выполняет шаг входа: блокирует кнопки, показывает ошибку понятным текстом.</summary>
    private async Task RunAsync(Func<Task> action, bool showBusy = true, Action? onError = null)
    {
        if (showBusy)
            IsBusy = true;
        ErrorText = null;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowError(ex);
            onError?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowError(Exception ex)
    {
        ErrorText = ex switch
        {
            AuthException { Kind: AuthErrorKind.TooManyRequests, RetryAfter: { } wait } =>
                Loc.Instance.Format("Error_TooManyRequests", FormatWait(wait)),
            AuthException { Kind: AuthErrorKind.TooManyRequests } => Loc.Instance["Error_TooManyRequestsLater"],
            AuthException { Kind: AuthErrorKind.Unknown } auth => Loc.Instance.Format("Error_Unknown", auth.RawMessage),
            AuthException auth => Loc.Instance[$"Error_{auth.Kind}"],
            _ => Loc.Instance.Format("Error_Unknown", ex.Message),
        };
    }

    private static string FormatWait(TimeSpan wait) =>
        wait.TotalHours >= 1 ? wait.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : wait.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        _resendTimer.Stop();
        if (_session is not null)
            _session.Auth.StateChanged -= OnAuthStateChanged;
        await Task.CompletedTask;
    }
}
