using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>Registry of IAgentTool implementations used for tool lookup and execution by the agent engines.</summary>
    public class ToolRegistry
    {
        private readonly Dictionary<string, IAgentTool> _tools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);

        public void Register(IAgentTool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            _tools[tool.Name] = tool;
        }

        public void RegisterAll(IEnumerable<IAgentTool> tools)
        {
            foreach (var tool in tools) Register(tool);
        }

        public IAgentTool GetTool(string name)
        {
            return _tools.TryGetValue(name, out var tool) ? tool : null;
        }

        public IReadOnlyList<IAgentTool> GetAllTools() => _tools.Values.ToList();

        public bool HasTool(string name) => _tools.ContainsKey(name);

        public bool Unregister(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) throw new ArgumentNullException(nameof(toolName));
            return _tools.Remove(toolName);
        }

        public async Task<AgentToolResult> ExecuteAsync(string toolName, JsonElement parameters, CancellationToken ct = default)
        {
            var tool = GetTool(toolName);
            if (tool == null)
                return AgentToolResult.Fail("Unknown tool: " + toolName);
            try { return await tool.ExecuteAsync(parameters, ct); }
            catch (Exception ex) { return AgentToolResult.Fail("Tool " + toolName + " failed: " + ex.Message); }
        }

        public string GenerateToolCatalog()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Available tools:");
            foreach (var tool in _tools.Values.OrderBy(t => t.Name))
                sb.AppendLine("- " + tool.Name + ": " + tool.Description + " [Permission: " + tool.Permission + "]");
            return sb.ToString();
        }
    }
}
