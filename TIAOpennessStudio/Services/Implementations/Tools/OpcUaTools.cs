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
    /// OPC UA tool family: when OPC UA is connected to the PLC, these tools let
    /// the agent read real-time variable values, browse the address space, and
    /// read the live values of a whole DB block's members. All are read-only.
    ///
    /// Note: these tools depend on the runtime connection state of
    /// IOpcUaService. When not connected, they return an explicit hint instead
    /// of crashing, so the agent can prompt the user to connect in the OPC UA
    /// panel first.
    /// </summary>
    internal static class OpcUaToolHelpers
    {
        public static AgentToolResult NotConnected()
            => AgentToolResult.Fail("OPC UA 未连接。请先在 OPC UA 面板连接到 PLC（Endpoint 形如 opc.tcp://<ip>:4840）后再试。");

        public static string QualityText(OpcUaQuality q) => q.ToString();
    }

    /// <summary>Checks the OPC UA connection status.</summary>
    public class OpcUaStatusTool : IAgentTool
    {
        private readonly IOpcUaService _opc;
        public OpcUaStatusTool(IOpcUaService opc) { _opc = opc; }

        public string Name => "opcua_status";
        public string Description => "Check whether the OPC UA client is currently connected to the PLC, and return the connection state. Use this before reading real-time values.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{},""required"":[]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true;

        public Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            var connected = _opc != null && _opc.IsConnected;
            var state = _opc?.State.ToString() ?? "Unknown";
            return Task.FromResult(AgentToolResult.Ok($"connected={connected}, state={state}"));
        }
    }

    /// <summary>Reads the real-time values of one or more NodeIds.</summary>
    public class OpcUaReadNodeTool : IAgentTool
    {
        private readonly IOpcUaService _opc;
        public OpcUaReadNodeTool(IOpcUaService opc) { _opc = opc; }

        public string Name => "opcua_read_node";
        public string Description =>
            "Read the current (real-time) value of one or more OPC UA variable nodes from the connected PLC. " +
            "Provide node IDs like 'ns=3;s=\"DB_Name\".\"Member\"'. Returns value, data type, quality and timestamp for each node. " +
            "Requires an active OPC UA connection (check with opcua_status).";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""node_ids"":{""type"":""array"",""items"":{""type"":""string""},""description"":""One or more OPC UA NodeIds to read""}},""required"":[""node_ids""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Read-only, does not touch Openness STA

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                if (_opc == null || !_opc.IsConnected) return OpcUaToolHelpers.NotConnected();

                var ids = new List<string>();
                if (parameters.TryGetProperty("node_ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        var s = e.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) ids.Add(s.Trim());
                    }
                }
                if (ids.Count == 0)
                    return AgentToolResult.Fail("node_ids is required and must contain at least one NodeId");

                var snaps = await _opc.ReadNodesAsync(ids, ct);
                var sb = new StringBuilder();
                sb.AppendLine($"Read {snaps.Count} node(s):");
                foreach (var s in snaps)
                {
                    sb.AppendLine($"- {s.NodeId} = {s.Value ?? "(null)"} " +
                                  $"[type={s.DataType ?? "?"}, quality={OpcUaToolHelpers.QualityText(s.Quality)}, " +
                                  $"ts={s.SourceTimestamp?.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}]");
                }
                return AgentToolResult.Ok(sb.ToString());
            }
            catch (Exception ex) { return AgentToolResult.Fail("OPC UA read failed: " + ex.Message); }
        }
    }

    /// <summary>Browses a node in the address space (lists its children).</summary>
    public class OpcUaBrowseTool : IAgentTool
    {
        private readonly IOpcUaService _opc;
        public OpcUaBrowseTool(IOpcUaService opc) { _opc = opc; }

        public string Name => "opcua_browse";
        public string Description =>
            "Browse the OPC UA address space of the connected PLC. Pass a node_id to list its children, or omit it to browse from the Objects root. " +
            "Useful to discover DBs under DeviceSet→PLC→DataBlocksGlobal or system variables under the Server node. " +
            "Returns each child's NodeId, display name, node class and data type.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""node_id"":{""type"":""string"",""description"":""Node to browse; omit for the Objects root""}},""required"":[]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true;

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                if (_opc == null || !_opc.IsConnected) return OpcUaToolHelpers.NotConnected();

                string nodeId = null;
                if (parameters.TryGetProperty("node_id", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                    nodeId = n.GetString().Trim();

                var children = await _opc.BrowseAsync(nodeId, ct);
                var sb = new StringBuilder();
                sb.AppendLine($"Browsed {children.Count} child node(s) under {(nodeId ?? "ObjectsFolder")}:");
                foreach (var c in children)
                {
                    var typePart = string.IsNullOrEmpty(c.DataType) ? "" : $", type={c.DataType}";
                    sb.AppendLine($"- [{c.NodeClass}] {c.DisplayName}  ({c.NodeId}{typePart})");
                }
                return AgentToolResult.Ok(sb.ToString());
            }
            catch (Exception ex) { return AgentToolResult.Fail("OPC UA browse failed: " + ex.Message); }
        }
    }

    /// <summary>Reads the real-time values of all variable members under a DB node.</summary>
    public class OpcUaReadDbTool : IAgentTool
    {
        private readonly IOpcUaService _opc;
        public OpcUaReadDbTool(IOpcUaService opc) { _opc = opc; }

        public string Name => "opcua_read_db";
        public string Description =>
            "Read the real-time values of all variable members under a DB (or any container) node in the connected PLC. " +
            "Provide the DB node's NodeId (e.g. the DB node found via opcua_browse under DataBlocksGlobal). " +
            "The tool browses the node's members and reads all leaf variables. Requires an active OPC UA connection.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""node_id"":{""type"":""string"",""description"":""NodeId of the DB / container node whose members to read""},""max_members"":{""type"":""integer"",""description"":""Max number of member variables to read"",""default"":200}},""required"":[""node_id""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true;

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                if (_opc == null || !_opc.IsConnected) return OpcUaToolHelpers.NotConnected();

                var nodeId = parameters.TryGetProperty("node_id", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(nodeId))
                    return AgentToolResult.Fail("node_id is required (the DB / container node to read)");

                int maxMembers = 200;
                if (parameters.TryGetProperty("max_members", out var mm) && mm.TryGetInt32(out var mv) && mv > 0)
                    maxMembers = mv;

                // Recursively collect the variable leaves under this node (up to two levels, covering DB->member and DB->UDT instance->member).
                var variableIds = new List<string>();
                await CollectVariableLeaves(nodeId.Trim(), variableIds, depth: 0, maxDepth: 3, maxMembers, ct);

                if (variableIds.Count == 0)
                    return AgentToolResult.Ok("该节点下没有可读取的变量成员（可能未展开到变量层，或该 DB 未开放 OPC UA 访问）。");

                var snaps = await _opc.ReadNodesAsync(variableIds, ct);
                var sb = new StringBuilder();
                sb.AppendLine($"DB/container '{nodeId}' has {snaps.Count} variable member(s):");
                foreach (var s in snaps)
                {
                    sb.AppendLine($"- {s.NodeId} = {s.Value ?? "(null)"} " +
                                  $"[type={s.DataType ?? "?"}, quality={OpcUaToolHelpers.QualityText(s.Quality)}]");
                }
                return AgentToolResult.Ok(sb.ToString());
            }
            catch (Exception ex) { return AgentToolResult.Fail("OPC UA read DB failed: " + ex.Message); }
        }

        private async Task CollectVariableLeaves(string nodeId, List<string> sink, int depth, int maxDepth, int maxMembers, CancellationToken ct)
        {
            if (depth > maxDepth || sink.Count >= maxMembers) return;

            var children = await _opc.BrowseAsync(nodeId, ct);
            foreach (var c in children)
            {
                if (sink.Count >= maxMembers) break;
                if (string.Equals(c.NodeClass, "Variable", StringComparison.OrdinalIgnoreCase))
                {
                    sink.Add(c.NodeId);
                }
                else
                {
                    // Containers (UDT instances, nested structures) drill down further.
                    await CollectVariableLeaves(c.NodeId, sink, depth + 1, maxDepth, maxMembers, ct);
                }
            }
        }
    }

    /// <summary>
    /// Writes an OPC UA variable to the running PLC. **High-risk operation**,
    /// with multiple layers of safety control:
    ///   1) Permission=RequiresConfirmation — ReActEngine prompts the user first and aborts on refusal.
    ///   2) SupportsParallel=false — never runs concurrently with other tools.
    ///   3) Parameter-level second confirmation — must pass confirm=true explicitly to prevent LLM mishaps.
    ///   4) Write-enable safety gate — IOpcUaService.WriteEnabled defaults to false; rejected unless enabled in the panel.
    ///   5) Post-write read-back verification — reads the actual value back and reports it rather than assuming success.
    ///   6) Audit log — OpcUaService records every write attempt.
    /// </summary>
    public class OpcUaWriteNodeTool : IAgentTool
    {
        private readonly IOpcUaService _opc;
        public OpcUaWriteNodeTool(IOpcUaService opc) { _opc = opc; }

        public string Name => "opcua_write_node";
        public string Description =>
            "HIGH-RISK: Write a value to a variable on the RUNNING PLC via OPC UA. This changes live device state and can affect physical equipment. " +
            "Requires: (a) OPC UA connected, (b) write explicitly enabled in the OPC UA panel, (c) confirm=true in arguments, (d) user confirmation prompt. " +
            "After writing, the tool reads the value back and reports the actual result. Use ONLY when the user explicitly asks to change/set a live value.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""node_id"":{""type"":""string"",""description"":""NodeId of the variable to write""},""value"":{""type"":""string"",""description"":""Value to write (string form; converted to the node data type)""},""data_type"":{""type"":""string"",""description"":""Optional target type: Boolean/Int16/Int32/Int64/Float/Double/String. Auto-detected if omitted.""},""confirm"":{""type"":""boolean"",""description"":""Must be true to proceed. Explicit safety acknowledgement.""}},""required"":[""node_id"",""value"",""confirm""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.RequiresConfirmation;
        public bool SupportsParallel => false; // Writes must never run concurrently

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                if (_opc == null || !_opc.IsConnected) return OpcUaToolHelpers.NotConnected();

                // Safety gate 1: write-enable must be explicitly turned on in the panel.
                if (!_opc.WriteEnabled)
                    return AgentToolResult.Fail("OPC UA 写入未启用。写运行中 PLC 属高风险操作，请先在 OPC UA 面板开启“允许写入”后再试。");

                // Safety gate 2: explicit parameter-level confirmation to prevent the LLM from triggering a write.
                bool confirm = parameters.TryGetProperty("confirm", out var cf) && cf.ValueKind == JsonValueKind.True;
                if (!confirm)
                    return AgentToolResult.Fail("写入被拒绝：必须在参数中显式传 confirm=true，以确认这是有意的在线写操作。");

                var nodeId = parameters.TryGetProperty("node_id", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(nodeId))
                    return AgentToolResult.Fail("node_id is required");

                if (!parameters.TryGetProperty("value", out var v))
                    return AgentToolResult.Fail("value is required");
                var value = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();

                string dataType = null;
                if (parameters.TryGetProperty("data_type", out var dt) && !string.IsNullOrWhiteSpace(dt.GetString()))
                    dataType = dt.GetString();

                // Perform the write (OpcUaService re-checks WriteEnabled, does type conversion and StatusCode checks internally).
                await _opc.WriteNodeAsync(nodeId.Trim(), value, dataType, ct);

                // Safety gate 5: post-write read-back verification, reporting the actual value rather than assuming success.
                try
                {
                    var snap = await _opc.ReadNodeAsync(nodeId.Trim(), ct);
                    return AgentToolResult.Ok(
                        $"Write OK. Read-back {snap.NodeId} = {snap.Value ?? "(null)"} " +
                        $"[type={snap.DataType ?? "?"}, quality={OpcUaToolHelpers.QualityText(snap.Quality)}]");
                }
                catch
                {
                    return AgentToolResult.Ok($"Write issued to {nodeId} (value={value}). Read-back verification unavailable.");
                }
            }
            catch (Exception ex) { return AgentToolResult.Fail("OPC UA write failed: " + ex.Message); }
        }
    }
}
