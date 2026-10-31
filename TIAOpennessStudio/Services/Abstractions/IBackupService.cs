using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Services.Abstractions
{
    public sealed class BackupRecord
    {
        public long Id { get; set; }
        public string SourcePath { get; set; }
        public string BackupRoot { get; set; }
        public string BackupPath { get; set; }
        public string BackupName { get; set; }
        public long SizeBytes { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    public sealed class RestoreResult
    {
        public string BackupPath { get; set; }
        public string TargetPath { get; set; }
        public string SafetyBackupPath { get; set; }
        public DateTime RestoredAtUtc { get; set; }
    }

    /// <summary>
    /// Creates, lists, and restores project backups.
    /// </summary>
    public interface IBackupService
    {
        BackupRecord CreateBackup(string sourcePath, string backupRoot);

        IReadOnlyList<BackupRecord> ListBackups(string backupRoot, int limit = 500);

        RestoreResult RestoreBackup(string backupPath, string targetPath, bool createSafetyBackup = true);
    }
}
