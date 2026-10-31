using System;
using System.Collections.Generic;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Records and controls agent execution traces for debugging.
    /// </summary>
    public interface IDebugService
    {
        event EventHandler<ExecutionStep> OnStepExecuted;
        event EventHandler<string> OnBreakpointHit;
        event EventHandler<AgentExecution> OnExecutionCompleted;

        AgentExecution CreateExecution(string agentName, string prompt);
        void StartExecution(string executionId);
        void EndExecution(string executionId, string output = null);
        void CancelExecution(string executionId);
        AgentExecution GetExecution(string executionId);
        List<AgentExecution> GetAllExecutions();
        void AddStep(string executionId, ExecutionStep step);
        void AddThought(string executionId, string thought, string parentStepId = null);
        void AddAction(string executionId, string toolName, Dictionary<string, object> input, string parentStepId = null);
        void AddObservation(string executionId, object result, string parentStepId);
        void AddCondition(string executionId, string condition, bool result, string parentStepId = null);
        void AddError(string executionId, string errorMessage, string parentStepId = null);
        void SetBreakpoint(string executionId, string stepId);
        void RemoveBreakpoint(string executionId, string stepId);
        void Pause(string executionId);
        void Resume(string executionId);
        void SetWatchVariable(string executionId, string name, object value);
        Dictionary<string, object> GetWatchVariables(string executionId);
        List<ExecutionLogEntry> GetLogs(string executionId);
        List<ExecutionLogEntry> GetAllLogs();
        List<ExecutionStep> GetSlowestSteps(string executionId, int count = 5);
        void ClearExecution(string executionId);
        void ClearAll();
    }
}
