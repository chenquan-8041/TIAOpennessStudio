using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services
{
    public class EvalCase
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public string Description { get; set; }
        public EvalCategory Category { get; set; }
        public string Prompt { get; set; }
        public string ExpectedPattern { get; set; }
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public Dictionary<string, string> Context { get; set; } = new Dictionary<string, string>();
        public EvalExpectation Expectation { get; set; } = new EvalExpectation();
    }

    public enum EvalCategory
    {
        CodeGeneration,
        CodeModification,
        LogicAnalysis,
        XmlToScl,
        UdtUnderstanding,
        Diagnostic
    }

    public class EvalExpectation
    {
        public double MinScore { get; set; } = 0.6;
        public List<string> RequiredPatterns { get; set; } = new List<string>();
        public List<string> ForbiddenPatterns { get; set; } = new List<string>();
        public int MaxToolCalls { get; set; } = 10;
        public int MaxLatencyMs { get; set; } = 60000;

        /// <summary>
        /// G-2: when true, this case is compiled after generation and compile success
        /// becomes a HARD gate — a failed compile forces the case to fail regardless of
        /// pattern/judge scores. Requires a compile delegate to be supplied to the run
        /// (see <see cref="EvalConfig.CompileAsync"/>); if none is available the case
        /// degrades gracefully (compile skipped, not failed).
        /// </summary>
        public bool RequireCompile { get; set; } = false;
    }

    /// <summary>
    /// G-2: evaluation run configuration. All advanced scoring is OFF by default so the
    /// legacy pattern-matching behavior is preserved unless explicitly enabled.
    /// </summary>
    public class EvalConfig
    {
        /// <summary>Enable LLM-as-a-Judge semantic scoring (uses the same aiService).</summary>
        public bool UseLlmJudge { get; set; } = false;

        /// <summary>Enable compile-pass-rate as a hard gate for cases with RequireCompile.</summary>
        public bool UseCompileGate { get; set; } = false;

        /// <summary>
        /// Weights for the composite score when advanced scoring is enabled. Pattern +
        /// judge weights are normalized; compile is a separate hard gate, not a weight.
        /// </summary>
        public double PatternWeight { get; set; } = 0.4;
        public double JudgeWeight { get; set; } = 0.6;

        /// <summary>
        /// Optional compile delegate: takes generated SCL, returns (success, summary).
        /// Supplied by the caller (C# side wires it to CompileAsync / compile_structured).
        /// When null, compile-gated cases skip compilation (degrade, don't fail).
        /// </summary>
        public Func<string, EvalCase, Task<(bool Success, string Summary)>> CompileAsync { get; set; }
    }

    /// <summary>G-2: structured verdict from the LLM-as-a-Judge rubric scoring.</summary>
    public class JudgeVerdict
    {
        public double Syntax { get; set; }        // 0..1
        public double Types { get; set; }         // 0..1
        public double Naming { get; set; }        // 0..1
        public double Logic { get; set; }         // 0..1
        public double Standards { get; set; }     // 0..1
        public string Rationale { get; set; }
        public bool Parsed { get; set; }          // false when the judge output couldn't be parsed

        /// <summary>Equal-weighted mean of the five rubric dimensions.</summary>
        public double Overall =>
            (Syntax + Types + Naming + Logic + Standards) / 5.0;
    }

    public class EvalResult
    {
        public string CaseId { get; set; }
        public string CaseName { get; set; }
        public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
        public bool Passed { get; set; }
        public double Score { get; set; }
        public string Output { get; set; }
        public List<string> DetectedPatterns { get; set; } = new List<string>();
        public List<string> MissingPatterns { get; set; } = new List<string>();
        public List<string> ForbiddenDetected { get; set; } = new List<string>();
        public int ToolCallsUsed { get; set; }
        public long LatencyMs { get; set; }
        public string Error { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        // ---- G-2 additions (populated only when advanced scoring is enabled) ----
        /// <summary>Pattern-match sub-score (legacy scoring), kept as one dimension.</summary>
        public double PatternScore { get; set; }
        /// <summary>LLM-as-a-Judge verdict (null when judge disabled/unavailable).</summary>
        public JudgeVerdict Judge { get; set; }
        /// <summary>Whether this case was compiled (RequireCompile + delegate present).</summary>
        public bool CompileAttempted { get; set; }
        /// <summary>Compile outcome when attempted (hard gate).</summary>
        public bool CompilePassed { get; set; }
        public string CompileSummary { get; set; }
    }

    public class EvalReport
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string EvaluatorVersion { get; set; }
        public List<EvalResult> Results { get; set; } = new List<EvalResult>();
        public EvalSummary Summary { get; set; } = new EvalSummary();
        public string RawOutput { get; set; }
    }

    public class EvalSummary
    {
        public int TotalCases { get; set; }
        public int PassedCases { get; set; }
        public int FailedCases { get; set; }
        public double PassRate => TotalCases > 0 ? (double)PassedCases / TotalCases * 100 : 0;
        public double AverageScore { get; set; }
        public double AverageLatencyMs { get; set; }
        public int TotalToolCalls { get; set; }
        public Dictionary<EvalCategory, CategoryScore> CategoryScores { get; set; } = new Dictionary<EvalCategory, CategoryScore>();

        // ---- G-2 additions ----
        /// <summary>Number of cases where compilation was attempted (RequireCompile + delegate).</summary>
        public int CompileAttempted { get; set; }
        /// <summary>Number of attempted compiles that passed.</summary>
        public int CompilePassed { get; set; }
        /// <summary>Compile pass rate among attempted cases (the G-2 hard metric).</summary>
        public double CompilePassRate => CompileAttempted > 0 ? (double)CompilePassed / CompileAttempted * 100 : 0;
        /// <summary>Average LLM-as-a-Judge overall score among judged cases (0..1).</summary>
        public double AverageJudgeScore { get; set; }
        public int JudgedCases { get; set; }
    }

    public class CategoryScore
    {
        public int Total { get; set; }
        public int Passed { get; set; }
        public double PassRate => Total > 0 ? (double)Passed / Total * 100 : 0;
        public double AverageScore { get; set; }
    }

    public class EvalHarness : IEvalHarness
    {
        private readonly string _evaluatorVersion = "1.0.0";

        public List<EvalCase> GetDefaultTestCases()
        {
            return new List<EvalCase>
            {
                new EvalCase
                {
                    Id = "gen-001",
                    Name = "电机启停控制",
                    Description = "生成基本的电机启停 FB 代码",
                    Category = EvalCategory.CodeGeneration,
                    Prompt = "生成一个电机控制功能块，包含启动、停止按钮输入和运行输出",
                    BlockName = "MotorControl",
                    BlockType = "FB",
                    Expectation = new EvalExpectation
                    {
                        RequiredPatterns = new List<string> { "FUNCTION_BLOCK", "VAR_INPUT", "VAR_OUTPUT", "IF", "END_IF" },
                        ForbiddenPatterns = new List<string> { "GOTO", "DELETE", "DROP" },
                        MaxLatencyMs = 30000
                    }
                },
                new EvalCase
                {
                    Id = "gen-002",
                    Name = "计数器逻辑",
                    Description = "生成计数器功能代码",
                    Category = EvalCategory.CodeGeneration,
                    Prompt = "生成一个递增计数器功能块，当触发信号到来时计数加1",
                    BlockName = "Counter",
                    BlockType = "FC",
                    Expectation = new EvalExpectation
                    {
                        RequiredPatterns = new List<string> { "FUNCTION", "VAR_INPUT", "VAR_OUTPUT", "IF", "THEN" },
                        ForbiddenPatterns = new List<string> { "GOTO", "EXECUTE" },
                        MaxLatencyMs = 30000
                    }
                },
                new EvalCase
                {
                    Id = "ana-001",
                    Name = "逻辑分析",
                    Description = "分析 LAD/FBD 代码逻辑",
                    Category = EvalCategory.LogicAnalysis,
                    Prompt = "分析以下代码的逻辑并给出说明",
                    BlockName = "AnalysisTarget",
                    BlockType = "FB",
                    Expectation = new EvalExpectation
                    {
                        MinScore = 0.5,
                        MaxLatencyMs = 45000
                    }
                },
                new EvalCase
                {
                    Id = "xml-001",
                    Name = "XML转SCL",
                    Description = "将 LAD XML 转换为 SCL",
                    Category = EvalCategory.XmlToScl,
                    Prompt = "将以下 LAD/FBD XML 转换为等效的 SCL 代码",
                    BlockName = "XmlConversion",
                    BlockType = "FB",
                    Expectation = new EvalExpectation
                    {
                        RequiredPatterns = new List<string> { "FUNCTION_BLOCK", "BEGIN", "IF", "END_IF" },
                        ForbiddenPatterns = new List<string> { "<?xml" },
                        MaxLatencyMs = 60000
                    }
                },
                new EvalCase
                {
                    Id = "udt-001",
                    Name = "UDT结构理解",
                    Description = "基于 UDT 类型生成代码",
                    Category = EvalCategory.UdtUnderstanding,
                    Prompt = "基于 MotorData UDT 生成初始化代码",
                    BlockName = "MotorInit",
                    BlockType = "FB",
                    Context = new Dictionary<string, string>
                    {
                        { "UDT", "MotorData : STRUCT\n  Speed : Int;\n  Status : Bool;\n  ErrorCode : Int;\nEND_STRUCT" }
                    },
                    Expectation = new EvalExpectation
                    {
                        RequiredPatterns = new List<string> { "MotorData", "#Speed", "#Status" },
                        ForbiddenPatterns = new List<string> { "GOTO", "DELETE" },
                        MaxLatencyMs = 30000
                    }
                },
                new EvalCase
                {
                    Id = "mod-001",
                    Name = "代码修改",
                    Description = "在现有代码基础上添加新功能",
                    Category = EvalCategory.CodeModification,
                    Prompt = "在现有电机控制块中添加急停功能",
                    BlockName = "MotorControl",
                    BlockType = "FB",
                    Expectation = new EvalExpectation
                    {
                        RequiredPatterns = new List<string> { "EmergencyStop", "IF", "#Motor" },
                        ForbiddenPatterns = new List<string> { "DELETE", "DROP TABLE" },
                        MaxToolCalls = 5,
                        MaxLatencyMs = 45000
                    }
                },
                new EvalCase
                {
                    Id = "diag-001",
                    Name = "故障诊断",
                    Description = "诊断代码中的潜在问题",
                    Category = EvalCategory.Diagnostic,
                    Prompt = "检查以下代码中可能存在的运行时错误风险",
                    BlockName = "DiagTarget",
                    BlockType = "FB",
                    Expectation = new EvalExpectation
                    {
                        MinScore = 0.4,
                        MaxLatencyMs = 30000
                    }
                }
            };
        }

        public async Task<EvalReport> RunEvaluationAsync(List<EvalCase> cases, IAIEngineService aiService, IProjectIndexService indexService)
        {
            var report = new EvalReport
            {
                EvaluatorVersion = _evaluatorVersion
            };

            foreach (var testCase in cases)
            {
                var result = new EvalResult
                {
                    CaseId = testCase.Id,
                    CaseName = testCase.Name
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    string output;
                    if (testCase.Category == EvalCategory.XmlToScl)
                    {
                        var xmlContext = (testCase.Context != null && testCase.Context.ContainsKey("xml")) ? testCase.Context["xml"] : "<Document></Document>";
                        output = await aiService.ConvertXmlAsync(xmlContext, testCase.Prompt, "scl");
                    }
                    else
                    {
                        var context = BuildContext(testCase, indexService);
                        output = await aiService.GenerateSclAsync(testCase.Prompt, context);
                    }

                    sw.Stop();
                    result.Output = output;
                    result.LatencyMs = sw.ElapsedMilliseconds;

                    EvaluateResult(testCase, result, output);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    result.LatencyMs = sw.ElapsedMilliseconds;
                    result.Error = ex.Message;
                    result.Passed = false;
                    result.Score = 0;
                }

                report.Results.Add(result);
            }

            ComputeSummary(report);
            return report;
        }

        public async Task<EvalReport> RunQuickEvalAsync(IAIEngineService aiService)
        {
            var cases = GetDefaultTestCases().Take(3).ToList();
            var mockIndexService = new MockProjectIndexService();
            return await RunEvaluationAsync(cases, aiService, mockIndexService);
        }

        /// <summary>
        /// G-2 advanced evaluation: same generation flow as <see cref="RunEvaluationAsync"/>,
        /// but composites three signals per case:
        ///   1. pattern match (legacy) — one dimension, weighted;
        ///   2. LLM-as-a-Judge rubric (syntax/types/naming/logic/standards) — weighted;
        ///   3. compile pass — a HARD gate (a failed compile fails the case outright).
        ///
        /// All three are OFF by default via <paramref name="config"/>, so passing a default
        /// EvalConfig reproduces the legacy pattern-only behavior. This lets the caller
        /// opt into semantic + compile-driven evaluation (evaluation-driven model selection)
        /// without changing existing callers.
        /// </summary>
        public async Task<EvalReport> RunEvaluationAsync(
            List<EvalCase> cases, IAIEngineService aiService, IProjectIndexService indexService, EvalConfig config)
        {
            config = config ?? new EvalConfig();
            var report = new EvalReport { EvaluatorVersion = _evaluatorVersion + "+g2" };

            foreach (var testCase in cases)
            {
                var result = new EvalResult { CaseId = testCase.Id, CaseName = testCase.Name };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    string output;
                    if (testCase.Category == EvalCategory.XmlToScl)
                    {
                        var xmlContext = (testCase.Context != null && testCase.Context.ContainsKey("xml")) ? testCase.Context["xml"] : "<Document></Document>";
                        output = await aiService.ConvertXmlAsync(xmlContext, testCase.Prompt, "scl");
                    }
                    else
                    {
                        var context = BuildContext(testCase, indexService);
                        output = await aiService.GenerateSclAsync(testCase.Prompt, context);
                    }
                    sw.Stop();
                    result.Output = output;
                    result.LatencyMs = sw.ElapsedMilliseconds;

                    // 1) Pattern match (legacy) → fills PatternScore + forbidden gate.
                    EvaluateResult(testCase, result, output);
                    result.PatternScore = result.Score;

                    // 2) LLM-as-a-Judge (optional).
                    if (config.UseLlmJudge)
                        result.Judge = await JudgeWithLlmAsync(aiService, testCase, output);

                    // 3) Compile gate (optional, hard).
                    if (config.UseCompileGate && testCase.Expectation.RequireCompile && config.CompileAsync != null)
                    {
                        result.CompileAttempted = true;
                        try
                        {
                            var (ok, summary) = await config.CompileAsync(output, testCase);
                            result.CompilePassed = ok;
                            result.CompileSummary = summary;
                        }
                        catch (Exception cex)
                        {
                            // Infra error compiling — record but don't crash the run.
                            result.CompilePassed = false;
                            result.CompileSummary = "Compile error: " + cex.Message;
                        }
                    }

                    ApplyCompositeScore(testCase, result, config);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    result.LatencyMs = sw.ElapsedMilliseconds;
                    result.Error = ex.Message;
                    result.Passed = false;
                    result.Score = 0;
                }
                report.Results.Add(result);
            }

            ComputeSummary(report);
            ComputeG2Summary(report);
            return report;
        }

        /// <summary>
        /// Combine pattern + judge into a composite score, then apply the compile hard gate.
        /// Forbidden patterns already zeroed the score in EvaluateResult; keep that.
        /// </summary>
        private static void ApplyCompositeScore(EvalCase testCase, EvalResult result, EvalConfig config)
        {
            // Forbidden content already failed the case in EvaluateResult — respect it.
            bool forbiddenHit = result.ForbiddenDetected != null && result.ForbiddenDetected.Count > 0;

            double composite;
            if (config.UseLlmJudge && result.Judge != null && result.Judge.Parsed)
            {
                var pw = Math.Max(0, config.PatternWeight);
                var jw = Math.Max(0, config.JudgeWeight);
                var wsum = pw + jw;
                if (wsum <= 0) { pw = 0.4; jw = 0.6; wsum = 1.0; }
                composite = (result.PatternScore * pw + result.Judge.Overall * jw) / wsum;
            }
            else
            {
                composite = result.PatternScore; // judge off/unparsed → pattern only
            }

            if (forbiddenHit) composite = 0;

            result.Score = Math.Max(0, Math.Min(1, composite));

            // Pass decision: meet MinScore AND (if compile was a required gate) compile passed.
            bool passesScore = result.Score >= testCase.Expectation.MinScore && !forbiddenHit;
            if (result.CompileAttempted)
                result.Passed = passesScore && result.CompilePassed; // compile is a hard gate
            else
                result.Passed = passesScore;
        }

        /// <summary>
        /// LLM-as-a-Judge: ask the model to score the generated SCL on five rubric
        /// dimensions and return strict JSON. Never throws — an unparseable/failed judge
        /// yields a Verdict with Parsed=false so the composite falls back to pattern only.
        /// </summary>
        private async Task<JudgeVerdict> JudgeWithLlmAsync(IAIEngineService aiService, EvalCase testCase, string output)
        {
            var verdict = new JudgeVerdict { Parsed = false };
            try
            {
                var prompt =
                    "You are a strict Siemens SCL (S7-1200/1500) code reviewer acting as an automated judge.\n" +
                    "Score the CANDIDATE code for the TASK on five dimensions, each 0.0–1.0:\n" +
                    "  syntax    — valid SCL syntax, statements terminated, control blocks closed\n" +
                    "  types     — correct S7 data types, no forbidden aliases, declarations consistent\n" +
                    "  naming    — Siemens naming conventions, length <=24, no reserved words\n" +
                    "  logic     — the logic actually satisfies the task intent (semantic correctness)\n" +
                    "  standards — adheres to common PLC best practices\n\n" +
                    "Return ONLY a JSON object, no prose:\n" +
                    "{\"syntax\":0.0,\"types\":0.0,\"naming\":0.0,\"logic\":0.0,\"standards\":0.0,\"rationale\":\"...\"}\n\n" +
                    "TASK:\n" + (testCase.Prompt ?? "") + "\n\n" +
                    "CANDIDATE:\n```scl\n" + (output ?? "") + "\n```";

                var raw = await aiService.GenerateSclAsync(prompt, "", null, null, intent: "Chat");
                if (string.IsNullOrWhiteSpace(raw)) return verdict;

                var json = ExtractJsonObject(raw);
                if (string.IsNullOrWhiteSpace(json)) return verdict;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                verdict.Syntax = ReadScore(root, "syntax");
                verdict.Types = ReadScore(root, "types");
                verdict.Naming = ReadScore(root, "naming");
                verdict.Logic = ReadScore(root, "logic");
                verdict.Standards = ReadScore(root, "standards");
                verdict.Rationale = root.TryGetProperty("rationale", out var r) ? (r.GetString() ?? "") : "";
                verdict.Parsed = true;
            }
            catch
            {
                verdict.Parsed = false; // fall back to pattern-only scoring
            }
            return verdict;
        }

        private static double ReadScore(JsonElement root, string key)
        {
            if (!root.TryGetProperty(key, out var v)) return 0;
            double d;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out d)) { }
            else if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out d)) { }
            else return 0;
            return Math.Max(0, Math.Min(1, d));
        }

        /// <summary>Extract the first balanced {...} JSON object from arbitrary LLM text.</summary>
        private static string ExtractJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int start = text.IndexOf('{');
            if (start < 0) return null;
            int depth = 0;
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        private static void ComputeG2Summary(EvalReport report)
        {
            report.Summary.CompileAttempted = report.Results.Count(r => r.CompileAttempted);
            report.Summary.CompilePassed = report.Results.Count(r => r.CompileAttempted && r.CompilePassed);
            var judged = report.Results.Where(r => r.Judge != null && r.Judge.Parsed).ToList();
            report.Summary.JudgedCases = judged.Count;
            report.Summary.AverageJudgeScore = judged.Count > 0 ? judged.Average(r => r.Judge.Overall) : 0;
        }

        private string BuildContext(EvalCase testCase, IProjectIndexService indexService)
        {
            var sb = new StringBuilder();
            foreach (var kv in testCase.Context)
            {
                sb.AppendLine($"// {kv.Key}:");
                sb.AppendLine(kv.Value);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private void EvaluateResult(EvalCase testCase, EvalResult result, string output)
        {
            result.Score = 0;
            var patternMatches = 0;
            var totalPatterns = testCase.Expectation.RequiredPatterns.Count;

            foreach (var pattern in testCase.Expectation.RequiredPatterns)
            {
                if (output.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.DetectedPatterns.Add(pattern);
                    patternMatches++;
                }
                else
                {
                    result.MissingPatterns.Add(pattern);
                }
            }

            foreach (var forbidden in testCase.Expectation.ForbiddenPatterns)
            {
                if (output.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.ForbiddenDetected.Add(forbidden);
                }
            }

            if (result.ForbiddenDetected.Count > 0)
            {
                result.Score = 0;
                result.Passed = false;
                return;
            }

            if (totalPatterns > 0)
            {
                result.Score = (double)patternMatches / totalPatterns;
            }
            else
            {
                result.Score = 0.7;
            }

            if (result.LatencyMs > testCase.Expectation.MaxLatencyMs)
            {
                result.Score *= 0.8;
            }

            if (result.ToolCallsUsed > testCase.Expectation.MaxToolCalls && testCase.Expectation.MaxToolCalls > 0)
            {
                result.Score *= 0.9;
            }

            result.Score = Math.Max(0, Math.Min(1, result.Score));
            result.Passed = result.Score >= testCase.Expectation.MinScore;
        }

        private void ComputeSummary(EvalReport report)
        {
            report.Summary = new EvalSummary
            {
                TotalCases = report.Results.Count,
                PassedCases = report.Results.Count(r => r.Passed),
                FailedCases = report.Results.Count(r => !r.Passed),
                AverageScore = report.Results.Count > 0 ? report.Results.Average(r => r.Score) : 0,
                AverageLatencyMs = report.Results.Count > 0 ? report.Results.Average(r => r.LatencyMs) : 0,
                TotalToolCalls = report.Results.Sum(r => r.ToolCallsUsed)
            };

            foreach (EvalCategory category in Enum.GetValues(typeof(EvalCategory)))
            {
                var categoryResults = report.Results.Where(r =>
                {
                    var testCase = GetDefaultTestCases().FirstOrDefault(c => c.Id == r.CaseId);
                    return testCase?.Category == category;
                }).ToList();

                if (categoryResults.Count > 0)
                {
                    report.Summary.CategoryScores[category] = new CategoryScore
                    {
                        Total = categoryResults.Count,
                        Passed = categoryResults.Count(r => r.Passed),
                        AverageScore = categoryResults.Average(r => r.Score)
                    };
                }
            }
        }

        public EvalReport LoadReport(string reportPath)
        {
            if (!File.Exists(reportPath)) return null;
            var json = File.ReadAllText(reportPath);
            return JsonSerializer.Deserialize<EvalReport>(json);
        }

        public void SaveReport(EvalReport report, string reportPath)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(report, options);
            File.WriteAllText(reportPath, json);
        }

        public string GenerateReportSummary(EvalReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 📊 AI 能力评测报告");
            sb.AppendLine();
            sb.AppendLine($"**评测时间**: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"**评测版本**: {report.EvaluatorVersion}");
            sb.AppendLine();
            sb.AppendLine("## 📈 总体概览");
            sb.AppendLine();
            sb.AppendLine($"| 指标 | 值 |");
            sb.AppendLine($"|------|-----|");
            sb.AppendLine($"| 总用例数 | {report.Summary.TotalCases} |");
            sb.AppendLine($"| 通过数 | {report.Summary.PassedCases} |");
            sb.AppendLine($"| 失败数 | {report.Summary.FailedCases} |");
            sb.AppendLine($"| 通过率 | {report.Summary.PassRate:F1}% |");
            sb.AppendLine($"| 平均得分 | {report.Summary.AverageScore:P1} |");
            sb.AppendLine($"| 平均延迟 | {report.Summary.AverageLatencyMs:F0}ms |");
            // G-2: show compile pass rate (hard metric) + judge average when present.
            if (report.Summary.CompileAttempted > 0)
                sb.AppendLine($"| 编译通过率 (硬指标) | {report.Summary.CompilePassRate:F1}% ({report.Summary.CompilePassed}/{report.Summary.CompileAttempted}) |");
            if (report.Summary.JudgedCases > 0)
                sb.AppendLine($"| LLM-as-a-Judge 平均分 | {report.Summary.AverageJudgeScore:P1} ({report.Summary.JudgedCases} 例) |");
            sb.AppendLine();

            if (report.Summary.CategoryScores.Count > 0)
            {
                sb.AppendLine("## 📂 分类得分");
                sb.AppendLine();
                sb.AppendLine($"| 类别 | 总数 | 通过 | 通过率 | 平均分 |");
                sb.AppendLine($"|------|------|------|--------|--------|");
                foreach (var kv in report.Summary.CategoryScores)
                {
                    sb.AppendLine($"| {kv.Key} | {kv.Value.Total} | {kv.Value.Passed} | {kv.Value.PassRate:F1}% | {kv.Value.AverageScore:P1} |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("## 📋 详细结果");
            sb.AppendLine();
            foreach (var result in report.Results)
            {
                var status = result.Passed ? "✅" : "❌";
                sb.AppendLine($"### {status} {result.CaseName}");
                sb.AppendLine($"- 得分: {result.Score:P1}");
                sb.AppendLine($"- 延迟: {result.LatencyMs}ms");
                if (result.MissingPatterns.Count > 0)
                {
                    sb.AppendLine($"- 缺失模式: {string.Join(", ", result.MissingPatterns)}");
                }
                if (result.ForbiddenDetected.Count > 0)
                {
                    sb.AppendLine($"- 禁止模式: {string.Join(", ", result.ForbiddenDetected)}");
                }
                if (!string.IsNullOrEmpty(result.Error))
                {
                    sb.AppendLine($"- 错误: {result.Error}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private class MockProjectIndexService : IProjectIndexService
        {
            public DependencyChainResult QueryCallChain(string projectKey, string blockName, string direction = "both", int maxDepth = 5, int maxNodeCount = 200) => new DependencyChainResult { Root = blockName, Direction = direction };
            public DependencyChainResult QueryVariableImpact(string projectKey, string canonicalPath, string direction = "both", int maxDepth = 5, int maxNodeCount = 200) => new DependencyChainResult { Root = canonicalPath, Direction = direction };
        }
    }
}
