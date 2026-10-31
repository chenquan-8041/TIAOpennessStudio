using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;
using System.Linq;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Orchestrates agent workflows: intent recognition, context assembly, plan
    /// building, execution (legacy and unified engines), and compile-fix loops.
    /// </summary>
    public class AgentOrchestrator : IAgentOrchestrator
    {
        private readonly IAIEngineService _aiService;
        private readonly IPlcKnowledgeIndexService _plcKnowledgeIndexService;
        private readonly IPlanningKnowledgeService _planningKnowledgeService;
        private readonly ITiaOpennessService _tiaService;
        private readonly IUIService _uiService;
        private readonly ILocalLlmService _localLlmService;
        private readonly IReActLoopEngine _reactEngine;
        private readonly IAgentTracer _agentTracer;
        private readonly ISemanticCompressor _compressor;
        private readonly TIAOpennessStudio.Services.TokenBudget _tokenBudget;
        private readonly IQualityGateService _qualityGate;
        private readonly IntentClassifier _intentClassifier;
        private readonly AgentPersistenceService _persistence;
        private readonly AgentConfig _agentConfig;
        private readonly IIntentUnderstander _intentUnderstander;
        private readonly IAgentPlanner _agentPlanner;
        private readonly IUnifiedAgentEngine _unifiedEngine;
        private readonly McpToolRegistrySync _mcpToolRegistrySync;
        private readonly IProjectKnowledgeWorkerHostService _knowledgeWorkerHost;
        private readonly NetworkKnowledgeFacade _networkKnowledgeFacade;
        private readonly IUserInteractionService _interaction;   // optional: null => no run scope, writes still gated by :8001
        private readonly IAgentAccessMode _accessMode;           // optional: null => runs are not mode-bound (write gate treats unknown rids as read-only)
        private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingConfirmations = new Dictionary<string, TaskCompletionSource<bool>>();
        private readonly Dictionary<string, AgentActionPlan> _activePlans = new Dictionary<string, AgentActionPlan>();

        /// <summary>The project key for the current request (SafeGetProjectKey result), forwarded to the Python side for evolution collection.</summary>
        private string _currentProjectKey = "default-project";

        /// <summary>Maximum auto-fix cycles for compile-fix loop. Configurable via constructor.</summary>
        public int MaxAutoFixCycles { get; set; } = 2;

        public AgentOrchestrator(
            IAIEngineService aiService,
            ITiaOpennessService tiaService,
            IUIService uiService,
            IPlcKnowledgeIndexService plcKnowledgeIndexService = null,
            IPlanningKnowledgeService planningKnowledgeService = null,
            ILocalLlmService localLlmService = null,
            IReActLoopEngine reactEngine = null,
            IAgentTracer agentTracer = null,
            ISemanticCompressor compressor = null,
            TIAOpennessStudio.Services.TokenBudget tokenBudget = null,
            IQualityGateService qualityGate = null,
            AgentPersistenceService persistence = null,
            AgentConfig agentConfig = null,
            IIntentUnderstander intentUnderstander = null,
            IAgentPlanner agentPlanner = null,
            IUnifiedAgentEngine unifiedEngine = null,
            McpToolRegistrySync mcpToolRegistrySync = null,
            IProjectKnowledgeWorkerHostService knowledgeWorkerHost = null,
            NetworkKnowledgeFacade networkKnowledgeFacade = null,
            IUserInteractionService interaction = null,
            IAgentAccessMode accessMode = null)
        {
            _interaction = interaction;
            _accessMode = accessMode;
            _aiService = aiService;
            _plcKnowledgeIndexService = plcKnowledgeIndexService;
            _planningKnowledgeService = planningKnowledgeService;
            _tiaService = tiaService;
            _uiService = uiService;
            _localLlmService = localLlmService;
            _reactEngine = reactEngine;
            _agentTracer = agentTracer;
            _compressor = compressor;
            _agentConfig = agentConfig ?? AgentConfig.CreateDefault();
            _tokenBudget = tokenBudget ?? new TIAOpennessStudio.Services.TokenBudget(_agentConfig.MaxTokens);
            _qualityGate = qualityGate;
            _intentClassifier = new IntentClassifier(_localLlmService, _aiService);
            _persistence = persistence;
            _intentUnderstander = intentUnderstander;
            _agentPlanner = agentPlanner;
            _unifiedEngine = unifiedEngine;
            _mcpToolRegistrySync = mcpToolRegistrySync;
            _knowledgeWorkerHost = knowledgeWorkerHost;
            _networkKnowledgeFacade = networkKnowledgeFacade;
            
            // Initialize MCP tool synchronization
            if (_mcpToolRegistrySync != null)
            {
                // Sync all connected MCP servers to make tools available
                Task.Run(async () =>
                {
                    try
                    {
                        await _mcpToolRegistrySync.SyncAllServersAsync();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"[AgentOrchestrator] Failed to sync MCP tools during initialization: {ex.Message}");
                    }
                });
            }
        }

        public async Task RunAgentActionAsync(string prompt, Action<AgentActionPlan> onPlanUpdated, Action<string> onContentDelta, CancellationToken ct = default)
        {
            await RunAgentActionAsync(prompt, null, null, null, null, null, null, onPlanUpdated, onContentDelta, ct);
        }

        public async Task RunAgentActionAsync(
            string prompt,
            string targetBlockName,
            string targetBlockType,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            string targetBlockGroupPath,
            string targetTypeGroupPath,
            Action<AgentActionPlan> onPlanUpdated,
            Action<string> onContentDelta,
            CancellationToken ct = default)
        {
            // Legacy callers only care about string deltas; flatten the typed version into text by joining Content/Thinking/Error.
            Action<AgentStreamEvent> bridge = ev =>
            {
                if (onContentDelta == null) return;
                switch (ev.Kind)
                {
                    case AgentStreamKind.Content:
                        onContentDelta(ev.Text);
                        break;
                    case AgentStreamKind.Thinking:
                        onContentDelta("\n" + ev.Text);
                        break;
                    case AgentStreamKind.ToolStart:
                        onContentDelta($"\n🔧 Calling tool: {ev.ToolName}");
                        break;
                    case AgentStreamKind.ToolEnd:
                        onContentDelta($"\n✅ Tool {ev.ToolName} done");
                        break;
                    case AgentStreamKind.Error:
                        onContentDelta("\n\n❌ " + ev.Text);
                        break;
                    case AgentStreamKind.Status:
                        // Status messages don't enter chat by default, but keep the channel open: legacy callers treat them as a plain line.
                        onContentDelta("\n" + ev.Text);
                        break;
                    case AgentStreamKind.Notice:
                        // Legacy text channel: notices carry an English fallback (the typed path localizes them).
                        onContentDelta("\n" + (ev.Notice?.Text ?? ""));
                        break;
                    case AgentStreamKind.TokenUsage:
                        // Token usage does not go through the text channel; it is handled by the typed event.
                        break;
                }
            };
            await RunAgentActionAsync(prompt, targetBlockName, targetBlockType, references, history, targetBlockGroupPath, targetTypeGroupPath, onPlanUpdated, bridge, ct);
        }

        public async Task RunAgentActionAsync(
            string prompt,
            string targetBlockName,
            string targetBlockType,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            string targetBlockGroupPath,
            string targetTypeGroupPath,
            List<string> images,
            Action<AgentActionPlan> onPlanUpdated,
            Action<AgentStreamEvent> onAgentEvent,
            CancellationToken ct = default)
        {
            // Images are passed through for now: append the image info to the prompt so the
            // LLM knows there is image context. Can later become a real multimodal API call.
            string enhancedPrompt = prompt;
            if (images != null && images.Count > 0)
            {
                enhancedPrompt = $"[The user attached {images.Count} image(s); answer using the image content]\n{prompt}";
                AppLogger.Log($"[AGENT] RunAgentActionAsync with {images.Count} images, enhanced prompt");
            }
            await RunAgentActionAsync(prompt, targetBlockName, targetBlockType, references, history, targetBlockGroupPath, targetTypeGroupPath, onPlanUpdated, onAgentEvent, ct);
        }

        public async Task RunAgentActionAsync(
            string prompt,
            string targetBlockName,
            string targetBlockType,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            string targetBlockGroupPath,
            string targetTypeGroupPath,
            Action<AgentActionPlan> onPlanUpdated,
            Action<AgentStreamEvent> onAgentEvent,
            CancellationToken ct = default)
        {
            // Every agent run is a "run scope" for user interaction: approvals asked during it belong to it,
            // "don't ask again in this task" lives exactly as long as it, and stopping the run releases anyone
            // still waiting. Disposed in finally, so a thrown exception or a cancel cannot leave a stale scope
            // behind (a stale scope would keep remembered approvals alive into the next task).
            // The run id doubles as the request id sent to the Python backend (see RunContext), so logs and the
            // approval audit trail of one task can be matched up.
            var runId = Guid.NewGuid().ToString("N");
            // Pin the run to the access mode the toggle showed when it STARTED. A mid-run flip of the switch
            // must not change the rules under this run; it applies to the next one. Released in finally.
            _accessMode?.BindRun(runId, _accessMode.Current);
            try
            {
                using (RunContext.Enter(runId))
                using (_interaction?.BeginRun(runId, ct))
                {
                    await RunAgentActionCoreAsync(prompt, targetBlockName, targetBlockType, references, history,
                        targetBlockGroupPath, targetTypeGroupPath, onPlanUpdated, onAgentEvent, ct);
                }
            }
            finally
            {
                _accessMode?.ReleaseRun(runId);
            }
        }

        private async Task RunAgentActionCoreAsync(
            string prompt,
            string targetBlockName,
            string targetBlockType,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            string targetBlockGroupPath,
            string targetTypeGroupPath,
            Action<AgentActionPlan> onPlanUpdated,
            Action<AgentStreamEvent> onAgentEvent,
            CancellationToken ct = default)
        {
            // One-stop entry: intent recognition + context assembly + plan building + execution

            // Session-level model: sync the current runtime model to TokenBudget so the
            // budget is computed against that model's context window (switching to a
            // smaller model shrinks the budget, avoiding context-window overflow).
            if (_tokenBudget != null && _aiService != null)
            {
                _tokenBudget.Model = _aiService.ModelName ?? "default";
            }

            _uiService.SetStatus("Recognizing intent...");
            var intent = await RecognizeIntentAsync(prompt);

            _uiService.SetStatus($"Building plan for {intent}...");
            string context = "";
            try
            {
                // GetProjectContext internally touches Openness objects (STA only), so it must be
                // marshaled back to the UI thread, otherwise calling it on a Task.Run background
                // thread throws a cross-thread exception.
                context = await RunOnTiaSta(() => Task.FromResult(SafeGetProjectContext()));
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AGENT] GetProjectContext outer catch: {ex.Message}");
            }

            // Append the user-referenced files / code blocks to the context with clear markers so the LLM can tell them apart.
            // NOTE: the references block is deliberately NOT merged into `context` yet. The semantic
            // compressor below is line/keyword based and capped at a few hundred tokens; running the
            // user's explicitly attached block (full XML/SCL) through it dropped almost everything
            // except the interface section. It is re-attached verbatim right after compression.
            string referencesBlock = BuildReferencesBlock(references);

            // Tell the LLM the user-selected target group path so it doesn't invent names or write to the wrong location.
            string targetBlock = (targetBlockGroupPath ?? "").Trim();
            string targetType = (targetTypeGroupPath ?? "").Trim();
            if (!string.IsNullOrEmpty(targetBlock) || !string.IsNullOrEmpty(targetType))
            {
                var locSb = new System.Text.StringBuilder();
                locSb.AppendLine("## Target Location");
                locSb.AppendLine("If you generate or modify code, write-back will go to:");
                if (!string.IsNullOrEmpty(targetBlock))
                    locSb.AppendLine($"- Blocks (FB/FC/DB/OB): `Blocks\\{targetBlock}`");
                if (!string.IsNullOrEmpty(targetType))
                    locSb.AppendLine($"- Types (UDT): `Types\\{targetType}`");
                locSb.AppendLine();
                context = locSb.ToString() + (context ?? "");
            }

            string projectKey = "default-project";
            try
            {
                projectKey = await RunOnTiaSta(() => Task.FromResult(SafeGetProjectKey()));
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AGENT] GetProjectKey outer catch: {ex.Message}");
            }
            _currentProjectKey = projectKey;

            // Planning knowledge is an optional, project-scoped hint recalled from past
            // runs. It is passed to the backend as a *separate* structured field so the
            // planner can distinguish proven experience from verified project fact. For
            // legacy paths that only consume `context`, we still fall back to prepending.
            var planningKnowledge = BuildPlanningKnowledgeContext(
                projectKey, prompt, intent.ToString(), targetBlockType, targetBlock, targetType);

            // Project knowledge is factual evidence, separate from planning history. Prefer
            // the isolated Worker cache so the Agent sees the same index as the UI/tools.
            try
            {
                ContextPack knowledgePack = null;
                if (_knowledgeWorkerHost != null && await _knowledgeWorkerHost.IsHealthyAsync().ConfigureAwait(false))
                {
                    knowledgePack = await _knowledgeWorkerHost.BuildContextPackAsync(prompt, null, 5,
                        ProjectKnowledgeFreshnessModes.CachePreferred, ct).ConfigureAwait(false);
                }
                else if (_plcKnowledgeIndexService != null)
                {
                    knowledgePack = _plcKnowledgeIndexService.BuildContextPack(projectKey, prompt, null, 5);
                }

                if (knowledgePack?.CodeFragments?.Count > 0)
                {
                    var projectKnowledge = knowledgePack.ToPromptString();
                    context = string.IsNullOrWhiteSpace(context)
                        ? projectKnowledge
                        : context + "\n\n## Verified Project Knowledge\n" + projectKnowledge;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentOrchestrator] Project knowledge lookup failed: {ex.Message}");
            }

            // ── Network-centric Evidence Pack (v2.0) ──
            // Inject Network-level evidence from Published Snapshot if available.
            // This provides per-Network deterministic facts and resolved relations.
            if (_networkKnowledgeFacade != null)
            {
                try
                {
                    var networkContext = _networkKnowledgeFacade.BuildAgentContext(projectKey, prompt, 8192);
                    if (!string.IsNullOrWhiteSpace(networkContext))
                    {
                        context = string.IsNullOrWhiteSpace(context)
                            ? networkContext
                            : context + "\n\n" + networkContext;
                        AppLogger.Log($"[AgentOrchestrator] Network evidence injected: {networkContext.Length} chars");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[AgentOrchestrator] Network evidence lookup failed: {ex.Message}");
                }
            }

            // Apply semantic compression to context (preserves SCL/XML structure)
            if (_compressor != null && !string.IsNullOrEmpty(context))
            {
                try
                {
                    var compressRequest = new CompressRequest
                    {
                        Content = context,
                        Type = ContentType.ProjectContext,
                        Config = CompressConfig.ForLocalModel(prompt)
                    };
                    context = await _compressor.CompressAsync(compressRequest, ct);
                    AppLogger.Log($"[AgentOrchestrator] Context compressed: {context.Length} chars");
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[AgentOrchestrator] Context compression failed: {ex.Message}");
                }
            }

            // Live environment facts (TIA version + CPU family) must survive the semantic
            // compressor: the user may switch the CPU or TIA version mid-session, so the block is
            // re-read and re-attached verbatim instead of being compressed into the context.
            try
            {
                var envProfile = await RunOnTiaSta(() => Task.FromResult(SafeGetEnvironmentProfile()));
                var environmentBlock = BuildEnvironmentBlock(envProfile);
                if (!string.IsNullOrWhiteSpace(environmentBlock))
                {
                    context = string.IsNullOrWhiteSpace(context)
                        ? environmentBlock
                        : environmentBlock + "\n\n" + context;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentOrchestrator] environment profile failed: {ex.Message}");
            }

            // Re-attach user-attached references uncompressed, at the very top of the context.
            if (!string.IsNullOrWhiteSpace(referencesBlock))
            {
                context = string.IsNullOrWhiteSpace(context)
                    ? referencesBlock
                    : referencesBlock + "\n\n" + context;
                AppLogger.Log($"[AgentOrchestrator] References attached uncompressed: {referencesBlock.Length} chars, final context {context.Length} chars");
            }

            // ── Phase 4: Planning stage (LLM-generated execution plans) ──
            // Skip when UseChatPlanEndpoint is true — Python handles planning internally.
            StructuredIntent structuredIntent = null;
            ExecutionPlan llmPlan = null;
            if (_agentConfig.EnablePlanning && _intentUnderstander != null
                && !_agentConfig.UseChatPlanEndpoint)
            {
                try
                {
                    _uiService.SetStatus("Understanding intent deeply...");
                    structuredIntent = await _intentUnderstander.UnderstandAsync(prompt, context, history as List<Dictionary<string, string>>, ct);

                    // Skip Planning for simple tasks (e.g. "explain this", "search for X")
                    var minComplexity = ParseComplexity(_agentConfig.MinComplexityForPlanning);
                    if (structuredIntent.Complexity >= minComplexity && _agentPlanner != null)
                    {
                        _uiService.SetStatus("Generating execution plan...");
                        llmPlan = await _agentPlanner.CreatePlanAsync(prompt, context, structuredIntent, ct);

                        // Emit plan to UI
                        if (llmPlan?.Steps.Count > 0)
                        {
                            var planSummary = $"📋 Plan ({llmPlan.Complexity}): {llmPlan.Summary}\n";
                            foreach (var step in llmPlan.Steps)
                            {
                                var icon = step.Phase switch
                                {
                                    "explore" => "🔍", "read" => "📖", "generate" => "📝",
                                    "write" => "✍️", "verify" => "🔨", _ => "➡️"
                                };
                                planSummary += $"  {icon} {step.Description}\n";
                            }
                            onAgentEvent?.Invoke(AgentStreamEvent.Thinking(planSummary));
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[AgentOrchestrator] Planning failed, falling back to static plan: {ex.Message}");
                }
            }

            // ── History compression (shared by both paths) ──
            List<Dictionary<string, string>> processedHistory = null;
            if (history != null && history.Count > 0)
            {
                if (_tokenBudget != null)
                {
                    var historyList = new List<Dictionary<string, string>>(history);
                    bool compressed = false;

                    var historyTokens = _tokenBudget.EstimateTokens(
                        string.Join("\n", historyList.Select(m => m.ContainsKey("content") ? m["content"] : "")));
                    if (historyTokens > _tokenBudget.MaxHistoryTokens * 0.2)
                    {
                        try
                        {
                            var compressedSummary = await CompressHistoryAsync(historyList, ct);
                            if (compressedSummary != null)
                            {
                                var recentCount = Math.Min(3, historyList.Count);
                                var recent = historyList.Skip(historyList.Count - recentCount).ToList();
                                processedHistory = new List<Dictionary<string, string>> { compressedSummary };
                                processedHistory.AddRange(recent);
                                compressed = true;
                                AppLogger.Log($"[TokenBudget] AI compression: {historyList.Count} messages → 1 summary + {recentCount} recent");
                                _agentTracer?.LogToolExecution("HistoryCompression", "CompressHistory",
                                    $"Compressed {historyList.Count} messages to summary + {recentCount} recent", TimeSpan.Zero);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Log($"[TokenBudget] Compression failed, falling back to truncation: {ex.Message}");
                        }
                    }

                    if (!compressed)
                    {
                        var histTruncResult = _tokenBudget.TruncateHistoryList(historyList);
                        if (histTruncResult.Truncated)
                        {
                            AppLogger.Log($"[TokenBudget] {histTruncResult.Warning}");
                            _agentTracer?.LogToolExecution("HistoryTruncation", "TruncateHistory", histTruncResult.Warning, TimeSpan.Zero);
                        }
                        processedHistory = histTruncResult.Content;
                    }
                }
                else
                {
                    processedHistory = new List<Dictionary<string, string>>(history);
                }
            }
            else
            {
                processedHistory = null;
            }

            // ── Phase 7: Unified Engine routing ──
            // When enabled, ALL intents go through the unified ReAct loop with LLM planning.
            // The legacy BuildPlanAsync + ExecutePlanAsync switch-case is skipped entirely.
            if (_agentConfig.UseUnifiedEngine && _unifiedEngine != null)
            {
                // Create a lightweight plan for UI tracking (not for execution)
                var unifiedPlan = new AgentActionPlan
                {
                    Intent = intent,
                    Prompt = prompt,
                    Context = context,
                    TargetBlockName = targetBlockName,
                    TargetBlockType = targetBlockType,
                    History = processedHistory,
                    State = AgentExecutionState.Running
                };
                _activePlans[unifiedPlan.Id] = unifiedPlan;
                onPlanUpdated?.Invoke(unifiedPlan);

                _uiService.SetStatus($"Executing unified workflow ({intent})...");

                Func<string, string, Task<bool>> unifiedConfirm = async (toolName, args) =>
                {
                    var step = new AgentActionStep { Id = Guid.NewGuid().ToString(), PlanId = unifiedPlan.Id, ActionType = toolName, Description = $"Executing tool: {toolName}", State = AgentExecutionState.WaitingConfirmation };
                    unifiedPlan.Steps.Add(step);
                    unifiedPlan.State = AgentExecutionState.Running;
                    onPlanUpdated?.Invoke(unifiedPlan);

                    var tcs = new TaskCompletionSource<bool>();
                    _pendingConfirmations[$"{unifiedPlan.Id}_{step.Id}"] = tcs;
                    using (ct.Register(() => tcs.TrySetCanceled()))
                    {
                        bool confirmed = await tcs.Task;
                        _pendingConfirmations.Remove($"{unifiedPlan.Id}_{step.Id}");
                        step.State = confirmed ? AgentExecutionState.Done : AgentExecutionState.Cancelled;
                        onPlanUpdated?.Invoke(unifiedPlan);
                        return confirmed;
                    }
                };

                var unifiedResult = await _unifiedEngine.ExecuteAsync(
                    prompt, intent, context, processedHistory, onAgentEvent, unifiedConfirm, ct,
                    plan: llmPlan, structuredIntent: structuredIntent, planningKnowledge: planningKnowledge,
                    projectKey: _currentProjectKey);

                // Prefer the real plan-tracking report when available; otherwise fall back
                // to a coarse completed/total estimate based on final status.
                var unifiedReport = unifiedResult.PlanCompletion;
                var unifiedTotalSteps = unifiedReport?.TotalSteps ?? (llmPlan?.Steps?.Count ?? 0);
                var unifiedCompletedSteps = unifiedReport?.CompletedSteps
                    ?? (unifiedResult.Status == ReActStatus.Completed ? unifiedTotalSteps : 0);
                var unifiedDeviations = unifiedReport != null
                    ? unifiedReport.SkippedSteps + unifiedReport.FailedSteps + (unifiedReport.Deviations?.Count ?? 0)
                    : 0;

                RecordPlanningOutcome(
                    projectKey,
                    intent.ToString(),
                    prompt,
                    targetBlockType,
                    targetBlock,
                    llmPlan,
                    unifiedResult.Status == ReActStatus.Completed,
                    unifiedResult.Status.ToString(),
                    unifiedCompletedSteps,
                    unifiedTotalSteps,
                    unifiedDeviations);

                switch (unifiedResult.Status)
                {
                    case ReActStatus.Completed:
                        onAgentEvent?.Invoke(AgentStreamEvent.Content(unifiedResult.FinalAnswer ?? ""));
                        break;
                    case ReActStatus.Cancelled:
                        onAgentEvent?.Invoke(AgentStreamEvent.Error("Task cancelled by user."));
                        break;
                    case ReActStatus.Error:
                        onAgentEvent?.Invoke(AgentStreamEvent.Error("Unified engine error: " + unifiedResult.Message));
                        break;
                    case ReActStatus.MaxIterationsReached:
                        onAgentEvent?.Invoke(AgentStreamEvent.Error("Reached maximum iterations. Task may be incomplete."));
                        if (!string.IsNullOrEmpty(unifiedResult.FinalAnswer))
                            onAgentEvent?.Invoke(AgentStreamEvent.Content(unifiedResult.FinalAnswer));
                        break;
                }

                unifiedPlan.State = AgentExecutionState.Done;
                onPlanUpdated?.Invoke(unifiedPlan);
                _activePlans.Remove(unifiedPlan.Id);
                _persistence?.DeletePlanSnapshot(unifiedPlan.Id);

                _uiService.SetStatus("Ready");
                return;
            }

            // The fixed-step legacy path (BuildPlanAsync + ExecutePlanAsync) has been removed:
            // the unified engine is the only execution path.
            onAgentEvent?.Invoke(AgentStreamEvent.Error("Unified agent engine is not available (UseUnifiedEngine is off or the engine is not registered)."));
            _uiService.SetStatus("Ready");
        }

        /// <summary>
        /// Packs the AI reference list into an LLM-readable context section. Uses
        /// markdown headers + separators so the model can tell each reference apart
        /// in a multi-file scenario.
        /// </summary>
        private static string BuildReferencesBlock(IReadOnlyList<AiReferenceItem> references)
        {
            if (references == null || references.Count == 0) return string.Empty;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## User-Attached References");
            sb.AppendLine("The user has attached the following files/blocks as context. Use them when answering or generating code; cite by display name where helpful.");
            sb.AppendLine();
            for (int i = 0; i < references.Count; i++)
            {
                var r = references[i];
                if (r == null) continue;
                sb.AppendLine($"### Reference {i + 1}: {r.DisplayName ?? r.Source ?? "(unnamed)"}");
                sb.AppendLine($"_Origin: {r.Source ?? "unknown"}_");
                sb.AppendLine();
                sb.AppendLine(r.Packed ?? string.Empty);
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public Task<AgentIntent> RecognizeIntentAsync(string prompt, List<Dictionary<string, string>> history = null)
            => _intentClassifier.ClassifyAsync(prompt, history);

        public void ConfirmStep(string planId, string stepId, bool confirmed = true)
        {
            string key = $"{planId}_{stepId}";
            if (_pendingConfirmations.TryGetValue(key, out var tcs))
            {
                tcs.TrySetResult(confirmed);
            }
        }

        public AgentActionPlan GetPlan(string planId)
        {
            _activePlans.TryGetValue(planId, out var plan);
            return plan;
        }

        /// <summary>
        /// Uses AI to compress the history messages into one summary, greatly
        /// reducing token usage while preserving key context. Returns a single
        /// system message, or null on failure (the caller should fall back to
        /// truncation).
        /// </summary>
        public async Task<Dictionary<string, string>> CompressHistoryAsync(
            List<Dictionary<string, string>> history,
            CancellationToken ct = default)
        {
            if (history == null || history.Count < 4) return null;

            // Estimate the history token count; below 10% of MaxHistoryTokens there's no need to compress.
            var estimatedTokens = _tokenBudget?.EstimateTokens(
                string.Join("\n", history.Select(m => m.ContainsKey("content") ? m["content"] : ""))) ?? 0;
            var minCompressTokens = _tokenBudget != null ? _tokenBudget.MaxHistoryTokens * 0.1 : 6000;
            if (estimatedTokens < minCompressTokens) return null;

            try
            {
                // Build the compression prompt
                var sb = new StringBuilder();
                sb.AppendLine("Summarize the following conversation between a user and a TIA Portal PLC programming assistant.");
                sb.AppendLine("Keep all technical details: block names, variable names, data types, logic decisions, error messages.");
                sb.AppendLine("Output ONLY the summary, no extra text.");
                sb.AppendLine();
                sb.AppendLine("--- Conversation ---");
                foreach (var msg in history)
                {
                    var role = msg.ContainsKey("role") ? msg["role"] : "unknown";
                    var content = msg.ContainsKey("content") ? msg["content"] : "";
                    // Truncate overly long messages so the compression prompt itself doesn't get too large.
                    if (content.Length > 2000) content = content.Substring(0, 2000) + "...";
                    sb.AppendLine($"[{role}]: {content}");
                }
                sb.AppendLine("--- End ---");

                var compressPrompt = sb.ToString();
                AppLogger.Log($"[Compress] Compressing {history.Count} history messages (~{estimatedTokens} tokens)...");

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                var summary = await _aiService.GenerateSclAsync(compressPrompt, "", null, null, intent: "Chat");
                if (string.IsNullOrWhiteSpace(summary) || summary.StartsWith("// Error"))
                {
                    AppLogger.Log("[Compress] AI compression failed, will fall back to truncation");
                    return null;
                }

                // Limit the summary length
                if (summary.Length > 3000) summary = summary.Substring(0, 3000) + "...";

                AppLogger.Log($"[Compress] Compression successful: {history.Count} messages → {summary.Length} chars summary");
                return new Dictionary<string, string>
                {
                    { "role", "system" },
                    { "content", $"[Conversation summary of previous {history.Count} messages]\n{summary}" }
                };
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Compress] Compression error: {ex.Message}");
                return null;
            }
        }

        public static SclStructure ParseSclToStructuredData(string sclCode)
            => SclStructureParser.ParseSclToStructuredData(sclCode);

        /// <summary>
        /// The TIA Openness API is single-threaded (STA). Once the agent call is
        /// pushed onto the thread pool via Task.Run by ChatViewModel, any access to
        /// Openness objects throws "Cross-thread operation is not valid in Openness
        /// within STA". This helper marshals Openness calls onto the dedicated TIA STA
        /// host thread (TiaStaHost) instead of the WPF UI thread, so the UI stays
        /// responsive during long Openness operations (compile, import/export).
        /// </summary>
        private static Task<T> RunOnTiaSta<T>(Func<Task<T>> work)
            => TiaStaHost.Instance.RunAsync(work);

        private string SafeGetProjectContext()
        {
            try
            {
                return _tiaService.GetProjectContext() ?? "";
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AGENT] SafeGetProjectContext failed: {ex.Message}");
                return "";
            }
        }

        private string SafeGetProjectKey()
        {
            try
            {
                var path = _tiaService?.Project?.Path?.FullName;
                if (!string.IsNullOrWhiteSpace(path))
                    return path;

                return _tiaService?.Project?.Name ?? "default-project";
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AGENT] SafeGetProjectKey failed: {ex.Message}");
                return "default-project";
            }
        }

        private EnvironmentProfileInfo SafeGetEnvironmentProfile()
        {
            try
            {
                return _tiaService?.GetEnvironmentProfile();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AGENT] SafeGetEnvironmentProfile failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Render the live environment profile as a verbatim prompt block. This is the single place
        /// where family → generation rules are written on the C# side; the Python side only
        /// re-states facts and keeps its own table for compile-error classification.
        /// </summary>
        private static string BuildEnvironmentBlock(EnvironmentProfileInfo info)
        {
            if (info == null) return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## Environment Profile (live, authoritative)");
            sb.AppendLine("TIA version: " + info.TiaVersion);
            sb.AppendLine("Project: " + info.ProjectName + " (" + info.ProjectKey + ")");
            sb.AppendLine("PLC: " + info.PlcName + "; device: " + info.DeviceName
                          + "; CPU: " + info.CpuName + "; order number: " + info.OrderNumber);
            sb.AppendLine("PLC family: " + info.PlcFamily);
            sb.AppendLine("Environment fingerprint: " + info.Fingerprint + " (detected " + info.DetectedAt + ")");

            switch ((info.PlcFamily ?? "").Trim().ToUpperInvariant())
            {
                case "S7-300/400":
                    sb.AppendLine("Rules: { S7_Optimized_Access := 'FALSE' } is required; DTL is not supported " +
                                  "(use DATE_AND_TIME or discrete fields); R_TRIG/F_TRIG are not supported " +
                                  "(write manual edge detection with a BOOL memory bit). Avoid S7-1200/1500-only features.");
                    break;
                case "S7-1200":
                case "S7-1500":
                    sb.AppendLine("Rules: optimized block access, DTL and R_TRIG/F_TRIG are available.");
                    break;
                default:
                    sb.AppendLine("Rules: CPU family is unknown — re-check get_environment_profile before writing.");
                    break;
            }

            if (!info.Live)
                sb.AppendLine("Note: the live read failed; treat these facts as unverified.");

            sb.AppendLine("If the user switches the CPU or TIA version mid-task, call get_environment_profile again before further writes.");
            sb.AppendLine();
            return sb.ToString();
        }

        private string BuildPlanningKnowledgeContext(
            string projectKey,
            string prompt,
            string taskIntent,
            string targetBlockType,
            string targetBlockGroup,
            string targetTypeGroup)
        {
            if (_planningKnowledgeService == null || !_agentConfig.EnablePlanningKnowledge)
                return string.Empty;

            try
            {
                var hits = _planningKnowledgeService.Search(projectKey, prompt, taskIntent, 3);
                if (hits == null || hits.Count == 0)
                    return string.Empty;

                var sb = new StringBuilder();
                sb.AppendLine("## Planning Knowledge (historical guidance, not project fact)");
                sb.AppendLine("Reuse only the tool paths that fit the current request; verify all observations against the current project.");
                foreach (var hit in hits)
                {
                    var warning = hit.Success
                        ? ""
                        : " [FAILED case — do NOT reuse; shown only to avoid repeating the mistake]";
                    sb.AppendLine($"- Intent: {hit.TaskIntent}; success: {hit.Success}; steps: {hit.CompletedSteps}/{hit.TotalSteps}; status: {hit.ExecutionStatus}{warning}");
                    if (!string.IsNullOrWhiteSpace(hit.UsedTools)) sb.AppendLine($"  Tools: {hit.UsedTools}");
                    if (!string.IsNullOrWhiteSpace(hit.PlanSummary)) sb.AppendLine($"  Plan: {hit.PlanSummary}");
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PlanningKnowledge] Search failed: {ex.Message}");
                return string.Empty;
            }
        }

        private void RecordPlanningOutcome(
            string projectKey,
            string taskIntent,
            string prompt,
            string targetBlockType,
            string targetBlockGroup,
            ExecutionPlan plan,
            bool success,
            string executionStatus,
            int completedSteps,
            int totalSteps,
            int deviationCount = 0)
        {
            if (_planningKnowledgeService == null || !_agentConfig.EnablePlanningKnowledge)
                return;

            try
            {
                var totalPlanSteps = Math.Max(0, totalSteps);
                var completedPlanSteps = Math.Max(0, Math.Min(completedSteps, totalPlanSteps));
                var usedTools = plan?.Steps == null
                    ? string.Empty
                    : string.Join(", ", plan.Steps.Where(s => !string.IsNullOrWhiteSpace(s.ToolName)).Select(s => s.ToolName).Distinct(StringComparer.OrdinalIgnoreCase));
                var summary = plan?.Summary ?? string.Empty;
                var targetKinds = string.Join("/", new[] { targetBlockType, targetBlockGroup }.Where(x => !string.IsNullOrWhiteSpace(x)));

                // Quality gate: only outcomes that finished the whole plan (or hit the
                // configured completion ratio) count as reusable successes. Everything
                // else is stored as a failure pattern so it won't be recalled as guidance.
                var completionRatio = totalPlanSteps > 0
                    ? (double)completedPlanSteps / totalPlanSteps
                    : (success ? 1.0 : 0.0);
                var qualifiesAsSuccess = success && completionRatio >= _agentConfig.PlanningKnowledgeSuccessThreshold;

                _planningKnowledgeService.Record(
                    projectKey, taskIntent, prompt, targetKinds, usedTools, summary,
                    qualifiesAsSuccess, completedPlanSteps, totalPlanSteps, Math.Max(0, deviationCount), executionStatus);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PlanningKnowledge] Record failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Parse a complexity string to enum. Defaults to Medium.
        /// </summary>
        private static TaskComplexity ParseComplexity(string complexity)
        {
            if (string.IsNullOrWhiteSpace(complexity))
                return TaskComplexity.Medium;

            return complexity.ToLowerInvariant() switch
            {
                "simple" => TaskComplexity.Simple,
                "complex" => TaskComplexity.Complex,
                _ => TaskComplexity.Medium
            };
        }
    }
}
