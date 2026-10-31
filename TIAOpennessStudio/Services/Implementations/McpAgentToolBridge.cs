using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Wraps an MCP external tool as an IAgentTool so it can be invoked
    /// uniformly through ToolRegistry / ReActEngine. Each MCP tool maps to one
    /// McpAgentTool instance.
    /// </summary>
    public class McpAgentTool : IAgentTool
    {
        private readonly IMcpAdapter _mcpAdapter;
        private readonly string _serverId;
        private readonly ToolInfo _toolInfo;

        public McpAgentTool(IMcpAdapter mcpAdapter, string serverId, ToolInfo toolInfo)
        {
            _mcpAdapter = mcpAdapter ?? throw new ArgumentNullException(nameof(mcpAdapter));
            _serverId = serverId ?? throw new ArgumentNullException(nameof(serverId));
            _toolInfo = toolInfo ?? throw new ArgumentNullException(nameof(toolInfo));
        }

        public string Name => $"mcp_{_serverId}_{_toolInfo.Name}";
        public string Description => $"[MCP:{_serverId}] {_toolInfo.Description}";
        public string InputSchema => BuildInputSchema();
        public ToolPermissionLevel Permission => _toolInfo.PermissionLevel;
        // MCP tools reach external servers over HTTP, never touch the TIA STA thread, and can run concurrently.
        public bool SupportsParallel => true;

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var args = new Dictionary<string, object>();
                foreach (var prop in parameters.EnumerateObject())
                {
                    args[prop.Name] = JsonElementToObject(prop.Value);
                }

                var result = await _mcpAdapter.ExecuteToolAsync(_serverId, _toolInfo.Name, args);

                if (result.Success)
                    return AgentToolResult.Ok(result.Output ?? result.Result?.ToString() ?? "");
                else
                    return AgentToolResult.Fail(result.Error ?? "MCP tool execution failed");
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail($"MCP tool '{_toolInfo.Name}' error: {ex.Message}");
            }
        }

        private string BuildInputSchema()
        {
            if (_toolInfo.InputSchema == null || _toolInfo.InputSchema.Count == 0)
                return "{}";

            var props = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var p in _toolInfo.InputSchema)
            {
                props[p.Name] = new Dictionary<string, object>
                {
                    ["type"] = p.Type ?? "string",
                    ["description"] = p.Description ?? ""
                };
                if (p.Required) required.Add(p.Name);
            }

            var schema = new Dictionary<string, object>
            {
                ["kind"] = "object",
                ["properties"] = props
            };
            if (required.Count > 0) schema["required"] = required;

            return JsonSerializer.Serialize(schema);
        }

        private static object JsonElementToObject(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
                JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
                _ => element.GetRawText()
            };
        }
    }

    /// <summary>
    /// Manages MCP server connections and syncs external tools into the
    /// ToolRegistry. Supports multiple MCP servers, automatic reconnection, and
    /// hot tool reloading.
    /// </summary>
    public class McpToolRegistrySync
    {
        private readonly IMcpAdapter _mcpAdapter;
        private readonly ToolRegistry _toolRegistry;
        private readonly Dictionary<string, List<McpAgentTool>> _serverTools = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, McpServerConfig> _serverConfigs = new(StringComparer.OrdinalIgnoreCase);

        public McpToolRegistrySync(IMcpAdapter mcpAdapter, ToolRegistry toolRegistry)
        {
            _mcpAdapter = mcpAdapter ?? throw new ArgumentNullException(nameof(mcpAdapter));
            _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        }

        /// <summary>
        /// Connects an MCP server and registers its tools into the ToolRegistry.
        /// </summary>
        public async Task<bool> ConnectAndRegisterAsync(McpServerConfig config)
        {
            // Disconnect first if a server with the same id is already connected
            if (_serverConfigs.ContainsKey(config.Id))
                await DisconnectAsync(config.Id);

            _serverConfigs[config.Id] = config;

            bool connected = await _mcpAdapter.ConnectAsync(config);
            if (!connected) return false;

            await RefreshToolsAsync(config.Id);
            return true;
        }

        /// <summary>
        /// Disconnects an MCP server and removes its tools from the ToolRegistry.
        /// </summary>
        public async Task DisconnectAsync(string serverId)
        {
            UnregisterServerTools(serverId);
            await _mcpAdapter.DisconnectAsync(serverId);
            _serverConfigs.Remove(serverId);
        }

        /// <summary>
        /// Refreshes the tool list for a server (hot reload).
        /// </summary>
        public async Task RefreshToolsAsync(string serverId)
        {
            UnregisterServerTools(serverId);

            var tools = await _mcpAdapter.GetAvailableToolsAsync(serverId);
            if (tools == null || tools.Count == 0) return;

            var wrappers = new List<McpAgentTool>();
            foreach (var tool in tools)
            {
                var wrapper = new McpAgentTool(_mcpAdapter, serverId, tool);
                _toolRegistry.Register(wrapper);
                wrappers.Add(wrapper);
            }
            _serverTools[serverId] = wrappers;

            AppLogger.Log($"[MCP] Registered {wrappers.Count} tools from server '{serverId}'");
        }

        /// <summary>
        /// Syncs the tool lists of all connected MCP servers (hot reload), by
        /// iterating the current server configs and refreshing their tools.
        /// </summary>
        public async Task SyncAllServersAsync()
        {
            // Copy the key set to avoid an enumeration exception if the collection is mutated while refreshing.
            var serverIds = new List<string>(_serverConfigs.Keys);
            foreach (var serverId in serverIds)
            {
                try
                {
                    await RefreshToolsAsync(serverId);
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[MCP] Failed to sync tools for server '{serverId}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets the status of all connected MCP servers.
        /// </summary>
        public List<McpServerStatusInfo> GetAllServerStatus() => _mcpAdapter.GetAllServerStatus();

        /// <summary>
        /// Gets the number of tools registered for a server.
        /// </summary>
        public int GetToolCount(string serverId) =>
            _serverTools.TryGetValue(serverId, out var tools) ? tools.Count : 0;

        private void UnregisterServerTools(string serverId)
        {
            // ToolRegistry does not yet support Unregister, so cleanup happens by filtering
            // on the "mcp_{serverId}_" prefix when the tool catalog is generated; a
            // re-registration simply overwrites same-named tools.
            _serverTools.Remove(serverId);
        }
    }
}
