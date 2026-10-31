using System;
using System.Collections.Generic;
using System.Linq;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Shared-budget token manager. All message parts (system prompt, context,
    /// history, tool outputs) share a single budget derived from the model's
    /// context window. Aligned with Python core/context.py thresholds.
    /// </summary>
    public sealed class TokenBudget
    {
        // ── Token budget ──
        // Budget = model context window. Override via TIA_TOKEN_BUDGET_DEFAULT env var.
        // Default 6000 is a conservative floor for tiny models; real models pass their
        // context window in via the constructor (e.g. Ollama qwen3.5:9b ≈ 262144).
        // Per-component caps and the response reserve now SCALE with the effective
        // budget instead of being hard-coded, so a 256K window is actually usable.

        private const int DefaultBudget = 6000;
        private static readonly int EnvDefaultBudget = ReadEnvDefaultBudget();
        public const int EstimatedCharsPerToken = 4;

        // Response reserve scales with the budget (fraction), clamped to a sane range.
        // Small windows keep ~2000; huge windows reserve proportionally more headroom.
        private const double ResponseReserveFraction = 0.15;
        private const int MinResponseReserve = 2000;
        private const int MaxResponseReserve = 32000;

        /// <summary>Instance-level token budget override. Set from AgentConfig.MaxTokens or model window.</summary>
        private readonly int _instanceBudget;

        // Explicit per-component overrides (null => auto-derive from EffectiveBudget).
        private int? _maxSystemTokens;
        private int? _maxContextTokens;
        private int? _maxHistoryTokens;
        private int? _maxToolOutputTokens;

        public TokenBudget(int maxTokens = 0)
        {
            _instanceBudget = maxTokens > 0 ? maxTokens : 0;
        }

        private static int ReadEnvDefaultBudget()
        {
            var env = Environment.GetEnvironmentVariable("TIA_TOKEN_BUDGET_DEFAULT");
            return int.TryParse(env, out var val) && val > 0 ? val : DefaultBudget;
        }

        /// <summary>Response reserve, proportional to the effective budget.</summary>
        public int ResponseReserve =>
            Math.Min(MaxResponseReserve,
                Math.Max(MinResponseReserve, (int)(EffectiveBudget * ResponseReserveFraction)));

        // ── Per-component caps (sub-limits within the shared budget) ──
        // Defaults auto-derive as a fraction of SharedBudget so they grow with the
        // model's context window. Any explicit set (e.g. retrieval callers wanting a
        // tight 3000-token context) overrides the derived value.
        public int MaxSystemTokens
        {
            get => _maxSystemTokens ?? Math.Max(4000, (int)(SharedBudget * 0.35));
            set => _maxSystemTokens = value;
        }
        public int MaxContextTokens
        {
            get => _maxContextTokens ?? Math.Max(3000, (int)(SharedBudget * 0.35));
            set => _maxContextTokens = value;
        }
        public int MaxHistoryTokens
        {
            get => _maxHistoryTokens ?? Math.Max(2000, (int)(SharedBudget * 0.20));
            set => _maxHistoryTokens = value;
        }
        public int MaxToolOutputTokens
        {
            get => _maxToolOutputTokens ?? Math.Max(2000, (int)(SharedBudget * 0.25));
            set => _maxToolOutputTokens = value;
        }

        /// <summary>Model name for budget lookup.</summary>
        public string Model { get; set; } = "default";

        /// <summary>Shared token budget (after response reserve).</summary>
        public int SharedBudget => Math.Max(1000, EffectiveBudget - ResponseReserve);

        // ── Context-window budget policy ──
        // Modern cloud models ship huge windows (1M+ is common). Policy:
        //   - CLOUD models  → default 1M unless overridden.
        //   - LOCAL (Ollama) models → their real, smaller windows (a local 7B/9B
        //     cannot run at 1M context on typical hardware).
        // A model is LOCAL when its name has an Ollama tag (":", e.g. "qwen2.5:7b")
        // or matches a known local family fragment.
        // Keep in sync with the Python side (core/context.py).

        /// <summary>Default window for any cloud model without a more specific entry.</summary>
        private const int CloudDefaultWindow = 1_000_000;

        // Known LOCAL (Ollama) family windows. Longest fragment wins.
        private static readonly Dictionary<string, int> LocalModelWindows =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "qwen3", 32768 },
            { "qwen2.5", 32768 },
            { "qwen2", 32768 },
            { "qwen", 32768 },
            { "qwq", 32768 },
            { "llama3", 8192 },
            { "llama", 8192 },
            { "gemma", 8192 },
            { "phi", 16384 },
            { "mistral", 32768 },
            { "codellama", 16384 },
        };

        /// <summary>
        /// Is this an Ollama/local model (by name alone)? LOCAL is signalled ONLY by
        /// an Ollama tag (":", e.g. "qwen2.5:7b"). A bare family name is CLOUD, since
        /// cloud APIs expose tagless names ("qwen-plus", "deepseek-chat" are cloud).
        /// </summary>
        public static bool IsLocalModelName(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return false;
            return model.Trim().Contains(":"); // Ollama tag convention
        }

        /// <summary>
        /// Resolve a model's context window.
        ///   LOCAL  → real small window (longest-fragment match, else 6000 floor).
        ///   CLOUD  → 1M default.
        /// </summary>
        public static int ResolveModelWindow(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return 0;
            var m = model.Trim().ToLowerInvariant();
            if (IsLocalModelName(model))
            {
                foreach (var kv in LocalModelWindows.OrderByDescending(k => k.Key.Length))
                {
                    if (m.Contains(kv.Key.ToLowerInvariant())) return kv.Value;
                }
                return EnvDefaultBudget; // unknown local model → conservative floor
            }
            return CloudDefaultWindow; // cloud model → 1M
        }

        /// <summary>
        /// Budget for a model. Priority:
        ///   1. TIA_TOKEN_BUDGET_DEFAULT env var, if explicitly set (operator override).
        ///   2. LOCAL models → their real window; CLOUD models → 1M.
        /// </summary>
        public static int GetBudget(string model = null)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TIA_TOKEN_BUDGET_DEFAULT")))
                return EnvDefaultBudget;
            var window = ResolveModelWindow(model);
            return window > 0 ? window : EnvDefaultBudget;
        }

        /// <summary>
        /// Effective budget, in priority order:
        ///   1. Explicit env override (TIA_TOKEN_BUDGET_DEFAULT) — operator ceiling.
        ///   2. The current model's context window, capping the configured instance
        ///      budget (so switching to a smaller model actually shrinks the budget
        ///      and we never exceed what the model supports).
        ///   3. The configured instance budget (AgentConfig.MaxTokens).
        ///   4. The default (6000).
        /// </summary>
        public int EffectiveBudget
        {
            get
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TIA_TOKEN_BUDGET_DEFAULT")))
                    return EnvDefaultBudget;

                int modelWindow = ResolveModelWindow(Model);
                int baseBudget = _instanceBudget > 0 ? _instanceBudget : EnvDefaultBudget;

                if (modelWindow > 0)
                {
                    // Cap the configured budget by the real model window; if no
                    // instance budget was configured, use the model window directly.
                    return _instanceBudget > 0 ? Math.Min(baseBudget, modelWindow) : modelWindow;
                }
                return baseBudget;
            }
        }

        public int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return text.Length / EstimatedCharsPerToken;
        }

        // ── Truncation helpers ──

        public TruncationResult TruncateContext(string context)
        {
            return TruncateToBudget(context, MaxContextTokens);
        }

        /// <summary>
        /// "Head + tail" truncation: keep the most informative beginning and end,
        /// omit the middle. Superior to plain head truncation for SCL/XML/tool output
        /// where both the declaration head and the logic tail carry signal. Mirrors the
        /// cloud-side core/context.py truncate_tool_result strategy.
        /// </summary>
        public TruncationResult TruncateHeadTail(string text, int maxTokens, double headRatio = 0.6)
        {
            if (string.IsNullOrEmpty(text))
                return new TruncationResult { Truncated = false, Content = text, RemovedTokens = 0 };

            int maxChars = maxTokens * EstimatedCharsPerToken;
            if (text.Length <= maxChars)
                return new TruncationResult { Truncated = false, Content = text, RemovedTokens = 0 };

            if (maxChars <= 80)
            {
                var hard = text.Substring(0, Math.Max(0, maxChars));
                return new TruncationResult
                {
                    Truncated = true,
                    Content = hard,
                    RemovedTokens = (text.Length - hard.Length) / EstimatedCharsPerToken,
                    Warning = $"Truncated: removed ~{text.Length - hard.Length} chars"
                };
            }

            int headSize = (int)(maxChars * headRatio);
            int tailSize = maxChars - headSize - 40; // 40 chars for the marker
            if (tailSize < 0) tailSize = 0;

            string head = text.Substring(0, headSize);
            int lastNl = head.LastIndexOf('\n');
            if (lastNl > headSize / 2) head = head.Substring(0, lastNl);

            string tail = tailSize > 0 ? text.Substring(text.Length - tailSize) : "";
            int firstNl = tail.IndexOf('\n');
            if (firstNl > 0 && firstNl < 40) tail = tail.Substring(firstNl);

            int omitted = text.Length - head.Length - tail.Length;
            var content = $"{head}\n\n... [truncated ~{omitted} chars] ...\n\n{tail}";
            return new TruncationResult
            {
                Truncated = true,
                Content = content,
                RemovedTokens = omitted / EstimatedCharsPerToken,
                Warning = $"Head+tail truncated: omitted ~{omitted} chars"
            };
        }

        public TruncationResult<List<Dictionary<string, string>>> TruncateHistoryList(
            List<Dictionary<string, string>> history)
        {
            if (history == null || history.Count == 0)
                return new TruncationResult<List<Dictionary<string, string>>>
                {
                    Truncated = false, Content = history
                };

            var removedCount = 0;
            var result = new List<Dictionary<string, string>>();
            var currentTokens = 0;

            // Keep from newest to oldest
            for (int i = history.Count - 1; i >= 0; i--)
            {
                var msg = history[i];
                var content = msg.ContainsKey("content") ? msg["content"] : "";
                var entryTokens = EstimateTokens($"\n{msg["role"]}: {content}");

                if (currentTokens + entryTokens <= MaxHistoryTokens)
                {
                    result.Insert(0, msg);
                    currentTokens += entryTokens;
                }
                else
                {
                    removedCount++;
                }
            }

            return new TruncationResult<List<Dictionary<string, string>>>
            {
                Truncated = removedCount > 0,
                Content = result,
                RemovedTokens = removedCount,
                Warning = removedCount > 0
                    ? $"Truncated {removedCount} history messages"
                    : null
            };
        }

        // ── Budget computation (shared model) ──

        public BudgetSummary ComputeBudget(
            string prompt,
            string context,
            List<Dictionary<string, string>> history,
            int toolOutputTokens = 0)
        {
            var promptTokens = EstimateTokens(prompt);
            var contextTokens = EstimateTokens(context);
            var historyTokens = 0;
            if (history != null)
            {
                foreach (var msg in history)
                {
                    if (msg.ContainsKey("content"))
                        historyTokens += EstimateTokens(msg["content"]);
                }
            }

            var totalUsed = promptTokens + contextTokens + historyTokens + toolOutputTokens;
            var budget = SharedBudget;
            var remaining = budget - totalUsed;

            var status = BudgetStatus.Ok;
            var warnings = new List<string>();

            // Per-component warnings
            if (contextTokens > MaxContextTokens)
            {
                status = BudgetStatus.Warning;
                warnings.Add($"Context exceeds cap: {contextTokens} > {MaxContextTokens}");
            }
            if (historyTokens > MaxHistoryTokens)
            {
                status = BudgetStatus.Warning;
                warnings.Add($"History exceeds cap: {historyTokens} > {MaxHistoryTokens}");
            }
            if (toolOutputTokens > MaxToolOutputTokens)
            {
                status = BudgetStatus.Warning;
                warnings.Add($"Tool output exceeds cap: {toolOutputTokens} > {MaxToolOutputTokens}");
            }

            // Shared budget check
            if (remaining < 0)
            {
                status = BudgetStatus.Exceeded;
                warnings.Add($"Shared budget exceeded by {-remaining} tokens ({budget} budget)");
            }

            return new BudgetSummary
            {
                PromptTokens = promptTokens,
                ContextTokens = contextTokens,
                HistoryTokens = historyTokens,
                ToolOutputTokens = toolOutputTokens,
                TotalUsed = totalUsed,
                TotalBudget = budget,
                Remaining = remaining,
                Status = status,
                Warnings = warnings
            };
        }

        // ── Internal ──

        private TruncationResult TruncateToBudget(string text, int maxTokens)
        {
            var tokens = EstimateTokens(text);
            if (tokens <= maxTokens)
                return new TruncationResult
                {
                    Truncated = false, Content = text, RemovedTokens = 0
                };

            var maxChars = maxTokens * EstimatedCharsPerToken;
            var truncated = text.Length > maxChars
                ? text.Substring(0, maxChars)
                : text;
            var removedChars = text.Length - truncated.Length;

            return new TruncationResult
            {
                Truncated = true,
                Content = truncated,
                RemovedTokens = removedChars / EstimatedCharsPerToken,
                Warning = $"Truncated: removed ~{removedChars} chars"
            };
        }
    }

    // ── Supporting types ──────────────────────────────────────

    public class TruncationResult
    {
        public bool Truncated { get; set; }
        public string Content { get; set; }
        public int RemovedTokens { get; set; }
        public string Warning { get; set; }
    }

    public class TruncationResult<T>
    {
        public bool Truncated { get; set; }
        public T Content { get; set; }
        public int RemovedTokens { get; set; }
        public string Warning { get; set; }
    }

    public class BudgetSummary
    {
        public int PromptTokens { get; set; }
        public int ContextTokens { get; set; }
        public int HistoryTokens { get; set; }
        public int ToolOutputTokens { get; set; }
        public int TotalUsed { get; set; }
        public int TotalBudget { get; set; }
        public int Remaining { get; set; }
        public BudgetStatus Status { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public enum BudgetStatus
    {
        Ok,
        Warning,
        Exceeded
    }

    public class ContextBuilder
    {
        private readonly TokenBudget _budget;
        private readonly ContextPack _pack;

        public ContextBuilder(TokenBudget budget)
        {
            _budget = budget;
            _pack = new ContextPack();
        }

        public ContextBuilder WithRid(string rid)
        {
            _pack.Rid = rid;
            return this;
        }

        public ContextBuilder WithProjectFacts(ProjectFacts facts)
        {
            _pack.ProjectFacts = facts;
            return this;
        }

        public ContextBuilder WithCodeFragments(List<CodeFragment> fragments)
        {
            _pack.CodeFragments = fragments;
            return this;
        }

        public ContextBuilder WithRuntimeContext(RuntimeContext context)
        {
            _pack.RuntimeContext = context;
            return this;
        }

        public ContextBuilder WithMetadata(string key, string value)
        {
            _pack.Metadata[key] = value;
            return this;
        }

        public (ContextPack pack, List<string> warnings) Build()
        {
            var warnings = new List<string>();
            var contextString = _pack.ToPromptString();
            var tokenCount = _budget.EstimateTokens(contextString);
            _pack.TotalTokenEstimate = tokenCount;

            if (tokenCount > _budget.MaxContextTokens)
            {
                var truncated = _budget.TruncateContext(contextString);
                if (truncated.Truncated)
                {
                    warnings.Add(truncated.Warning);
                }
            }

            var budgetSummary = _budget.ComputeBudget("", contextString, null);
            warnings.AddRange(budgetSummary.Warnings);

            return (_pack, warnings);
        }
    }
}
