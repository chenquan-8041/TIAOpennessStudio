using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace TIAOpennessStudio.Services.Standards
{
    /// <summary>Packages a rule set into an encrypted standards.kb (offline/first-run only, not on the hot path).</summary>
    public static class StandardsPackager
    {
        private static readonly byte[] Magic = { 0x54, 0x4B, 0x42, 0x31 }; // "TKB1"
        private const int IvSize = 16;

        /// <summary>Packages with the internal fallback key.</summary>
        public static void Build(IEnumerable<StandardRule> rules, string outputKbPath)
            => Build(rules, outputKbPath, null);

        /// <summary>Packages with a given secret source (PassphraseSecretProvider for passphrase libraries, OfficialSecretProvider for official ones).
        /// bindToMachine=true merges a machine fingerprint so the output cannot be decrypted elsewhere even with a correct passphrase.</summary>
        public static void Build(IEnumerable<StandardRule> rules, string outputKbPath, IStandardsSecretProvider secretProvider, bool bindToMachine = false)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (string.IsNullOrWhiteSpace(outputKbPath)) throw new ArgumentException("outputKbPath required");

            var secretSalt = secretProvider?.GetSecretSalt();
            var tempDb = Path.Combine(Path.GetTempPath(), "tia_std_build_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                BuildPlaintextDb(rules, tempDb);
                SqliteConnection.ClearAllPools(); // release the temp-file handle
                var plaintext = File.ReadAllBytes(tempDb);
                try
                {
                    var container = EncryptContainer(plaintext, secretSalt, bindToMachine);
                    var dir = Path.GetDirectoryName(outputKbPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(outputKbPath, container);
                }
                finally
                {
                    Array.Clear(plaintext, 0, plaintext.Length);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { /* best effort */ }
            }
        }

        private static void BuildPlaintextDb(IEnumerable<StandardRule> rules, string dbPath)
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
            using (var conn = new SqliteConnection(cs))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "CREATE TABLE IF NOT EXISTS standard_rule (" +
                        "rule_id TEXT PRIMARY KEY," +
                        "category TEXT," +
                        "scope TEXT," +
                        "title TEXT NOT NULL," +
                        "content TEXT," +
                        "examples TEXT," +
                        "keywords TEXT);";
                    cmd.ExecuteNonQuery();
                }

                using (var tx = conn.BeginTransaction())
                {
                    foreach (var rule in rules)
                    {
                        if (rule == null || string.IsNullOrWhiteSpace(rule.Title)) continue;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText =
                                "INSERT OR REPLACE INTO standard_rule (rule_id, category, scope, title, content, examples, keywords) " +
                                "VALUES ($id,$cat,$scope,$title,$content,$ex,$kw);";
                            cmd.Parameters.AddWithValue("$id", (object)rule.RuleId ?? Guid.NewGuid().ToString("N"));
                            cmd.Parameters.AddWithValue("$cat", (object)rule.Category ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("$scope", (object)rule.Scope ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("$title", rule.Title);
                            cmd.Parameters.AddWithValue("$content", (object)rule.Content ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("$ex", (object)rule.Examples ?? DBNull.Value);
                            // keywords: title + category + scope, to improve search hits
                            var kw = string.Join(" ", rule.Title, rule.Category, rule.Scope);
                            cmd.Parameters.AddWithValue("$kw", kw);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        private static byte[] EncryptContainer(byte[] plaintext, byte[] secretSalt, bool bindToMachine)
        {
            StandardsKeyProvider.DeriveKeys(secretSalt, bindToMachine, out var aesKey, out var hmacKey);
            try
            {
                byte[] iv, cipher;
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = aesKey;
                    aes.GenerateIV();
                    iv = aes.IV;
                    using (var enc = aes.CreateEncryptor())
                        cipher = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
                }

                byte[] mac;
                using (var hmac = new HMACSHA256(hmacKey))
                {
                    var macInput = new byte[Magic.Length + IvSize + cipher.Length];
                    Buffer.BlockCopy(Magic, 0, macInput, 0, Magic.Length);
                    Buffer.BlockCopy(iv, 0, macInput, Magic.Length, IvSize);
                    Buffer.BlockCopy(cipher, 0, macInput, Magic.Length + IvSize, cipher.Length);
                    mac = hmac.ComputeHash(macInput);
                    Array.Clear(macInput, 0, macInput.Length);
                }

                var container = new byte[Magic.Length + IvSize + mac.Length + cipher.Length];
                int o = 0;
                Buffer.BlockCopy(Magic, 0, container, o, Magic.Length); o += Magic.Length;
                Buffer.BlockCopy(iv, 0, container, o, IvSize); o += IvSize;
                Buffer.BlockCopy(mac, 0, container, o, mac.Length); o += mac.Length;
                Buffer.BlockCopy(cipher, 0, container, o, cipher.Length);
                return container;
            }
            finally
            {
                Array.Clear(aesKey, 0, aesKey.Length);
                Array.Clear(hmacKey, 0, hmacKey.Length);
            }
        }
    }
}
