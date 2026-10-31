using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services
{
    internal static class TiaXmlParseHelper
    {
        public static FlgNetModel ParseFlgNet(XElement flgNet)
        {
            var model = new FlgNetModel();
            if (flgNet == null) return model;

            model.OriginalXml = flgNet.ToString();
            model.AccessByUid = BuildAccessTextMap(flgNet);
            model.ScopeByUid = BuildAccessScopeMap(flgNet);
            XmlAstDfsWalker.Walk(flgNet, new FlgPartCollectVisitor(model.Parts));
            XmlAstDfsWalker.Walk(flgNet, new FlgWireCollectVisitor(model.Wires));

            return model;
        }

        public static Dictionary<string, string> BuildAccessTextMap(XElement scope)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (scope == null) return result;

            var accesses = CollectByLocalName(scope, "Access");
            foreach (var access in accesses)
            {
                var uid = access.Attribute("UId")?.Value;
                if (string.IsNullOrWhiteSpace(uid)) continue;
                result[uid] = BuildAccessText(access);
            }

            return result;
        }

        /// <summary>
        /// Builds the Scope map of Access elements (UId -> Scope).
        /// Scope kinds: GlobalVariable, LocalVariable, TypedConstant, etc.
        /// TypedConstant marks constant inputs that are not tied to the left power rail.
        /// </summary>
        public static Dictionary<string, string> BuildAccessScopeMap(XElement scope)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (scope == null) return result;

            var accesses = CollectByLocalName(scope, "Access");
            foreach (var access in accesses)
            {
                var uid = access.Attribute("UId")?.Value;
                var scopeValue = access.Attribute("Scope")?.Value ?? "";
                if (string.IsNullOrWhiteSpace(uid)) continue;
                result[uid] = scopeValue.Trim();
            }

            return result;
        }

        public static List<CallParameterRef> ParseCallParameters(XElement callInfo)
        {
            var result = new List<CallParameterRef>();
            if (callInfo == null) return result;

            foreach (var parameter in callInfo.Elements().Where(x => x.Name.LocalName == "Parameter"))
            {
                var name = parameter.Attribute("Name")?.Value ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;

                result.Add(new CallParameterRef
                {
                    Name = name.Trim(),
                    Section = (parameter.Attribute("Section")?.Value ?? "").Trim()
                });
            }

            return result;
        }

        public static string NormalizeSymbol(string symbol)
        {
            return SanitizeSymbol(symbol);
        }

        public static List<XElement> CollectByLocalName(XElement root, params string[] localNames)
        {
            var collected = new List<XElement>();
            if (root == null) return collected;

            var visitor = new LocalNameCollectVisitor(localNames, collected);
            XmlAstDfsWalker.Walk(root, visitor);
            return collected;
        }

        /// <summary>
        /// Rebuild readable SCL source text from a &lt;StructuredText&gt; network element.
        ///
        /// TIA exports SCL as a token tree (Token/Access/Blank/NewLine/LineComment...),
        /// NOT as plain text — so <c>XElement.Value</c> only yields comment text and drops
        /// all code. This walks the tree in document order and re-emits the source, matching
        /// what the block viewer shows. Element semantics (verified against a real V15.1 export):
        ///   Token[@Text]                → literal operator/keyword text (IF, :=, ;, (, ), AND ...)
        ///   Blank[@Num]                 → Num spaces (default 1)
        ///   NewLine[@Num]               → Num line breaks (default 1)
        ///   Access &gt; Symbol &gt; Component[@Name]  → variable path (dot-joined), with nested [index]
        ///   Access &gt; Constant &gt; ConstantValue    → literal value (0, True, ...)
        ///   Access &gt; Constant[@Name]              → named constant (MaxSpeed ...)
        ///   LineComment &gt; Text          → "// text"
        /// Returns "" if the element is null or contains no reconstructable content.
        /// </summary>
        public static string RebuildStructuredText(XElement structuredText)
        {
            if (structuredText == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var node in structuredText.Elements())
                AppendStructuredTextNode(sb, node);
            return sb.ToString();
        }

        private static void AppendStructuredTextNode(System.Text.StringBuilder sb, XElement el)
        {
            switch (el.Name.LocalName)
            {
                case "Token":
                    sb.Append(el.Attribute("Text")?.Value ?? "");
                    break;

                case "Blank":
                    sb.Append(new string(' ', ParseCount(el.Attribute("Num")?.Value)));
                    break;

                case "NewLine":
                    sb.Append(new string('\n', ParseCount(el.Attribute("Num")?.Value)));
                    break;

                case "Access":
                    AppendStructuredTextAccess(sb, el);
                    break;

                case "LineComment":
                {
                    // <LineComment><Text>...</Text></LineComment> — TIA trims to a single line.
                    var text = el.Elements().FirstOrDefault(c => c.Name.LocalName == "Text")?.Value ?? "";
                    sb.Append("//").Append(text);
                    break;
                }

                case "Comment":
                {
                    // Block comment <Comment><Text>...</Text></Comment> → (* ... *)
                    var text = el.Elements().FirstOrDefault(c => c.Name.LocalName == "Text")?.Value ?? "";
                    sb.Append("(*").Append(text).Append("*)");
                    break;
                }

                default:
                    // Unknown container: recurse so nested Token/Access are not lost.
                    foreach (var child in el.Elements())
                        AppendStructuredTextNode(sb, child);
                    break;
            }
        }

        /// <summary>
        /// Emit the text of one &lt;Access&gt; inside a StructuredText body. Handles symbol
        /// component paths (with array indices and nested tokens) and constants (literal or named).
        /// </summary>
        private static void AppendStructuredTextAccess(System.Text.StringBuilder sb, XElement access)
        {
            var symbol = FindFirstByLocalName(access, "Symbol");
            if (symbol != null)
            {
                bool first = true;
                foreach (var child in symbol.Elements())
                {
                    if (child.Name.LocalName == "Component")
                    {
                        if (!first) sb.Append('.');
                        sb.Append(child.Attribute("Name")?.Value ?? "");
                        first = false;
                        // Nested tokens/accesses inside a Component (e.g. array "[" index "]").
                        foreach (var inner in child.Elements())
                            AppendStructuredTextNode(sb, inner);
                    }
                    else
                    {
                        // Token (".") or other separators between components.
                        AppendStructuredTextNode(sb, child);
                    }
                }
                return;
            }

            var constant = FindFirstByLocalName(access, "Constant");
            if (constant != null)
            {
                var named = constant.Attribute("Name")?.Value;
                if (!string.IsNullOrWhiteSpace(named)) { sb.Append(named); return; }
                var value = FindFirstByLocalName(constant, "ConstantValue")?.Value;
                sb.Append(value?.Trim() ?? "");
                return;
            }
        }

        private static int ParseCount(string num)
        {
            if (!string.IsNullOrWhiteSpace(num) && int.TryParse(num, out var n) && n > 0) return n;
            return 1;
        }

        /// <summary>
        /// Parse an STL &lt;StatementList&gt; network into a language-neutral model.
        ///
        /// TIA exports STL as a list of &lt;StlStatement&gt; nodes. Each statement carries one or
        /// more &lt;StlToken Text="..."/&gt; (mnemonic/operator such as CALL, U, =, L ...) and optional
        /// &lt;Access&gt; children. Calls use &lt;Access Scope="Call"&gt;&lt;CallInfo Name="..." BlockType="FC"/&gt;,
        /// which is the SAME CallInfo shape used by LAD/FBD — so call edges are fully recoverable.
        /// Operand accesses use &lt;Access&gt;&lt;Symbol&gt;&lt;Component Name="..."/&gt; or &lt;Constant&gt;, matching
        /// <see cref="BuildAccessText"/>. EMPTY_LINE tokens are blank separators.
        ///
        /// Direction in STL is not encoded structurally (no Wire/Pin graph), so operand accesses are
        /// left Unresolved; call edges — the important signal for scheduling blocks like OB1 — are exact.
        /// </summary>
        public static StatementListModel ParseStatementList(XElement statementList)
        {
            var model = new StatementListModel();
            if (statementList == null) return model;

            model.OriginalXml = statementList.ToString();

            var statements = statementList.Elements()
                .Where(e => string.Equals(e.Name.LocalName, "StlStatement", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var stmt in statements)
            {
                var uid = stmt.Attribute("UId")?.Value ?? "";

                // Collect the mnemonic tokens (CALL, U, O, =, L, T, EMPTY_LINE ...).
                var tokens = stmt.Elements()
                    .Where(e => string.Equals(e.Name.LocalName, "StlToken", StringComparison.OrdinalIgnoreCase))
                    .Select(e => (e.Attribute("Text")?.Value ?? "").Trim())
                    .ToList();

                bool isEmptyLine = tokens.Count == 1 &&
                    string.Equals(tokens[0], "EMPTY_LINE", StringComparison.OrdinalIgnoreCase);

                var mnemonic = string.Join(" ", tokens.Where(t => !string.IsNullOrEmpty(t)
                    && !string.Equals(t, "EMPTY_LINE", StringComparison.OrdinalIgnoreCase)));

                // Operand: CallInfo (call) or a plain symbol/constant Access.
                var callInfo = FindFirstByLocalName(stmt, "CallInfo");
                string calleeName = "", calleeBlockType = "", operandText = "";

                if (callInfo != null)
                {
                    calleeName = (callInfo.Attribute("Name")?.Value ?? "").Trim();
                    calleeBlockType = (callInfo.Attribute("BlockType")?.Value ?? "").Trim();
                    var instance = callInfo.Descendants()
                        .FirstOrDefault(x => x.Name.LocalName == "Component")?.Attribute("Name")?.Value ?? "";
                    operandText = SanitizeSymbol(calleeName);
                    model.Statements.Add(new StlStatementModel
                    {
                        Uid = uid,
                        Mnemonic = mnemonic,
                        IsCall = true,
                        IsEmptyLine = false,
                        CalleeName = calleeName,
                        CalleeBlockType = calleeBlockType,
                        InstancePath = NormalizeSymbol(instance),
                        OperandText = operandText
                    });
                    continue;
                }

                // Non-call operand access (address / symbol / constant).
                var access = stmt.Elements()
                    .FirstOrDefault(e => string.Equals(e.Name.LocalName, "Access", StringComparison.OrdinalIgnoreCase));
                var accessScope = "";
                if (access != null)
                {
                    accessScope = access.Attribute("Scope")?.Value ?? "";
                    operandText = BuildAccessText(access);
                }

                model.Statements.Add(new StlStatementModel
                {
                    Uid = uid,
                    Mnemonic = mnemonic,
                    IsCall = false,
                    IsEmptyLine = isEmptyLine,
                    OperandText = operandText,
                    OperandScope = accessScope
                });
            }

            return model;
        }

        /// <summary>
        /// Rebuild readable STL source text from a &lt;StatementList&gt; element. One statement per line,
        /// mnemonic followed by its operand (call name, symbol, or constant). Mirrors what the STL
        /// editor shows, e.g. <c>CALL "Grundfunktionen"</c>.
        /// </summary>
        public static string RebuildStatementList(XElement statementList)
        {
            var model = ParseStatementList(statementList);
            var sb = new System.Text.StringBuilder();

            foreach (var stmt in model.Statements)
            {
                if (stmt.IsEmptyLine)
                {
                    sb.AppendLine();
                    continue;
                }

                var line = stmt.Mnemonic ?? "";
                if (!string.IsNullOrEmpty(stmt.OperandText))
                {
                    // Quote call/symbol names the way TIA does; constants pass through.
                    var operand = stmt.IsCall ? QuoteStlName(stmt.CalleeName) : stmt.OperandText;
                    line = string.IsNullOrEmpty(line) ? operand : (line + " " + operand);
                }
                sb.AppendLine(line.TrimEnd());
            }

            return sb.ToString();
        }

        private static string QuoteStlName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var trimmed = name.Trim();
            if (trimmed.StartsWith("\"") || trimmed.StartsWith("%")) return trimmed;
            return "\"" + trimmed + "\"";
        }

        private static string BuildAccessText(XElement access)
        {
            var symbol = FindFirstByLocalName(access, "Symbol");
            if (symbol != null)
            {
                var segments = new List<string>();
                foreach (var comp in symbol.Elements().Where(e => e.Name.LocalName == "Component"))
                {
                    var name = comp.Attribute("Name")?.Value ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var accessModifier = comp.Attribute("AccessModifier")?.Value;
                    if (string.Equals(accessModifier, "Array", StringComparison.OrdinalIgnoreCase))
                    {
                        var indexValue = FindFirstByLocalName(comp, "ConstantValue")?.Value;
                        if (!string.IsNullOrWhiteSpace(indexValue))
                        {
                            segments.Add(SanitizeSegment(name) + "[" + indexValue.Trim() + "]");
                            continue;
                        }
                    }

                    segments.Add(SanitizeSegment(name));
                }

                if (segments.Count > 0) return string.Join(".", segments);
                if (!string.IsNullOrWhiteSpace(symbol.Value)) return SanitizeSymbol(symbol.Value.Trim());
            }

            var constant = FindFirstByLocalName(access, "Constant");
            if (constant != null)
            {
                var value = FindFirstByLocalName(constant, "ConstantValue")?.Value;
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                if (!string.IsNullOrWhiteSpace(constant.Value)) return constant.Value.Trim();
            }

            return (access.Value ?? "").Trim();
        }

        private static string SanitizeSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return "";
            if (symbol.StartsWith("%", StringComparison.Ordinal)) return symbol;
            var parts = symbol.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(".", parts.Select(SanitizeSegment));
        }

        private static string SanitizeSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment)) return "";
            if (segment.StartsWith("%", StringComparison.Ordinal)) return segment;

            bool needsQuote = segment.Length > 0 && char.IsDigit(segment[0]);
            if (!needsQuote)
            {
                foreach (var c in segment)
                {
                    if (c == ' ' || c == '-' || c == '/' || c == '(' || c == ')' || c == '+' || c == '*' || c == ',')
                    {
                        needsQuote = true;
                        break;
                    }
                }
            }

            return needsQuote ? "\"" + segment + "\"" : segment;
        }

        private static XElement FindFirstByLocalName(XElement root, string localName)
        {
            if (root == null || string.IsNullOrWhiteSpace(localName)) return null;

            var visitor = new FirstLocalNameVisitor(localName);
            XmlAstDfsWalker.Walk(root, visitor);
            return visitor.Found;
        }

        private static FlgPart BuildPartFromElement(XElement element)
        {
            if (element == null) return null;
            var uid = element.Attribute("UId")?.Value;
            if (string.IsNullOrWhiteSpace(uid)) return null;

            if (string.Equals(element.Name.LocalName, "Part", StringComparison.OrdinalIgnoreCase))
            {
                var partName = (element.Attribute("Name")?.Value ?? "").Trim();
                var srcType = element.Elements().FirstOrDefault(x =>
                    x.Name.LocalName == "TemplateValue" &&
                    string.Equals(x.Attribute("Name")?.Value ?? string.Empty, "SrcType", StringComparison.OrdinalIgnoreCase))
                    ?.Value?.Trim() ?? "";

                return new FlgPart
                {
                    Uid = uid,
                    IsCall = false,
                    Name = partName,
                    DisplayName = partName,
                    Negated = element.Elements().Any(x => x.Name.LocalName == "Negated"),
                    SrcType = srcType,
                    Attributes = element.Elements().Where(x => x.Name.LocalName == "Attribute")
                        .ToDictionary(x => x.Attribute("Name")?.Value ?? "", x => x.Value, StringComparer.OrdinalIgnoreCase),
                    TemplateValues = element.Elements().Where(x => x.Name.LocalName == "TemplateValue")
                        .ToDictionary(x => x.Attribute("Name")?.Value ?? "", x => x.Value, StringComparer.OrdinalIgnoreCase)
                };
            }

            if (string.Equals(element.Name.LocalName, "Call", StringComparison.OrdinalIgnoreCase))
            {
                var callInfo = element.Elements().FirstOrDefault(x => x.Name.LocalName == "CallInfo");
                var callName = (callInfo?.Attribute("Name")?.Value ?? "").Trim();
                var blockType = (callInfo?.Attribute("BlockType")?.Value ?? "").Trim();
                var instanceName = callInfo?.Descendants().FirstOrDefault(x => x.Name.LocalName == "Component")?.Attribute("Name")?.Value;

                return new FlgPart
                {
                    Uid = uid,
                    IsCall = true,
                    Name = "Call",
                    DisplayName = callName,
                    CallName = callName,
                    BlockType = blockType,
                    Instance = NormalizeSymbol(instanceName ?? ""),
                    CallParameters = ParseCallParameters(callInfo)
                };
            }

            return null;
        }

        private static FlgWire BuildWireFromElement(XElement wire)
        {
            if (wire == null || !string.Equals(wire.Name.LocalName, "Wire", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var w = new FlgWire
            {
                Uid = wire.Attribute("UId")?.Value ?? "",
                HasPowerrail = wire.Elements().Any(e => e.Name.LocalName == "Powerrail"),
                IdentAccessUid = wire.Elements().FirstOrDefault(e => e.Name.LocalName == "IdentCon")
                    ?.Attribute("UId")?.Value ?? ""
            };

            foreach (var nc in wire.Elements().Where(e => e.Name.LocalName == "NameCon"))
            {
                var uid = nc.Attribute("UId")?.Value ?? "";
                var pin = nc.Attribute("Name")?.Value ?? "";
                if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(pin)) continue;

                w.NameConnections.Add(new FlgNameConnection
                {
                    Uid = uid,
                    Pin = pin
                });
            }

            return w;
        }

        private sealed class FlgPartCollectVisitor : IXmlAstVisitor
        {
            private readonly List<FlgPart> _parts;

            public FlgPartCollectVisitor(List<FlgPart> parts)
            {
                _parts = parts ?? new List<FlgPart>();
            }

            public bool Enter(XElement node, int depth)
            {
                var parentName = node.Parent?.Name.LocalName;
                if (string.Equals(parentName, "Parts", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(node.Name.LocalName, "Part", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(node.Name.LocalName, "Call", StringComparison.OrdinalIgnoreCase)))
                {
                    var part = BuildPartFromElement(node);
                    if (part != null) _parts.Add(part);
                    return false;
                }

                return true;
            }

            public void Exit(XElement node, int depth)
            {
            }
        }

        private sealed class FlgWireCollectVisitor : IXmlAstVisitor
        {
            private readonly List<FlgWire> _wires;

            public FlgWireCollectVisitor(List<FlgWire> wires)
            {
                _wires = wires ?? new List<FlgWire>();
            }

            public bool Enter(XElement node, int depth)
            {
                var parentName = node.Parent?.Name.LocalName;
                if (string.Equals(parentName, "Wires", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(node.Name.LocalName, "Wire", StringComparison.OrdinalIgnoreCase))
                {
                    var wire = BuildWireFromElement(node);
                    if (wire != null) _wires.Add(wire);
                    return false;
                }

                return true;
            }

            public void Exit(XElement node, int depth)
            {
            }
        }

        private sealed class FirstLocalNameVisitor : IXmlAstVisitor
        {
            private readonly string _target;
            public XElement Found { get; private set; }

            public FirstLocalNameVisitor(string target)
            {
                _target = target ?? "";
            }

            public bool Enter(XElement node, int depth)
            {
                if (Found != null) return false;
                if (string.Equals(node.Name.LocalName, _target, StringComparison.OrdinalIgnoreCase))
                {
                    Found = node;
                    return false;
                }
                return true;
            }

            public void Exit(XElement node, int depth)
            {
            }
        }
    }

    public sealed class CallParameterRef
    {
        public string Name { get; set; }
        public string Section { get; set; }
    }

    /// <summary>
    /// Language-neutral model of a parsed FlgNet (LAD/FBD network).
    /// </summary>
    public sealed class FlgNetModel
    {
        public Dictionary<string, string> AccessByUid { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ScopeByUid { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<FlgPart> Parts { get; set; } = new List<FlgPart>();
        public List<FlgWire> Wires { get; set; } = new List<FlgWire>();

        // Retain the raw XML so downstream conversion steps can reuse it.
        public string OriginalXml { get; set; } = "";
    }

    /// <summary>
    /// A single part (instruction or call) inside a parsed LAD/FBD network.
    /// </summary>
    public sealed class FlgPart
    {
        public string Uid { get; set; }
        public bool IsCall { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string CallName { get; set; }
        public string BlockType { get; set; }
        public string Instance { get; set; }
        public bool Negated { get; set; }
        public string SrcType { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> TemplateValues { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<CallParameterRef> CallParameters { get; set; } = new List<CallParameterRef>();
    }

    /// <summary>
    /// A wire (or power-rail) connection between parts in a parsed LAD/FBD network.
    /// </summary>
    public sealed class FlgWire
    {
        public string Uid { get; set; }
        public bool HasPowerrail { get; set; }
        public string IdentAccessUid { get; set; }
        public List<FlgNameConnection> NameConnections { get; set; } = new List<FlgNameConnection>();
    }

    public sealed class FlgNameConnection
    {
        public string Uid { get; set; }
        public string Pin { get; set; }
    }

    /// <summary>
    /// Language-neutral model of an STL &lt;StatementList&gt; network (see
    /// <see cref="TiaXmlParseHelper.ParseStatementList"/>).
    /// </summary>
    public sealed class StatementListModel
    {
        public List<StlStatementModel> Statements { get; set; } = new List<StlStatementModel>();
        public string OriginalXml { get; set; } = "";
    }

    /// <summary>
    /// One STL statement: a mnemonic and its operand, with optional call info.
    /// </summary>
    public sealed class StlStatementModel
    {
        public string Uid { get; set; } = "";
        public string Mnemonic { get; set; } = "";     // CALL, U, O, =, L, T ...
        public bool IsCall { get; set; }
        public bool IsEmptyLine { get; set; }
        public string OperandText { get; set; } = "";  // symbol/constant/call name (display)
        public string OperandScope { get; set; } = ""; // GlobalVariable / LocalVariable / ... ("" for calls)
        public string CalleeName { get; set; } = "";   // call target (raw)
        public string CalleeBlockType { get; set; } = ""; // FC / FB / ...
        public string InstancePath { get; set; } = "";  // instance DB for FB calls ("" otherwise)
    }
}
