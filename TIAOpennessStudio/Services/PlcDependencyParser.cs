using System;
using System.Linq;
using System.Xml.Linq;
using TIAOpennessStudio.Models.Dependency;

namespace TIAOpennessStudio.Services
{
    public sealed class PlcDependencyParser
    {
        private readonly PlcKnowledgeCanonicalizer _canonicalizer;

        public PlcDependencyParser()
        {
            _canonicalizer = new PlcKnowledgeCanonicalizer();
        }

        public PlcBlockDependencyRecord Parse(string projectKey, string blockKey, string xmlContent,
            string fallbackName = null, string fallbackType = null,
            string fallbackLanguage = null, string versionHash = null, string exportPath = null)
        {
            var record = new PlcBlockDependencyRecord
            {
                ProjectKey = projectKey,
                BlockKey = blockKey,
                BlockName = fallbackName ?? "",
                BlockType = fallbackType ?? "",
                Language = fallbackLanguage ?? "",
                VersionHash = versionHash ?? "",
                ExportPath = exportPath ?? "",
                IndexedAtUtc = DateTime.UtcNow
            };

            if (string.IsNullOrWhiteSpace(xmlContent))
            {
                return record;
            }

            try
            {
                var document = XDocument.Parse(xmlContent, LoadOptions.PreserveWhitespace);

                // Extract block metadata from XML
                var blockElement = document.Descendants()
                    .FirstOrDefault(x =>
                        x.Name.LocalName.EndsWith("_SW_BLOCKS", StringComparison.OrdinalIgnoreCase) ||
                        x.Name.LocalName == "Document");
                var blockName = ExtractBlockName(document, fallbackName);
                var blockType = ExtractBlockType(document, fallbackType);
                var language = ExtractProgrammingLanguage(document, fallbackLanguage);

                record.BlockName = blockName;
                record.BlockType = blockType;
                record.Language = language;

                // Extract definitions, references, and calls
                ExtractDefinitions(record, document, blockName, blockType);
                ExtractReferencesAndCalls(record, document, blockName, blockType);

                // Deduplicate
                record.References = record.References
                    .GroupBy(x => string.Join("|", x.CanonicalPath, x.AccessKind, x.NetworkIndex, x.SourceRef))
                    .Select(g => g.First())
                    .ToList();

                record.Calls = record.Calls
                    .GroupBy(x => string.Join("|", x.CallerBlock, x.CalleeBlock, x.NetworkIndex))
                    .Select(g => g.First())
                    .ToList();

                record.Definitions = record.Definitions
                    .GroupBy(x => x.CanonicalPath)
                    .Select(g => g.First())
                    .ToList();
            }
            catch
            {
                // Return partial record on parse failure
            }

            return record;
        }

