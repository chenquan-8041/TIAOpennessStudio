using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// G-4: SQLite-backed cross-session user memory. Lives in the same
    /// <c>Index/project_index.db</c> as the other auxiliary tables, but its table is
    /// keyed by <c>user_key</c> (orthogonal to project_key) so preferences follow the
    /// user across projects/sessions. Self-heals its own table before each operation.
    ///
    /// Discipline mirrors <c>planning_knowledge</c>: memory is advisory context, always
    /// labelled by source, never treated as verified project fact.
    /// </summary>
    public sealed class UserMemoryService : IUserMemoryService
    {
        private readonly string _connectionString;
        private readonly object _sync = new object();

        public UserMemoryService(string appBaseDirectory)
        {
            var baseDir = string.IsNullOrWhiteSpace(appBaseDirectory) ? AppDomain.CurrentDomain.BaseDirectory : appBaseDirectory;
            var indexDir = System.IO.Path.Combine(baseDir, "Index");
            System.IO.Directory.CreateDirectory(indexDir);
            var dbPath = System.IO.Path.Combine(indexDir, "project_index.db");
            _connectionString = $"Data Source={dbPath}";
            Initialize();
        }

        private void Initialize()
        {
            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    Exec(conn,
                        "CREATE TABLE IF NOT EXISTS user_memory (" +
                        "user_key TEXT NOT NULL," +
                        "memory_key TEXT NOT NULL," +
                        "category TEXT," +
                        "key TEXT," +
                        "value TEXT," +
                        "source TEXT," +              // explicit | derived | summarized
                        "confidence REAL DEFAULT 1.0," +
                        "hit_count INTEGER DEFAULT 0," +
                        "updated_at TEXT," +
                        "PRIMARY KEY(user_key, memory_key)" +
                        ");");
                    Exec(conn, "CREATE INDEX IF NOT EXISTS idx_user_memory_cat ON user_memory(user_key, category);");
                }
            }
        }

        private void EnsureTable()
        {
            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    if (!TableExists(conn, "user_memory"))
                    {
                        conn.Close();
                        Initialize();
                    }
                }
            }
        }

        public void Record(string userKey, string category, string key, string value,
            string source = "explicit", double confidence = 1.0)
        {
            if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(value)) return;
            EnsureTable();
            var uk = NormalizeUserKey(userKey);
            var cat = (category ?? "misc").Trim().ToLowerInvariant();
            var k = (key ?? "").Trim();
            var memoryKey = BuildMemoryKey(cat, k);
            var now = DateTime.UtcNow.ToString("o");
            var conf = Math.Max(0.0, Math.Min(1.0, confidence));

            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        // Re-recording reinforces (hit_count++) and refreshes the value.
                        cmd.CommandText =
                            "INSERT INTO user_memory(user_key, memory_key, category, key, value, source, confidence, hit_count, updated_at) " +
                            "VALUES(@uk,@mk,@cat,@key,@val,@src,@conf,1,@now) " +
                            "ON CONFLICT(user_key, memory_key) DO UPDATE SET " +
                            "value=excluded.value, source=excluded.source, " +
                            "confidence=MAX(user_memory.confidence, excluded.confidence), " +
                            "hit_count=user_memory.hit_count + 1, updated_at=excluded.updated_at;";
                        cmd.Parameters.AddWithValue("@uk", uk);
                        cmd.Parameters.AddWithValue("@mk", memoryKey);
                        cmd.Parameters.AddWithValue("@cat", cat);
                        cmd.Parameters.AddWithValue("@key", k);
                        cmd.Parameters.AddWithValue("@val", LimitText(value, 1000));
                        cmd.Parameters.AddWithValue("@src", (source ?? "explicit").Trim().ToLowerInvariant());
                        cmd.Parameters.AddWithValue("@conf", conf);
                        cmd.Parameters.AddWithValue("@now", now);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        public List<UserMemoryEntry> Recall(string userKey, string query = null, string category = null, int limit = 10)
        {
            EnsureTable();
            var uk = NormalizeUserKey(userKey);
            limit = Math.Max(1, Math.Min(limit <= 0 ? 10 : limit, 100));
            var tokens = Tokenize(query);
            var cat = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();

            var entries = new List<UserMemoryEntry>();
            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT user_key, memory_key, category, key, value, source, confidence, hit_count, updated_at " +
                            "FROM user_memory WHERE user_key=@uk " +
                            (cat != null ? "AND category=@cat " : "") +
                            "ORDER BY hit_count DESC, updated_at DESC LIMIT @fetch;";
                        cmd.Parameters.AddWithValue("@uk", uk);
                        if (cat != null) cmd.Parameters.AddWithValue("@cat", cat);
                        cmd.Parameters.AddWithValue("@fetch", Math.Max(limit * 5, 50));
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var e = Read(reader);
                                if (tokens.Count == 0 ||
                                    tokens.Any(t => ContainsIgnoreCase(e.Key, t) || ContainsIgnoreCase(e.Value, t)))
                                {
                                    entries.Add(e);
                                }
                            }
                        }
                    }
                }
            }
            return entries.Take(limit).ToList();
        }

        public void Forget(string userKey, string category, string key)
        {
            EnsureTable();
            var uk = NormalizeUserKey(userKey);
            var memoryKey = BuildMemoryKey((category ?? "misc").Trim().ToLowerInvariant(), (key ?? "").Trim());
            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM user_memory WHERE user_key=@uk AND memory_key=@mk;";
                        cmd.Parameters.AddWithValue("@uk", uk);
                        cmd.Parameters.AddWithValue("@mk", memoryKey);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        public void ClearUser(string userKey)
        {
            EnsureTable();
            var uk = NormalizeUserKey(userKey);
            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM user_memory WHERE user_key=@uk;";
                        cmd.Parameters.AddWithValue("@uk", uk);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        public string BuildMemoryContext(string userKey, string query = null, int limit = 8)
        {
            var entries = Recall(userKey, query, null, limit);
            if (entries.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("## User Preferences (advisory — user habits, not project fact)");
            sb.AppendLine("The following are the user's remembered preferences. Prefer them when they don't conflict with the current request or project facts.");
            foreach (var e in entries)
            {
                sb.Append("- [").Append(e.Category).Append("] ");
                if (!string.IsNullOrWhiteSpace(e.Key)) sb.Append(e.Key).Append(": ");
                sb.Append(e.Value);
                if (!string.Equals(e.Source, "explicit", StringComparison.OrdinalIgnoreCase))
                    sb.Append(" (").Append(e.Source).Append(')');
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        // ---- helpers ----

        private static UserMemoryEntry Read(SqliteDataReader r) => new UserMemoryEntry
        {
            UserKey = Str(r, "user_key"),
            MemoryKey = Str(r, "memory_key"),
            Category = Str(r, "category"),
            Key = Str(r, "key"),
            Value = Str(r, "value"),
            Source = Str(r, "source"),
            Confidence = Dbl(r, "confidence"),
            HitCount = Int(r, "hit_count"),
            UpdatedAt = DateTime.TryParse(Str(r, "updated_at"), out var dt) ? dt : DateTime.MinValue
        };

        private static string BuildMemoryKey(string category, string key)
        {
            var basis = (category ?? "") + "|" + (key ?? "");
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(basis));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string NormalizeUserKey(string userKey)
            => string.IsNullOrWhiteSpace(userKey) ? "default-user" : userKey.Trim();

        private static string LimitText(string value, int max)
        {
            var t = value ?? string.Empty;
            return t.Length <= max ? t : t.Substring(0, max);
        }

        private static bool ContainsIgnoreCase(string value, string token)
            => !string.IsNullOrWhiteSpace(value) && value.IndexOf(token ?? "", StringComparison.OrdinalIgnoreCase) >= 0;

        private static List<string> Tokenize(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<string>();
            return query
                .Split(new[] { ' ', '\t', '\n', '\r', ',', ';', '.', '，', '；', '。' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
        }

        private static bool TableExists(SqliteConnection conn, string name)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;";
                cmd.Parameters.AddWithValue("@name", name);
                return cmd.ExecuteScalar() != null;
            }
        }

        private static string Str(SqliteDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? "" : r.GetValue(i)?.ToString() ?? "";
        }

        private static int Int(SqliteDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            if (r.IsDBNull(i)) return 0;
            return int.TryParse(r.GetValue(i)?.ToString(), out var v) ? v : 0;
        }

        private static double Dbl(SqliteDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            if (r.IsDBNull(i)) return 0;
            return double.TryParse(r.GetValue(i)?.ToString(), out var v) ? v : 0;
        }

        private static void Exec(SqliteConnection conn, string sql)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
    }
}
