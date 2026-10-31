using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TIAOpennessStudio.Models.Blocks;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Blocks;
using TIAOpennessStudio.Services.Builders;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Built-in MCP builder tools for the TIA context server.
    ///
    /// Design: the ladder (LAD) block builders are Openness-STA-affine *write* tools, so by project
    /// convention they live in the built-in MCP (this server), not in the agent-side tool layer.
    /// The agent only decides to call <c>build_lad_fc</c> / <c>build_lad_fb</c>; the concrete
    /// compose + import happens here.
    ///
    /// This handler is kept in a dedicated partial file so the <see cref="HandleMcpToolCallAsync"/>
    /// switch stays a thin one-line dispatcher per tool instead of accumulating large case bodies.
    ///
    /// Two steps:
    ///   1. Compose — offline, pure XML generation via <see cref="PlcBuilderToolJson"/>. Does NOT
    ///      touch TIA / COM, so it runs on the HTTP worker thread.
    ///   2. Import — STA-affine, marshaled through <see cref="_runOnTiaSta"/> into
    ///      <see cref="ITiaOpennessService.ImportBlockCodeAsync"/> (the single real write API).
    /// </summary>
    public sealed partial class TiaContextHttpServer
    {
        /// <summary>
        /// Compose a LAD FC/FB block from the builder JSON contract and import it into TIA Portal.
        /// </summary>
        /// <param name="arguments">
        /// MCP tool arguments. Expected shape:
        ///   { "json": &lt;LAD block JSON per the 4.7 contract&gt;, "group_path": "optional/group" }
        /// For backward tolerance, the LAD JSON may also be passed inline (top-level blockName/networks).
        /// </param>
        /// <param name="kind">"ladfc" or "ladfb".</param>
        /// <returns>(resultText, errorText) — exactly one is non-null; the switch writes the MCP envelope.</returns>
        private async Task<(string resultText, string errorText)> HandleBuildLadAsync(
            JsonElement arguments, string kind, string rid = null, bool isRemote = false)
        {
            // --- Extract the LAD JSON payload ---
            // Prefer an explicit "json" argument (object or escaped string); fall back to treating the
            // whole arguments object as the LAD JSON (tolerant to a model that inlines the fields).
            string ladJson = null;
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("json", out var jsonEl))
            {
                ladJson = jsonEl.ValueKind == JsonValueKind.String ? jsonEl.GetString() : jsonEl.GetRawText();
            }
            else if (arguments.ValueKind == JsonValueKind.Object)
            {
                ladJson = arguments.GetRawText();
            }

            if (string.IsNullOrWhiteSpace(ladJson))
                return (null, "A 'json' argument carrying the LAD block specification is required.");

            string groupPath = "";
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("group_path", out var gpEl) && gpEl.ValueKind == JsonValueKind.String)
                groupPath = gpEl.GetString() ?? "";

            // MultilingualText cultures must exist in the project; resolve from the live project.
            string culture = "zh-CN";
            try { culture = await _tia.ResolveProjectCultureAsync().ConfigureAwait(false); }
            catch { /* keep the default culture when unavailable */ }

            // --- Step 1: compose offline (no TIA/COM) ---
            string xml;
            string blockName;
            try
            {
                switch (kind)
                {
                    case "ladfc":
                        xml = ExtractComposedXml(PlcBuilderToolJson.ComposeLadFcBlock(ladJson, culture));
                        break;
                    case "ladfb":
                        xml = ExtractComposedXml(PlcBuilderToolJson.ComposeLadFbBlock(ladJson, culture));
                        break;
                    default:
                        return (null, $"Unsupported LAD kind: {kind}");
                }
                blockName = ExtractLadBlockName(ladJson);
            }
            catch (Exception ex)
            {
                // Offline compose failures are user/content errors (bad JSON contract) — report as-is.
                return (null, $"LAD compose failed: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(xml))
                return (null, "LAD compose produced empty XML.");
            if (string.IsNullOrWhiteSpace(blockName))
                return (null, "The LAD block specification is missing 'blockName'.");

            // --- Step 2: import into TIA on the STA thread ---
            // blockType is "FC"/"FB": ImportBlockCodeCoreAsync routes XML (<?xml/<Document) to the
            // XML import branch regardless, and the block element inside the XML already carries the
            // real SW.Blocks.FC/FB kind.
            string blockType = kind == "ladfb" ? "FB" : "FC";

            // Approval comes after the offline compose (so the card can name the real block and its size)
            // and before anything touches TIA.
            var refusal = await AuthorizeWriteAsync(new TIAOpennessStudio.Services.WriteGate.WriteRequest
            {
                Tool = kind == "ladfb" ? "build_lad_fb" : "build_lad_fc",
                BlockName = blockName,
                BlockType = blockType,
                Code = xml,
                GroupPath = groupPath,
                FormatLabel = "LAD (XML)",
                Rid = rid,
                IsRemote = isRemote
            }).ConfigureAwait(false);
            if (refusal != null)
                return (null, refusal.Message);

            string resultText = null;
            string errorText = null;
            await _runOnTiaSta(async () =>
            {
                try { resultText = await _tia.ImportBlockCodeAsync(blockName, blockType, xml, groupPath).ConfigureAwait(false); }
                catch (Exception ex) { errorText = ex.Message; }
            }).ConfigureAwait(false);

            if (errorText != null)
                return (null, errorText);
            return ($"[LAD {blockType}] '{blockName}' built and imported.\n{resultText}", null);
        }

        /// <summary>
        /// Compose a S7-GRAPH FB (step sequence) from the builder JSON contract, import it, and run a
        /// block-level compile gate. Compose failures are content errors; import/compile failures are
        /// reported as tool errors so the caller never treats a broken block as successfully generated.
        /// </summary>
        private async Task<(string resultText, string errorText)> HandleBuildGraphAsync(
            JsonElement arguments, string rid, bool isRemote)
        {
            string graphJson = null;
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("json", out var jsonEl))
                graphJson = jsonEl.ValueKind == JsonValueKind.String ? jsonEl.GetString() : jsonEl.GetRawText();
            else if (arguments.ValueKind == JsonValueKind.Object)
                graphJson = arguments.GetRawText();

            if (string.IsNullOrWhiteSpace(graphJson))
                return (null, "A 'json' argument carrying the GRAPH block specification is required.");

            string groupPath = "";
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("group_path", out var gpEl) && gpEl.ValueKind == JsonValueKind.String)
                groupPath = gpEl.GetString() ?? "";

            string culture = "zh-CN";
            try { culture = await _tia.ResolveProjectCultureAsync().ConfigureAwait(false); }
            catch { /* keep the default culture when the project/language is unavailable */ }

            string xml;
            string blockName;
            string schemaNote;
            try
            {
                var composed = GraphBlockToolJson.ComposeGraphFbBlock(graphJson, culture);
                xml = ExtractComposedXml(composed);
                schemaNote = DescribeGraphSchema(composed);
                blockName = ExtractLadBlockName(graphJson);
            }
            catch (Exception ex)
            {
                return (null, $"GRAPH compose failed: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(xml)) return (null, "GRAPH compose produced empty XML.");
            if (string.IsNullOrWhiteSpace(blockName)) return (null, "The GRAPH block specification is missing 'blockName'.");

            var refusal = await AuthorizeWriteAsync(new TIAOpennessStudio.Services.WriteGate.WriteRequest
            {
                Tool = "build_graph_fb",
                BlockName = blockName,
                BlockType = "FB",
                Code = xml,
                GroupPath = groupPath,
                FormatLabel = "GRAPH (XML)",
                Rid = rid,
                IsRemote = isRemote
            }).ConfigureAwait(false);
            if (refusal != null) return (null, refusal.Message);

            string resultText = null;
            string errorText = null;
            await _runOnTiaSta(async () =>
            {
                try { resultText = await _tia.ImportBlockCodeAsync(blockName, "FB", xml, groupPath).ConfigureAwait(false); }
                catch (Exception ex) { errorText = ex.Message; }
            }).ConfigureAwait(false);
            if (errorText != null) return (null, errorText);

            string compileNote = "compile: skipped";
            string compileError = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    var compile = await _tia.CompileBlockAsync(blockName).ConfigureAwait(false);
                    if (compile == null)
                    {
                        compileNote = "compile: no result";
                        return;
                    }
                    if (compile.Success)
                    {
                        compileNote = "compile: ok";
                        return;
                    }

                    var messages = (compile.Errors ?? new List<CompilationError>())
                        .Select(e => e?.Message ?? "")
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Take(5)
                        .ToList();
                    compileNote = "compile: failed (" + (compile.Errors?.Count ?? 0) + " error(s))";
                    compileError = "GRAPH block '" + blockName + "' was imported but block compile failed: " +
                                   (messages.Count > 0 ? string.Join("; ", messages) : (compile.ErrorMessage ?? "unknown error"));
                }
                catch (Exception ex)
                {
                    // Compile is a gentle gate: an unavailable compile must not turn a successful import into a failure.
                    compileNote = "compile: unavailable (" + ex.Message + ")";
                }
            }).ConfigureAwait(false);

            if (compileError != null)
                return ($"[GRAPH FB] '{blockName}' built and imported.\n{resultText}\n{compileNote}\n{schemaNote}", compileError);

            return ($"[GRAPH FB] '{blockName}' built and imported.\n{resultText}\n{compileNote}\n{schemaNote}", null);
        }

        /// <summary>Describe the resolved GRAPH schema/template (and any provisional fallback) for the tool result.</summary>
        private static string DescribeGraphSchema(JsonObject composed)
        {
            var schema = composed?["schema"] as JsonObject;
            if (schema == null) return "";

            var major = schema["tiaMajor"]?.ToString() ?? "?";
            var template = schema["template"]?.ToString() ?? "?";
            var verified = schema["verified"] is JsonValue verifiedValue &&
                           verifiedValue.TryGetValue<bool>(out var isVerified) && isVerified;
            var warnings = (schema["warnings"] as JsonArray)?
                .Select(x => x?.ToString() ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray() ?? Array.Empty<string>();

            var text = $"schema: TIA V{major}, template={template}, verified={verified.ToString().ToLowerInvariant()}";
            if (warnings.Length > 0)
                text += "; warnings: " + string.Join(" | ", warnings);
            return text;
        }

        /// <summary>Pull the composed XML string out of the PlcBuilderToolJson result envelope.</summary>
        private static string ExtractComposedXml(System.Text.Json.Nodes.JsonObject result)
        {
            if (result == null) return null;
            if (result["ok"] is System.Text.Json.Nodes.JsonValue okVal && okVal.TryGetValue<bool>(out var ok) && !ok)
            {
                var err = result["error"]?.ToString();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? "offline XML build reported not-ok" : err);
            }
            return result["xml"]?.ToString();
        }

        /// <summary>Read blockName (or name) from the LAD JSON contract.</summary>
        private static string ExtractLadBlockName(string ladJson)
        {
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(ladJson) as System.Text.Json.Nodes.JsonObject;
                return root?["blockName"]?.ToString() ?? root?["name"]?.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Extract the UDT name from a build_udt JSON spec (name / udtName).</summary>
        private static string ExtractUdtBlockName(string json)
        {
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
                return root?["name"]?.ToString() ?? root?["udtName"]?.ToString();
            }
            catch { return null; }
        }

        /// <summary>Extract the global DB name from a build_global_db JSON spec (dbName / name).</summary>
        private static string ExtractDbBlockName(string json)
        {
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
                return root?["dbName"]?.ToString() ?? root?["name"]?.ToString();
            }
            catch { return null; }
        }

        /// <summary>Compose a PLC UDT (PLC data type) from a JSON spec and import it into TIA. Write gate + STA.</summary>
        private async Task<(string resultText, string errorText)> HandleBuildUdtAsync(JsonElement arguments, string rid, bool isRemote)
        {
            string jsonText = null;
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("json", out var jsonEl))
                jsonText = jsonEl.ValueKind == JsonValueKind.String ? jsonEl.GetString() : jsonEl.GetRawText();
            else if (arguments.ValueKind == JsonValueKind.Object)
                jsonText = arguments.GetRawText();

            if (string.IsNullOrWhiteSpace(jsonText))
                return (null, "A 'json' argument carrying the UDT spec is required.");

            string groupPath = "";
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("group_path", out var gpEl) && gpEl.ValueKind == JsonValueKind.String)
                groupPath = gpEl.GetString() ?? "";

            // MultilingualText cultures must exist in the project, so resolve from the live project
            // instead of hardcoding (a hardcoded culture fails on projects that don't have it).
            string culture = "zh-CN";
            try { culture = await _tia.ResolveProjectCultureAsync().ConfigureAwait(false); }
            catch { /* keep the default culture when the project/language is unavailable */ }

            string xml;
            string blockName;
            try
            {
                xml = ExtractComposedXml(PlcBuilderToolJson.BuildUdt(jsonText, culture));
                blockName = ExtractUdtBlockName(jsonText);
            }
            catch (Exception ex)
            {
                return (null, $"UDT compose failed: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(xml)) return (null, "UDT compose produced empty XML.");
            if (string.IsNullOrWhiteSpace(blockName)) return (null, "The UDT spec is missing 'name'.");

            var refusal = await AuthorizeWriteAsync(new TIAOpennessStudio.Services.WriteGate.WriteRequest
            {
                Tool = "build_udt",
                BlockName = blockName,
                BlockType = "UDT",
                Code = xml,
                GroupPath = groupPath,
                FormatLabel = "UDT (XML)",
                Rid = rid,
                IsRemote = isRemote
            }).ConfigureAwait(false);
            if (refusal != null) return (null, refusal.Message);

            string resultText = null;
            string errorText = null;
            await _runOnTiaSta(async () =>
            {
                try { resultText = await _tia.ImportBlockCodeAsync(blockName, "UDT", xml, groupPath).ConfigureAwait(false); }
                catch (Exception ex) { errorText = ex.Message; }
            }).ConfigureAwait(false);

            if (errorText != null) return (null, errorText);
            return ($"[UDT] '{blockName}' built and imported.\n{resultText}", null);
        }

        /// <summary>Compose a PLC global DB from a JSON spec and import it into TIA. Write gate + STA.</summary>
        private async Task<(string resultText, string errorText)> HandleBuildGlobalDbAsync(JsonElement arguments, string rid, bool isRemote)
        {
            string jsonText = null;
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("json", out var jsonEl))
                jsonText = jsonEl.ValueKind == JsonValueKind.String ? jsonEl.GetString() : jsonEl.GetRawText();
            else if (arguments.ValueKind == JsonValueKind.Object)
                jsonText = arguments.GetRawText();

            if (string.IsNullOrWhiteSpace(jsonText))
                return (null, "A 'json' argument carrying the global DB spec is required.");

            string groupPath = "";
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("group_path", out var gpEl) && gpEl.ValueKind == JsonValueKind.String)
                groupPath = gpEl.GetString() ?? "";

            string xml;
            string blockName;
            var memoryLayout = ReadStringArg(arguments, "memory_layout");
            try
            {
                xml = ExtractComposedXml(PlcBuilderToolJson.BuildGlobalDb(jsonText, memoryLayout));
                blockName = ExtractDbBlockName(jsonText);
            }
            catch (Exception ex)
            {
                return (null, $"Global DB compose failed: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(xml)) return (null, "Global DB compose produced empty XML.");
            if (string.IsNullOrWhiteSpace(blockName)) return (null, "The global DB spec is missing 'dbName'.");

            var refusal = await AuthorizeWriteAsync(new TIAOpennessStudio.Services.WriteGate.WriteRequest
            {
                Tool = "build_global_db",
                BlockName = blockName,
                BlockType = "DB",
                Code = xml,
                GroupPath = groupPath,
                FormatLabel = "DB (XML)",
                Rid = rid,
                IsRemote = isRemote
            }).ConfigureAwait(false);
            if (refusal != null) return (null, refusal.Message);

            string resultText = null;
            string errorText = null;
            await _runOnTiaSta(async () =>
            {
                try { resultText = await _tia.ImportBlockCodeAsync(blockName, "DB", xml, groupPath).ConfigureAwait(false); }
                catch (Exception ex) { errorText = ex.Message; }
            }).ConfigureAwait(false);

            if (errorText != null) return (null, errorText);
            return ($"[DB] '{blockName}' built and imported.\n{resultText}", null);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Patch tools: modify an existing block in place (never rebuild)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Patch ONE network body of an existing block. Only the target CompileUnit's NetworkSource
        /// changes; block attributes, other networks and ObjectLists are preserved. Revision and
        /// source-profile preconditions are enforced by <see cref="BlockPatchService"/>.
        /// </summary>
        private async Task<(string resultText, string errorText)> HandlePatchBlockNetworkAsync(
            JsonElement arguments, string rid, bool isRemote)
        {
            var blockName = ReadStringArg(arguments, "block_name");
            var baseRevision = ReadStringArg(arguments, "base_revision");
            var compileUnitId = ReadStringArg(arguments, "compile_unit_id");
            var networkOrder = ReadIntArg(arguments, "network_order");
            var bodyJson = ReadRawArg(arguments, "body");

            if (string.IsNullOrWhiteSpace(blockName)) return (null, "block_name is required");
            if (string.IsNullOrWhiteSpace(baseRevision))
                return (null, "base_revision is required (use the revision returned by get_block_content).");
            if (string.IsNullOrWhiteSpace(bodyJson))
                return (null, "body is required (networks[].body from get_block_content format=ir).");

            string xml = null;
            string exportError = null;
            await _runOnTiaSta(async () =>
            {
                try { xml = await _tia.ExportBlockCodeAsync(blockName).ConfigureAwait(false); }
                catch (Exception ex) { exportError = ex.Message; }
            }).ConfigureAwait(false);
            if (xml == null)
                return (null, exportError ?? ("Block '" + blockName + "' not found in project."));

            var patched = await Task.Run(() =>
            {
                BlockSnapshot snapshot = null;
                BlockPatchResult result = null;
                try
                {
                    snapshot = BlockSnapshotService.FromXml(xml, null, null);
                    result = BlockPatchService.PatchNetwork(snapshot, baseRevision, compileUnitId, networkOrder, bodyJson);
                }
                catch (Exception ex)
                {
                    result = BlockPatchResult.Fail("invalid_snapshot", ex.Message);
                }
                return (snapshot, result);
            }).ConfigureAwait(false);

            var snapshotResult = patched.snapshot;
            var patch = patched.result;
            if (patch == null || !patch.Ok) return (null, PatchErrorMessage(patch));
            return await ImportPatchAsync(blockName, snapshotResult, patch, "patch_block_network",
                "Network patch (XML)", "'" + patch.TargetId + "' network patched", rid, isRemote).ConfigureAwait(false);
        }

        /// <summary>
        /// Patch ONLY the interface of an existing block/type (DB/UDT/FB/FC). Networks, block
        /// attributes (MemoryLayout, InstanceOfName, ...) and ObjectLists are preserved.
        /// </summary>
        private async Task<(string resultText, string errorText)> HandlePatchBlockInterfaceAsync(
            JsonElement arguments, string rid, bool isRemote)
        {
            var blockName = ReadStringArg(arguments, "block_name");
            var baseRevision = ReadStringArg(arguments, "base_revision");
            var interfaceJson = ReadRawArg(arguments, "interface");

            if (string.IsNullOrWhiteSpace(blockName)) return (null, "block_name is required");
            if (string.IsNullOrWhiteSpace(baseRevision))
                return (null, "base_revision is required (use the revision returned by get_block_content).");
            if (string.IsNullOrWhiteSpace(interfaceJson))
                return (null, "interface is required (the Interface tree from get_block_content format=interface).");

            string xml = null;
            string exportError = null;
            await _runOnTiaSta(async () =>
            {
                try { xml = await _tia.ExportBlockCodeAsync(blockName).ConfigureAwait(false); }
                catch (Exception ex) { exportError = ex.Message; }
            }).ConfigureAwait(false);
            if (xml == null)
                return (null, exportError ?? ("Block '" + blockName + "' not found in project."));

            var patched = await Task.Run(() =>
            {
                BlockSnapshot snapshot = null;
                BlockPatchResult result = null;
                try
                {
                    snapshot = BlockSnapshotService.FromXml(xml, null, null);
                    result = BlockPatchService.PatchInterface(snapshot, baseRevision, interfaceJson);
                }
                catch (Exception ex)
                {
                    result = BlockPatchResult.Fail("invalid_snapshot", ex.Message);
                }
                return (snapshot, result);
            }).ConfigureAwait(false);

            var snapshotResult = patched.snapshot;
            var patch = patched.result;
            if (patch == null || !patch.Ok) return (null, PatchErrorMessage(patch));
            return await ImportPatchAsync(blockName, snapshotResult, patch, "patch_block_interface",
                "Interface patch (XML)", "interface patched", rid, isRemote).ConfigureAwait(false);
        }

        /// <summary>Resolve the current group path, authorize, then import the patched XML in place.</summary>
        private async Task<(string resultText, string errorText)> ImportPatchAsync(
            string blockName, BlockSnapshot snapshot, BlockPatchResult patch,
            string tool, string formatLabel, string summary, string rid, bool isRemote)
        {
            var groupPath = await ResolvePatchGroupPathAsync(blockName, snapshot).ConfigureAwait(false);
            if (groupPath == null)
            {
                return (null,
                    "Cannot resolve the block's current group path; refusing to patch "
                    + "(re-importing at the root would relocate it).");
            }

            var refusal = await AuthorizeWriteAsync(new TIAOpennessStudio.Services.WriteGate.WriteRequest
            {
                Tool = tool,
                BlockName = blockName,
                BlockType = snapshot?.BlockType ?? "",
                Code = patch.PatchedXml,
                GroupPath = groupPath,
                FormatLabel = formatLabel,
                Rid = rid,
                IsRemote = isRemote
            }).ConfigureAwait(false);
            if (refusal != null) return (null, refusal.Message);

            string importResult = null;
            string importError = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    importResult = await _tia.ImportBlockCodeAsync(
                        blockName, snapshot?.BlockType ?? "", patch.PatchedXml, groupPath).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    importError = ex.Message;
                }
            }).ConfigureAwait(false);
            if (importError != null) return (null, importError);

            // A fresh import leaves the block "inconsistent (needs compile)" and TIA refuses to export
            // it, so the very next get_block_content would fail. Compile the patched block here so the
            // patch → read round trip works, and surface the compiler verdict to the caller.
            string compileNote;
            string compileError = null;
            if (string.Equals(snapshot?.BlockType, "UDT", StringComparison.OrdinalIgnoreCase))
            {
                // Openness has no single-type compile, and an imported type stays "inconsistent"
                // (unexportable) until something compiles it. A project compile is the only entry
                // point, so run it here; its verdict is reported but never turns the patch into a
                // failure (unrelated pre-existing errors must not mask a good patch).
                compileNote = "compile: project compile skipped";
                await _runOnTiaSta(async () =>
                {
                    try
                    {
                        var compile = await _tia.GetCompilationResultAsync().ConfigureAwait(false);
                        if (compile == null)
                        {
                            compileNote = "compile: project compile returned no result; "
                                          + "run compile before reading the type again";
                            return;
                        }
                        var errorCount = compile.Errors?.Count ?? 0;
                        compileNote = compile.Success
                            ? "compile: project compile ok (the type is exportable again)"
                            : $"compile: project compile finished with {errorCount} error(s); "
                              + "the type stays inconsistent only if one of them belongs to it";
                    }
                    catch (Exception ex)
                    {
                        compileNote = "compile: project compile unavailable (" + ex.Message + "); "
                                      + "run compile before reading the type again";
                    }
                }).ConfigureAwait(false);
            }
            else
            {
                compileNote = "compile: skipped";
                await _runOnTiaSta(async () =>
                {
                    try
                    {
                        var compile = await _tia.CompileBlockAsync(blockName).ConfigureAwait(false);
                        if (compile == null)
                        {
                            compileNote = "compile: no result";
                            return;
                        }
                        if (compile.Success)
                        {
                            compileNote = "compile: ok";
                            return;
                        }

                        var messages = (compile.Errors ?? new List<CompilationError>())
                            .Select(e => e?.Message ?? "")
                            .Where(m => !string.IsNullOrWhiteSpace(m))
                            .Take(5)
                            .ToList();
                        compileNote = "compile: failed (" + (compile.Errors?.Count ?? 0) + " error(s))";
                        compileError = "'" + blockName + "' was patched but block compile failed: "
                                       + (messages.Count > 0
                                           ? string.Join("; ", messages)
                                           : (compile.ErrorMessage ?? "unknown error"));
                    }
                    catch (Exception ex)
                    {
                        // Compile is a gentle gate: an unavailable compile must not turn a good patch into a failure.
                        compileNote = "compile: unavailable (" + ex.Message + ")";
                    }
                }).ConfigureAwait(false);
            }

            var patchedText = $"[PATCH] {summary} for '{blockName}' (revision {snapshot?.Revision}).\n{importResult}\n{compileNote}";
            return (patchedText, compileError);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Type-level repair: an inconsistent block/type cannot be exported, so the only
        // lossless source is the last indexed snapshot. The interface is patched (everything
        // else stays byte-identical), imported, and the project is compiled once.
        // ─────────────────────────────────────────────────────────────────────

        private sealed class InconsistentTarget
        {
            public string BlockId { get; set; }
            public string Name { get; set; }
            public string BlockType { get; set; }
            public string Message { get; set; }
        }

        // Snapshot ids with these prefixes are not program blocks/types that can be repaired.
        private static readonly string[] NonRepairableSnapshotPrefixes =
            { "TagTable", "Tag", "BlockGroup", "TypeGroup" };

        private static string MapSnapshotPrefixToBlockType(string prefix)
        {
            if (string.Equals(prefix, "UDT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(prefix, "PlcType", StringComparison.OrdinalIgnoreCase))
                return "UDT";
            if (prefix.IndexOf("DB", StringComparison.OrdinalIgnoreCase) >= 0)
                return "DB";
            return prefix.ToUpperInvariant();
        }

        /// <summary>Candidates = snapshot ids whose live export fails with "inconsistent".</summary>
        private async Task<List<InconsistentTarget>> ProbeInconsistentTargetsAsync(string projectKey)
        {
            var found = new List<InconsistentTarget>();
            if (_projectIndex == null || string.IsNullOrWhiteSpace(projectKey)) return found;

            foreach (var id in _projectIndex.ListSnapshotBlockIds(projectKey))
            {
                var separator = id.IndexOf(':');
                if (separator <= 0 || separator >= id.Length - 1) continue;
                var prefix = id.Substring(0, separator);
                if (NonRepairableSnapshotPrefixes.Any(p => string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var name = id.Substring(separator + 1);

                string error = null;
                await _runOnTiaSta(async () =>
                {
                    try { await _tia.ExportBlockCodeAsync(name).ConfigureAwait(false); }
                    catch (Exception ex) { error = ex.Message; }
                }).ConfigureAwait(false);

                if (error == null
                    || error.IndexOf("inconsistent", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                found.Add(new InconsistentTarget
                {
                    BlockId = id,
                    Name = name,
                    BlockType = MapSnapshotPrefixToBlockType(prefix),
                    Message = error
                });
            }
            return found;
        }

        /// <summary>
        /// Rewrite only Member Datatype attributes according to the caller's replacement map.
        /// Refuses nested structs and members with a start value: those cannot be retyped by a
        /// blind swap without changing meaning.
        /// </summary>
        private static JsonObject TransformInterfaceDatatypes(
            string interfaceJson, JsonObject replacements, out int changed, out string refuseReason)
        {
            changed = 0;
            refuseReason = null;

            JsonObject root;
            try { root = JsonNode.Parse(interfaceJson) as JsonObject; }
            catch (Exception ex)
            {
                refuseReason = "interface projection unreadable: " + ex.Message;
                return null;
            }
            if (root == null)
            {
                refuseReason = "interface projection is empty";
                return null;
            }

            foreach (var member in EnumerateInterfaceMembers(root))
            {
                var attrs = member["attrs"] as JsonObject;
                if (attrs == null) continue;
                var name = attrs["Name"]?.ToString() ?? "?";
                var datatype = attrs["Datatype"]?.ToString();
                if (string.IsNullOrWhiteSpace(datatype)) continue;

                var nested = (member["children"] as JsonArray)?.Any(child =>
                    string.Equals(child?["kind"]?.ToString(), "Member", StringComparison.OrdinalIgnoreCase)) == true;
                if (nested)
                {
                    refuseReason = "member '" + name + "' is a nested struct; refusing automatic retype";
                    return null;
                }
                foreach (var startKey in new[] { "StartValue", "InitialValue" })
                {
                    if (attrs[startKey] != null && !string.IsNullOrWhiteSpace(attrs[startKey].ToString()))
                    {
                        refuseReason = "member '" + name + "' has a start value; refusing automatic retype";
                        return null;
                    }
                }

                var replacement = replacements[datatype]?.ToString();
                if (string.IsNullOrWhiteSpace(replacement)) continue;
                attrs["Datatype"] = replacement;
                changed++;
            }
            return root;
        }

        private static IEnumerable<JsonObject> EnumerateInterfaceMembers(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (string.Equals(obj["kind"]?.ToString(), "Member", StringComparison.OrdinalIgnoreCase))
                    yield return obj;
                if (obj["children"] is JsonArray children)
                    foreach (var child in children)
                        foreach (var nested in EnumerateInterfaceMembers(child))
                            yield return nested;
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array)
                    foreach (var nested in EnumerateInterfaceMembers(child))
                        yield return nested;
            }
        }

        /// <summary>Project key read on the TIA STA thread (a plain call can see an empty profile).</summary>
        private async Task<string> GetProjectKeyAsync()
        {
            string projectKey = null;
            await _runOnTiaSta(async () =>
            {
                try { projectKey = _tia.GetEnvironmentProfile()?.ProjectKey; }
                catch { /* no project: the caller reports it */ }
            }).ConfigureAwait(false);
            return projectKey;
        }

        private async Task<(string resultText, string errorText)> HandleRepairInconsistentTypesAsync(
            JsonElement arguments, string rid, bool isRemote)
        {
            var dryRun = arguments.ValueKind == JsonValueKind.Object
                         && arguments.TryGetProperty("dry_run", out var dryEl)
                         && dryEl.ValueKind == JsonValueKind.True;

            JsonObject replacements = null;
            if (arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty("replacements", out var replEl)
                && replEl.ValueKind == JsonValueKind.Object)
            {
                replacements = JsonNode.Parse(replEl.GetRawText()) as JsonObject;
            }
            if (replacements == null || replacements.Count == 0)
                return (null, "replacements is required: {\"DTL\":\"DATE_AND_TIME\", ...} — build it from the platform matrix.");

            if (_projectIndex == null)
                return (null, "Knowledge index store is unavailable, so snapshots cannot be read.");

            var projectKey = await GetProjectKeyAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(projectKey))
                return (null, "No project connected; open a project first.");

            var targets = await ProbeInconsistentTargetsAsync(projectKey);
            if (targets.Count == 0)
                return ("[REPAIR] no inconsistent block/type found in the current project.", null);

            var repaired = new JsonArray();
            var skipped = new JsonArray();

            foreach (var target in targets)
            {
                var snapshotRow = _projectIndex.GetBlockSnapshot(projectKey, target.BlockId);
                if (snapshotRow == null || string.IsNullOrWhiteSpace(snapshotRow.RawXml))
                {
                    skipped.Add(SkipEntry(target, "no_index_snapshot"));
                    continue;
                }

                BlockSnapshot snapshot;
                try
                {
                    snapshot = BlockSnapshotService.FromXml(snapshotRow.RawXml, target.BlockId, projectKey);
                }
                catch (Exception ex)
                {
                    skipped.Add(SkipEntry(target, "snapshot_unreadable: " + ex.Message));
                    continue;
                }

                string interfaceJson;
                try { interfaceJson = BlockSnapshotService.BuildInterface(snapshot); }
                catch (Exception ex)
                {
                    skipped.Add(SkipEntry(target, "interface_projection_failed: " + ex.Message));
                    continue;
                }

                int changed;
                string refuseReason;
                var patchedInterface = TransformInterfaceDatatypes(interfaceJson, replacements, out changed, out refuseReason);
                if (patchedInterface == null)
                {
                    skipped.Add(SkipEntry(target, refuseReason));
                    continue;
                }
                if (changed == 0)
                {
                    skipped.Add(SkipEntry(target, "no matrix replacement applies to its members"));
                    continue;
                }

                var patch = BlockPatchService.PatchInterface(snapshot, snapshot.Revision, patchedInterface.ToJsonString());
                if (patch == null || !patch.Ok)
                {
                    skipped.Add(SkipEntry(target, "interface_patch_failed: " + (patch?.Message ?? "unknown")));
                    continue;
                }

                if (dryRun)
                {
                    repaired.Add(new JsonObject
                    {
                        ["block_id"] = target.BlockId,
                        ["replaced"] = changed,
                        ["dry_run"] = true
                    });
                    continue;
                }

                var groupPath = await ResolvePatchGroupPathAsync(target.Name, snapshot).ConfigureAwait(false);
                if (groupPath == null)
                {
                    skipped.Add(SkipEntry(target, "group_path_unresolved"));
                    continue;
                }

                var refusal = await AuthorizeWriteAsync(new TIAOpennessStudio.Services.WriteGate.WriteRequest
                {
                    Tool = "repair_inconsistent_types",
                    BlockName = target.Name,
                    BlockType = target.BlockType,
                    Code = patch.PatchedXml,
                    GroupPath = groupPath,
                    FormatLabel = "Type repair (XML)",
                    Rid = rid,
                    IsRemote = isRemote
                }).ConfigureAwait(false);
                if (refusal != null)
                {
                    skipped.Add(SkipEntry(target, "write_refused: " + refusal.Message));
                    continue;
                }

                string importError = null;
                await _runOnTiaSta(async () =>
                {
                    try
                    {
                        await _tia.ImportBlockCodeAsync(target.Name, target.BlockType, patch.PatchedXml, groupPath)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) { importError = ex.Message; }
                }).ConfigureAwait(false);
                if (importError != null)
                {
                    skipped.Add(SkipEntry(target, "import_failed: " + importError));
                    continue;
                }

                repaired.Add(new JsonObject
                {
                    ["block_id"] = target.BlockId,
                    ["replaced"] = changed
                });
            }

            List<InconsistentTarget> stillInconsistent = null;
            JsonObject compileInfo = null;
            if (!dryRun && repaired.Count > 0)
            {
                CompilationResult compile = null;
                await _runOnTiaSta(async () =>
                {
                    try { compile = await _tia.GetCompilationResultAsync().ConfigureAwait(false); }
                    catch { /* report zero information rather than failing the repair */ }
                }).ConfigureAwait(false);
                if (compile != null)
                {
                    compileInfo = new JsonObject
                    {
                        ["success"] = compile.Success,
                        ["error_count"] = compile.Errors?.Count ?? 0
                    };
                }
                stillInconsistent = await ProbeInconsistentTargetsAsync(projectKey);
            }

            var result = new JsonObject
            {
                ["candidates"] = targets.Count,
                ["repaired"] = repaired,
                ["skipped"] = skipped,
                ["still_inconsistent"] = new JsonArray(
                    (stillInconsistent ?? new List<InconsistentTarget>())
                        .Select(t => (JsonNode)new JsonObject { ["block_id"] = t.BlockId, ["message"] = t.Message })
                        .ToArray())
            };
            if (compileInfo != null) result["compile"] = compileInfo;
            return (result.ToJsonString(), null);
        }

        private static JsonObject SkipEntry(InconsistentTarget target, string reason)
        {
            return new JsonObject
            {
                ["block_id"] = target.BlockId,
                ["reason"] = reason
            };
        }

        private async Task<string> ResolvePatchGroupPathAsync(string blockName, BlockSnapshot snapshot)
        {
            string path = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    path = string.Equals(snapshot?.BlockType, "UDT", StringComparison.OrdinalIgnoreCase)
                        ? await _tia.GetTypeGroupPathAsync(blockName).ConfigureAwait(false)
                        : await _tia.GetBlockGroupPathAsync(blockName).ConfigureAwait(false);
                }
                catch
                {
                    path = null;
                }
            }).ConfigureAwait(false);
            return path;
        }

        private static string PatchErrorMessage(BlockPatchResult patch)
        {
            if (patch == null) return "Patch failed.";
            var code = string.IsNullOrEmpty(patch.ErrorCode) ? "patch_failed" : patch.ErrorCode;
            return code + ": " + patch.Message;
        }

        private static int ReadIntArg(JsonElement arguments, string name)
        {
            return arguments.ValueKind == JsonValueKind.Object
                   && arguments.TryGetProperty(name, out var element)
                   && element.ValueKind == JsonValueKind.Number
                   && element.TryGetInt32(out var value)
                ? value
                : 0;
        }

        private static string ReadRawArg(JsonElement arguments, string name)
        {
            if (arguments.ValueKind != JsonValueKind.Object) return null;
            if (!arguments.TryGetProperty(name, out var element)) return null;
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }

        /// <summary>
        /// Build a valid organization block (OB) and import it. An OB cannot come from an SCL external
        /// source (TIA rejects the number and the 20-byte interface), so: probe name/number, tokenize the
        /// SCL body through a temporary FC (TIA's own StructuredText form), compose the OB envelope and
        /// import it. The temp FC always gets deleted.
        /// </summary>
        private async Task<(string resultText, string errorText)> HandleBuildObAsync(JsonElement arguments, string rid, bool isRemote)
        {
            string jsonText = null;
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("json", out var jsonEl))
                jsonText = jsonEl.ValueKind == JsonValueKind.String ? jsonEl.GetString() : jsonEl.GetRawText();
            else if (arguments.ValueKind == JsonValueKind.Object)
                jsonText = arguments.GetRawText();
            if (string.IsNullOrWhiteSpace(jsonText))
                return (null, "A 'json' argument carrying the OB spec is required.");

            string groupPath = "";
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("group_path", out var gpEl) && gpEl.ValueKind == JsonValueKind.String)
                groupPath = gpEl.GetString() ?? "";

            JsonNode root;
            try { root = JsonNode.Parse(jsonText); }
            catch (Exception ex) { return (null, $"OB spec is not valid JSON: {ex.Message}"); }
            if (!(root is JsonObject obj)) return (null, "OB spec must be a JSON object.");

            string obName = obj["blockName"]?.ToString() ?? obj["obName"]?.ToString() ?? obj["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(obName)) return (null, "The OB spec is missing 'blockName'.");

            int number;
            if (!(int.TryParse(obj["obNumber"]?.ToString() ?? obj["number"]?.ToString(), out number) && number > 0))
                return (null, "The OB spec is missing a legal 'obNumber' (1, 30-38 or 100-102).");
            string secondary = PlcObXmlBuilder.SecondaryTypeFor(number);
            if (secondary == null)
                return (null, $"OB number {number} is not supported. Use 1 (program cycle), 30-38 (cyclic interrupt) or 100-102 (startup).");
            if (secondary == "Startup")
                return (null, "Startup OBs (100-102) are not supported yet (no verified start-information template).");

            string language = (obj["language"]?.ToString() ?? "SCL").ToUpperInvariant();
            if (language != "SCL")
                return (null, "Only language='SCL' is supported for OB bodies right now.");

            string body = obj["body"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(body))
                return (null, "The OB spec is missing 'body' (SCL statements).");

            var temps = new List<(string Name, string Datatype)>();
            if (obj["temps"] is JsonArray tempsArr)
            {
                foreach (var t in tempsArr)
                {
                    if (!(t is JsonObject to)) continue;
                    string tn = to["name"]?.ToString();
                    string td = to["datatype"]?.ToString() ?? to["dataType"]?.ToString() ?? "Bool";
                    if (!string.IsNullOrWhiteSpace(tn)) temps.Add((tn, td));
                }
            }

            bool allowOverwrite = obj["allow_overwrite"] is JsonValue aov && aov.TryGetValue(out bool ao) && ao;

            // Probe existing blocks (read-only) so the user is never surprised by an overwrite/number clash.
            bool nameExists = false, numberTaken = false;
            string takenBy = "";
            await _runOnTiaSta(async () =>
            {
                try
                {
                    var tree = await _tia.GetBlockTreeAsync().ConfigureAwait(false);
                    void Walk(PlcTreeNodeDto n)
                    {
                        if (n?.Children == null) return;
                        foreach (var c in n.Children)
                        {
                            if (c == null) continue;
                            if (c.Kind == PlcTreeNodeKind.Block)
                            {
                                if (string.Equals(c.Name, obName, StringComparison.Ordinal)) nameExists = true;
                                if (c.Number == number && !string.Equals(c.Name, obName, StringComparison.Ordinal))
                                {
                                    numberTaken = true;
                                    takenBy = c.Name;
                                }
                            }
                            else Walk(c);
                        }
                    }
                    Walk(tree);
                }
                catch { /* probe is best-effort; TIA still validates the import */ }
            }).ConfigureAwait(false);

            if (numberTaken)
                return (null, $"build_ob: OB number {number} is already used by '{takenBy}'. Pick a free number.");
            if (nameExists && !allowOverwrite)
                return (null, $"build_ob: a block named '{obName}' already exists. Pass allow_overwrite=true to replace it.");

            string culture = "zh-CN";
            try { culture = await _tia.ResolveProjectCultureAsync().ConfigureAwait(false); } catch { }

            // Authorize BEFORE touching the project: the temp tokenizer FC below is an implementation detail.
            var refusal = await AuthorizeWriteAsync(new TIAOpennessStudio.Services.WriteGate.WriteRequest
            {
                Tool = "build_ob",
                BlockName = obName,
                BlockType = "OB",
                Code = body,
                GroupPath = groupPath,
                FormatLabel = "OB (SCL body + generated XML envelope)",
                Rid = rid,
                IsRemote = isRemote
            }).ConfigureAwait(false);
            if (refusal != null) return (null, refusal.Message);

            string tmpName = "ZZ_TmpOb_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string tmpScl = BuildTempFcScl(tmpName, temps, body);
            string networkInner = null, tokenizeError = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    await _tia.ImportBlockCodeAsync(tmpName, "FC", tmpScl, "").ConfigureAwait(false);
                    string xml = await _tia.ExportBlockCodeAsync(tmpName).ConfigureAwait(false);
                    var m = System.Text.RegularExpressions.Regex.Match(
                        xml, "<NetworkSource>(.*?)</NetworkSource>",
                        System.Text.RegularExpressions.RegexOptions.Singleline);
                    if (m.Success) networkInner = m.Groups[1].Value;
                    else tokenizeError = "tokenizer FC export had no NetworkSource";
                }
                catch (Exception ex) { tokenizeError = ex.Message; }
                finally
                {
                    try { await _tia.DeleteBlockAsync(tmpName).ConfigureAwait(false); } catch { }
                }
            }).ConfigureAwait(false);

            if (tokenizeError != null || string.IsNullOrWhiteSpace(networkInner))
                return (null, $"build_ob: could not tokenize the SCL body: {tokenizeError ?? "empty network"}");

            string xmlOut;
            try
            {
                xmlOut = PlcObXmlBuilder.BuildXml(
                    obName, number, language, temps, networkInner, culture,
                    obj["titleZhCn"]?.ToString() ?? "", obj["commentZhCn"]?.ToString() ?? "");
            }
            catch (Exception ex) { return (null, $"OB compose failed: {ex.Message}"); }

            string resultText = null, errorText = null;
            await _runOnTiaSta(async () =>
            {
                try { resultText = await _tia.ImportBlockCodeAsync(obName, "OB", xmlOut, groupPath).ConfigureAwait(false); }
                catch (Exception ex) { errorText = ex.Message; }
            }).ConfigureAwait(false);

            if (errorText != null) return (null, errorText);
            return ($"[OB] '{obName}' built and imported.\n{resultText}", null);
        }

        private static string BuildTempFcScl(string name, List<(string Name, string Datatype)> temps, string body)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FUNCTION \"{name}\" : Void");
            sb.AppendLine("VERSION : 0.1");
            if (temps.Count > 0)
            {
                sb.AppendLine("VAR_TEMP");
                foreach (var t in temps) sb.AppendLine($"  {t.Name} : {t.Datatype};");
                sb.AppendLine("END_VAR");
            }
            sb.AppendLine("BEGIN");
            sb.AppendLine(body);
            sb.AppendLine("END_FUNCTION");
            return sb.ToString();
        }

        /// <summary>Offline-compose an HMI screen / tag-table artifact and return its XML/JSON. No TIA, no write gateway.</summary>
        private (string resultText, string errorText) HandleBuildHmiAsync(JsonElement arguments, string kind)
        {
            if (_hmiBuilder == null)
                return (null, "HMI builder is not available.");

            string jsonText = null;
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("json", out var jsonEl))
                jsonText = jsonEl.ValueKind == JsonValueKind.String ? jsonEl.GetString() : jsonEl.GetRawText();
            else if (arguments.ValueKind == JsonValueKind.Object)
                jsonText = arguments.GetRawText();

            if (string.IsNullOrWhiteSpace(jsonText))
                return (null, "A 'json' argument carrying the HMI design spec is required.");

            string target = ReadStringArg(arguments, "target") ?? "classic";
            string engineeringVersion = ResolveHmiEngineeringVersion(ReadStringArg(arguments, "tia_version"));

            try
            {
                var result = kind == "screen"
                    ? _hmiBuilder.BuildHmiScreen(jsonText, target, engineeringVersion)
                    : _hmiBuilder.BuildHmiTagTable(jsonText, target, engineeringVersion);
                return (result?.ToJsonString() ?? "{}", null);
            }
            catch (Exception ex)
            {
                return (null, $"HMI compose failed ({kind}): {ex.Message}");
            }
        }

        private static string ReadStringArg(JsonElement arguments, string name)
        {
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
            return null;
        }

        private static string ResolveHmiEngineeringVersion(string tiaVersion)
        {
            // Canonical TIA Engineering version header for the v15.1–v18 range (V21 out of scope).
            var v = (tiaVersion ?? "").Trim();
            if (v.StartsWith("V", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
            switch (v)
            {
                case "15.1": return "V15.1";
                case "16": return "V16";
                case "17": return "V17";
                case "18": return "V18";
                default: return "V18"; // unknown/unsupported → latest in the supported range
            }
        }

        /// <summary>Create a device/station from a hardware-catalog TypeIdentifier. Write gate + STA.</summary>
        private async Task<(string resultText, string errorText)> HandleConfigureDeviceAsync(JsonElement arguments, string rid, bool isRemote)
        {
            var typeIdentifier = ReadStringArg(arguments, "type_identifier") ?? ReadStringArg(arguments, "typeIdentifier");
            var deviceName = ReadStringArg(arguments, "name") ?? ReadStringArg(arguments, "device_name");
            if (string.IsNullOrWhiteSpace(typeIdentifier))
                return (null, "A 'type_identifier' (hardware catalog TypeIdentifier) is required.");
            if (string.IsNullOrWhiteSpace(deviceName))
                deviceName = typeIdentifier.Replace("OrderNumber:", "").Replace("/", "_").Replace(":", "_").Trim();

            var refusal = await AuthorizeWriteAsync(new TIAOpennessStudio.Services.WriteGate.WriteRequest
            {
                Tool = "configure_device",
                BlockName = deviceName,
                HardwareDescription = "订货号 " + typeIdentifier,
                Rid = rid,
                IsRemote = isRemote
            }).ConfigureAwait(false);
            if (refusal != null)
                return (null, refusal.Message);

            string resultText = null;
            string errorText = null;
            await _runOnTiaSta(async () =>
            {
                try { resultText = await _tia.AddDeviceAsync(typeIdentifier, deviceName).ConfigureAwait(false); }
                catch (Exception ex) { errorText = ex.Message; }
            }).ConfigureAwait(false);
            if (errorText != null) return (null, errorText);
            return (resultText, null);
        }

        /// <summary>Shared write gate + STA for the hardware-config write tools (set_device_attribute / add_module / configure_subnet).</summary>
        private async Task<(string resultText, string errorText)> HandleHardwareWriteAsync(JsonElement arguments, string kind, string rid, bool isRemote)
        {
            string target;
            string summary;
            Func<Task<string>> write;

            if (kind == "set_device_attribute")
            {
                var item = ReadStringArg(arguments, "device_item_name") ?? ReadStringArg(arguments, "deviceItemName");
                var attr = ReadStringArg(arguments, "attribute_name") ?? ReadStringArg(arguments, "attributeName");
                var val = ReadStringArg(arguments, "value");
                if (string.IsNullOrWhiteSpace(item) || string.IsNullOrWhiteSpace(attr))
                    return (null, "'set_device_attribute' requires device_item_name and attribute_name.");
                target = item;
                summary = "属性 " + attr + " = " + (val ?? "");
                write = () => _tia.SetDeviceItemAttributeAsync(item, attr, val);
            }
            else if (kind == "add_module")
            {
                var parent = ReadStringArg(arguments, "parent_device_item_name") ?? ReadStringArg(arguments, "parentDeviceItemName");
                var modName = ReadStringArg(arguments, "module_name") ?? ReadStringArg(arguments, "moduleName");
                var order = ReadStringArg(arguments, "order_number") ?? ReadStringArg(arguments, "orderNumber");
                if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(modName) || string.IsNullOrWhiteSpace(order))
                    return (null, "'add_module' requires parent_device_item_name, module_name and order_number.");
                target = modName;
                summary = "订货号 " + order + "（父级 " + parent + "）";
                write = () => _tia.AddModuleAsync(parent, modName, order);
            }
            else
            {
                var item = ReadStringArg(arguments, "device_item_name") ?? ReadStringArg(arguments, "deviceItemName");
                var sname = ReadStringArg(arguments, "subnet_name") ?? ReadStringArg(arguments, "subnetName");
                var stype = ReadStringArg(arguments, "subnet_type") ?? ReadStringArg(arguments, "subnetType") ?? "Profinet";
                if (string.IsNullOrWhiteSpace(item) || string.IsNullOrWhiteSpace(sname))
                    return (null, "'configure_subnet' requires device_item_name and subnet_name.");
                target = item;
                summary = "子网 " + sname + "（" + stype + "）";
                write = () => _tia.ConfigureSubnetAsync(item, sname, stype);
            }

            var refusal = await AuthorizeWriteAsync(new TIAOpennessStudio.Services.WriteGate.WriteRequest
            {
                Tool = kind,
                BlockName = target,
                HardwareDescription = summary,
                Rid = rid,
                IsRemote = isRemote
            }).ConfigureAwait(false);
            if (refusal != null)
                return (null, refusal.Message);

            string resultText = null;
            string errorText = null;
            await _runOnTiaSta(async () =>
            {
                try { resultText = await write().ConfigureAwait(false); }
                catch (Exception ex) { errorText = ex.Message; }
            }).ConfigureAwait(false);
            if (errorText != null) return (null, errorText);
            return (resultText, null);
        }
    }
}
