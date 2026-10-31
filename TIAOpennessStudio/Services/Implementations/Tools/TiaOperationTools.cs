using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations.Tools
{
    /// <summary>Export a block from TIA Portal</summary>
    [Obsolete("Use ExportBlockIrTool instead. Kept for backward compatibility.")]
    public class ExportBlockTool : IAgentTool
    {
        private readonly ITiaOpennessService _tiaService;
        public string Name => "export_block";
        public string Description => "Export a PLC block's code from TIA Portal. Returns the block's XML or SCL content.";
        public string InputSchema => @"{""kind"": ""object"", ""properties"": {""block_name"": {""type"": ""string""}}, ""required"": [""block_name""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => false; // Accessing PlcSoftware (STA-affine)

        public ExportBlockTool(ITiaOpennessService tiaService) => _tiaService = tiaService;

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var blockName = parameters.GetProperty("block_name").GetString();
                if (string.IsNullOrEmpty(blockName))
                    return AgentToolResult.Fail("block_name is required");
                var code = await _tiaService.ExportBlockCodeAsync(blockName);
                return AgentToolResult.Ok(code);
            }
            catch (Exception ex) { return AgentToolResult.Fail(ex.Message); }
        }
    }

    /// <summary>Export a PLC block as canonical JSON (the exact shape the write tools accept).</summary>
    public class ExportBlockIrTool : IAgentTool
    {
        private readonly ITiaOpennessService _tiaService;
        public string Name => "export_block_ir";
        public string Description => "Export a PLC block as canonical JSON (networks[].body plus interface). The same shape is accepted by build_lad_*/build_udt/build_global_db; GRAPH blocks return the build_graph_fb contract.";
        public string InputSchema => @"{""kind"": ""object"", ""properties"": {""block_name"": {""type"": ""string""}}, ""required"": [""block_name""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => false;

        public ExportBlockIrTool(ITiaOpennessService tiaService) => _tiaService = tiaService;

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var blockName = parameters.GetProperty("block_name").GetString();
                if (string.IsNullOrEmpty(blockName))
                    return AgentToolResult.Fail("block_name is required");
                var code = await _tiaService.ExportBlockCodeAsync(blockName);
                if (string.IsNullOrWhiteSpace(code))
                    return AgentToolResult.Fail($"Block '{blockName}' not found or empty");
                if (TiaXmlAgentReferenceBuilder.TryBuildJsonIr(code, out var jsonIr, out _))
                    return AgentToolResult.Ok(jsonIr);
                // Same failure semantics as the MCP tool: never silently return raw XML under the
                // canonical-JSON contract (callers would feed it to the write tools and fail there).
                return AgentToolResult.Fail($"Block '{blockName}' XML could not be converted to canonical JSON.");
            }
            catch (Exception ex) { return AgentToolResult.Fail(ex.Message); }
        }
    }

    /// <summary>Compile the TIA Portal project</summary>
    public class CompileTool : IAgentTool
    {
        private readonly ITiaOpennessService _tiaService;
        public string Name => "compile";
        public string Description => "Compile the currently open TIA Portal project. Returns compilation result with errors if any.";
        public string InputSchema => @"{""kind"": ""object"", ""properties"": {}}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.RequiresConfirmation;
        public bool SupportsParallel => false; // Write/compile operation, STA-affine and must run serially

        public CompileTool(ITiaOpennessService tiaService) => _tiaService = tiaService;

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                if (!_tiaService.IsConnected)
                    return AgentToolResult.Fail("Not connected to TIA Portal");
                var result = await _tiaService.CompileAsync();
                return AgentToolResult.Ok(result);
            }
            catch (Exception ex) { return AgentToolResult.Fail(ex.Message); }
        }
    }

    /// <summary>Get the project tree from TIA Portal</summary>
    public class GetProjectTreeTool : IAgentTool
    {
        private readonly ITiaOpennessService _tiaService;
        public string Name => "get_project_tree";
        public string Description => "Get the project tree from TIA Portal showing all devices and their items.";
        public string InputSchema => @"{""kind"": ""object"", ""properties"": {}}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => false; // Accessing PlcSoftware (STA-affine)

        public GetProjectTreeTool(ITiaOpennessService tiaService) => _tiaService = tiaService;

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                if (!_tiaService.IsConnected)
                    return AgentToolResult.Fail("Not connected to TIA Portal");
                // STA-safe: names are extracted on the STA host thread, so no COM object
                // crosses onto this agent (background) thread.
                var names = await _tiaService.GetProjectTreeNamesAsync();
                var summary = string.Join("\n", names.Take(50).Select(name => "- " + name));
                return AgentToolResult.Ok("Project tree (" + names.Count + " items):\n" + summary);
            }
            catch (Exception ex) { return AgentToolResult.Fail(ex.Message); }
        }
    }

    /// <summary>Search the project index</summary>
    public class SearchProjectTool : IAgentTool
    {
        private readonly IProjectKnowledgeWorkerHostService _workerHost;
        public string Name => "search_project";
        public string Description => "Search the project index for blocks, variables, and other PLC elements matching a query.";
        public string InputSchema => @"{""kind"": ""object"", ""properties"": {""query"": {""type"": ""string""}, ""top_k"": {""type"": ""integer""}, ""freshness"": {""type"": ""string""}}, ""required"": [""query""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Uses the separate knowledge cache process / SQLite index, no STA involvement

        public SearchProjectTool(IProjectKnowledgeWorkerHostService workerHost)
        {
            _workerHost = workerHost;
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var query = parameters.GetProperty("query").GetString();
                var topK = parameters.TryGetProperty("top_k", out var topKValue) && topKValue.ValueKind == JsonValueKind.Number
                    ? topKValue.GetInt32()
                    : 10;
                var freshness = parameters.TryGetProperty("freshness", out var freshnessValue) && freshnessValue.ValueKind == JsonValueKind.String
                    ? freshnessValue.GetString()
                    : ProjectKnowledgeFreshnessModes.PreferFresh;

                await _workerHost.EnsureStartedAsync().ConfigureAwait(false);
                var status = await _workerHost.EnsureCacheReadyAsync(freshness, ct).ConfigureAwait(false);
                var knowledgeHits = await _workerHost.SearchKnowledgeAsync(query, topK, freshness, ct).ConfigureAwait(false);
                if (knowledgeHits != null && knowledgeHits.Count > 0)
                {
                    var knowledgeSummary = string.Join("\n", knowledgeHits.Select(h =>
                        "[" + h.BlockName + "/" + h.BlockType + "] " + h.Summary));
                    return AgentToolResult.Ok("Search results for '" + query + "' [" + status.Status + "]:\n" + knowledgeSummary);
                }

                return AgentToolResult.Ok("No results found for: " + query + " [" + status.Status + "]");
            }
            catch (Exception ex) { return AgentToolResult.Fail(ex.Message); }
        }
    }

    /// <summary>Query variable references from the structured PLC knowledge index</summary>
    public class QueryVariableReferencesTool : IAgentTool
    {
        private readonly IProjectKnowledgeWorkerHostService _workerHost;
        public string Name => "query_variable_references";
        public string Description => "Query variable definitions and reference relationships from the structured PLC knowledge index.";
        public string InputSchema => @"{""kind"": ""object"", ""properties"": {""variable_path"": {""type"": ""string""}, ""direction"": {""type"": ""string""}, ""top_k"": {""type"": ""integer""}, ""include_definitions"": {""type"": ""boolean""}, ""freshness"": {""type"": ""string""}}, ""required"": [""variable_path""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Uses the separate knowledge cache process / SQLite index, no STA involvement

        public QueryVariableReferencesTool(IProjectKnowledgeWorkerHostService workerHost)
        {
            _workerHost = workerHost;
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var variablePath = parameters.GetProperty("variable_path").GetString();
                if (string.IsNullOrWhiteSpace(variablePath))
                {
                    return AgentToolResult.Fail("variable_path is required");
                }

                var direction = parameters.TryGetProperty("direction", out var directionValue)
                    ? directionValue.GetString()
                    : "both";
                var topK = parameters.TryGetProperty("top_k", out var topKValue) && topKValue.ValueKind == JsonValueKind.Number
                    ? topKValue.GetInt32()
                    : 20;
                var includeDefinitions = !parameters.TryGetProperty("include_definitions", out var includeDefinitionsValue) ||
                                         includeDefinitionsValue.ValueKind != JsonValueKind.False;
                var freshness = parameters.TryGetProperty("freshness", out var freshnessValue) && freshnessValue.ValueKind == JsonValueKind.String
                    ? freshnessValue.GetString()
                    : ProjectKnowledgeFreshnessModes.PreferFresh;

                await _workerHost.EnsureStartedAsync().ConfigureAwait(false);
                var status = await _workerHost.EnsureCacheReadyAsync(freshness, ct).ConfigureAwait(false);
                var result = await _workerHost.QueryVariableReferencesAsync(variablePath, direction, topK, includeDefinitions, freshness, ct).ConfigureAwait(false);
                if (result == null || result.Hits.Count == 0)
                {
                    return AgentToolResult.Ok("No variable references found for: " + variablePath + " [" + status.Status + "]");
                }

                var lines = new[]
                {
                    "Variable reference results for '" + result.VariablePath + "'",
                    "CacheStatus: " + status.Status,
                    "Direction: " + result.Direction,
                    "Precision: " + result.Precision,
                    "Note: " + result.Note,
                    "Hits: " + result.TotalHits
                }.ToList();

                foreach (var hit in result.Hits.Take(Math.Min(topK, 20)))
                {
                    var network = hit.NetworkIndex > 0 ? " network=" + hit.NetworkIndex : "";
                    var sourceRef = string.IsNullOrWhiteSpace(hit.SourceRef) ? "" : " sourceRef=" + hit.SourceRef;
                    lines.Add("- [" + hit.BlockName + "/" + hit.BlockType + "] " + hit.CanonicalPath +
                              " usage=" + hit.UsageKind + " kind=" + hit.DefinitionKind + network + sourceRef);
                }

                return AgentToolResult.Ok(string.Join("\n", lines));
            }
            catch (Exception ex) { return AgentToolResult.Fail(ex.Message); }
        }
    }

    /// <summary>Read the current project knowledge cache status</summary>
    public class GetIndexStatusTool : IAgentTool
    {
        private readonly IProjectKnowledgeWorkerHostService _workerHost;
        public string Name => "get_index_status";
        public string Description => "Get the current project knowledge cache status, including freshness and dirty state.";
        public string InputSchema => @"{""kind"": ""object"", ""properties"": {}}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Read-only cache status query, no STA involvement

        public GetIndexStatusTool(IProjectKnowledgeWorkerHostService workerHost) => _workerHost = workerHost;

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                await _workerHost.EnsureStartedAsync().ConfigureAwait(false);
                var status = await _workerHost.GetCacheStatusAsync(ct).ConfigureAwait(false);
                var lines = new[]
                {
                    "ProjectKey: " + status.ProjectKey,
                    "Status: " + status.Status,
                    "Connected: " + status.IsConnected,
                    "HasIndex: " + status.HasIndex,
                    "IsStale: " + status.IsStale,
                    "IndexedBlocks: " + status.IndexedBlockCount,
                    "DirtyItems: " + status.DirtyItemCount,
                    "LastIndexedAt: " + status.LastIndexedAt,
                    "Git: " + status.GitBranch + "@" + status.GitCommit,
                    "Note: " + status.Note
                };
                return AgentToolResult.Ok(string.Join("\n", lines));
            }
            catch (Exception ex) { return AgentToolResult.Fail(ex.Message); }
        }
    }

    /// <summary>Force a project knowledge cache rebuild</summary>
    public class RefreshProjectIndexTool : IAgentTool
    {
        private readonly IProjectKnowledgeWorkerHostService _workerHost;
        public string Name => "refresh_project_index";
        public string Description => "Rebuild the current project knowledge cache. Use when the cache is missing, stale, or the user requests a re-check.";
        public string InputSchema => @"{""kind"": ""object"", ""properties"": {""force"": {""type"": ""boolean""}}}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => false; // Cache rebuild is heavy; running it serially is safer

        public RefreshProjectIndexTool(IProjectKnowledgeWorkerHostService workerHost) => _workerHost = workerHost;

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var force = parameters.TryGetProperty("force", out var forceValue) && forceValue.ValueKind == JsonValueKind.True;
                await _workerHost.EnsureStartedAsync().ConfigureAwait(false);
                var result = await _workerHost.RebuildAsync(force, ct).ConfigureAwait(false);
                return AgentToolResult.Ok(
                    "Project index rebuilt: upserted=" + result.Upserted +
                    ", skipped=" + result.SkippedUnchanged +
                    ", failed=" + result.Failed);
            }
            catch (Exception ex) { return AgentToolResult.Fail(ex.Message); }
        }
    }

    /// <summary>Import raw XML or SCL code into TIA Portal</summary>
    public class ImportBlockCodeTool : IAgentTool
    {
        private readonly ITiaOpennessService _tiaService;
        private readonly IProjectKnowledgeWorkerHostService _workerHost;
        private readonly IAgentTargetGroupProvider _targetGroupProvider;
        public string Name => "import_block_code";
        public string Description => "Import raw XML or SCL payload into TIA Portal for a block.";
        public string InputSchema => @"{""kind"": ""object"", ""properties"": {""block_name"": {""type"": ""string""}, ""block_type"": {""type"": ""string""}, ""code"": {""type"": ""string""}, ""group_path"": {""type"": ""string""}}, ""required"": [""block_name"", ""block_type"", ""code""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.RequiresConfirmation;
        public bool SupportsParallel => false; // Write operation, STA-affine and must run serially

        public ImportBlockCodeTool(ITiaOpennessService tiaService, IProjectKnowledgeWorkerHostService workerHost, IAgentTargetGroupProvider targetGroupProvider)
        {
            _tiaService = tiaService;
            _workerHost = workerHost;
            _targetGroupProvider = targetGroupProvider;
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var blockName = parameters.GetProperty("block_name").GetString();
                var blockType = parameters.GetProperty("block_type").GetString();
                var code = parameters.GetProperty("code").GetString();
                var groupPath = parameters.TryGetProperty("group_path", out var gp) ? gp.GetString() : "";
                if (string.IsNullOrWhiteSpace(blockName) || string.IsNullOrWhiteSpace(blockType) || string.IsNullOrWhiteSpace(code))
                    return AgentToolResult.Fail("block_name, block_type and code are required");

                // The LLM rarely fills group_path, which would drop the block into the root
                // group. When no path is specified, fall back to the target group the
                // user selected on the status bar chip.
                if (string.IsNullOrWhiteSpace(groupPath) && _targetGroupProvider != null)
                {
                    bool isUdt = string.Equals(blockType, "UDT", StringComparison.OrdinalIgnoreCase);
                    groupPath = isUdt ? _targetGroupProvider.TypeGroupPath : _targetGroupProvider.BlockGroupPath;
                }
                var result = await _tiaService.ImportBlockCodeAsync(blockName, blockType, code, groupPath);
                await _workerHost.EnsureStartedAsync().ConfigureAwait(false);
                await _workerHost.MarkDirtyAsync(blockType + ":" + blockName, "import_block_code", ct).ConfigureAwait(false);
                return AgentToolResult.Ok(result);
            }
            catch (Exception ex) { return AgentToolResult.Fail(ex.Message); }
        }
    }

    /// <summary>Resolve XML fragment from exported TIA block</summary>
    public class ResolveXmlFragmentTool : IAgentTool
    {
        public string Name => "resolve_xml_fragment";
        public string Description => "Resolve a raw XML fragment by compileUnitId and optional uid from an exported TIA XML block.";
        public string InputSchema => @"{""kind"": ""object"", ""properties"": {""xml"": {""type"": ""string""}, ""compile_unit_id"": {""type"": ""string""}, ""uid"": {""type"": ""string""}}, ""required"": [""xml""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Pure in-memory XML parsing, no external dependencies

        public Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var xml = parameters.GetProperty("xml").GetString();
                var compileUnitId = parameters.TryGetProperty("compile_unit_id", out var c) ? c.GetString() : null;
                var uid = parameters.TryGetProperty("uid", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(xml))
                    return Task.FromResult(AgentToolResult.Fail("xml is required"));
                if (!TiaXmlAgentReferenceBuilder.TryExtractFragment(xml, compileUnitId, uid, out var fragment))
                    return Task.FromResult(AgentToolResult.Fail("Unable to resolve XML fragment"));
                return Task.FromResult(AgentToolResult.Ok(fragment));
            }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail(ex.Message)); }
        }
    }
}
