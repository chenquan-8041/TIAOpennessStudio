using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Persists project/block backups to a local SQLite index so earlier versions can be restored.
    /// </summary>
    public sealed class BackupService : IBackupService
    {
        private const int MinListLimit = 1;
        private const int MaxListLimit = 5000;

        private readonly string _connectionString;
        private readonly object _sync = new object();

        public BackupService(string appBaseDirectory)
        {
            var baseDir = string.IsNullOrWhiteSpace(appBaseDirectory) ? AppDomain.CurrentDomain.BaseDirectory : appBaseDirectory;
            var indexDir = Path.Combine(baseDir, "Index");
            Directory.CreateDirectory(indexDir);
            var dbPath = Path.Combine(indexDir, "backup_index.db");
            _connectionString = $"Data Source={dbPath}";
            Initialize();
        }

        public BackupRecord CreateBackup(string sourcePath, string backupRoot)
        {
            EnsureExistingFile(sourcePath, nameof(sourcePath), "Source file does not exist.");
            EnsureNotEmpty(backupRoot, nameof(backupRoot), "Backup root is empty.");
            string normalizedRoot = NormalizePath(backupRoot);
            string normalizedSource = NormalizePath(sourcePath);

            Directory.CreateDirectory(normalizedRoot);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string baseName = Path.GetFileNameWithoutExtension(normalizedSource);
            string ext = Path.GetExtension(normalizedSource);
            string projectFolder = Path.Combine(normalizedRoot, baseName);
            Directory.CreateDirectory(projectFolder);
            string backupName = baseName + "_" + stamp + ext;
            string backupPath = Path.Combine(projectFolder, backupName);
            File.Copy(normalizedSource, backupPath, false);

            var info = new FileInfo(backupPath);
            var record = new BackupRecord
            {
                SourcePath = normalizedSource,
                BackupRoot = normalizedRoot,
                BackupPath = info.FullName,
                BackupName = backupName,
                SizeBytes = info.Length,
                CreatedAtUtc = DateTime.UtcNow
            };

            InsertBackup(record);
            return record;
        }

        public IReadOnlyList<BackupRecord> ListBackups(string backupRoot, int limit = 500)
        {
            if (limit < MinListLimit) limit = MinListLimit;
            if (limit > MaxListLimit) limit = MaxListLimit;

            string normalizedRoot = string.IsNullOrWhiteSpace(backupRoot)
                ? string.Empty
                : NormalizePath(backupRoot);

            var result = new List<BackupRecord>();
            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT id, source_path, backup_root, backup_path, backup_name, size_bytes, created_at_utc " +
                            "FROM backup_records " +
                            "WHERE (@backup_root='' OR backup_root=@backup_root) " +
                            "ORDER BY created_at_utc DESC LIMIT @limit;";
                        cmd.Parameters.AddWithValue("@backup_root", normalizedRoot);
                        cmd.Parameters.AddWithValue("@limit", limit);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                result.Add(new BackupRecord
                                {
                                    Id = reader.GetInt64(0),
                                    SourcePath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                    BackupRoot = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                    BackupPath = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                                    BackupName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                                    SizeBytes = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                                    CreatedAtUtc = reader.IsDBNull(6) ? DateTime.MinValue : DateTime.Parse(reader.GetString(6)).ToUniversalTime()
                                });
                            }
                        }
                    }
                }
            }
            return result;
        }

        public RestoreResult RestoreBackup(string backupPath, string targetPath, bool createSafetyBackup = true)
        {
            EnsureExistingFile(backupPath, nameof(backupPath), "Backup file does not exist.");
            EnsureNotEmpty(targetPath, nameof(targetPath), "Target path is empty.");

            string fullBackupPath = NormalizePath(backupPath);
            string fullTargetPath = NormalizePath(targetPath);
            string safetyPath = string.Empty;
            if (createSafetyBackup && File.Exists(fullTargetPath))
            {
                safetyPath = fullTargetPath + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Copy(fullTargetPath, safetyPath, true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullTargetPath) ?? AppDomain.CurrentDomain.BaseDirectory);
            File.Copy(fullBackupPath, fullTargetPath, true);

            var restoredAt = DateTime.UtcNow;
            InsertRestoreAudit(fullBackupPath, fullTargetPath, safetyPath, restoredAt);

            return new RestoreResult
            {
                BackupPath = fullBackupPath,
                TargetPath = fullTargetPath,
                SafetyBackupPath = safetyPath,
                RestoredAtUtc = restoredAt
            };
        }

        private void Initialize()
        {
            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "CREATE TABLE IF NOT EXISTS backup_records (" +
                            "id INTEGER PRIMARY KEY AUTOINCREMENT," +
                            "source_path TEXT NOT NULL," +
                            "backup_root TEXT NOT NULL," +
                            "backup_path TEXT NOT NULL UNIQUE," +
                            "backup_name TEXT NOT NULL," +
                            "size_bytes INTEGER NOT NULL," +
                            "created_at_utc TEXT NOT NULL" +
                            ");";
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "CREATE TABLE IF NOT EXISTS restore_audit (" +
                            "id INTEGER PRIMARY KEY AUTOINCREMENT," +
                            "backup_path TEXT NOT NULL," +
                            "target_path TEXT NOT NULL," +
                            "safety_backup_path TEXT," +
                            "restored_at_utc TEXT NOT NULL" +
                            ");";
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_backup_root_time ON backup_records(backup_root, created_at_utc);";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private void InsertBackup(BackupRecord record)
        {
            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "INSERT INTO backup_records(source_path, backup_root, backup_path, backup_name, size_bytes, created_at_utc) " +
                            "VALUES(@source_path, @backup_root, @backup_path, @backup_name, @size_bytes, @created_at_utc); " +
                            "SELECT last_insert_rowid();";
                        cmd.Parameters.AddWithValue("@source_path", record.SourcePath);
                        cmd.Parameters.AddWithValue("@backup_root", record.BackupRoot);
                        cmd.Parameters.AddWithValue("@backup_path", record.BackupPath);
                        cmd.Parameters.AddWithValue("@backup_name", record.BackupName);
                        cmd.Parameters.AddWithValue("@size_bytes", record.SizeBytes);
                        cmd.Parameters.AddWithValue("@created_at_utc", record.CreatedAtUtc.ToString("o"));
                        record.Id = (long)(cmd.ExecuteScalar() ?? 0L);
                    }
                }
            }
        }

        private void InsertRestoreAudit(string backupPath, string targetPath, string safetyBackupPath, DateTime restoredAtUtc)
        {
            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "INSERT INTO restore_audit(backup_path, target_path, safety_backup_path, restored_at_utc) " +
                            "VALUES(@backup_path, @target_path, @safety_backup_path, @restored_at_utc);";
                        cmd.Parameters.AddWithValue("@backup_path", backupPath);
                        cmd.Parameters.AddWithValue("@target_path", targetPath);
                        cmd.Parameters.AddWithValue("@safety_backup_path", safetyBackupPath ?? string.Empty);
                        cmd.Parameters.AddWithValue("@restored_at_utc", restoredAtUtc.ToString("o"));
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath((path ?? string.Empty).Trim());
        }

        private static void EnsureNotEmpty(string value, string paramName, string message)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(message, paramName);
            }
        }

        private static void EnsureExistingFile(string path, string paramName, string message)
        {
            EnsureNotEmpty(path, paramName, message);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(message, path);
            }
        }
    }
}
