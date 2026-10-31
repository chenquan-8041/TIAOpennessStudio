using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// MCP adapter interface — lets the Agent call external tools.
    /// </summary>
    public interface IMcpAdapter
    {
        /// <summary>
        /// Connects to an MCP server.
        /// </summary>
        Task<bool> ConnectAsync(McpServerConfig config);

        /// <summary>
        /// Disconnects.
        /// </summary>
        Task DisconnectAsync(string serverId);

        /// <summary>
        /// Gets the list of available tools.
        /// </summary>
        Task<List<ToolInfo>> GetAvailableToolsAsync(string serverId);

        /// <summary>
        /// Executes a tool.
        /// </summary>
        Task<ToolResult> ExecuteToolAsync(string serverId, string toolName, Dictionary<string, object> parameters);

        /// <summary>
        /// Gets the server status.
        /// </summary>
        McpServerStatusInfo GetServerStatus(string serverId);

        /// <summary>
        /// Gets all server statuses.
        /// </summary>
        List<McpServerStatusInfo> GetAllServerStatus();

        /// <summary>
        /// Tool call event.
        /// </summary>
        event EventHandler<ToolCallEventArgs> OnToolCalled;
    }

    /// <summary>
    /// MCP adapter implementation.
    /// </summary>
    public class McpAdapter : IMcpAdapter, IDisposable
    {
        private readonly ILogger<McpAdapter> _logger;
        private readonly Dictionary<string, HttpClient> _httpClients = new Dictionary<string, HttpClient>();
        private readonly Dictionary<string, McpServerStatusInfo> _serverStatus = new Dictionary<string, McpServerStatusInfo>();
        private readonly Dictionary<string, List<ToolInfo>> _serverTools = new Dictionary<string, List<ToolInfo>>();
        private readonly Dictionary<string, McpServerConfig> _serverConfigs = new Dictionary<string, McpServerConfig>();
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public event EventHandler<ToolCallEventArgs> OnToolCalled;

        public McpAdapter(ILogger<McpAdapter> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region Connection management

        public async Task<bool> ConnectAsync(McpServerConfig config)
        {
            _logger.LogInformation("Connecting to MCP server {ServerId} ({ServerName}) at {Url}", 
                config.Id, config.Name, config.Url);
            
            try
            {
                var client = new HttpClient
                {
                    BaseAddress = new Uri(config.Url),
                    Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
                };

                foreach (var header in config.Headers)
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                }

                _httpClients[config.Id] = client;
                _serverConfigs[config.Id] = config;

                var statusInfo = new McpServerStatusInfo
                {
                    ServerId = config.Id,
                    ServerName = config.Name,
                    Status = McpServerStatus.Connecting
                };

                try
                {
                    var tools = await GetToolsListAsync(config.Id);
                    statusInfo.Status = McpServerStatus.Connected;
                    statusInfo.LastConnected = DateTime.Now;
                    statusInfo.AvailableTools = tools;
                    _serverTools[config.Id] = tools;
                    
                    _logger.LogInformation("Successfully connected to MCP server {ServerId} ({ServerName}), discovered {ToolCount} tools", 
                        config.Id, config.Name, tools.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get tools list from MCP server {ServerId} ({ServerName})", 
                        config.Id, config.Name);
                    statusInfo.Status = McpServerStatus.Error;
                    statusInfo.ErrorMessage = ex.Message;
                }

                _serverStatus[config.Id] = statusInfo;
                return statusInfo.Status == McpServerStatus.Connected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to MCP server {ServerId} ({ServerName}) at {Url}", 
                    config.Id, config.Name, config.Url);
                return false;
            }
        }

        public Task DisconnectAsync(string serverId)
        {
            _logger.LogInformation("Disconnecting from MCP server {ServerId}", serverId);
            
            if (_httpClients.TryGetValue(serverId, out var client))
            {
                client.Dispose();
                _httpClients.Remove(serverId);
            }

            _serverStatus.Remove(serverId);
            _serverTools.Remove(serverId);
            _serverConfigs.Remove(serverId);
            
            _logger.LogInformation("Disconnected from MCP server {ServerId}", serverId);
            return Task.CompletedTask;
        }

        public McpServerStatusInfo GetServerStatus(string serverId)
        {
            _serverStatus.TryGetValue(serverId, out var status);
            return status;
        }

        public List<McpServerStatusInfo> GetAllServerStatus()
        {
            return new List<McpServerStatusInfo>(_serverStatus.Values);
        }

        #endregion

        #region Tool management

        public async Task<List<ToolInfo>> GetAvailableToolsAsync(string serverId)
        {
            if (_serverTools.TryGetValue(serverId, out var cachedTools))
            {
                return cachedTools;
            }

            return await GetToolsListAsync(serverId);
        }

        private async Task<List<ToolInfo>> GetToolsListAsync(string serverId)
        {
            if (!_httpClients.TryGetValue(serverId, out var client))
            {
                throw new InvalidOperationException($"Server {serverId} not connected");
            }

            var request = new McpRequest
            {
                Method = McpMethods.ToolsList,
                Params = new Dictionary<string, object>()
            };

            var response = await SendRequestAsync(client, request);
            
            return ParseToolsList(response);
        }

        /// <summary>
        /// High-risk keywords: a hit marks the operation high risk and forces
        /// RequiresConfirmation (run only after user confirmation). External MCP
        /// tools are untrusted and cannot be audited one by one, so classify
        /// conservatively by name/description.
        /// </summary>
        private static readonly string[] HighRiskKeywords =
        {
            "write", "create", "update", "insert", "modify", "edit", "set", "put", "post", "patch",
            "delete", "remove", "drop", "truncate", "destroy", "kill", "terminate", "purge",
            "import", "upload", "install", "uninstall", "deploy", "publish", "rollback",
            "exec", "execute", "run", "command", "shell", "spawn", "eval", "invoke",
            "compile", "download", "restart", "reboot", "shutdown", "stop", "start",
            "move", "rename", "copy", "overwrite", "format", "grant", "revoke", "chmod", "chown",
            "send", "transfer", "pay", "transaction", "sign", "approve", "reset",
            "写", "删", "改", "建", "导入", "上传", "安装", "执行", "运行", "编译", "下载", "重启", "移动", "发送"
        };

        /// <summary>
        /// Read-only safety keywords: when hit without a high-risk keyword, the tool is
        /// low risk and may be Allowed (no confirmation required).
        /// </summary>
        private static readonly string[] ReadOnlyKeywords =
        {
            "get", "read", "list", "search", "query", "find", "fetch", "lookup", "view",
            "show", "describe", "inspect", "status", "info", "detail", "count", "check",
            "查", "读", "获取", "列", "搜索", "查询", "查看", "状态"
        };

        /// <summary>
        /// Evaluates the safety level for an external MCP tool.
        /// Policy (conservative; defaults to strict):
        ///   1) Hits a high-risk keyword          -> RequiresConfirmation (high-risk ops need user confirmation)
        ///   2) Confirmably read-only, no high-risk -> Allowed (no confirmation)
        ///   3) Unclear (not confirmably read-only) -> RequiresConfirmation (unknown => strict)
        /// Also writes the verdict back to <see cref="ToolInfo.IsDangerous"/>.
        /// </summary>
        private ToolPermissionLevel ClassifyExternalToolPermission(ToolInfo tool)
        {
            var haystack = ((tool?.Name ?? string.Empty) + " " + (tool?.Description ?? string.Empty))
                .ToLowerInvariant();

            bool highRisk = HighRiskKeywords.Any(k => haystack.Contains(k.ToLowerInvariant()));
            if (highRisk)
            {
                if (tool != null) tool.IsDangerous = true;
                _logger?.LogInformation(
                    "MCP external tool '{ToolName}' classified as HIGH-RISK -> RequiresConfirmation", tool?.Name);
                return ToolPermissionLevel.RequiresConfirmation;
            }

            bool readOnly = ReadOnlyKeywords.Any(k => haystack.Contains(k.ToLowerInvariant()));
            if (readOnly)
            {
                if (tool != null) tool.IsDangerous = false;
                return ToolPermissionLevel.Allowed;
            }

            // Not confirmably read-only: treat strictly and require confirmation.
            if (tool != null) tool.IsDangerous = true;
            _logger?.LogInformation(
                "MCP external tool '{ToolName}' could not be verified as read-only -> RequiresConfirmation (fail-safe)",
                tool?.Name);
            return ToolPermissionLevel.RequiresConfirmation;
        }

        /// <summary>
        /// Parses the tool list from an MCP response, handling JsonElement types.
        /// </summary>
        /// <param name="response">The MCP response object.</param>
        /// <returns>The list of tool info.</returns>
        private List<ToolInfo> ParseToolsList(McpResponse response)
        {
            var tools = new List<ToolInfo>();

            if (response == null || response.Error != null)
            {
                return tools;
            }

            try
            {
                if (response.Result is JsonElement jsonElement)
                {
                    if (jsonElement.TryGetProperty("tools", out var toolsProperty))
                    {
                        if (toolsProperty.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var toolElement in toolsProperty.EnumerateArray())
                            {
                                var toolInfo = new ToolInfo();

                                if (toolElement.TryGetProperty("name", out var nameProperty))
                                {
                                    toolInfo.Name = nameProperty.GetString();
                                }

                                if (toolElement.TryGetProperty("description", out var descProperty))
                                {
                                    toolInfo.Description = descProperty.GetString();
                                }

                                if (toolElement.TryGetProperty("inputSchema", out var schemaProperty))
                                {
                                    toolInfo.InputSchema = ParseInputSchemaFromJsonElement(schemaProperty);
                                }

                                // Only add tools with a non-empty Name.
                                if (!string.IsNullOrEmpty(toolInfo.Name))
                                {
                                    // Classify external tools by safety level: high-risk -> user confirmation.
                                    toolInfo.PermissionLevel = ClassifyExternalToolPermission(toolInfo);
                                    tools.Add(toolInfo);
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Fallback to the legacy Dictionary parsing path.
                    var resultObj = response.Result as Dictionary<string, object>;
                    if (resultObj != null && resultObj.ContainsKey("tools"))
                    {
                        var toolsArray = resultObj["tools"] as object[];
                        if (toolsArray != null)
                        {
                            foreach (var tool in toolsArray)
                            {
                                if (tool is Dictionary<string, object> toolDict)
                                {
                                    var toolInfo = new ToolInfo
                                    {
                                        Name = toolDict.ContainsKey("name") ? toolDict["name"].ToString() : "",
                                        Description = toolDict.ContainsKey("description") ? toolDict["description"].ToString() : "",
                                        InputSchema = ParseParameters(toolDict)
                                    };

                                    // Only add tools with a non-empty Name.
                                    if (!string.IsNullOrEmpty(toolInfo.Name))
                                    {
                                        // Classify external tools by safety level: high-risk -> user confirmation.
                                        toolInfo.PermissionLevel = ClassifyExternalToolPermission(toolInfo);
                                        tools.Add(toolInfo);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing tools list");
            }

            return tools;
        }

        /// <summary>
        /// Parses an InputSchema from a JsonElement.
        /// </summary>
        private List<ToolParameter> ParseInputSchemaFromJsonElement(JsonElement schemaElement)
        {
            var parameters = new List<ToolParameter>();

            try
            {
                if (schemaElement.TryGetProperty("properties", out var propertiesElement))
                {
                    string[] requiredFields = null;
                    if (schemaElement.TryGetProperty("required", out var requiredElement))
                    {
                        if (requiredElement.ValueKind == JsonValueKind.Array)
                        {
                            var requiredList = new List<string>();
                            foreach (var item in requiredElement.EnumerateArray())
                            {
                                requiredList.Add(item.GetString());
                            }
                            requiredFields = requiredList.ToArray();
                        }
                    }

                    foreach (var property in propertiesElement.EnumerateObject())
                    {
                        var param = new ToolParameter
                        {
                            Name = property.Name
                        };

                        var propValue = property.Value;

                        if (propValue.TryGetProperty("type", out var typeElement))
                        {
                            param.Type = typeElement.GetString();
                        }

                        if (propValue.TryGetProperty("description", out var descElement))
                        {
                            param.Description = descElement.GetString();
                        }

                        if (requiredFields != null)
                        {
                            param.Required = Array.Exists(requiredFields, r => r == property.Name);
                        }

                        parameters.Add(param);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing input schema");
            }

            return parameters;
        }

        private List<ToolParameter> ParseParameters(Dictionary<string, object> toolDict)
        {
            var parameters = new List<ToolParameter>();
            
            if (toolDict.ContainsKey("inputSchema"))
            {
                var inputSchema = toolDict["inputSchema"] as Dictionary<string, object>;
                if (inputSchema != null && inputSchema.ContainsKey("properties"))
                {
                    var props = inputSchema["properties"] as Dictionary<string, object>;
                    if (props != null)
                    {
                        var required = inputSchema.ContainsKey("required") 
                            ? inputSchema["required"] as object[] 
                            : null;

                        foreach (var prop in props)
                        {
                            var propDict = prop.Value as Dictionary<string, object>;
                            if (propDict != null)
                            {
                                var param = new ToolParameter
                                {
                                    Name = prop.Key,
                                    Type = propDict.ContainsKey("type") ? propDict["type"].ToString() : "string",
                                    Description = propDict.ContainsKey("description") ? propDict["description"].ToString() : ""
                                };

                                if (required != null)
                                {
                                    param.Required = Array.Exists(required, r => r.ToString() == prop.Key);
                                }

                                parameters.Add(param);
                            }
                        }
                    }
                }
            }

            return parameters;
        }

        #endregion

        #region Tool execution

        public async Task<ToolResult> ExecuteToolAsync(string serverId, string toolName, Dictionary<string, object> parameters)
        {
            _logger.LogInformation("Executing MCP tool {ToolName} on server {ServerId}", toolName, serverId);
            var startTime = DateTime.Now;
            var result = new ToolResult();

            try
            {
                if (!_httpClients.TryGetValue(serverId, out var client))
                {
                    _logger.LogError("Server {ServerId} not connected for tool execution", serverId);
                    throw new InvalidOperationException($"Server {serverId} not connected");
                }

                // Optional: Validate parameters against inputSchema (NFR12)
                if (_serverTools.TryGetValue(serverId, out var tools))
                {
                    var toolInfo = tools.Find(t => t.Name == toolName);
                    if (toolInfo != null && toolInfo.InputSchema != null && toolInfo.InputSchema.Count > 0)
                    {
                        ValidateParameters(parameters, toolInfo.InputSchema, toolName);
                    }
                }

                OnToolCalled?.Invoke(this, new ToolCallEventArgs
                {
                    ServerId = serverId,
                    ToolName = toolName,
                    Parameters = parameters
                });

                var request = new McpRequest
                {
                    Method = McpMethods.ToolsCall,
                    Params = new Dictionary<string, object>
                    {
                        { "name", toolName },
                        { "arguments", parameters }
                    }
                };

                var response = await SendRequestAsync(client, request);

                if (response?.Error != null)
                {
                    _logger.LogError("Tool execution failed for {ToolName} on server {ServerId}: {ErrorMessage}", 
                        toolName, serverId, response.Error.Message);
                    result.Success = false;
                    result.Error = response.Error.Message;
                }
                else if (response?.Result != null)
                {
                    result.Success = true;
                    result.Result = response.Result;
                    result.Output = SerializeObject(response.Result);
                    _logger.LogInformation("Successfully executed tool {ToolName} on server {ServerId} in {ExecutionTimeMs}ms", 
                        toolName, serverId, (DateTime.Now - startTime).TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during tool execution for {ToolName} on server {ServerId}", 
                    toolName, serverId);
                result.Success = false;
                result.Error = ex.Message;
            }
            finally
            {
                result.ExecutionTimeMs = (long)(DateTime.Now - startTime).TotalMilliseconds;
            }

            return result;
        }

        #endregion

        #region HTTP communication

        private async Task<McpResponse> SendRequestAsync(HttpClient client, McpRequest request)
        {
            McpResponse response;
            bool jsonrpcFieldPresent;
            try
            {
                // Ensure Params is not null (set to empty dictionary if null)
                if (request.Params == null)
                {
                    request.Params = new Dictionary<string, object>();
                }

                var json = JsonSerializer.Serialize(request, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Send HTTP POST to correct endpoint (empty string "" uses BaseAddress)
                var httpResponse = await client.PostAsync("", content);
                var responseJson = await httpResponse.Content.ReadAsStringAsync();

                // Detect whether the raw payload actually carried a "jsonrpc" field.
                // The model's default initializer (= "2.0") would otherwise mask a missing field.
                jsonrpcFieldPresent = false;
                using (var doc = JsonDocument.Parse(responseJson))
                {
                    jsonrpcFieldPresent = doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("jsonrpc", out _);
                }

                response = JsonSerializer.Deserialize<McpResponse>(responseJson, _jsonOptions);
            }
            catch (HttpRequestException httpEx)
            {
                // HTTP-specific exception handling — network failures return an error response.
                return new McpResponse
                {
                    Id = request.Id,
                    Jsonrpc = "2.0",
                    Error = McpError.ParseError(McpErrorCodes.InternalError, $"HTTP error: {httpEx.Message}")
                };
            }
            catch (Exception ex)
            {
                // General transport/deserialization errors return an error response.
                return new McpResponse
                {
                    Id = request.Id,
                    Jsonrpc = "2.0",
                    Error = McpError.ParseError(McpErrorCodes.InternalError, ex.Message)
                };
            }

            // JSON-RPC protocol validation — performed outside the try/catch so violations
            // propagate to the caller instead of being swallowed as transport errors.
            if (response != null && response.Id != request.Id)
            {
                throw new Exception("Response ID mismatch");
            }

            if (response != null && (!jsonrpcFieldPresent || response.Jsonrpc != "2.0"))
            {
                throw new Exception("Invalid JSON-RPC version in response");
            }

            return response;
        }

        private string SerializeObject(object obj)
        {
            try
            {
                return JsonSerializer.Serialize(obj, _jsonOptions);
            }
            catch
            {
                return obj?.ToString() ?? "";
            }
        }

        #endregion

        #region Resource management

        /// <summary>
        /// Gets the resource list.
        /// </summary>
        public async Task<List<string>> GetResourcesAsync(string serverId)
        {
            if (!_httpClients.TryGetValue(serverId, out var client))
            {
                throw new InvalidOperationException($"Server {serverId} not connected");
            }

            var request = new McpRequest
            {
                Method = McpMethods.ResourcesList
            };

            var response = await SendRequestAsync(client, request);
            var resources = new List<string>();

            if (response?.Result != null)
            {
                var resultObj = response.Result as Dictionary<string, object>;
                if (resultObj != null && resultObj.ContainsKey("resources"))
                {
                    var resourcesArray = resultObj["resources"] as object[];
                    if (resourcesArray != null)
                    {
                        foreach (var r in resourcesArray)
                        {
                            if (r is Dictionary<string, object> rDict)
                            {
                                resources.Add(rDict.ContainsKey("uri") ? rDict["uri"].ToString() : "");
                            }
                        }
                    }
                }
            }

            return resources;
        }

        #endregion

        #region Parameter Validation

        /// <summary>
        /// Validates parameters against inputSchema (NFR12)
        /// </summary>
        /// <param name="parameters">Provided parameters</param>
        /// <param name="schema">Input schema definition</param>
        /// <param name="toolName">Tool name for error messages</param>
        private void ValidateParameters(Dictionary<string, object> parameters, List<ToolParameter> schema, string toolName)
        {
            if (parameters == null)
            {
                parameters = new Dictionary<string, object>();
            }

            foreach (var paramDef in schema)
            {
                if (paramDef.Required)
                {
                    if (!parameters.ContainsKey(paramDef.Name))
                    {
                        throw new ArgumentException(
                            $"Tool '{toolName}' requires parameter '{paramDef.Name}' but it was not provided. " +
                            $"Description: {paramDef.Description}",
                            nameof(parameters));
                    }

                    if (parameters[paramDef.Name] == null)
                    {
                        throw new ArgumentException(
                            $"Tool '{toolName}' requires parameter '{paramDef.Name}' but it was null. " +
                            $"Description: {paramDef.Description}",
                            nameof(parameters));
                    }
                }
            }

            _logger.LogDebug("Parameter validation passed for tool {ToolName}", toolName);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            foreach (var client in _httpClients.Values)
            {
                client.Dispose();
            }
            _httpClients.Clear();
        }

        #endregion
    }
}