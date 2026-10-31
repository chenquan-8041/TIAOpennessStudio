using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TIAOpennessStudio.Models.Blocks;
using TIAOpennessStudio.Models.Lad;
using TIAOpennessStudio.Services.Builders;
using TIAOpennessStudio.Services.Implementations;
using TIAOpennessStudio.Services.Lad;

namespace TIAOpennessStudio.Services.Blocks
{
    /// <summary>
    /// Single read-side contract for a PLC block.
    ///
    /// A <see cref="BlockSnapshot"/> owns the raw export XML (the write-back skeleton) plus the
    /// facts the canonical IR does not carry (block attributes, instance-DB identity, source
    /// version, revision). Every projection below is derived from that one snapshot:
    ///   - BuildIr        : canonical JSON (LAD/FBD/GRAPH write contract)
    ///   - BuildInterface : canonical interface tree (DB/UDT/FB/FC)
    ///   - BuildSource    : reconstructed SCL/STL network text
    ///   - BuildXml       : verbatim export XML
    ///
    /// Parsing is local-name based on purpose: TIA exports are namespace-versioned (v3/v4/v5)
    /// and the block structure must be readable regardless of the installed version.
    /// </summary>
    public static class BlockSnapshotService
    {
        /// <summary>Bumped whenever the snapshot/projection contract changes; participates in Revision.</summary>
        public const string ContractVersion = "T7";

