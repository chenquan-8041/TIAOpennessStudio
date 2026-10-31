using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Models
{
    #region MCP message models

    /// <summary>
    /// MCP message type.
    /// </summary>
    public enum McpMessageType
    {
        Request,
        Response,
        Error,
        Notification
    }

    /// <summary>
    /// MCP protocol method names.
    /// </summary>
    public static class McpMethods
    {
        public const string Initialize = "initialize";
        public const string ToolsList = "tools/list";
        public const string ToolsCall = "tools/call";
        public const string ResourcesList = "resources/list";
        public const string ResourcesRead = "resources/read";
        public const string PromptsList = "prompts/list";
        public const string PromptsGet = "prompts/get";
    }

    /// <summary>
    /// Base class shared by all MCP messages.
    /// </summary>
    public class McpMessage
    {
        [System.Text.Json.Serialization.JsonPropertyName("jsonrpc")]
        public string Jsonrpc { get; set; } = "2.0";

        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [System.Text.Json.Serialization.JsonIgnore]
        public McpMessageType MessageType { get; set; }
    }

    /// <summary>
    /// MCP request message.
    /// </summary>
    public class McpRequest : McpMessage
    {
        [System.Text.Json.Serialization.JsonPropertyName("method")]
        public string Method { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("params")]
        public Dictionary<string, object> Params { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// MCP response message.
    /// </summary>
    public class McpResponse : McpMessage
    {
        [System.Text.Json.Serialization.JsonPropertyName("result")]
        public object Result { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public McpError Error { get; set; }
    }

    /// <summary>
    /// MCP error details.
    /// </summary>
    public class McpError
    {
        public int Code { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }

        public static McpError ParseError(int code, string message)
        {
            return new McpError { Code = code, Message = message };
        }
    }

    /// <summary>
    /// Standard MCP error codes.
    /// </summary>
    public static class McpErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
        public const int ServerError = -32000;
    }

    #endregion

    #region MCP tool models

    /// <summary>
    /// Describes a tool exposed over MCP.
    /// </summary>
    public class ToolInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<ToolParameter> InputSchema { get; set; } = new List<ToolParameter>();
        public bool IsDangerous { get; set; }
        // Built-in tools default to Allowed; each may be promoted explicitly at
        // registration. External MCP tools bypass this default:
        // McpAdapter.ClassifyExternalToolPermission rates them by name/description at
        // resolve time — high-risk -> RequiresConfirmation, unknown -> RequiresConfirmation (fail safe).
        public ToolPermissionLevel PermissionLevel { get; set; } = ToolPermissionLevel.Allowed;
        public string Category { get; set; }
        public string Version { get; set; } = "1.0";
    }

    /// <summary>
    /// Describes one tool parameter.
    /// </summary>
    public class ToolParameter
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public bool Required { get; set; }
        public object Default { get; set; }
    }

    /// <summary>
    /// A request to call a tool.
    /// </summary>
    public class ToolCallRequest
    {
        public string Name { get; set; }
        public Dictionary<string, object> Arguments { get; set; }
    }

    /// <summary>
    /// Result of a tool call.
    /// </summary>
    public class ToolResult
    {
        public bool Success { get; set; }
        public string Output { get; set; }
        public object Result { get; set; }
        public string Error { get; set; }
        public long ExecutionTimeMs { get; set; }
    }

    #endregion

    #region MCP server models

    /// <summary>
    /// Connection state of an MCP server.
    /// </summary>
    public enum McpServerStatus
    {
        Disconnected,
        Connecting,
        Connected,
        Error
    }

    /// <summary>
    /// Configuration for one MCP server.
    /// </summary>
    public class McpServerConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Url { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public int TimeoutSeconds { get; set; } = 30;
        public bool AutoReconnect { get; set; } = true;
    }

    /// <summary>
    /// Runtime status report for an MCP server.
    /// </summary>
    public class McpServerStatusInfo
    {
        public string ServerId { get; set; }
        public string ServerName { get; set; }
        public McpServerStatus Status { get; set; }
        public DateTime LastConnected { get; set; }
        public string ErrorMessage { get; set; }
        public List<ToolInfo> AvailableTools { get; set; } = new List<ToolInfo>();
    }

    #endregion

    #region Tool call events

    /// <summary>
    /// Event args for a tool call event.
    /// </summary>
    public class ToolCallEventArgs : EventArgs
    {
        public string ServerId { get; set; }
        public string ToolName { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    #endregion
}