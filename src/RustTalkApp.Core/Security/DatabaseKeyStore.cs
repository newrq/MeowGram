using System.Security.Cryptography;

namespace RustTalkApp.Core.Security;

/// <summary>
/// Хранит случайный ключ шифрования базы TDLib.
/// Windows — DPAPI (ключ привязан к учётной записи пользователя).
/// macOS/Linux — файл с правами только для владельца (TODO: Keychain / libsecret).
/// </summary>
public sealed class DatabaseKeyStore(string keyPath)
{
    private const int KeySize = 32;
    private static readonly byte[] Entropy = "RustTalkApp.TdlibDatabaseKey"u8.ToArray();

    public byte[] GetOrCreateKey()
    {
        if (File.Exists(keyPath))
            return Unprotect(File.ReadAllBytes(keyPath));

        var key = RandomNumberGenerator.GetBytes(KeySize);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllBytes(keyPath, Protect(key));

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return key;
    }

    private static byte[] Protect(byte[] key) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser)
        : key;

    private static byte[] Unprotect(byte[] stored) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser)
        : stored;
}
