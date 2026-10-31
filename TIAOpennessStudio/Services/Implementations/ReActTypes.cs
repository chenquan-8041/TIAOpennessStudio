using System;
using System.Collections.Generic;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    public class ReActResult
    {
        public ReActStatus Status { get; set; } = ReActStatus.Running;
        public string FinalAnswer { get; set; }
        public string Message { get; set; }
        public List<ToolCallRecord> ToolCalls { get; set; } = new List<ToolCallRecord>();
        public int TotalIterations { get; set; }

        /// <summary>
        /// Plan completion report (non-null only when plan tracking is active and an
        /// ExecutionPlan was supplied to the local ReAct loop).
        /// </summary>
        public PlanCompletionReport PlanCompletion { get; set; }
    }

    public enum ReActStatus
    {
        Running,
        Completed,
        Cancelled,
        Error,
        MaxIterationsReached
    }

    public class ToolCallRecord
    {
        public int Iteration { get; set; }
        public string ToolName { get; set; }
        public string Arguments { get; set; }
        public string Result { get; set; }
        public bool Success { get; set; }
        public TimeSpan Duration { get; set; }
    }

    public class ToolCallArgs
    {
        public string ToolName { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public class ToolResult
    {
        public bool Success { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public TimeSpan Duration { get; set; }
    }

    public class ParsedResponse
    {
        public bool IsFinalAnswer { get; set; }
        public string FinalAnswer { get; set; }
        public ToolCallParsed ToolCall { get; set; }
        public string Text { get; set; }

        /// <summary>
        /// All tool calls parsed from a JSON function-calling response (OpenAI-style
        /// {"tool_calls":[...]}). When multiple calls are present, <see cref="ToolCall"/>
        /// holds the first one for backward compatibility, and this list holds all of them.
        /// Null/empty for legacy single TOOL_CALL text responses.
        /// </summary>
        public List<ToolCallParsed> ToolCalls { get; set; }
    }

    public class ToolCallParsed
    {
        public string ToolName { get; set; }
        public string Arguments { get; set; }
    }
}