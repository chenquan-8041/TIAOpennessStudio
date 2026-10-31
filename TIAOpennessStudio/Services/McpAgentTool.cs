using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Wraps an MCP tool so it can be used through the IAgentTool interface.
    /// </summary>
    public class McpAgentTool : IAgentTool
    {
        private readonly string _serverId;
        private readonly ToolInfo _toolInfo;
        private readonly IMcpAdapter _mcpAdapter;
        private readonly ILogger _logger;

        public McpAgentTool(
            string serverId,
            ToolInfo toolInfo,
            IMcpAdapter mcpAdapter,
            ILogger logger)
        {
            _serverId = serverId ?? throw new ArgumentNullException(nameof(serverId));
            _toolInfo = toolInfo ?? throw new ArgumentNullException(nameof(toolInfo));
            _mcpAdapter = mcpAdapter ?? throw new ArgumentNullException(nameof(mcpAdapter));
            _logger = logger;
        }

        /// <summary>
        /// The tool name, taken from ToolInfo.Name.
        /// </summary>
        public string Name => _toolInfo.Name;

        /// <summary>
        /// The tool description, taken from ToolInfo.Description.
        /// </summary>
        public string Description => _toolInfo.Description ?? "";

        /// <summary>
        /// The input parameter schema, serialized from ToolInfo.InputSchema into a JSON string.
        /// </summary>
        public string InputSchema
        {
            get
            {
                if (_toolInfo.InputSchema == null || _toolInfo.InputSchema.Count == 0)
                {
                    return "{}";
                }

                try
                {
                    // Build the JSON Schema.
                    var schema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() },
                        { "required", new List<string>() }
                    };

                    var properties = (Dictionary<string, object>)schema["properties"];
                    var required = (List<string>)schema["required"];

                    foreach (var param in _toolInfo.InputSchema)
                    {
                        var paramSchema = new Dictionary<string, object>
                        {
                            { "type", param.Type ?? "string" }
                        };

                        if (!string.IsNullOrEmpty(param.Description))
                        {
                            paramSchema["description"] = param.Description;
                        }

                        if (param.Default != null)
                        {
                            paramSchema["default"] = param.Default;
                        }

                        properties[param.Name] = paramSchema;

                        if (param.Required)
                        {
                            required.Add(param.Name);
                        }
                    }

                    return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = false });
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to serialize InputSchema for tool {ToolName}", Name);
                    return "{}";
                }
            }
        }

        /// <summary>
        /// The tool permission level, taken from ToolInfo.PermissionLevel.
        /// </summary>
        public ToolPermissionLevel Permission => _toolInfo.PermissionLevel;

        /// <summary>
        /// MCP tools call an external server over HTTP and never touch the TIA STA
        /// thread, so they can run concurrently.
        /// </summary>
        public bool SupportsParallel => true;

        /// <summary>
        /// Executes the MCP tool.
        /// </summary>
        /// <param name="parameters">Tool parameters (as a JsonElement).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The tool execution result.</returns>
        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            _logger?.LogInformation("Executing MCP tool {ToolName} on server {ServerId}", Name, _serverId);

            try
            {
                var parametersDict = ConvertJsonElementToDictionary(parameters);

                var toolResult = await _mcpAdapter.ExecuteToolAsync(_serverId, Name, parametersDict);

                if (toolResult.Success)
                {
                    _logger?.LogInformation("Successfully executed MCP tool {ToolName} on server {ServerId} in {ExecutionTimeMs}ms",
                        Name, _serverId, toolResult.ExecutionTimeMs);

                    // Prefer the text output; fall back to the structured result.
                    var resultData = !string.IsNullOrEmpty(toolResult.Output) 
                        ? toolResult.Output 
                        : toolResult.Result;

                    return AgentToolResult.Ok(resultData);
                }
                else
                {
                    _logger?.LogError("MCP tool {ToolName} on server {ServerId} failed: {Error}",
                        Name, _serverId, toolResult.Error);

                    return AgentToolResult.Fail(toolResult.Error ?? "Tool execution failed");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Exception during ExecuteAsync for MCP tool {ToolName} on server {ServerId}",
                    Name, _serverId);

                return AgentToolResult.Fail($"Tool execution exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Converts a JsonElement into a Dictionary&lt;string, object&gt;.
        /// </summary>
        private Dictionary<string, object> ConvertJsonElementToDictionary(JsonElement element)
        {
            var dict = new Dictionary<string, object>();

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    dict[property.Name] = ConvertJsonElementToObject(property.Value);
                }
            }

            return dict;
        }

        /// <summary>
        /// Converts a JsonElement into the corresponding C# object type.
        /// </summary>
        private object ConvertJsonElementToObject(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    if (element.TryGetInt32(out int intValue))
                        return intValue;
                    if (element.TryGetInt64(out long longValue))
                        return longValue;
                    if (element.TryGetDouble(out double doubleValue))
                        return doubleValue;
                    return element.GetDecimal();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                    return null;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var item in element.EnumerateArray())
                    {
                        list.Add(ConvertJsonElementToObject(item));
                    }
                    return list;
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var property in element.EnumerateObject())
                    {
                        dict[property.Name] = ConvertJsonElementToObject(property.Value);
                    }
                    return dict;
                default:
                    return element.ToString();
            }
        }
    }
}
