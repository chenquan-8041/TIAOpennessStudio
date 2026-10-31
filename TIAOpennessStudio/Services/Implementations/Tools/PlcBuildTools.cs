using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations.Tools
{
    public class PlcBuildUdtTool : IAgentTool
    {
        private readonly ITiaToolService _svc;
        public string Name => "plc_build_udt";
        public string Description => "Build PLC UDT XML from JSON spec. Returns TIA-importable XML.";
        public string InputSchema => "{\"kind\":\"object\",\"properties\":{\"json\":{\"type\":\"string\"}},\"required\":[\"json\"]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Offline XML build, pure logic, does not touch STA
        public PlcBuildUdtTool(ITiaToolService svc) => _svc = svc;
        public Task<AgentToolResult> ExecuteAsync(JsonElement p, CancellationToken ct = default)
        {
            try { return Task.FromResult(AgentToolResult.Ok(_svc.BuildUdtXml(p.GetProperty("json").GetRawText()))); }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail(ex.Message)); }
        }
    }

    public class PlcBuildTagTableTool : IAgentTool
    {
        private readonly ITiaToolService _svc;
        public string Name => "plc_build_tag_table";
        public string Description => "Build PLC tag table XML from JSON spec. Returns TIA-importable XML.";
        public string InputSchema => "{\"kind\":\"object\",\"properties\":{\"json\":{\"type\":\"string\"}},\"required\":[\"json\"]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Offline XML build, pure logic, does not touch STA
        public PlcBuildTagTableTool(ITiaToolService svc) => _svc = svc;
        public Task<AgentToolResult> ExecuteAsync(JsonElement p, CancellationToken ct = default)
        {
            try { return Task.FromResult(AgentToolResult.Ok(_svc.BuildTagTableXml(p.GetProperty("json").GetRawText()))); }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail(ex.Message)); }
        }
    }

    public class PlcBuildGlobalDbTool : IAgentTool
    {
        private readonly ITiaToolService _svc;
        public string Name => "plc_build_global_db";
        public string Description => "Build PLC global DB XML from JSON spec. Returns TIA-importable XML.";
        public string InputSchema => "{\"kind\":\"object\",\"properties\":{\"json\":{\"type\":\"string\"}},\"required\":[\"json\"]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Offline XML build, pure logic, does not touch STA
        public PlcBuildGlobalDbTool(ITiaToolService svc) => _svc = svc;
        public Task<AgentToolResult> ExecuteAsync(JsonElement p, CancellationToken ct = default)
        {
            try { return Task.FromResult(AgentToolResult.Ok(_svc.BuildGlobalDbXml(p.GetProperty("json").GetRawText()))); }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail(ex.Message)); }
        }
    }

    public class PlcBuildFcTool : IAgentTool
    {
        private readonly ITiaToolService _svc;
        public string Name => "plc_build_fc";
        public string Description => "Build SCL FC block XML from JSON spec. Returns TIA-importable XML.";
        public string InputSchema => "{\"kind\":\"object\",\"properties\":{\"json\":{\"type\":\"string\"}},\"required\":[\"json\"]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Offline XML build, pure logic, does not touch STA
        public PlcBuildFcTool(ITiaToolService svc) => _svc = svc;
        public Task<AgentToolResult> ExecuteAsync(JsonElement p, CancellationToken ct = default)
        {
            try { return Task.FromResult(AgentToolResult.Ok(_svc.BuildFcBlockXml(p.GetProperty("json").GetRawText()))); }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail(ex.Message)); }
        }
    }

    public class PlcBuildFbTool : IAgentTool
    {
        private readonly ITiaToolService _svc;
        public string Name => "plc_build_fb";
        public string Description => "Build SCL FB block XML from JSON spec. Returns TIA-importable XML.";
        public string InputSchema => "{\"kind\":\"object\",\"properties\":{\"json\":{\"type\":\"string\"}},\"required\":[\"json\"]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Offline XML build, pure logic, does not touch STA
        public PlcBuildFbTool(ITiaToolService svc) => _svc = svc;
        public Task<AgentToolResult> ExecuteAsync(JsonElement p, CancellationToken ct = default)
        {
            try { return Task.FromResult(AgentToolResult.Ok(_svc.BuildFbBlockXml(p.GetProperty("json").GetRawText()))); }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail(ex.Message)); }
        }
    }

    public class PlcBuildLadFcTool : IAgentTool
    {
        private readonly ITiaToolService _svc;
        public string Name => "plc_build_lad_fc";
        public string Description => "Build LAD FC block XML with FlgNet networks from JSON spec.";
        public string InputSchema => "{\"kind\":\"object\",\"properties\":{\"json\":{\"type\":\"string\"}},\"required\":[\"json\"]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Offline XML build, pure logic, does not touch STA
        public PlcBuildLadFcTool(ITiaToolService svc) => _svc = svc;
        public Task<AgentToolResult> ExecuteAsync(JsonElement p, CancellationToken ct = default)
        {
            try { return Task.FromResult(AgentToolResult.Ok(_svc.BuildLadFcBlockXml(p.GetProperty("json").GetRawText()))); }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail(ex.Message)); }
        }
    }

    public class PlcBuildAndImportTool : IAgentTool
    {
        private readonly ITiaToolService _svc;
        public string Name => "plc_build_and_import";
        public string Description => "Build PLC block from JSON and import into TIA Portal. Supports: udt, tag_table, global_db, fc, fb, lad_fc.";
        public string InputSchema => "{\"kind\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\"},\"json\":{\"type\":\"string\"},\"group_path\":{\"type\":\"string\"}},\"required\":[\"kind\",\"json\"]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.RequiresConfirmation;
        public bool SupportsParallel => false; // Imports into TIA, STA-affine and must run serially
        private readonly IAgentTargetGroupProvider _targetGroupProvider;
        public PlcBuildAndImportTool(ITiaToolService svc, IAgentTargetGroupProvider targetGroupProvider = null)
        {
            _svc = svc;
            _targetGroupProvider = targetGroupProvider;
        }
        public async Task<AgentToolResult> ExecuteAsync(JsonElement p, CancellationToken ct = default)
        {
            try
            {
                var kind = p.GetProperty("kind").GetString();
                var json = p.GetProperty("json").GetRawText();
                var groupPath = p.TryGetProperty("group_path", out var gp) ? gp.GetString() : "";

                // The LLM rarely fills group_path; fall back to the target group the user picked on the status bar chip.
                if (string.IsNullOrWhiteSpace(groupPath) && _targetGroupProvider != null)
                {
                    bool isUdt = string.Equals(kind, "udt", StringComparison.OrdinalIgnoreCase);
                    groupPath = isUdt ? _targetGroupProvider.TypeGroupPath : _targetGroupProvider.BlockGroupPath;
                }

                var result = await _svc.BuildAndImportAsync("", kind, json, groupPath);
                return AgentToolResult.Ok(result);
            }
            catch (Exception ex) { return AgentToolResult.Fail(ex.Message); }
        }
    }
}
