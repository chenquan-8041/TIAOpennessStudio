using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Text-based fact extraction for SCL (StructuredText) networks.
    /// TIA exports SCL as plain text (no FlgNet Wire/Pin graph), so calls and
    /// variable accesses are recovered via lexical scanning after stripping
    /// comments and string literals. Direction is best-effort: assignment LHS is
    /// Write, otherwise Read; ambiguous cases stay Unresolved.
    ///
    /// Output is language-neutral (see <see cref="SclExtractResult"/>); the two
    /// existing extractors map it into their own models (PlcSymbolReference /
    /// PlcCallEdge for dependency, AccessNode / CallSiteNode for network facts).
    /// </summary>
    public static class SclFactExtractor
    {
        private readonly static PlcKnowledgeCanonicalizer _canon = new PlcKnowledgeCanonicalizer();

        // IEC / SCL keywords and built-in functions that look like calls "Name(" but are not block calls.
        // The same table also filters symbol tokens (step 3): these words can never become read_vars/write_vars.
        // P2c: add missing keywords (ELSE/UNTIL/declaration section/type names); previously they leaked into read_vars as noise.
        private static readonly HashSet<string> NonCallIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IF","ELSIF","ELSE","CASE","FOR","WHILE","REPEAT","UNTIL","RETURN","EXIT","CONTINUE","GOTO",
            "AND","OR","XOR","NOT","MOD","DIV",
            "ABS","SQR","SQRT","LN","EXP","SIN","COS","TAN","ASIN","ACOS","ATAN",
            "LIMIT","MIN","MAX","SEL","MUX","ROUND","TRUNC","CEIL","FLOOR",
            "SHL","SHR","ROL","ROR","LEN","LEFT","RIGHT","MID","CONCAT","INSERT","DELETE","REPLACE","FIND",
            "TO","BY","DO","THEN","OF","END_IF","END_CASE","END_FOR","END_WHILE","END_REPEAT",
            // Declaration-section keywords (encountered when scanning a whole SCL text)
            "VAR","VAR_INPUT","VAR_OUTPUT","VAR_IN_OUT","VAR_TEMP","VAR_GLOBAL","VAR_EXTERNAL","VAR_ACCESS","VAR_CONSTANT",
            "END_VAR","BEGIN","CONSTANT",
            "FUNCTION","END_FUNCTION","FUNCTION_BLOCK","END_FUNCTION_BLOCK",
            "ORGANIZATION_BLOCK","END_ORGANIZATION_BLOCK","DATA_BLOCK","END_DATA_BLOCK",
            "TYPE","END_TYPE","STRUCT","END_STRUCT","ARRAY",
            // Boolean literals
            "TRUE","FALSE",
            // IEC elementary type names (typed literals INT#5 / conversions INT_TO_REAL would otherwise leak type words)
            "BOOL","BYTE","WORD","DWORD","LWORD","SINT","INT","DINT","LINT",
            "USINT","UINT","UDINT","ULINT","REAL","LREAL","TIME","LTIME","DATE",
            "TIME_OF_DAY","TOD","LTOD","DATE_AND_TIME","DT","LDT","DTL","STRING","WSTRING","CHAR","WCHAR",
            "TON","TOF","TP","CTU","CTD","CTUD" // IEC timers/counters as inline calls are still calls, but keep conservative
        };

        // Identifier or quoted name possibly followed by '(' -> potential call.
        private static readonly Regex CallRegex = new Regex(
            "(?:\"(?<q>[^\"]+)\"|(?<b>[A-Za-z_][A-Za-z0-9_]*))\\s*\\(",
            RegexOptions.Compiled);

        // Assignment target on the LHS of ':='.
        private static readonly Regex AssignRegex = new Regex(
            "(?<lhs>[#\"A-Za-z_][#\"A-Za-z0-9_.\\[\\]]*)\\s*:=",
            RegexOptions.Compiled);

        // Symbol tokens: #local, "DB".member, Tag, dotted paths.
        private static readonly Regex SymbolRegex = new Regex(
            "#?\"?[A-Za-z_][A-Za-z0-9_]*\"?(?:\\.[\"A-Za-z_][\"A-Za-z0-9_]*)*",
            RegexOptions.Compiled);

        public sealed class SclAccess
        {
            public string CanonicalPath;
            public string AccessKind;   // Read / Write / Unresolved
            public string Scope;        // Local / Global (best-effort)
            public int Position;        // char offset, used for SourceRef uid
        }

        public sealed class SclCall
        {
            public string CalleeName;
            public string InstancePath; // "" for SCL (not resolvable from text)
            public int Position;
        }

        public sealed class SclExtractResult
        {
            public string CleanText;                       // comment/string-stripped text
            public List<SclAccess> Accesses = new List<SclAccess>();
            public List<SclCall> Calls = new List<SclCall>();
        }

        /// <summary>
        /// Extract calls and accesses from an SCL StructuredText body.
        /// </summary>
        public static SclExtractResult Extract(string sourceText, string blockName)
        {
            var result = new SclExtractResult { CleanText = "" };
            if (string.IsNullOrWhiteSpace(sourceText))
                return result;

            var clean = StripCommentsAndStrings(sourceText);
            result.CleanText = clean;

            // 1) Calls
            var calleePositions = new HashSet<int>();
            foreach (Match m in CallRegex.Matches(clean))
            {
                var name = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["b"].Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!m.Groups["q"].Success && NonCallIdentifiers.Contains(name)) continue;

                var callee = _canon.NormalizeWhitespace(name);
                if (string.IsNullOrWhiteSpace(callee)) continue;

                calleePositions.Add(m.Index);
                result.Calls.Add(new SclCall
                {
                    CalleeName = callee,
                    InstancePath = "",
                    Position = m.Index
                });
            }

            // 2) Assignment targets -> Write
            var writeTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in AssignRegex.Matches(clean))
            {
                var lhs = m.Groups["lhs"].Value;
                var canonical = BuildCanonical(blockName, lhs, out var scope);
                if (string.IsNullOrWhiteSpace(canonical)) continue;
                writeTargets.Add(canonical);
                result.Accesses.Add(new SclAccess
                {
                    CanonicalPath = canonical,
                    AccessKind = "Write",
                    Scope = scope,
                    Position = m.Index
                });
            }

            // 3) All symbol tokens -> Read (unless already recorded as Write at same path)
            foreach (Match m in SymbolRegex.Matches(clean))
            {
                var token = m.Value;
                // Skip pure keywords and things immediately followed by '(' (calls).
                var bare = token.Trim('#', '"');
                if (NonCallIdentifiers.Contains(bare)) continue;
                if (IsFollowedByParen(clean, m.Index + m.Length)) continue;

                var canonical = BuildCanonical(blockName, token, out var scope);
                if (string.IsNullOrWhiteSpace(canonical)) continue;
                if (writeTargets.Contains(canonical)) continue; // already a Write access

                result.Accesses.Add(new SclAccess
                {
                    CanonicalPath = canonical,
                    AccessKind = "Read",
                    Scope = scope,
                    Position = m.Index
                });
            }

            return result;
        }

        private static bool IsFollowedByParen(string text, int pos)
        {
            for (int i = pos; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i])) continue;
                return text[i] == '(';
            }
            return false;
        }

        private static string BuildCanonical(string blockName, string rawToken, out string scope)
        {
            scope = "";
            if (string.IsNullOrWhiteSpace(rawToken)) return "";

            var isLocal = rawToken.StartsWith("#");
            var cleaned = rawToken.TrimStart('#');
            var canonical = _canon.CanonicalizeSymbolPath(cleaned);
            if (string.IsNullOrWhiteSpace(canonical) || !_canon.LooksLikeSymbol(canonical))
                return "";

            if (isLocal)
            {
                scope = "Local";
                return _canon.BuildScopedPath(blockName, "Local", canonical);
            }

            scope = "Global";
            return canonical;
        }

        /// <summary>
        /// Replace SCL comments (// line, (* block *)) and string literals ('...')
        /// with spaces of equal length so char offsets remain stable.
        /// </summary>
        public static string StripCommentsAndStrings(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                // line comment //
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
                    continue;
                }
                // block comment (* *)
                if (c == '(' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == ')'))
                    {
                        sb.Append(text[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    if (i + 1 < text.Length) { sb.Append("  "); i += 2; }
                    continue;
                }
                // string literal '...'
                if (c == '\'')
                {
                    sb.Append(' '); i++;
                    while (i < text.Length && text[i] != '\'')
                    {
                        sb.Append(text[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    if (i < text.Length) { sb.Append(' '); i++; }
                    continue;
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
