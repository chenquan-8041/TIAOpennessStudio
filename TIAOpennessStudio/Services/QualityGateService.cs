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
    public class QualityConstraint
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public string Description { get; set; }
        public QualityConstraintType Type { get; set; }
        public QualityConstraintSeverity Severity { get; set; }
        public bool IsEnabled { get; set; } = true;
        public string Pattern { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public enum QualityConstraintType
    {
        ForbiddenPattern,
        RequiredPattern,
        SizeLimit,
        NamingConvention,
        DependencyCheck,
        ComplexityCheck
    }

    public enum QualityConstraintSeverity
    {
        Info,
        Warning,
        Error,
        Blocker
    }

    public class QualityViolation
    {
        public string ConstraintId { get; set; }
        public string ConstraintName { get; set; }
        public QualityConstraintSeverity Severity { get; set; }
        public string Message { get; set; }
        public string FileName { get; set; }
        public int LineNumber { get; set; }
        public string CodeSnippet { get; set; }
        public string SuggestedFix { get; set; }
    }

    public class StylePattern
    {
        public string Name { get; set; }
        public string Pattern { get; set; }
        public string Replacement { get; set; }
        public string Description { get; set; }
    }

    public class QualityGateResult
    {
        public bool Passed { get; set; }
        public List<QualityViolation> Violations { get; set; } = new List<QualityViolation>();
        public List<StylePattern> AppliedCorrections { get; set; } = new List<StylePattern>();
        public string Summary { get; set; }
        public int InfoCount => Violations.Count(v => v.Severity == QualityConstraintSeverity.Info);
        public int WarningCount => Violations.Count(v => v.Severity == QualityConstraintSeverity.Warning);
        public int ErrorCount => Violations.Count(v => v.Severity == QualityConstraintSeverity.Error);
        public int BlockerCount => Violations.Count(v => v.Severity == QualityConstraintSeverity.Blocker);
        public bool HasErrors => ErrorCount > 0 || BlockerCount > 0;
    }

    /// <summary>
    /// Validates generated code against quality constraints and enforces style rules.
    /// </summary>
    public class QualityGateService : IQualityGateService
    {
        private readonly List<QualityConstraint> _defaultConstraints;
        private readonly List<StylePattern> _stylePatterns;

        public QualityGateService()
        {
            _defaultConstraints = InitializeDefaultConstraints();
            _stylePatterns = InitializeStylePatterns();
        }

        private List<QualityConstraint> InitializeDefaultConstraints()
        {
            return new List<QualityConstraint>
            {
                new QualityConstraint
                {
                    Id = "forbidden-external-block-ref",
                    Name = "禁止引用不存在的块",
                    Description = "SCL中禁止调用项目中不存在的FB/FC/UDT。检查方式：仅检测明显不合法的调用模式（如包含URL路径、不存在的系统库调用）。完整验证需要与项目索引交叉比对。",
                    Type = QualityConstraintType.ForbiddenPattern,
                    Severity = QualityConstraintSeverity.Error,
                    Pattern = @"(?<!\w)(https?://|ftp://|file://|\\[\w\\]+|/[a-zA-Z])[\w./\\:]+(?=\s*\(|\s*#|\s*:|\s*;|$)",
                    Message = "检测到可能的外部资源引用，请使用项目内部块/变量"
                },
                new QualityConstraint
                {
                    Id = "forbidden-unknown-system-call",
                    Name = "禁止未知系统调用",
                    Description = "禁止调用项目中未注册的系统函数或库。典型模式如：LIBRARY::Function() 或外部DLL调用",
                    Type = QualityConstraintType.ForbiddenPattern,
                    Severity = QualityConstraintSeverity.Error,
                    Pattern = @"(LIBRARY|EXTERNAL|DLL|COM)\s*::\s*\w+",
                    Message = "检测到可能的外部库调用，请使用项目内部资源"
                },
                new QualityConstraint
                {
                    Id = "forbidden-hardcoded-secret",
                    Name = "禁止硬编码密钥",
                    Description = "禁止在代码中硬编码密码、API密钥等敏感信息",
                    Type = QualityConstraintType.ForbiddenPattern,
                    Severity = QualityConstraintSeverity.Blocker,
                    Pattern = @"(password|passwd|pwd|apikey|api_key|secret|token)\s*:?=\s*['""][^'""]+['""]",
                    Message = "检测到可能的硬编码密钥，请使用配置或变量替代"
                },
                new QualityConstraint
                {
                    Id = "forbidden-dangerous-ops",
                    Name = "禁止危险操作",
                    Description = "禁止使用危险的系统操作",
                    Type = QualityConstraintType.ForbiddenPattern,
                    Severity = QualityConstraintSeverity.Blocker,
                    Pattern = @"\b(DELETE\s+FROM|DROP\s+TABLE|TRUNCATE|EXEC\s*\(|xp_cmdshell)\b",
                    Message = "检测到危险操作，已禁止"
                },
                new QualityConstraint
                {
                    Id = "forbidden-goto",
                    Name = "禁止GOTO",
                    Description = "禁止使用GOTO语句",
                    Type = QualityConstraintType.ForbiddenPattern,
                    Severity = QualityConstraintSeverity.Error,
                    Pattern = @"\bGOTO\s+\w+",
                    Message = "GOTO语句破坏代码结构，禁止使用"
                },
                new QualityConstraint
                {
                    Id = "forbidden-hardcoded-array-bounds",
                    Name = "禁止硬编码数组边界",
                    Description = "硬编码数组边界值（如 arr[100]）可能不可靠，建议使用 SIZEOF 或正式常量",
                    Type = QualityConstraintType.ForbiddenPattern,
                    Severity = QualityConstraintSeverity.Info,
                    Pattern = @"\[[0-9]{3,}\]",
                    Message = "建议使用 SIZEOF() 或命名常量替代硬编码数组边界"
                },
                new QualityConstraint
                {
                    Id = "required-comment",
                    Name = "重要逻辑注释",
                    Description = "重要逻辑应有注释说明",
                    Type = QualityConstraintType.RequiredPattern,
                    Severity = QualityConstraintSeverity.Info,
                    Pattern = @"IF\s+.+\s+THEN[\s\S]{100,}(END_IF|ELSIF)",
                    Message = "重要条件分支建议添加注释"
                },
                new QualityConstraint
                {
                    Id = "forbidden-goto",
                    Name = "禁止GOTO",
                    Description = "禁止使用GOTO语句",
                    Type = QualityConstraintType.ForbiddenPattern,
                    Severity = QualityConstraintSeverity.Error,
                    Pattern = @"\bGOTO\s+\w+",
                    Message = "GOTO语句破坏代码结构，禁止使用"
                },
                new QualityConstraint
                {
                    Id = "forbidden-magic-numbers",
                    Name = "禁止魔法数字",
                    Description = "禁止使用未命名的数字常量",
                    Type = QualityConstraintType.ForbiddenPattern,
                    Severity = QualityConstraintSeverity.Info,
                    Pattern = @"(?<![""\w])(?<!\d\.\d)(?<!\d)[0-9]{4,}(?![.""]|\w)",
                    Message = "建议将数字常量定义为命名常量"
                }
            };
        }

        private List<StylePattern> InitializeStylePatterns()
        {
            return new List<StylePattern>
            {
                new StylePattern
                {
                    Name = "remove-trailing-whitespace",
                    Pattern = @"[ \t]+(\r?\n)",
                    Replacement = "$1",
                    Description = "移除行尾空格"
                },
                new StylePattern
                {
                    Name = "normalize-line-endings",
                    Pattern = @"\r\n|\n|\r",
                    Replacement = "\n",
                    Description = "规范化行尾符号"
                },
                new StylePattern
                {
                    Name = "ensure-single-blank-line",
                    Pattern = @"\n{3,}",
                    Replacement = "\n\n",
                    Description = "合并多余空行"
                },
                new StylePattern
                {
                    Name = "normalize-tabs",
                    Pattern = @"\t",
                    Replacement = "    ",
                    Description = "将Tab替换为4个空格"
                }
            };
        }

        public QualityGateResult ValidateCode(string code, string fileName = "", List<QualityConstraint> customConstraints = null)
        {
            var result = new QualityGateResult { Passed = true };
            var constraints = customConstraints ?? _defaultConstraints;

            foreach (var constraint in constraints.Where(c => c.IsEnabled))
            {
                var violations = CheckConstraint(code, fileName, constraint);
                result.Violations.AddRange(violations);
            }

            // result.HasErrors is calculated property, don't set it directly.
            result.Passed = !result.HasErrors;
            result.Summary = GenerateSummary(result);

            return result;
        }

        public QualityGateResult ValidatePatch(FilePatch patch, List<QualityConstraint> customConstraints = null)
        {
            var result = new QualityGateResult { Passed = true };
            var constraints = (customConstraints ?? _defaultConstraints).ToList();

            var patchConstraint = constraints.FirstOrDefault(c => c.Id == "max-patch-size");
            if (patchConstraint != null)
            {
                if (patch.AddedLines > 300)
                {
                    result.Violations.Add(new QualityViolation
                    {
                        ConstraintId = patchConstraint.Id,
                        ConstraintName = patchConstraint.Name,
                        Severity = QualityConstraintSeverity.Error,
                        Message = $"新增行数 {patch.AddedLines} 超过限制 300",
                        FileName = patch.FileName,
                        SuggestedFix = "建议将变更拆分多次处理"
                    });
                }
                if (patch.RemovedLines > 100)
                {
                    result.Violations.Add(new QualityViolation
                    {
                        ConstraintId = patchConstraint.Id,
                        ConstraintName = patchConstraint.Name,
                        Severity = QualityConstraintSeverity.Error,
                        Message = $"删除行数 {patch.RemovedLines} 超过限制 100",
                        FileName = patch.FileName,
                        SuggestedFix = "建议谨慎处理大量删除操作"
                    });
                }
            }

            var diffViolations = ValidateCode(patch.NewContent, patch.FileName, constraints);
            foreach (var v in diffViolations.Violations)
            {
                v.FileName = patch.FileName;
            }
            result.Violations.AddRange(diffViolations.Violations);

            // result.HasErrors is calculated property, don't set it directly.
            result.Passed = !result.HasErrors;
            result.Summary = GenerateSummary(result);

            return result;
        }

        public QualityGateResult ValidateManifest(ChangeManifest manifest, List<QualityConstraint> customConstraints = null)
        {
            var result = new QualityGateResult { Passed = true };

            if (manifest.TotalFiles > 20)
            {
                result.Violations.Add(new QualityViolation
                {
                    ConstraintId = "manifest-max-files",
                    ConstraintName = "变更文件数限制",
                    Severity = QualityConstraintSeverity.Warning,
                    Message = $"变更涉及 {manifest.TotalFiles} 个文件，超过建议值 20",
                    SuggestedFix = "建议将大型变更拆分为多个小任务"
                });
            }

            foreach (var file in manifest.Files)
            {
                var fileResult = ValidatePatch(file, customConstraints);
                foreach (var v in fileResult.Violations)
                {
                    result.Violations.Add(v);
                }
            }

            if (manifest.OverallRisk == PatchRisk.High)
            {
                result.Violations.Add(new QualityViolation
                {
                    ConstraintId = "manifest-risk",
                    ConstraintName = "整体风险评估",
                    Severity = QualityConstraintSeverity.Error,
                    Message = "变更清单整体风险等级为 High",
                    SuggestedFix = "请仔细评估变更风险并在必要时拆分任务"
                });
            }

            // result.HasErrors is calculated property, don't set it directly.
            result.Passed = !result.HasErrors;
            result.Summary = GenerateSummary(result);

            return result;
        }

        private List<QualityViolation> CheckConstraint(string code, string fileName, QualityConstraint constraint)
        {
            var violations = new List<QualityViolation>();

            if (string.IsNullOrEmpty(constraint.Pattern)) return violations;

            try
            {
                var regex = new Regex(constraint.Pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                var matches = regex.Matches(code);

                if (matches.Count > 0)
                {
                    foreach (Match match in matches)
                    {
                        var violation = new QualityViolation
                        {
                            ConstraintId = constraint.Id,
                            ConstraintName = constraint.Name,
                            Severity = constraint.Severity,
                            Message = constraint.Message,
                            FileName = fileName,
                            CodeSnippet = match.Value.Length > 100 ? match.Value.Substring(0, 100) + "..." : match.Value
                        };

                        if (match.Index > 0)
                        {
                            var lines = code.Substring(0, match.Index).Split('\n');
                            violation.LineNumber = lines.Length;
                        }

                        if (constraint.Type == QualityConstraintType.SizeLimit && constraint.Id == "max-function-lines")
                        {
                            if (match.Length <= GetIntParam(constraint.Parameters, "maxLines", 200))
                            {
                                continue;
                            }
                        }

                        violations.Add(violation);
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
            catch (ArgumentException)
            {
            }

            return violations;
        }

        private int GetIntParam(Dictionary<string, object> parameters, string key, int defaultValue)
        {
            if (parameters.TryGetValue(key, out var value))
            {
                if (value is int i) return i;
                if (value is string s && int.TryParse(s, out var parsed)) return parsed;
            }
            return defaultValue;
        }

        public string EnforceStyle(string code, string fileName = "")
        {
            var result = code;

            foreach (var pattern in _stylePatterns)
            {
                try
                {
                    result = Regex.Replace(result, pattern.Pattern, pattern.Replacement, RegexOptions.Multiline);
                }
                catch
                {
                }
            }

            return result;
        }

        public List<QualityConstraint> GetDefaultConstraints()
        {
            return _defaultConstraints.ToList();
        }

        public List<StylePattern> GetStylePatterns()
        {
            return _stylePatterns.ToList();
        }

        private string GenerateSummary(QualityGateResult result)
        {
            if (result.Passed)
            {
                return "✅ 质量检查通过";
            }

            var sb = new StringBuilder();
            sb.AppendLine("❌ 质量检查未通过:");

            if (result.BlockerCount > 0)
            {
                sb.AppendLine($"  🔴 Blocker: {result.BlockerCount}");
            }
            if (result.ErrorCount > 0)
            {
                sb.AppendLine($"  🔴 Error: {result.ErrorCount}");
            }
            if (result.WarningCount > 0)
            {
                sb.AppendLine($"  🟡 Warning: {result.WarningCount}");
            }
            if (result.InfoCount > 0)
            {
                sb.AppendLine($"  🔵 Info: {result.InfoCount}");
            }

            return sb.ToString();
        }
    }

    public class ChangeBoundaryGuard
    {
        public int MaxFilesPerChange { get; set; } = 20;
        public int MaxLinesAddedPerFile { get; set; } = 1000;
        public int MaxLinesRemovedPerFile { get; set; } = 500;
        public int MaxLinesAddedTotal { get; set; } = 2000;
        public HashSet<string> AllowedFileTypes { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".xml", ".scl", ".awl", ".db", ".udt"
        };
        public HashSet<string> AllowedDirectories { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PlcBlocks", "Types", "ProgramBlocks"
        };

        public List<QualityViolation> CheckBoundary(ChangeManifest manifest)
        {
            var violations = new List<QualityViolation>();

            if (manifest.TotalFiles > MaxFilesPerChange)
            {
                violations.Add(new QualityViolation
                {
                    ConstraintId = "boundary-max-files",
                    ConstraintName = "文件数限制",
                    Severity = QualityConstraintSeverity.Error,
                    Message = $"变更涉及 {manifest.TotalFiles} 个文件，超过限制 {MaxFilesPerChange}",
                    SuggestedFix = $"请将变更拆分为不超过 {MaxFilesPerChange} 个文件的小批次"
                });
            }

            if (manifest.TotalAddedLines > MaxLinesAddedTotal)
            {
                violations.Add(new QualityViolation
                {
                    ConstraintId = "boundary-max-lines",
                    ConstraintName = "总行数限制",
                    Severity = QualityConstraintSeverity.Error,
                    Message = $"变更新增 {manifest.TotalAddedLines} 行，超过限制 {MaxLinesAddedTotal}",
                    SuggestedFix = "请将变更拆分处理"
                });
            }

            foreach (var file in manifest.Files)
            {
                if (!AllowedFileTypes.Any(ext => file.FileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add(new QualityViolation
                    {
                        ConstraintId = "boundary-file-type",
                        ConstraintName = "文件类型限制",
                        Severity = QualityConstraintSeverity.Error,
                        Message = $"文件类型 {Path.GetExtension(file.FileName)} 不在允许列表中",
                        FileName = file.FileName,
                        SuggestedFix = $"仅允许修改: {string.Join(", ", AllowedFileTypes)}"
                    });
                }

                if (file.AddedLines > MaxLinesAddedPerFile)
                {
                    violations.Add(new QualityViolation
                    {
                        ConstraintId = "boundary-file-lines",
                        ConstraintName = "单文件行数限制",
                        Severity = QualityConstraintSeverity.Warning,
                        Message = $"文件 {file.FileName} 新增 {file.AddedLines} 行，超过建议值 {MaxLinesAddedPerFile}",
                        FileName = file.FileName,
                        SuggestedFix = "考虑将大文件变更拆分"
                    });
                }
            }

            return violations;
        }
    }
}
