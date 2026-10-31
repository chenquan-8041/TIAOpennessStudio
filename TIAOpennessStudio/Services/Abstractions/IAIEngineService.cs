using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    public delegate void SseEventHandler(string eventType, object eventData);

    /// <summary>
    /// AI engine service that wraps the Python backend for SCL generation, code
    /// analysis, and provider/model management.
    /// </summary>
    public interface IAIEngineService
    {
        Task<string> GenerateSclAsync(string prompt, string context = "", List<Dictionary<string, string>> history = null, string rid = null, string intent = "");
        
        Task GenerateSclStreamingAsync(string prompt, Action<string, string> onChunkReceived, string context = "", List<Dictionary<string, string>> history = null, System.Threading.CancellationToken ct = default, string rid = null);
        
        Task GenerateSclStreamingExAsync(string prompt, Action<SseEvent> onEventReceived, string context = "", List<Dictionary<string, string>> history = null, System.Threading.CancellationToken ct = default, string rid = null, string intent = "", string planningKnowledge = "", List<string> images = null, string projectKey = "", bool evolutionEnabled = false);

        /// <summary>
        /// Stream from Python /chat_plan endpoint (cloud models only).
        /// Same SSE protocol as GenerateSclStreamingExAsync. The Python backend runs the
        /// multi-agent supervisor and defaults the intent to GenerateCode when none is supplied.
        /// Ollama/local models never use this endpoint (the backend rejects them fail-closed);
        /// they run on the C# ReActEngine local loop.
        /// </summary>
        Task GenerateChatPlanStreamAsync(string prompt, Action<SseEvent> onEventReceived, string context = "", List<Dictionary<string, string>> history = null, System.Threading.CancellationToken ct = default, string rid = null, string intent = "", string planningKnowledge = "", string projectKey = "");

        /// <summary>
        /// Record a project-plan milestone decision (approve / adjust / reject) on the Python
        /// backend via /plan/decide. The frontend calls this after the user answers the milestone
        /// approval card, then re-sends /chat_plan (with the same project_key) to resume the plan.
        /// Returns the backend's plan_status (e.g. "paused" / "rejected") or null on failure.
        /// </summary>
        Task<string> PostPlanDecisionAsync(string planId, string phaseId, string decision, string note = null);

        /// <summary>Task-failure decision (continue / skip / terminate): POST /plan/task_decide. Returns the backend plan_status or null.</summary>
        Task<string> PostTaskDecisionAsync(string planId, string taskId, string decision);
        
        Task<string> ConvertXmlAsync(string xmlContent, string instruction = "", string mode = "scl", string rid = null);
        Task<string> GenerateXmlAsync(string prompt, string name = "", string blockType = "", string rid = null);

        /// <summary>
        /// Project-level long-task entry: POST /plan/run; produces the plan, starts a background job, and returns immediately.
        /// Non-project / planning-failure returns Started=false so the caller falls back to the single-block /chat_plan flow.
        /// </summary>
        Task<PlanStartResult> StartPlanAsync(string prompt, string context = "", List<Dictionary<string, string>> history = null, string intent = "", string planningKnowledge = "", string projectKey = "", bool verifyCompileAtPlanEnd = false);

        /// <summary>Progress snapshot: GET /plan/{planId}; the data source for reconnect / frontend polling.</summary>
        Task<PlanStatusSnapshot> GetPlanStatusAsync(string planId);

        /// <summary>Follow an already-started plan's background job: POST /plan/{planId}/stream; forwards SSE events until this round ends.</summary>
        Task StreamPlanAsync(string planId, Action<SseEvent> onEventReceived, System.Threading.CancellationToken ct = default);

        /// <summary>
        /// Probe whether the current model supports image (vision) input via the
        /// Python backend /probe_vision endpoint. Returns a tri-state string:
        /// "supported" | "unsupported" | "unknown". Never throws.
        /// </summary>
        Task<string> ProbeVisionAsync(string rid = null);

        /// <summary>
        /// Test the current provider/model/key/base_url by sending one tiny real
        /// chat request through the Python backend /provider/test endpoint. This
        /// keeps all platform-adapter knowledge in the backend — the C# side does
        /// not talk to each platform directly. Never throws; returns a structured
        /// result the UI can render.
        /// </summary>
        Task<ProviderTestResult> TestProviderAsync(string rid = null);

        /// <summary>
        /// Hot-swap runtime model/credentials so users can switch model without restart.
        /// Pass null for fields you do not want to change.
        /// </summary>
        void UpdateRuntimeConfig(string apiKey = null, string endpoint = null, string model = null, string provider = null, string reasoningEffort = null);

        /// <summary>
        /// Current model name (e.g. "gpt-4o", "qwen2.5-coder:7b").
        /// Used for runtime routing decisions (Ollama vs cloud).
        /// </summary>
        string ModelName { get; }

        /// <summary>
        /// Current provider (e.g. "ollama", "openai", "deepseek").
        /// Used for runtime routing — Ollama goes to local service, cloud to Python.
        /// </summary>
        string ModelProvider { get; }
    }

    /// <summary>
    /// Result of a provider connectivity test (from Python /provider/test).
    /// </summary>
    public class ProviderTestResult
    {
        public bool Ok { get; set; }
        // "ok" | "auth" | "network" | "model_not_found" | "unknown"
        public string Category { get; set; } = "unknown";
        public int? LatencyMs { get; set; }
        public string Model { get; set; }
        public string Reply { get; set; }
        public string Detail { get; set; }
    }

    public class SseEvent
    {
        public string EventType { get; set; }
        public string TextContent { get; set; }
        public ThoughtData Thought { get; set; }
        public FileChangeData FileChange { get; set; }
        public CommandData Command { get; set; }
        public StatusData Status { get; set; }
        public ContextRefData ContextRef { get; set; }
        public NoticeData Notice { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class ThoughtData
    {
        public string Text { get; set; }
        public string Details { get; set; }
    }

    public class FileChangeData
    {
        public string File { get; set; }
        public string Action { get; set; }
        public int LinesAdded { get; set; }
        public int LinesRemoved { get; set; }
        public string Summary { get; set; }
    }

    public class CommandData
    {
        public string Command { get; set; }
        public string Status { get; set; }
        public string Output { get; set; }
    }

    public class StatusData
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
    }

    public class ContextRefData
    {
        public string Type { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public int Line { get; set; }
    }

    /// <summary>Project-plan start result returned by /plan/run.</summary>
    public class PlanStartResult
    {
        public bool Started { get; set; }
        public string PlanId { get; set; }
        public string Status { get; set; }
        public string Summary { get; set; }
        public int Total { get; set; }
    }

    /// <summary>Progress snapshot returned by GET /plan/{planId}.</summary>
    public class PlanStatusSnapshot
    {
        public string PlanId { get; set; }
        public string Status { get; set; }
        public string Summary { get; set; }
        public int Total { get; set; }
        public int Done { get; set; }
        public int Failed { get; set; }
        public string Current { get; set; }
    }
}