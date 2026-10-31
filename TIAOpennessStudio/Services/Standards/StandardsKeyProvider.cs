using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace TIAOpennessStudio.Services.Standards
{
    /// <summary>Knowledge-base key derivation: PBKDF2 derives AES/HMAC keys from a secret salt (externally injected) plus an optional machine fingerprint.</summary>
    public static class StandardsKeyProvider
    {
        /// <summary>Environment variable name for the secret salt.</summary>
        public const string SecretEnvVar = "TIA_STD_KB_SECRET";

        /// <summary>Whether to fold the machine fingerprint into the key (true = bound to this machine to prevent copying, false = portable). Default false.</summary>
        public static bool BindToMachine { get; set; } = false;

        private const int Pbkdf2Iterations = 120_000;
        public const int AesKeyBytes = 32;   // AES-256
        public const int HmacKeyBytes = 32;  // HMAC-SHA256
        private const int TotalKeyBytes = AesKeyBytes + HmacKeyBytes;

        // public fallback salt when no external secret is available (not secret).
        private static readonly byte[] PublicFallbackSalt =
            Encoding.ASCII.GetBytes("TIA-STD-KB-PUBLIC-FALLBACK-SALT-v1");

        /// <summary>Whether the last derivation used the weak mode (no external secret).</summary>
        public static bool LastDeriveUsedWeakMode { get; private set; }

        /// <summary>Derives keys from a given secret salt; a null/empty salt falls back internally. Uses the global BindToMachine (compat with older callers).</summary>
        public static void DeriveKeys(byte[] secretSalt, out byte[] aesKey, out byte[] hmacKey)
            => DeriveKeys(secretSalt, BindToMachine, out aesKey, out hmacKey);

        /// <summary>Derives keys from a given secret salt with an explicit machine-binding choice (thread-safe, does not mutate global state).</summary>
        public static void DeriveKeys(byte[] secretSalt, bool bindToMachine, out byte[] aesKey, out byte[] hmacKey)
        {
            byte[] salt;
            if (secretSalt != null && secretSalt.Length > 0)
            {
                using (var sha = SHA256.Create())
                    salt = sha.ComputeHash(secretSalt);
                LastDeriveUsedWeakMode = false;
            }
            else
            {
                salt = LoadSecretSalt(out bool weak);
                LastDeriveUsedWeakMode = weak;
            }

            var passwordBytes = ComposeKeyMaterial(bindToMachine);
            try
            {
                using (var kdf = new Rfc2898DeriveBytes(passwordBytes, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256))
                {
                    var all = kdf.GetBytes(TotalKeyBytes);
                    aesKey = new byte[AesKeyBytes];
                    hmacKey = new byte[HmacKeyBytes];
                    Buffer.BlockCopy(all, 0, aesKey, 0, AesKeyBytes);
                    Buffer.BlockCopy(all, AesKeyBytes, hmacKey, 0, HmacKeyBytes);
                    Array.Clear(all, 0, all.Length);
                }
            }
            finally
            {
                Array.Clear(passwordBytes, 0, passwordBytes.Length);
                Array.Clear(salt, 0, salt.Length);
            }
        }

        /// <summary>Derives keys with the internal fallback salt (first 32B = AES-256, next 32B = HMAC-SHA256).</summary>
        public static void DeriveKeys(out byte[] aesKey, out byte[] hmacKey)
        {
            var salt = LoadSecretSalt(out bool weak);
            LastDeriveUsedWeakMode = weak;

            var passwordBytes = ComposeKeyMaterial();
            try
            {
                using (var kdf = new Rfc2898DeriveBytes(passwordBytes, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256))
                {
                    var all = kdf.GetBytes(TotalKeyBytes);
                    aesKey = new byte[AesKeyBytes];
                    hmacKey = new byte[HmacKeyBytes];
                    Buffer.BlockCopy(all, 0, aesKey, 0, AesKeyBytes);
                    Buffer.BlockCopy(all, AesKeyBytes, hmacKey, 0, HmacKeyBytes);
                    Array.Clear(all, 0, all.Length);
                }
            }
            finally
            {
                Array.Clear(passwordBytes, 0, passwordBytes.Length);
                Array.Clear(salt, 0, salt.Length);
            }
        }

        /// <summary>Reads the external secret salt: environment variable → local key file → public fallback salt (weak=true).</summary>
        private static byte[] LoadSecretSalt(out bool weak)
        {
            var env = Environment.GetEnvironmentVariable(SecretEnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                weak = false;
                return DeriveSaltFromSecretString(env);
            }

            var file = GetSecretFilePath();
            try
            {
                if (File.Exists(file))
                {
                    var bytes = File.ReadAllBytes(file);
                    if (bytes != null && bytes.Length > 0)
                    {
                        weak = false;
                        using (var sha = SHA256.Create())
                            return sha.ComputeHash(bytes);
                    }
                }
            }
            catch { }

            weak = true;
            using (var sha = SHA256.Create())
                return sha.ComputeHash(PublicFallbackSalt);
        }

        private static byte[] DeriveSaltFromSecretString(string secret)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes(secret));
        }

        /// <summary>Local key-file path (kept out of version control).</summary>
        public static string GetSecretFilePath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TIAOpennessStudio", "Secrets");
            return Path.Combine(dir, "kb.secret");
        }

        /// <summary>Key material (uses the global BindToMachine).</summary>
        private static byte[] ComposeKeyMaterial() => ComposeKeyMaterial(BindToMachine);

        /// <summary>Key material; bindToMachine=true folds in the machine fingerprint.</summary>
        private static byte[] ComposeKeyMaterial(bool bindToMachine)
        {
            var sb = new StringBuilder();
            sb.Append("kb::");
            if (bindToMachine)
                sb.Append(GetMachineFingerprint());
            else
                sb.Append("no-machine-binding");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>Machine fingerprint: SHA256 over CPU/motherboard/BIOS serials; falls back to the machine name when WMI is unavailable.</summary>
        private static string GetMachineFingerprint()
        {
            var parts = new StringBuilder();
            parts.Append(SafeWmi("SELECT ProcessorId FROM Win32_Processor", "ProcessorId"));
            parts.Append('|');
            parts.Append(SafeWmi("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber"));
            parts.Append('|');
            parts.Append(SafeWmi("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber"));

            var raw = parts.ToString();
            if (string.IsNullOrWhiteSpace(raw.Replace("|", "")))
            {
                // extreme fallback: all WMI lookups failed, use the machine name (much weaker binding, availability only).
                raw = Environment.MachineName + "|fallback";
            }

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return Convert.ToBase64String(hash);
            }
        }

        private static string SafeWmi(string query, string property)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    foreach (var obj in searcher.Get().OfType<ManagementObject>())
                    {
                        var val = obj[property]?.ToString();
                        if (!string.IsNullOrWhiteSpace(val))
                            return val.Trim();
                    }
                }
            }
            catch
            {
                // degrade silently when WMI is unavailable; don't throw.
            }
            return string.Empty;
        }
    }
}
