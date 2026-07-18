using System.Security.Cryptography;
using System.Text;

namespace NexusApp.Infrastructure;

/// <summary>
/// Lightweight symmetric protection for credentials stored at rest.
/// Uses Windows DPAPI when running on Windows so the ciphertext can only be
/// decrypted by the same user/machine; falls back to AES with a key derived
/// from a configured passphrase on other platforms.
/// </summary>
public static class CredentialProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string AesPrefix = "aes:";

    /// <summary>
    /// Encrypts a plaintext credential. Returns a self-describing string that
    /// can be safely persisted and later handed to <see cref="Unprotect"/>.
    /// </summary>
    public static string Protect(string plaintext, string? passphrase = null)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;

        if (OperatingSystem.IsWindows())
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(enc);
        }

        var key = DeriveKey(passphrase ?? "trading-app-default");
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var enc2 = aes.CreateEncryptor();
        var cipher = enc2.TransformFinalBlock(Encoding.UTF8.GetBytes(plaintext), 0, plaintext.Length);
        var combined = new byte[aes.IV.Length + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
        Buffer.BlockCopy(cipher, 0, combined, aes.IV.Length, cipher.Length);
        return AesPrefix + Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Decrypts a value previously produced by <see cref="Protect"/>. Returns the
    /// input unchanged if it doesn't look protected, so we degrade gracefully
    /// during first-run/development.
    /// </summary>
    public static string Unprotect(string cipher, string? passphrase = null)
    {
        if (string.IsNullOrEmpty(cipher))
            return string.Empty;

        try
        {
            if (cipher.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("DPAPI ciphertext requires Windows to decrypt.");
                var bytes = Convert.FromBase64String(cipher[DpapiPrefix.Length..]);
                var dec = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }

            if (cipher.StartsWith(AesPrefix, StringComparison.Ordinal))
            {
                var combined = Convert.FromBase64String(cipher[AesPrefix.Length..]);
                using var aes = Aes.Create();
                aes.Key = DeriveKey(passphrase ?? "trading-app-default");
                var iv = new byte[16];
                Buffer.BlockCopy(combined, 0, iv, 0, iv.Length);
                aes.IV = iv;
                using var dec = aes.CreateDecryptor();
                var plain = dec.TransformFinalBlock(combined, iv.Length, combined.Length - iv.Length);
                return Encoding.UTF8.GetString(plain);
            }
        }
        catch
        {
            // Fall through to raw return so consumers can decide how to react.
        }

        return cipher;
    }

    private static byte[] DeriveKey(string passphrase)
    {
        using var derive = new Rfc2898DeriveBytes(passphrase, Encoding.UTF8.GetBytes("trading-app-salt-v1"), 100_000, HashAlgorithmName.SHA256);
        return derive.GetBytes(32);
    }
}