        private void ExtractDefinitions(PlcBlockDependencyRecord record, XDocument document,
            string blockName, string blockType)
        {
            var sectionNames = new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant" };
            foreach (var sectionName in sectionNames)
            {
                var sections = document.Descendants()
                    .Where(x => string.Equals(x.Name.LocalName, sectionName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var section in sections)
                {
                    var members = section.Elements()
                        .Where(x => string.Equals(x.Name.LocalName, "Member", StringComparison.OrdinalIgnoreCase));
                    foreach (var member in members)
                    {
                        CollectMemberDefinitions(record, member, blockName, blockType, sectionName, null);
                    }
                }
            }
        }

        private void CollectMemberDefinitions(PlcBlockDependencyRecord record, XElement member,
            string blockName, string blockType, string scope, string parentPath)
        {
            var memberName = _canonicalizer.CanonicalizeSymbolPath(member.Attribute("Name")?.Value ?? "");
            if (string.IsNullOrWhiteSpace(memberName))
            {
                return;
            }

            var localPath = string.IsNullOrWhiteSpace(parentPath)
                ? memberName
                : parentPath + "." + memberName;

            var canonicalPath = _canonicalizer.BuildScopedPath(blockName, scope, localPath);
            if (!_canonicalizer.LooksLikeSymbol(canonicalPath))
            {
                return;
            }

            var dataType = _canonicalizer.NormalizeWhitespace(
                member.Attribute("Datatype")?.Value ??
                member.Element("Datatype")?.Value ?? "");
            var definitionKind = DetermineDefinitionKind(scope, blockType);

            record.Definitions.Add(new PlcSymbolDefinition
            {
                CanonicalPath = canonicalPath,
                OwnerBlock = blockName,
                DefinitionKind = definitionKind,
                DataType = dataType
            });

            // Recurse into nested members (struct/UDT members)
            foreach (var child in member.Elements()
                .Where(x => string.Equals(x.Name.LocalName, "Member", StringComparison.OrdinalIgnoreCase)))
            {
                CollectMemberDefinitions(record, child, blockName, blockType, scope, localPath);
            }
        }

        private static string DetermineDefinitionKind(string scope, string blockType)
        {
            if (string.Equals(blockType, "GlobalDB", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(blockType, "DB", StringComparison.OrdinalIgnoreCase))
            {
                return "GlobalDbMember";
            }

            var normalized = (scope ?? "").ToLowerInvariant();
            switch (normalized)
            {
                case "input": return "In";
                case "output": return "Out";
                case "inout": return "InOut";
                case "static": return "FbStatic";
                case "temp": return "Temp";
                case "constant": return "Constant";
                default: return normalized;
            }
        }

        private void ExtractReferencesAndCalls(PlcBlockDependencyRecord record, XDocument document,
            string blockName, string blockType)
        {
            var compileUnits = document.Descendants()
                .Where(x => string.Equals(x.Name.LocalName, "SW.Blocks.CompileUnit", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.Name.LocalName, "CompileUnit", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var networkIndex = 0;
            foreach (var compileUnit in compileUnits)
            {
                // network_index = source order (CompileUnit appearance order), shared with
                // PlcNetworkFactExtractor. Increment for every CompileUnit (LAD or SCL) so the
                // index stays consistent across both extractors. This is NOT execution order.
                networkIndex++;
                var compileUnitId = compileUnit.Attribute("ID")?.Value ?? "";

                var flgNet = compileUnit.Descendants()
                    .FirstOrDefault(x => string.Equals(x.Name.LocalName, "FlgNet", StringComparison.OrdinalIgnoreCase));
                if (flgNet == null)
                {
                    // STL (StatementList) network: recover call edges + operand references from the
                    // statement tree. Without this, calls in STL scheduling blocks (e.g. OB1) are lost.
                    var statementList = compileUnit.Descendants()
                        .FirstOrDefault(x => string.Equals(x.Name.LocalName, "StatementList", StringComparison.OrdinalIgnoreCase));
                    if (statementList != null)
                    {
                        ExtractFromStl(record, statementList, blockName, networkIndex, compileUnitId);
                        continue;
                    }

                    // SCL (or other text) network: extract calls/references from StructuredText.
                    var structuredText = compileUnit.Descendants()
                        .FirstOrDefault(x => string.Equals(x.Name.LocalName, "StructuredText", StringComparison.OrdinalIgnoreCase));
                    if (structuredText != null)
                    {
                        ExtractFromScl(record, structuredText.Value, blockName, networkIndex, compileUnitId);
                    }
                    continue;
                }

                var model = TiaXmlParseHelper.ParseFlgNet(flgNet);

                // Extract symbol references from Access elements
                foreach (var entry in model.AccessByUid)
                {
                    var scope = model.ScopeByUid.TryGetValue(entry.Key, out var scopeValue) ? scopeValue : "";
                    if (string.Equals(scope, "TypedConstant", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var canonicalPath = BuildAccessCanonicalPath(blockName, scope, entry.Value);
                    if (!_canonicalizer.LooksLikeSymbol(canonicalPath))
                    {
                        continue;
                    }

                    var accessKind = DetermineAccessKind(scope);
                    var sourceRef = BuildSourceRef(compileUnitId, entry.Key, networkIndex);

                    record.References.Add(new PlcSymbolReference
                    {
                        CanonicalPath = canonicalPath,
                        AccessKind = accessKind,
                        BlockName = blockName,
                        NetworkIndex = networkIndex,
                        Scope = _canonicalizer.NormalizeScope(scope),
                        DataType = string.Empty,
                        SourceRef = sourceRef
                    });
                }

                // Extract call edges from CallInfo elements
                var callInfos = flgNet.Descendants()
                    .Where(x => string.Equals(x.Name.LocalName, "CallInfo", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var callInfo in callInfos)
                {
                    var calleeName = _canonicalizer.NormalizeWhitespace(callInfo.Attribute("Name")?.Value ?? "");
                    if (string.IsNullOrWhiteSpace(calleeName))
                    {
                        continue;
                    }

                    record.Calls.Add(new PlcCallEdge
                    {
                        CallerBlock = blockName,
                        CalleeBlock = calleeName,
                        CalleeBlockType = _canonicalizer.NormalizeWhitespace(callInfo.Attribute("BlockType")?.Value ?? ""),
                        InstancePath = TiaXmlParseHelper.NormalizeSymbol(
                            callInfo.Descendants().FirstOrDefault(x =>
                                string.Equals(x.Name.LocalName, "Component", StringComparison.OrdinalIgnoreCase))
                                ?.Attribute("Name")?.Value ?? ""),
                        NetworkIndex = networkIndex,
                        SourceRef = BuildSourceRef(compileUnitId,
                            callInfo.Parent?.Attribute("UId")?.Value ?? callInfo.Attribute("UId")?.Value,
                            networkIndex)
                    });
                }
            }
        }

        /// <summary>
        /// SCL branch: recover references and call edges from a StructuredText body via
        /// lexical scanning (see <see cref="SclFactExtractor"/>). Produces the same
        /// PlcSymbolReference / PlcCallEdge shapes as the LAD branch.
        /// </summary>
        private void ExtractFromScl(PlcBlockDependencyRecord record, string sourceText,
            string blockName, int networkIndex, string compileUnitId)
        {
            var extract = SclFactExtractor.Extract(sourceText, blockName);

            foreach (var acc in extract.Accesses)
            {
                record.References.Add(new PlcSymbolReference
                {
                    CanonicalPath = acc.CanonicalPath,
                    AccessKind = acc.AccessKind,
                    BlockName = blockName,
                    NetworkIndex = networkIndex,
                    Scope = _canonicalizer.NormalizeScope(acc.Scope),
                    DataType = string.Empty,
                    SourceRef = BuildSclSourceRef(compileUnitId, acc.Position, networkIndex)
                });
            }

            foreach (var call in extract.Calls)
            {
                record.Calls.Add(new PlcCallEdge
                {
                    CallerBlock = blockName,
                    CalleeBlock = call.CalleeName,
                    CalleeBlockType = string.Empty,
                    InstancePath = string.Empty,
                    NetworkIndex = networkIndex,
                    SourceRef = BuildSclSourceRef(compileUnitId, call.Position, networkIndex)
                });
            }
        }

        private static string BuildSclSourceRef(string compileUnitId, int position, int networkIndex)
        {
            return $"net:{networkIndex}|cu:{compileUnitId ?? "?"}|uid:scl:{position}";
        }

        /// <summary>
        /// STL branch: recover call edges (exact, via CallInfo) and operand references from a
        /// StatementList body. Produces the same PlcCallEdge / PlcSymbolReference shapes as the
        /// LAD branch. STL encodes no access direction, so references use a best-effort kind.
        /// </summary>
        private void ExtractFromStl(PlcBlockDependencyRecord record, XElement statementList,
            string blockName, int networkIndex, string compileUnitId)
        {
            var stl = TiaXmlParseHelper.ParseStatementList(statementList);

            foreach (var stmt in stl.Statements)
            {
                if (stmt.IsCall)
                {
                    var calleeName = _canonicalizer.NormalizeWhitespace(stmt.CalleeName);
                    if (string.IsNullOrWhiteSpace(calleeName)) continue;

                    record.Calls.Add(new PlcCallEdge
                    {
                        CallerBlock = blockName,
                        CalleeBlock = calleeName,
                        CalleeBlockType = _canonicalizer.NormalizeWhitespace(stmt.CalleeBlockType),
                        InstancePath = TiaXmlParseHelper.NormalizeSymbol(stmt.InstancePath ?? ""),
                        NetworkIndex = networkIndex,
                        SourceRef = BuildSourceRef(compileUnitId, stmt.Uid, networkIndex)
                    });
                    continue;
                }

                if (stmt.IsEmptyLine || string.IsNullOrWhiteSpace(stmt.OperandText)) continue;
                if (string.Equals(stmt.OperandScope, "TypedConstant", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(stmt.OperandScope, "LiteralConstant", StringComparison.OrdinalIgnoreCase))
                    continue;

                var canonicalPath = BuildAccessCanonicalPath(blockName, stmt.OperandScope, stmt.OperandText);
                if (!_canonicalizer.LooksLikeSymbol(canonicalPath)) continue;

                record.References.Add(new PlcSymbolReference
                {
                    CanonicalPath = canonicalPath,
                    AccessKind = DetermineAccessKind(stmt.OperandScope),
                    BlockName = blockName,
                    NetworkIndex = networkIndex,
                    Scope = _canonicalizer.NormalizeScope(stmt.OperandScope),
                    DataType = string.Empty,
                    SourceRef = BuildSourceRef(compileUnitId, stmt.Uid, networkIndex)
                });
            }
        }

        private string BuildAccessCanonicalPath(string blockName, string scope, string rawPath)
        {
            var canonical = _canonicalizer.CanonicalizeSymbolPath(rawPath);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return canonical;
            }

            if (string.Equals(scope, "LocalVariable", StringComparison.OrdinalIgnoreCase))
            {
                return _canonicalizer.BuildScopedPath(blockName, "Local", canonical);
            }

            return canonical;
        }

        private static string DetermineAccessKind(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                return "Read";
            }

            var lower = scope.ToLowerInvariant();
            if (lower.Contains("output") || lower.Contains("inout"))
            {
                return "Write";
            }

            return "Read";
        }

        private static string BuildSourceRef(string compileUnitId, string uid, int networkIndex)
        {
            return $"net:{networkIndex}|cu:{compileUnitId ?? "?"}|uid:{uid ?? "?"}";
        }

        // ---- XML metadata extraction helpers ----

        private static string ExtractBlockName(XDocument document, string fallback)
        {
            var el = document.Descendants()
                .FirstOrDefault(x => string.Equals(x.Name.LocalName, "Name", StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrWhiteSpace(el?.Value) ? el.Value.Trim() : (fallback ?? "");
        }

        private static string ExtractBlockType(XDocument document, string fallback)
        {
            var el = document.Descendants()
                .FirstOrDefault(x => string.Equals(x.Name.LocalName, "BlockType", StringComparison.OrdinalIgnoreCase));
            if (el != null && !string.IsNullOrWhiteSpace(el.Value))
            {
                return el.Value.Trim();
            }

            // Try to infer from child elements of root
            var root = document.Root;
            if (root != null)
            {
                foreach (var child in root.Elements())
                {
                    // Suffix match on the type element, not a substring search: "SW.Blocks.GlobalDB"
                    // contains "ob", which used to be misread as an OB before DB was ever tested.
                    var name = child.Name.LocalName;
                    var shortName = name.Substring(name.LastIndexOf('.') + 1);
                    if (shortName.EndsWith("DataBlock", StringComparison.OrdinalIgnoreCase)
                        || shortName.EndsWith("DB", StringComparison.OrdinalIgnoreCase)) return "DB";
                    if (shortName.EndsWith("OB", StringComparison.OrdinalIgnoreCase)) return "OB";
                    if (shortName.EndsWith("FB", StringComparison.OrdinalIgnoreCase)) return "FB";
                    if (shortName.EndsWith("FC", StringComparison.OrdinalIgnoreCase)) return "FC";
                }
            }

            return fallback ?? "";
        }

        private static string ExtractProgrammingLanguage(XDocument document, string fallback)
        {
            var el = document.Descendants()
                .FirstOrDefault(x => string.Equals(x.Name.LocalName, "ProgrammingLanguage", StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrWhiteSpace(el?.Value) ? el.Value.Trim() : (fallback ?? "");
        }
    }
}
