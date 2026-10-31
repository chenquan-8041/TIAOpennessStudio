#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TIAOpennessStudio.Services.Builders
{
    /// <summary>
    /// JSON adapter layer for the PLC Builder MCP tools.
    /// Field validation and error location are centralized here, so the tool layer does not accumulate messy parsing logic.
    /// </summary>
    public static class PlcBuilderToolJson
    {
        public static JsonObject BuildUdt(string json, string culture = "zh-CN")
        {
            var root = ParseObject(json, "$");
            var udtName = ReadString(root, "$.name", "name", "udtName");

            // Canonical path: the interface tree from export_block_ir is written back verbatim.
            if (root["interface"] is JsonObject interfaceJson)
            {
                var node = TIAOpennessStudio.Services.Lad.LadJson.NodeFromJson(interfaceJson);
                var interfaceXml = PlcUdtXmlBuilder.BuildXml(udtName, node, culture);
                return BuildResult("plc-build-udt-xml", interfaceXml, new JsonObject
                {
                    ["udtName"] = udtName,
                    ["interfaceTree"] = true
                });
            }

            var members = ReadArray(root, "members", "$.members")
                .Select((x, i) => new PlcUdtMemberDefinition(
                    ReadString(AsObject(x, "$.members[" + i + "]"), "$.members[" + i + "].name", "name"),
                    ReadString(AsObject(x, "$.members[" + i + "]"), "$.members[" + i + "].datatype", "datatype", "dataType"),
                    ReadBool(AsObject(x, "$.members[" + i + "]"), "externalWritable", defaultValue: false),
                    ReadOptionalString(AsObject(x, "$.members[" + i + "]"), "commentZhCn", "comment", "commentZh")))
                .ToArray();

            var xml = PlcUdtXmlBuilder.BuildXml(udtName, members, culture);
            return BuildResult("plc-build-udt-xml", xml, new JsonObject
            {
                ["udtName"] = udtName,
                ["memberCount"] = members.Length
            });
        }

        public static JsonObject BuildTagTable(string json)
        {
            var root = ParseObject(json, "$");
            var tableName = ReadString(root, "$.tableName", "tableName", "name");
            var tags = ReadArray(root, "tags", "$.tags")
                .Select((x, i) =>
                {
                    var tag = AsObject(x, "$.tags[" + i + "]");
                    return new PlcTagDefinition(
                        ReadString(tag, "$.tags[" + i + "].name", "name"),
                        ReadString(tag, "$.tags[" + i + "].dataTypeName", "dataTypeName", "datatype", "dataType"),
                        ReadString(tag, "$.tags[" + i + "].logicalAddress", "logicalAddress", "address"));
                })
                .ToArray();

            var xml = PlcTagTableXmlBuilder.BuildXml(tableName, tags);
            return BuildResult("plc-build-tag-table-xml", xml, new JsonObject
            {
                ["tableName"] = tableName,
                ["tagCount"] = tags.Length
            });
        }

        public static JsonObject BuildGlobalDb(string json, string memoryLayoutOverride = null)
        {
            var root = ParseObject(json, "$");

            // Instance DBs must never be rebuilt as global DBs (silent structural change).
            if (TIAOpennessStudio.Services.Blocks.BlockWriteGuards.TryRejectInstanceDb(root, out var instanceError))
                throw new InvalidOperationException(instanceError);
            // Invalid memory_layout values fail closed; omitted values fall back to the IR's own layout.
            if (!TIAOpennessStudio.Services.Blocks.BlockWriteGuards.TryResolveMemoryLayout(
                    root, memoryLayoutOverride, out var memoryLayout, out var layoutError))
                throw new ArgumentException(layoutError);

            // An explicit layout override cannot be honoured on a version whose XML import rejects
            // the block-level MemoryLayout element: fail closed instead of silently keeping the default.
            if (!string.IsNullOrWhiteSpace(memoryLayoutOverride)
                && !PlcBlockXmlHelpers.SupportsBlockAttribute("MemoryLayout"))
                throw new ArgumentException(
                    "memory_layout='" + memoryLayoutOverride + "' cannot be applied on TIA "
                    + PlcBlockXmlHelpers.DisplayVersion
                    + ": this version rejects the block-level MemoryLayout element on XML import. "
                    + "Omit memory_layout and let TIA keep the project default.");

            var dbName = ReadString(root, "$.dbName", "dbName", "name");
            var dbNumber = ReadInt(root, "$.dbNumber", "dbNumber", "number");

            // Canonical path: the interface tree from export_block_ir is written back verbatim.
            if (root["interface"] is JsonObject interfaceJson)
            {
                var node = TIAOpennessStudio.Services.Lad.LadJson.NodeFromJson(interfaceJson);
                var interfaceXml = PlcGlobalDbXmlBuilder.BuildXml(dbName, dbNumber, node, memoryLayout);
                return BuildResult("plc-build-global-db-xml", interfaceXml, new JsonObject
                {
                    ["dbName"] = dbName,
                    ["dbNumber"] = dbNumber,
                    ["interfaceTree"] = true,
                    ["memoryLayout"] = memoryLayout
                });
            }

            var membersNode = root["staticMembers"] as JsonArray ?? root["members"] as JsonArray;
            if (membersNode == null)
                throw new ArgumentException("Missing required JSON array: $.staticMembers (or $.members)");

            var members = membersNode
                .Select((x, i) =>
                {
                    var member = AsObject(x, "$.staticMembers[" + i + "]");
                    return new PlcDbMemberDefinition(
                        ReadString(member, "$.staticMembers[" + i + "].name", "name"),
                        ReadString(member, "$.staticMembers[" + i + "].datatype", "datatype", "dataType"),
                        ReadNullableBool(member, "externalWritable"),
                        ReadOptionalString(member, "commentZhCn", "comment", "commentZh"),
                        ReadOptionalString(member, "startValue"));
                })
                .ToArray();

            var xml = PlcGlobalDbXmlBuilder.BuildXml(dbName, dbNumber, members, memoryLayout);
            return BuildResult("plc-build-global-db-xml", xml, new JsonObject
            {
                ["dbName"] = dbName,
                ["dbNumber"] = dbNumber,
                ["memberCount"] = members.Length,
                ["memoryLayout"] = memoryLayout
            });
        }

        public static JsonObject BuildStructuredText(string json, bool innerOnly = false)
        {
            var root = ParseObject(json, "$");
            var builder = BuildStructuredTextBuilder(root);
            var xml = innerOnly ? builder.BuildInnerXml() : builder.BuildStructuredTextXml();
            return BuildResult("plc-build-structured-text-xml", xml, new JsonObject
            {
                ["innerOnly"] = innerOnly,
                ["operationCount"] = (root["operations"] as JsonArray)?.Count ?? 0
            });
        }

        public static JsonObject BuildFlgNetCall(string json)
        {
            var root = ParseObject(json, "$");
            var callName = ReadString(root, "$.callName", "callName", "name");
            var parameters = ReadArray(root, "parameters", "$.parameters")
                .Select((x, i) => ReadFlgNetParameter(AsObject(x, "$.parameters[" + i + "]"), i))
                .ToArray();

            var xml = FlgNetCallXmlBuilder.BuildXml(callName, parameters);
            return BuildResult("plc-build-flgnet-call-xml", xml, new JsonObject
            {
                ["callName"] = callName,
                ["parameterCount"] = parameters.Length
            });
        }

        public static JsonObject ComposeFcBlock(string json, string culture = "zh-CN")
        {
            var root = ParseObject(json, "$");
            var blockName = ReadString(root, "$.blockName", "blockName", "name");
            var blockNumber = ReadInt(root, "$.blockNumber", "blockNumber", "number");
            var inputs = ReadOptionalMembers(root, "inputs", "$.inputs");
            var outputs = ReadOptionalMembers(root, "outputs", "$.outputs");
            var structuredTextInnerXml = ReadCanonicalSclInnerXml(root);
            if (string.IsNullOrWhiteSpace(structuredTextInnerXml))
                structuredTextInnerXml = ReadStructuredTextInnerXml(root);
            var interfaceOverride = ReadInterfaceOverride(root);
            var blockComment = ReadOptionalString(root, "commentZhCn", "blockCommentZhCn", "comment");
            var blockTitle = ReadOptionalString(root, "titleZhCn", "blockTitleZhCn", "title");
            var netComment = ReadOptionalString(root, "networkCommentZhCn", "networkComment");
            var netTitle = ReadOptionalString(root, "networkTitleZhCn", "networkTitle");

            var xml = PlcFcBlockXmlComposer.ComposeXml(blockName, blockNumber, inputs, outputs, structuredTextInnerXml,
                blockComment, blockTitle, netComment, netTitle, culture, interfaceOverride);
            return BuildResult("plc-compose-fc-block-xml", xml, new JsonObject
            {
                ["blockName"] = blockName,
                ["blockNumber"] = blockNumber,
                ["inputCount"] = inputs.Length,
                ["outputCount"] = outputs.Length
            });
        }

        public static JsonObject ComposeLadFcBlock(string json, string culture = "zh-CN")
        {
            var root = ParseObject(json, "$");
            // Never rebuild from a partial page: missing networks would be deleted silently.
            if (TIAOpennessStudio.Services.Blocks.BlockWriteGuards.TryRejectTruncatedIr(root, out var truncatedError))
                throw new InvalidOperationException(truncatedError);
            var blockName = ReadString(root, "$.blockName", "blockName", "name");
            var blockNumber = ReadInt(root, "$.blockNumber", "blockNumber", "number");
            var inputs = ReadOptionalMembers(root, "inputs", "$.inputs");
            var outputs = ReadOptionalMembers(root, "outputs", "$.outputs");
            var blockComment = ReadOptionalString(root, "commentZhCn", "blockCommentZhCn", "comment");
            var blockTitle = ReadOptionalString(root, "titleZhCn", "blockTitleZhCn", "title");
            var blockLanguage = ReadOptionalString(root, "language", "programmingLanguage");
            var interfaceOverride = ReadInterfaceOverride(root);

            var built = ReadLadNetworks(root, blockLanguage);

            var xml = PlcLadFcBlockXmlComposer.ComposeXml(
                blockName, blockNumber, inputs, outputs, built,
                kind: PlcLadFcBlockXmlComposer.LadBlockKind.Fc,
                blockCommentZhCn: blockComment, blockTitleZhCn: blockTitle, culture: culture,
                blockLanguage: blockLanguage, interfaceOverride: interfaceOverride);
            VerifyRoundTrip(xml, built, interfaceOverride);
            return BuildResult("plc-compose-lad-fc-block-xml", xml, new JsonObject
            {
                ["blockName"] = blockName,
                ["blockNumber"] = blockNumber,
                ["networkCount"] = built.Count,
                ["inputCount"] = inputs.Length,
                ["outputCount"] = outputs.Length
            });
        }

        public static JsonObject ComposeLadFbBlock(string json, string culture = "zh-CN")
        {
            var root = ParseObject(json, "$");
            // Never rebuild from a partial page: missing networks would be deleted silently.
            if (TIAOpennessStudio.Services.Blocks.BlockWriteGuards.TryRejectTruncatedIr(root, out var truncatedError))
                throw new InvalidOperationException(truncatedError);
            var blockName = ReadString(root, "$.blockName", "blockName", "name");
            var blockNumber = ReadInt(root, "$.blockNumber", "blockNumber", "number");
            var inputs = ReadOptionalMembers(root, "inputs", "$.inputs");
            var outputs = ReadOptionalMembers(root, "outputs", "$.outputs");
            var inouts = ReadOptionalMembers(root, "inouts", "$.inouts", "inOuts", "inOut");
            var statics = ReadOptionalMembers(root, "statics", "$.statics", "staticMembers", "static");
            var temps = ReadOptionalMembers(root, "temps", "$.temps", "tempMembers", "temp");
            var blockComment = ReadOptionalString(root, "commentZhCn", "blockCommentZhCn", "comment");
            var blockTitle = ReadOptionalString(root, "titleZhCn", "blockTitleZhCn", "title");
            var blockLanguage = ReadOptionalString(root, "language", "programmingLanguage");
            var interfaceOverride = ReadInterfaceOverride(root);

            var built = ReadLadNetworks(root, blockLanguage);

            var xml = PlcLadFcBlockXmlComposer.ComposeXml(
                blockName, blockNumber, inputs, outputs, built,
                kind: PlcLadFcBlockXmlComposer.LadBlockKind.Fb,
                inOutMembers: inouts, staticMembers: statics, tempMembers: temps,
                blockCommentZhCn: blockComment, blockTitleZhCn: blockTitle, culture: culture,
                blockLanguage: blockLanguage, interfaceOverride: interfaceOverride);
            VerifyRoundTrip(xml, built, interfaceOverride);
            return BuildResult("plc-compose-lad-fb-block-xml", xml, new JsonObject
            {
                ["blockName"] = blockName,
                ["blockNumber"] = blockNumber,
                ["networkCount"] = built.Count,
                ["inputCount"] = inputs.Length,
                ["outputCount"] = outputs.Length,
                ["inOutCount"] = inouts.Length,
                ["staticCount"] = statics.Length,
                ["tempCount"] = temps.Length
            });
        }

        /// <summary>
        /// Parse the shared <c>networks[]</c> array into graphical networks. A network carries either
        /// <c>body</c> (the canonical node tree from export_block_ir, emitted by the same codec the
        /// read path uses) or <c>callJson</c> (legacy shortcut for one FC/FB call). Shared by
        /// LAD/FBD FC and FB (the network part is identical; only the outer interface differs).
        /// </summary>
        private static List<PlcLadFcBlockXmlComposer.LadNetwork> ReadLadNetworks(JsonObject root, string defaultLanguage)
        {
            var nets = ReadArray(root, "networks", "$.networks");
            if (nets.Count == 0)
                throw new ArgumentException("LAD block requires at least one networks[] element");

            var built = new List<PlcLadFcBlockXmlComposer.LadNetwork>();
            for (var i = 0; i < nets.Count; i++)
            {
                var n = AsObject(nets[i], "$.networks[" + i + "]");
                var netTitle = ReadOptionalString(n, "titleZhCn", "title");
                var netComment = ReadOptionalString(n, "commentZhCn", "comment");

                // Unified path: the network carries the canonical body tree produced by the read
                // side (LadBlockParser/LadJson), so read and write share one shape.
                if (n["body"] is JsonObject)
                {
                    var network = TIAOpennessStudio.Services.Lad.LadJson.NetworkFromJson(n);
                    var facts = TIAOpennessStudio.Models.Lad.LadSchemaRegistry.Resolve(
                        TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentMajorVersion);
                    var codec = TIAOpennessStudio.Services.Lad.Codecs.NetworkCodecRegistry
                        .ForLanguage(network.Language, facts);
                    var unifiedFlgNet = codec.Emit(
                        network,
                        TIAOpennessStudio.Services.Lad.Codecs.LadEmitContext.For(facts));
                    var networkLanguage = string.IsNullOrWhiteSpace(network.Language)
                        ? defaultLanguage
                        : network.Language;
                    built.Add(new PlcLadFcBlockXmlComposer.LadNetwork(
                        unifiedFlgNet,
                        netTitle ?? network.Title,
                        netComment ?? network.Comment,
                        networkLanguage));
                    continue;
                }

                // Legacy sugar: one FC/FB call per network via callJson.
                var callJson = n["callJson"] as JsonObject ?? n["call"] as JsonObject;
                if (callJson == null)
                    throw new ArgumentException("$.networks[" + i + "] is missing the callJson object");
                var callName = ReadString(callJson, "$.networks[" + i + "].callJson.callName", "callName", "name");
                var parametersArray = callJson["parameters"] as JsonArray;
                var parameters = parametersArray == null
                    ? Array.Empty<FlgNetCallParameter>()
                    : parametersArray
                        .Select((x, j) => ReadFlgNetParameter(AsObject(x, "$.networks[" + i + "].callJson.parameters[" + j + "]"), j))
                        .ToArray();
                var flgNet = FlgNetCallXmlBuilder.BuildFlgNet(callName, parameters);
                built.Add(new PlcLadFcBlockXmlComposer.LadNetwork(flgNet, netTitle, netComment, defaultLanguage));
            }
            return built;
        }

        public static JsonObject ComposeFbBlock(string json, string culture = "zh-CN")
        {
            var root = ParseObject(json, "$");
            var blockName = ReadString(root, "$.blockName", "blockName", "name");
            var blockNumber = ReadInt(root, "$.blockNumber", "blockNumber", "number");
            var inputs = ReadOptionalMembers(root, "inputs", "$.inputs");
            var outputs = ReadOptionalMembers(root, "outputs", "$.outputs");
            var inouts = ReadOptionalMembers(root, "inouts", "$.inouts", "inOuts", "inOut");
            var statics = ReadOptionalMembers(root, "statics", "$.statics", "staticMembers", "static");
            var temps = ReadOptionalMembers(root, "temps", "$.temps", "tempMembers", "temp");
            var structuredTextInnerXml = ReadCanonicalSclInnerXml(root);
            if (string.IsNullOrWhiteSpace(structuredTextInnerXml))
                structuredTextInnerXml = ReadStructuredTextInnerXml(root);
            var interfaceOverride = ReadInterfaceOverride(root);
            var blockComment = ReadOptionalString(root, "commentZhCn", "blockCommentZhCn", "comment");
            var blockTitle = ReadOptionalString(root, "titleZhCn", "blockTitleZhCn", "title");
            var netComment = ReadOptionalString(root, "networkCommentZhCn", "networkComment");
            var netTitle = ReadOptionalString(root, "networkTitleZhCn", "networkTitle");

            var xml = PlcFbBlockXmlComposer.ComposeXml(blockName, blockNumber, inputs, outputs, inouts, statics, temps, structuredTextInnerXml,
                blockComment, blockTitle, netComment, netTitle, culture, interfaceOverride);
            return BuildResult("plc-compose-fb-block-xml", xml, new JsonObject
            {
                ["blockName"] = blockName,
                ["blockNumber"] = blockNumber,
                ["inputCount"] = inputs.Length,
                ["outputCount"] = outputs.Length,
                ["inOutCount"] = inouts.Length,
                ["staticCount"] = statics.Length,
                ["tempCount"] = temps.Length
            });
        }

        private static StructuredTextXmlBuilder BuildStructuredTextBuilder(JsonObject root)
        {
            var firstUid = ReadOptionalInt(root, "firstUid") ?? 21;
            var builder = new StructuredTextXmlBuilder(firstUid);
            var operations = ReadArray(root, "operations", "$.operations");
            for (var i = 0; i < operations.Count; i++)
            {
                var op = AsObject(operations[i], "$.operations[" + i + "]");
                var kind = ReadString(op, "$.operations[" + i + "].op", "op", "kind", "type").Trim().ToLowerInvariant();
                var indent = ReadOptionalInt(op, "indent") ?? 0;
                switch (kind)
                {
                    case "if":
                    case "ifheader":
                        builder.IfHeader(ReadString(op, "$.operations[" + i + "].condition", "condition", "conditionVariable", "variable"), indent);
                        break;
                    case "elsif":
                    case "elseif":
                    case "elsifheader":
                        builder.ElsIfHeader(ReadString(op, "$.operations[" + i + "].condition", "condition", "conditionVariable", "variable"), indent);
                        break;
                    case "else":
                        builder.ElseLine(indent);
                        break;
                    case "endif":
                    case "end_if":
                        builder.EndIf(indent);
                        break;
                    case "assign":
                    case "assignment":
                        {
                            var target = ReadString(op, "$.operations[" + i + "].target", "target");
                            var src = ReadOptionalString(op, "source", "fromSymbol");
                            if (!string.IsNullOrWhiteSpace(src))
                                builder.AssignFromSymbol(target, src, indent);
                            else
                                builder.Assignment(target, ReadString(op, "$.operations[" + i + "].literalValue", "literalValue", "value"), indent);
                        }
                        break;
                    case "token":
                        if (indent > 0) builder.Blank(indent);
                        builder.Token(ReadString(op, "$.operations[" + i + "].text", "text"));
                        break;
                    case "blank":
                        builder.Blank(ReadOptionalInt(op, "count") ?? 1);
                        break;
                    case "newline":
                    case "new_line":
                        builder.NewLine();
                        break;
                    case "global":
                        if (indent > 0) builder.Blank(indent);
                        builder.GlobalVariable(ReadString(op, "$.operations[" + i + "].name", "name").Split('.'));
                        break;
                    case "local":
                        if (indent > 0) builder.Blank(indent);
                        builder.LocalVariable(ReadString(op, "$.operations[" + i + "].name", "name"));
                        break;
                    case "symbol":
                        // Auto-detect scope: "x" global / x local
                        if (indent > 0) builder.Blank(indent);
                        builder.Symbol(ReadString(op, "$.operations[" + i + "].name", "name"));
                        break;
                    case "literal":
                        if (indent > 0) builder.Blank(indent);
                        builder.LiteralConstant(ReadString(op, "$.operations[" + i + "].value", "value", "literalValue"));
                        break;
                    case "line":
                        // Free expression line: items[] array, each item may be:
                        //   {sym:"name"}     local / global if quoted, auto-detected by Symbol()
                        //   {token:"AND"}    keyword / operator token (Blank added before and after, except ( ) , ; )
                        //   {lit:"FALSE"}    literal constant
                        //   {raw:"("}        raw token (no automatic Blank)
                        // A ; + newline is appended at the end (unless the last item is already ; or raw=";")
                        if (indent > 0) builder.Blank(indent);
                        EmitLine(builder, op, i);
                        break;
                    default:
                        throw new ArgumentException("Unsupported StructuredText operation at $.operations[" + i + "].op: " + kind);
                }
            }

            return builder;
        }

        // SCL free expression line: items[] parsing + automatic Blank (except punctuation that must be tight)
        private static void EmitLine(StructuredTextXmlBuilder builder, JsonObject lineOp, int opIndex)
        {
            if (lineOp["items"] is not JsonArray items)
                throw new ArgumentException("$.operations[" + opIndex + "].items is missing");
            string? lastTokenText = null;
            for (var k = 0; k < items.Count; k++)
            {
                if (items[k] is not JsonObject item)
                    throw new ArgumentException("$.operations[" + opIndex + "].items[" + k + "] must be an object");
                bool tightBefore = false; // Whether the current item must be tight against the previous one (e.g. ) ; ,)

                if (item["sym"] is JsonNode symN)
                {
                    if (k > 0 && !tightBefore) builder.Blank();
                    builder.Symbol(symN.ToString());
                    lastTokenText = null;
                }
                else if (item["token"] is JsonNode tokN)
                {
                    var t = tokN.ToString();
                    tightBefore = t == ")" || t == "," || t == ";";
                    if (k > 0 && !tightBefore) builder.Blank();
                    builder.Token(t);
                    lastTokenText = t;
                }
                else if (item["lit"] is JsonNode litN)
                {
                    if (k > 0) builder.Blank();
                    builder.LiteralConstant(litN.ToString());
                    lastTokenText = null;
                }
                else if (item["raw"] is JsonNode rawN)
                {
                    builder.Token(rawN.ToString()); // No automatic Blank
                    lastTokenText = rawN.ToString();
                }
                else
                {
                    throw new ArgumentException("$.operations[" + opIndex + "].items[" + k + "] requires one of sym / token / lit / raw");
                }
            }
            // Append ; if the end is not already ;
            if (lastTokenText != ";")
                builder.Token(";");
            builder.NewLine();
        }

        private static FlgNetCallParameter ReadFlgNetParameter(JsonObject parameter, int index)
        {
            var name = ReadString(parameter, "$.parameters[" + index + "].name", "parameterName", "name");
            var section = ReadString(parameter, "$.parameters[" + index + "].section", "section");
            var dataType = ReadString(parameter, "$.parameters[" + index + "].dataType", "dataType", "datatype", "type");
            var sourceKind = ReadOptionalString(parameter, "sourceKind", "source", "kind").Trim().ToLowerInvariant();
            if (sourceKind == "constant" || sourceKind == "literal" || sourceKind == "literalconstant")
            {
                return FlgNetCallParameter.Constant(
                    name,
                    section,
                    dataType,
                    ReadString(parameter, "$.parameters[" + index + "].value", "value", "constantValue"));
            }

            var path = parameter["symbolPath"] as JsonArray;
            if (path != null)
            {
                return FlgNetCallParameter.Global(name, section, dataType, path.Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToArray());
            }

            var symbol = ReadString(parameter, "$.parameters[" + index + "].symbol", "symbol", "path", "plcTag");
            return FlgNetCallParameter.Global(name, section, dataType, symbol.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static PlcBlockMemberDefinition[] ReadMembers(JsonObject root, string arrayName, string path)
        {
            return ReadArray(root, arrayName, path)
                .Select((x, i) =>
                {
                    var member = AsObject(x, path + "[" + i + "]");
                    return new PlcBlockMemberDefinition(
                        ReadString(member, path + "[" + i + "].name", "name"),
                        ReadString(member, path + "[" + i + "].datatype", "datatype", "dataType"),
                        ReadOptionalString(member, "commentZhCn", "comment", "commentZh"));
                })
                .ToArray();
        }

        /// <summary>
        /// Canonical interface tree from export_block_ir → Interface element for the target version.
        /// Whenever present it replaces the member-section skeleton, so read → write keeps nested
        /// members, comments and start values instead of flattening them.
        /// </summary>
        private static XElement ReadInterfaceOverride(JsonObject root)
        {
            if (!(root["interface"] is JsonObject interfaceJson))
                return null;

            var node = TIAOpennessStudio.Services.Lad.LadJson.NodeFromJson(interfaceJson);
            var facts = TIAOpennessStudio.Models.Lad.LadSchemaRegistry.Resolve(
                TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentMajorVersion);
            return TIAOpennessStudio.Services.Lad.Codecs.NetworkBodyCodec.EmitInterface(node, facts, facts);
        }

        /// <summary>
        /// Canonical StructuredText body (export_block_ir network body) → inner XML for the FC/FB
        /// composer, so SCL networks can be read, edited and written back too.
        /// </summary>
        private static string ReadCanonicalSclInnerXml(JsonObject root)
        {
            JsonObject body = root["body"] as JsonObject;
            if (body == null
                && root["networks"] is JsonArray networks
                && networks.Count > 0
                && networks[0] is JsonObject firstNetwork)
            {
                body = firstNetwork["body"] as JsonObject;
            }
            if (body == null)
                return null;

            var network = TIAOpennessStudio.Services.Lad.LadJson.NetworkFromJson(new JsonObject
            {
                ["language"] = "SCL",
                ["body"] = body.DeepClone()
            });
            var facts = TIAOpennessStudio.Models.Lad.LadSchemaRegistry.Resolve(
                TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentMajorVersion);
            var codec = TIAOpennessStudio.Services.Lad.Codecs.NetworkCodecRegistry.ForLanguage("SCL", facts);
            var emitted = codec.Emit(network, TIAOpennessStudio.Services.Lad.Codecs.LadEmitContext.For(facts));

            var sb = new StringBuilder();
            foreach (var node in emitted.Nodes())
            {
                if (node is XElement element) sb.Append(element.ToString(SaveOptions.DisableFormatting));
                else if (node is XText text) sb.Append(text.Value);
            }
            return sb.ToString();
        }

        /// <summary>
        /// L1 self-check: parse the generated XML back with the same codec the read path uses and
        /// compare every network body node-by-node. Catches namespace/order/reference bugs offline,
        /// before the XML ever reaches TIA (the GRAPH side already does the same via GraphXmlParser).
        /// </summary>
        private static void VerifyRoundTrip(
            string xml, IReadOnlyList<PlcLadFcBlockXmlComposer.LadNetwork> expected, XElement interfaceOverride = null)
        {
            TIAOpennessStudio.Models.Lad.LadBlock parsed;
            try
            {
                var facts = TIAOpennessStudio.Models.Lad.LadSchemaRegistry.Resolve(
                    TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentMajorVersion);
                parsed = TIAOpennessStudio.Services.Lad.LadBlockParser.Parse(xml, "selfcheck", facts);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Generated XML cannot be parsed back: " + ex.Message, ex);
            }

            if (parsed.Networks.Count != expected.Count)
            {
                throw new InvalidOperationException(
                    "Round-trip check failed: generated XML has " + parsed.Networks.Count
                    + " network(s) but " + expected.Count + " were requested.");
            }

            for (var i = 0; i < expected.Count; i++)
            {
                var expectedNode = TIAOpennessStudio.Services.Lad.Codecs.NetworkBodyCodec.FromXmlElement(expected[i].FlgNet);
                var actualNode = parsed.Networks[i].Body;
                if (!TIAOpennessStudio.Services.Lad.LadTreeComparer.TryCompare(expectedNode, actualNode, out var difference))
                {
                    throw new InvalidOperationException(
                        "Round-trip check failed on network " + (i + 1) + ": " + difference);
                }

                if (!string.Equals(expected[i].Language, parsed.Networks[i].Language, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Round-trip check failed on network " + (i + 1) + ": language '"
                        + parsed.Networks[i].Language + "' != '" + expected[i].Language + "'.");
                }
                if (!string.Equals(expected[i].TitleZhCn ?? "", parsed.Networks[i].Title ?? "", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Round-trip check failed on network " + (i + 1) + ": title was not preserved.");
                }
                if (!string.Equals(expected[i].CommentZhCn ?? "", parsed.Networks[i].Comment ?? "", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Round-trip check failed on network " + (i + 1) + ": comment was not preserved.");
                }
            }

            if (interfaceOverride != null)
            {
                var expectedInterface = TIAOpennessStudio.Services.Lad.Codecs.NetworkBodyCodec.FromXmlElement(interfaceOverride);
                if (!TIAOpennessStudio.Services.Lad.LadTreeComparer.TryCompare(expectedInterface, parsed.Interface, out var interfaceDiff))
                {
                    throw new InvalidOperationException(
                        "Round-trip check failed on the block interface: " + interfaceDiff);
                }
            }
        }

        private static PlcBlockMemberDefinition[] ReadOptionalMembers(JsonObject root, string defaultArrayName, string defaultPath, params string[] aliases)
        {
            var names = new[] { defaultArrayName }.Concat(aliases ?? Array.Empty<string>()).ToArray();
            var foundName = names.FirstOrDefault(name => root[name] is JsonArray);
            if (foundName == null)
                return Array.Empty<PlcBlockMemberDefinition>();

            return ReadMembers(root, foundName, "$." + foundName);
        }

        private static string ReadStructuredTextInnerXml(JsonObject root)
        {
            var direct = ReadOptionalString(root, "structuredTextInnerXml", "structuredTextXml", "sclInnerXml");
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            if (root["structuredText"] is JsonObject structuredText)
                return BuildStructuredTextBuilder(structuredText).BuildInnerXml();

            throw new ArgumentException("Missing required StructuredText content: $.structuredTextInnerXml or $.structuredText.operations");
        }

        private static JsonObject BuildResult(string mode, string xml, JsonObject summary)
        {
            var parseOk = true;
            string error = "";
            try
            {
                XDocument.Parse(xml);
            }
            catch (Exception ex)
            {
                try
                {
                    // StructuredText inner XML is a fragment for the Block Composer to embed; multiple sibling nodes are allowed.
                    XDocument.Parse("<Fragment>" + xml + "</Fragment>");
                }
                catch
                {
                    parseOk = false;
                    error = ex.Message;
                }
            }

            return new JsonObject
            {
                ["ok"] = parseOk,
                ["mode"] = mode,
                ["offlineOnly"] = true,
                ["xmlParseOk"] = parseOk,
                ["error"] = string.IsNullOrWhiteSpace(error) ? null : JsonValue.Create(error),
                ["xml"] = xml,
                ["summary"] = summary,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "离线 XML 生成：不连接 TIA Portal，不打开项目，不导入 PLC 对象。",
                    ["write"] = "本工具只返回 XML 字符串，不写工程、不写交付包。"
                }
            };
        }

        private static JsonObject ParseObject(string json, string path)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("Missing required JSON object: " + path);

            var node = JsonNode.Parse(json);
            return AsObject(node, path);
        }

        private static JsonObject AsObject(JsonNode? node, string path)
        {
            return node as JsonObject ?? throw new ArgumentException("Expected JSON object at " + path);
        }

        private static JsonArray ReadArray(JsonObject root, string name, string path)
        {
            return root[name] as JsonArray ?? throw new ArgumentException("Missing required JSON array: " + path);
        }

        private static string ReadString(JsonObject root, string path, params string[] names)
        {
            var value = ReadOptionalString(root, names);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Missing required JSON string: " + path);
            return value;
        }

        private static string ReadOptionalString(JsonObject root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetPropertyValue(name, out var node) && node != null)
                    return node.ToString();
            }

            return "";
        }

        private static int ReadInt(JsonObject root, string path, params string[] names)
        {
            var value = ReadOptionalInt(root, names);
            if (!value.HasValue)
                throw new ArgumentException("Missing required JSON integer: " + path);
            return value.Value;
        }

        private static int? ReadOptionalInt(JsonObject root, params string[] names)
        {
            foreach (var name in names)
            {
                if (!root.TryGetPropertyValue(name, out var node) || node == null)
                    continue;

                if (node is JsonValue value && value.TryGetValue<int>(out var intValue))
                    return intValue;
                if (int.TryParse(node.ToString(), out var parsed))
                    return parsed;
            }

            return null;
        }

        private static bool ReadBool(JsonObject root, string name, bool defaultValue)
        {
            return ReadNullableBool(root, name) ?? defaultValue;
        }

        private static bool? ReadNullableBool(JsonObject root, string name)
        {
            if (!root.TryGetPropertyValue(name, out var node) || node == null)
                return null;
            if (node is JsonValue value && value.TryGetValue<bool>(out var boolValue))
                return boolValue;
            if (bool.TryParse(node.ToString(), out var parsed))
                return parsed;
            throw new ArgumentException("Expected JSON boolean at property '" + name + "'.");
        }
    }
}
