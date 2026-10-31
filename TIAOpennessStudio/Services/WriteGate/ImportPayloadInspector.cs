using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.WriteGate
{
    /// <summary>
    /// Reads an import payload the same way <c>OpennessService.ImportBlockCodeCoreAsync</c> will act on it, so
    /// the approval card names the block that is really going to be touched and not just the one the model
    /// said. Pure string work: no TIA, no COM, safe on any thread.
    ///
    /// Mirrors the branch rules of the importer:
    ///   * code starting with "&lt;?xml" or "&lt;Document" is an XML import (the block name is inside the XML);
    ///   * blockType "UDT" imports into the type group;
    ///   * everything else is SCL source, and the generated blocks are named by their declarations
    ///     (FUNCTION_BLOCK "X" ...), not by the file / argument name.
    /// The SCL declaration pattern is a copy of OpennessService.ExtractDeclaredBlockName's, extended to find
    /// every declaration instead of only the first. Keep them in step.
    /// </summary>
    public static class ImportPayloadInspector
    {
        // Deliberately NOT anchored to the start of a line, unlike OpennessService.ExtractDeclaredBlockName.
        // The importer only uses the first declaration to find the block it moves, but the SCL compiler turns
        // every declaration in the source into a block (and replaces one of the same name). A declaration
        // that starts mid-line, for example after END_FUNCTION_BLOCK, must still show up on the card.
        // \b keeps END_FUNCTION_BLOCK / END_DATA_BLOCK from matching. Matching inside a comment only
        // over-reports, which is the safe direction for an approval card.
        private static readonly Regex SclDeclaration = new Regex(
            "\\b(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK)\\s+(?:\"([^\"]+)\"|([A-Za-z_][A-Za-z0-9_]*))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));

        // A user data type declared in SCL source. Line-anchored on purpose: the word TYPE appears in prose
        // and in "VAR x : ...", and an identifier after it would then be reported as a type.
        private static readonly Regex SclTypeDeclaration = new Regex(
            "^[ \\t]*TYPE[ \\t]+(?:\"([^\"]+)\"|([A-Za-z_][A-Za-z0-9_]*))",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));

        private const int MaxDeclared = 50;

        public sealed class Result
        {
            public string Format = "";
            public bool IsType;
            public int Lines;
            public int Chars;

            /// <summary>Declared (name, kind) pairs in source order. Never empty: falls back to the requested name.</summary>
            public System.Collections.Generic.List<Tuple<string, string>> Declared =
                new System.Collections.Generic.List<Tuple<string, string>>();

            /// <summary>True when the payload names its own block(s), so the requested name may be beside the point.</summary>
            public bool NameFromPayload;

            /// <summary>True when the payload declares more than <c>MaxDeclared</c> blocks and the list was cut.</summary>
            public bool Truncated;

            /// <summary>True when reading the payload failed, so the names on the card are only what the caller claimed.</summary>
            public bool Unreadable;
        }

        public static Result Inspect(string requestedName, string blockType, string code)
        {
            var r = new Result();
            code = code ?? "";
            r.Chars = code.Length;
            r.Lines = CountLines(code);

            string head = code.TrimStart();
            bool isXml = head.StartsWith("<?xml", StringComparison.Ordinal) || head.StartsWith("<Document", StringComparison.Ordinal);
            r.IsType = string.Equals(blockType, "UDT", StringComparison.OrdinalIgnoreCase);

            if (isXml)
            {
                r.Format = "XML";
                var named = ReadXmlTarget(code);
                if (named != null)
                {
                    r.Declared.Add(named);
                    r.NameFromPayload = true;
                }
                else
                {
                    // Not parseable, or no block / type element in it. The importer will do whatever it does with
                    // it; the card must not present the caller's claimed name as something we verified.
                    r.Unreadable = true;
                }
            }
            else if (r.IsType)
            {
                r.Format = "UDT source";
            }
            else
            {
                r.Format = "SCL";
                try
                {
                    // Collect with positions so blocks and types come out in source order.
                    var found = new System.Collections.Generic.List<Tuple<int, string, string>>();
                    foreach (Match m in SclDeclaration.Matches(code))
                    {
                        string name = (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
                        if (name.Length > 0) found.Add(Tuple.Create(m.Index, name, KindFromKeyword(m.Groups[1].Value)));
                    }
                    foreach (Match m in SclTypeDeclaration.Matches(code))
                    {
                        string name = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                        if (name.Length > 0) found.Add(Tuple.Create(m.Index, name, "UDT"));
                    }
                    found.Sort((a, b) => a.Item1.CompareTo(b.Item1));

                    foreach (var f in found)
                    {
                        if (r.Declared.Count >= MaxDeclared) { r.Truncated = true; break; }
                        r.Declared.Add(Tuple.Create(f.Item2, f.Item3));
                        r.NameFromPayload = true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // Pathological input. Do not guess: report that the payload could not be read, and let the
                    // card say the content was not checked rather than present the caller's name as the truth.
                    r.Declared.Clear();
                    r.NameFromPayload = false;
                    r.Unreadable = true;
                }
            }

            if (r.Declared.Count == 0)
            {
                string fallback = string.IsNullOrWhiteSpace(requestedName) ? "AI_Block" : requestedName.Trim();
                r.Declared.Add(Tuple.Create(fallback, r.IsType ? "UDT" : (blockType ?? "").Trim().ToUpperInvariant()));
            }
            return r;
        }

        private static int CountLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 1;
            foreach (char c in s) if (c == '\n') n++;
            if (s[s.Length - 1] == '\n') n--;
            return n;
        }

        private static string KindFromKeyword(string kw)
        {
            switch ((kw ?? "").ToUpperInvariant())
            {
                case "FUNCTION_BLOCK": return "FB";
                case "FUNCTION": return "FC";
                case "DATA_BLOCK": return "DB";
                case "ORGANIZATION_BLOCK": return "OB";
                default: return "";
            }
        }

        /// <summary>Name and kind of the single block / type an Openness XML file describes, or null when unreadable.</summary>
        private static Tuple<string, string> ReadXmlTarget(string xml)
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,   // no entity expansion from a model-supplied document
                    XmlResolver = null,
                    MaxCharactersInDocument = 64L * 1024 * 1024
                };
                using (var sr = new StringReader(xml))
                using (var xr = XmlReader.Create(sr, settings))
                {
                    var doc = XDocument.Load(xr);
                    var el = doc.Descendants().FirstOrDefault(e =>
                        e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal) ||
                        e.Name.LocalName.StartsWith("SW.Types.", StringComparison.Ordinal));
                    if (el == null) return null;

                    var attrs = el.Elements().FirstOrDefault(c => c.Name.LocalName == "AttributeList");
                    var nameEl = attrs?.Elements().FirstOrDefault(c => c.Name.LocalName == "Name");
                    string name = nameEl?.Value?.Trim();
                    if (string.IsNullOrEmpty(name)) return null;

                    string local = el.Name.LocalName;
                    string kind;
                    if (local.StartsWith("SW.Types.", StringComparison.Ordinal)) kind = "UDT";
                    else
                    {
                        string t = local.Substring("SW.Blocks.".Length);
                        kind = t == "GlobalDB" || t == "InstanceDB" ? "DB" : t;
                    }
                    return Tuple.Create(name, kind);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
