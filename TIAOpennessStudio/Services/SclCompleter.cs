using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TIAOpennessStudio.Services
{
    public static class SclCompleter
    {
        private static readonly Regex BlockNamePattern =
            new Regex("^[A-Za-z_][A-Za-z0-9_]{0,23}$", RegexOptions.Compiled);

        private const string DefaultBlockName = "AI_Generated_Block";

        public static SclCompletionResult Complete(string code, string blockType, string blockName)
        {
            var completedParts = new List<string>();
            var stillMissing = new List<string>();

            string keyword = NormalizeBlockType(blockType);
            if (keyword == null)
            {
                return new SclCompletionResult
                {
                    IsValid = false,
                    CompletedCode = code ?? string.Empty,
                    CompletedParts = completedParts.AsReadOnly(),
                    StillMissing = stillMissing.AsReadOnly(),
                    Reason = "Unsupported block type: " + (blockType ?? string.Empty),
                };
            }

            string effectiveName = blockName;
            if (string.IsNullOrEmpty(effectiveName) || !BlockNamePattern.IsMatch(effectiveName))
            {
                effectiveName = DefaultBlockName;
                completedParts.Add("sanitized block name");
            }

            string working = NormalizeNewLines(code ?? string.Empty).Trim();

            bool hasHeader = StartsWithBlockKeyword(working, keyword);
            if (!hasHeader)
            {
                string header = BuildHeader(keyword, effectiveName);
                working = working.Length == 0 ? header : header + "\n" + working;
                completedParts.Add("header");
            }

            working = NormalizeExternalSourceSyntax(working, completedParts);

            if (NeedsExecutableSection(keyword) && !ContainsKeyword(working, "BEGIN"))
            {
                working = TrimTrailingEndKeyword(working, keyword);
                working = EnsureOpenVarSectionsClosed(working, completedParts);
                if (working.Length > 0 && !working.EndsWith("\n", StringComparison.Ordinal)) working += "\r\n";
                working += "BEGIN";
                completedParts.Add("begin");
            }
            else
            {
                working = EnsureOpenVarSectionsClosed(working, completedParts);
            }

            string endKeyword = "END_" + keyword;
            if (!ContainsEndKeyword(working, endKeyword))
            {
                if (working.Length > 0 && !working.EndsWith("\n", StringComparison.Ordinal)) working += "\r\n";
                working += endKeyword;
                completedParts.Add("closing keyword");
            }

            bool finalHasHeader = StartsWithBlockKeyword(working, keyword);
            bool finalHasEnd = ContainsEndKeyword(working, endKeyword);
            bool finalNeedsBegin = NeedsExecutableSection(keyword);
            bool finalHasBegin = !finalNeedsBegin || ContainsKeyword(working, "BEGIN");
            bool hasBalancedVarSections = HasBalancedVarSections(working);

            if (!finalHasHeader) stillMissing.Add(keyword + " keyword");
            if (!finalHasBegin) stillMissing.Add("BEGIN");
            if (!finalHasEnd) stillMissing.Add(endKeyword);
            if (!hasBalancedVarSections) stillMissing.Add("END_VAR");

            return new SclCompletionResult
            {
                IsValid = stillMissing.Count == 0,
                CompletedCode = working,
                CompletedParts = completedParts.AsReadOnly(),
                StillMissing = stillMissing.AsReadOnly(),
                Reason = null,
            };
        }

        private static string NormalizeBlockType(string blockType)
        {
            if (string.IsNullOrWhiteSpace(blockType)) return null;
            string trimmed = blockType.Trim();
            if (string.Equals(trimmed, "FB", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "FUNCTION_BLOCK", StringComparison.OrdinalIgnoreCase))
            {
                return "FUNCTION_BLOCK";
            }
            if (string.Equals(trimmed, "FC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "FUNCTION", StringComparison.OrdinalIgnoreCase))
            {
                return "FUNCTION";
            }
            if (string.Equals(trimmed, "DB", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "DATA_BLOCK", StringComparison.OrdinalIgnoreCase))
            {
                return "DATA_BLOCK";
            }
            if (string.Equals(trimmed, "OB", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "ORGANIZATION_BLOCK", StringComparison.OrdinalIgnoreCase))
            {
                return "ORGANIZATION_BLOCK";
            }
            return null;
        }

        private static string NormalizeNewLines(string text)
        {
            // TIA Portal's parsing of external source files on Windows relies heavily on
            // CRLF (\r\n) line endings. With LF (\n) only, its line-number math and
            // end-of-block parsing often break with "unexpected end of file" or a
            // mnemonic check error.
            string normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            return normalized.Replace("\n", "\r\n");
        }

        private static string NormalizeExternalSourceSyntax(string code, IList<string> completedParts)
        {
            string working = code ?? string.Empty;

            string before = working;
            working = Regex.Replace(
                working,
                @"(?im)^(\s*)(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK)\s+([A-Za-z_][A-Za-z0-9_]*)\b(.*)$",
                m => $"{m.Groups[1].Value}{m.Groups[2].Value} \"{m.Groups[3].Value}\"{m.Groups[4].Value}");
            if (!string.Equals(before, working, StringComparison.Ordinal)) completedParts.Add("quoted block name");

            // 1. Remove illegal semicolons after block/var ends
            before = working;
            working = Regex.Replace(working, @"\b(END_(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK|VAR|REGION))\s*;", "$1", RegexOptions.IgnoreCase);
            if (!string.Equals(before, working, StringComparison.Ordinal)) completedParts.Add("removed illegal semicolons");

            // 2. Ensure block attributes (Optimized Access & VERSION) exist if a block header is present
            var headerMatch = Regex.Match(working, @"(?im)^(\s*(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK)[^\r\n]*)(?:\r?\n)");
            if (headerMatch.Success)
            {
                string attributesToInsert = "";
                if (!Regex.IsMatch(working, @"(?i)\{\s*S7_Optimized_Access"))
                {
                    // For S7-300/400 compatibility, optimized block access must be FALSE.
                    attributesToInsert += "\r\n{ S7_Optimized_Access := 'FALSE' }";
                }
                if (!Regex.IsMatch(working, @"(?im)^\s*VERSION\s*:"))
                {
                    attributesToInsert += "\r\nVERSION : 0.1";
                }
                if (attributesToInsert.Length > 0)
                {
                    // Must be inserted at the end of the header line (preserving the newline).
                    working = working.Insert(headerMatch.Index + headerMatch.Groups[1].Length, attributesToInsert);
                    completedParts.Add("block attributes");
                }
            }

            before = working;
            working = Regex.Replace(working, @"(?im)^\s*VAR_CONSTANT\b", "VAR CONSTANT");
            if (!string.Equals(before, working, StringComparison.Ordinal)) completedParts.Add("VAR CONSTANT syntax");

            before = working;
            working = Regex.Replace(working, @"\bTON_TIME\b", "TON", RegexOptions.IgnoreCase);
            working = Regex.Replace(working, @"\bTOF_TIME\b", "TOF", RegexOptions.IgnoreCase);
            working = Regex.Replace(working, @"\bTP_TIME\b", "TP", RegexOptions.IgnoreCase);
            if (!string.Equals(before, working, StringComparison.Ordinal)) completedParts.Add("IEC timer type syntax");

            return working;
        }

        private static bool NeedsExecutableSection(string keyword)
        {
            return string.Equals(keyword, "FUNCTION_BLOCK", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(keyword, "FUNCTION", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(keyword, "ORGANIZATION_BLOCK", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(keyword, "DATA_BLOCK", StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureOpenVarSectionsClosed(string code, IList<string> completedParts)
        {
            if (HasBalancedVarSections(code)) return code;

            int beginIndex = IndexOfWholeKeyword(code, "BEGIN");
            if (beginIndex >= 0)
            {
                string before = code.Substring(0, beginIndex).TrimEnd();
                string after = code.Substring(beginIndex);
                completedParts.Add("END_VAR");
                return before + "\r\nEND_VAR\r\n" + after;
            }

            completedParts.Add("END_VAR");
            return code.TrimEnd() + "\r\nEND_VAR";
        }

        private static bool HasBalancedVarSections(string code)
        {
            int openCount = Regex.Matches(code ?? string.Empty, @"\bVAR(?:_INPUT|_OUTPUT|_IN_OUT|_TEMP|_STATIC)?\b", RegexOptions.IgnoreCase).Count;
            int closeCount = Regex.Matches(code ?? string.Empty, @"\bEND_VAR\b", RegexOptions.IgnoreCase).Count;
            return closeCount >= openCount;
        }

        private static string TrimTrailingEndKeyword(string code, string keyword)
        {
            string endKeyword = "END_" + keyword;
            var pattern = @"\s*\b" + Regex.Escape(endKeyword) + @"\b\s*$";
            return Regex.Replace(code ?? string.Empty, pattern, string.Empty, RegexOptions.IgnoreCase).TrimEnd();
        }

        private static bool StartsWithBlockKeyword(string code, string keyword)
        {
            if (string.IsNullOrEmpty(code)) return false;

            using (var reader = new System.IO.StringReader(code))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.TrimStart();
                    if (trimmed.Length == 0) continue;
                    if (trimmed.Length < keyword.Length) return false;
                    if (!trimmed.Substring(0, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase)) return false;
                    if (trimmed.Length == keyword.Length) return true;
                    char next = trimmed[keyword.Length];
                    return !(char.IsLetterOrDigit(next) || next == '_');
                }
            }

            return false;
        }

        private static bool ContainsEndKeyword(string code, string endKeyword)
        {
            if (string.IsNullOrEmpty(code)) return false;
            var pattern = "\\b" + Regex.Escape(endKeyword) + "\\b";
            return Regex.IsMatch(code, pattern, RegexOptions.IgnoreCase);
        }

        private static bool ContainsKeyword(string code, string keyword)
        {
            if (string.IsNullOrEmpty(code)) return false;
            int idx = IndexOfWholeKeyword(code, keyword);
            return idx >= 0;
        }

        private static int IndexOfWholeKeyword(string code, string keyword)
        {
            if (string.IsNullOrEmpty(code)) return -1;
            var match = Regex.Match(code, "\\b" + Regex.Escape(keyword) + "\\b", RegexOptions.IgnoreCase);
            return match.Success ? match.Index : -1;
        }

        private static string BuildHeader(string keyword, string blockName)
        {
            var sb = new StringBuilder();
            sb.Append(keyword);
            sb.Append(" \"");
            sb.Append(blockName);
            sb.Append("\"");
            if (string.Equals(keyword, "FUNCTION", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" : Void");
            }
            sb.Append("\r\n");
            // For S7-300/400 compatibility, optimized block access must be FALSE.
            sb.Append("{ S7_Optimized_Access := 'FALSE' }\r\n");
            sb.Append("VERSION : 0.1");
            return sb.ToString();
        }
    }

    public sealed class SclCompletionResult
    {
        public bool IsValid { get; set; }

        public string CompletedCode { get; set; }

        public IReadOnlyList<string> CompletedParts { get; set; }

        public IReadOnlyList<string> StillMissing { get; set; }

        public string Reason { get; set; }
    }
}
