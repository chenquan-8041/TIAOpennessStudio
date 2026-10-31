using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TIAOpennessStudio.Models
{
    public enum PatchStatus
    {
        Pending,
        Generated,
        Previewed,
        Applied,
        RolledBack,
        Failed,
        Conflict
    }

    public enum PatchRisk
    {
        Low,
        Medium,
        High
    }

    public class CodePatch
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Rid { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string OriginalContent { get; set; }
        public string ModifiedContent { get; set; }
        public string UnifiedDiff { get; set; }
        public int AddedLines { get; set; }
        public int RemovedLines { get; set; }
        public int ChangedLines => AddedLines + RemovedLines;
        public PatchRisk Risk { get; set; } = PatchRisk.Low;
        public string RiskReason { get; set; }
    }

    public class FilePatch
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public string OriginalContent { get; set; }
        public string NewContent { get; set; }
        public string UnifiedDiff { get; set; }
        public PatchStatus Status { get; set; } = PatchStatus.Pending;
        public int AddedLines { get; set; }
        public int RemovedLines { get; set; }
        public int ChangedLines => AddedLines + RemovedLines;
        public PatchRisk Risk { get; set; } = PatchRisk.Low;
        public string RiskReason { get; set; }
        public string ConflictContext { get; set; }
    }

    public class ChangeManifest
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Rid { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string Purpose { get; set; }
        public List<FilePatch> Files { get; set; } = new List<FilePatch>();
        public List<string> Constraints { get; set; } = new List<string>();
        public string AgentPrompt { get; set; }
        public int TotalFiles => Files.Count;
        public int TotalAddedLines => Files.Sum(f => f.AddedLines);
        public int TotalRemovedLines => Files.Sum(f => f.RemovedLines);
        public PatchRisk OverallRisk => ComputeOverallRisk();
        public List<string> RiskWarnings { get; set; } = new List<string>();

        private PatchRisk ComputeOverallRisk()
        {
            if (Files.Any(f => f.Risk == PatchRisk.High)) return PatchRisk.High;
            if (Files.Any(f => f.Risk == PatchRisk.Medium)) return PatchRisk.Medium;
            return PatchRisk.Low;
        }

        public string ToSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## 变更清单 #{Id.Substring(0, 8)}");
            sb.AppendLine($"目的: {Purpose}");
            sb.AppendLine($"文件数: {TotalFiles}");
            sb.AppendLine($"新增行: +{TotalAddedLines}");
            sb.AppendLine($"删除行: -{TotalRemovedLines}");
            sb.AppendLine($"风险等级: {OverallRisk}");
            
            if (RiskWarnings.Count > 0)
            {
                sb.AppendLine("\n### 风险警告");
                foreach (var warning in RiskWarnings)
                {
                    sb.AppendLine($"- {warning}");
                }
            }

            sb.AppendLine("\n### 变更文件");
            foreach (var file in Files)
            {
                sb.AppendLine($"- {file.FileName} ({file.BlockType}) [{file.Status}]");
            }

            return sb.ToString();
        }

        public string GenerateRollbackManifest()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 回滚清单 - 基于变更 {Id.Substring(0, 8)}");
            sb.AppendLine($"创建时间: {CreatedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            
            foreach (var file in Files.Where(f => f.Status == PatchStatus.Applied))
            {
                sb.AppendLine($"## {file.FilePath}");
                sb.AppendLine($"块名称: {file.BlockName}");
                sb.AppendLine($"块类型: {file.BlockType}");
                sb.AppendLine();
                sb.AppendLine("原始内容:");
                sb.AppendLine("```");
                sb.AppendLine(file.OriginalContent ?? "");
                sb.AppendLine("```");
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }

    public class PatchApplicationResult
    {
        public bool Success { get; set; }
        public string ManifestId { get; set; }
        public List<FileApplicationResult> FileResults { get; set; } = new List<FileApplicationResult>();
        public List<string> Errors { get; set; } = new List<string>();
        public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
        public long TotalTimeMs { get; set; }
        public bool WasRolledBack { get; set; }
        public string RollbackReason { get; set; }
    }

    public class FileApplicationResult
    {
        public string FileId { get; set; }
        public string FileName { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public PatchStatus FinalStatus { get; set; }
    }

    public class ConflictInfo
    {
        public string FileId { get; set; }
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public string OriginalContent { get; set; }
        public string CurrentContent { get; set; }
        public string ConflictRegion { get; set; }
        public List<string> ConflictMarkers { get; set; } = new List<string>();
        public string SuggestedResolution { get; set; }
        public bool CanAutoResolve { get; set; }
    }
}
