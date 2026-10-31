using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations.Tools
{
    /// <summary>Read content from a file</summary>
    public class FileReadTool : IAgentTool
    {
        public string Name => "file_read";
        public string Description => "Read content from a file on disk. Returns the file contents as text.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""File path to read""},""encoding"":{""type"":""string"",""description"":""File encoding"",""default"":""UTF-8""}},""required"":[""path""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Read-only file access, pure .NET

        public Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var path = parameters.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(path))
                    return Task.FromResult(AgentToolResult.Fail("path is required"));

                if (!File.Exists(path))
                    return Task.FromResult(AgentToolResult.Fail("File not found: " + path));

                var encoding = "UTF-8";
                if (parameters.TryGetProperty("encoding", out var enc) && !string.IsNullOrEmpty(enc.GetString()))
                    encoding = enc.GetString();

                var content = File.ReadAllText(path, Encoding.GetEncoding(encoding));
                return Task.FromResult(AgentToolResult.Ok(content));
            }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail(ex.Message)); }
        }
    }

    /// <summary>Write content to a file</summary>
    public class FileWriteTool : IAgentTool
    {
        public string Name => "file_write";
        public string Description => "Write content to a file on disk. Creates parent directories if needed.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""File path to write""},""content"":{""type"":""string"",""description"":""Content to write""},""encoding"":{""type"":""string"",""description"":""File encoding"",""default"":""UTF-8""}},""required"":[""path"",""content""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.RequiresConfirmation;
        public bool SupportsParallel => false; // Writes files; avoid concurrent write conflicts

        public Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var path = parameters.TryGetProperty("path", out var p) ? p.GetString() : null;
                var content = parameters.TryGetProperty("content", out var c) ? c.GetString() : null;
                if (string.IsNullOrEmpty(path))
                    return Task.FromResult(AgentToolResult.Fail("path is required"));
                if (content == null)
                    return Task.FromResult(AgentToolResult.Fail("content is required"));

                // Path sandbox: only allow writes inside whitelisted roots, blocking traversal and writes to system directories.
                if (!ToolSecurity.IsWritePathAllowed(path, out var fullPath, out var reason))
                    return Task.FromResult(AgentToolResult.Fail("Write refused: " + reason));

                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var encoding = "UTF-8";
                if (parameters.TryGetProperty("encoding", out var enc) && !string.IsNullOrEmpty(enc.GetString()))
                    encoding = enc.GetString();

                File.WriteAllText(fullPath, content, Encoding.GetEncoding(encoding));
                return Task.FromResult(AgentToolResult.Ok("File written: " + fullPath));
            }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail(ex.Message)); }
        }
    }

    /// <summary>List files in a directory</summary>
    public class FileListTool : IAgentTool
    {
        public string Name => "file_list";
        public string Description => "List files in a directory with optional pattern filter and recursive search.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""Directory path""},""pattern"":{""type"":""string"",""description"":""File pattern (e.g. *.txt)"",""default"":""*""},""recursive"":{""type"":""boolean"",""description"":""Search recursively"",""default"":false}},""required"":[""path""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Read-only directory listing, pure .NET

        public Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var path = parameters.TryGetProperty("path", out var p) ? p.GetString() : ".";
                if (!Directory.Exists(path))
                    return Task.FromResult(AgentToolResult.Fail("Directory not found: " + path));

                var pattern = "*";
                if (parameters.TryGetProperty("pattern", out var pat) && !string.IsNullOrEmpty(pat.GetString()))
                    pattern = pat.GetString();

                var recursive = false;
                if (parameters.TryGetProperty("recursive", out var rec))
                    recursive = rec.GetBoolean();

                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var files = Directory.GetFiles(path, pattern, searchOption);

                var sb = new StringBuilder();
                sb.AppendLine("Found " + files.Length + " files:");
                foreach (var file in files.Take(200))
                {
                    var fi = new FileInfo(file);
                    sb.AppendLine("- " + fi.Name + " (" + FormatSize(fi.Length) + ", " + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + ")");
                }
                if (files.Length > 200)
                    sb.AppendLine("... and " + (files.Length - 200) + " more");

                return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
            }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail(ex.Message)); }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
        }
    }

    /// <summary>Web search stub</summary>
    public class WebSearchTool : IAgentTool
    {
        public string Name => "web_search";
        public string Description => "Search the web for information. Returns search query reference URL.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""query"":{""type"":""string"",""description"":""Search query""},""max_results"":{""type"":""integer"",""description"":""Max results"",""default"":5}},""required"":[""query""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Stateless network query

        public Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var query = parameters.TryGetProperty("query", out var q) ? q.GetString() : null;
                if (string.IsNullOrEmpty(query))
                    return Task.FromResult(AgentToolResult.Fail("query is required"));

                var maxResults = 5;
                if (parameters.TryGetProperty("max_results", out var mr) && mr.ValueKind == JsonValueKind.Number)
                    maxResults = mr.GetInt32();

                var result = "Search query: " + query + "\n"
                    + "Max results: " + maxResults + "\n"
                    + "Search URL: https://duckduckgo.com/?q=" + Uri.EscapeDataString(query) + "\n"
                    + "Note: Configure a search API for full results.";
                return Task.FromResult(AgentToolResult.Ok(result));
            }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail(ex.Message)); }
        }
    }

    /// <summary>Mathematical calculator</summary>
    public class CalculatorTool : IAgentTool
    {
        public string Name => "calculator";
        public string Description => "Perform mathematical calculations. Supports arithmetic expressions.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""expression"":{""type"":""string"",""description"":""Mathematical expression to evaluate""}},""required"":[""expression""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Pure computation, no side effects

        public Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var expression = parameters.TryGetProperty("expression", out var e) ? e.GetString() : null;
                if (string.IsNullOrEmpty(expression))
                    return Task.FromResult(AgentToolResult.Fail("expression is required"));

                var dt = new DataTable();
                var result = dt.Compute(expression, null);
                return Task.FromResult(AgentToolResult.Ok(expression + " = " + result));
            }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail("Calculation error: " + ex.Message)); }
        }
    }

    /// <summary>Execute a shell command</summary>
    public class ExecuteCommandTool : IAgentTool
    {
        public string Name => "execute_command";
        public string Description => "Execute a shell command and return its output. Use with caution.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""command"":{""type"":""string"",""description"":""Command to execute""},""working_directory"":{""type"":""string"",""description"":""Working directory""}},""required"":[""command""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.RequiresConfirmation;
        public bool SupportsParallel => false; // External command with side effects, run serially

        public async Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var command = parameters.TryGetProperty("command", out var cmd) ? cmd.GetString() : null;
                if (string.IsNullOrEmpty(command))
                    return AgentToolResult.Fail("command is required");

                // Command blacklist: block destructive / system-altering commands such as
                // format/del/shutdown/reg/net, including sub-commands inside a command
                // chain (&, |, ; etc.). A confirmation dialog alone is not enough to
                // prevent accidental execution.
                if (!ToolSecurity.IsCommandAllowed(command, out var cmdReason))
                    return AgentToolResult.Fail("Command refused: " + cmdReason);

                var workingDirectory = "";
                if (parameters.TryGetProperty("working_directory", out var wd) && !string.IsNullOrEmpty(wd.GetString()))
                    workingDirectory = wd.GetString();

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                if (!string.IsNullOrEmpty(workingDirectory))
                    psi.WorkingDirectory = workingDirectory;

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                        return AgentToolResult.Fail("Failed to start process");

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    process.WaitForExit();

                    var output = await outputTask;
                    var error = await errorTask;

                    if (process.ExitCode == 0)
                        return AgentToolResult.Ok(string.IsNullOrEmpty(output) ? "(no output)" : output);

                    return AgentToolResult.Fail("Exit code " + process.ExitCode + ": " + (string.IsNullOrEmpty(error) ? output : error));
                }
            }
            catch (Exception ex) { return AgentToolResult.Fail(ex.Message); }
        }
    }

    /// <summary>Get system information</summary>
    public class GetSystemInfoTool : IAgentTool
    {
        public string Name => "get_system_info";
        public string Description => "Get current system information including OS, machine name, and process stats.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{}}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Read-only system info

        public Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Machine: " + Environment.MachineName);
                sb.AppendLine("User: " + Environment.UserName);
                sb.AppendLine("OS: " + Environment.OSVersion);
                sb.AppendLine("Processors: " + Environment.ProcessorCount);
                sb.AppendLine("Working Set: " + (Environment.WorkingSet / 1024 / 1024) + " MB");
                sb.AppendLine("Current Directory: " + Environment.CurrentDirectory);
                sb.AppendLine("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
            }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail(ex.Message)); }
        }
    }

    /// <summary>Parse and validate JSON</summary>
    public class JsonParserTool : IAgentTool
    {
        public string Name => "json_parser";
        public string Description => "Parse and validate a JSON string. Returns the parsed structure.";
        public string InputSchema => @"{""kind"":""object"",""properties"":{""json"":{""type"":""string"",""description"":""JSON string to parse""}},""required"":[""json""]}";
        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed;
        public bool SupportsParallel => true; // Pure parsing, no side effects

        public Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                var json = parameters.TryGetProperty("json", out var j) ? j.GetString() : null;
                if (string.IsNullOrEmpty(json))
                    return Task.FromResult(AgentToolResult.Fail("json is required"));

                using var doc = JsonDocument.Parse(json);
                var formatted = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                return Task.FromResult(AgentToolResult.Ok("Valid JSON:\n" + formatted));
            }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail("JSON parse error: " + ex.Message)); }
        }
    }
}
