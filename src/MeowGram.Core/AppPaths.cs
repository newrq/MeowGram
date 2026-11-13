using System.Text.RegularExpressions;

namespace MeowGram.Core;

/// <summary>
/// Пути к пользовательским данным приложения: наша БД, общий лог TDLib и папки аккаунтов.
/// </summary>
public sealed partial class AppPaths
{
    public AppPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        Directory.CreateDirectory(RootDirectory);
    }

    /// <summary>По умолчанию: %LocalAppData%/MeowGram (Windows), ~/.local/share/MeowGram (Linux), ~/Library/Application Support/MeowGram (macOS).</summary>
    public static AppPaths Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "MeowGram"));

    public string RootDirectory { get; }

    /// <summary>Наша БД: аккаунты, прокси, настройки кастомизации (чаты и сообщения хранит TDLib).</summary>
    public string DatabasePath => Path.Combine(RootDirectory, "meowgram.db");

    /// <summary>Лог TDLib общий на процесс (так устроен TDLib).</summary>
    public string TdlibLogPath => Path.Combine(RootDirectory, "logs", "tdlib.log");

    public string AccountsDirectory => Path.Combine(RootDirectory, "accounts");

    /// <summary>Папка аккаунта. <paramref name="accountId"/> — 32 hex-символа (защита от выхода за пределы папки).</summary>
    public AccountPaths ForAccount(string accountId)
    {
        if (!AccountIdPattern().IsMatch(accountId))
            throw new ArgumentException($"Invalid account id '{accountId}'.", nameof(accountId));

        return new AccountPaths(Path.Combine(AccountsDirectory, accountId));
    }

    public static string NewAccountId() => Guid.NewGuid().ToString("N");

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex AccountIdPattern();
}

/// <summary>Данные TDLib одного аккаунта.</summary>
public sealed record AccountPaths(string RootDirectory)
{
    /// <summary>База TDLib (зашифрована ключом из <see cref="TdlibKeyPath"/>).</summary>
    public string TdlibDatabaseDirectory => Path.Combine(RootDirectory, "tdlib", "db");

    /// <summary>Скачанные TDLib файлы и медиа.</summary>
    public string TdlibFilesDirectory => Path.Combine(RootDirectory, "tdlib", "files");

    /// <summary>Ключ шифрования базы TDLib, защищённый средствами ОС.</summary>
    public string TdlibKeyPath => Path.Combine(RootDirectory, "tdlib", "db.key");
}
