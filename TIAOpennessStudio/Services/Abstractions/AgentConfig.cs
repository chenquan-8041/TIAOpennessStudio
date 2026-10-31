using System.Text.Json.Serialization;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Agent loop feature flags. Each flag controls one phase of the V2 refactor.
    /// All flags default to false — the agent behaves exactly as before the refactor.
    /// Enable flags one by one after verifying each phase.
    ///
    /// Loaded from ai_config.json → "Agent" section, so users can toggle features
    /// without recompiling.
    /// </summary>
    public class AgentConfig
    {
        // ── Phase 1: Function Calling ──────────────────────────────
        /// <summary>
        /// When true, ParseLlmResponse first tries to parse OpenAI-style
        /// tool_calls JSON before falling back to TOOL_CALL: text parsing.
        /// </summary>
        [JsonPropertyName("useFunctionCalling")]
        public bool UseFunctionCalling { get; set; } = false;

        // ── Phase 2: Safe Writes (inject QualityGate/Snapshot/CompileFix into ReAct) ──
        /// <summary>Validate code via QualityGate before write tools execute.</summary>
        [JsonPropertyName("enableQualityGateInReAct")]
        public bool EnableQualityGateInReAct { get; set; } = false;

        /// <summary>Create a snapshot before write tools execute.</summary>
        [JsonPropertyName("enableSnapshotInReAct")]
        public bool EnableSnapshotInReAct { get; set; } = false;

        /// <summary>Auto-compile + auto-fix after successful writes.</summary>
        [JsonPropertyName("enableCompileFixInReAct")]
        public bool EnableCompileFixInReAct { get; set; } = false;

        // ── Phase 3: Streaming ReAct ──────────────────────────────
        /// <summary>
        /// When true, ReAct LLM calls use GenerateSclStreamingExAsync instead of
        /// the blocking GenerateSclAsync. Users see AI thinking in real time.
        /// </summary>
        [JsonPropertyName("enableStreamingReAct")]
        public bool EnableStreamingReAct { get; set; } = true;

        // ── Phase 4: Planning Phase ───────────────────────────────
        /// <summary>Enable deep intent understanding + LLM-generated execution plans.</summary>
        [JsonPropertyName("enablePlanning")]
        public bool EnablePlanning { get; set; } = false;

        /// <summary>Minimum complexity that triggers Planning. "Simple" tasks skip it.</summary>
        [JsonPropertyName("minComplexityForPlanning")]
        public string MinComplexityForPlanning { get; set; } = "Medium";

        // ── Phase 5: Reflection ───────────────────────────────────
        /// <summary>Analyze tool failures and adjust strategy instead of blind retry.</summary>
        [JsonPropertyName("enableReflection")]
        public bool EnableReflection { get; set; } = false;

        /// <summary>Max auto-reflection attempts before giving up.</summary>
        [JsonPropertyName("maxReflectionRetries")]
        public int MaxReflectionRetries { get; set; } = 3;

        // ── Phase 6: Observation Compression ──────────────────────
        /// <summary>Auto-summarize tool outputs longer than CompressThreshold chars.</summary>
        [JsonPropertyName("enableObservationCompression")]
        public bool EnableObservationCompression { get; set; } = false;

        /// <summary>Tool output longer than this (chars) triggers compression.</summary>
        [JsonPropertyName("compressThreshold")]
        public int CompressThreshold { get; set; } = 500;

        // ── Plan Tracking: bridge planning ↔ execution ─────────────
        /// <summary>
        /// When true, ReAct loop matches tool calls to ExecutionPlan steps
        /// and emits progress events. Pure observer — never blocks execution.
        /// </summary>
        [JsonPropertyName("enablePlanTracking")]
        public bool EnablePlanTracking { get; set; } = false;

        // ── Planning Knowledge: reuse proven plans across sessions ──
        /// <summary>
        /// When true, the orchestrator recalls historical successful plans before
        /// planning and records execution outcomes afterward. Pure augmentation —
        /// planning knowledge is treated as guidance, never as project fact.
        /// </summary>
        [JsonPropertyName("enablePlanningKnowledge")]
        public bool EnablePlanningKnowledge { get; set; } = true;

        /// <summary>
        /// When true, the evolution system records full agent trajectories and
        /// derives/injects repair experiences. Default false — evolution is
        /// opt-in (fail-closed baseline unless explicitly enabled).
        /// </summary>
        [JsonPropertyName("enableRepairEvolution")]
        public bool EnableRepairEvolution { get; set; } = false;

        /// <summary>
        /// Minimum ratio of completed/total steps for an outcome to count as a
        /// reusable success. Below this, the run is recorded as a failure pattern.
        /// </summary>
        [JsonPropertyName("planningKnowledgeSuccessThreshold")]
        public double PlanningKnowledgeSuccessThreshold { get; set; } = 1.0;

        // ── Phase 7: Unified Engine ────────────────────────────────
        /// <summary>
        /// When true, all agent requests go through UnifiedAgentEngine
        /// instead of the legacy AgentOrchestrator static plan path.
        /// Default true. The legacy static-plan path is not reached when this is on;
        /// AIConfig.Load() migrates older configs that stored false.
        /// </summary>
        [JsonPropertyName("useUnifiedEngine")]
        public bool UseUnifiedEngine { get; set; } = true;

        // ── [NEW] Cloud Agent routing ──────────────────────────────
        /// <summary>
        /// When true (and UseUnifiedEngine is also true), the agent uses
        /// Python LangGraph Agent (cloud LLM with function calling) instead of
        /// C# ReActEngine text-based TOOL_CALL loop.
        /// Ollama/local models are never routed to Python regardless of this flag:
        /// UnifiedAgentEngine.IsOllamaModel() takes precedence and runs the C# ReActEngine loop.
        /// </summary>
        [JsonPropertyName("useCloudAgent")]
        public bool UseCloudAgent { get; set; } = true;

        // ── [NEW] Chat Plan Endpoint ───────────────────────────────
        /// <summary>
        /// When true (and UseUnifiedEngine is also true), routes CLOUD models
        /// through the Python /chat_plan endpoint instead of /generate_stream.
        /// Both endpoints run the same multi-agent supervisor. /chat_plan defaults the intent
        /// to GenerateCode when none is supplied and does not carry project_key / evolution_enabled.
        /// Ollama/local models never use this endpoint: they always run on the C# ReActEngine
        /// local loop, and the Python backend rejects local endpoints fail-closed.
        /// </summary>
        [JsonPropertyName("useChatPlanEndpoint")]
        public bool UseChatPlanEndpoint { get; set; } = false;

        // ── Parallel Tool Execution ─────────────────────────────
        /// <summary>
        /// When true, ReAct loop can execute multiple tool calls in parallel
        /// when the LLM returns multiple tool_calls in one response.
        /// </summary>
        [JsonPropertyName("enableParallelTools")]
        public bool EnableParallelTools { get; set; } = false;

        /// <summary>Maximum number of tools to execute in parallel (hard limit).</summary>
        [JsonPropertyName("maxParallelTools")]
        public int MaxParallelTools { get; set; } = 3;

        /// <summary>Total timeout for all parallel tools combined (seconds).</summary>
        [JsonPropertyName("parallelToolTimeoutSeconds")]
        public int ParallelToolTimeoutSeconds { get; set; } = 120;

        // ── Write approvals ───────────────────────────────────────
        /// <summary>
        /// Soft approval deadline in seconds. After this the approval card is marked expired but stays
        /// on screen and can still be approved, so a slow user never loses a queued write.
        /// 0 or negative = the service default (180).
        /// </summary>
        [JsonPropertyName("approvalTimeoutSeconds")]
        public int ApprovalTimeoutSeconds { get; set; } = 180;

        /// <summary>
        /// Hard approval deadline in seconds. At this point the write is refused (approval_timeout).
        /// Must stay above <see cref="ApprovalTimeoutSeconds"/>; capped by the interaction service (600).
        /// The Python REST/MCP read timeouts are sized above this value.
        /// </summary>
        [JsonPropertyName("approvalHardTimeoutSeconds")]
        public int ApprovalHardTimeoutSeconds { get; set; } = 600;

        /// <summary>
        /// How many times an unanswered approval card is re-shown before the write is refused.
        /// Only a timeout is retried — a deny/cancel is final. 0 or negative = 1 (ask once).
        /// The Python MCP read timeout must stay above attempts × <see cref="ApprovalHardTimeoutSeconds"/>.
        /// </summary>
        [JsonPropertyName("approvalMaxAttempts")]
        public int ApprovalMaxAttempts { get; set; } = 3;

        /// <summary>
        /// Opt-in: after a project plan finishes, run ONE full PLC compile (snapshot + diff) and report
        /// newly introduced errors. Off by default because a whole-PLC compile is heavy and competes
        /// with the user editing in TIA Portal. Per-block checks after each write still run regardless.
        /// </summary>
        [JsonPropertyName("verifyCompileAtPlanEnd")]
        public bool VerifyCompileAtPlanEnd { get; set; } = false;

        // ── ReAct limits ──────────────────────────────────────────
        [JsonPropertyName("maxIterations")]
        public int MaxIterations { get; set; } = 20;

        [JsonPropertyName("maxTokens")]
        public int MaxTokens { get; set; } = 1024000;

        [JsonPropertyName("toolTimeoutSeconds")]
        public int ToolTimeoutSeconds { get; set; } = 45;

        /// <summary>
        /// Create a default config with all V2 features disabled.
        /// </summary>
        public static AgentConfig CreateDefault() => new AgentConfig();
    }
}
