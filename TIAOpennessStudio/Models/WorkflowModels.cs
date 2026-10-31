using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// Workflow node type.
    /// </summary>
    public enum NodeType
    {
        Start,
        End,
        Action,       // Execute a skill/tool
        Condition,    // Conditional branch
        Parallel,     // Run branches in parallel
        Fork,         // Split into branches
        Join,         // Merge branches
        Loop,         // Loop
        Switch,       // Multi-way conditional branch
        TryCatch,     // Error handling
        SubFlow,      // Nested sub-flow
        Delay         // Delay
    }

    /// <summary>
    /// A node in a flow definition.
    /// </summary>
    public class FlowNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public NodeType Type { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }

        // Node configuration
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

        // Flow control
        public List<string> NextNodes { get; set; } = new List<string>();
        public string DefaultNextNode { get; set; }  // Default target for conditional branches

        // Condition configuration
        public string ConditionExpression { get; set; }

        // Loop configuration
        public int MaxLoopCount { get; set; } = 100;
        public string LoopVariable { get; set; }

        // Parallel execution configuration
        public List<string> ParallelNodes { get; set; } = new List<string>();
        public ParallelMode ParallelMode { get; set; } = ParallelMode.All;

        // Error-handling configuration
        public string ErrorVariable { get; set; }
        public List<string> CatchNodes { get; set; } = new List<string>();
        public List<string> FinallyNodes { get; set; } = new List<string>();

        // Position (for visualization)
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>
    /// Parallel mode.
    /// </summary>
    public enum ParallelMode
    {
        All,        // All complete
        Any,        // Any one complete
        Race        // First one completes
    }

    /// <summary>
    /// A flow definition.
    /// </summary>
    public class FlowDefinition
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Description { get; set; }
        public string Version { get; set; } = "1.0.0";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public List<FlowNode> Nodes { get; set; } = new List<FlowNode>();
        public string StartNodeId { get; set; }

        public Dictionary<string, object> Variables { get; set; } = new Dictionary<string, object>();

        public List<SkillParameter> Parameters { get; set; } = new List<SkillParameter>();
    }

    /// <summary>
    /// State of a flow instance.
    /// </summary>
    public enum FlowInstanceStatus
    {
        Pending,
        Running,
        Paused,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// A running instance of a flow.
    /// </summary>
    public class FlowInstance
    {
        public string InstanceId { get; set; } = Guid.NewGuid().ToString();
        public string FlowId { get; set; }
        public FlowDefinition FlowDefinition { get; set; }

        public FlowInstanceStatus Status { get; set; } = FlowInstanceStatus.Pending;
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }

        // Current execution position
        public string CurrentNodeId { get; set; }
        public List<string> ExecutedNodes { get; set; } = new List<string>();

        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();

        public Dictionary<string, int> LoopCounters { get; set; } = new Dictionary<string, int>();

        // Outcome
        public object Result { get; set; }
        public string Error { get; set; }

        public List<FlowInstance> SubInstances { get; set; } = new List<FlowInstance>();
    }

    /// <summary>
    /// Result of a flow execution.
    /// </summary>
    public class FlowExecutionResult
    {
        public bool Success { get; set; }
        public object Result { get; set; }
        public string Error { get; set; }
        public long ExecutionTimeMs { get; set; }
        public List<FlowNode> ExecutedNodes { get; set; } = new List<FlowNode>();
    }
}