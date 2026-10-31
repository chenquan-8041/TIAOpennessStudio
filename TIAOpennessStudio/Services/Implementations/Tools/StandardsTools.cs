using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Standards;

namespace TIAOpennessStudio.Services.Implementations.Tools
{
    /// <summary>
    /// Agent tool that searches the PLC coding standards and returns "constraint
    /// instructions".
    ///
    /// Confidentiality guardrails (tier one):
    ///   - Only searches and returns a small number of entries (bounded by StandardsKnowledgeStore.MaxTopK);
    ///   - Returns rewritten constraint instructions rather than the raw rules by default (summarizeOnly=true);
    ///   - Built-in sliding-window rate limiting prevents the agent from being coaxed into draining the store with many queries;
    ///   - No "list all / export" capability.
    ///
    /// This tool never touches the TIA STA thread and can run concurrently
    /// (implements IParallelSafeTool).
    /// </summary>
    public sealed class SearchCodingStandardTool : IAgentTool, IParallelSafeTool
    {
        private readonly StandardsRegistry _registry;

        // Rate limiting: at most N searches per sliding window, to prevent scripted store scraping.
        private const int WindowSeconds = 60;
        private const int MaxCallsPerWindow = 20;
        private readonly object _rateSync = new object();
        private readonly Queue<DateTime> _callTimes = new Queue<DateTime>();

        public SearchCodingStandardTool(StandardsRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public string Name => "search_coding_standard";

        public string Description =>
            "检索适用的 PLC 编码标准，返回必须遵守的约束指令（命名/地址/块编号/注释等）。" +
            "在生成或修改 FB/FC/DB/OB/Tag 前调用，据此保证产物合规。仅返回相关的少量约束。";

        public string InputSchema =>
            @"{""type"":""object"",""properties"":{" +
            @"""query"":{""type"":""string"",""description"":""要查的主题，如 'FB 命名'、'DB 地址分配'、'注释规范'""}," +
            @"""top_k"":{""type"":""integer"",""description"":""返回条数(1-8)，默认 3"",""default"":3}}," +
            @"""required"":[""query""]}";

        public ToolPermissionLevel Permission => ToolPermissionLevel.Allowed; // Read-only
        public bool SupportsParallel => true;

        public Task<AgentToolResult> ExecuteAsync(JsonElement parameters, CancellationToken ct = default)
        {
            try
            {
                if (!AllowCall())
                    return Task.FromResult(AgentToolResult.Fail(
                        "编码标准检索过于频繁，请稍后再试。"));

                var query = parameters.TryGetProperty("query", out var q) ? q.GetString() : null;
                if (string.IsNullOrWhiteSpace(query))
                    return Task.FromResult(AgentToolResult.Fail("query 不能为空。"));

                int topK = 3;
                if (parameters.TryGetProperty("top_k", out var k) && k.ValueKind == JsonValueKind.Number)
                    topK = k.GetInt32();

                // Aggregate search over the official store plus the user's enabled/unlocked mounted stores
                var hits = _registry.SearchAll(query, topK);
                if (hits.Count == 0)
                    return Task.FromResult(AgentToolResult.Ok("未找到相关编码标准约束。按通用最佳实践处理即可。"));

                var rules = hits.Select(h => h.Rule).ToList();
                // Return constraint instructions, not the raw rules.
                var injection = StandardsPolicyInjector.BuildInjection(rules, summarizeOnly: true);
                return Task.FromResult(AgentToolResult.Ok(injection));
            }
            catch (Exception ex)
            {
                // Do not leak internal path / secret details to the agent or the user.
                return Task.FromResult(AgentToolResult.Fail("编码标准检索失败：" + ex.Message));
            }
        }

        private bool AllowCall()
        {
            var now = DateTime.UtcNow;
            lock (_rateSync)
            {
                while (_callTimes.Count > 0 && (now - _callTimes.Peek()).TotalSeconds > WindowSeconds)
                    _callTimes.Dequeue();
                if (_callTimes.Count >= MaxCallsPerWindow)
                    return false;
                _callTimes.Enqueue(now);
                return true;
            }
        }
    }
}
