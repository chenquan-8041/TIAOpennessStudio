using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Assembles context for LLM calls with token budget management.
    /// Ensures the total context stays within the model's token limit
    /// by prioritizing and truncating different context sections.
    /// </summary>
    public class ContextAssembler
    {
        private readonly IPlcKnowledgeIndexService _knowledgeIndex;
        private readonly IPlcContextBuilder _plcContextBuilder;
        private readonly ITiaOpennessService _tiaService;
        private readonly int _maxTokens;

        // Token estimation: ~4 chars per token for English, ~2 chars per token for Chinese
        private const int CharsPerToken = 4;

        public ContextAssembler(IPlcKnowledgeIndexService knowledgeIndex, ITiaOpennessService tiaService, IPlcContextBuilder plcContextBuilder = null, int maxTokens = 32000)
        {
            _knowledgeIndex = knowledgeIndex;
            _tiaService = tiaService;
            _plcContextBuilder = plcContextBuilder;
            _maxTokens = maxTokens;
        }

        /// <summary>
        /// Assemble a full context string for an LLM call, respecting token budget.
        /// </summary>
        public AssembledContext Assemble(string userPrompt, string targetBlockName = null, string targetBlockType = null, List<Dictionary<string, string>> history = null)
        {
            var ctx = new AssembledContext { MaxTokens = _maxTokens };
            var budget = new TokenBudget(_maxTokens);

            // 1. System prompt (always included, ~2000 tokens)
            var systemPrompt = BuildSystemPrompt();
            ctx.SystemPrompt = systemPrompt;
            budget.Consume(EstimateTokens(systemPrompt));

            // 2. Project context (if connected, ~3000 tokens max)
            if (_tiaService != null && _tiaService.IsConnected)
            {
                var projectCtx = BuildProjectContext(budget.Remaining);
                if (!string.IsNullOrEmpty(projectCtx))
                {
                    ctx.ProjectContext = projectCtx;
                    budget.Consume(EstimateTokens(projectCtx));
                }
            }

            // 3. Target block context (if specified, ~2000 tokens max)
            if (!string.IsNullOrEmpty(targetBlockName))
            {
                var blockCtx = BuildBlockContext(targetBlockName, budget.Remaining);
                if (!string.IsNullOrEmpty(blockCtx))
                {
                    ctx.BlockContext = blockCtx;
                    budget.Consume(EstimateTokens(blockCtx));
                }
            }

            // 4. Related blocks from index (RAG, ~2000 tokens max)
            if (_knowledgeIndex != null && !string.IsNullOrEmpty(userPrompt))
            {
                var relatedCtx = BuildRelatedContext(userPrompt, budget.Remaining);
                if (!string.IsNullOrEmpty(relatedCtx))
                {
                    ctx.RelatedContext = relatedCtx;
                    budget.Consume(EstimateTokens(relatedCtx));
                }
            }

            // 5. History (sliding window, ~2000 tokens max)
            if (history != null && history.Count > 0)
            {
                var historyCtx = BuildHistoryContext(history, budget.Remaining);
                if (!string.IsNullOrEmpty(historyCtx))
                {
                    ctx.HistoryContext = historyCtx;
                    budget.Consume(EstimateTokens(historyCtx));
                }
            }

            ctx.UsedTokens = budget.UsedTokens;
            ctx.RemainingTokens = budget.Remaining;
            ctx.UsagePercent = budget.UsagePercent;

            return ctx;
        }

        /// <summary>
        /// Build the full prompt string for the LLM.
        /// </summary>
        public string BuildFullPrompt(AssembledContext ctx, string userPrompt)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(ctx.SystemPrompt))
            {
                sb.AppendLine(ctx.SystemPrompt);
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(ctx.ProjectContext))
            {
                sb.AppendLine("## Project Context");
                sb.AppendLine(ctx.ProjectContext);
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(ctx.BlockContext))
            {
                sb.AppendLine("## Target Block");
                sb.AppendLine(ctx.BlockContext);
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(ctx.RelatedContext))
            {
                sb.AppendLine("## Related Blocks");
                sb.AppendLine(ctx.RelatedContext);
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(ctx.HistoryContext))
            {
                sb.AppendLine("## Conversation History");
                sb.AppendLine(ctx.HistoryContext);
                sb.AppendLine();
            }

            sb.AppendLine("## Current Task");
            sb.AppendLine(userPrompt);

            return sb.ToString();
        }

        private string BuildSystemPrompt()
        {
            return @"You are a TIA Portal expert assistant. You help users with PLC programming, HMI design, and TIA Portal operations.

            You can:
            - Generate SCL code for PLC blocks (FC, FB, DB, UDT)
            - Generate LAD (Ladder Logic) blocks
            - Analyze and explain existing code
            - Modify and optimize code
            - Compile and diagnose errors

            Always output complete, valid SCL code. Use proper TIA Portal naming conventions.";
        }

        private string BuildProjectContext(int remainingTokens)
        {
            try
            {
                // Use ConfigureAwait(false) and run on thread-pool to avoid deadlock
                // when called from a synchronous context that holds the UI SynchronizationContext.
                var tree = _tiaService.GetProjectTreeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                if (tree == null || tree.Count == 0) return null;

                var sb = new StringBuilder();
                sb.AppendLine("Open project devices:");
                foreach (var item in tree.Take(20))
                {
                    sb.AppendLine("- " + item.Name);
                }
                if (tree.Count > 20)
                    sb.AppendLine("... and " + (tree.Count - 20) + " more");

                var text = sb.ToString();
                return TruncateToTokens(text, remainingTokens);
            }
            catch
            {
                return null;
            }
        }

        private string BuildBlockContext(string blockName, int remainingTokens)
        {
            try
            {
                // Use ConfigureAwait(false) to avoid deadlock on UI SynchronizationContext.
                var code = _tiaService.ExportBlockCodeAsync(blockName).ConfigureAwait(false).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(code)) return null;

                var sb = new StringBuilder();
                sb.AppendLine("Block: " + blockName);
                sb.AppendLine("```");
                sb.AppendLine(code);
                sb.AppendLine("```");

                var text = sb.ToString();
                return TruncateToTokens(text, remainingTokens);
            }
            catch
            {
                return null;
            }
        }

        private string BuildRelatedContext(string query, int remainingTokens)
        {
            try
            {
                if (_plcContextBuilder != null)
                {
                    var structured = _plcContextBuilder.BuildRelatedContext("", query, remainingTokens, 5);
                    if (!string.IsNullOrWhiteSpace(structured))
                    {
                        return structured;
                    }
                }

                var hits = _knowledgeIndex?.Search("", query, 5);
                if (hits == null || hits.Count == 0) return null;

                var sb = new StringBuilder();
                foreach (var hit in hits)
                {
                    sb.AppendLine("- [" + hit.BlockName + "/" + hit.BlockType + "] " + hit.Summary);
                }

                var text = sb.ToString();
                return TruncateToTokens(text, remainingTokens);
            }
            catch
            {
                return null;
            }
        }

        private string BuildHistoryContext(List<Dictionary<string, string>> history, int remainingTokens)
        {
            var sb = new StringBuilder();
            var recentHistory = history.Skip(Math.Max(0, history.Count - 10)).ToList();

            foreach (var msg in recentHistory)
            {
                var role = msg.ContainsKey("role") ? msg["role"] : "unknown";
                var content = msg.ContainsKey("content") ? msg["content"] : "";
                sb.AppendLine("[" + role + "]: " + content);
            }

            var text = sb.ToString();
            return TruncateToTokens(text, remainingTokens);
        }

        private int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return text.Length / CharsPerToken;
        }

        private string TruncateToTokens(string text, int maxTokens)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int maxChars = maxTokens * CharsPerToken;
            if (text.Length <= maxChars) return text;
            return text.Substring(0, maxChars) + "\n... (truncated)";
        }
    }

    public class AssembledContext
    {
        public string SystemPrompt { get; set; }
        public string ProjectContext { get; set; }
        public string BlockContext { get; set; }
        public string RelatedContext { get; set; }
        public string HistoryContext { get; set; }
        public int MaxTokens { get; set; }
        public int UsedTokens { get; set; }
        public int RemainingTokens { get; set; }
        public double UsagePercent { get; set; }
    }

    public class TokenBudget
    {
        public int MaxTokens { get; }
        public int UsedTokens { get; private set; }
        public int Remaining => MaxTokens - UsedTokens;
        public double UsagePercent => (double)UsedTokens / MaxTokens * 100;

        public TokenBudget(int maxTokens)
        {
            MaxTokens = maxTokens;
        }

        public void Consume(int tokens)
        {
            UsedTokens += tokens;
        }

        public bool IsNearLimit => UsagePercent > 80;
        public bool IsOverLimit => UsagePercent > 95;
    }
}
