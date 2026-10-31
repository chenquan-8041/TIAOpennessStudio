using System;
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
    /// C-6: local IAgentTool wrappers over existing ITiaOpennessService capabilities that
    /// were only reachable via the MCP endpoint, not the local C# ReAct ToolRegistry:
    ///   - bootstrap (read-only)         → BootstrapAsync
    ///   - hardware_search (read-only)   → SearchHardwareCatalogAsync
    ///   - delete_block (DESTRUCTIVE)    → DeleteBlockAsync  [RequiresConfirmation]
    ///
    /// All touch PlcSoftware (STA), so none are parallel-safe. delete_block is a hard
    /// write and gated by RequiresConfirmation (the ReActEngine confirmation callback),
    /// consistent with the write-safety discipline used elsewhere.
    /// </summary>
    public class BootstrapTool : IAgentTool
    {
        private readonly ITiaOpennessService _tia;
        public BootstrapTool(ITiaOpennessService tia) { _tia = tia; }

        public string Name => "bootstrap";
        public string Description =>
            "Get project bootstrap info: TIA version, connection state, current project name, and a recommended next tool. " +
            "Call this first when unsure of the current TIA/project state.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{},""required"":[]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => false; // touches Openness (STA)

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                if (_tia == null) return AgentToolResult.Fail("TIA service unavailable");
                var info = await _tia.BootstrapAsync();
                if (info == null) return AgentToolResult.Fail("Bootstrap returned no info");
                var sb = new StringBuilder();
                sb.AppendLine($"TIA version: {info.TiaVersion}");
                sb.AppendLine($"Connected: {info.IsConnected}");
                sb.AppendLine($"Openness group valid: {info.OpennessGroupValid}");
                sb.AppendLine($"Current project: {info.CurrentProjectName ?? "(none)"}");
                if (!string.IsNullOrWhiteSpace(info.RecommendedNextTool))
                    sb.AppendLine($"Recommended next tool: {info.RecommendedNextTool}");
                if (info.KnownLimitations != null && info.KnownLimitations.Count > 0)
                    sb.AppendLine("Known limitations: " + string.Join("; ", info.KnownLimitations));
                return AgentToolResult.Ok(sb.ToString());
            }
            catch (Exception ex) { return AgentToolResult.Fail("bootstrap failed: " + ex.Message); }
        }
    }

    /// <summary>hardware_search — search the TIA hardware catalog (read-only).</summary>
    public class HardwareSearchTool : IAgentTool
    {
        private readonly ITiaOpennessService _tia;
        public HardwareSearchTool(ITiaOpennessService tia) { _tia = tia; }

        public string Name => "hardware_search";
        public string Description =>
            "Search the TIA hardware catalog for devices/modules by a search term. " +
            "Returns matching catalog items (name, type identifier, order number, version).";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""search_term"":{""type"":""string"",""description"":""Text to search the hardware catalog for""}},""required"":[""search_term""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => false; // touches Openness (STA)

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                if (_tia == null) return AgentToolResult.Fail("TIA service unavailable");
                var term = parameters.TryGetProperty("search_term", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(term)) return AgentToolResult.Fail("search_term is required");

                var items = await _tia.SearchHardwareCatalogAsync(term);
                if (items == null || items.Count == 0) return AgentToolResult.Ok($"No hardware catalog matches for '{term}'.");
                var sb = new StringBuilder();
                sb.AppendLine($"Found {items.Count} hardware item(s) for '{term}':");
                foreach (var it in items.Take(50))
                    sb.AppendLine($"- {it.Name} [type={it.TypeIdentifier}, order={it.OrderNumber}, ver={it.Version}]");
                return AgentToolResult.Ok(sb.ToString());
            }
            catch (Exception ex) { return AgentToolResult.Fail("hardware_search failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// delete_block — DELETE a PLC block. Destructive write: RequiresConfirmation + non-parallel.
    /// </summary>
    public class DeleteBlockTool : IAgentTool
    {
        private readonly ITiaOpennessService _tia;
        public DeleteBlockTool(ITiaOpennessService tia) { _tia = tia; }

        public string Name => "delete_block";
        public string Description =>
            "Delete a PLC block from the project by name. DESTRUCTIVE and irreversible — requires user confirmation. " +
            "Only use when the user explicitly asks to remove a block.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""block_name"":{""type"":""string"",""description"":""Name of the block to delete""}},""required"":[""block_name""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.RequiresConfirmation;
        public bool SupportsParallel => false; // destructive write, STA

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                if (_tia == null) return AgentToolResult.Fail("TIA service unavailable");
                var name = parameters.TryGetProperty("block_name", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) return AgentToolResult.Fail("block_name is required");

                var ok = await _tia.DeleteBlockAsync(name);
                return ok
                    ? AgentToolResult.Ok($"Block '{name}' deleted.")
                    : AgentToolResult.Fail($"Delete failed or block '{name}' not found.");
            }
            catch (Exception ex) { return AgentToolResult.Fail("delete_block failed: " + ex.Message); }
        }
    }
}
