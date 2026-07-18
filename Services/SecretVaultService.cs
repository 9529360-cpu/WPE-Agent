using System;
using System.Security.Cryptography;
using System.Text;

namespace 币安量化机器人.Services;

public static class SecretVaultService
{
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Decrypt(string cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher))
            return string.Empty;

        var encrypted = Convert.FromBase64String(cipher);
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
