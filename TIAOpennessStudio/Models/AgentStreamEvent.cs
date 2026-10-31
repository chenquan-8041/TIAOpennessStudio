using System.Collections.Generic;

namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// Typed agent stream events dispatched from AgentOrchestrator to the frontend
    /// ChatViewModel. They let ChatViewModel route each kind of content into the
    /// appropriate IMessageBlock on ChatMessage instead of concatenating everything
    /// as plain text.
    /// </summary>
    public enum AgentStreamKind
    {
        /// <summary>Plain text fragment (LLM natural-language reply / explanation).</summary>
        Content,

        /// <summary>Model reasoning (langgraph thinking event, not a tool call).</summary>
        Thinking,

        /// <summary>Tool call started; Tool* fields carry the tool name / input.</summary>
        ToolStart,

        /// <summary>Tool call finished; ToolName / ToolOutput carry the result.</summary>
        ToolEnd,

        /// <summary>Error message (API or agent error).</summary>
        Error,

        /// <summary>Status message (e.g. step transition), rendered as a lightweight status bar.</summary>
        Status,

        /// <summary>Token usage stats, reported after context assembly.</summary>
        TokenUsage,

        /// <summary>Delivery manifest (block list table) shown at task completion.</summary>
        Delivery,

        /// <summary>Project-plan milestone reached; PlanId/PhaseId/PhaseName carry the phase to approve.</summary>
        Milestone,

        /// <summary>Project-plan execution progress (long-task job); PlanProgress carries done/total/failed/current.</summary>
        PlanProgress,

        /// <summary>A task in the project plan failed (long-task job); TaskFailed carries the task to continue/skip/terminate.</summary>
        TaskFailed,

        /// <summary>User-facing notice from the backend: code + English fallback + params, localized by the UI.</summary>
        Notice,
    }

    public class AgentStreamEvent
    {
        public AgentStreamKind Kind { get; set; }
        public string Text { get; set; }
        public string ToolName { get; set; }
        public string ToolInput { get; set; }
        public string ToolOutput { get; set; }

        /// <summary>Token usage data (valid only when Kind == TokenUsage).</summary>
        public TokenUsageData TokenUsage { get; set; }

        /// <summary>Delivery manifest data (valid only when Kind == Delivery).</summary>
        public DeliveryBlock Delivery { get; set; }

        /// <summary>Milestone plan id (valid only when Kind == Milestone).</summary>
        public string PlanId { get; set; }

        /// <summary>Milestone phase id (valid only when Kind == Milestone).</summary>
        public string PhaseId { get; set; }

        /// <summary>Milestone phase name (valid only when Kind == Milestone).</summary>
        public string PhaseName { get; set; }

        /// <summary>Plan progress data (valid only when Kind == PlanProgress).</summary>
        public PlanProgressData PlanProgress { get; set; }

        /// <summary>Failed-task data (valid only when Kind == TaskFailed).</summary>
        public TaskFailedData TaskFailed { get; set; }

        /// <summary>User-facing notice data (valid only when Kind == Notice).</summary>
        public NoticeData Notice { get; set; }

        public static AgentStreamEvent Content(string text) => new AgentStreamEvent { Kind = AgentStreamKind.Content, Text = text };
        public static AgentStreamEvent Thinking(string text) => new AgentStreamEvent { Kind = AgentStreamKind.Thinking, Text = text };
        public static AgentStreamEvent ToolStart(string name, string input = null) => new AgentStreamEvent { Kind = AgentStreamKind.ToolStart, ToolName = name, ToolInput = input };
        public static AgentStreamEvent ToolEnd(string name, string output = null) => new AgentStreamEvent { Kind = AgentStreamKind.ToolEnd, ToolName = name, ToolOutput = output };
        public static AgentStreamEvent Error(string text) => new AgentStreamEvent { Kind = AgentStreamKind.Error, Text = text };
        public static AgentStreamEvent Status(string text) => new AgentStreamEvent { Kind = AgentStreamKind.Status, Text = text };
        public static AgentStreamEvent TokenUsageUpdate(TokenUsageData data) => new AgentStreamEvent { Kind = AgentStreamKind.TokenUsage, TokenUsage = data };
        public static AgentStreamEvent DeliveryUpdate(DeliveryBlock block) => new AgentStreamEvent { Kind = AgentStreamKind.Delivery, Delivery = block };
        public static AgentStreamEvent Milestone(string planId, string phaseId, string name) => new AgentStreamEvent { Kind = AgentStreamKind.Milestone, PlanId = planId, PhaseId = phaseId, PhaseName = name };
        public static AgentStreamEvent PlanProgressUpdate(PlanProgressData data) => new AgentStreamEvent { Kind = AgentStreamKind.PlanProgress, PlanProgress = data };
        public static AgentStreamEvent TaskFailedUpdate(TaskFailedData data) => new AgentStreamEvent { Kind = AgentStreamKind.TaskFailed, TaskFailed = data };
        public static AgentStreamEvent NoticeUpdate(NoticeData data) => new AgentStreamEvent { Kind = AgentStreamKind.Notice, Notice = data };
    }

    /// <summary>
    /// User-facing backend notice. The backend only emits English; Code is the stable
    /// translation key resolved by LanguageManager (English / German / Chinese).
    /// </summary>
    public class NoticeData
    {
        public string Code { get; set; }
        public string Text { get; set; }
        public List<string> Params { get; set; } = new List<string>();
    }

    /// <summary>Long-task project plan execution progress.</summary>
    public class PlanProgressData
    {
        public int Total { get; set; }
        public int Done { get; set; }
        public int Failed { get; set; }
        public string Current { get; set; }
    }

    /// <summary>A failed task in a project plan (continue / skip / terminate).</summary>
    public class TaskFailedData
    {
        public string PlanId { get; set; }
        public string TaskId { get; set; }
        public string BlockName { get; set; }
        public string Error { get; set; }

        /// <summary>Present when the failure is a known blocker requiring a manual user action.</summary>
        public ManualActionData ManualAction { get; set; }
    }

    /// <summary>A known write blocker the agent cannot fix itself; the UI shows a card instructing the user.</summary>
    public class ManualActionData
    {
        /// <summary>Stable translation key resolved by LanguageManager ("Agent.<code>").</summary>
        public string Code { get; set; }

        /// <summary>English instruction fallback (the backend only emits English).</summary>
        public string Text { get; set; }
    }

    /// <summary>
    /// Token usage data sent back from AgentOrchestrator to the UI.
    /// </summary>
    public class TokenUsageData
    {
        public int PromptTokens { get; set; }
        public int ContextTokens { get; set; }
        public int HistoryTokens { get; set; }
        public int ToolOutputTokens { get; set; }
        public int TotalUsed { get; set; }
        public int TotalBudget { get; set; }
        public int Remaining { get; set; }
        public double UsagePercent { get; set; }
        public string Status { get; set; } // "Ok", "Warning", "Exceeded"
    }
}
