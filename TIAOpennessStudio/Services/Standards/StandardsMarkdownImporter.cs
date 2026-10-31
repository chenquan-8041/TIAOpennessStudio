using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TIAOpennessStudio.Services.Standards
{
    /// <summary>
    /// Splits a Markdown document into sections by heading (#/##/###…) and turns each section into a
    /// StandardRule whose Content keeps the section's raw Markdown (paragraphs, lists, tables and
    /// code blocks are all preserved). Intended to give AI retrieval/display the full original text
    /// rather than flattening content into table rows.
    /// - A preamble before the first heading is also kept as one rule.
    /// - A Markdown document with no headings is imported whole as a single rule.
    /// </summary>
    public static class StandardsMarkdownImporter
    {
        public static IReadOnlyList<StandardRule> ParseFile(string markdownPath)
        {
            if (!File.Exists(markdownPath))
                throw new FileNotFoundException("Standard markdown source not found.", markdownPath);

            var docId = Path.GetFileNameWithoutExtension(markdownPath);
            string text;
            try { text = File.ReadAllText(markdownPath, Encoding.UTF8); }
            catch { return ParseFileWhole(markdownPath); } // Fall back to importing the whole file if reading fails.

            try
            {
                var rules = ParseText(text, docId);
                // If sectioning yields nothing (e.g. all blank), fall back to a single whole-file rule to avoid packing an empty base.
                if (rules == null || rules.Count == 0)
                    return WrapWhole(docId, text);
                return rules;
            }
            catch
            {
                // Never crash on malformed headings/structures; pack the whole document as one rule instead.
                return WrapWhole(docId, text);
            }
        }

        /// <summary>
        /// Imports the document whole: no sectioning, the entire document becomes one StandardRule
        /// whose Content is the full file text (nothing dropped). Whatever the user provides is stored.
        /// </summary>
        public static IReadOnlyList<StandardRule> ParseFileWhole(string markdownPath)
        {
            if (!File.Exists(markdownPath))
                throw new FileNotFoundException("Standard markdown source not found.", markdownPath);

            var docId = Path.GetFileNameWithoutExtension(markdownPath);
            var text = File.ReadAllText(markdownPath, Encoding.UTF8);
            return WrapWhole(docId, text);
        }

        /// <summary>Wraps the whole text as a single rule (fallback when sectioning fails or yields nothing). Empty text returns an empty list.</summary>
        private static IReadOnlyList<StandardRule> WrapWhole(string docId, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<StandardRule>();

            var safeId = string.IsNullOrWhiteSpace(docId) ? "document" : docId;
            return new List<StandardRule>
            {
                new StandardRule
                {
                    RuleId = $"{safeId}#001#{Sanitize(safeId)}",
                    Category = safeId,
                    Scope = "全局",
                    Title = safeId,
                    Content = text,   // preserve full text verbatim
                    Examples = null
                }
            };
        }

        public static IReadOnlyList<StandardRule> ParseText(string markdown, string docId)
        {
            var rules = new List<StandardRule>();
            if (string.IsNullOrWhiteSpace(markdown)) return rules;

            var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            // accumulation buffer for the current section
            var buffer = new List<string>();
            string currentTitle = null;   // title of the current section (null means "preamble")
            int currentLevel = 0;         // heading level (count of '#')
            int seq = 0;
            bool inFencedCode = false;     // a '#' inside a code fence is not a heading

            void Flush()
            {
                // After trimming, emit a rule if anything remains (content or a heading).
                var content = string.Join("\n", buffer).Trim();
                bool hasTitle = !string.IsNullOrWhiteSpace(currentTitle);
                if (content.Length == 0 && !hasTitle) { buffer.Clear(); return; }

                seq++;
                var title = hasTitle ? currentTitle : "(前言)";
                rules.Add(new StandardRule
                {
                    RuleId = $"{docId}#{seq:D3}#{Sanitize(title)}",
                    Category = hasTitle ? currentTitle : "前言",
                    Scope = InferScope(title, content),
                    Title = title,
                    // keep raw Markdown: heading line + body
                    Content = hasTitle
                        ? (new string('#', currentLevel) + " " + currentTitle + "\n" + content).Trim()
                        : content,
                    Examples = null
                });
                buffer.Clear();
            }

            foreach (var raw in lines)
            {
                var line = raw;
                var trimmed = line.TrimStart();

                // toggle on ``` fences; content inside is kept verbatim and not parsed for headings
                if (trimmed.StartsWith("```"))
                {
                    inFencedCode = !inFencedCode;
                    buffer.Add(line);
                    continue;
                }

                if (!inFencedCode && IsAtxHeading(trimmed, out int level, out string headingText))
                {
                    // on a new heading, flush the previous section before starting the next
                    Flush();
                    currentTitle = headingText;
                    currentLevel = level;
                    continue;
                }

                buffer.Add(line);
            }
            Flush();

            return rules;
        }

        /// <summary>Detects an ATX heading line ('#' to '######' followed by a space) and returns its level and text.</summary>
        internal static bool IsAtxHeading(string trimmedLine, out int level, out string text)
        {
            level = 0;
            text = null;
            int i = 0;
            while (i < trimmedLine.Length && trimmedLine[i] == '#') i++;
            if (i == 0 || i > 6) return false;
            // a space must follow '#' (excludes "#tag" and the like)
            if (i >= trimmedLine.Length || trimmedLine[i] != ' ') return false;
            level = i;
            text = trimmedLine.Substring(i + 1).Trim();
            return text.Length > 0;
        }

        private static string InferScope(string title, string content)
        {
            var hay = (title + " " + content).ToUpperInvariant();
            if (hay.Contains("地址") || hay.Contains("PROFINET") || hay.Contains("INTERBUS") || hay.Contains("ET200")) return "地址";
            if (hay.Contains("OB")) return "OB";
            if (hay.Contains("FB")) return "FB";
            if (hay.Contains("FC")) return "FC";
            if (hay.Contains("DB")) return "DB";
            if (hay.Contains("UDT")) return "UDT";
            if (hay.Contains("LAD") || hay.Contains("STL") || hay.Contains("FBD") || hay.Contains("指令")) return "指令";
            return "命名";
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s)
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            var result = sb.ToString();
            return result.Length > 40 ? result.Substring(0, 40) : result;
        }
    }
}
