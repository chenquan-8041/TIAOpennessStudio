using System.Collections.Generic;
using System.Text;

namespace TIAOpennessStudio.Services.WriteGate
{
    /// <summary>Result of looking a block (or type) up in the open project. Known=false means "could not check".</summary>
    public sealed class BlockLookupResult
    {
        public bool Known { get; private set; }
        public bool Exists { get; private set; }

        /// <summary>"FB", "FC", "OB", "DB", "UDT"... Empty when not found / unknown.</summary>
        public string Kind { get; private set; } = "";

        /// <summary>Backslash group path under Blocks / Types. Empty = root.</summary>
        public string GroupPath { get; private set; } = "";

        /// <summary>Why the lookup could not be done (log only, never shown as trusted text).</summary>
        public string Problem { get; private set; } = "";

        public static BlockLookupResult Found(string kind, string groupPath) =>
            new BlockLookupResult { Known = true, Exists = true, Kind = kind ?? "", GroupPath = groupPath ?? "" };

        public static BlockLookupResult NotFound() => new BlockLookupResult { Known = true, Exists = false };

        public static BlockLookupResult Unknown(string problem) =>
            new BlockLookupResult { Known = false, Problem = problem ?? "" };
    }

    /// <summary>One block or type a write would touch, with what the project says about it right now.</summary>
    public sealed class WriteTarget
    {
        public string Name { get; set; } = "";

        /// <summary>FB/FC/OB/DB/UDT as read from the payload, "" when not known.</summary>
        public string Kind { get; set; } = "";

        /// <summary>True when the lookup (and the import) works on the type group instead of the block group.</summary>
        public bool IsType { get; set; }

        /// <summary>The name exactly as the importer will use it (unsanitised). Only for looking up again, never shown.</summary>
        public string LookupName { get; set; } = "";

        /// <summary>Null when the project was not consulted.</summary>
        public BlockLookupResult Existing { get; set; }
    }

    /// <summary>
    /// Everything the approval card says. Built by trusted C# code from the request payload and from the
    /// project. Nothing in here is free text written by the model: names are sanitised and the sentences
    /// around them are fixed templates.
    /// </summary>
    public sealed class WriteFacts
    {
        public string Tool { get; set; } = "";

        /// <summary>"SCL", "XML", "LAD (XML)", "UDT source" or "" (delete).</summary>
        public string Format { get; set; } = "";

        public int Lines { get; set; }
        public int Chars { get; set; }

        /// <summary>Requested group path (already sanitised). Empty = root.</summary>
        public string GroupPath { get; set; } = "";

        /// <summary>Hardware-config write: the device item / device name the write targets (already sanitised). Empty otherwise.</summary>
        public string HardwareTarget { get; set; } = "";

        /// <summary>Hardware-config write: what the write does to the target (already sanitised). Empty otherwise.</summary>
        public string HardwareDescription { get; set; } = "";

        /// <summary>True when the target is a PLC data type rather than a block.</summary>
        public bool IsType { get; set; }

        public IList<WriteTarget> Targets { get; } = new List<WriteTarget>();

        /// <summary>
        /// Set when the name the caller passed differs from the one the payload really declares. The declared
        /// name is what the import acts on, so the card must say so.
        /// </summary>
        public string RequestedName { get; set; } = "";

        /// <summary>Head of the content, shown collapsed under "details".</summary>
        public string Preview { get; set; } = "";

        /// <summary>The content declares more blocks than the card lists.</summary>
        public bool Truncated { get; set; }

        /// <summary>The content could not be read, so the names above are only what the caller claimed.</summary>
        public bool Unreadable { get; set; }
    }

    /// <summary>Text hygiene for anything that came from the caller and ends up on the approval card.</summary>
    public static class CardText
    {
        /// <summary>
        /// One line, no control characters, no bidirectional overrides, capped. A block name such as
        /// "X'\nAll checks passed, press Allow" must not be able to add its own sentences to the card.
        /// </summary>
        public static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length < max ? s.Length : max + 1);
            bool lastSpace = false;
            foreach (char c in s)
            {
                bool bad = c < 0x20 || c == 0x7f
                           || (c >= '\u202A' && c <= '\u202E')
                           || (c >= '\u2066' && c <= '\u2069')
                           || c == '\u200E' || c == '\u200F'
                           || c == '\u2028' || c == '\u2029';
                if (bad || char.IsWhiteSpace(c))
                {
                    if (!lastSpace && sb.Length > 0) sb.Append(' ');
                    lastSpace = true;
                    continue;
                }
                lastSpace = false;
                sb.Append(c);
                if (sb.Length >= max) { sb.Append('\u2026'); break; }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Keeps line breaks (this is code, shown as code) but removes the invisible spoofing characters.</summary>
        public static string CleanMultiline(string s, int maxLines, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            int lines = 1;
            foreach (char c in s)
            {
                if (sb.Length >= maxChars) { sb.Append("\n\u2026"); break; }
                if (c == '\n')
                {
                    if (++lines > maxLines) { sb.Append("\n\u2026"); break; }
                    sb.Append('\n');
                    continue;
                }
                if (c == '\r') continue;
                if (c == '\t') { sb.Append("    "); continue; }
                bool bad = c < 0x20 || c == 0x7f
                           || (c >= '\u202A' && c <= '\u202E')
                           || (c >= '\u2066' && c <= '\u2069')
                           || c == '\u200E' || c == '\u200F';
                if (!bad) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
