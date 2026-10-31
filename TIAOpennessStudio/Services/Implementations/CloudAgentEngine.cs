using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Cloud delegation engine. The Python LangGraph Agent (or multi-agent supervisor)
    /// runs the full ReAct loop; the C# side only forwards SSE events to the UI, with
    /// no TOOL_CALL parsing and no tool execution.
    /// </summary>
    public class CloudAgentEngine : ICloudAgentEngine
    {
        private readonly IAIEngineService _aiService;
        private readonly AgentConfig _agentConfig;

        public CloudAgentEngine(IAIEngineService aiService, AgentConfig agentConfig)
        {
            _aiService = aiService;
            _agentConfig = agentConfig ?? AgentConfig.CreateDefault();
        }

        /// <summary>
        /// Parse a Python-emitted plan_progress JSON payload into a PlanCompletionReport.
        /// Returns null on any parse failure (best-effort — never throws).
        /// </summary>
        private static PlanCompletionReport ParsePlanProgress(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var report = new PlanCompletionReport
                {
                    TotalSteps = root.TryGetProperty("total_steps", out var ts) ? ts.GetInt32() : 0,
                    CompletedSteps = root.TryGetProperty("completed_steps", out var cs) ? cs.GetInt32() : 0,
                    SkippedSteps = root.TryGetProperty("skipped_steps", out var sk) ? sk.GetInt32() : 0,
                    FailedSteps = root.TryGetProperty("failed_steps", out var fs) ? fs.GetInt32() : 0
                };
                if (root.TryGetProperty("deviations", out var devs) && devs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in devs.EnumerateArray())
                    {
                        var msg = d.GetString();
                        if (!string.IsNullOrWhiteSpace(msg)) report.Deviations.Add(msg);
                    }
                }
                return report;
            }
            catch
            {
                return null;
            }
        }

        private static DeliveryItemStatus ParseDeliveryStatus(string status)
        {
            switch (status)
            {
                case "prepared_not_written":
                    return DeliveryItemStatus.PreparedNotWritten;
                case "failed":
                    return DeliveryItemStatus.Failed;
                default:
                    return DeliveryItemStatus.Written;
            }
        }

        /// <summary>
        /// Parse the Python-emitted "delivery" event JSON into a DeliveryBlock.
        /// Returns null when there are no blocks or the payload is malformed (best-effort).
        /// </summary>
        private static DeliveryBlock ParseDelivery(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("blocks", out var blocksEl) || blocksEl.ValueKind != JsonValueKind.Array)
                    return null;

                var block = new DeliveryBlock();
                foreach (var item in blocksEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    var deliveryItem = new DeliveryItem
                    {
                        Name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null,
                        Type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null,
                        Language = item.TryGetProperty("lang", out var langEl) ? langEl.GetString() : null,
                        Number = item.TryGetProperty("number", out var numEl) && numEl.TryGetInt32(out var numVal) ? numVal : (int?)null,
                        Status = ParseDeliveryStatus(item.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null)
                    };

                    if (item.TryGetProperty("compiled", out var compiledEl) &&
                        (compiledEl.ValueKind == JsonValueKind.True || compiledEl.ValueKind == JsonValueKind.False))
                    {
                        deliveryItem.Compiled = compiledEl.GetBoolean();
                    }

                    block.Items.Add(deliveryItem);
                }

                if (block.Items.Count == 0) return null;
                block.Summary = LanguageManager.Instance.Format(
                    "Agent.delivery.summary", $"{block.Items.Count} blocks", block.Items.Count);
                return block;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Cloud mode: Python LangGraph Agent handles the full ReAct loop
        /// (reasoning + function calling). C# only forwards SSE events to UI.
        /// No TOOL_CALL parsing, no tool execution on the C# side.
        /// </summary>
        public async Task<ReActResult> ExecuteCloudAsync(
            string prompt,
            string context = "",
            List<Dictionary<string, string>> history = null,
            Action<AgentStreamEvent> onEvent = null,
            CancellationToken ct = default,
            string planningKnowledge = "",
            string projectKey = "",
            string intent = "")
        {
            var result = new ReActResult();
            var resultBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<string>();
            PlanCompletionReport planProgress = null;

            onEvent?.Invoke(AgentStreamEvent.Thinking("Starting cloud Agent (Python LangGraph)..."));
            onEvent?.Invoke(AgentStreamEvent.Thinking("THINKING: Analyzing task and planning approach..."));

            try
            {
                await _aiService.GenerateSclStreamingExAsync(prompt, ev =>
                {
                    if (ev == null) return;
                    switch (ev.EventType)
                    {
                        case "content":
                        case "delta":
                            var text = ev.TextContent ?? "";
                            resultBuilder.Append(text);
                            onEvent?.Invoke(AgentStreamEvent.Content(text));
                            break;

                        case "notice":
                            if (ev.Notice != null)
                                onEvent?.Invoke(AgentStreamEvent.NoticeUpdate(ev.Notice));
                            break;

                        case "thinking":
                            var thinkingText = (ev.TextContent ?? ev.Thought?.Text ?? "").Trim();
                            if (string.IsNullOrEmpty(thinkingText)) break;
                            // Filter internal markers
                            if (thinkingText == "done" || thinkingText == "generating" ||
                                thinkingText == "json_fallback" || thinkingText == "json_parsed" ||
                                thinkingText == "langchain-agent" || thinkingText == "xml-ast-llm" ||
                                thinkingText == "xml-analysis-llm")
                                break;
                            // Tool call markers from Python Agent
                            if (thinkingText.StartsWith("🔧"))
                            {
                                int colon = thinkingText.IndexOf(':');
                                string name = colon >= 0 ? thinkingText.Substring(colon + 1).Trim() : thinkingText;
                                onEvent?.Invoke(AgentStreamEvent.ToolStart(name));
                            }
                            else if (thinkingText.StartsWith("✅"))
                            {
                                string body = thinkingText;
                                int idx1 = body.IndexOf("工具");
                                if (idx1 >= 0) body = body.Substring(idx1 + 2).Trim();
                                int idx2 = body.IndexOf("完成");
                                if (idx2 >= 0) body = body.Substring(0, idx2).Trim();
                                onEvent?.Invoke(AgentStreamEvent.ToolEnd(body));
                            }
                            else
                            {
                                onEvent?.Invoke(AgentStreamEvent.Thinking(thinkingText));
                            }
                            break;

                        case SseEventTypes.ToolCall:
                            // [NEW] Structured tool_call event from Python Agent
                            try
                            {
                                using var toolDoc = JsonDocument.Parse(ev.TextContent ?? "{}");
                                var root = toolDoc.RootElement;
                                if (root.TryGetProperty("name", out var tn))
                                {
                                    var toolName = tn.GetString();
                                    var status = root.TryGetProperty("status", out var st) ? st.GetString() : "";
                                    if (status == "started")
                                        onEvent?.Invoke(AgentStreamEvent.ToolStart(toolName ?? "tool"));
                                    else if (status == "completed")
                                        onEvent?.Invoke(AgentStreamEvent.ToolEnd(toolName ?? "tool"));
                                }
                            }
                            catch { /* best-effort: ignore malformed tool_call JSON */ }
                            break;

                        case "error":
                            errorBuilder.AppendLine(ev.ErrorMessage ?? ev.TextContent ?? "");
                            onEvent?.Invoke(AgentStreamEvent.Error(ev.ErrorMessage ?? ev.TextContent ?? ""));
                            break;

                        case SseEventTypes.TokenUsage:
                            // [NEW] Token usage feedback from the Python backend
                            try
                            {
                                using var tuDoc = JsonDocument.Parse(ev.TextContent ?? "{}");
                                var tuRoot = tuDoc.RootElement;
                                var tuData = new TokenUsageData
                                {
                                    PromptTokens = tuRoot.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
                                    ContextTokens = tuRoot.TryGetProperty("context_tokens", out var ct2) ? ct2.GetInt32() : 0,
                                    HistoryTokens = tuRoot.TryGetProperty("history_tokens", out var ht) ? ht.GetInt32() : 0,
                                    ToolOutputTokens = tuRoot.TryGetProperty("tool_output_tokens", out var tot) ? tot.GetInt32() : 0,
                                    TotalUsed = tuRoot.TryGetProperty("total_used", out var tu) ? tu.GetInt32() : 0,
                                    TotalBudget = tuRoot.TryGetProperty("total_budget", out var tb) ? tb.GetInt32() : 0,
                                    Remaining = tuRoot.TryGetProperty("remaining", out var rem) ? rem.GetInt32() : 0,
                                    UsagePercent = tuRoot.TryGetProperty("usage_percent", out var up) ? up.GetDouble() : 0,
                                    Status = tuRoot.TryGetProperty("status", out var st2) ? st2.GetString() : "Ok",
                                };
                                onEvent?.Invoke(AgentStreamEvent.TokenUsageUpdate(tuData));
                            }
                            catch { /* best-effort */ }
                            break;

                        case SseEventTypes.PlanProgress:
                            planProgress = ParsePlanProgress(ev.TextContent) ?? planProgress;
                            if (planProgress != null)
                                onEvent?.Invoke(AgentStreamEvent.Thinking(planProgress.Summary));
                            break;

                        case "delivery":
                        {
                            var deliveryBlock = ParseDelivery(ev.TextContent);
                            if (deliveryBlock != null)
                                onEvent?.Invoke(AgentStreamEvent.DeliveryUpdate(deliveryBlock));
                            break;
                        }

                        case SseEventTypes.Milestone:
                            try
                            {
                                using var mDoc = JsonDocument.Parse(ev.TextContent ?? "{}");
                                var mRoot = mDoc.RootElement;
                                string planId = mRoot.TryGetProperty("plan_id", out var p) ? p.GetString() : "";
                                string phaseId = mRoot.TryGetProperty("phase_id", out var ph) ? ph.GetString() : "";
                                string name = mRoot.TryGetProperty("name", out var n) ? n.GetString() : "";
                                onEvent?.Invoke(AgentStreamEvent.Milestone(planId, phaseId, name));
                            }
                            catch { /* best-effort — ignore malformed milestone JSON */ }
                            break;

                        case "done":
                            if (errorBuilder.Length > 0)
                                tcs.TrySetResult(errorBuilder.ToString().Trim());
                            else
                                tcs.TrySetResult(resultBuilder.ToString());
                            break;
                    }
                }, context, history, ct, intent: intent, planningKnowledge: planningKnowledge, projectKey: projectKey, evolutionEnabled: _agentConfig.EnableRepairEvolution);

                var finalText = await tcs.Task;
                result.Status = ReActStatus.Completed;
                result.FinalAnswer = finalText;
                result.Message = "Cloud Agent completed";
                result.PlanCompletion = planProgress;
            }
            catch (OperationCanceledException)
            {
                result.Status = ReActStatus.Cancelled;
                result.Message = "Cancelled by user";
                result.FinalAnswer = resultBuilder.Length > 0 ? resultBuilder.ToString() : null;
            }
            catch (Exception ex)
            {
                result.Status = ReActStatus.Error;
                result.Message = ex.Message;
                result.FinalAnswer = resultBuilder.Length > 0 ? resultBuilder.ToString() : null;
            }

            return result;
        }

        /// <summary>
        /// Chat Plan mode (cloud only): the Python multi-agent supervisor handles the full
        /// coder / reviewer / compiler loop.
        /// C# only forwards SSE events to UI. No TOOL_CALL parsing, no tool execution.
        /// Never used for Ollama/local models; they always run in ExecuteAsync.
        /// </summary>
        public async Task<ReActResult> ExecuteChatPlanAsync(
            string prompt,
            string context = "",
            List<Dictionary<string, string>> history = null,
            Action<AgentStreamEvent> onEvent = null,
            CancellationToken ct = default,
            string planningKnowledge = "",
            string intent = "",
            string projectKey = "")
        {
            var result = new ReActResult();
            var resultBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<string>();
            PlanCompletionReport planProgress = null;

            // A1+A2+A3: project-level long tasks run the background job model (/plan/run + follow stream),
            // fully decoupling execution from the connection. Only non-project requests (Started=false) fall back
            // to the single-block SSE.
            PlanStartResult startedPlan = null;
            try
            {
                startedPlan = await _aiService.StartPlanAsync(prompt, context ?? "", history, intent ?? "", planningKnowledge ?? "", projectKey ?? "", _agentConfig?.VerifyCompileAtPlanEnd == true);
            }
            catch (Exception ex)
            {
                AppLogger.Log("[PLAN] StartPlanAsync failed, falling back to /chat_plan: " + ex.Message);
                startedPlan = null;
            }
            if (startedPlan != null && startedPlan.Started)
            {
                onEvent?.Invoke(AgentStreamEvent.Thinking(LanguageManager.Instance.Format(
                    "Agent.plan.startedDetail",
                    $"📋 Project plan started ({startedPlan.Total} blocks): {startedPlan.Summary}",
                    startedPlan.Total, startedPlan.Summary ?? "")));
                await StreamProjectJobAsync(startedPlan, onEvent, ct);
                result.Status = ReActStatus.Completed;
                result.Message = "Project plan round completed";
                return result;
            }

            onEvent?.Invoke(AgentStreamEvent.Thinking("Starting Chat Plan (Python multi-agent supervisor)..."));
            onEvent?.Invoke(AgentStreamEvent.Thinking("THINKING: Generating execution plan..."));

            try
            {
                await _aiService.GenerateChatPlanStreamAsync(prompt, ev =>
                {
                    if (ev == null) return;
                    switch (ev.EventType)
                    {
                        case "content":
                        case "delta":
                            var text = ev.TextContent ?? "";
                            resultBuilder.Append(text);
                            onEvent?.Invoke(AgentStreamEvent.Content(text));
                            break;

                        case "notice":
                            if (ev.Notice != null)
                                onEvent?.Invoke(AgentStreamEvent.NoticeUpdate(ev.Notice));
                            break;

                        case "thinking":
                            var thinkingText = (ev.TextContent ?? ev.Thought?.Text ?? "").Trim();
                            if (string.IsNullOrEmpty(thinkingText)) break;
                            // Filter internal markers
                            if (thinkingText == "done" || thinkingText == "generating" ||
                                thinkingText == "json_fallback" || thinkingText == "json_parsed")
                                break;
                            // Tool call markers from Python Agent
                            if (thinkingText.StartsWith("🔧"))
                            {
                                int colon = thinkingText.IndexOf(':');
                                string name = colon >= 0 ? thinkingText.Substring(colon + 1).Trim() : thinkingText;
                                onEvent?.Invoke(AgentStreamEvent.ToolStart(name));
                            }
                            else if (thinkingText.StartsWith("✅"))
                            {
                                string body = thinkingText;
                                int idx1 = body.IndexOf("工具");
                                if (idx1 >= 0) body = body.Substring(idx1 + 2).Trim();
                                int idx2 = body.IndexOf("完成");
                                if (idx2 >= 0) body = body.Substring(0, idx2).Trim();
                                onEvent?.Invoke(AgentStreamEvent.ToolEnd(body));
                            }
                            else if (thinkingText.StartsWith("❌"))
                            {
                                string body = thinkingText;
                                int idx1 = body.IndexOf("工具");
                                if (idx1 >= 0) body = body.Substring(idx1 + 2).Trim();
                                int idx2 = body.IndexOf("失败");
                                if (idx2 >= 0) body = body.Substring(0, idx2).Trim();
                                onEvent?.Invoke(AgentStreamEvent.Thinking(thinkingText));
                            }
                            else
                            {
                                onEvent?.Invoke(AgentStreamEvent.Thinking(thinkingText));
                            }
                            break;

                        case SseEventTypes.ToolCall:
                            try
                            {
                                using var toolDoc = JsonDocument.Parse(ev.TextContent ?? "{}");
                                var root = toolDoc.RootElement;
                                if (root.TryGetProperty("name", out var tn))
                                {
                                    var toolName = tn.GetString();
                                    var status = root.TryGetProperty("status", out var st) ? st.GetString() : "";
                                    if (status == "started")
                                        onEvent?.Invoke(AgentStreamEvent.ToolStart(toolName ?? "tool"));
                                    else if (status == "completed")
                                        onEvent?.Invoke(AgentStreamEvent.ToolEnd(toolName ?? "tool"));
                                }
                            }
                            catch { /* best-effort */ }
                            break;

                        case "error":
                            errorBuilder.AppendLine(ev.ErrorMessage ?? ev.TextContent ?? "");
                            onEvent?.Invoke(AgentStreamEvent.Error(ev.ErrorMessage ?? ev.TextContent ?? ""));
                            break;

                        case SseEventTypes.TokenUsage:
                            try
                            {
                                using var tuDoc = JsonDocument.Parse(ev.TextContent ?? "{}");
                                var tuRoot = tuDoc.RootElement;
                                var tuData = new TokenUsageData
                                {
                                    PromptTokens = tuRoot.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
                                    ContextTokens = tuRoot.TryGetProperty("context_tokens", out var ct2) ? ct2.GetInt32() : 0,
                                    HistoryTokens = tuRoot.TryGetProperty("history_tokens", out var ht) ? ht.GetInt32() : 0,
                                    ToolOutputTokens = tuRoot.TryGetProperty("tool_output_tokens", out var tot) ? tot.GetInt32() : 0,
                                    TotalUsed = tuRoot.TryGetProperty("total_used", out var tu) ? tu.GetInt32() : 0,
                                    TotalBudget = tuRoot.TryGetProperty("total_budget", out var tb) ? tb.GetInt32() : 0,
                                    Remaining = tuRoot.TryGetProperty("remaining", out var rem) ? rem.GetInt32() : 0,
                                    UsagePercent = tuRoot.TryGetProperty("usage_percent", out var up) ? up.GetDouble() : 0,
                                    Status = tuRoot.TryGetProperty("status", out var st2) ? st2.GetString() : "Ok",
                                };
                                onEvent?.Invoke(AgentStreamEvent.TokenUsageUpdate(tuData));
                            }
                            catch { /* best-effort */ }
                            break;

                        case SseEventTypes.PlanProgress:
                            planProgress = ParsePlanProgress(ev.TextContent) ?? planProgress;
                            if (planProgress != null)
                                onEvent?.Invoke(AgentStreamEvent.Thinking(planProgress.Summary));
                            break;

                        case "delivery":
                        {
                            var deliveryBlock = ParseDelivery(ev.TextContent);
                            if (deliveryBlock != null)
                                onEvent?.Invoke(AgentStreamEvent.DeliveryUpdate(deliveryBlock));
                            break;
                        }

                        case SseEventTypes.Milestone:
                            try
                            {
                                using var mDoc = JsonDocument.Parse(ev.TextContent ?? "{}");
                                var mRoot = mDoc.RootElement;
                                string planId = mRoot.TryGetProperty("plan_id", out var p) ? p.GetString() : "";
                                string phaseId = mRoot.TryGetProperty("phase_id", out var ph) ? ph.GetString() : "";
                                string name = mRoot.TryGetProperty("name", out var n) ? n.GetString() : "";
                                onEvent?.Invoke(AgentStreamEvent.Milestone(planId, phaseId, name));
                            }
                            catch { /* best-effort — ignore malformed milestone JSON */ }
                            break;

                        case "done":
                            if (errorBuilder.Length > 0)
                                tcs.TrySetResult(errorBuilder.ToString().Trim());
                            else
                                tcs.TrySetResult(resultBuilder.ToString());
                            break;
                    }
                }, context, history, ct, intent: intent, planningKnowledge: planningKnowledge, projectKey: projectKey);

                var finalText = await tcs.Task;
                result.Status = ReActStatus.Completed;
                result.FinalAnswer = finalText;
                result.Message = "Chat Plan Agent completed";
                result.PlanCompletion = planProgress;
            }
            catch (OperationCanceledException)
            {
                result.Status = ReActStatus.Cancelled;
                result.Message = "Cancelled by user";
                result.FinalAnswer = resultBuilder.Length > 0 ? resultBuilder.ToString() : null;
            }
            catch (Exception ex)
            {
                result.Status = ReActStatus.Error;
                result.Message = ex.Message;
                result.FinalAnswer = resultBuilder.Length > 0 ? resultBuilder.ToString() : null;
            }

            return result;
        }

        /// <summary>Follow a background job's live events (plan progress / milestone / thinking / content), converted to UI events.</summary>
        private async Task StreamProjectJobAsync(PlanStartResult started, Action<AgentStreamEvent> onEvent, CancellationToken ct)
        {
            await _aiService.StreamPlanAsync(started.PlanId, ev =>
            {
                if (ev == null) return;
                switch (ev.EventType)
                {
                    case "plan_status":
                    case "plan_progress":
                        var progressData = ParsePlanProgressData(ev.TextContent);
                        if (progressData != null)
                            onEvent?.Invoke(AgentStreamEvent.PlanProgressUpdate(progressData));
                        break;

                    case "milestone":
                        try
                        {
                            using var mDoc = JsonDocument.Parse(ev.TextContent ?? "{}");
                            var mRoot = mDoc.RootElement;
                            string planId = mRoot.TryGetProperty("plan_id", out var p) ? p.GetString() : started.PlanId;
                            string phaseId = mRoot.TryGetProperty("phase_id", out var ph) ? ph.GetString() : "";
                            string name = mRoot.TryGetProperty("name", out var n) ? n.GetString() : "";
                            onEvent?.Invoke(AgentStreamEvent.Milestone(planId, phaseId, name));
                        }
                        catch { /* ignore malformed milestone */ }
                        break;

                    case "task_failed":
                        try
                        {
                            using var fDoc = JsonDocument.Parse(ev.TextContent ?? "{}");
                            var fRoot = fDoc.RootElement;
                            onEvent?.Invoke(AgentStreamEvent.TaskFailedUpdate(new TaskFailedData
                            {
                                PlanId = fRoot.TryGetProperty("plan_id", out var fp) ? fp.GetString() : started.PlanId,
                                TaskId = fRoot.TryGetProperty("task_id", out var ft) ? ft.GetString() : "",
                                BlockName = fRoot.TryGetProperty("block_name", out var fb) ? fb.GetString() : "",
                                Error = fRoot.TryGetProperty("error", out var fe) ? fe.GetString() : "",
                                ManualAction = fRoot.TryGetProperty("manual_action", out var fma) && fma.ValueKind == JsonValueKind.Object
                                    ? new ManualActionData
                                    {
                                        Code = fma.TryGetProperty("code", out var fmac) ? fmac.GetString() : "",
                                        Text = fma.TryGetProperty("text", out var fmat) ? fmat.GetString() : "",
                                    }
                                    : null,
                            }));
                        }
                        catch { /* ignore malformed task_failed */ }
                        break;

                    case "notice":
                        try
                        {
                            using var nDoc = JsonDocument.Parse(ev.TextContent ?? "{}");
                            var nRoot = nDoc.RootElement;
                            var notice = new NoticeData
                            {
                                Code = nRoot.TryGetProperty("code", out var nc) ? nc.GetString() : "",
                                Text = nRoot.TryGetProperty("text", out var nt) ? nt.GetString() : "",
                            };
                            if (nRoot.TryGetProperty("params", out var np) && np.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in np.EnumerateArray())
                                    notice.Params.Add(item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString());
                            }
                            onEvent?.Invoke(AgentStreamEvent.NoticeUpdate(notice));
                        }
                        catch { /* ignore malformed notice */ }
                        break;

                    case "thinking":
                        var t = (ev.TextContent ?? "").Trim();
                        if (!string.IsNullOrEmpty(t))
                            onEvent?.Invoke(AgentStreamEvent.Thinking(t));
                        break;

                    case "content":
                    case "delta":
                        onEvent?.Invoke(AgentStreamEvent.Content(ev.TextContent ?? ""));
                        break;

                    case "error":
                        onEvent?.Invoke(AgentStreamEvent.Error(ev.TextContent ?? ev.ErrorMessage ?? ""));
                        break;
                }
            }, ct);
        }

        /// <summary>Parse a job's progress JSON (plan_status / plan_progress; fields total/done/failed/current) into structured progress.</summary>
        private static PlanProgressData ParsePlanProgressData(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new PlanProgressData
                {
                    Total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0,
                    Done = root.TryGetProperty("done", out var d) && d.TryGetInt32(out var dv) ? dv : 0,
                    Failed = root.TryGetProperty("failed", out var f) && f.TryGetInt32(out var fv) ? fv : 0,
                    Current = root.TryGetProperty("current", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "",
                };
            }
            catch { return null; }
        }
    }
}