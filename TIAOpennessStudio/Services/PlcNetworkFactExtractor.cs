using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TIAOpennessStudio.LadRendering.Parser;
using TIAOpennessStudio.Models.Lad;
using TIAOpennessStudio.Models.NetworkKnowledge;
using TIAOpennessStudio.Services.Builders;
using TIAOpennessStudio.Services.Lad;
using TIAOpennessStudio.Services.Lad.Codecs;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Extracts Network-level deterministic facts from LAD block XML.
    /// Produces one <see cref="PlcNetworkDocument"/> per CompileUnit/FlgNet.
    /// Uses Wire/Pin analysis (not Scope guessing) to resolve Access direction.
    /// </summary>
    public sealed class PlcNetworkFactExtractor
    {
        public const string CurrentParserVersion = "2.0.0";

        private readonly PlcKnowledgeCanonicalizer _canonicalizer = new PlcKnowledgeCanonicalizer();

        // Known output pins on standard LAD instructions (coils, assignments, etc.)
        private static readonly HashSet<string> KnownOutputPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "out", "out1", "out2", "q", "q1", "operand", "ret_val"
        };

        // Known input pins
        private static readonly HashSet<string> KnownInputPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "in", "in1", "in2", "en", "operand1", "operand2", "s", "r", "clk", "tv", "pv", "cu", "cd"
        };

        // Parts whose operand connections are writes (coils, set, reset, move-out, etc.)
        private static readonly HashSet<string> WriteInstructionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Coil", "SCoil", "RCoil", "SetCoil", "ResetCoil", "Set_BF", "Reset_BF",
            "Move", "BLKMOV", "FILL", "Serialize", "Deserialize"
        };

        /// <summary>
        /// Extract all Networks from a block's exported XML.
        /// </summary>
        public List<PlcNetworkDocument> ExtractNetworks(
            string projectKey, string blockId, string blockName, string xmlContent, string blockLanguage = null)
        {
            var language = string.IsNullOrWhiteSpace(blockLanguage) ? "LAD" : blockLanguage.Trim().ToUpperInvariant();
            var results = new List<PlcNetworkDocument>();
            if (string.IsNullOrWhiteSpace(xmlContent))
                return results;

            XDocument document;
            try
            {
                document = XDocument.Parse(xmlContent, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                results.Add(CreateFailedNetwork(projectKey, blockId, 1, "", ex.Message));
                return results;
            }

            var compileUnits = document.Descendants()
                .Where(x => string.Equals(x.Name.LocalName, "SW.Blocks.CompileUnit", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(x.Name.LocalName, "CompileUnit", StringComparison.OrdinalIgnoreCase))
                .ToList();

            int networkOrder = 0;
            foreach (var cu in compileUnits)
            {
                networkOrder++;
                var compileUnitId = cu.Attribute("ID")?.Value ?? "";

                // S7-GRAPH: the compile unit carries <Graph><Sequence>. Its nested <FlgNet> elements are
                // transition conditions, not LAD networks, so GRAPH must be indexed as its own language
                // (never folded into the ladder table's first-FlgNet heuristic).
                var graphElement = cu.Descendants()
                    .FirstOrDefault(x => string.Equals(x.Name.LocalName, "Graph", StringComparison.OrdinalIgnoreCase));
                if (graphElement != null)
                {
                    results.Add(BuildGraphNetwork(projectKey, blockId, cu, compileUnitId, networkOrder));
                    continue;
                }

                var flgNet = cu.Descendants()
                    .FirstOrDefault(x => string.Equals(x.Name.LocalName, "FlgNet", StringComparison.OrdinalIgnoreCase));

                if (flgNet == null)
                {
                    // STL (StatementList) network: extract call edges (and best-effort operand
                    // accesses) from the statement tree. Critical for scheduling blocks like OB1
                    // whose FC calls live entirely in STL networks.
                    var statementList = cu.Descendants()
                        .FirstOrDefault(x => string.Equals(x.Name.LocalName, "StatementList", StringComparison.OrdinalIgnoreCase));
                    if (statementList != null)
                    {
                        var stlDoc = BuildStlNetwork(projectKey, blockId, blockName, cu, compileUnitId, networkOrder, statementList);
                        AttachCanonical(stlDoc, statementList, blockId, compileUnitId, networkOrder);
                        results.Add(stlDoc);
                        continue;
                    }

                    // SCL (StructuredText) network: extract facts from source text.
                    var structuredText = cu.Descendants()
                        .FirstOrDefault(x => string.Equals(x.Name.LocalName, "StructuredText", StringComparison.OrdinalIgnoreCase));
                    if (structuredText != null)
                    {
                        // Rebuild real SCL from the token tree (same reconstruction the block
                        // viewer uses). structuredText.Value would only yield comments and drop
                        // all code, so pass the element and reconstruct.
                        var sclText = TiaXmlParseHelper.RebuildStructuredText(structuredText);
                        var sclDoc = BuildSclNetwork(projectKey, blockId, blockName, cu, compileUnitId, networkOrder, sclText);
                        AttachCanonical(sclDoc, structuredText, blockId, compileUnitId, networkOrder);
                        results.Add(sclDoc);
                    }
                    // else: empty/unsupported compile unit — skip without failure.
                    continue;
                }

                var networkDoc = new PlcNetworkDocument
                {
                    NetworkId = GenerateNetworkId(projectKey, blockId, compileUnitId, networkOrder),
                    BlockId = blockId,
                    ProjectKey = projectKey,
                    NetworkOrder = networkOrder,
                    CompileUnitId = compileUnitId,
                    Title = ExtractNetworkTitle(cu),
                    Comment = ExtractNetworkComment(cu),
                    Language = language,
                    ContentHash = ComputeContentHash(flgNet.ToString()),
                    ParserVersion = CurrentParserVersion,
                    IndexedAtUtc = DateTime.UtcNow
                };

                try
                {
                    var facts = LadSchemaRegistry.Resolve(
                        TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentMajorVersion);
                    var codec = NetworkCodecRegistry.ForXml(flgNet, facts);
                    var canonical = codec.Parse(flgNet, new LadParseContext
                    {
                        NetworkId = networkDoc.NetworkId,
                        BlockId = blockId,
                        Order = networkOrder,
                        CompileUnitId = compileUnitId,
                        Title = networkDoc.Title,
                        Comment = networkDoc.Comment,
                        Language = language
                    });

                    networkDoc.CanonicalJson = LadJson.SerializeNetwork(canonical);

                    // Facts are derived from the canonical body emitted back to XML, so any loss in
                    // the canonical round trip surfaces here instead of hiding in a second parser.
                    var emitted = codec.Emit(canonical, LadEmitContext.For(facts));
                    ExtractFacts(networkDoc, emitted, blockName);
                    networkDoc.Status = ParseStatus.Succeeded;
                }
                catch (Exception ex)
                {
                    networkDoc.Status = ParseStatus.Failed;
                    networkDoc.FailureReason = ex.Message;
                }

                results.Add(networkDoc);
            }

            return results;
        }

        /// <summary>
        /// Build the GRAPH network document: one compile unit holds the whole step sequence.
        /// The canonical JSON is the same shape build_graph_fb consumes (GraphJsonBridge), and
        /// condition/action operands are projected into Accesses so GRAPH stays searchable by tag.
        /// </summary>
        private PlcNetworkDocument BuildGraphNetwork(
            string projectKey, string blockId,
            XElement compileUnit, string compileUnitId, int networkOrder)
        {
            var xml = compileUnit.ToString(SaveOptions.DisableFormatting);
            var doc = new PlcNetworkDocument
            {
                NetworkId = GenerateNetworkId(projectKey, blockId, compileUnitId, networkOrder),
                BlockId = blockId,
                ProjectKey = projectKey,
                NetworkOrder = networkOrder,
                CompileUnitId = compileUnitId,
                Title = ExtractNetworkTitle(compileUnit),
                Comment = ExtractNetworkComment(compileUnit),
                Language = "GRAPH",
                ContentHash = ComputeContentHash(xml),
                ParserVersion = CurrentParserVersion,
                IndexedAtUtc = DateTime.UtcNow
            };

            try
            {
                var sequence = GraphXmlParser.Parse(xml);
                var profile = LadSchemaRegistry.Resolve(
                    TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentMajorVersion);

                var canonical = new JsonObject
                {
                    ["networkId"] = doc.NetworkId,
                    ["blockId"] = blockId,
                    ["order"] = networkOrder,
                    ["compileUnitId"] = compileUnitId,
                    ["title"] = doc.Title,
                    ["comment"] = doc.Comment,
                    ["language"] = "GRAPH",
                    ["sourceProfile"] = profile.DisplayVersion,
                    ["sequence"] = GraphJsonBridge.SequenceToJson(sequence)
                };
                doc.CanonicalJson = canonical.ToJsonString();

                // Read side of a transition/supervision/interlock; write side of an action.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var transition in sequence.Transitions)
                    AddGraphAccesses(doc, GraphJsonBridge.ConditionVariables(transition.FlgNetXml), AccessDirection.Read, seen);
                foreach (var step in sequence.Steps)
                {
                    foreach (var supervision in step.Supervisions)
                        AddGraphAccesses(doc, GraphJsonBridge.ConditionVariables(supervision.FlgNetXml), AccessDirection.Read, seen);
                    foreach (var interlock in step.Interlocks)
                        AddGraphAccesses(doc, GraphJsonBridge.ConditionVariables(interlock.FlgNetXml), AccessDirection.Read, seen);
                    foreach (var action in step.Actions)
                        AddGraphAccesses(doc, new[] { action.Operand }, AccessDirection.Write, seen);
                }

                doc.Status = ParseStatus.Succeeded;
            }
            catch (Exception ex)
            {
                doc.Status = ParseStatus.Failed;
                doc.FailureReason = "graph: " + ex.Message;
            }

            return doc;
        }

        private static void AddGraphAccesses(
            PlcNetworkDocument doc, IEnumerable<string> names, AccessDirection direction, HashSet<string> seen)
        {
            foreach (var raw in names ?? Enumerable.Empty<string>())
            {
                var name = (raw ?? "").Trim().Trim('"');
                if (name.Length == 0 || !seen.Add(name)) continue;
                doc.Accesses.Add(new AccessNode
                {
                    Uid = "",
                    CanonicalPath = name,
                    Scope = "GlobalVariable",
                    Direction = direction,
                    DirectionEvidence = direction == AccessDirection.Read ? "graph condition" : "graph action",
                    SourceRef = $"net:{doc.NetworkOrder}|cu:{doc.CompileUnitId}"
                });
            }
        }

        /// <summary>
        /// Build an SCL network document from a StructuredText body. Stores the raw
        /// source text and fills Accesses/CallSites via lexical extraction. Direction
        /// is best-effort (assignment LHS = Write, else Read, ambiguous = Unresolved).
        /// </summary>
        private PlcNetworkDocument BuildSclNetwork(string projectKey, string blockId, string blockName,
            XElement cu, string compileUnitId, int networkOrder, string sourceText)
        {
            var doc = new PlcNetworkDocument
            {
                NetworkId = GenerateNetworkId(projectKey, blockId, compileUnitId, networkOrder),
                BlockId = blockId,
                ProjectKey = projectKey,
                NetworkOrder = networkOrder,
                CompileUnitId = compileUnitId,
                Title = ExtractNetworkTitle(cu),
                Comment = ExtractNetworkComment(cu),
                Language = "SCL",
                SourceText = sourceText ?? "",
                ContentHash = ComputeContentHash(sourceText ?? ""),
                ParserVersion = CurrentParserVersion,
                IndexedAtUtc = DateTime.UtcNow
            };

            try
            {
                var extract = SclFactExtractor.Extract(sourceText, blockName);

                foreach (var acc in extract.Accesses)
                {
                    doc.Accesses.Add(new AccessNode
                    {
                        Uid = "scl:" + acc.Position,
                        CanonicalPath = acc.CanonicalPath,
                        Scope = acc.Scope,
                        Direction = acc.AccessKind == "Write" ? AccessDirection.Write
                                  : acc.AccessKind == "Read" ? AccessDirection.Read
                                  : AccessDirection.Unresolved,
                        DirectionEvidence = "scl:text",
                        SourceRef = $"net:{networkOrder}|cu:{compileUnitId}|uid:scl:{acc.Position}"
                    });
                }

                foreach (var call in extract.Calls)
                {
                    doc.CallSites.Add(new CallSiteNode
                    {
                        Uid = "scl:" + call.Position,
                        RawCalleeName = call.CalleeName,
                        RawBlockType = "",
                        RawInstancePath = call.InstancePath,
                        SourceRef = $"net:{networkOrder}|cu:{compileUnitId}|uid:scl:{call.Position}"
                    });
                }

                doc.Status = ParseStatus.Succeeded;
            }
            catch (Exception ex)
            {
                doc.Status = ParseStatus.Failed;
                doc.FailureReason = ex.Message;
            }

            return doc;
        }

        /// <summary>
        /// Build an STL network document from a StatementList body. Reconstructs the readable STL
        /// source, and extracts call edges (exact, via CallInfo) plus operand accesses. STL has no
        /// Wire/Pin graph, so access direction stays Unresolved.
        /// </summary>
        private PlcNetworkDocument BuildStlNetwork(string projectKey, string blockId, string blockName,
            XElement cu, string compileUnitId, int networkOrder, XElement statementList)
        {
            var stl = TiaXmlParseHelper.ParseStatementList(statementList);
            var sourceText = TiaXmlParseHelper.RebuildStatementList(statementList);

            var doc = new PlcNetworkDocument
            {
                NetworkId = GenerateNetworkId(projectKey, blockId, compileUnitId, networkOrder),
                BlockId = blockId,
                ProjectKey = projectKey,
                NetworkOrder = networkOrder,
                CompileUnitId = compileUnitId,
                Title = ExtractNetworkTitle(cu),
                Comment = ExtractNetworkComment(cu),
                Language = "STL",
                SourceText = sourceText ?? "",
                ContentHash = ComputeContentHash(statementList.ToString()),
                ParserVersion = CurrentParserVersion,
                IndexedAtUtc = DateTime.UtcNow
            };

            try
            {
                foreach (var stmt in stl.Statements)
                {
                    if (stmt.IsCall)
                    {
                        doc.CallSites.Add(new CallSiteNode
                        {
                            Uid = stmt.Uid,
                            RawCalleeName = stmt.CalleeName,
                            RawBlockType = stmt.CalleeBlockType,
                            RawInstancePath = stmt.InstancePath,
                            SourceRef = $"net:{networkOrder}|cu:{compileUnitId}|uid:{stmt.Uid}"
                        });
                        continue;
                    }

                    if (stmt.IsEmptyLine || string.IsNullOrWhiteSpace(stmt.OperandText))
                        continue;
                    if (string.Equals(stmt.OperandScope, "TypedConstant", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(stmt.OperandScope, "LiteralConstant", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var canonicalPath = BuildCanonicalPath(blockName, stmt.OperandScope, stmt.OperandText);
                    if (string.IsNullOrWhiteSpace(canonicalPath))
                        continue;

                    doc.Accesses.Add(new AccessNode
                    {
                        Uid = stmt.Uid,
                        CanonicalPath = canonicalPath,
                        Scope = stmt.OperandScope,
                        Direction = AccessDirection.Unresolved, // STL has no Wire/Pin graph to resolve direction
                        DirectionEvidence = "stl:no-graph",
                        SourceRef = $"net:{networkOrder}|cu:{compileUnitId}|uid:{stmt.Uid}"
                    });
                }

                doc.Status = ParseStatus.Succeeded;
            }
            catch (Exception ex)
            {
                doc.Status = ParseStatus.Failed;
                doc.FailureReason = ex.Message;
            }

            return doc;
        }

        private void ExtractFacts(PlcNetworkDocument doc, XElement flgNet, string blockName)
        {
            var model = TiaXmlParseHelper.ParseFlgNet(flgNet);

            // 1. Extract Access nodes
            foreach (var kvp in model.AccessByUid)
            {
                var uid = kvp.Key;
                var rawPath = kvp.Value;
                var scope = model.ScopeByUid.TryGetValue(uid, out var s) ? s : "";

                if (string.Equals(scope, "TypedConstant", StringComparison.OrdinalIgnoreCase))
                    continue;

                var canonicalPath = BuildCanonicalPath(blockName, scope, rawPath);
                if (string.IsNullOrWhiteSpace(canonicalPath))
                    continue;

                doc.Accesses.Add(new AccessNode
                {
                    Uid = uid,
                    CanonicalPath = canonicalPath,
                    Scope = scope,
                    Direction = AccessDirection.Unresolved, // resolved below via Wire analysis
                    SourceRef = $"net:{doc.NetworkOrder}|cu:{doc.CompileUnitId}|uid:{uid}"
                });
            }

            // 2. Extract Parts
            foreach (var part in model.Parts)
            {
                if (part.IsCall) continue; // Calls handled separately

                doc.Parts.Add(new LogicPart
                {
                    Uid = part.Uid,
                    Name = part.Name,
                    Negated = part.Negated,
                    TemplateValues = part.TemplateValues ?? new Dictionary<string, string>()
                });
            }

            // 3. Extract Call Sites
            foreach (var part in model.Parts.Where(p => p.IsCall))
            {
                var callSite = new CallSiteNode
                {
                    Uid = part.Uid,
                    RawCalleeName = part.CallName,
                    RawBlockType = part.BlockType,
                    RawInstancePath = part.Instance,
                    SourceRef = $"net:{doc.NetworkOrder}|cu:{doc.CompileUnitId}|uid:{part.Uid}"
                };

                foreach (var param in part.CallParameters)
                {
                    callSite.Parameters.Add(new CallParameterFact
                    {
                        Name = param.Name,
                        Section = param.Section
                    });
                }

                doc.CallSites.Add(callSite);
            }

            // 4. Extract Wires and resolve directions
            var accessByUid = doc.Accesses.ToDictionary(a => a.Uid, StringComparer.OrdinalIgnoreCase);
            var partByUid = doc.Parts.ToDictionary(p => p.Uid, StringComparer.OrdinalIgnoreCase);
            var callByUid = doc.CallSites.ToDictionary(c => c.Uid, StringComparer.OrdinalIgnoreCase);

            foreach (var wire in model.Wires)
            {
                var wireEdge = new WireEdge
                {
                    WireUid = wire.Uid,
                    FromPowerRail = wire.HasPowerrail,
                    IdentAccessUid = wire.IdentAccessUid
                };

                // Process each NameCon on this wire
                foreach (var nc in wire.NameConnections)
                {
                    wireEdge.TargetPartUid = nc.Uid;
                    wireEdge.TargetPinName = nc.Pin;

                    // Determine pin direction
                    var pinDir = ResolvePinDirection(nc.Uid, nc.Pin, partByUid, callByUid);
                    wireEdge.PinDirection = pinDir;

                    doc.Wires.Add(new WireEdge
                    {
                        WireUid = wire.Uid,
                        FromPowerRail = wire.HasPowerrail,
                        IdentAccessUid = wire.IdentAccessUid,
                        TargetPartUid = nc.Uid,
                        TargetPinName = nc.Pin,
                        PinDirection = pinDir
                    });

                    // Resolve Access direction from Wire/Pin
                    if (!string.IsNullOrWhiteSpace(wire.IdentAccessUid)
                        && accessByUid.TryGetValue(wire.IdentAccessUid, out var access))
                    {
                        var inferred = InferAccessDirection(pinDir, nc.Pin, nc.Uid, partByUid);
                        if (inferred != AccessDirection.Unresolved)
                        {
                            MergeDirection(access, inferred, $"wire:{wire.Uid}|pin:{nc.Pin}|part:{nc.Uid}");
                        }
                    }

                    // Resolve Parameter Binding for Call Sites
                    if (callByUid.TryGetValue(nc.Uid, out var call))
                    {
                        var matchingParam = call.Parameters.FirstOrDefault(p =>
                            string.Equals(p.Name, nc.Pin, StringComparison.OrdinalIgnoreCase));

                        if (matchingParam != null && !string.IsNullOrWhiteSpace(wire.IdentAccessUid))
                        {
                            var accessPath = accessByUid.TryGetValue(wire.IdentAccessUid, out var a)
                                ? a.CanonicalPath : "";

                            var bindingDir = InferDirectionFromSection(matchingParam.Section);

                            doc.ParameterBindings.Add(new ParameterBindingFact
                            {
                                CallSiteUid = nc.Uid,
                                FormalParameterName = matchingParam.Name,
                                FormalSection = matchingParam.Section,
                                ActualAccessUid = wire.IdentAccessUid,
                                ActualAccessPath = accessPath,
                                InferredDirection = bindingDir,
                                Evidence = $"wire:{wire.Uid}|pin:{nc.Pin}"
                            });

                            // Also set the connected info on the parameter
                            matchingParam.ConnectedAccessUid = wire.IdentAccessUid;
                            matchingParam.ConnectedAccessPath = accessPath;

                            // Resolve Access direction from parameter section
                            if (accessByUid.TryGetValue(wire.IdentAccessUid, out var paramAccess))
                            {
                                MergeDirection(paramAccess, bindingDir,
                                    $"param:{matchingParam.Name}|section:{matchingParam.Section}|call:{nc.Uid}");
                            }
                        }
                    }
                }
            }
        }

        private static PinDirection ResolvePinDirection(string partUid, string pinName,
            Dictionary<string, LogicPart> parts, Dictionary<string, CallSiteNode> calls)
        {
            if (string.IsNullOrWhiteSpace(pinName))
                return PinDirection.Unknown;

            // Call parameters have explicit section
            if (calls.TryGetValue(partUid, out var call))
            {
                var param = call.Parameters.FirstOrDefault(p =>
                    string.Equals(p.Name, pinName, StringComparison.OrdinalIgnoreCase));
                if (param != null)
                {
                    return param.Section?.ToLowerInvariant() switch
                    {
                        "input" => PinDirection.Input,
                        "output" => PinDirection.Output,
                        "inout" => PinDirection.InOut,
                        _ => PinDirection.Unknown
                    };
                }
            }

            // Standard instruction pin names
            if (KnownOutputPins.Contains(pinName))
                return PinDirection.Output;
            if (KnownInputPins.Contains(pinName))
                return PinDirection.Input;

            return PinDirection.Unknown;
        }

        private static AccessDirection InferAccessDirection(PinDirection pinDir, string pinName,
            string partUid, Dictionary<string, LogicPart> parts)
        {
            switch (pinDir)
            {
                case PinDirection.Input:
                    return AccessDirection.Read;
                case PinDirection.Output:
                    return AccessDirection.Write;
                case PinDirection.InOut:
                    return AccessDirection.ReadWrite;
            }

            // Fallback: check if the part is a known write instruction
            if (parts.TryGetValue(partUid, out var part))
            {
                if (WriteInstructionNames.Contains(part.Name))
                {
                    // For Move, "out"/"out1" are outputs; "in" is input
                    if (KnownInputPins.Contains(pinName))
                        return AccessDirection.Read;
                    return AccessDirection.Write;
                }
            }

            return AccessDirection.Unresolved;
        }

        private static AccessDirection InferDirectionFromSection(string section)
        {
            return (section ?? "").ToLowerInvariant() switch
            {
                "input" => AccessDirection.Read,
                "output" => AccessDirection.Write,
                "inout" => AccessDirection.ReadWrite,
                _ => AccessDirection.Unresolved
            };
        }

        private static void MergeDirection(AccessNode access, AccessDirection newDir, string evidence)
        {
            if (newDir == AccessDirection.Unresolved)
                return;

            if (access.Direction == AccessDirection.Unresolved)
            {
                access.Direction = newDir;
                access.DirectionEvidence = evidence;
                return;
            }

            // If already set and different, upgrade to ReadWrite
            if (access.Direction != newDir)
            {
                access.Direction = AccessDirection.ReadWrite;
                access.DirectionEvidence = (access.DirectionEvidence ?? "") + " | " + evidence;
            }
        }

        private string BuildCanonicalPath(string blockName, string scope, string rawPath)
        {
            var canonical = _canonicalizer.CanonicalizeSymbolPath(rawPath);
            if (string.IsNullOrWhiteSpace(canonical))
                return canonical;

            if (string.Equals(scope, "LocalVariable", StringComparison.OrdinalIgnoreCase))
                return _canonicalizer.BuildScopedPath(blockName, "Local", canonical);

            return canonical;
        }

        /// <summary>
        /// <summary>
        /// Attach the canonical (LadJson) body payload for SCL/STL networks. Failures are recorded on
        /// the document but never abort indexing; the legacy payload stays available as a fallback.
        /// </summary>
        private static void AttachCanonical(
            PlcNetworkDocument doc,
            XElement body,
            string blockId,
            string compileUnitId,
            int order)
        {
            if (doc == null || body == null)
                return;

            try
            {
                var facts = LadSchemaRegistry.Resolve(
                    TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentMajorVersion);
                var codec = NetworkCodecRegistry.ForXml(body, facts);
                var canonical = codec.Parse(body, new LadParseContext
                {
                    NetworkId = doc.NetworkId,
                    BlockId = blockId,
                    Order = order,
                    CompileUnitId = compileUnitId,
                    Title = doc.Title,
                    Comment = doc.Comment,
                    Language = doc.Language
                });
                doc.CanonicalJson = LadJson.SerializeNetwork(canonical);
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(doc.FailureReason))
                    doc.FailureReason = "canonical: " + ex.Message;
            }
        }

        /// <summary>
        /// Deterministic network id from (project, block, compileUnit, source order).
        /// Public so dependency-side writers can compute the same id to link
        /// plc_symbol_doc / plc_call_relation rows to their owning network.
        /// </summary>
        public static string GenerateNetworkId(string projectKey, string blockId, string compileUnitId, int order)
        {
            var basis = $"{projectKey}|{blockId}|{compileUnitId}|{order}";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(basis));
                return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string ComputeContentHash(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return "";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
                return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string ExtractNetworkTitle(XElement compileUnit)
        {
            var title = compileUnit.Descendants()
                .FirstOrDefault(x => string.Equals(x.Name.LocalName, "Title", StringComparison.OrdinalIgnoreCase));
            if (title == null) return "";

            var text = title.Descendants()
                .FirstOrDefault(x => string.Equals(x.Name.LocalName, "MultiLanguageText", StringComparison.OrdinalIgnoreCase));
            return text?.Value?.Trim() ?? title.Value?.Trim() ?? "";
        }

        private static string ExtractNetworkComment(XElement compileUnit)
        {
            var comment = compileUnit.Descendants()
                .FirstOrDefault(x => string.Equals(x.Name.LocalName, "Comment", StringComparison.OrdinalIgnoreCase));
            if (comment == null) return "";

            var text = comment.Descendants()
                .FirstOrDefault(x => string.Equals(x.Name.LocalName, "MultiLanguageText", StringComparison.OrdinalIgnoreCase));
            return text?.Value?.Trim() ?? comment.Value?.Trim() ?? "";
        }

        private static PlcNetworkDocument CreateFailedNetwork(string projectKey, string blockId, int order, string cuId, string reason)
        {
            return new PlcNetworkDocument
            {
                NetworkId = GenerateNetworkId(projectKey, blockId, cuId, order),
                BlockId = blockId,
                ProjectKey = projectKey,
                NetworkOrder = order,
                CompileUnitId = cuId,
                Language = "LAD",
                ParserVersion = CurrentParserVersion,
                IndexedAtUtc = DateTime.UtcNow,
                Status = ParseStatus.Failed,
                FailureReason = reason
            };
        }
    }
}
