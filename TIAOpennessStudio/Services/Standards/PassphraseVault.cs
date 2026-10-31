using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;

namespace TIAOpennessStudio.Services.Standards
{
    /// <summary>
    /// Encrypts knowledge-base passphrases with DPAPI (current user account) so libraries auto-unlock
    /// on restart. Ciphertext is stored in %LocalAppData%\TIAOpennessStudio\Secrets\passphrases.dat
    /// (not in the base) and cannot be decrypted by another user or machine.
    /// </summary>
    public sealed class PassphraseVault
    {
        // additional entropy: binds the ciphertext to this app.
        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("TIA-STD-KB-VAULT-v1");

        private readonly string _path;
        private readonly object _sync = new object();
        private Dictionary<string, string> _store; // mountId -> base64(DPAPI ciphertext)

        public PassphraseVault(string appBaseDirectory)
        {
            var baseDir = string.IsNullOrWhiteSpace(appBaseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory : appBaseDirectory;
            var dir = Path.Combine(baseDir, "Secrets");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "passphrases.dat");
            Load();
        }

        /// <summary>Remembers a passphrase (DPAPI-encrypted and persisted).</summary>
        public void Save(string mountId, string passphrase)
        {
            if (string.IsNullOrEmpty(mountId)) return;
            try
            {
                var cipher = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(passphrase ?? string.Empty),
                    Entropy, DataProtectionScope.CurrentUser);
                lock (_sync)
                {
                    _store[mountId] = Convert.ToBase64String(cipher);
                    Persist();
                }
            }
            catch { /* if DPAPI fails, don't remember; it must not break the main flow */ }
        }

        /// <summary>Retrieves a passphrase; returns null when absent or on failure.</summary>
        public string TryGet(string mountId)
        {
            if (string.IsNullOrEmpty(mountId)) return null;
            string b64;
            lock (_sync)
            {
                if (!_store.TryGetValue(mountId, out b64)) return null;
            }
            try
            {
                var plain = ProtectedData.Unprotect(
                    Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return null;
            }
        }

        public bool Has(string mountId)
        {
            if (string.IsNullOrEmpty(mountId)) return false;
            lock (_sync) return _store.ContainsKey(mountId);
        }

        /// <summary>Forgets a library's passphrase.</summary>
        public void Remove(string mountId)
        {
            if (string.IsNullOrEmpty(mountId)) return;
            lock (_sync)
            {
                if (_store.Remove(mountId)) Persist();
            }
        }

        private void Load()
        {
            lock (_sync)
            {
                _store = new Dictionary<string, string>();
                try
                {
                    if (File.Exists(_path))
                    {
                        var json = File.ReadAllText(_path);
                        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        if (d != null) _store = d;
                    }
                }
                catch { /* corrupt file: start empty */ }
            }
        }

        private void Persist()
        {
            try
            {
                var json = JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
            catch { /* best effort */ }
        }
    }
}
