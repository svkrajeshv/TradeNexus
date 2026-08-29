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

        // DPAPI CurrentUser only works on Windows when a real user profile is loaded.
        // On Azure App Service (and other hosted Windows) the worker runs without a
        // loaded profile, so ProtectedData.Protect throws "data protection operation
        // was unsuccessful". DPAPI ciphertext is also machine/user-bound and cannot be
        // decrypted after a restart or on another instance. In those environments use
        // the portable AES scheme instead.
        if (OperatingSystem.IsWindows() && CanUseDpapi())
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(plaintext);
                var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(enc);
            }
            catch (CryptographicException)
            {
                // Profile/impersonation issue at runtime — fall back to AES.
            }
        }

        var key = DeriveKey(passphrase ?? DefaultPassphrase);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var enc2 = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = enc2.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
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
                aes.Key = DeriveKey(passphrase ?? DefaultPassphrase);
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
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), Encoding.UTF8.GetBytes("trading-app-salt-v1"), 100_000, HashAlgorithmName.SHA256, 32);
    }

    /// <summary>
    /// The passphrase used for the portable AES scheme. Prefer an app setting /
    /// environment variable (<c>NEXUSAPP_CRED_KEY</c>) so ciphertext stays decryptable
    /// across restarts and instances; falls back to a fixed default for local dev.
    /// </summary>
    private static string DefaultPassphrase =>
        Environment.GetEnvironmentVariable("NEXUSAPP_CRED_KEY") is { Length: > 0 } key
            ? key
            : "trading-app-default";

    /// <summary>
    /// DPAPI CurrentUser requires a loaded Windows user profile. On Azure App Service
    /// the worker typically runs without one, so we avoid DPAPI there and use AES.
    /// </summary>
    private static bool CanUseDpapi()
    {
        // Azure App Service / Functions set these; when present, skip DPAPI.
        var isAppService =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
        return !isAppService;
    }
}
