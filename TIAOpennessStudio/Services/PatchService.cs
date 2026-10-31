using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Generates, applies, rolls back, and detects conflicts for code patches.
    /// </summary>
    public class PatchService : IPatchService
    {
        private readonly Dictionary<string, ChangeManifest> _appliedManifests = new Dictionary<string, ChangeManifest>();

        public CodePatch GeneratePatch(string original, string modified, string fileName = "")
        {
            var diff = GenerateUnifiedDiff(original, modified, fileName);
            var lines = diff.Split('\n');
            var addedLines = lines.Count(l => l.StartsWith("+") && !l.StartsWith("+++"));
            var removedLines = lines.Count(l => l.StartsWith("-") && !l.StartsWith("---"));

            return new CodePatch
            {
                OriginalContent = original,
                ModifiedContent = modified,
                UnifiedDiff = diff,
                AddedLines = addedLines,
                RemovedLines = removedLines
            };
        }

        public FilePatch GenerateFilePatch(string filePath, string blockName, string blockType, string original, string modified)
        {
            var patch = new FilePatch
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                BlockName = blockName,
                BlockType = blockType,
                OriginalContent = original,
                NewContent = modified,
                UnifiedDiff = GenerateUnifiedDiff(original, modified, blockName)
            };

            var lines = patch.UnifiedDiff.Split('\n');
            patch.AddedLines = lines.Count(l => l.StartsWith("+") && !l.StartsWith("+++"));
            patch.RemovedLines = lines.Count(l => l.StartsWith("-") && !l.StartsWith("---"));
            patch.Risk = EvaluateRisk(patch);

            return patch;
        }

        public ChangeManifest CreateManifest(string rid, string purpose, List<FilePatch> files, List<string> constraints = null)
        {
            var manifest = new ChangeManifest
            {
                Rid = rid,
                Purpose = purpose,
                Files = files,
                Constraints = constraints ?? new List<string>()
            };

            foreach (var file in files)
            {
                if (file.Risk == PatchRisk.High)
                {
                    manifest.RiskWarnings.Add($"高风险文件: {file.FileName} - {file.RiskReason}");
                }
            }

            if (files.Sum(f => f.ChangedLines) > 500)
            {
                manifest.RiskWarnings.Add($"变更规模较大: {files.Sum(f => f.ChangedLines)} 行，建议分批次处理");
            }

            if (files.Count > 10)
            {
                manifest.RiskWarnings.Add($"涉及文件较多: {files.Count} 个文件，建议拆分任务");
            }

            return manifest;
        }

        public string GenerateUnifiedDiff(string original, string modified, string fileName, int contextLines = 3)
        {
            var oldLines = SplitLines(original, out bool oldHasEol);
            var newLines = SplitLines(modified, out bool newHasEol);

            bool identical = oldHasEol == newHasEol
                             && oldLines.Length == newLines.Length
                             && oldLines.SequenceEqual(newLines);
            if (identical)
                return $"--- {fileName}\n+++ {fileName}\n";

            contextLines = Math.Max(0, contextLines);

            // Line-level alignment: ' ' = context, '-' = removed, '+' = added. The final newline
            // is part of a line's identity so a newline-only change still surfaces as a change.
            var ops = new List<char>();
            var texts = new List<string>();
            var oldNoEol = new List<bool>();
            var newNoEol = new List<bool>();
            BuildOpStream(oldLines, newLines, oldHasEol, newHasEol, ops, texts, oldNoEol, newNoEol);

            // Prefix counts of old/new file lines used to number hunks.
            var oldBefore = new int[ops.Count + 1];
            var newBefore = new int[ops.Count + 1];
            for (int i = 0; i < ops.Count; i++)
            {
                char op = ops[i];
                oldBefore[i + 1] = oldBefore[i] + (op == ' ' || op == '-' ? 1 : 0);
                newBefore[i + 1] = newBefore[i] + (op == ' ' || op == '+' ? 1 : 0);
            }

            // Locate changed runs, then widen each by the requested context lines.
            var starts = new List<int>();
            var ends = new List<int>();
            int run = 0;
            while (run < ops.Count)
            {
                if (ops[run] == ' ')
                {
                    run++;
                    continue;
                }

                int runStart = run;
                while (run < ops.Count && ops[run] != ' ') run++;
                starts.Add(Math.Max(0, runStart - contextLines));
                ends.Add(Math.Min(ops.Count, run + contextLines));
            }

            // Merge adjacent/overlapping context windows into single hunks.
            if (starts.Count > 0)
            {
                var mergedStarts = new List<int> { starts[0] };
                var mergedEnds = new List<int> { ends[0] };
                for (int h = 1; h < starts.Count; h++)
                {
                    int last = mergedStarts.Count - 1;
                    if (starts[h] <= mergedEnds[last])
                    {
                        mergedEnds[last] = Math.Max(mergedEnds[last], ends[h]);
                    }
                    else
                    {
                        mergedStarts.Add(starts[h]);
                        mergedEnds.Add(ends[h]);
                    }
                }
                starts = mergedStarts;
                ends = mergedEnds;
            }

            var diff = new StringBuilder();
            diff.Append($"--- {fileName}\n");
            diff.Append($"+++ {fileName}\n");

            for (int h = 0; h < starts.Count; h++)
            {
                int start = starts[h];
                int end = ends[h];

                int oldCount = oldBefore[end] - oldBefore[start];
                int newCount = newBefore[end] - newBefore[start];

                // Zero-count side uses the 0-based line before the hunk; otherwise it is 1-based.
                int oldLine = oldCount > 0 ? oldBefore[start] + 1 : oldBefore[start];
                int newLine = newCount > 0 ? newBefore[start] + 1 : newBefore[start];

                diff.Append("@@ -");
                diff.Append(oldCount == 1 ? oldLine.ToString() : oldLine + "," + oldCount);
                diff.Append(" +");
                diff.Append(newCount == 1 ? newLine.ToString() : newLine + "," + newCount);
                diff.Append(" @@\n");

                for (int i = start; i < end; i++)
                {
                    diff.Append(ops[i]);
                    diff.Append(texts[i]);
                    diff.Append('\n');

                    if (oldNoEol[i] || newNoEol[i])
                        diff.Append("\\ No newline at end of file\n");
                }
            }

            return diff.ToString();
        }

        private static string[] SplitLines(string text, out bool hasTrailingNewline)
        {
            text = text ?? "";
            text = text.Replace("\r\n", "\n");
            hasTrailingNewline = text.EndsWith("\n");
            if (text.Length == 0)
                return Array.Empty<string>();

            string[] parts = text.Split('\n');
            if (hasTrailingNewline)
                Array.Resize(ref parts, parts.Length - 1);

            return parts;
        }

        private static void BuildOpStream(string[] oldLines, string[] newLines, bool oldHasEol, bool newHasEol,
            List<char> ops, List<string> texts, List<bool> oldNoEol, List<bool> newNoEol)
        {
            int n = oldLines.Length;
            int m = newLines.Length;

            // The final newline is part of a line's identity: appending "\n" (which content cannot
            // contain) to the last line of a side that lacks a trailing newline keeps "c" and
            // "c (no eol)" distinct, so newline-only changes still surface as a change.
            string OldKey(int i) => (i == n - 1 && !oldHasEol) ? oldLines[i] + "\n" : oldLines[i];
            string NewKey(int j) => (j == m - 1 && !newHasEol) ? newLines[j] + "\n" : newLines[j];

            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int IdOf(string s) => ids.TryGetValue(s, out int id) ? id : (ids[s] = ids.Count);

            var oldIds = new int[n];
            var newIds = new int[m];
            for (int i = 0; i < n; i++) oldIds[i] = IdOf(OldKey(i));
            for (int j = 0; j < m; j++) newIds[j] = IdOf(NewKey(j));

            // LCS table. Prefer deletion on ties so a replaced line renders as '-' then '+'.
            var lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = m - 1; j >= 0; j--)
                {
                    lcs[i, j] = oldIds[i] == newIds[j]
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (oldIds[x] == newIds[y])
                {
                    ops.Add(' '); texts.Add(oldLines[x]);
                    oldNoEol.Add(x == n - 1 && !oldHasEol);
                    newNoEol.Add(y == m - 1 && !newHasEol);
                    x++; y++;
                }
                else if (lcs[x + 1, y] >= lcs[x, y + 1])
                {
                    ops.Add('-'); texts.Add(oldLines[x]);
                    oldNoEol.Add(x == n - 1 && !oldHasEol);
                    newNoEol.Add(false);
                    x++;
                }
                else
                {
                    ops.Add('+'); texts.Add(newLines[y]);
                    oldNoEol.Add(false);
                    newNoEol.Add(y == m - 1 && !newHasEol);
                    y++;
                }
            }
            while (x < n)
            {
                ops.Add('-'); texts.Add(oldLines[x]);
                oldNoEol.Add(x == n - 1 && !oldHasEol);
                newNoEol.Add(false);
                x++;
            }
            while (y < m)
            {
                ops.Add('+'); texts.Add(newLines[y]);
                oldNoEol.Add(false);
                newNoEol.Add(y == m - 1 && !newHasEol);
                y++;
            }
        }

        // Keep the old LCS around just in case, but unused now
        private List<(int origIdx, int modIdx)> ComputeLCS(string[] orig, string[] mod)
        {
            var lcs = new List<(int, int)>();
            return lcs;
        }

        private (string match, int origIdx, int modIdx) FindNextMatch(List<(int origIdx, int modIdx)> lcs, int i, int j)
        {
            return (null, -1, -1);
        }

        public PatchApplicationResult ApplyManifest(ChangeManifest manifest, ITiaOpennessService tiaService, Action<string> onProgress = null)
        {
            var result = new PatchApplicationResult { ManifestId = manifest.Id };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var appliedFiles = new List<FilePatch>();

            onProgress?.Invoke("开始应用补丁...");

            try
            {
                for (int i = 0; i < manifest.Files.Count; i++)
                {
                    var file = manifest.Files[i];
                    onProgress?.Invoke($"应用文件 {i + 1}/{manifest.Files.Count}: {file.FileName}");

                    var fileResult = new FileApplicationResult
                    {
                        FileId = file.Id,
                        FileName = file.FileName
                    };

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(file.BlockName) && tiaService != null)
                        {
                            var importResult = tiaService.ImportBlockCodeAsync(file.BlockName, file.BlockType, file.NewContent).GetAwaiter().GetResult();
                            
                            if (importResult.Contains("Error") || importResult.Contains("失败"))
                            {
                                fileResult.Success = false;
                                fileResult.Error = importResult;
                                fileResult.FinalStatus = PatchStatus.Failed;
                                result.Errors.Add($"{file.FileName}: {importResult}");
                            }
                            else
                            {
                                fileResult.Success = true;
                                file.Status = PatchStatus.Applied;
                                fileResult.FinalStatus = PatchStatus.Applied;
                                appliedFiles.Add(file);
                            }
                        }
                        else
                        {
                            fileResult.Success = true;
                            file.Status = PatchStatus.Applied;
                            fileResult.FinalStatus = PatchStatus.Applied;
                            appliedFiles.Add(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        fileResult.Success = false;
                        fileResult.Error = ex.Message;
                        fileResult.FinalStatus = PatchStatus.Failed;
                        result.Errors.Add($"{file.FileName}: {ex.Message}");
                    }

                    result.FileResults.Add(fileResult);
                }

                sw.Stop();
                result.TotalTimeMs = sw.ElapsedMilliseconds;

                if (result.Errors.Count > 0)
                {
                    result.Success = false;
                    onProgress?.Invoke($"应用失败: {result.Errors.Count} 个文件出错");
                    
                    if (manifest.Constraints.Contains("atomic"))
                    {
                        onProgress?.Invoke("执行原子化回滚...");
                        var rollbackResult = RollbackManifest(manifest, tiaService);
                        result.WasRolledBack = rollbackResult.Success;
                        result.RollbackReason = "原子化回滚：部分文件失败，撤销所有更改";
                    }
                }
                else
                {
                    result.Success = true;
                    _appliedManifests[manifest.Id] = manifest;
                    onProgress?.Invoke($"应用成功: {manifest.Files.Count} 个文件");
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"应用异常: {ex.Message}");
            }

            return result;
        }

        public PatchApplicationResult RollbackManifest(ChangeManifest manifest, ITiaOpennessService tiaService)
        {
            var result = new PatchApplicationResult { ManifestId = manifest.Id };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var file in manifest.Files.Where(f => f.Status == PatchStatus.Applied))
            {
                var fileResult = new FileApplicationResult
                {
                    FileId = file.Id,
                    FileName = file.FileName
                };

                try
                {
                    if (!string.IsNullOrWhiteSpace(file.BlockName) && tiaService != null)
                    {
                        var importResult = tiaService.ImportBlockCodeAsync(file.BlockName, file.BlockType, file.OriginalContent).GetAwaiter().GetResult();
                        
                        if (importResult.Contains("Error") || importResult.Contains("失败"))
                        {
                            fileResult.Success = false;
                            fileResult.Error = importResult;
                            result.Errors.Add($"{file.FileName}: {importResult}");
                        }
                        else
                        {
                            fileResult.Success = true;
                            file.Status = PatchStatus.RolledBack;
                            fileResult.FinalStatus = PatchStatus.RolledBack;
                        }
                    }
                    else
                    {
                        fileResult.Success = true;
                        file.Status = PatchStatus.RolledBack;
                        fileResult.FinalStatus = PatchStatus.RolledBack;
                    }
                }
                catch (Exception ex)
                {
                    fileResult.Success = false;
                    fileResult.Error = ex.Message;
                    result.Errors.Add($"{file.FileName}: {ex.Message}");
                }

                result.FileResults.Add(fileResult);
            }

            sw.Stop();
            result.TotalTimeMs = sw.ElapsedMilliseconds;
            result.Success = result.Errors.Count == 0;
            result.WasRolledBack = result.Success;

            return result;
        }

        public List<ConflictInfo> DetectConflicts(ChangeManifest manifest, ITiaOpennessService tiaService)
        {
            var conflicts = new List<ConflictInfo>();

            foreach (var file in manifest.Files)
            {
                if (string.IsNullOrWhiteSpace(file.BlockName)) continue;

                try
                {
                    var currentContent = tiaService.ExportBlockCodeAsync(file.BlockName).GetAwaiter().GetResult();
                    
                    if (!string.IsNullOrWhiteSpace(file.OriginalContent) && currentContent != file.OriginalContent)
                    {
                        var conflict = new ConflictInfo
                        {
                            FileId = file.Id,
                            FileName = file.FileName,
                            FilePath = file.FilePath,
                            OriginalContent = file.OriginalContent,
                            CurrentContent = currentContent
                        };

                        var origLines = (file.OriginalContent ?? "").Split('\n');
                        var currLines = (currentContent ?? "").Split('\n');
                        
                        var diff = GenerateUnifiedDiff(file.OriginalContent, currentContent, file.BlockName);
                        conflict.ConflictRegion = diff;

                        conflict.CanAutoResolve = ComputeSimilarity(file.OriginalContent, currentContent) > 0.7;
                        
                        if (!conflict.CanAutoResolve)
                        {
                            conflict.SuggestedResolution = "建议手动解决冲突或保持当前版本";
                            file.Status = PatchStatus.Conflict;
                            file.ConflictContext = diff;
                        }

                        conflicts.Add(conflict);
                    }
                }
                catch
                {
                }
            }

            return conflicts;
        }

        private double ComputeSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0;
            
            var longer = s1.Length > s2.Length ? s1 : s2;
            var shorter = s1.Length > s2.Length ? s2 : s1;
            
            if (longer.Length == 0) return 1.0;
            
            return (longer.Length - EditDistance(longer, shorter)) / (double)longer.Length;
        }

        private int EditDistance(string s1, string s2)
        {
            var costs = new int[s2.Length + 1];
            for (int i = 0; i <= s1.Length; i++)
            {
                int lastValue = i;
                for (int j = 0; j <= s2.Length; j++)
                {
                    if (i == 0)
                        costs[j] = j;
                    else if (j > 0)
                    {
                        int newValue = costs[j - 1];
                        if (s1[i - 1] != s2[j - 1])
                            newValue = Math.Min(Math.Min(newValue, lastValue), costs[j]) + 1;
                        costs[j - 1] = lastValue;
                        lastValue = newValue;
                    }
                }
                if (i > 0)
                    costs[s2.Length] = lastValue;
            }
            return costs[s2.Length];
        }

        public PatchRisk EvaluateRisk(FilePatch patch)
        {
            var reasons = new List<string>();
            var risk = PatchRisk.Low;

            if (patch.RemovedLines > 50)
            {
                risk = PatchRisk.Medium;
                reasons.Add($"删除行数较多: {patch.RemovedLines} 行");
            }

            if (patch.AddedLines > 200)
            {
                risk = PatchRisk.Medium;
                reasons.Add($"新增行数较多: {patch.AddedLines} 行");
            }

            if (patch.RemovedLines > 100 || patch.AddedLines > 500)
            {
                risk = PatchRisk.High;
                reasons.Add("变更规模过大");
            }

            if (patch.BlockType == "DB" && patch.AddedLines + patch.RemovedLines > 100)
            {
                risk = PatchRisk.High;
                reasons.Add("数据块大规模变更，可能影响运行时");
            }

            var dangerousPatterns = new[]
            {
                @"DELETE\s+", @"DROP\s+", @"TRUNCATE\s+", 
                @"\*\s*//\s*危险", @"\*\s*//\s*警告",
                @"OVERWRITE", @"FORCE"
            };

            foreach (var pattern in dangerousPatterns)
            {
                if (Regex.IsMatch(patch.NewContent ?? "", pattern, RegexOptions.IgnoreCase))
                {
                    risk = PatchRisk.High;
                    reasons.Add("检测到潜在危险操作");
                    break;
                }
            }

            patch.RiskReason = string.Join("; ", reasons);
            return risk;
        }

        public ChangeManifest GetAppliedManifest(string manifestId)
        {
            return _appliedManifests.TryGetValue(manifestId, out var manifest) ? manifest : null;
        }
    }
}
