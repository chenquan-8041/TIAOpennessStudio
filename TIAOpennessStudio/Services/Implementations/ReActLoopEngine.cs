using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using TokenBudgetSvc = TIAOpennessStudio.Services.TokenBudget;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// ReAct (Reasoning + Acting) engine.
    /// Replaces static plan execution with a dynamic loop where the LLM decides
    /// what to do at each step based on observations from previous tool calls.
    ///
    /// Flow: Reason → Act → Observe → Reason → ... → Finish
    ///
    /// Tools are managed via the shared ToolRegistry (IAgentTool implementations).
    /// </summary>
    public class ReActLoopEngine : IReActLoopEngine
    {
        private readonly IAIEngineService _aiService;
        private readonly ITiaToolService _tiaToolService;
        private readonly ITiaOpennessService _tiaService;
        private readonly IUIService _uiService;
        private readonly ILocalLlmService _localLlmService;
        private readonly ToolRegistry _toolRegistry;
        private readonly ISemanticCompressor _compressor;
        private readonly TokenBudgetSvc _tokenBudget;
        private readonly AgentConfig _agentConfig;
        // G-3: optional Skills provider. When wired, the loop injects the most
        // relevant skill descriptions into the prompt on demand. Null → no-op.
        private readonly ISkillManager _skillManager;
        private readonly int _maxIterations;
        private readonly int _maxTokens;

        // Default context window for local Ollama models when the model's real
        // window is unknown. qwen3.5:9b advertises ~256K. Override via
        // TIA_OLLAMA_CONTEXT_WINDOW env var (in tokens).
        private static readonly int OllamaContextWindow = ReadOllamaContextWindow();

        private static int ReadOllamaContextWindow()
        {
            var env = Environment.GetEnvironmentVariable("TIA_OLLAMA_CONTEXT_WINDOW");
            return int.TryParse(env, out var v) && v > 0 ? v : 262144; // 256K
        }

        public ReActLoopEngine(
            IAIEngineService aiService,
            ITiaToolService tiaToolService,
            ITiaOpennessService tiaService,
            IUIService uiService,
            ToolRegistry toolRegistry,
            ISemanticCompressor compressor = null,
            TokenBudgetSvc tokenBudget = null,
            ILocalLlmService localLlmService = null,
            AgentConfig agentConfig = null,
            int maxIterations = 20,
            int maxTokens = 32000,
            ISkillManager skillManager = null)
        {
            _aiService = aiService;
            _tiaToolService = tiaToolService;
            _tiaService = tiaService;
            _uiService = uiService;
            _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
            _compressor = compressor;
            _localLlmService = localLlmService;
            _skillManager = skillManager;
            _agentConfig = agentConfig ?? AgentConfig.CreateDefault();
            _maxIterations = maxIterations;
            _maxTokens = maxTokens;
            // NOTE: assign _agentConfig BEFORE building the budget — the previous order
            // read _agentConfig.MaxTokens while it was still null, so the large configured
            // budget never applied and it silently fell back to the 6000 default.
            _tokenBudget = tokenBudget ?? new TokenBudgetSvc(_agentConfig.MaxTokens);
        }

        /// <summary>
        /// Current model name from AI service (e.g. "gpt-4o", "qwen2.5-coder:7b").
        /// </summary>
        public string ModelName => _aiService?.ModelName ?? "";

        /// <summary>
        /// Current provider from AI service (e.g. "ollama", "openai", "deepseek").
        /// </summary>
        public string ModelProvider => _aiService?.ModelProvider ?? "";

        /// <summary>
        /// Execute a ReAct loop for the given user prompt.
        /// </summary>
        public async Task<ReActResult> ExecuteAsync(
            string prompt,
            string context = "",
            List<Dictionary<string, string>> history = null,
            Action<AgentStreamEvent> onEvent = null,
            Func<string, string, Task<bool>> onConfirmRequired = null,
            CancellationToken ct = default,
            ExecutionPlan plan = null)
        {
            var result = new ReActResult();
            var conversationHistory = new List<Dictionary<string, string>>(history ?? new List<Dictionary<string, string>>());
            var toolCallTrace = new List<ToolCallRecord>();

            // Plan tracking is a pure observer: it matches each tool call to a plan step
            // and produces a completion report (skipped/failed/deviations) without ever
            // blocking or altering execution. A fresh tracker per run keeps the singleton
            // engine free of cross-request state.
            IPlanTracker planTracker = null;
            if (_agentConfig.EnablePlanTracking && plan != null && plan.Steps != null && plan.Steps.Count > 0)
            {
                planTracker = new PlanTracker();
                planTracker.Initialize(plan);
            }

            // Add user prompt to history
            conversationHistory.Add(new Dictionary<string, string>
            {
                ["role"] = "user",
                ["content"] = prompt
            });

            onEvent?.Invoke(AgentStreamEvent.Thinking("Starting ReAct loop..."));

            // ── Cumulative Ollama token usage across ReAct iterations ──
            // Ollama returns exact per-call counts (prompt_eval_count / eval_count).
            // The context window is per-call, but the running total shows how much
            // the multi-turn ReAct loop has consumed overall.
            bool sawOllamaUsage = false;
            int cumPromptTokens = 0;
            int cumCompletionTokens = 0;

            // Ollama context budget: a per-run TokenBudget sized to the model's context
            // window (256K default). It drives budget-aware prompt trimming (context
            // head+tail truncation, tool-history retention, history sliding window) so a
            // large window is used fully but never blown. Cloud path keeps budget=null
            // (Python owns its own context management) — unchanged behavior.
            bool isOllama = ModelProvider == "ollama";
            TokenBudgetSvc ollamaBudget = isOllama ? new TokenBudgetSvc(OllamaContextWindow) : null;

            for (int iteration = 0; iteration < _maxIterations; iteration++)
            {
                if (ct.IsCancellationRequested)
                {
                    result.Status = ReActStatus.Cancelled;
                    result.Message = "Cancelled by user";
                    break;
                }

                onEvent?.Invoke(AgentStreamEvent.Thinking($"Iteration {iteration + 1}/{_maxIterations}"));

                // Build the prompt for this iteration (budget-aware for Ollama).
                string reactPrompt = BuildReActPrompt(prompt, context, conversationHistory, toolCallTrace, budget: ollamaBudget);

                // Call LLM - emit "thinking" status before LLM call
                onEvent?.Invoke(AgentStreamEvent.Thinking("正在思考下一步操作..."));
                // Track what this call already streamed to the UI. Streaming call paths forward
                // every content chunk (including the whole FINAL_ANSWER text); emitting the
                // parsed final answer again below would render it twice.
                var streamedThisCall = new StringBuilder();
                Action<AgentStreamEvent> emitForCall = ev =>
                {
                    if (ev != null && ev.Kind == AgentStreamKind.Content && !string.IsNullOrEmpty(ev.Text))
                        streamedThisCall.Append(ev.Text);
                    onEvent?.Invoke(ev);
                };
                var llmResponse = await CallLlmAsync(reactPrompt, context, conversationHistory, emitForCall, ct,
                    onUsage: usage =>
                    {
                        if (usage == null) return;
                        sawOllamaUsage = true;
                        cumPromptTokens += usage.PromptTokens;
                        cumCompletionTokens += usage.CompletionTokens;

                        // The context window applies to a single call: current
                        // occupancy ≈ this call's prompt + its generated output.
                        int currentContext = usage.PromptTokens + usage.CompletionTokens;
                        int remaining = Math.Max(0, OllamaContextWindow - currentContext);
                        double pct = OllamaContextWindow > 0
                            ? (double)currentContext / OllamaContextWindow * 100
                            : 0;
                        string status = pct >= 95 ? "Exceeded" : pct >= 80 ? "Warning" : "Ok";

                        onEvent?.Invoke(AgentStreamEvent.TokenUsageUpdate(new TokenUsageData
                        {
                            PromptTokens = usage.PromptTokens,
                            // Report cumulative generated tokens as the "tool/output" bucket
                            // so the UI shows both the live window and the running spend.
                            ToolOutputTokens = cumCompletionTokens,
                            TotalUsed = currentContext,
                            TotalBudget = OllamaContextWindow,
                            Remaining = remaining,
                            UsagePercent = pct,
                            Status = status,
                        }));
                        AppLogger.Log(
                            $"[Ollama][Tokens] prompt={usage.PromptTokens} completion={usage.CompletionTokens} " +
                            $"ctx={currentContext}/{OllamaContextWindow} ({pct:F1}%) " +
                            $"cumPrompt={cumPromptTokens} cumCompletion={cumCompletionTokens}");
                    });

                if (llmResponse == null)
                {
                    result.Status = ReActStatus.Error;
                    result.Message = "LLM returned null response";
                    break;
                }

                // Parse LLM response
                var parsed = ParseLlmResponse(llmResponse);

                if (parsed.IsFinalAnswer)
                {
                    // LLM decided the task is complete
                    result.Status = ReActStatus.Completed;
                    result.FinalAnswer = parsed.FinalAnswer;
                    result.Message = "Task completed";
                    // Only emit when the answer has not already been streamed chunk-by-chunk
                    // (blocking call paths emit it here; streaming paths already did).
                    if (!string.IsNullOrEmpty(parsed.FinalAnswer) &&
                        !streamedThisCall.ToString().Contains(parsed.FinalAnswer))
                    {
                        onEvent?.Invoke(AgentStreamEvent.Content(parsed.FinalAnswer));
                    }
                    break;
                }

                // Display thinking text if present (from THINKING: format)
                if (!string.IsNullOrWhiteSpace(parsed.Text))
                {
                    onEvent?.Invoke(AgentStreamEvent.Thinking(parsed.Text));
                }

                if (parsed.ToolCall != null)
                {
                    // LLM wants to call a tool
                    onEvent?.Invoke(AgentStreamEvent.ToolStart(parsed.ToolCall.ToolName));

                    var toolResult = await ExecuteToolAsync(parsed.ToolCall, onConfirmRequired, ct);

                    toolCallTrace.Add(new ToolCallRecord
                    {
                        Iteration = iteration,
                        ToolName = parsed.ToolCall.ToolName,
                        Arguments = parsed.ToolCall.Arguments,
                        Result = toolResult.Success ? toolResult.Output : toolResult.Error,
                        Success = toolResult.Success,
                        Duration = toolResult.Duration
                    });

                    // Dead-loop guard: the model kept retrying the exact same action.
                    if (IsDeadToolLoop(toolCallTrace))
                    {
                        result.Status = ReActStatus.Error;
                        result.Message = "Dead loop detected: repeated identical tool call";
                        break;
                    }

                    // Observe the tool call against the plan (best-effort, never throws).
                    if (planTracker != null)
                    {
                        try
                        {
                            var match = planTracker.OnToolCalled(parsed.ToolCall.ToolName, parsed.ToolCall.Arguments, toolResult.Success);
                            if (match != null && match.IsNewMatch)
                            {
                                onEvent?.Invoke(AgentStreamEvent.Thinking(
                                    $"📌 Step {match.StepIndex}/{match.TotalSteps}: {match.Description}"));
                            }
                        }
                        catch (Exception trackEx)
                        {
                            AppLogger.Log($"[PlanTracker] OnToolCalled failed: {trackEx.Message}");
                        }
                    }

                    if (toolResult.Success)
                    {
                        onEvent?.Invoke(AgentStreamEvent.ToolEnd(parsed.ToolCall.ToolName));
                    }
                    else
                    {
                        onEvent?.Invoke(AgentStreamEvent.Error($"Tool {parsed.ToolCall.ToolName} failed: {toolResult.Error}"));
                    }

                    // Add tool result to conversation history
                    conversationHistory.Add(new Dictionary<string, string>
                    {
                        ["role"] = "assistant",
                        ["content"] = $"I need to call tool: {parsed.ToolCall.ToolName}"
                    });
                    // Phase 6: compress tool output before injecting into conversation history
                    string outputForHistory;
                    if (toolResult.Success && _compressor != null)
                    {
                        var config = CompressConfig.ForCloudModel(prompt);
                        if (_agentConfig.EnableObservationCompression || true) // always compress for tool results
                        {
                            var compressRequest = new CompressRequest
                            {
                                Content = toolResult.Output ?? "",
                                ToolName = parsed.ToolCall.ToolName,
                                Config = config
                            };
                            outputForHistory = await _compressor.CompressAsync(compressRequest, ct);
                        }
                        else
                        {
                            outputForHistory = toolResult.Output ?? "";
                        }
                    }
                    else
                    {
                        outputForHistory = toolResult.Success
                            ? (toolResult.Output ?? "")
                            : "Error: " + toolResult.Error;
                    }

                    conversationHistory.Add(new Dictionary<string, string>
                    {
                        ["role"] = "user",
                        ["content"] = $"Tool result: {outputForHistory}"
                    });
                }
                else if (!string.IsNullOrWhiteSpace(parsed.Text))
                {
                    // LLM response is thinking/intermediate response without tool call
                    conversationHistory.Add(new Dictionary<string, string>
                    {
                        ["role"] = "assistant",
                        ["content"] = parsed.Text
                    });
                }
            }

            if (result.Status == ReActStatus.Running)
            {
                result.Status = ReActStatus.MaxIterationsReached;
                result.Message = $"Reached maximum iterations ({_maxIterations})";
            }

            // Final cumulative token summary for local Ollama runs.
            if (sawOllamaUsage)
            {
                AppLogger.Log(
                    $"[Ollama][Tokens] Run total: prompt={cumPromptTokens} completion={cumCompletionTokens} " +
                    $"grand_total={cumPromptTokens + cumCompletionTokens} tokens over {toolCallTrace.Count} tool call(s)");
            }

            result.ToolCalls = toolCallTrace;
            result.TotalIterations = toolCallTrace.Count;

            if (planTracker != null)
            {
                try
                {
                    result.PlanCompletion = planTracker.GetCompletionReport();
                    if (result.PlanCompletion != null)
                        onEvent?.Invoke(AgentStreamEvent.Thinking(result.PlanCompletion.Summary));
                }
                catch (Exception reportEx)
                {
                    AppLogger.Log($"[PlanTracker] GetCompletionReport failed: {reportEx.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Build the prompt for the LLM with ReAct formatting.
        /// </summary>
        /// <summary>
        /// G-3: inject the most relevant skill descriptions for this task into the prompt.
        /// Best-effort — a SkillManager failure never breaks prompt building.
        /// </summary>
        private void AppendRelevantSkills(StringBuilder sb, string userPrompt)
        {
            if (_skillManager == null || string.IsNullOrWhiteSpace(userPrompt)) return;
            try
            {
                var relevant = _skillManager.GetRelevantSkills(userPrompt, 3);
                if (relevant == null || relevant.Count == 0) return;
                var injection = _skillManager.BuildSkillInjection(relevant);
                if (string.IsNullOrWhiteSpace(injection)) return;
                sb.AppendLine(injection);
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ReAct][Skills] skill injection skipped: {ex.Message}");
            }
        }

        private string BuildReActPrompt(string userPrompt, string context, List<Dictionary<string, string>> history, List<ToolCallRecord> toolCalls, bool aggressive = false, TokenBudgetSvc budget = null)
        {
            var sb = new StringBuilder();

            // System instructions
            sb.AppendLine("You are an expert TIA Portal and PLC programming assistant. You follow the ReAct (Reasoning and Acting) framework.");
            sb.AppendLine("You will receive a user task. You must use the available tools to explore the project, generate code, compile, and fix errors until the task is complete.");
            sb.AppendLine();
            sb.AppendLine("## Safety Rules");
            sb.AppendLine("1. NEVER delete or overwrite an existing block without first understanding its current content.");
            sb.AppendLine("2. When modifying an existing block, ALWAYS export it first (export_block) before writing changes.");
            sb.AppendLine("3. Do not claim a write succeeded until the import/compile tool has actually reported success.");
            sb.AppendLine();
            sb.AppendLine("## Coding Standards Rule");
            sb.AppendLine("When the user mentions coding standards / conventions / naming rules / a specific standard (e.g. VASS), OR before you generate or modify a block, you MUST first call search_coding_standard to retrieve the applicable constraints, then follow them. If it returns 'No coding-standard library mounted', proceed with general best practice.");
            sb.AppendLine();
            sb.AppendLine("You have access to the following tools:");
            sb.AppendLine(_toolRegistry.GenerateToolCatalog());
            sb.AppendLine();

            // G-3: on-demand Skills injection. When a SkillManager is wired and skills
            // match the task, inject only the most relevant few (advisory guidance),
            // instead of dumping the whole skill catalog. No-op when unwired/no match.
            AppendRelevantSkills(sb, userPrompt);

            sb.AppendLine("To use a tool, you MUST use the exact following format:");
            sb.AppendLine("TOOL_CALL: <tool_name>");
            sb.AppendLine("ARGUMENTS: {\"param1\": \"value1\", \"param2\": \"value2\"}");
            sb.AppendLine();
            sb.AppendLine("WRITE-BACK RULES:");
            sb.AppendLine("1. Prefer structured JSON spec + plc_build_and_import for write actions.");
            sb.AppendLine("2. Use export_block first when modifying an existing block.");
            sb.AppendLine("3. Use resolve_xml_fragment when you need precise XML around a compileUnitId or uid.");
            sb.AppendLine("4. Use import_block_code only when you already have a complete SCL/XML payload.");
            sb.AppendLine("5. Do not claim a write succeeded until import_block_code or plc_build_and_import has succeeded.");
            sb.AppendLine("6. For a write task, call compile after import when practical.");
            sb.AppendLine();
            sb.AppendLine("JSON SPEC HINTS:");
            sb.AppendLine("- plc_build_fc / plc_build_and_import(kind=fc): json may be an object with blockName, blockNumber, inputs, outputs, structuredText.");
            sb.AppendLine("- plc_build_fb / plc_build_and_import(kind=fb): json may be an object with blockName, blockNumber, inputs, outputs, inouts, statics, temps, structuredText.");
            sb.AppendLine("- plc_build_lad_fc / plc_build_and_import(kind=ladfc): json may be an object with blockName, blockNumber, inputs, outputs, networks.");
            sb.AppendLine("- The json parameter may be a nested JSON object, not only an escaped string.");
            sb.AppendLine();
            sb.AppendLine("OPC UA REAL-TIME RULES (live values from a running PLC):");
            sb.AppendLine("- The opcua_* tools read LIVE runtime values from the PLC over OPC UA. This is different from export_block, which reads the STATIC project/offline definition.");
            sb.AppendLine("- When the user asks for the CURRENT / real-time / online value of a variable or DB (e.g. 'what is X right now', 'read DB10 live'), use the opcua_* tools, NOT export/index tools.");
            sb.AppendLine("- Always call opcua_status first; if not connected, tell the user to connect in the OPC UA panel (opc.tcp://<ip>:4840) instead of guessing values.");
            sb.AppendLine("- Use opcua_browse to discover NodeIds (DBs live under DeviceSet->PLC->DataBlocksGlobal; system vars under the Server node).");
            sb.AppendLine("- Use opcua_read_node for specific NodeIds, or opcua_read_db to read all members of a DB/container at once.");
            sb.AppendLine("- These tools are read-only; never claim to have changed a live PLC value with them.");
            sb.AppendLine("- opcua_write_node is HIGH-RISK (changes live PLC state). Only call it when the user EXPLICITLY asks to set/change a live value. It requires write enabled in the panel AND confirm=true in the arguments AND a user confirmation prompt; if any is missing it will refuse. Never write speculatively.");
            sb.AppendLine();
            sb.AppendLine("## Few-shot Tool Usage Examples");
            sb.AppendLine();
            sb.AppendLine("Example 1 - Create a new FC with math/comparison logic:");
            sb.AppendLine("Task: Create FC150 FC_MathCompareDemo with Setpoint, Actual, Deadband inputs and OutputValue, InTolerance outputs");
            sb.AppendLine("TOOL_CALL: plc_build_and_import");
            sb.AppendLine("ARGUMENTS: {\"kind\": \"fc\", \"json\": {\"blockName\": \"FC_MathCompareDemo\", \"blockNumber\": 150, \"inputs\": [{\"name\": \"Setpoint\", \"datatype\": \"Real\"}, {\"name\": \"Actual\", \"datatype\": \"Real\"}, {\"name\": \"Deadband\", \"datatype\": \"Real\"}], \"outputs\": [{\"name\": \"OutputValue\", \"datatype\": \"Real\"}, {\"name\": \"InTolerance\", \"datatype\": \"Bool\"}], \"structuredText\": {\"operations\": [{\"op\": \"assignment\", \"target\": \"ErrorValue\", \"source\": \"Setpoint - Actual\"}, {\"op\": \"assignment\", \"target\": \"AbsError\", \"source\": \"ABS(ErrorValue)\"}, {\"op\": \"assignment\", \"target\": \"OutputValue\", \"source\": \"LIMIT(MN := -1000, IN := ErrorValue, MX := 1000)\"}, {\"op\": \"assignment\", \"target\": \"InTolerance\", \"source\": \"AbsError <= Deadband\"}]}}}");
            sb.AppendLine("Tool result: Success - FC150 created");
            sb.AppendLine("FINAL_ANSWER: Created FC150 FC_MathCompareDemo with Setpoint-Actual comparison, absolute error calculation, output limiting, and tolerance checking.");
            sb.AppendLine();
            sb.AppendLine("Example 2 - Create a new FB with state latch logic:");
            sb.AppendLine("Task: Create FB110 FB_BasicLatch with Enable, Disable, FaultInput, Reset inputs and Active, Fault outputs");
            sb.AppendLine("TOOL_CALL: plc_build_and_import");
            sb.AppendLine("ARGUMENTS: {\"kind\": \"fb\", \"json\": {\"blockName\": \"FB_BasicLatch\", \"blockNumber\": 110, \"inputs\": [{\"name\": \"Enable\", \"datatype\": \"Bool\"}, {\"name\": \"Disable\", \"datatype\": \"Bool\"}, {\"name\": \"FaultInput\", \"datatype\": \"Bool\"}, {\"name\": \"Reset\", \"datatype\": \"Bool\"}], \"outputs\": [{\"name\": \"Active\", \"datatype\": \"Bool\"}, {\"name\": \"Fault\", \"datatype\": \"Bool\"}], \"statics\": [{\"name\": \"ActiveLatch\", \"datatype\": \"Bool\"}, {\"name\": \"FaultLatch\", \"datatype\": \"Bool\"}], \"structuredText\": {\"operations\": [{\"op\": \"if\", \"condition\": \"FaultInput\"}, {\"op\": \"assignment\", \"target\": \"FaultLatch\", \"literalValue\": \"TRUE\", \"indent\": 1}, {\"op\": \"assignment\", \"target\": \"ActiveLatch\", \"literalValue\": \"FALSE\", \"indent\": 1}, {\"op\": \"elsif\", \"condition\": \"Reset\"}, {\"op\": \"assignment\", \"target\": \"FaultLatch\", \"literalValue\": \"FALSE\", \"indent\": 1}, {\"op\": \"endif\"}, {\"op\": \"if\", \"condition\": \"Disable OR FaultLatch\"}, {\"op\": \"assignment\", \"target\": \"ActiveLatch\", \"literalValue\": \"FALSE\", \"indent\": 1}, {\"op\": \"elsif\", \"condition\": \"Enable AND NOT FaultLatch\"}, {\"op\": \"assignment\", \"target\": \"ActiveLatch\", \"literalValue\": \"TRUE\", \"indent\": 1}, {\"op\": \"endif\"}, {\"op\": \"assignment\", \"target\": \"Active\", \"source\": \"ActiveLatch\"}, {\"op\": \"assignment\", \"target\": \"Fault\", \"source\": \"FaultLatch\"}]}}}");
            sb.AppendLine("Tool result: Success - FB110 created");
            sb.AppendLine("FINAL_ANSWER: Created FB110 FB_BasicLatch with Enable/Disable/Fault/Reset logic. The FB maintains Active and Fault states using internal latch variables.");
            sb.AppendLine();
            sb.AppendLine("Example 3 - Create a LAD FC with networks:");
            sb.AppendLine("Task: Create LAD FC160 FC_BasicScaleLimit with Real inputs/outputs using LIMIT instruction");
            sb.AppendLine("TOOL_CALL: plc_build_and_import");
            sb.AppendLine("ARGUMENTS: {\"kind\": \"ladfc\", \"json\": {\"blockName\": \"FC_BasicScaleLimit\", \"blockNumber\": 160, \"inputs\": [{\"name\": \"RawValue\", \"datatype\": \"Real\"}, {\"name\": \"MinScale\", \"datatype\": \"Real\"}, {\"name\": \"MaxScale\", \"datatype\": \"Real\"}], \"outputs\": [{\"name\": \"ScaledValue\", \"datatype\": \"Real\"}], \"networks\": [{\"networkId\": 1, \"title\": \"Limit output to range\", \"elements\": [{\"type\": \"call\", \"name\": \"LIMIT\", \"inputs\": [{\"name\": \"MN\", \"connection\": \"MinScale\"}, {\"name\": \"IN\", \"connection\": \"RawValue\"}, {\"name\": \"MX\", \"connection\": \"MaxScale\"}], \"outputAssignments\": [{\"name\": \"\", \"connection\": \"ScaledValue\"}]}]}]}}");
            sb.AppendLine("Tool result: Success - LAD FC160 created");
            sb.AppendLine("FINAL_ANSWER: Created LAD FC160 FC_BasicScaleLimit using LIMIT instruction to constrain RawValue within MinScale-MaxScale range.");
            sb.AppendLine();
            sb.AppendLine("Example 4 - Modify an existing block (read first, then write):");
            sb.AppendLine("Task: Add a new output parameter StatusText to existing FC150");
            sb.AppendLine("TOOL_CALL: export_block");
            sb.AppendLine("ARGUMENTS: {\"block_name\": \"FC_MathCompareDemo\"}");
            sb.AppendLine("Tool result: (returns current XML)");
            sb.AppendLine("TOOL_CALL: resolve_xml_fragment");
            sb.AppendLine("ARGUMENTS: {\"compile_unit_id\": \"...\", \"uid\": \"...\"}");
            sb.AppendLine("Tool result: (returns XML snippet for the section being modified)");
            sb.AppendLine("TOOL_CALL: plc_build_and_import");
            sb.AppendLine("ARGUMENTS: {\"kind\": \"fc\", \"json\": {\"blockName\": \"FC_MathCompareDemo\", \"blockNumber\": 150, \"inputs\": [...existing...], \"outputs\": [...existing..., {\"name\": \"StatusText\", \"datatype\": \"String\"}], \"structuredText\": {\"operations\": [...existing..., {\"op\": \"assignment\", \"target\": \"StatusText\", \"source\": \"IF InTolerance THEN 'OK' ELSE 'Error' END_IF\"}]}}}");
            sb.AppendLine("Tool result: Success - FC150 updated with StatusText output");
            sb.AppendLine("FINAL_ANSWER: Added StatusText String output to FC150. StatusText returns 'OK' when InTolerance is true, 'Error' otherwise.");
            sb.AppendLine();
            sb.AppendLine("Example 5 - Create UDT and global DB:");
            sb.AppendLine("Task: Create UDT_BasicStatus and DB100 DB_BasicStatus");
            sb.AppendLine("TOOL_CALL: plc_build_and_import");
            sb.AppendLine("ARGUMENTS: {\"kind\": \"udt\", \"json\": {\"name\": \"UDT_BasicStatus\", \"members\": [{\"name\": \"Active\", \"datatype\": \"Bool\"}, {\"name\": \"Ready\", \"datatype\": \"Bool\"}, {\"name\": \"Error\", \"datatype\": \"Bool\"}, {\"name\": \"State\", \"datatype\": \"Int\"}, {\"name\": \"Setpoint\", \"datatype\": \"Real\"}, {\"name\": \"Actual\", \"datatype\": \"Real\"}]}}");
            sb.AppendLine("Tool result: Success - UDT_BasicStatus created");
            sb.AppendLine("TOOL_CALL: plc_build_and_import");
            sb.AppendLine("ARGUMENTS: {\"kind\": \"globaldb\", \"json\": {\"dbName\": \"DB_BasicStatus\", \"dbNumber\": 100, \"staticMembers\": [{\"name\": \"Status\", \"datatype\": \"\\\"UDT_BasicStatus\\\"\"}, {\"name\": \"EnableRequest\", \"datatype\": \"Bool\"}, {\"name\": \"CycleCounter\", \"datatype\": \"DInt\", \"startValue\": \"0\"}]}}");
            sb.AppendLine("Tool result: Success - DB100 created");
            sb.AppendLine("FINAL_ANSWER: Created UDT_BasicStatus (with Active, Ready, Error, State, Setpoint, Actual members) and DB100 DB_BasicStatus (using UDT_BasicStatus plus EnableRequest and CycleCounter).");
            sb.AppendLine();
            sb.AppendLine("When you have fully completed the user's task, or if you cannot proceed further, you MUST respond with:");
            sb.AppendLine("FINAL_ANSWER: <Your final explanation to the user>");
            sb.AppendLine();
            sb.AppendLine("## MANDATORY THINKING FORMAT");
            sb.AppendLine("Before each TOOL_CALL, you MUST output your reasoning in this exact format:");
            sb.AppendLine("THINKING: <your step-by-step reasoning>");
            sb.AppendLine("TOOL_CALL: <tool_name>");
            sb.AppendLine("ARGUMENTS: {...}");
            sb.AppendLine();
            sb.AppendLine("This is REQUIRED for every tool call. Do not skip the THINKING step.");
            sb.AppendLine();

            // Project context — budget-driven head+tail truncation when a budget is
            // supplied (Ollama path), so a large project context can't blow the window.
            if (!string.IsNullOrEmpty(context))
            {
                var ctxText = context;
                if (budget != null)
                {
                    var t = budget.TruncateHeadTail(context, budget.MaxContextTokens, headRatio: 0.7);
                    ctxText = t.Content;
                    if (t.Truncated)
                        AppLogger.Log($"[ReAct][CtxBudget] Project context {t.Warning}");
                }
                sb.AppendLine("## Project Context");
                sb.AppendLine(ctxText);
                sb.AppendLine();
            }

            // Tool call history — bound prompt size by retaining recent records.
            // With a budget (Ollama), retention count and per-result truncation are
            // derived from MaxToolOutputTokens so a big window keeps more history and a
            // small one keeps less — instead of the old fixed keep=3/5 + 300-char cut.
            if (toolCalls.Count > 0)
            {
                int keep;
                int perResultChars;
                if (budget != null)
                {
                    // Split the tool-output sub-budget across retained calls. Aim to keep
                    // more calls when the window is large; each result gets a fair share.
                    int toolBudgetChars = budget.MaxToolOutputTokens * TokenBudgetSvc.EstimatedCharsPerToken;
                    // Target ~800 chars per retained result as a baseline, scale keep by budget.
                    keep = Math.Max(aggressive ? 3 : 5, Math.Min(toolCalls.Count, toolBudgetChars / 800));
                    perResultChars = Math.Max(300, toolBudgetChars / Math.Max(1, keep));
                }
                else
                {
                    keep = aggressive ? 3 : 5;
                    perResultChars = 300;
                }
                int omitted = Math.Max(0, toolCalls.Count - keep);

                sb.AppendLine("## Previous Tool Calls");
                if (omitted > 0)
                {
                    sb.AppendLine($"({omitted} earlier tool calls omitted)");
                }

                foreach (var call in toolCalls.Skip(omitted))
                {
                    var resultText = call.Result ?? "";
                    // Prefer semantic compression; fall back to budget-driven head+tail truncation.
                    if (_compressor != null && resultText.Length > perResultChars)
                    {
                        try
                        {
                            var compressRequest = new CompressRequest
                            {
                                Content = resultText,
                                ToolName = call.ToolName,
                                Config = CompressConfig.ForCloudModel()
                            };
                            resultText = _compressor.CompressAsync(compressRequest, CancellationToken.None)
                                .GetAwaiter().GetResult();
                        }
                        catch
                        {
                            resultText = TruncateResultText(resultText, perResultChars, budget);
                        }
                    }
                    else if (resultText.Length > perResultChars)
                    {
                        resultText = TruncateResultText(resultText, perResultChars, budget);
                    }

                    sb.AppendLine($"Iteration {call.Iteration}:");
                    sb.AppendLine($"Action: {call.ToolName}");
                    sb.AppendLine($"Action Input: {call.Arguments}");
                    sb.AppendLine($"Observation: {(call.Success ? "Success" : "Failed")}");
                    sb.AppendLine($"Result: {resultText}");
                    sb.AppendLine();
                }
                sb.AppendLine();
            }

            // Conversation history — budget-driven sliding window. Without a budget we
            // keep the legacy last-10; with a budget we keep as many recent messages as
            // fit under MaxHistoryTokens (newest first), so the window drives retention.
            if (history.Count > 0)
            {
                List<Dictionary<string, string>> kept;
                if (budget != null)
                {
                    kept = new List<Dictionary<string, string>>();
                    int used = 0;
                    for (int i = history.Count - 1; i >= 0; i--)
                    {
                        var m = history[i];
                        var c = m.ContainsKey("content") ? m["content"] : "";
                        var r = m.ContainsKey("role") ? m["role"] : "unknown";
                        int t = budget.EstimateTokens($"[{r}]: {c}");
                        if (used + t > budget.MaxHistoryTokens && kept.Count > 0) break;
                        kept.Insert(0, m);
                        used += t;
                    }
                    if (kept.Count < history.Count)
                        AppLogger.Log($"[ReAct][CtxBudget] History window: kept {kept.Count}/{history.Count} messages (~{used} tok)");
                }
                else
                {
                    kept = history.Skip(Math.Max(0, history.Count - 10)).ToList();
                }

                sb.AppendLine("## Conversation History");
                foreach (var msg in kept)
                {
                    var role = msg.ContainsKey("role") ? msg["role"] : "unknown";
                    var content = msg.ContainsKey("content") ? msg["content"] : "";
                    sb.AppendLine($"[{role}]: {content}");
                }
                sb.AppendLine();
            }

            // User prompt
            sb.AppendLine("## Current Task");
            sb.AppendLine(userPrompt);

            return sb.ToString();
        }

        /// <summary>
        /// Truncate a tool result: head+tail via the budget when available (keeps the
        /// informative head and tail), else a plain head cut. Bounded by maxChars.
        /// </summary>
        private static string TruncateResultText(string text, int maxChars, TokenBudgetSvc budget)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
            if (budget != null)
            {
                var t = budget.TruncateHeadTail(text, Math.Max(1, maxChars / TokenBudgetSvc.EstimatedCharsPerToken));
                return t.Content;
            }
            return text.Substring(0, maxChars) + "...";
        }

        /// <summary>
        /// Call the LLM with the given prompt.
        /// When EnableStreamingReAct is true, uses streaming SSE API and emits
        /// content/thinking events in real time via onEvent callback.
        /// Otherwise falls back to blocking GenerateSclAsync.
        /// </summary>
        private async Task<string> CallLlmAsync(
            string prompt,
            string context,
            List<Dictionary<string, string>> history,
            Action<AgentStreamEvent> onEvent,
            CancellationToken ct,
            Action<OllamaTokenStats> onUsage = null)
        {
            // Ollama models: use local LLM service directly (no Python backend)
            if (ModelProvider == "ollama" && _localLlmService != null)
            {
                return await CallLocalLlmAsync(prompt, context, history, onEvent, ct, onUsage);
            }

            if (_agentConfig.EnableStreamingReAct)
            {
                return await CallLlmStreamingAsync(prompt, context, history, onEvent, ct);
            }

            // Non-streaming fallback
            try
            {
                var result = await _aiService.GenerateSclAsync(prompt, context, history);
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Log("[ReAct] Cloud LLM failed: " + ex.Message);

                // Fallback to local LLM (use a general-purpose call, not intent classification)
                if (_localLlmService != null)
                {
                    try
                    {
                        var localResult = await _localLlmService.GenerateAsync(prompt, _localLlmService.CurrentModel);
                        return localResult;
                    }
                    catch (Exception localEx)
                    {
                        AppLogger.Log("[ReAct] Local LLM also failed: " + localEx.Message);
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Streaming LLM call that emits content/thinking events in real time
        /// via the SSE pipeline (Python backend → C# SSE parser → onEvent callback).
        /// </summary>
        private async Task<string> CallLlmStreamingAsync(
            string prompt,
            string context,
            List<Dictionary<string, string>> history,
            Action<AgentStreamEvent> onEvent,
            CancellationToken ct)
        {
            var resultBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<string>();

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
                        case "thinking":
                            var thinkingText = (ev.TextContent ?? ev.Thought?.Text ?? "").Trim();
                            if (string.IsNullOrEmpty(thinkingText)) break;
                            // Filter internal markers
                            if (thinkingText == "done" || thinkingText == "generating" ||
                                thinkingText == "json_fallback" || thinkingText == "json_parsed" ||
                                thinkingText == "langchain-agent" || thinkingText == "xml-ast-llm" ||
                                thinkingText == "xml-analysis-llm")
                                break;
                            // Tool call markers from Python backend
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
                        case "error":
                            errorBuilder.AppendLine(ev.ErrorMessage ?? ev.TextContent ?? "");
                            onEvent?.Invoke(AgentStreamEvent.Error(ev.ErrorMessage ?? ev.TextContent ?? ""));
                            break;
                        case "done":
                            if (errorBuilder.Length > 0)
                                tcs.TrySetResult(errorBuilder.ToString().Trim());
                            else
                                tcs.TrySetResult(resultBuilder.ToString());
                            break;
                    }
                }, context, history, ct);

                // Wait for the done event (no hard timeout; the caller's cancellation token governs).
                return await tcs.Task;
            }
            catch (OperationCanceledException)
            {
                return resultBuilder.Length > 0 ? resultBuilder.ToString() : null;
            }
            catch (Exception ex)
            {
                AppLogger.Log("[ReAct] Streaming LLM failed: " + ex.Message);
                // Fallback to non-streaming
                try
                {
                    return await _aiService.GenerateSclAsync(prompt, context, history);
                }
                catch
                {
                    return resultBuilder.Length > 0 ? resultBuilder.ToString() : null;
                }
            }
        }

        /// <summary>
        /// Call local Ollama model directly (no Python backend).
        /// Uses ILocalLlmService for text generation without function calling.
        /// </summary>
        private async Task<string> CallLocalLlmAsync(
            string prompt,
            string context,
            List<Dictionary<string, string>> history,
            Action<AgentStreamEvent> onEvent,
            CancellationToken ct,
            Action<OllamaTokenStats> onUsage = null)
        {
            try
            {
                onEvent?.Invoke(AgentStreamEvent.Thinking($"🦙 Calling {ModelName} directly..."));

                // Reasoning tasks -> InvokeAsync(Chat): /api/chat + think=true; the thought
                // process is shown live as a ThoughtBlock while the body streams out.
                var result = await _localLlmService.InvokeAsync(
                    prompt,
                    kind: OllamaTaskKind.Chat,
                    onChunk: c =>
                    {
                        if (!string.IsNullOrEmpty(c))
                            onEvent?.Invoke(AgentStreamEvent.Content(c));
                    },
                    onThinking: t =>
                    {
                        if (!string.IsNullOrEmpty(t))
                            onEvent?.Invoke(AgentStreamEvent.Thinking(t));
                    },
                    onUsage: onUsage,
                    maxTokens: 4096,
                    ct: ct);

                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ReAct] Local LLM ({ModelName}) failed: {ex.Message}");
                onEvent?.Invoke(AgentStreamEvent.Error(ex.Message));
                return null;
            }
        }

        /// <summary>
        /// Parse the LLM response to determine if it's a tool call, final answer, or thinking.
        /// </summary>
        /// <summary>
        /// Try to parse an OpenAI-style function-calling response:
        /// {"tool_calls":[{"name":"...","arguments":{...}}, ...]}.
        /// Returns null if the payload is not valid JSON or has no usable tool_calls,
        /// so the caller can fall back to legacy text parsing.
        /// </summary>
        private ParsedResponse TryParseFunctionCalling(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("tool_calls", out var toolCallsEl) ||
                    toolCallsEl.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var calls = new List<ToolCallParsed>();
                foreach (var el in toolCallsEl.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;

                    // Support both {name, arguments} and OpenAI {function:{name, arguments}}.
                    string name = null;
                    JsonElement argsEl = default;
                    bool hasArgs = false;

                    if (el.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    {
                        name = nameEl.GetString();
                        hasArgs = el.TryGetProperty("arguments", out argsEl);
                    }
                    else if (el.TryGetProperty("function", out var fnEl) && fnEl.ValueKind == JsonValueKind.Object)
                    {
                        if (fnEl.TryGetProperty("name", out var fnName) && fnName.ValueKind == JsonValueKind.String)
                            name = fnName.GetString();
                        hasArgs = fnEl.TryGetProperty("arguments", out argsEl);
                    }

                    if (string.IsNullOrWhiteSpace(name)) continue;

                    string arguments = "{}";
                    if (hasArgs)
                    {
                        // arguments may be a JSON object or a JSON-encoded string.
                        arguments = argsEl.ValueKind == JsonValueKind.String
                            ? (argsEl.GetString() ?? "{}")
                            : argsEl.GetRawText();
                    }

                    calls.Add(new ToolCallParsed { ToolName = name, Arguments = arguments });
                }

                if (calls.Count == 0)
                {
                    // No tool calls. If a "content" field is present, treat it as the final answer;
                    // otherwise return null so the caller falls back to legacy text parsing.
                    if (root.TryGetProperty("content", out var contentEl) &&
                        contentEl.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(contentEl.GetString()))
                    {
                        return new ParsedResponse
                        {
                            IsFinalAnswer = true,
                            FinalAnswer = contentEl.GetString()
                        };
                    }
                    return null;
                }

                // Respect the parallel hard limit when parsing (keeps ToolCalls bounded).
                int limit = _agentConfig.EnableParallelTools
                    ? Math.Max(1, _agentConfig.MaxParallelTools)
                    : 1;
                if (calls.Count > limit)
                    calls = calls.GetRange(0, limit);

                return new ParsedResponse
                {
                    ToolCall = calls[0],
                    ToolCalls = calls
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private ParsedResponse ParseLlmResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return new ParsedResponse { Text = "" };

            var trimmed = response.Trim();

            // Function-calling format: OpenAI-style {"tool_calls":[{"name":...,"arguments":{...}}, ...]}
            // Preferred over the legacy TOOL_CALL text format when the model emits JSON.
            // Gated by the UseFunctionCalling feature flag; when off, JSON is treated as plain text.
            if (_agentConfig.UseFunctionCalling && trimmed.StartsWith("{") && trimmed.Contains("tool_calls"))
            {
                var fnParsed = TryParseFunctionCalling(trimmed);
                if (fnParsed != null)
                    return fnParsed;
            }

            // Check for final answer
            if (trimmed.StartsWith("FINAL_ANSWER:", StringComparison.OrdinalIgnoreCase))
            {
                return new ParsedResponse
                {
                    IsFinalAnswer = true,
                    FinalAnswer = trimmed.Substring("FINAL_ANSWER:".Length).Trim()
                };
            }

            // Check for thinking (new mandatory format)
            if (trimmed.StartsWith("THINKING:", StringComparison.OrdinalIgnoreCase))
            {
                var thinkingText = trimmed.Substring("THINKING:".Length).Trim();
                // After THINKING, there might be a TOOL_CALL on the next line
                var lines = trimmed.Split('\n');
                if (lines.Length > 1)
                {
                    var remaining = string.Join("\n", lines.Skip(1)).Trim();
                    if (remaining.StartsWith("TOOL_CALL:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Parse the tool call from the remaining text
                        var toolLines = remaining.Split('\n');
                        var toolName = toolLines[0].Substring("TOOL_CALL:".Length).Trim();
                        var arguments = "{}";

                        if (toolLines.Length > 1 && toolLines[1].StartsWith("ARGUMENTS:", StringComparison.OrdinalIgnoreCase))
                        {
                            arguments = toolLines[1].Substring("ARGUMENTS:".Length).Trim();
                        }

                        return new ParsedResponse
                        {
                            Text = thinkingText, // Return thinking text to be displayed
                            ToolCall = new ToolCallParsed
                            {
                                ToolName = toolName,
                                Arguments = arguments
                            }
                        };
                    }
                }
                // Just thinking, no tool call
                return new ParsedResponse { Text = thinkingText };
            }

            // Check for tool call (legacy format without THINKING prefix)
            if (trimmed.StartsWith("TOOL_CALL:", StringComparison.OrdinalIgnoreCase))
            {
                var lines = trimmed.Split('\n');
                var toolName = lines[0].Substring("TOOL_CALL:".Length).Trim();
                var arguments = "{}";

                if (lines.Length > 1 && lines[1].StartsWith("ARGUMENTS:", StringComparison.OrdinalIgnoreCase))
                {
                    arguments = lines[1].Substring("ARGUMENTS:".Length).Trim();
                }

                return new ParsedResponse
                {
                    ToolCall = new ToolCallParsed
                    {
                        ToolName = toolName,
                        Arguments = arguments
                    }
                };
            }

            // Otherwise it's thinking/intermediate text
            return new ParsedResponse { Text = trimmed };
        }

        /// <summary>
        /// Execute a tool call via the shared ToolRegistry.
        /// </summary>
        private async Task<ToolResult> ExecuteToolAsync(ToolCallParsed toolCall, Func<string, string, Task<bool>> onConfirmRequired, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                // Look up the tool in the registry
                var tool = _toolRegistry.GetTool(toolCall.ToolName);
                if (tool == null)
                {
                    return new ToolResult
                    {
                        Success = false,
                        Error = $"Unknown tool: {toolCall.ToolName}",
                        Duration = sw.Elapsed
                    };
                }

                // Permission check
                if (tool.Permission == ToolPermissionLevel.Denied)
                {
                    return new ToolResult
                    {
                        Success = false,
                        Error = $"Tool '{tool.Name}' is denied by policy.",
                        Duration = sw.Elapsed
                    };
                }

                if (tool.Permission == ToolPermissionLevel.RequiresConfirmation)
                {
                    // Safe default: a tool requiring confirmation must have a confirmation
                    // callback. If the caller did not provide one (e.g. unattended /
                    // non-interactive paths), always refuse to run — never silently allow.
                    // "Refuse when nobody can confirm", not "allow when nobody confirms".
                    if (onConfirmRequired == null)
                    {
                        return new ToolResult
                        {
                            Success = false,
                            Error = $"Tool '{tool.Name}' requires user confirmation, but no confirmation channel is available in this context. Refused for safety.",
                            Duration = sw.Elapsed
                        };
                    }

                    bool confirmed = await onConfirmRequired(tool.Name, toolCall.Arguments);
                    if (!confirmed)
                    {
                        return new ToolResult
                        {
                            Success = false,
                            Error = "User cancelled tool execution.",
                            Duration = sw.Elapsed
                        };
                    }
                }

                // Execute without a hard timeout; only the caller's cancellation token stops the tool.
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                // Parse arguments JSON and execute via ToolRegistry
                JsonElement jsonParams;
                try
                {
                    using var jsonDoc = JsonDocument.Parse(toolCall.Arguments ?? "{}");
                    jsonParams = jsonDoc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // If arguments is not valid JSON, pass empty object
                    using var jsonDoc = JsonDocument.Parse("{}");
                    jsonParams = jsonDoc.RootElement.Clone();
                }

                var handlerTask = _toolRegistry.ExecuteAsync(toolCall.ToolName, jsonParams, linkedCts.Token);
                var delayTask = Task.Delay(Timeout.Infinite, linkedCts.Token);

                if (await Task.WhenAny(handlerTask, delayTask) == delayTask)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();
                }

                var agentResult = await handlerTask;
                return new ToolResult
                {
                    Success = agentResult.IsSuccess,
                    Output = agentResult.IsSuccess ? agentResult.Data?.ToString() ?? "" : "",
                    Error = agentResult.ErrorMessage,
                    Duration = sw.Elapsed
                };
            }
            catch (OperationCanceledException)
            {
                var errorMsg = "Execution cancelled by user.";
                return new ToolResult
                {
                    Success = false,
                    Error = errorMsg,
                    Duration = sw.Elapsed
                };
            }
            catch (Exception ex)
            {
                return new ToolResult
                {
                    Success = false,
                    Error = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }

        private static bool IsDeadToolLoop(List<ToolCallRecord> trace)
        {
            // Dead-loop guard: N consecutive identical (tool + arguments) calls mean the model
            // is stuck retrying the same action. Conservative: exact ToolName + Arguments equality.
            int threshold = WatchdogPolicy.ToolRepeatThreshold;
            if (trace == null || trace.Count < threshold) return false;

            var last = trace[trace.Count - 1];
            if (string.IsNullOrEmpty(last.ToolName)) return false;

            for (int i = trace.Count - 2, n = 1; i >= 0 && n < threshold; i--, n++)
            {
                var prev = trace[i];
                if (!string.Equals(prev.ToolName, last.ToolName, StringComparison.Ordinal) ||
                    !string.Equals(prev.Arguments ?? string.Empty, last.Arguments ?? string.Empty, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Execute multiple tool calls, honoring the single-STA constraint of TIA Openness.
        ///
        /// Tools whose <see cref="IAgentTool.SupportsParallel"/> is true (pure .NET / SQLite
        /// knowledge index / MCP over HTTP — nothing that touches PlcSoftware) run concurrently,
        /// bounded by <see cref="AgentConfig.MaxParallelTools"/>. All other tools (STA-affine or
        /// write operations) run serially so they queue naturally on the STA thread and never
        /// corrupt project state. Results are returned in the original call order.
        ///
        /// The batch is bounded only by the caller's cancellation token; the user cancels the run to stop it.
        /// </summary>
        private async Task<List<ToolResult>> ExecuteToolsParallelAsync(
            List<ToolCallParsed> calls,
            Func<string, string, Task<bool>> onConfirmRequired,
            CancellationToken ct)
        {
            var results = new List<ToolResult>();
            if (calls == null || calls.Count == 0)
                return results;

            // Pre-size the result slots so we can write by original index regardless of completion order.
            for (int i = 0; i < calls.Count; i++)
                results.Add(null);

            using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Partition into parallel-safe vs serial while remembering original positions.
            var parallelIndices = new List<int>();
            var serialIndices = new List<int>();
            for (int i = 0; i < calls.Count; i++)
            {
                var tool = _toolRegistry.GetTool(calls[i].ToolName);
                if (tool != null && tool.SupportsParallel)
                    parallelIndices.Add(i);
                else
                    serialIndices.Add(i);
            }

            // Run parallel-safe tools concurrently, throttled to MaxParallelTools.
            if (parallelIndices.Count > 0)
            {
                int maxDop = Math.Max(1, _agentConfig.MaxParallelTools);
                using var throttle = new SemaphoreSlim(maxDop);
                var tasks = parallelIndices.Select(async idx =>
                {
                    await throttle.WaitAsync(batchCts.Token).ConfigureAwait(false);
                    try
                    {
                        results[idx] = await ExecuteToolAsync(calls[idx], onConfirmRequired, batchCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        results[idx] = new ToolResult
                        {
                            Success = false,
                            Error = "Execution cancelled or timed out (parallel batch)."
                        };
                    }
                    finally
                    {
                        throttle.Release();
                    }
                }).ToList();

                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Individual slots are already filled with cancellation results above.
                }
            }

            // Run the remaining (STA-affine / write) tools serially in original order.
            foreach (var idx in serialIndices)
            {
                if (batchCts.IsCancellationRequested)
                {
                    results[idx] = new ToolResult
                    {
                        Success = false,
                        Error = "Execution cancelled or timed out before start (parallel batch)."
                    };
                    continue;
                }

                try
                {
                    results[idx] = await ExecuteToolAsync(calls[idx], onConfirmRequired, batchCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    results[idx] = new ToolResult
                    {
                        Success = false,
                        Error = "Execution cancelled or timed out (parallel batch)."
                    };
                }
            }

            // Safety net: no slot should be null.
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i] == null)
                {
                    results[i] = new ToolResult
                    {
                        Success = false,
                        Error = "Tool did not produce a result."
                    };
                }
            }

            return results;
        }
    }
}