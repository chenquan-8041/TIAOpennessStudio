using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Semantic-aware compressor — a single entry point that routes to different
    /// strategies by content type.
    ///
    /// Shared by both pipelines:
    ///   - Cloud LLM path: MaxTokens=1500, AllowLlmSummarize=true
    ///   - 7B local path:  MaxTokens=400,  AllowLlmSummarize=false
    ///
    /// Core principles:
    ///   1. Code content (SCL/XML) is compressed only by rules, preserving semantic structure.
    ///   2. Compile / search / reference content is compressed by rule-based key extraction.
    ///   3. LLM summarization is used only for conversation-history compression.
    ///   4. Declarations and key code lines are always kept; never truncate mechanically.
    /// </summary>
    public class SemanticCompressor : ISemanticCompressor
    {
        // ── SCL structure keywords ────────────────────────────────────
        private static readonly string[] SclDeclarationKeywords =
        {
            "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_STATIC", "VAR_TEMP",
            "VAR_GLOBAL", "VAR", "END_VAR", "TYPE", "END_TYPE", "STRUCT", "END_STRUCT",
            "FUNCTION_BLOCK", "FUNCTION", "ORGANIZATION_BLOCK", "DATA_BLOCK",
            "BEGIN", "END_FUNCTION_BLOCK", "END_FUNCTION", "END_ORGANIZATION_BLOCK",
            "END_DATA_BLOCK", "CONSTANT"
        };

        private static readonly string[] SclBodyStartKeywords =
        {
            "BEGIN", "NETWORK", "TITLE"
        };

        // ── Estimation constants ──────────────────────────────────────
        private const int CharsPerToken = 4;

        /// <inheritdoc />
        public ContentType InferContentType(string toolName, string output)
        {
            if (string.IsNullOrEmpty(output))
                return ContentType.Unknown;

            return toolName switch
            {
                "export_block" => IsXmlFormat(output) ? ContentType.XmlBlock : ContentType.SclCode,
                "compile" => ContentType.CompileOutput,
                "search_project" or "get_project_tree" or "get_index_status"
                    => ContentType.SearchResults,
                "query_variable_references" or "query_dependency"
                    => ContentType.VariableReferences,
                "plc_build_and_import" or "import_block_code" => ContentType.Unknown,
                _ => ContentType.Unknown
            };
        }

        private static bool IsXmlFormat(string output)
        {
            var trimmed = output.TrimStart();
            return trimmed.StartsWith("<?xml") || trimmed.StartsWith("<SW.") || trimmed.StartsWith("<Document");
        }

        /// <inheritdoc />
        public async Task<string> CompressAsync(CompressRequest request, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.Content))
                return request.Content;

            // Infer the content type automatically
            if (request.Type == ContentType.Unknown)
                request.Type = InferContentType(request.ToolName, request.Content);

            var maxChars = request.Config.MaxTokens * CharsPerToken;

            // Content already fits the budget; no compression needed
            if (request.Content.Length <= maxChars)
                return request.Content;

            return request.Type switch
            {
                ContentType.SclCode => CompressSclCode(request.Content, maxChars, request.Config),
                ContentType.XmlBlock => CompressXmlBlock(request.Content, maxChars),
                ContentType.CompileOutput => CompressCompileOutput(request.Content),
                ContentType.SearchResults => CompressSearchResults(request.Content, maxChars),
                ContentType.VariableReferences => CompressVariableReferences(request.Content, maxChars),
                ContentType.ProjectContext => CompressProjectContext(request.Content, maxChars, request.Config),
                ContentType.ChatHistory => await CompressChatHistoryAsync(request.Content, maxChars, request.Config, ct),
                _ => TruncateWithMetadata(request.Content, maxChars)
            };
        }

        //  SCL code compression

        private string CompressSclCode(string scl, int maxChars, CompressConfig config)
        {
            var lines = scl.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var result = new StringBuilder();
            result.AppendLine("/* 压缩前: " + lines.Length + " 行 */");

            // 1. Parse the SCL regions
            var regions = ParseSclRegions(lines);

            // 2. Declarations: keep in full
            result.AppendLine("── 声明区 ──");
            foreach (var line in regions.Declarations)
                result.AppendLine(line);

            // 3. Body: keep key lines according to the mode
            int bodyHeadLines, bodyTailLines;
            switch (config.Mode)
            {
                case CompressMode.Analysis:
                    bodyHeadLines = 3; bodyTailLines = 3; break;
                case CompressMode.Generation:
                    bodyHeadLines = 5; bodyTailLines = 5; break;
                case CompressMode.Modification:
                    bodyHeadLines = 10; bodyTailLines = 10; break;
                default:
                    bodyHeadLines = 3; bodyTailLines = 3; break;
            }

            result.AppendLine("── 代码体 ──");
            CompressSclBody(result, regions.BodyLines, bodyHeadLines, bodyTailLines, config.UserQuery);

            // 4. Footer
            if (regions.FooterLines.Count > 0)
            {
                result.AppendLine("── 尾部 ──");
                foreach (var line in regions.FooterLines)
                    result.AppendLine(line);
            }

            var compressed = result.ToString();
            if (compressed.Length > maxChars)
            {
                // Still too long; shrink the body retention further
                var sb = new StringBuilder();
                AppendRegion(sb, "── 声明区 ──", regions.Declarations);
                sb.AppendLine("── 代码体 ──");
                CompressSclBody(sb, regions.BodyLines, 2, 2, config.UserQuery);
                AppendRegion(sb, "── 尾部 ──", regions.FooterLines);
                compressed = sb.ToString();
            }

            return compressed;
        }

        private static void CompressSclBody(StringBuilder sb, List<string> bodyLines,
            int headLines, int tailLines, string userQuery)
        {
            var kept = new HashSet<int>();
            var keywords = ExtractKeywords(userQuery);

            for (int i = 0; i < bodyLines.Count; i++)
            {
                var line = bodyLines[i].Trim();

                // Keyword match
                if (keywords.Count > 0 && keywords.Any(k =>
                    line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    kept.Add(i);
                    continue;
                }

                // Keep assignment statements (key to understanding the logic)
                if (line.Contains(":="))
                {
                    kept.Add(i);
                    continue;
                }

                // Keep comments
                if (line.StartsWith("//"))
                {
                    kept.Add(i);
                    continue;
                }
            }

            // Always keep the head and tail
            for (int i = 0; i < Math.Min(headLines, bodyLines.Count); i++)
                kept.Add(i);
            for (int i = Math.Max(0, bodyLines.Count - tailLines); i < bodyLines.Count; i++)
                kept.Add(i);

            // Output, marking omissions
            int lastKept = -1;
            foreach (int i in kept.OrderBy(x => x))
            {
                if (lastKept >= 0 && i - lastKept > 1)
                    sb.AppendLine($"  ... ({i - lastKept - 1} 行省略) ...");
                sb.AppendLine(bodyLines[i]);
                lastKept = i;
            }

            if (lastKept < bodyLines.Count - 1)
                sb.AppendLine($"  ... ({bodyLines.Count - lastKept - 1} 行省略) ...");
        }

        //  SCL region parsing

        private static SclRegions ParseSclRegions(string[] lines)
        {
            var regions = new SclRegions();
            int state = 0; // 0=header, 1=declaration, 2=body, 3=footer

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();

                if (state == 0)
                {
                    regions.HeaderLines.Add(lines[i]);
                    if (Array.Exists(SclDeclarationKeywords, k =>
                        trimmed.StartsWith(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        state = 1;
                        regions.Declarations.Add(lines[i]);
                    }
                }
                else if (state == 1)
                {
                    regions.Declarations.Add(lines[i]);
                    if (Array.Exists(SclBodyStartKeywords, k =>
                        trimmed.StartsWith(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        state = 2;
                        regions.BodyLines.Add(lines[i]);
                    }
                }
                else if (state == 2)
                {
                    if (trimmed.StartsWith("END_FUNCTION_BLOCK", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("END_FUNCTION", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("END_ORGANIZATION_BLOCK", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("END_DATA_BLOCK", StringComparison.OrdinalIgnoreCase))
                    {
                        state = 3;
                        regions.FooterLines.Add(lines[i]);
                    }
                    else
                    {
                        regions.BodyLines.Add(lines[i]);
                    }
                }
                else
                {
                    regions.FooterLines.Add(lines[i]);
                }
            }

            return regions;
        }

        private class SclRegions
        {
            public List<string> HeaderLines { get; } = new List<string>();
            public List<string> Declarations { get; } = new List<string>();
            public List<string> BodyLines { get; } = new List<string>();
            public List<string> FooterLines { get; } = new List<string>();
        }

        //  XML block structure compression

        private string CompressXmlBlock(string xml, int maxChars)
        {
            var lines = xml.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder();
            sb.AppendLine("/* XML 块, 原始 " + lines.Length + " 行 */");

            var kept = new HashSet<int>();

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();

                // Keep block definition nodes
                if (trimmed.Contains("<SW.Blocks.FC") ||
                    trimmed.Contains("<SW.Blocks.FB") ||
                    trimmed.Contains("<SW.Blocks.OB") ||
                    trimmed.Contains("<SW.Blocks.DB") ||
                    trimmed.Contains("<SW.Blocks.UDT") ||
                    trimmed.Contains("Name=") ||
                    trimmed.Contains("UId=") ||
                    trimmed.Contains("Number="))
                {
                    kept.Add(i);
                    continue;
                }

                // Keep network topology
                if (trimmed.Contains("<SW.Blocks.FlgNet") ||
                    trimmed.Contains("<FlgNet") ||
                    trimmed.Contains("<Parts") ||
                    trimmed.Contains("<Access") ||
                    trimmed.Contains("Scope") ||
                    trimmed.Contains("UId"))
                {
                    kept.Add(i);
                    continue;
                }

                // Keep call relationships
                if (trimmed.Contains("CallInfo") ||
                    trimmed.Contains("BlockRef") ||
                    trimmed.Contains("<Call") ||
                    trimmed.Contains("<Instance"))
                {
                    kept.Add(i);
                }
            }

            // Always keep the head and tail
            for (int i = 0; i < Math.Min(3, lines.Length); i++) kept.Add(i);
            for (int i = Math.Max(0, lines.Length - 3); i < lines.Length; i++) kept.Add(i);

            int lastKept = -1;
            foreach (int i in kept.OrderBy(x => x))
            {
                if (lastKept >= 0 && i - lastKept > 1)
                    sb.AppendLine($"  ... ({i - lastKept - 1} 行 XML 省略) ...");
                sb.AppendLine(lines[i]);
                lastKept = i;
            }

            if (lastKept < lines.Length - 1)
                sb.AppendLine($"  ... ({lines.Length - lastKept - 1} 行 XML 省略) ...");

            var result = sb.ToString();
            return result.Length > maxChars ? TruncateWithMetadata(xml, maxChars) : result;
        }

        //  Compile-output compression

        private string CompressCompileOutput(string output)
        {
            var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var errors = new List<string>();
            var successCount = 0;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    trimmed.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    trimmed.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    trimmed.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    errors.Add(line);
                }
                else if (trimmed.IndexOf("成功", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         trimmed.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         trimmed.IndexOf("compiled", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    successCount++;
                }
            }

            if (errors.Count == 0)
                return $"[编译成功: {successCount} 个块编译通过]";

            var sb = new StringBuilder();
            sb.AppendLine($"[编译失败: {errors.Count} 个错误/警告]");
            foreach (var err in errors)
                sb.AppendLine(err);
            return sb.ToString();
        }

        //  Search-result compression

        private string CompressSearchResults(string output, int maxChars)
        {
            var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder();
            sb.AppendLine("/* 搜索结果 */");

            var blockNames = new List<string>();
            var callRelations = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Block-name lines
                if (Regex.IsMatch(trimmed, @"\b(FC|FB|OB|DB|UDT)[_\d]"))
                    blockNames.Add(trimmed);

                // Call relationships
                if (trimmed.Contains("→") || trimmed.Contains("->") ||
                    trimmed.Contains("calls") || trimmed.Contains("调用"))
                    callRelations.Add(trimmed);
            }

            if (blockNames.Count > 0)
            {
                sb.Append("块: ");
                sb.AppendLine(string.Join(", ", blockNames.Take(20)));
                if (blockNames.Count > 20)
                    sb.AppendLine($"  ... 及其他 {blockNames.Count - 20} 个块");
            }

            if (callRelations.Count > 0)
            {
                sb.AppendLine("调用关系:");
                foreach (var rel in callRelations.Take(5))
                    sb.AppendLine($"  {rel}");
                if (callRelations.Count > 5)
                    sb.AppendLine($"  ... 及其他 {callRelations.Count - 5} 条关系");
            }

            if (blockNames.Count == 0 && callRelations.Count == 0)
                return TruncateWithMetadata(output, maxChars);

            return sb.ToString();
        }

        //  Variable-reference compression

        private string CompressVariableReferences(string output, int maxChars)
        {
            var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder();
            sb.AppendLine("/* 变量引用关系 */");

            var refLines = new List<string>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                // Keep reference-relationship lines (contain a block or variable name)
                if (trimmed.Contains("→") || trimmed.Contains("引用") ||
                    trimmed.Contains("Reference") || trimmed.Contains("refs"))
                {
                    refLines.Add(trimmed);
                }
            }

            if (refLines.Count == 0)
                return TruncateWithMetadata(output, maxChars);

            // Deduplicate
            var unique = refLines.Distinct().ToList();
            foreach (var r in unique.Take(10))
                sb.AppendLine($"  {r}");
            if (unique.Count > 10)
                sb.AppendLine($"  ... 及其他 {unique.Count - 10} 条引用");

            return sb.ToString();
        }

        //  Project-context compression

        private string CompressProjectContext(string output, int maxChars, CompressConfig config)
        {
            var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder();

            var keywords = ExtractKeywords(config.UserQuery);
            var relevant = new List<string>();
            var other = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (keywords.Count > 0 && keywords.Any(k =>
                    trimmed.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    relevant.Add(trimmed);
                }
                else
                {
                    other.Add(trimmed);
                }
            }

            // Keep the relevant ones first
            foreach (var r in relevant)
            {
                sb.AppendLine(r);
                if (sb.Length > maxChars * 0.7) break;
            }

            // Fill the remaining space with a digest of the unrelated content
            if (sb.Length < maxChars && other.Count > 0)
            {
                sb.AppendLine($"/* 其他 {other.Count} 条信息 */");
                foreach (var o in other.Take(3))
                    sb.AppendLine(o);
            }

            return sb.Length > maxChars ? TruncateWithMetadata(output, maxChars) : sb.ToString();
        }

        //  Chat-history compression (the only case that permits LLM summarization)

        private Task<string> CompressChatHistoryAsync(string output, int maxChars,
            CompressConfig config, CancellationToken ct)
        {
            // Chat-history compression: no LLM summarization on the C# side — the caller
            // (AgentOrchestrator.CompressHistoryAsync) handles that. Here we only do
            // basic rule-based truncation, keeping the most recent messages.
            if (output.Length <= maxChars)
                return Task.FromResult(output);

            var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            // Keep the last maxChars characters
            var kept = new StringBuilder();
            int total = 0;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (total + lines[i].Length > maxChars)
                    break;
                kept.Insert(0, lines[i] + "\n");
                total += lines[i].Length + 1;
            }

            return Task.FromResult(kept.ToString().TrimEnd());
        }

        //  Fallback: hard truncation

        private static string TruncateWithMetadata(string output, int maxChars)
        {
            if (output.Length <= maxChars)
                return output;

            var headLen = Math.Min(maxChars * 3 / 5, output.Length);
            var tailLen = Math.Min(maxChars * 2 / 5, output.Length - headLen);

            var head = output.Substring(0, headLen);
            var tail = output.Substring(output.Length - tailLen);

            // Break at a newline
            var lastNewline = head.LastIndexOf('\n');
            if (lastNewline > headLen / 2)
                head = head.Substring(0, lastNewline);

            var firstNewline = tail.IndexOf('\n');
            if (firstNewline > 0 && firstNewline < tailLen / 2)
                tail = tail.Substring(firstNewline);

            return $"{head}\n... ({output.Length - head.Length - tail.Length} 字符省略) ...\n{tail}";
        }

        //  Utilities

        private static void AppendRegion(StringBuilder sb, string header, List<string> lines)
        {
            sb.AppendLine(header);
            foreach (var line in lines)
                sb.AppendLine(line);
        }

        private static HashSet<string> ExtractKeywords(string query)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query))
                return keywords;

            // Extract English words and Chinese terms
            var wordMatches = Regex.Matches(query, @"[a-zA-Z_][a-zA-Z0-9_]{2,}|[\u4e00-\u9fff]{1,4}");
            foreach (Match m in wordMatches)
            {
                var word = m.Value;
                // Drop overly short English words
                if (word.Length >= 3 || word.Any(c => c >= 0x4e00))
                    keywords.Add(word);
            }

            // Filter out common stop words
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "and", "for", "are", "that", "this", "with", "from", "have",
                "what", "when", "where", "which", "about", "TIA", "SCL", "PLC",
                "分析", "代码", "这个", "那个", "一下", "帮忙", "给我"
            };
            keywords.ExceptWith(stopWords);

            return keywords;
        }
    }
}