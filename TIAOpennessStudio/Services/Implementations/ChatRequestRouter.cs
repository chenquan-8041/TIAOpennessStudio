using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>Routes a chat prompt to the chat / analyze / agent-task pipeline based on rule-based intent classification.</summary>
    public class ChatRequestRouter : IChatRequestRouter
    {
        private readonly ILocalLlmService _localLlmService;

        // The constructor parameter is kept for compatibility with the existing DI
        // registration; intent routing no longer relies on local LLM classification.
        public ChatRequestRouter(ILocalLlmService localLlmService = null)
        {
            _localLlmService = localLlmService;
        }

        public async Task<ChatRouteResult> RouteAsync(
            string prompt,
            IReadOnlyList<AiReferenceItem> references = null,
            IReadOnlyList<Dictionary<string, string>> history = null,
            CancellationToken ct = default)
        {
            // Routing is now pure synchronous keyword/rule logic; there is no async LLM
            // classification call. The async signature is kept to match IChatRequestRouter.
            await Task.CompletedTask;

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return ChatRouteResult.Create(ChatRouteKind.Chat, AgentIntent.Chat, 1.0, "empty prompt");
            }

            var lower = prompt.ToLowerInvariant();
            var hasReferences = references != null && references.Count > 0;
            var looksLikeCodeQuestion = LooksLikeCodeQuestion(lower) || hasReferences;
            var agentIntent = ClassifyByRules(lower, looksLikeCodeQuestion);
            if (agentIntent.HasValue)
            {
                return MapIntentToRoute(agentIntent.Value, "rule match", 0.95, hasReferences);
            }

            // No longer call the small model (0.5b) for 7-way classification fallback: it
            // is inaccurate and adds an LLM round-trip, and only the coarse RouteKind is
            // actually consumed downstream (exact intent is decided by the ReAct loop).
            // When keywords miss, use this coarse fallback: code hints -> SimpleAnalyze,
            // otherwise -> Chat. Local Ollama and cloud behave identically here (both
            // skip LLM classification), so the local path is unaffected.
            if (looksLikeCodeQuestion)
            {
                return ChatRouteResult.Create(
                    ChatRouteKind.SimpleAnalyze,
                    AgentIntent.ExplainCode,
                    0.6,
                    "code-oriented question fallback",
                    requiresProjectContext: hasReferences);
            }

            return ChatRouteResult.Create(ChatRouteKind.Chat, AgentIntent.Chat, 0.6, "default chat fallback");
        }

        private static ChatRouteResult MapIntentToRoute(AgentIntent intent, string reason, double confidence, bool hasReferences)
        {
            return intent switch
            {
                AgentIntent.Chat => ChatRouteResult.Create(ChatRouteKind.Chat, intent, confidence, reason),
                AgentIntent.ExplainCode => ChatRouteResult.Create(
                    ChatRouteKind.SimpleAnalyze,
                    intent,
                    confidence,
                    reason,
                    requiresProjectContext: hasReferences),
                AgentIntent.ModifyCode => ChatRouteResult.Create(
                    ChatRouteKind.AgentTask,
                    intent,
                    confidence,
                    reason,
                    requiresTools: true,
                    requiresWrite: true,
                    requiresProjectContext: true),
                AgentIntent.GenerateCode => ChatRouteResult.Create(
                    ChatRouteKind.AgentTask,
                    intent,
                    confidence,
                    reason,
                    requiresTools: true,
                    requiresWrite: true,
                    requiresProjectContext: true),
                AgentIntent.GenerateAndFix => ChatRouteResult.Create(
                    ChatRouteKind.AgentTask,
                    intent,
                    confidence,
                    reason,
                    requiresTools: true,
                    requiresWrite: true,
                    requiresProjectContext: true),
                AgentIntent.Compile => ChatRouteResult.Create(
                    ChatRouteKind.AgentTask,
                    intent,
                    confidence,
                    reason,
                    requiresTools: true,
                    requiresProjectContext: true),
                AgentIntent.TraceVariable => ChatRouteResult.Create(
                    ChatRouteKind.AgentTask,
                    intent,
                    confidence,
                    reason,
                    requiresTools: true,
                    requiresProjectContext: true),
                AgentIntent.Diagnose => ChatRouteResult.Create(
                    ChatRouteKind.AgentTask,
                    intent,
                    confidence,
                    reason,
                    requiresTools: true,
                    requiresProjectContext: true),
                _ => ChatRouteResult.Create(ChatRouteKind.Chat, AgentIntent.Chat, 0.5, reason)
            };
        }

        private static AgentIntent? ClassifyByRules(string lower, bool looksLikeCodeQuestion)
        {
            if (ContainsAny(lower, "compile", "verify", "build", "编译", "验证", "health check", "diagnose", "诊断"))
                return ContainsAny(lower, "health check", "diagnose", "诊断") ? AgentIntent.Diagnose : AgentIntent.Compile;

            if (ContainsAny(lower, "trace", "usage of", "where is", "find variable", "引用", "追踪", "哪里用到", "查找变量"))
                return AgentIntent.TraceVariable;

            if (ContainsAny(lower, "modify", "fix", "optimize", "refactor", "change", "update", "rename", "replace", "修改", "修复", "优化", "重构"))
                // No keyword guessing about "did they really mean analyse-only": the input-box access-mode
                // switch is the source of truth. Read-only runs reach the write gate and are stopped/elevated
                // there, so a modify word here always routes to the write-capable flow.
                return AgentIntent.ModifyCode;

            if (ContainsAny(lower, "generate", "create", "write a", "write an", "implement", "make a", "new fb", "new fc", "new ob", "new block", "write to", "import into", "生成", "创建", "新建", "写一个", "实现一个", "写入", "写到", "写进", "导入到"))
                return AgentIntent.GenerateCode;

            if (ContainsAny(lower, "explain", "describe", "analyze code", "walk me through", "understand", "解释", "说明", "分析"))
                return AgentIntent.ExplainCode;

            if (ContainsAny(lower, "what is", "what does", "how does", "是什么", "怎么工作", "如何工作") && looksLikeCodeQuestion)
                return AgentIntent.ExplainCode;

            if (ContainsAny(lower, "model", "who are you", "what can you do", "你好", "你是什么模型", "你能做什么"))
                return AgentIntent.Chat;

            return null;
        }

        private static bool LooksLikeCodeQuestion(string lower)
        {
            return ContainsAny(
                lower,
                "code", "logic", "block", "function", "program", "variable",
                "代码", "逻辑", "块", "变量",
                "scl", "lad", "fbd", "awl",
                "fb", "fc", "db", "ob", "udt", "tag");
        }

        private static bool ContainsAny(string text, params string[] patterns)
        {
            return patterns.Any(pattern => !string.IsNullOrEmpty(pattern) && text.Contains(pattern));
        }
    }
}
