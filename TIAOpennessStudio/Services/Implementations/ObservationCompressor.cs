using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.AICore;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Phase 6: Compresses long tool outputs to prevent token explosion in ReAct loops.
    ///
    /// Strategy:
    ///   1. Under threshold → return as-is (no cost)
    ///   2. Rule-based compression → extract structured summary (fast, no LLM cost)
    ///   3. LLM summarization → only for very large or unstructured outputs
    ///
    /// Compression preserves:
    ///   - Block names, error messages, file paths
    ///   - Success/failure status
    ///   - Line counts, sizes, counts
    ///   - First N and last N characters of code
    /// </summary>
    public class ObservationCompressor : IObservationCompressor
    {
        private readonly IAIEngineService _aiService;
        private readonly ILocalLlmService _localLlmService;

        // Pattern to extract block names from TIA output
        private static readonly Regex BlockNamePattern = new Regex(
            @"(?:FB|FC|DB|UDT|OB)[_\s]?\d*\s*[_\s]?(\w+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Pattern to extract error messages
        private static readonly Regex ErrorPattern = new Regex(
            @"(?:Error|ERROR|error|Failed|FAILED|failed)[:\s]+(.+?)(?:\n|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ObservationCompressor(IAIEngineService aiService, ILocalLlmService localLlmService = null)
        {
            _aiService = aiService;
            _localLlmService = localLlmService;
        }

        public async Task<string> CompressAsync(string output, string toolName, int threshold = 500, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(output))
                return output ?? "";

            // Under threshold → return as-is
            if (output.Length <= threshold)
                return output;

            // Try rule-based compression first
            var compressed = CompressByRules(output, toolName);
            if (compressed != null)
                return compressed;

            // Unknown tool type → try LLM summarization for large outputs
            if (output.Length > 2000)
            {
                try
                {
                    var summary = await SummarizeWithLlmAsync(output, toolName, ct);
                    if (!string.IsNullOrWhiteSpace(summary))
                        return summary;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[ObservationCompressor] LLM summarization failed: {ex.Message}");
                }
            }

            // Last resort: truncate with metadata
            return TruncateWithMetadata(output);
        }

        /// <summary>
        /// Rule-based compression: extract structured info from the output.
        /// Returns null if the output doesn't match known patterns (caller should try LLM).
        /// </summary>
        private string CompressByRules(string output, string toolName)
        {
            var toolLower = (toolName ?? "").ToLowerInvariant();

            // ── Compile output ──
            if (toolLower == "compile")
            {
                return CompressCompileOutput(output);
            }

            // ── Export block output ──
            if (toolLower == "export_block")
            {
                return CompressExportOutput(output);
            }

            // ── Search project output ──
            if (toolLower == "search_project" || toolLower == "get_project_tree")
            {
                return CompressSearchOutput(output);
            }

            // ── Unknown tool: return null so LLM summarization can try ──
            return null;
        }

        /// <summary>
        /// Compress compile output: keep errors/warnings, drop successful block list.
        /// </summary>
        private string CompressCompileOutput(string output)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Compile Output — compressed]");

            // Extract error lines
            var errors = new List<string>();
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.IndexOf("Error:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    trimmed.IndexOf("error:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    trimmed.IndexOf("Failed:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    trimmed.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    errors.Add(trimmed);
                }
            }

            if (errors.Count > 0)
            {
                sb.AppendLine($"Compile FAILED with {errors.Count} error(s):");
                foreach (var err in errors.Take(10))
                    sb.AppendLine($"  {err}");
                if (errors.Count > 10)
                    sb.AppendLine($"  ... and {errors.Count - 10} more errors");
            }
            else
            {
                sb.AppendLine("Compile SUCCEEDED — no errors detected.");
            }

            // Extract block name references
            var blockNames = ExtractBlockNames(output);
            if (blockNames.Count > 0)
            {
                sb.AppendLine($"Blocks referenced: {string.Join(", ", blockNames.Take(10))}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Compress export output: keep first/last N lines of code + block info.
        /// </summary>
        private string CompressExportOutput(string output)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Export Output — compressed]");

            var lines = output.Split('\n');
            var totalLines = lines.Length;

            sb.AppendLine($"Exported block: {totalLines} lines total.");

            // Extract block name
            var blockNames = ExtractBlockNames(output);
            if (blockNames.Count > 0)
                sb.AppendLine($"Block: {blockNames[0]}");

            // First 10 lines (header/declarations)
            if (lines.Length > 0)
            {
                sb.AppendLine("--- First 10 lines ---");
                for (int i = 0; i < Math.Min(10, lines.Length); i++)
                    sb.AppendLine(lines[i]);
            }

            // Last 5 lines (END_FUNCTION_BLOCK etc.)
            if (lines.Length > 15)
            {
                sb.AppendLine($"--- ... {lines.Length - 15} lines omitted ... ---");
                sb.AppendLine("--- Last 5 lines ---");
                for (int i = Math.Max(0, lines.Length - 5); i < lines.Length; i++)
                    sb.AppendLine(lines[i]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Compress search output: count results, list names, drop details.
        /// </summary>
        private string CompressSearchOutput(string output)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Search Output — compressed]");

            // Count lines with block names
            var blockNames = ExtractBlockNames(output);
            if (blockNames.Count > 0)
            {
                sb.AppendLine($"Found {blockNames.Count} block(s): {string.Join(", ", blockNames.Take(20))}");
                if (blockNames.Count > 20)
                    sb.AppendLine($"  ... and {blockNames.Count - 20} more");
            }
            else
            {
                // Count non-empty lines
                var nonEmptyLines = output.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l));
                sb.AppendLine($"{nonEmptyLines} result(s) found.");
                // Include first few lines
                var lines = output.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(5);
                foreach (var line in lines)
                    sb.AppendLine($"  {line.Trim()}");
                if (nonEmptyLines > 5)
                    sb.AppendLine($"  ... and {nonEmptyLines - 5} more");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generic compression: extract known patterns, truncate.
        /// </summary>
        private string CompressGeneric(string output)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Output — compressed]");

            // Extract errors
            var errors = ErrorPattern.Matches(output);
            if (errors.Count > 0)
            {
                sb.AppendLine($"Errors detected ({errors.Count}):");
                foreach (Match m in errors)
                {
                    var msg = m.Groups[1].Value.Trim();
                    if (msg.Length > 120) msg = msg.Substring(0, 120) + "...";
                    sb.AppendLine($"  - {msg}");
                }
            }

            // Extract block names
            var blockNames = ExtractBlockNames(output);
            if (blockNames.Count > 0)
            {
                sb.AppendLine($"Referenced: {string.Join(", ", blockNames.Take(15))}");
            }

            // Truncate with stats
            var lines = output.Split('\n');
            sb.AppendLine($"Total: {lines.Length} lines, {output.Length} chars");
            sb.AppendLine("First 5 lines:");
            for (int i = 0; i < Math.Min(5, lines.Length); i++)
                sb.AppendLine($"  {lines[i].Trim()}");

            return sb.ToString();
        }

        /// <summary>
        /// LLM-based summarization for outputs that rule-based compression can't handle well.
        /// </summary>
        private async Task<string> SummarizeWithLlmAsync(string output, string toolName, CancellationToken ct)
        {
            if (output.Length <= 2000)
                return null;

            var prompt = BuildSummarizationPrompt(output, toolName);
            var isLocal = ChatModelRouting.IsLocalModelSelected(AIConfig.Load(), _localLlmService?.CurrentModel);
            try
            {
                if (isLocal && _localLlmService != null)
                {
                    var summary = await _localLlmService.InvokeAsync(prompt, OllamaTaskKind.Completion, ct: ct);
                    if (!string.IsNullOrWhiteSpace(summary) && summary.Length < output.Length)
                        return $"[Local LLM Summary of {toolName} output]\n{summary}";
                }
                else
                {
                    var summary = await _aiService.GenerateSclAsync(prompt);
                    if (!string.IsNullOrWhiteSpace(summary) && summary.Length < output.Length)
                        return $"[LLM Summary of {toolName} output]\n{summary}";
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ObservationCompressor] LLM summarization failed: {ex.Message}");
            }
            return null;
        }

        private string BuildSummarizationPrompt(string output, string toolName)
        {
            // Truncate input to avoid sending massive prompts for summarization
            var truncated = output.Length > 4000 ? output.Substring(0, 4000) + "\n... (truncated)" : output;

            return string.Join("\n",
                "Summarize the following TIA Portal tool output in 2-4 sentences.",
                "Focus on: what happened, key results, any errors or warnings.",
                "Output ONLY the summary text, no JSON, no markdown.",
                "",
                $"Tool: {toolName}",
                "",
                truncated);
        }

        /// <summary>
        /// Last resort: truncate with metadata header.
        /// </summary>
        private string TruncateWithMetadata(string output)
        {
            var lines = output.Split('\n');
            var sb = new StringBuilder();
            sb.AppendLine($"[Truncated: {lines.Length} lines, {output.Length} chars total]");

            // Keep first 15 lines
            for (int i = 0; i < Math.Min(15, lines.Length); i++)
                sb.AppendLine(lines[i]);

            if (lines.Length > 15)
            {
                sb.AppendLine($"... ({lines.Length - 15} lines omitted) ...");
                // Keep last 5 lines
                for (int i = Math.Max(15, lines.Length - 5); i < lines.Length; i++)
                    sb.AppendLine(lines[i]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Compress a batch of parallel tool results into a single merged summary.
        /// 1. Each result is individually compressed (rule-based or truncation).
        /// 2. Results are merged with labeled headers.
        /// 3. If merged total exceeds 2000 chars, generate an LLM summary.
        /// </summary>
        public async Task<string> CompressBatchAsync(List<(string toolName, string output)> results, int threshold = 500, CancellationToken ct = default)
        {
            if (results == null || results.Count == 0)
                return "";

            // Single result — just use standard compression
            if (results.Count == 1)
            {
                return await CompressAsync(results[0].output, results[0].toolName, threshold, ct);
            }

            // Compress each result individually
            var compressed = new List<(string toolName, string output)>();
            foreach (var (toolName, output) in results)
            {
                var comp = await CompressAsync(output, toolName, threshold, ct);
                compressed.Add((toolName, comp));
            }

            // Merge into labeled blocks
            var sb = new StringBuilder();
            sb.AppendLine($"[Parallel Results — {compressed.Count} tools]");
            sb.AppendLine();

            for (int i = 0; i < compressed.Count; i++)
            {
                var (toolName, output) = compressed[i];
                sb.AppendLine($"{i + 1}. {toolName}:");
                // Indent each line of the output
                foreach (var line in (output ?? "").Split('\n'))
                {
                    sb.AppendLine($"   {line}");
                }
                sb.AppendLine();
            }

            var merged = sb.ToString().TrimEnd();

            // If merged is still large, generate LLM summary
            if (merged.Length > 2000)
            {
                try
                {
                    var summary = await SummarizeBatchWithLlmAsync(compressed, ct);
                    if (!string.IsNullOrWhiteSpace(summary))
                        return summary;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[ObservationCompressor] Batch LLM summarization failed: {ex.Message}");
                }

                // Fallback: truncate merged output
                if (merged.Length > 3000)
                    merged = merged.Substring(0, 3000) + $"\n... (merged output truncated, total {merged.Length} chars)";
            }

            return merged;
        }

        /// <summary>
        /// Use LLM to summarize a batch of parallel tool results.
        /// </summary>
        private async Task<string> SummarizeBatchWithLlmAsync(
            List<(string toolName, string output)> compressed, CancellationToken ct)
        {
            var prompt = new System.Text.StringBuilder();
            prompt.AppendLine("Summarize the following parallel tool execution results in 3-5 sentences.");
            prompt.AppendLine("Focus on: what was done, key findings, any errors or failures.");
            prompt.AppendLine("Output ONLY the summary text, no JSON, no markdown.");
            prompt.AppendLine();
            foreach (var (toolName, output) in compressed)
            {
                var compact = (output ?? "").Length > 400 ? output.Substring(0, 400) + "..." : output;
                prompt.AppendLine($"- {toolName}: {compact}");
            }
            var promptText = prompt.ToString();

            var isLocal = ChatModelRouting.IsLocalModelSelected(AIConfig.Load(), _localLlmService?.CurrentModel);
            try
            {
                string summary;
                if (isLocal && _localLlmService != null)
                {
                    summary = await _localLlmService.InvokeAsync(promptText, OllamaTaskKind.Completion, ct: ct);
                    if (!string.IsNullOrWhiteSpace(summary))
                        return $"[Local LLM Summary of {compressed.Count} parallel tool results]\n{summary}";
                }
                else
                {
                    summary = await _aiService.GenerateSclAsync(promptText);
                    if (!string.IsNullOrWhiteSpace(summary))
                        return $"[LLM Summary of {compressed.Count} parallel tool results]\n{summary}";
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ObservationCompressor] Batch LLM summarization failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Extract block names (FB_xxx, FC_xxx, DB_xxx, etc.) from output text.
        /// </summary>
        private List<string> ExtractBlockNames(string text)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return names;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in BlockNamePattern.Matches(text))
            {
                var name = m.Value.Trim();
                if (name.Length >= 3 && name.Length <= 100 && seen.Add(name))
                    names.Add(name);
            }

            return names;
        }
    }
}
