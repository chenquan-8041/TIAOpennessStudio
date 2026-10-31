using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TIAOpennessStudio.Models.Blocks;
using TIAOpennessStudio.Models.Lad;
using TIAOpennessStudio.Services.Implementations;
using TIAOpennessStudio.Services.Lad;
using TIAOpennessStudio.Services.Lad.Codecs;

namespace TIAOpennessStudio.Services.Blocks
{
    public enum BlockPatchScope
    {
        Network,
        Interface
    }

    /// <summary>
    /// Result of a patch operation. ErrorCode is stable (machine-readable); Message is for the
    /// model/user and always names the concrete mismatch (expected vs actual revision/profile).
    /// </summary>
    public sealed class BlockPatchResult
    {
        public bool Ok => string.IsNullOrEmpty(ErrorCode);
        public string ErrorCode { get; set; } = "";
        public string Message { get; set; } = "";
        public string PatchedXml { get; set; }
        public string TargetId { get; set; } = "";

        public static BlockPatchResult Fail(string code, string message)
        {
            return new BlockPatchResult { ErrorCode = code, Message = message };
        }
    }

    /// <summary>
    /// Patch-first write contract.
    ///
    /// A patch never rebuilds a block. It replaces exactly one subtree (one CompileUnit body or
    /// the Interface) inside the snapshot's raw XML, using the SOURCE document's namespace, so
    /// every other attribute, network and ObjectList entry (MemoryLayout, InstanceOfName,
    /// SetENOAutomatically, titles, comments) survives untouched.
    ///
    /// Preconditions:
    ///   - base_revision must equal the snapshot revision (stale edits are rejected, never merged).
    ///   - The export profile must match the currently attached TIA version (cross-version patch
    ///     is refused; rebuilding for another version belongs to the build_* tools).
    /// </summary>
    public static class BlockPatchService
    {
        private static readonly string[] BodyElementNames = { "FlgNet", "FbdNet", "StructuredText", "StatementList" };

        // ─────────────────────────────────────────────────────────────────────
        // Network body
        // ─────────────────────────────────────────────────────────────────────

