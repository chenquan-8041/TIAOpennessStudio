using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace TIAOpennessStudio.Services.Standards
{
    /// <summary>A single coding-standard rule.</summary>
    public sealed class StandardRule
    {
        public string RuleId { get; set; }
        public string Category { get; set; }   // naming | addressing | block number | comment | safety ...
        public string Scope { get; set; }      // FB | FC | DB | OB | Tag | global
        public string Title { get; set; }
        public string Content { get; set; }    // rule body (the core asset)
        public string Examples { get; set; }
    }

    /// <summary>
    /// Encrypted read-only knowledge base. Container format standards.kb:
    /// [4B "TKB1"][16B IV][32B HMAC][AES-256-CBC ciphertext].
    /// At runtime it verifies HMAC, decrypts in memory, opens a read-only temp file and deletes it
    /// after use. Only search is provided; no export.
    /// </summary>
    public sealed class StandardsKnowledgeStore : IDisposable
    {
        private static readonly byte[] Magic = { 0x54, 0x4B, 0x42, 0x31 }; // "TKB1"
        private const int IvSize = 16;
        private const int MacSize = 32;

        /// <summary>Maximum number of rules returned by a single search.</summary>
        public const int MaxTopK = 8;

        private readonly string _kbPath;
        private readonly IStandardsSecretProvider _secret;
        private readonly bool _bindToMachine;
        private string _tempDbPath;
        private string _readConnectionString;
        private readonly object _sync = new object();
        private bool _disposed;

        /// <summary>Display name of the library (used for UI and search-result provenance).</summary>
        public string DisplayName { get; set; }

        /// <summary>Official library: AppBase/Knowledge/standards.kb with the built-in default key.</summary>
        public StandardsKnowledgeStore(string appBaseDirectory)
        {
            var baseDir = string.IsNullOrWhiteSpace(appBaseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : appBaseDirectory;
            _kbPath = Path.Combine(baseDir, "Knowledge", "standards.kb");
            _secret = new OfficialSecretProvider();
            DisplayName = "默认知识库";
        }

        /// <summary>General constructor: given kb path and secret source (for user-mounted libraries). bindToMachine=true merges a machine fingerprint so a correct passphrase cannot decrypt elsewhere.</summary>
        public StandardsKnowledgeStore(string kbFilePath, IStandardsSecretProvider secretProvider, string displayName, bool bindToMachine = false)
        {
            _kbPath = kbFilePath ?? throw new ArgumentNullException(nameof(kbFilePath));
            _secret = secretProvider ?? new NullSecretProvider();
            _bindToMachine = bindToMachine;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(kbFilePath) : displayName;
        }

        public string KbPath => _kbPath;
        public bool IsAvailable => File.Exists(_kbPath);

        /// <summary>Searches rules and returns the top <paramref name="topK"/> (clamped to [1, MaxTopK]).</summary>
        public IReadOnlyList<StandardRule> SearchRules(string query, int topK = 3)
        {
            EnsureDecrypted();
            if (topK < 1) topK = 1;
            if (topK > MaxTopK) topK = MaxTopK;

            var results = new List<StandardRule>();
            var q = (query ?? string.Empty).Trim();
            if (q.Length == 0) return results;

            var like = "%" + q + "%";
            using (var conn = new SqliteConnection(_readConnectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    // relevance: title > keywords > body
                    cmd.CommandText =
                        "SELECT rule_id, category, scope, title, content, examples FROM standard_rule " +
                        "WHERE title LIKE $q OR keywords LIKE $q OR content LIKE $q " +
                        "ORDER BY (CASE WHEN title LIKE $q THEN 0 WHEN keywords LIKE $q THEN 1 ELSE 2 END) " +
                        "LIMIT $k;";
                    cmd.Parameters.AddWithValue("$q", like);
                    cmd.Parameters.AddWithValue("$k", topK);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            results.Add(ReadRule(r));
                    }
                }
            }
            return results;
        }

        /// <summary>Lists every rule in the library (for the central tab). Ordered by category then title.</summary>
        public IReadOnlyList<StandardRule> GetAllRules()
        {
            EnsureDecrypted();
            var results = new List<StandardRule>();
            using (var conn = new SqliteConnection(_readConnectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT rule_id, category, scope, title, content, examples FROM standard_rule " +
                        "ORDER BY category, title;";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            results.Add(ReadRule(r));
                    }
                }
            }
            return results;
        }

        /// <summary>Returns the single rule with the given id.</summary>
        public StandardRule GetRuleById(string ruleId)
        {
            if (string.IsNullOrWhiteSpace(ruleId)) return null;
            EnsureDecrypted();
            using (var conn = new SqliteConnection(_readConnectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT rule_id, category, scope, title, content, examples FROM standard_rule WHERE rule_id=$id LIMIT 1;";
                    cmd.Parameters.AddWithValue("$id", ruleId);
                    using (var r = cmd.ExecuteReader())
                        return r.Read() ? ReadRule(r) : null;
                }
            }
        }

        private static StandardRule ReadRule(SqliteDataReader r) => new StandardRule
        {
            RuleId = r.IsDBNull(0) ? null : r.GetString(0),
            Category = r.IsDBNull(1) ? null : r.GetString(1),
            Scope = r.IsDBNull(2) ? null : r.GetString(2),
            Title = r.IsDBNull(3) ? null : r.GetString(3),
            Content = r.IsDBNull(4) ? null : r.GetString(4),
            Examples = r.IsDBNull(5) ? null : r.GetString(5),
        };

        /// <summary>Lazy decryption: on first search, decrypt to a temporary SQLite file opened read-only.</summary>
        private void EnsureDecrypted()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StandardsKnowledgeStore));
            if (_readConnectionString != null) return;

            lock (_sync)
            {
                if (_readConnectionString != null) return;
                if (!File.Exists(_kbPath))
                    throw new FileNotFoundException("Standards knowledge base not found.", _kbPath);

                var plaintext = DecryptContainer(File.ReadAllBytes(_kbPath), _secret?.GetSecretSalt(), _bindToMachine);
                try
                {
                    _tempDbPath = Path.Combine(Path.GetTempPath(), "tia_std_" + Guid.NewGuid().ToString("N") + ".db");
                    File.WriteAllBytes(_tempDbPath, plaintext);
                }
                finally
                {
                    Array.Clear(plaintext, 0, plaintext.Length);
                }

                _readConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = _tempDbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Private
                }.ToString();
            }
        }

        private static byte[] DecryptContainer(byte[] container, byte[] secretSalt, bool bindToMachine)
        {
            if (container.Length < Magic.Length + IvSize + MacSize)
                throw new InvalidDataException("Corrupt standards.kb: too short.");
            for (int i = 0; i < Magic.Length; i++)
                if (container[i] != Magic[i])
                    throw new InvalidDataException("Corrupt standards.kb: bad magic.");

            int offset = Magic.Length;
            var iv = new byte[IvSize];
            Buffer.BlockCopy(container, offset, iv, 0, IvSize); offset += IvSize;
            var mac = new byte[MacSize];
            Buffer.BlockCopy(container, offset, mac, 0, MacSize); offset += MacSize;
            int cipherLen = container.Length - offset;
            var cipher = new byte[cipherLen];
            Buffer.BlockCopy(container, offset, cipher, 0, cipherLen);

            StandardsKeyProvider.DeriveKeys(secretSalt, bindToMachine, out var aesKey, out var hmacKey);
            byte[] plaintext = null;
            try
            {
                // verify HMAC first (over magic+IV+ciphertext); reject on wrong key
                using (var hmac = new HMACSHA256(hmacKey))
                {
                    var macInput = new byte[Magic.Length + IvSize + cipherLen];
                    Buffer.BlockCopy(Magic, 0, macInput, 0, Magic.Length);
                    Buffer.BlockCopy(iv, 0, macInput, Magic.Length, IvSize);
                    Buffer.BlockCopy(cipher, 0, macInput, Magic.Length + IvSize, cipherLen);
                    var computed = hmac.ComputeHash(macInput);
                    Array.Clear(macInput, 0, macInput.Length);
                    if (!FixedTimeEquals(computed, mac))
                        throw new InvalidDataException(
                            "Failed to verify standards.kb. The knowledge base is bound to the licensed machine.");
                }

                // only decrypt once the MAC passes
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = aesKey;
                    aes.IV = iv;
                    using (var dec = aes.CreateDecryptor())
                        plaintext = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                }
                return plaintext;
            }
            catch (CryptographicException)
            {
                if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length);
                throw new InvalidDataException(
                    "Failed to decrypt standards.kb. The knowledge base is bound to the licensed machine.");
            }
            finally
            {
                Array.Clear(aesKey, 0, aesKey.Length);
                Array.Clear(hmacKey, 0, hmacKey.Length);
            }
        }

        /// <summary>Constant-time comparison to prevent timing side channels.</summary>
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SqliteConnection.ClearAllPools(); // release the temp-file handle so it can be deleted
            try
            {
                if (_tempDbPath != null && File.Exists(_tempDbPath))
                    File.Delete(_tempDbPath);
            }
            catch { /* best-effort delete; if held, the OS temp cleanup will reclaim it later */ }
        }
    }
}
