using System.Security.Cryptography;
using System.Text;

namespace MiMi.App.AI;

/// <summary>Encrypts API keys for the current Windows user. Ciphertext can be backed up,
/// but it cannot be decrypted by another user or computer without their own DPAPI key.</summary>
public static class AiApiKeyProtector
{
    private static readonly byte[] Entropy = "MiMi.AI.ApiKey.v1"u8.ToArray();

    public static string Protect(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "";
        var plaintext = Encoding.UTF8.GetBytes(apiKey.Trim());
        return Convert.ToBase64String(ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser));
    }

    public static string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrWhiteSpace(ciphertext)) return null;
        try
        {
            var plaintext = ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), Entropy,
                DataProtectionScope.CurrentUser);
            var value = Encoding.UTF8.GetString(plaintext);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or DecoderFallbackException)
        {
            // A settings file copied from another machine is expected to fail here.
            return null;
        }
    }
}