        public static BlockPatchResult PatchNetwork(
            BlockSnapshot snapshot,
            string baseRevision,
            string compileUnitId,
            int networkOrder,
            string bodyJson)
        {
            var precondition = CheckPreconditions(snapshot, baseRevision);
            if (precondition != null) return precondition;

            if (snapshot.IsGraph || string.Equals(snapshot.Language, "GRAPH", StringComparison.OrdinalIgnoreCase))
                return BlockPatchResult.Fail("graph_unsupported",
                    "GRAPH blocks carry a sequence contract, not network bodies; use build_graph_fb to write them.");

            JsonObject bodyObject;
            try
            {
                bodyObject = ParseBodyObject(bodyJson);
            }
            catch (Exception ex)
            {
                return BlockPatchResult.Fail("invalid_body", "Body JSON is not a canonical node tree: " + ex.Message);
            }
            if (bodyObject == null)
                return BlockPatchResult.Fail("invalid_body", "Body JSON must be a canonical node object.");

            var bodyNode = LadJson.NodeFromJson(bodyObject);
            var language = InferLanguage(bodyNode);
            if (language == null)
            {
                return BlockPatchResult.Fail("unsupported_body",
                    "Body root '" + (bodyNode.Kind ?? "") + "' is not one of FlgNet/FbdNet/StructuredText/StatementList.");
            }

            XDocument document;
            XElement block;
            try
            {
                document = XDocument.Parse(snapshot.RawXml, LoadOptions.PreserveWhitespace);
                block = FindBlockElement(document.Root);
            }
            catch (Exception ex)
            {
                return BlockPatchResult.Fail("xml_parse_failed", "Snapshot XML cannot be parsed: " + ex.Message);
            }
            if (block == null)
                return BlockPatchResult.Fail("xml_parse_failed", "Snapshot has no SW.Blocks.* / SW.Types.* block element.");

            var units = block.Descendants().Where(IsCompileUnit).ToList();
            var unit = FindCompileUnit(units, compileUnitId, networkOrder);
            if (unit == null)
            {
                return BlockPatchResult.Fail("network_not_found",
                    "No compile unit matched compile_unit_id='" + (compileUnitId ?? "")
                    + "' network_order=" + networkOrder + " (block has " + units.Count + " compile units).");
            }

            var unitId = unit.Attribute("ID")?.Value ?? "";
            var attrList = unit.Elements().FirstOrDefault(e => Eq(e.Name.LocalName, "AttributeList"));
            var networkSource = attrList?.Elements().FirstOrDefault(e => Eq(e.Name.LocalName, "NetworkSource"));
            if (networkSource == null)
            {
                return BlockPatchResult.Fail("network_not_found",
                    "Compile unit '" + unitId + "' has no NetworkSource to patch (empty compile unit).");
            }

            var sourceFacts = ResolveSourceFacts(snapshot.SourceProfile);
            var ns = ResolveBodyNamespace(networkSource, language, sourceFacts);
            if (ns == null)
            {
                return BlockPatchResult.Fail("namespace_unconfirmed",
                    "Namespace for " + language + " is not confirmed on " + sourceFacts.DisplayVersion
                    + "; refusing to patch XML guesswork.");
            }

            XElement emitted;
            try
            {
                emitted = NetworkBodyCodec.ToXmlElement(
                    ns, bodyNode,
                    materializeSystemDefined: NetworkBodyCodec.ShouldMaterializeSystemDefined(sourceFacts, sourceFacts),
                    sourceSystemDefinedDefault: sourceFacts.SystemDefinedDefault);
            }
            catch (Exception ex)
            {
                return BlockPatchResult.Fail("invalid_body", "Body cannot be emitted as XML: " + ex.Message);
            }

            foreach (var existing in networkSource.Elements().Where(e => IsBodyElement(e)).ToList())
                existing.Remove();
            networkSource.Add(emitted);

            var languageElement = attrList.Elements().FirstOrDefault(e => Eq(e.Name.LocalName, "ProgrammingLanguage"));
            if (languageElement != null) languageElement.Value = language;

            var patchedXml = Serialize(document);
            string validationError;
            if (!ValidatePatch(snapshot, patchedXml, BlockPatchScope.Network, unitId, out validationError))
                return BlockPatchResult.Fail("invalid_result", validationError);

            return new BlockPatchResult { PatchedXml = patchedXml, TargetId = unitId };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Interface
        // ─────────────────────────────────────────────────────────────────────

        public static BlockPatchResult PatchInterface(
            BlockSnapshot snapshot,
            string baseRevision,
            string interfaceJson)
        {
            var precondition = CheckPreconditions(snapshot, baseRevision);
            if (precondition != null) return precondition;

            JsonObject interfaceObject;
            try
            {
                interfaceObject = ParseInterfaceObject(interfaceJson);
            }
            catch (Exception ex)
            {
                return BlockPatchResult.Fail("invalid_interface", "Interface JSON is not a canonical node tree: " + ex.Message);
            }
            if (interfaceObject == null)
                return BlockPatchResult.Fail("invalid_interface", "Interface JSON must be a canonical Interface object.");

            var interfaceNode = LadJson.NodeFromJson(interfaceObject);
            if (!string.Equals(interfaceNode.Kind, "Interface", StringComparison.OrdinalIgnoreCase))
                return BlockPatchResult.Fail("invalid_interface",
                    "Interface root must be kind 'Interface' (got '" + (interfaceNode.Kind ?? "") + "').");
            if (interfaceNode.Child("Sections") == null
                && !string.Equals(interfaceNode.Kind, "Sections", StringComparison.OrdinalIgnoreCase))
            {
                return BlockPatchResult.Fail("invalid_interface", "Interface tree is missing 'Sections'.");
            }

            InheritSectionAttributes(interfaceNode);

            XDocument document;
            XElement block;
            try
            {
                document = XDocument.Parse(snapshot.RawXml, LoadOptions.PreserveWhitespace);
                block = FindBlockElement(document.Root);
            }
            catch (Exception ex)
            {
                return BlockPatchResult.Fail("xml_parse_failed", "Snapshot XML cannot be parsed: " + ex.Message);
            }
            if (block == null)
                return BlockPatchResult.Fail("xml_parse_failed", "Snapshot has no SW.Blocks.* / SW.Types.* block element.");

            var attrList = block.Elements().FirstOrDefault(e => Eq(e.Name.LocalName, "AttributeList"));
            var existingInterface = attrList?.Elements().FirstOrDefault(e => Eq(e.Name.LocalName, "Interface"));
            if (existingInterface == null)
                return BlockPatchResult.Fail("interface_not_found", "Block has no Interface element to patch.");

            var sourceFacts = ResolveSourceFacts(snapshot.SourceProfile);
            XElement emitted;
            try
            {
                emitted = NetworkBodyCodec.EmitInterface(interfaceNode, sourceFacts, sourceFacts);
            }
            catch (Exception ex)
            {
                return BlockPatchResult.Fail("namespace_unconfirmed", "Interface cannot be emitted: " + ex.Message);
            }
            if (emitted == null)
                return BlockPatchResult.Fail("invalid_interface", "Interface projection produced no XML.");

            existingInterface.ReplaceWith(emitted);

            var patchedXml = Serialize(document);
            string validationError;
            if (!ValidatePatch(snapshot, patchedXml, BlockPatchScope.Interface, "", out validationError))
                return BlockPatchResult.Fail("invalid_result", validationError);

            return new BlockPatchResult { PatchedXml = patchedXml, TargetId = snapshot.Name };
        }

        /// <summary>
        /// A DB static member must declare the same Remanence as its siblings: V15.1 refuses an
        /// imported member without it ("cannot set property Remanence"). A member added through a
        /// patch therefore inherits the section's declared value. Sections that never declare it
        /// (Temp/InOut, UDT structs) are left untouched, and mixed values are left alone so the
        /// caller has to spell out what it wants.
        /// </summary>
        private static void InheritSectionAttributes(LadNode interfaceNode)
        {
            var sectionsNode = string.Equals(interfaceNode.Kind, "Sections", StringComparison.OrdinalIgnoreCase)
                ? interfaceNode
                : interfaceNode.Child("Sections");
            if (sectionsNode == null) return;

            foreach (var section in sectionsNode.ChildrenOf("Section"))
            {
                var members = section.ChildrenOf("Member").ToList();
                if (members.Count == 0) continue;

                var declared = members
                    .Select(m => m.Attr("Remanence"))
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (declared.Count != 1) continue;

                var value = declared[0];
                foreach (var member in members)
                    if (member.Attr("Remanence") == null)
                        member.SetAttr("Remanence", value);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Validation: only the targeted subtree may change
        // ─────────────────────────────────────────────────────────────────────
        public static bool ValidatePatch(
            BlockSnapshot snapshot,
            string patchedXml,
            BlockPatchScope scope,
            string targetId,
            out string error)
        {
            error = null;
            if (snapshot == null)
            {
                error = "Snapshot is missing.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(patchedXml))
            {
                error = "Patched XML is empty.";
                return false;
            }

            XDocument originalDoc;
            XDocument patchedDoc;
            try
            {
                originalDoc = XDocument.Parse(snapshot.RawXml, LoadOptions.PreserveWhitespace);
                patchedDoc = XDocument.Parse(patchedXml, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                error = "Patched XML cannot be parsed: " + ex.Message;
                return false;
            }

            var originalBlock = FindBlockElement(originalDoc.Root);
            var patchedBlock = FindBlockElement(patchedDoc.Root);
            if (originalBlock == null || patchedBlock == null)
            {
                error = "Block element is missing after the patch.";
                return false;
            }
            if (!string.Equals(originalBlock.Name.LocalName, patchedBlock.Name.LocalName, StringComparison.OrdinalIgnoreCase))
            {
                error = "Block type changed: " + originalBlock.Name.LocalName + " -> " + patchedBlock.Name.LocalName + ".";
                return false;
            }

            var scalarError = CompareScalarAttributes(originalBlock, patchedBlock, scope);
            if (scalarError != null)
            {
                error = scalarError;
                return false;
            }

            var originalUnits = originalBlock.Descendants().Where(IsCompileUnit).ToList();
            var patchedUnits = patchedBlock.Descendants().Where(IsCompileUnit).ToList();
            if (originalUnits.Count != patchedUnits.Count)
            {
                error = "Compile unit count changed: " + originalUnits.Count + " -> " + patchedUnits.Count + ".";
                return false;
            }

            for (var i = 0; i < originalUnits.Count; i++)
            {
                var originalUnit = originalUnits[i];
                var patchedUnit = patchedUnits[i];
                var originalId = originalUnit.Attribute("ID")?.Value ?? "";
                var patchedId = patchedUnit.Attribute("ID")?.Value ?? "";
                if (!string.Equals(originalId, patchedId, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Compile unit order/ID changed at position " + (i + 1) + ": "
                            + originalId + " -> " + patchedId + ".";
                    return false;
                }

                var isTarget = scope == BlockPatchScope.Network
                               && string.Equals(originalId, targetId ?? "", StringComparison.OrdinalIgnoreCase);

                if (isTarget)
                {
                    // The target network body may change; its identity/attributes/ObjectList must not.
                    var originalShell = UnitShell(originalUnit);
                    var patchedShell = UnitShell(patchedUnit);
                    if (!string.Equals(originalShell, patchedShell, StringComparison.Ordinal))
                    {
                        error = "Target compile unit '" + originalId + "' changed outside NetworkSource.";
                        return false;
                    }
                }
                else if (!string.Equals(SerializeElement(originalUnit), SerializeElement(patchedUnit), StringComparison.Ordinal))
                {
                    error = "Compile unit '" + originalId + "' changed but was not the patch target.";
                    return false;
                }
            }

            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Preconditions / helpers
        // ─────────────────────────────────────────────────────────────────────

        private static BlockPatchResult CheckPreconditions(BlockSnapshot snapshot, string baseRevision)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.RawXml))
                return BlockPatchResult.Fail("invalid_snapshot", "Block snapshot is missing.");

            var expected = (baseRevision ?? "").Trim();
            if (string.IsNullOrEmpty(expected))
            {
                return BlockPatchResult.Fail("revision_required",
                    "base_revision is required: read the block first and pass its revision (" + snapshot.Revision + ").");
            }
            if (!string.Equals(expected, snapshot.Revision, StringComparison.OrdinalIgnoreCase))
            {
                return BlockPatchResult.Fail("revision_mismatch",
                    "The block changed after it was read (base_revision=" + expected
                    + ", current=" + snapshot.Revision + "). Read it again before patching.");
            }

            var currentProfile = NormalizeProfile(LadSchemaRegistry.Resolve(TiaVersionAdapter.CurrentMajorVersion).DisplayVersion);
            var sourceProfile = NormalizeProfile(snapshot.SourceProfile);
            if (!string.IsNullOrEmpty(sourceProfile) && !string.IsNullOrEmpty(currentProfile)
                && !string.Equals(sourceProfile, currentProfile, StringComparison.OrdinalIgnoreCase))
            {
                return BlockPatchResult.Fail("version_mismatch",
                    "The block was exported from " + snapshot.SourceProfile + " but the attached TIA is "
                    + currentProfile + ". Cross-version edits must be rebuilt, not patched.");
            }

            return null;
        }

        private static JsonObject ParseBodyObject(string bodyJson)
        {
            var node = JsonNode.Parse(bodyJson);
            var obj = node as JsonObject;
            if (obj == null) return null;
            // Accept either the node itself or a full network entry ({ language, body }).
            if (obj["body"] is JsonObject inner) return inner;
            return obj;
        }

        private static JsonObject ParseInterfaceObject(string interfaceJson)
        {
            var node = JsonNode.Parse(interfaceJson);
            var obj = node as JsonObject;
            if (obj == null) return null;
            // Accept either the Interface node itself or a full block IR ({ interface }).
            if (obj["interface"] is JsonObject inner) return inner;
            return obj;
        }

        private static XElement FindCompileUnit(List<XElement> units, string compileUnitId, int networkOrder)
        {
            if (!string.IsNullOrWhiteSpace(compileUnitId))
            {
                return units.FirstOrDefault(u =>
                    string.Equals(u.Attribute("ID")?.Value ?? "", compileUnitId.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (networkOrder > 0 && networkOrder <= units.Count)
                return units[networkOrder - 1];
            return null;
        }

        private static string InferLanguage(LadNode body)
        {
            var kind = (body?.Kind ?? "").Trim();
            if (string.Equals(kind, "StructuredText", StringComparison.OrdinalIgnoreCase)) return "SCL";
            if (string.Equals(kind, "StatementList", StringComparison.OrdinalIgnoreCase)) return "STL";
            if (string.Equals(kind, "FbdNet", StringComparison.OrdinalIgnoreCase)) return "FBD";
            if (string.Equals(kind, "FlgNet", StringComparison.OrdinalIgnoreCase)) return "LAD";
            return null;
        }

        private static XNamespace ResolveBodyNamespace(XElement networkSource, string language, LadSchemaFacts facts)
        {
            // Prefer the namespace already present in this export: patch keeps the source version.
            var existingBody = networkSource.Elements().FirstOrDefault(IsBodyElement);
            if (existingBody != null && !string.IsNullOrEmpty(existingBody.Name.NamespaceName))
                return existingBody.Name.Namespace;

            var uri = NetworkBodyCodec.NamespaceFor(facts, language);
            return string.IsNullOrEmpty(uri) ? null : XNamespace.Get(uri);
        }

        private static LadSchemaFacts ResolveSourceFacts(string sourceProfile)
        {
            var text = (sourceProfile ?? "").Trim();
            if (text.StartsWith("V", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            var majorText = text.Split('.')[0];
            var major = 0;
            int.TryParse(majorText, out major);
            return LadSchemaRegistry.Resolve(major);
        }

        private static string NormalizeProfile(string value)
        {
            var text = (value ?? "").Trim();
            if (string.IsNullOrEmpty(text)) return "";
            var space = text.IndexOf(' ');
            if (space > 0) text = text.Substring(0, space);
            if (!text.StartsWith("V", StringComparison.OrdinalIgnoreCase)) text = "V" + text;
            return text.ToUpperInvariant();
        }

        private static string CompareScalarAttributes(XElement originalBlock, XElement patchedBlock, BlockPatchScope scope)
        {
            var original = ReadScalarAttributes(originalBlock, scope);
            var patched = ReadScalarAttributes(patchedBlock, scope);
            foreach (var pair in original)
            {
                var patchedValue = patched.TryGetValue(pair.Key, out var value) ? value : null;
                if (patchedValue == null)
                    return "Block attribute '" + pair.Key + "' disappeared after the patch.";
                if (!string.Equals(pair.Value, patchedValue, StringComparison.Ordinal))
                    return "Block attribute '" + pair.Key + "' changed: '" + pair.Value + "' -> '" + patchedValue + "'.";
            }
            foreach (var pair in patched)
            {
                if (!original.ContainsKey(pair.Key))
                    return "Unexpected block attribute '" + pair.Key + "' appeared after the patch.";
            }
            return null;
        }

        private static Dictionary<string, string> ReadScalarAttributes(XElement block, BlockPatchScope scope)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var attrList = block?.Elements().FirstOrDefault(e => Eq(e.Name.LocalName, "AttributeList"));
            if (attrList == null) return result;

            foreach (var element in attrList.Elements())
            {
                if (scope == BlockPatchScope.Interface && Eq(element.Name.LocalName, "Interface")) continue;
                if (element.HasElements) continue; // complex nodes are validated by identity/structure below
                result[element.Name.LocalName] = (element.Value ?? "").Trim();
            }
            return result;
        }

        private static string UnitShell(XElement unit)
        {
            var clone = new XElement(unit);
            foreach (var attrList in clone.Elements().Where(e => Eq(e.Name.LocalName, "AttributeList")))
            {
                foreach (var networkSource in attrList.Elements().Where(e => Eq(e.Name.LocalName, "NetworkSource")).ToList())
                {
                    foreach (var body in networkSource.Elements().Where(IsBodyElement).ToList())
                        body.Remove();
                }
            }
            return SerializeElement(clone);
        }

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
                   || local.EndsWith("CompileUnit", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBodyElement(XElement element)
        {
            var local = element?.Name.LocalName ?? "";
            return BodyElementNames.Any(name => string.Equals(local, name, StringComparison.OrdinalIgnoreCase))
                   || local.EndsWith("Net", StringComparison.OrdinalIgnoreCase);
        }

        private static string Serialize(XDocument document)
        {
            var text = document.ToString(SaveOptions.DisableFormatting);
            // XDocument.ToString drops the XML declaration; keep the export header so the
            // patched document is byte-compatible with a normal Openness export.
            if (document.Declaration != null && !text.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
                text = document.Declaration + "\n" + text;
            return text;
        }

        private static string SerializeElement(XElement element)
        {
            return element?.ToString(SaveOptions.DisableFormatting) ?? "";
        }

        private static bool Eq(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