        private static readonly JsonSerializerOptions IrJsonOptions = new JsonSerializerOptions
        {
            // Compact (single-line) serialization: indentation inflates LAD/FBD IR to tens of
            // thousands of lines and pushes it past the context budget.
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // ─────────────────────────────────────────────────────────────────────
        // Snapshot
        // ─────────────────────────────────────────────────────────────────────

        public static BlockSnapshot FromXml(string xml, string blockId = null, string projectKey = null)
        {
            if (string.IsNullOrWhiteSpace(xml))
                throw new ArgumentException("Block XML is empty.", nameof(xml));

            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var blockElement = FindBlockElement(document.Root);
            if (blockElement == null)
                throw new InvalidOperationException("No SW.Blocks.* / SW.Types.* block element found.");

            var attrList = blockElement.Elements()
                .FirstOrDefault(e => Eq(e.Name.LocalName, "AttributeList"));

            var snapshot = new BlockSnapshot
            {
                ProjectKey = projectKey ?? "",
                BlockId = blockId ?? "",
                RawXml = xml,
                XmlBlockElement = blockElement.Name.LocalName,
                XmlBlockKind = LastLocalSegment(blockElement.Name.LocalName),
                BlockType = NormalizeBlockType(LastLocalSegment(blockElement.Name.LocalName)),
                Name = ReadChildText(attrList, "Name"),
                Language = ReadChildText(attrList, "ProgrammingLanguage"),
                SourceProfile = ReadSourceProfile(document)
            };

            var numberText = ReadChildText(attrList, "Number");
            if (int.TryParse(numberText, out var number))
                snapshot.Number = number;

            // UDT/DB exports do not always carry ProgrammingLanguage; keep UDT explicit and
            // never fall back to LAD (the LadBlock model default is not a block fact).
            if (string.IsNullOrWhiteSpace(snapshot.Language)
                && string.Equals(snapshot.XmlBlockKind, "PlcStruct", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.Language = "UDT";
            }

            snapshot.Title = ReadObjectListText(blockElement, "Title");
            snapshot.Comment = ReadObjectListText(blockElement, "Comment");

            snapshot.IsInstanceDb = string.Equals(snapshot.XmlBlockKind, "InstanceDB", StringComparison.OrdinalIgnoreCase);
            snapshot.InstanceOfName = ReadChildText(attrList, "InstanceOfName");
            if (!string.IsNullOrWhiteSpace(snapshot.InstanceOfName))
                snapshot.IsInstanceDb = true;

            snapshot.IsGraph = string.Equals(snapshot.Language, "GRAPH", StringComparison.OrdinalIgnoreCase)
                                || document.Descendants().Any(e => Eq(e.Name.LocalName, "Graph"));

            snapshot.Attributes = ReadAttributes(attrList);
            snapshot.Revision = ComputeRevision(xml, snapshot.SourceProfile, ContractVersion);
            return snapshot;
        }

        /// <summary>
        /// Stable content revision: canonical XML + contract version + source profile.
        ///
        /// The raw export cannot be hashed as-is: TIA writes a fresh timestamp into
        /// &lt;DocumentInfo&gt; on every export and re-orders attributes / reformats the file after
        /// a re-import or compile, so two reads of unchanged content hashed differently and a
        /// patch base_revision was never stable. The revision therefore hashes a structural
        /// canonicalization (see <see cref="CanonicalForRevision"/>), not raw bytes.
        /// </summary>
        public static string ComputeRevision(string rawXml, string sourceProfile, string contractVersion)
        {
            var basis = CanonicalForRevision(rawXml)
                        + "\n//contract:" + (contractVersion ?? "")
                        + "\n//profile:" + (sourceProfile ?? "");
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(basis));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Content-only XML for revision hashing.
        ///
        /// The export is parsed and re-serialized structurally, because TIA does not guarantee a
        /// stable byte layout for unchanged content:
        ///   - &lt;DocumentInfo&gt; carries the export timestamp / installed products → dropped;
        ///   - attribute order changes after a re-import or compile (DB "Name"/"Datatype" swapped
        ///     on V15.1) → attributes sorted by name;
        ///   - formatting whitespace between elements → removed (DisableFormatting).
        /// Element text stays verbatim, so a real content edit still changes the revision.
        /// </summary>
        private static string CanonicalForRevision(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return "";

            XDocument document;
            try
            {
                document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch
            {
                // Unparseable XML still needs a deterministic hash: normalize line endings only.
                return xml.Replace("\r\n", "\n").Replace('\r', '\n');
            }

            var documentInfo = document.Descendants().FirstOrDefault(
                e => string.Equals(e.Name.LocalName, "DocumentInfo", StringComparison.OrdinalIgnoreCase));
            documentInfo?.Remove();

            NormalizeForRevision(document.Root);
            return document.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>Remove layout-only nodes and sort attributes so re-serialization cannot change the hash.</summary>
        private static void NormalizeForRevision(XElement element)
        {
            if (element == null) return;

            foreach (var node in element.Nodes().ToList())
            {
                if (node is XText text && string.IsNullOrWhiteSpace(text.Value))
                    text.Remove();
                else if (node is XComment comment)
                    comment.Remove();
                else if (node is XElement child)
                    NormalizeForRevision(child);
            }

            var attributes = element.Attributes()
                .OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal)
                .ThenBy(a => a.Name.LocalName, StringComparer.Ordinal)
                .ToList();
            if (attributes.Count > 1)
            {
                element.RemoveAttributes();
                foreach (var attribute in attributes)
                    element.SetAttributeValue(attribute.Name, attribute.Value);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Projection: canonical IR (LAD/FBD/GRAPH write contract)
        // ─────────────────────────────────────────────────────────────────────

        public static bool BuildIr(BlockSnapshot snapshot, out string json, out string summary)
        {
            if (snapshot == null)
            {
                json = null;
                summary = null;
                return false;
            }
            return BuildIr(snapshot.RawXml, 1, 0, out json, out summary);
        }

        public static bool BuildIr(BlockSnapshot snapshot, int networkStart, int networkCount,
            out string json, out string summary)
        {
            if (snapshot == null)
            {
                json = null;
                summary = null;
                return false;
            }
            return BuildIr(snapshot.RawXml, networkStart, networkCount, out json, out summary);
        }

        /// <summary>
        /// XML -> canonical JSON. Network-range arguments only apply to non-GRAPH blocks:
        /// GRAPH exports carry one sequence, so the whole contract is always returned.
        /// </summary>
        public static bool BuildIr(string xml, int networkStart, int networkCount,
            out string json, out string summary)
        {
            json = null;
            summary = null;
            if (string.IsNullOrWhiteSpace(xml))
                return false;

            try
            {
                var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                var meta = ExtractBlockMeta(document.Root, xml);
                if (!IsGraphBlock(document))
                    return TryBuildCanonicalJson(xml, networkStart, networkCount, out json, out summary);

                return TryBuildGraphJson(document, xml, meta, out json, out summary);
            }
            catch
            {
                json = null;
                summary = null;
                return false;
            }
        }

        /// <summary>Reference text wrapper used by Add to AI Context and file drop.</summary>
        public static bool BuildIrReference(BlockSnapshot snapshot, string displayName, out string referenceText)
        {
            referenceText = null;
            if (!BuildIr(snapshot, 1, 0, out var json, out var summary))
                return false;

            var sb = new StringBuilder();
            sb.AppendLine($"TIA Agent IR Reference: {(!string.IsNullOrWhiteSpace(displayName) ? displayName : "UnnamedBlock")}");
            sb.AppendLine(summary);
            sb.AppendLine("Use this canonical JSON as the primary semantic source instead of reconstructing from raw XML.");
            sb.AppendLine("It is the same shape the write tools accept: pass networks[].body back to build_lad_*, and");
            sb.AppendLine("interface to build_udt/build_global_db. Preserve uid, pin and scope when editing a network.");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(json);
            sb.AppendLine("```");
            referenceText = sb.ToString().TrimEnd();
            return true;
        }

        /// <summary>
        /// Add-to-AI / file-drop reference policy: SCL/STL blocks are attached as plain source
        /// text (roughly an order of magnitude smaller than the token tree); every other block
        /// type keeps the canonical JSON contract (DB/UDT IR is interface-only and already small).
        /// </summary>
        public static bool BuildReferenceText(BlockSnapshot snapshot, string displayName, out string referenceText)
        {
            referenceText = null;
            if (snapshot == null) return false;

            var language = (snapshot.Language ?? "").Trim();
            var isTextLanguage = language.EndsWith("SCL", StringComparison.OrdinalIgnoreCase)
                                 || language.EndsWith("STL", StringComparison.OrdinalIgnoreCase);
            var source = isTextLanguage ? BuildSource(snapshot) : null;
            if (source != null)
            {
                var display = string.IsNullOrWhiteSpace(displayName) ? snapshot.Name : displayName;
                var sb = new StringBuilder();
                sb.AppendLine("TIA Agent Source Reference: " + (string.IsNullOrWhiteSpace(display) ? "UnnamedBlock" : display));
                sb.AppendLine("Block=" + snapshot.Name
                              + " | Kind=" + snapshot.BlockType
                              + " | Language=" + snapshot.Language
                              + " | Format=source-text");
                sb.AppendLine("Reconstructed from the XML token tree. Block declaration sections (VAR) are not included;");
                sb.AppendLine("this is the network logic source used for analysis and edits.");
                sb.AppendLine();
                sb.AppendLine(source);
                referenceText = sb.ToString().TrimEnd();
                return true;
            }

            return BuildIrReference(snapshot, displayName, out referenceText);
        }

        /// <summary>True when the export carries a Graph/Sequence subtree (S7-GRAPH block).</summary>
        private static bool IsGraphBlock(XDocument document)
        {
            return document != null
                   && document.Descendants().Any(x =>
                       string.Equals(x.Name.LocalName, "Graph", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// GRAPH read path: exported block -> GraphJsonBridge sequence JSON, exactly the object
        /// build_graph_fb accepts, so read -> edit -> write round-trips.
        /// </summary>
        private static bool TryBuildGraphJson(
            XDocument document, string xmlContent, BlockMeta meta, out string json, out string summary)
        {
            json = null;
            summary = null;

            try
            {
                var root = document?.Root;
                var sequence = LadRendering.Parser.GraphXmlParser.Parse(xmlContent);

                var obj = new JsonObject
                {
                    ["blockName"] = meta.Name,
                    ["blockNumber"] = meta.Number,
                    ["blockType"] = meta.BlockType,
                    ["programmingLanguage"] = "GRAPH",
                    ["titleZhCn"] = meta.Title ?? "",
                    ["commentZhCn"] = meta.Comment ?? "",
                    ["inputs"] = MembersToJson(ExtractMembers(root, "Input", "In"), "Input"),
                    ["outputs"] = MembersToJson(ExtractMembers(root, "Output", "Out"), "Output"),
                    ["inOuts"] = MembersToJson(ExtractMembers(root, "InOut", "In_Out"), "InOut"),
                    ["statics"] = MembersToJson(ExtractMembers(root, "Static", "Stat"), "Static"),
                    ["temps"] = MembersToJson(ExtractMembers(root, "Temp", "Var_Temp"), "Temp"),
                    ["sequence"] = GraphJsonBridge.SequenceToJson(sequence),
                    ["fidelity"] = new JsonObject
                    {
                        ["truncated"] = false,
                        ["totalNetworks"] = 1,
                        ["returnedFrom"] = 1,
                        ["returnedCount"] = 1
                    }
                };

                json = obj.ToJsonString(IrJsonOptions);
                summary = "Block=" + meta.Name
                          + " | Type=" + meta.BlockType
                          + " | Language=GRAPH"
                          + " | Steps=" + sequence.Steps.Count
                          + " | Transitions=" + sequence.Transitions.Count
                          + " | Format=graph-write-contract";
                return true;
            }
            catch
            {
                json = null;
                summary = null;
                return false;
            }
        }

        private static JsonArray MembersToJson(IEnumerable<CanonicalMember> members, string sectionName)
        {
            var array = new JsonArray();
            foreach (var member in members ?? Enumerable.Empty<CanonicalMember>())
            {
                // The GRAPH template owns the system interface; only user members may travel back.
                if (GraphSystemMembers.IsSystemMember(sectionName, member.Name, member.Datatype))
                    continue;

                array.Add(new JsonObject
                {
                    ["name"] = member.Name ?? "",
                    ["datatype"] = member.Datatype ?? ""
                });
            }
            return array;
        }

        /// <summary>
        /// Unified path: block XML to <see cref="LadBlock"/> (via LadBlockParser) to canonical JSON
        /// (via LadJson). Supports LAD, FBD, SCL, STL and interface-only blocks (UDT / DB).
        /// Whole networks are dropped when the JSON exceeds the reference budget, never cut mid-JSON.
        /// </summary>
        private static bool TryBuildCanonicalJson(
            string xmlContent,
            int networkStart,
            int networkCount,
            out string json,
            out string summary)
        {
            json = null;
            summary = null;

            try
            {
                var facts = Facts();
                var block = LadBlockParser.Parse(xmlContent, "block", facts);

                var total = block.Networks.Count;
                var start = Math.Max(1, networkStart);
                var take = networkCount <= 0 ? int.MaxValue : networkCount;
                var slice = block.Networks.Skip(start - 1).Take(take).ToList();

                var projected = new LadBlock
                {
                    BlockId = block.BlockId,
                    Kind = block.Kind,
                    XmlBlockElement = block.XmlBlockElement,
                    XmlBlockKind = block.XmlBlockKind,
                    IsInstanceDb = block.IsInstanceDb,
                    InstanceOfName = block.InstanceOfName,
                    Attributes = block.Attributes,
                    Name = block.Name,
                    Number = block.Number,
                    Language = block.Language,
                    Title = block.Title,
                    Comment = block.Comment,
                    Interface = block.Interface,
                    Networks = new List<LadNetwork>(slice)
                };

                json = ToCanonicalJson(projected, total, start);

                // Budget: drop whole trailing networks instead of truncating the JSON text.
                while (json.Length > DefaultValues.MaxReferenceLength && projected.Networks.Count > 1)
                {
                    projected.Networks.RemoveAt(projected.Networks.Count - 1);
                    json = ToCanonicalJson(projected, total, start);
                }

                var returned = projected.Networks.Count;
                summary = "Block=" + block.Name
                          + " | Kind=" + block.Kind
                          + " | Language=" + block.Language
                          + " | Networks=" + returned + "/" + total
                          + (returned < total ? " | Truncated" : string.Empty);
                return true;
            }
            catch
            {
                json = null;
                summary = null;
                return false;
            }
        }

        private static string ToCanonicalJson(LadBlock block, int totalNetworks, int returnedFrom)
        {
            var obj = LadJson.ToJson(block);
            obj["fidelity"] = new JsonObject
            {
                ["truncated"] = block.Networks.Count < totalNetworks,
                ["totalNetworks"] = totalNetworks,
                ["returnedFrom"] = returnedFrom,
                ["returnedCount"] = block.Networks.Count
            };
            return obj.ToJsonString();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Projection: interface (DB / UDT / FB / FC)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Canonical interface node tree (the same object export_block_ir exposes as "interface").
        /// GRAPH returns null: its interface is expressed as flat user-member arrays in BuildIr.
        /// </summary>
        public static string BuildInterface(BlockSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.RawXml)) return null;
            if (snapshot.IsGraph || string.Equals(snapshot.Language, "GRAPH", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                var block = LadBlockParser.Parse(snapshot.RawXml, snapshot.BlockId ?? "block", Facts());
                if (block.Interface == null) return null;
                return LadJson.ToJson(block.Interface).ToJsonString();
            }
            catch
            {
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Projection: source text (SCL / STL)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reconstructed network source text for SCL/STL blocks. This is the network body only:
        /// the block declaration (VAR sections) is intentionally not synthesized, because edits
        /// go through the patch write path instead of re-importing a generated SCL envelope.
        /// Returns null when the block carries no text network (LAD/FBD/GRAPH/DB/UDT).
        /// </summary>
        public static string BuildSource(BlockSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.RawXml)) return null;

            XElement block;
            try
            {
                var document = XDocument.Parse(snapshot.RawXml, LoadOptions.PreserveWhitespace);
                block = FindBlockElement(document.Root);
            }
            catch
            {
                return null;
            }
            if (block == null) return null;

            var sb = new StringBuilder();
            var order = 0;
            var textNetworks = 0;

            foreach (var unit in block.Descendants().Where(IsCompileUnit))
            {
                order++;
                var structuredText = unit.Descendants()
                    .FirstOrDefault(e => Eq(e.Name.LocalName, "StructuredText"));
                var statementList = unit.Descendants()
                    .FirstOrDefault(e => Eq(e.Name.LocalName, "StatementList"));

                string text = null;
                string language = null;
                if (structuredText != null)
                {
                    text = TiaXmlParseHelper.RebuildStructuredText(structuredText);
                    language = "SCL";
                }
                else if (statementList != null)
                {
                    text = TiaXmlParseHelper.RebuildStatementList(statementList);
                    language = "STL";
                }

                if (string.IsNullOrWhiteSpace(text)) continue;

                textNetworks++;
                var title = ReadObjectListText(unit, "Title");
                var comment = ReadObjectListText(unit, "Comment");
                sb.Append("// Network ").Append(order);
                if (!string.IsNullOrWhiteSpace(title))
                    sb.Append(": ").Append(SingleLine(title));
                sb.Append(" [").Append(language).Append("]\n");
                if (!string.IsNullOrWhiteSpace(comment))
                    sb.Append("// ").Append(SingleLine(comment)).Append('\n');
                sb.Append(text.TrimEnd()).Append('\n').Append('\n');
            }

            if (textNetworks == 0) return null;
            return sb.ToString().TrimEnd();
        }

        /// <summary>Number of networks that actually carry SCL/STL text (used by probes/tests).</summary>
        public static int CountSourceNetworks(BlockSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.RawXml)) return 0;
            try
            {
                var document = XDocument.Parse(snapshot.RawXml, LoadOptions.PreserveWhitespace);
                var block = FindBlockElement(document.Root);
                if (block == null) return 0;

                return block.Descendants()
                    .Where(IsCompileUnit)
                    .Count(unit => unit.Descendants().Any(e =>
                        Eq(e.Name.LocalName, "StructuredText") || Eq(e.Name.LocalName, "StatementList")));
            }
            catch
            {
                return 0;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Projection: raw XML
        // ─────────────────────────────────────────────────────────────────────

        public static string BuildXml(BlockSnapshot snapshot)
        {
            return snapshot?.RawXml;
        }

        // ─────────────────────────────────────────────────────────────────────
        // XML helpers
        // ─────────────────────────────────────────────────────────────────────

        private static XElement FindBlockElement(XElement root)
        {
            if (root == null) return null;
            if (IsBlockElement(root)) return root;
            return root.Descendants().FirstOrDefault(IsBlockElement);
        }

        private static bool IsBlockElement(XElement element)
        {
            var local = element?.Name.LocalName ?? "";
            return local.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase)
                   || local.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCompileUnit(XElement element)
        {
            var local = element?.Name.LocalName ?? "";
            return local.Equals("CompileUnit", StringComparison.OrdinalIgnoreCase)
                   || local.Equals("SW.Blocks.CompileUnit", StringComparison.OrdinalIgnoreCase)
                   || local.EndsWith("CompileUnit", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeBlockType(string kind)
        {
            switch ((kind ?? "").ToUpperInvariant())
            {
                case "GLOBALDB":
                case "INSTANCEDB":
                    return "DB";
                case "PLCSTRUCT":
                    return "UDT";
                default:
                    return kind ?? "";
            }
        }

        private static string LastLocalSegment(string localName)
        {
            var text = localName ?? "";
            var idx = text.LastIndexOf('.');
            return idx >= 0 ? text.Substring(idx + 1) : text;
        }

        private static string ReadChildText(XElement parent, string childName)
        {
            var child = parent?.Elements().FirstOrDefault(e => Eq(e.Name.LocalName, childName));
            return child?.Value?.Trim() ?? "";
        }

        /// <summary>Engineering header of the export itself, then the live-detected version.</summary>
        private static string ReadSourceProfile(XDocument document)
        {
            var engineering = document?.Root?.Elements()
                .FirstOrDefault(e => Eq(e.Name.LocalName, "Engineering"));
            var version = engineering?.Attribute("version")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(version)) return version;

            var detected = TiaVersionAdapter.CurrentVersion;
            if (!string.IsNullOrWhiteSpace(detected)) return detected;

            return LadSchemaRegistry.Resolve(TiaVersionAdapter.CurrentMajorVersion).DisplayVersion;
        }

        private static List<BlockAttribute> ReadAttributes(XElement attrList)
        {
            var result = new List<BlockAttribute>();
            if (attrList == null) return result;

            foreach (var element in attrList.Elements())
            {
                var isComplex = element.HasElements;
                result.Add(new BlockAttribute
                {
                    Name = element.Name.LocalName,
                    Value = isComplex ? "" : (element.Value ?? "").Trim(),
                    IsComplex = isComplex,
                    ChildCount = isComplex ? element.Elements().Count() : 0
                });
            }
            return result;
        }

        private static string ReadObjectListText(XElement blockElement, string compositionName)
        {
            var objectList = blockElement?.Elements()
                .FirstOrDefault(e => Eq(e.Name.LocalName, "ObjectList"));
            var text = objectList?.Descendants().FirstOrDefault(e =>
                Eq(e.Name.LocalName, "MultilingualText") &&
                Eq(e.Attribute("CompositionName")?.Value, compositionName));
            return text?.Descendants()
                .FirstOrDefault(e => Eq(e.Name.LocalName, "Text"))
                ?.Value?.Trim() ?? "";
        }

        private static string SingleLine(string text)
        {
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static LadSchemaFacts Facts()
        {
            return LadSchemaRegistry.Resolve(TiaVersionAdapter.CurrentMajorVersion);
        }

        private static bool Eq(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        // ─────────────────────────────────────────────────────────────────────
        // IR metadata helpers (moved from TiaXmlAgentReferenceBuilder)
        // ─────────────────────────────────────────────────────────────────────

        private static BlockMeta ExtractBlockMeta(XElement root, string xmlContent)
        {
            var meta = new BlockMeta
            {
                Name = "AI_Generated_Block",
                BlockType = "FB",
                Language = "Unknown"
            };

            if (root == null)
                return meta;

            // Step 1: find the top-level block element (SW.Blocks.FC / FB / OB / DB, etc.).
            var blockElement = root.Elements()
                .FirstOrDefault(x =>
                {
                    var ln = x.Name.LocalName;
                    return ln.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase) ||
                           ln.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase);
                });

            if (blockElement != null)
            {
                var localName = blockElement.Name.LocalName;
                var blockType = localName.Replace("SW.Blocks.", "").Replace("SW.Types.", "");
                if (!string.IsNullOrWhiteSpace(blockType))
                    meta.BlockType = blockType;

                var attrList = blockElement.Element("AttributeList");
                if (attrList != null)
                {
                    var nameEl = attrList.Element("Name");
                    if (nameEl != null && !string.IsNullOrWhiteSpace(nameEl.Value))
                        meta.Name = nameEl.Value.Trim();

                    var numberEl = attrList.Element("Number");
                    if (numberEl != null && int.TryParse(numberEl.Value.Trim(), out var parsedNumber))
                        meta.Number = parsedNumber;

                    var langEl = attrList.Element("ProgrammingLanguage");
                    if (langEl != null && !string.IsNullOrWhiteSpace(langEl.Value))
                        meta.Language = langEl.Value.Trim();
                }

                meta.Title = ReadObjectListText(blockElement, "Title");
                meta.Comment = ReadObjectListText(blockElement, "Comment");
            }

            if (string.Equals(meta.Language, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                var upper = (xmlContent ?? "").ToUpperInvariant();
                if (upper.Contains("LAD")) meta.Language = "LAD";
                else if (upper.Contains("FBD")) meta.Language = "FBD";
                else if (upper.Contains("SCL")) meta.Language = "SCL";
            }

            return meta;
        }

        private static List<CanonicalMember> ExtractMembers(XElement root, params string[] sectionNames)
        {
            var expected = new HashSet<string>(
                sectionNames.Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);
            var result = new List<CanonicalMember>();
            if (root == null || expected.Count == 0)
                return result;

            var sections = root.Descendants()
                .Where(x => Eq(x.Name.LocalName, "Section"))
                .Where(x => expected.Contains((x.Attribute("Name")?.Value ?? "").Trim()));

            foreach (var section in sections)
            {
                foreach (var member in section.Elements().Where(x => Eq(x.Name.LocalName, "Member")))
                {
                    var name = (member.Attribute("Name")?.Value ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var datatype = (member.Attribute("Datatype")?.Value ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(datatype))
                    {
                        datatype = member.Elements()
                            .FirstOrDefault(x => Eq(x.Name.LocalName, "Datatype"))?.Value?.Trim() ?? "";
                    }

                    if (string.IsNullOrWhiteSpace(datatype))
                    {
                        datatype = member.Elements()
                            .FirstOrDefault(x => Eq(x.Name.LocalName, "Type"))
                            ?.Attribute("Name")?.Value?.Trim() ?? "";
                    }

                    result.Add(new CanonicalMember { Name = name, Datatype = datatype });
                }
            }

            return result;
        }

        private sealed class BlockMeta
        {
            public string Name { get; set; }
            public string BlockType { get; set; }
            public string Language { get; set; }
            public int Number { get; set; }
            public string Title { get; set; }
            public string Comment { get; set; }
        }

        private sealed class CanonicalMember
        {
            public string Name { get; set; }
            public string Datatype { get; set; }
        }
    }
}
