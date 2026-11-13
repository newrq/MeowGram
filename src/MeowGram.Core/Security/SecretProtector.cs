using System.Security.Cryptography;
using System.Text;

namespace MeowGram.Core.Security;

/// <summary>
/// Защищает короткие секреты (пароль/секрет прокси) перед записью в нашу БД.
/// Windows — DPAPI (CurrentUser). macOS/Linux — пока только base64 (TODO: Keychain / libsecret).
/// </summary>
public sealed class SecretProtector
{
    private static readonly byte[] Entropy = "MeowGram.Secrets"u8.ToArray();

    public string? Protect(string? plain)
    {
        if (plain is null)
            return null;

        var bytes = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);

        return Convert.ToBase64String(bytes);
    }

    public string? Unprotect(string? stored)
    {
        if (stored is null)
            return null;

        var bytes = Convert.FromBase64String(stored);
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);

        return Encoding.UTF8.GetString(bytes);
    }
}
