using System;
using System.Collections.Generic;
using System.Linq;

namespace TIAOpennessStudio.Models
{
    public enum ToolPermissionLevel
    {
        Allowed,
        RequiresConfirmation,
        Denied
    }

    public class ToolPermission
    {
        public string ToolName { get; set; }
        public ToolPermissionLevel Level { get; set; }
        public string Reason { get; set; }
    }

    public class ToolCall
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Rid { get; set; }
        public string PlanId { get; set; }
        public string ToolName { get; set; }
        public string ServerId { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public DateTime CallStartTime { get; set; } = DateTime.UtcNow;
        public DateTime? CallEndTime { get; set; }
        public long ExecutionTimeMs { get; set; }
        public ToolResult Result { get; set; }
        public int RetryCount { get; set; }
        public string ErrorCode { get; set; }
        public bool WasSuccessful => Result?.Success ?? false;
    }

    public class ToolCallTrace
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Rid { get; set; }
        public string PlanId { get; set; }
        public List<ToolCall> Calls { get; set; } = new List<ToolCall>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public int TotalCalls => Calls.Count;
        public int SuccessfulCalls => Calls.Count(c => c.WasSuccessful);
        public int FailedCalls => Calls.Count(c => !c.WasSuccessful);
        public long TotalExecutionTimeMs => Calls.Sum(c => c.ExecutionTimeMs);

        public ToolCall AddCall(string toolName, string serverId = "builtin")
        {
            var call = new ToolCall
            {
                Rid = Rid,
                PlanId = PlanId,
                ToolName = toolName,
                ServerId = serverId
            };
            Calls.Add(call);
            return call;
        }

        public string ToReplayScript()
        {
            var lines = new List<string> { "# Tool Call Replay Script", $"# Rid: {Rid}", $"# PlanId: {PlanId}", "" };
            foreach (var call in Calls)
            {
                lines.Add($"# === Call {call.Id} ===");
                lines.Add($"# Tool: {call.ToolName}");
                lines.Add($"# Server: {call.ServerId}");
                lines.Add($"# Time: {call.CallStartTime:yyyy-MM-dd HH:mm:ss}");
                if (call.Result != null)
                {
                    lines.Add($"# Success: {call.Result.Success}");
                    lines.Add($"# ExecutionTime: {call.ExecutionTimeMs}ms");
                    if (!string.IsNullOrEmpty(call.Result.Error))
                    {
                        lines.Add($"# Error: {call.Result.Error}");
                    }
                }
                lines.Add($"call('{call.ToolName}', {SerializeParams(call.Parameters)})");
                lines.Add("");
            }
            return string.Join("\n", lines);
        }

        private static string SerializeParams(Dictionary<string, object> parameters)
        {
            if (parameters == null || parameters.Count == 0) return "{}";
            var parts = parameters.Select(kv => $"\"{kv.Key}\": {SerializeValue(kv.Value)}");
            return "{" + string.Join(", ", parts) + "}";
        }

