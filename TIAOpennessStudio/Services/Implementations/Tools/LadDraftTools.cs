using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations.Tools
{
    /// <summary>
    /// G-6 incremental LAD authoring tools (add-oil tactic). The agent accumulates
    /// networks into ONE session draft and triggers a single block compile at commit.
    ///
    /// Safety (code-enforced, not delegated to the agent):
    ///   - begin/append/preview/discard are offline, parallel-safe, Allowed.
    ///   - commit is the ONLY tool that touches TIA (import + compile), so it is
    ///     RequiresConfirmation and non-parallel. Import already has snapshot + rollback
    ///     on failure (OpennessService.ImportBlockCodeAsync).
    /// </summary>
    internal static class LadDraftToolJson
    {
        public static string Str(JsonElement p, string name)
            => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public static int Int(JsonElement p, string name, int dflt)
            => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : dflt;

        public static List<LadDraftMember> Members(JsonElement p, string name)
        {
            var list = new List<LadDraftMember>();
            if (p.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var m = new LadDraftMember
                    {
                        Name = Str(e, "name"),
                        Datatype = Str(e, "datatype") ?? Str(e, "dataType"),
                        Comment = Str(e, "comment") ?? ""
                    };
                    if (!string.IsNullOrWhiteSpace(m.Name) && !string.IsNullOrWhiteSpace(m.Datatype))
                        list.Add(m);
                }
            }
            return list;
        }
    }

    /// <summary>lad_draft_begin — initialize a session draft (kind/blockName/number/interface).</summary>
    public class LadDraftBeginTool : IAgentTool, IParallelSafeTool
    {
        private readonly ILadDraftStore _store;
        public LadDraftBeginTool(ILadDraftStore store) { _store = store; }

        public string Name => "lad_draft_begin";
        public string Description =>
            "Start an incremental LAD draft for building a block network-by-network (add-oil style). " +
            "Use this for COMPLEX LAD blocks where step-by-step validation helps; for simple blocks prefer one-shot plc_build_and_import. " +
            "Phase 1 supports kind='ladfc' (LAD FC). Provide blockName, blockNumber, and optional input/output members. " +
            "Returns a sessionId to use with lad_draft_append_network / preview / commit / discard.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""session_id"":{""type"":""string""},""kind"":{""type"":""string"",""description"":""ladfc | ladfb (only ladfc composes in phase 1)""},""block_name"":{""type"":""string""},""block_number"":{""type"":""integer""},""block_title"":{""type"":""string""},""block_comment"":{""type"":""string""},""inputs"":{""type"":""array""},""outputs"":{""type"":""array""}},""required"":[""block_name"",""block_number""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true;

        public Task<AgentToolResult> ExecuteAsync(JsonElement p, CancellationToken ct = default)
        {
            try
            {
                var blockName = LadDraftToolJson.Str(p, "block_name");
                if (string.IsNullOrWhiteSpace(blockName))
                    return Task.FromResult(AgentToolResult.Fail("block_name is required"));
                var blockNumber = LadDraftToolJson.Int(p, "block_number", 0);
                if (blockNumber <= 0)
                    return Task.FromResult(AgentToolResult.Fail("block_number must be > 0"));

                var draft = new LadDraft
                {
                    SessionId = LadDraftToolJson.Str(p, "session_id"),
                    Kind = LadDraftToolJson.Str(p, "kind") ?? "ladfc",
                    BlockName = blockName,
                    BlockNumber = blockNumber,
                    BlockTitle = LadDraftToolJson.Str(p, "block_title") ?? "",
                    BlockComment = LadDraftToolJson.Str(p, "block_comment") ?? "",
                    Inputs = LadDraftToolJson.Members(p, "inputs"),
                    Outputs = LadDraftToolJson.Members(p, "outputs")
                };

                var created = _store.Begin(draft);
                var note = string.Equals(created.Kind, "ladfb", StringComparison.OrdinalIgnoreCase)
                    ? " NOTE: kind='ladfb' is accepted but compose is not implemented in phase 1 (only 'ladfc' commits)."
                    : "";
                return Task.FromResult(AgentToolResult.Ok(
                    $"Draft started. sessionId={created.SessionId}, kind={created.Kind}, block={created.BlockName} (#{created.BlockNumber}), " +
                    $"inputs={created.Inputs.Count}, outputs={created.Outputs.Count}.{note}"));
            }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail("lad_draft_begin failed: " + ex.Message)); }
        }
    }

    /// <summary>lad_draft_append_network — append one call-type FlgNet network to the draft.</summary>
    public class LadDraftAppendNetworkTool : IAgentTool, IParallelSafeTool
    {
        private readonly ILadDraftStore _store;
        public LadDraftAppendNetworkTool(ILadDraftStore store) { _store = store; }

        public string Name => "lad_draft_append_network";
        public string Description =>
            "Append ONE call-type network (calls an FC/FB with wired parameters) to an existing LAD draft. " +
            "Each parameter is either a global symbol (source='global', symbol_path=[...]) or a literal constant (source='constant', constant_value=...). " +
            "No block-global IDs are assigned here — the composer assigns them at commit, so networks never clash. Requires a sessionId from lad_draft_begin.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""session_id"":{""type"":""string""},""title"":{""type"":""string""},""comment"":{""type"":""string""},""call_name"":{""type"":""string""},""parameters"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""parameter_name"":{""type"":""string""},""section"":{""type"":""string""},""data_type"":{""type"":""string""},""source"":{""type"":""string""},""constant_value"":{""type"":""string""},""symbol_path"":{""type"":""array"",""items"":{""type"":""string""}}}}}},""required"":[""session_id"",""call_name""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true;

        public Task<AgentToolResult> ExecuteAsync(JsonElement p, CancellationToken ct = default)
        {
            try
            {
                var sessionId = LadDraftToolJson.Str(p, "session_id");
                if (string.IsNullOrWhiteSpace(sessionId))
                    return Task.FromResult(AgentToolResult.Fail("session_id is required"));
                var callName = LadDraftToolJson.Str(p, "call_name");
                if (string.IsNullOrWhiteSpace(callName))
                    return Task.FromResult(AgentToolResult.Fail("call_name is required (phase 1 = call-type networks)"));

                var net = new LadDraftNetwork
                {
                    Title = LadDraftToolJson.Str(p, "title") ?? "",
                    Comment = LadDraftToolJson.Str(p, "comment") ?? "",
                    CallName = callName,
                    Parameters = ParseParameters(p)
                };

                var draft = _store.AppendNetwork(sessionId, net);
                return Task.FromResult(AgentToolResult.Ok(
                    $"Network appended to draft {sessionId}. Now {draft.Networks.Count} network(s). Call: {callName} with {net.Parameters.Count} parameter(s)."));
            }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail("lad_draft_append_network failed: " + ex.Message)); }
        }

        private static List<LadDraftCallParameter> ParseParameters(JsonElement p)
        {
            var list = new List<LadDraftCallParameter>();
            if (!p.TryGetProperty("parameters", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in arr.EnumerateArray())
            {
                var param = new LadDraftCallParameter
                {
                    ParameterName = LadDraftToolJson.Str(e, "parameter_name"),
                    Section = LadDraftToolJson.Str(e, "section") ?? "Input",
                    DataType = LadDraftToolJson.Str(e, "data_type") ?? LadDraftToolJson.Str(e, "datatype"),
                    SourceKind = (LadDraftToolJson.Str(e, "source") ?? "global").ToLowerInvariant(),
                    ConstantValue = LadDraftToolJson.Str(e, "constant_value") ?? ""
                };
                if (e.TryGetProperty("symbol_path", out var sp) && sp.ValueKind == JsonValueKind.Array)
                    param.SymbolPath = sp.EnumerateArray().Select(x => x.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                if (!string.IsNullOrWhiteSpace(param.ParameterName) && !string.IsNullOrWhiteSpace(param.DataType))
                    list.Add(param);
            }
            return list;
        }
    }

    /// <summary>lad_draft_preview — return current draft XML + network count (offline, no import).</summary>
    public class LadDraftPreviewTool : IAgentTool, IParallelSafeTool
    {
        private readonly ILadDraftStore _store;
        public LadDraftPreviewTool(ILadDraftStore store) { _store = store; }

        public string Name => "lad_draft_preview";
        public string Description =>
            "Preview the current LAD draft: composes the accumulated networks into block XML WITHOUT importing to TIA. " +
            "Use this to inspect what commit would write. Requires a sessionId.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""session_id"":{""type"":""string""}},""required"":[""session_id""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true;

        public Task<AgentToolResult> ExecuteAsync(JsonElement p, CancellationToken ct = default)
        {
            try
            {
                var sessionId = LadDraftToolJson.Str(p, "session_id");
                if (string.IsNullOrWhiteSpace(sessionId))
                    return Task.FromResult(AgentToolResult.Fail("session_id is required"));
                var draft = _store.Get(sessionId);
                if (draft == null) return Task.FromResult(AgentToolResult.Fail($"No active draft for session '{sessionId}'."));

                var (ok, xmlOrError) = _store.ComposeXml(sessionId);
                var sb = new StringBuilder();
                sb.AppendLine($"Draft {sessionId}: kind={draft.Kind}, block={draft.BlockName} (#{draft.BlockNumber}), networks={draft.Networks.Count}.");
                if (!ok) { sb.AppendLine("Compose status: NOT READY — " + xmlOrError); return Task.FromResult(AgentToolResult.Ok(sb.ToString())); }
                sb.AppendLine("Composed XML preview (not imported):");
                sb.AppendLine(xmlOrError);
                return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
            }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail("lad_draft_preview failed: " + ex.Message)); }
        }
    }

    /// <summary>lad_draft_discard — drop the session draft.</summary>
    public class LadDraftDiscardTool : IAgentTool, IParallelSafeTool
    {
        private readonly ILadDraftStore _store;
        public LadDraftDiscardTool(ILadDraftStore store) { _store = store; }

        public string Name => "lad_draft_discard";
        public string Description => "Discard the current LAD draft for a session (memory + persisted file). Requires a sessionId.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""session_id"":{""type"":""string""}},""required"":[""session_id""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true;

        public Task<AgentToolResult> ExecuteAsync(JsonElement p, CancellationToken ct = default)
        {
            var sessionId = LadDraftToolJson.Str(p, "session_id");
            if (string.IsNullOrWhiteSpace(sessionId))
                return Task.FromResult(AgentToolResult.Fail("session_id is required"));
            _store.Discard(sessionId);
            return Task.FromResult(AgentToolResult.Ok($"Draft {sessionId} discarded."));
        }
    }

    /// <summary>
    /// lad_draft_commit — compose the whole draft, import to TIA, then compile.
    /// This is the ONLY draft tool that touches TIA, so it is RequiresConfirmation
    /// and NOT parallel-safe (STA). Import has snapshot + rollback on failure.
    /// </summary>
    public class LadDraftCommitTool : IAgentTool
    {
        private readonly ILadDraftStore _store;
        private readonly ITiaOpennessService _tia;
        public LadDraftCommitTool(ILadDraftStore store, ITiaOpennessService tia) { _store = store; _tia = tia; }

        public string Name => "lad_draft_commit";
        public string Description =>
            "Compose the accumulated LAD draft into a complete block, import it to TIA Portal, then compile. " +
            "This is a WRITE action (requires confirmation). On success the draft is cleared; on failure the draft is kept for further edits and the import auto-rolls-back. Requires a sessionId.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""session_id"":{""type"":""string""},""group_path"":{""type"":""string"",""description"":""Backslash-delimited block group path; empty = root""}},""required"":[""session_id""]}";
        // Write action → confirmation gate; STA → never parallel.
        public ToolPermissionLevel Permission => ToolPermissionLevel.RequiresConfirmation;
        public bool SupportsParallel => false;

        public async Task<AgentToolResult> ExecuteAsync(JsonElement p, CancellationToken ct = default)
        {
            try
            {
                var sessionId = LadDraftToolJson.Str(p, "session_id");
                if (string.IsNullOrWhiteSpace(sessionId))
                    return AgentToolResult.Fail("session_id is required");
                var draft = _store.Get(sessionId);
                if (draft == null) return AgentToolResult.Fail($"No active draft for session '{sessionId}'.");

                var (ok, xmlOrError) = _store.ComposeXml(sessionId);
                if (!ok) return AgentToolResult.Fail("Cannot commit — " + xmlOrError);

                var groupPath = LadDraftToolJson.Str(p, "group_path") ?? "";
                // Import (snapshot + gated compile + rollback on failure lives in OpennessService).
                var importResult = await _tia.ImportBlockCodeAsync(draft.BlockName, "FC", xmlOrError, groupPath);
                if (!string.IsNullOrEmpty(importResult) &&
                    (importResult.StartsWith("Error", StringComparison.OrdinalIgnoreCase) || importResult.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    // Keep the draft for further edits; import already rolled back.
                    return AgentToolResult.Fail($"Import failed (draft kept for editing): {importResult}");
                }

                var compileResult = await _tia.CompileAsync();
                var failed = !string.IsNullOrWhiteSpace(compileResult) &&
                             (compileResult.IndexOf("Error:", StringComparison.OrdinalIgnoreCase) >= 0 || compileResult.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0);

                if (failed)
                {
                    // Keep the draft so the agent can fix a network and re-commit.
                    return AgentToolResult.Fail(
                        $"Committed block '{draft.BlockName}' imported but compile reported errors (draft kept):\n{compileResult}");
                }

                // Success → clear the draft.
                _store.Discard(sessionId);
                return AgentToolResult.Ok(
                    $"Committed draft {sessionId}: block '{draft.BlockName}' ({draft.Networks.Count} networks) imported and compiled OK.\n{compileResult}");
            }
            catch (Exception ex) { return AgentToolResult.Fail("lad_draft_commit failed: " + ex.Message); }
        }
    }
}
