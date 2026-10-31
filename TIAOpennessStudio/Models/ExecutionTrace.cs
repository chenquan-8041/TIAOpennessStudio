using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// Execution status.
    /// </summary>
    public enum ExecutionStatus
    {
        Pending,
        Running,
        Paused,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Step type.
    /// </summary>
    public enum StepType
    {
        Thought,         // Reasoning
        Action,          // Perform an action
        Observation,     // Observe result
        Condition,       // Conditional branch
        Parallel,        // Parallel execution
        Error,           // Error
        System           // System message
    }

    /// <summary>
    /// A single execution step.
    /// </summary>
    public class ExecutionStep
    {
        public int StepNumber { get; set; }
        public string StepId { get; set; } = Guid.NewGuid().ToString();
        public StepType StepType { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public long DurationMs { get; set; }

        // Tool call info
        public string ToolName { get; set; }
        public Dictionary<string, object> ToolInput { get; set; } = new Dictionary<string, object>();
        public object ToolOutput { get; set; }

        // Conditional branch info
        public string Condition { get; set; }
        public bool ConditionResult { get; set; }
        
        // Parallel execution info
        public List<ExecutionStep> Children { get; set; } = new List<ExecutionStep>();
        
        // Error info
        public string ErrorMessage { get; set; }
        
        // Parent step (for building the call chain)
        public string ParentStepId { get; set; }
    }

    /// <summary>
    /// Trace of an agent execution.
    /// </summary>
    public class AgentExecution
    {
        public string ExecutionId { get; set; } = Guid.NewGuid().ToString();
        public string AgentName { get; set; }
        public DateTime StartTime { get; set; } = DateTime.Now;
        public DateTime? EndTime { get; set; }
        public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;
        
        // Input
        public string InputPrompt { get; set; }
        public Dictionary<string, object> InputContext { get; set; } = new Dictionary<string, object>();
        
        // Output
        public string Output { get; set; }
        
        // Execution steps
        public List<ExecutionStep> Steps { get; set; } = new List<ExecutionStep>();
        
        // Execution stats
        public int TotalSteps => Steps.Count;
        public int ToolCalls => Steps.FindAll(s => s.StepType == StepType.Action).Count;
        public long TotalDurationMs => EndTime.HasValue 
            ? (long)(EndTime.Value - StartTime).TotalMilliseconds 
            : (long)(DateTime.Now - StartTime).TotalMilliseconds;
        
        // Error info
        public string ErrorMessage { get; set; }
        
        // Breakpoints
        public List<string> BreakpointStepIds { get; set; } = new List<string>();
        
        /// <summary>
        /// Appends a step, assigning its sequence number.
        /// </summary>
        public void AddStep(ExecutionStep step)
        {
            step.StepNumber = Steps.Count + 1;
            Steps.Add(step);
        }

        /// <summary>
        /// Returns the top-level steps (those without a parent), i.e. the call-tree roots.
        /// </summary>
        public List<ExecutionStep> GetCallChain()
        {
            // Return top-level steps (those without a parent).
            return Steps.FindAll(s => string.IsNullOrEmpty(s.ParentStepId));
        }
    }

    /// <summary>
    /// A single execution log entry.
    /// </summary>
    public class ExecutionLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Level { get; set; } = "Info";  // Debug, Info, Warning, Error
        public string Message { get; set; }
        public string Source { get; set; }
        public string ExecutionId { get; set; }
        public string StepId { get; set; }
    }

    /// <summary>
    /// A debugging session for an execution.
    /// </summary>
    public class DebugSession
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString();
        public string ExecutionId { get; set; }
        public DateTime StartTime { get; set; } = DateTime.Now;
        
        // Breakpoints
        public List<string> Breakpoints { get; set; } = new List<string>();
        
        // Current step
        public string CurrentStepId { get; set; }
        public bool IsPaused { get; set; }
        
        // Watched variables
        public Dictionary<string, object> WatchVariables { get; set; } = new Dictionary<string, object>();
        
        // Execution history (for replay)
        public List<AgentExecution> ExecutionHistory { get; set; } = new List<AgentExecution>();
        
        // Logs
        public List<ExecutionLogEntry> Logs { get; set; } = new List<ExecutionLogEntry>();
        
        /// <summary>
        /// Appends a log entry bound to this session.
        /// </summary>
        public void AddLog(string level, string message, string source = "System")
        {
            Logs.Add(new ExecutionLogEntry
            {
                Level = level,
                Message = message,
                Source = source,
                ExecutionId = ExecutionId,
                StepId = CurrentStepId
            });
        }
    }
}