        private static string SerializeValue(object value)
        {
            if (value == null) return "null";
            if (value is string s) return $"\"{s.Replace("\"", "\\\"")}\"";
            if (value is bool b) return b.ToString().ToLower();
            if (value is int i) return i.ToString();
            if (value is double d) return d.ToString();
            return value.ToString();
        }
    }

    public class ToolRegistry
    {
        private readonly Dictionary<string, ToolInfo> _tools = new Dictionary<string, ToolInfo>();
        private readonly Dictionary<string, ToolPermission> _permissions = new Dictionary<string, ToolPermission>();
        private readonly HashSet<string> _dangerousTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "file_write",
            "execute_command",
            "delete_file",
            "delete_directory",
            "import_block",
            "write_block"
        };

        public ToolRegistry()
        {
            InitializeDefaultTools();
        }

        private void InitializeDefaultTools()
        {
            var defaultTools = new List<ToolInfo>
            {
                CreateTool("web_search", "Search the web for information", new[]
                {
                    Param("query", "string", true, "Search query"),
                    Param("max_results", "integer", false, "Maximum results", 5)
                }),
                CreateTool("file_read", "Read content from a file", new[]
                {
                    Param("path", "string", true, "File path to read"),
                    Param("encoding", "string", false, "File encoding", "UTF-8")
                }),
                CreateTool("file_write", "Write content to a file (DANGEROUS)", new[]
                {
                    Param("path", "string", true, "File path to write"),
                    Param("content", "string", true, "Content to write"),
                    Param("encoding", "string", false, "File encoding", "UTF-8")
                }, isDangerous: true),
                CreateTool("file_list", "List files in a directory", new[]
                {
                    Param("path", "string", true, "Directory path"),
                    Param("pattern", "string", false, "File pattern", "*"),
                    Param("recursive", "boolean", false, "Search recursively", false)
                }),
                CreateTool("calculator", "Perform mathematical calculations", new[]
                {
                    Param("expression", "string", true, "Mathematical expression")
                }),
                CreateTool("execute_command", "Execute a shell command (DANGEROUS)", new[]
                {
                    Param("command", "string", true, "Command to execute"),
                    Param("working_directory", "string", false, "Working directory")
                }, isDangerous: true),
                CreateTool("get_system_info", "Get system information", Array.Empty<(string, string, bool, string, object)>()),
                CreateTool("json_parser", "Parse and validate JSON", new[]
                {
                    Param("json", "string", true, "JSON string to parse")
                }),
                CreateTool("export_block", "Export a TIA Portal block to XML/SCL", new[]
                {
                    Param("block_name", "string", true, "Block name"),
                    Param("block_type", "string", false, "Block type (FB/FC/OB/DB)", "FB"),
                    Param("format", "string", false, "Export format (xml/scl)", "xml")
                }, isDangerous: false),
                CreateTool("import_block", "Import code to TIA Portal (DANGEROUS)", new[]
                {
                    Param("block_name", "string", true, "Block name"),
                    Param("block_type", "string", false, "Block type", "FB"),
                    Param("code", "string", true, "SCL code to import")
                }, isDangerous: true),
                CreateTool("compile_project", "Trigger TIA Portal compilation", new[]
                {
                    Param("target", "string", false, "Compile target (all/block name)", "all")
                }, isDangerous: false)
            };

            foreach (var tool in defaultTools)
            {
                RegisterTool(tool);
            }
        }

        private static ToolInfo CreateTool(string name, string description, (string name, string type, bool required, string desc, object defaultValue)[] parameters, bool isDangerous = false)
        {
            var tool = new ToolInfo
            {
                Name = name,
                Description = description,
                IsDangerous = isDangerous,
                PermissionLevel = isDangerous ? ToolPermissionLevel.RequiresConfirmation : ToolPermissionLevel.Allowed
            };

            foreach (var p in parameters)
            {
                tool.InputSchema.Add(new ToolParameter
                {
                    Name = p.name,
                    Type = p.type,
                    Required = p.required,
                    Description = p.desc,
                    Default = p.defaultValue
                });
            }

            return tool;
        }

        private static (string, string, bool, string, object) Param(string name, string type, bool required, string desc, object defaultValue = null)
        {
            return (name, type, required, desc, defaultValue);
        }

        public void RegisterTool(ToolInfo tool)
        {
            _tools[tool.Name.ToLower()] = tool;
        }

        public ToolInfo GetTool(string name)
        {
            return _tools.TryGetValue(name.ToLower(), out var tool) ? tool : null;
        }

        public List<ToolInfo> GetAllTools()
        {
            return _tools.Values.ToList();
        }

        public List<ToolInfo> GetAllowedTools()
        {
            return _tools.Values.Where(t => GetPermission(t.Name) != ToolPermissionLevel.Denied).ToList();
        }

        public void SetPermission(string toolName, ToolPermissionLevel level, string reason = "")
        {
            _permissions[toolName.ToLower()] = new ToolPermission
            {
                ToolName = toolName,
                Level = level,
                Reason = reason
            };
        }

        public ToolPermissionLevel GetPermission(string toolName)
        {
            if (_permissions.TryGetValue(toolName.ToLower(), out var perm))
            {
                return perm.Level;
            }

            if (_dangerousTools.Contains(toolName))
            {
                return ToolPermissionLevel.RequiresConfirmation;
            }

            return ToolPermissionLevel.Allowed;
        }

        public (bool allowed, string reason) CheckPermission(string toolName)
        {
            var level = GetPermission(toolName);
            if (level == ToolPermissionLevel.Denied)
            {
                return (false, "Tool is explicitly denied");
            }

            if (level == ToolPermissionLevel.RequiresConfirmation)
            {
                return (false, "Tool requires user confirmation");
            }

            return (true, "Allowed");
        }

        public ValidationResult ValidateParameters(string toolName, Dictionary<string, object> parameters)
        {
            var tool = GetTool(toolName);
            if (tool == null)
            {
                return new ValidationResult { IsValid = false, ErrorMessage = "Unknown tool" };
            }

            var errors = new List<string>();
            var missingParams = new List<string>();

            foreach (var param in tool.InputSchema.Where(p => p.Required))
            {
                if (!parameters.ContainsKey(param.Name) || parameters[param.Name] == null)
                {
                    missingParams.Add(param.Name);
                }
            }

            if (missingParams.Count > 0)
            {
                errors.Add($"Missing required parameters: {string.Join(", ", missingParams)}");
            }

            foreach (var kv in parameters)
            {
                var paramDef = tool.InputSchema.FirstOrDefault(p => p.Name == kv.Key);
                if (paramDef != null)
                {
                    var validationError = ValidateParameterType(kv.Key, kv.Value, paramDef.Type);
                    if (!string.IsNullOrEmpty(validationError))
                    {
                        errors.Add(validationError);
                    }
                }
            }

            return new ValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors,
                MissingParameters = missingParams
            };
        }

        private static string ValidateParameterType(string name, object value, string expectedType)
        {
            if (value == null) return null;

            var actualType = value.GetType().Name.ToLower();
            var typeMatch = expectedType.ToLower() switch
            {
                "string" => value is string,
                "integer" or "int" => value is int || (value is string s && int.TryParse(s, out _)),
                "number" => value is int || value is double || value is float || (value is string s && double.TryParse(s, out _)),
                "boolean" or "bool" => value is bool || value is string s && (bool.TryParse(s, out _) || s == "true" || s == "false"),
                "object" => value is Dictionary<string, object> || value is object,
                "array" => value is object[],
                _ => true
            };

            return typeMatch ? null : $"Parameter '{name}' expected type '{expectedType}' but got '{actualType}'";
        }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> MissingParameters { get; set; } = new List<string>();

        public string ErrorMessage
        {
            get => string.Join("; ", Errors);
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    Errors.Clear();
                    Errors.Add(value);
                }
            }
        }
    }

    public static class ToolCallTracker
    {
        private static readonly Dictionary<string, ToolCallTrace> _activeTraces = new Dictionary<string, ToolCallTrace>();
        private static readonly List<ToolCallTrace> _completedTraces = new List<ToolCallTrace>();
        private const int MaxCompletedTraces = 100;

        public static ToolCallTrace CreateTrace(string rid, string planId)
        {
            var trace = new ToolCallTrace { Rid = rid, PlanId = planId };
            _activeTraces[rid] = trace;
            return trace;
        }

        public static ToolCallTrace GetTrace(string rid)
        {
            return _activeTraces.TryGetValue(rid, out var trace) ? trace : null;
        }

        public static void CompleteTrace(string rid)
        {
            if (_activeTraces.TryGetValue(rid, out var trace))
            {
                trace.CompletedAt = DateTime.UtcNow;
                _activeTraces.Remove(rid);
                _completedTraces.Add(trace);

                while (_completedTraces.Count > MaxCompletedTraces)
                {
                    _completedTraces.RemoveAt(0);
                }
            }
        }

        public static ToolCallTrace ReplayTrace(string rid)
        {
            var trace = _completedTraces.FirstOrDefault(t => t.Rid == rid);
            return trace;
        }
    }
}
