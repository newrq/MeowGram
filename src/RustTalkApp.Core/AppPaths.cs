namespace RustTalkApp.Core;

/// <summary>
/// Пути к пользовательским данным приложения (база, сессия Telegram, кэш медиа).
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

    public string DatabasePath => Path.Combine(RootDirectory, "rusttalk.db");

    public string TelegramSessionPath => Path.Combine(RootDirectory, "telegram.session");

    public string MediaCacheDirectory => Path.Combine(RootDirectory, "media");
}
