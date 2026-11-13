namespace MeowGram.ViewModels.Login;

/// <summary>
/// Шаг экрана входа. По типу шага карточка выбирает вид (DataTemplate), смена шага — анимированный переход.
/// Все данные шагов — в <see cref="Login"/>.
/// </summary>
public abstract record LoginStep(LoginViewModel Login)
{
    /// <summary>Порядок для направления перехода: вперёд — больше.</summary>
    public abstract int Order { get; }
}

public sealed record NotConfiguredStep(LoginViewModel Login) : LoginStep(Login)
{
    public override int Order => 0;
}

public sealed record QrStep(LoginViewModel Login) : LoginStep(Login)
{
    public override int Order => 1;
}

public sealed record PhoneStep(LoginViewModel Login) : LoginStep(Login)
{
    public override int Order => 2;
}

public sealed record CodeStep(LoginViewModel Login) : LoginStep(Login)
{
    public override int Order => 3;
}

public sealed record PasswordStep(LoginViewModel Login) : LoginStep(Login)
{
    public override int Order => 4;
}

public sealed record PasswordRecoveryStep(LoginViewModel Login) : LoginStep(Login)
{
    public override int Order => 5;
}

public sealed record EmailStep(LoginViewModel Login) : LoginStep(Login)
{
    public override int Order => 3;
}

public sealed record EmailCodeStep(LoginViewModel Login) : LoginStep(Login)
{
    public override int Order => 4;
}

public sealed record RegistrationStep(LoginViewModel Login) : LoginStep(Login)
{
    public override int Order => 4;
}

public sealed record PremiumStep(LoginViewModel Login) : LoginStep(Login)
{
    public override int Order => 5;
}
