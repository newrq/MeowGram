namespace RustTalkApp.Core;

/// <summary>
/// Пути к пользовательским данным приложения: наша БД и данные TDLib.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        Directory.CreateDirectory(RootDirectory);
    }

    /// <summary>По умолчанию: %LocalAppData%/RustTalkApp (Windows), ~/.local/share/RustTalkApp (Linux), ~/Library/Application Support/RustTalkApp (macOS).</summary>
    public static AppPaths Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "RustTalkApp"));

    public string RootDirectory { get; }

    /// <summary>Наша БД: настройки кастомизации и собственные данные клиента (чаты и сообщения хранит TDLib).</summary>
    public string DatabasePath => Path.Combine(RootDirectory, "rusttalk.db");

    /// <summary>База TDLib (зашифрована ключом из <see cref="TdlibKeyPath"/>).</summary>
    public string TdlibDatabaseDirectory => Path.Combine(RootDirectory, "tdlib", "db");

    /// <summary>Скачанные TDLib файлы и медиа.</summary>
    public string TdlibFilesDirectory => Path.Combine(RootDirectory, "tdlib", "files");

    /// <summary>Ключ шифрования базы TDLib, защищённый средствами ОС.</summary>
    public string TdlibKeyPath => Path.Combine(RootDirectory, "tdlib", "db.key");

    public string TdlibLogPath => Path.Combine(RootDirectory, "logs", "tdlib.log");
}
