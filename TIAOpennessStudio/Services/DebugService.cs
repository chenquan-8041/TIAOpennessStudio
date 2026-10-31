using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Agent debugging service — provides execution tracing, step execution, and
    /// log replay.
    /// </summary>
    public class DebugService : IDebugService
    {
        private readonly Dictionary<string, AgentExecution> _executions = new Dictionary<string, AgentExecution>();
        private readonly Dictionary<string, DebugSession> _sessions = new Dictionary<string, DebugSession>();
        private AgentExecution _currentExecution;
        private DebugSession _currentSession;

        public event EventHandler<ExecutionStep> OnStepExecuted;
        public event EventHandler<string> OnBreakpointHit;
        public event EventHandler<AgentExecution> OnExecutionCompleted;

        #region Execution management

        /// <summary>
        /// Creates a new execution trace.
        /// </summary>
        public AgentExecution CreateExecution(string agentName, string prompt)
        {
            var execution = new AgentExecution
            {
                AgentName = agentName,
                InputPrompt = prompt,
                Status = ExecutionStatus.Pending
            };

            _executions[execution.ExecutionId] = execution;
            return execution;
        }

        /// <summary>
        /// Starts execution.
        /// </summary>
        public void StartExecution(string executionId)
        {
            if (_executions.TryGetValue(executionId, out var execution))
            {
                execution.Status = ExecutionStatus.Running;
                execution.StartTime = DateTime.Now;
                _currentExecution = execution;

                // Create the debug session.
                _currentSession = new DebugSession
                {
                    ExecutionId = executionId
                };
                _sessions[executionId] = _currentSession;

                _currentSession.AddLog("Info", $"Execution started: {execution.AgentName}", "DebugService");
            }
        }

        /// <summary>
        /// Ends execution.
        /// </summary>
        public void EndExecution(string executionId, string output = null)
        {
            if (_executions.TryGetValue(executionId, out var execution))
            {
                execution.EndTime = DateTime.Now;
                execution.Output = output;

                if (execution.Status == ExecutionStatus.Running)
                {
                    execution.Status = string.IsNullOrEmpty(execution.ErrorMessage) 
                        ? ExecutionStatus.Completed 
                        : ExecutionStatus.Failed;
                }

                _currentSession?.AddLog("Info", $"Execution completed. Status: {execution.Status}", "DebugService");
                OnExecutionCompleted?.Invoke(this, execution);
            }
        }

        /// <summary>
        /// Cancels execution.
        /// </summary>
        public void CancelExecution(string executionId)
        {
            if (_executions.TryGetValue(executionId, out var execution))
            {
                execution.Status = ExecutionStatus.Cancelled;
                execution.EndTime = DateTime.Now;
                _currentSession?.AddLog("Warning", "Execution cancelled by user", "DebugService");
            }
        }

        /// <summary>
        /// Gets an execution.
        /// </summary>
        public AgentExecution GetExecution(string executionId)
        {
            _executions.TryGetValue(executionId, out var execution);
            return execution;
        }

        /// <summary>
        /// Gets all executions.
        /// </summary>
        public List<AgentExecution> GetAllExecutions()
        {
            return _executions.Values.OrderByDescending(e => e.StartTime).ToList();
        }

        #endregion

        #region Step execution

        /// <summary>
        /// Adds an execution step.
        /// </summary>
        public void AddStep(string executionId, ExecutionStep step)
        {
            if (_executions.TryGetValue(executionId, out var execution))
            {
                execution.AddStep(step);
                _currentSession?.AddLog("Debug", $"Step {step.StepNumber}: {step.StepType} - {step.Content?.Substring(0, Math.Min(50, step.Content?.Length ?? 0))}...", "Execution");

                // Check whether this step is a breakpoint.
                if (_currentSession != null && _currentSession.Breakpoints.Contains(step.StepId))
                {
                    _currentSession.IsPaused = true;
                    _currentSession.CurrentStepId = step.StepId;
                    OnBreakpointHit?.Invoke(this, step.StepId);
                }

                OnStepExecuted?.Invoke(this, step);
            }
        }

        /// <summary>
        /// Adds a thought step.
        /// </summary>
        public void AddThought(string executionId, string thought, string parentStepId = null)
        {
            AddStep(executionId, new ExecutionStep
            {
                StepType = StepType.Thought,
                Content = thought,
                ParentStepId = parentStepId
            });
        }

        /// <summary>
        /// Adds an action step (tool call).
        /// </summary>
        public void AddAction(string executionId, string toolName, Dictionary<string, object> input, string parentStepId = null)
        {
            AddStep(executionId, new ExecutionStep
            {
                StepType = StepType.Action,
                ToolName = toolName,
                ToolInput = input,
                Content = $"Calling tool: {toolName}",
                ParentStepId = parentStepId
            });
        }

        /// <summary>
        /// Adds an observation step (tool result).
        /// </summary>
        public void AddObservation(string executionId, object result, string parentStepId)
        {
            AddStep(executionId, new ExecutionStep
            {
                StepType = StepType.Observation,
                ToolOutput = result,
                Content = $"Tool result received",
                ParentStepId = parentStepId
            });
        }
        /// <summary>
        /// Adds a condition step.
        /// </summary>
        public void AddCondition(string executionId, string condition, bool result, string parentStepId = null)
        {
            AddStep(executionId, new ExecutionStep
            {
                StepType = StepType.Condition,
                Condition = condition,
                ConditionResult = result,
                Content = $"Condition: {condition} = {result}",
                ParentStepId = parentStepId
            });
        }

        /// <summary>
        /// Adds an error step.
        /// </summary>
        public void AddError(string executionId, string errorMessage, string parentStepId = null)
        {
            AddStep(executionId, new ExecutionStep
            {
                StepType = StepType.Error,
                ErrorMessage = errorMessage,
                Content = $"Error: {errorMessage}",
                ParentStepId = parentStepId
            });

            if (_executions.TryGetValue(executionId, out var execution))
            {
                execution.ErrorMessage = errorMessage;
            }
        }

        #endregion

        #region Debug control

        /// <summary>
        /// Sets a breakpoint.
        /// </summary>
        public void SetBreakpoint(string executionId, string stepId)
        {
            if (_sessions.TryGetValue(executionId, out var session))
            {
                if (!session.Breakpoints.Contains(stepId))
                {
                    session.Breakpoints.Add(stepId);
                    _currentSession?.AddLog("Info", $"Breakpoint set at step: {stepId}", "DebugService");
                }
            }
        }

        /// <summary>
        /// Removes a breakpoint.
        /// </summary>
        public void RemoveBreakpoint(string executionId, string stepId)
        {
            if (_sessions.TryGetValue(executionId, out var session))
            {
                session.Breakpoints.Remove(stepId);
                _currentSession?.AddLog("Info", $"Breakpoint removed from step: {stepId}", "DebugService");
            }
        }

        /// <summary>
        /// Pauses execution.
        /// </summary>
        public void Pause(string executionId)
        {
            if (_sessions.TryGetValue(executionId, out var session))
            {
                session.IsPaused = true;
                _currentSession?.AddLog("Info", "Execution paused", "DebugService");
            }
        }

        /// <summary>
        /// Resumes execution.
        /// </summary>
        public void Resume(string executionId)
        {
            if (_sessions.TryGetValue(executionId, out var session))
            {
                session.IsPaused = false;
                _currentSession?.AddLog("Info", "Execution resumed", "DebugService");
            }
        }

        /// <summary>
        /// Steps over.
        /// </summary>
        public async Task StepOverAsync(string executionId)
        {
            if (_sessions.TryGetValue(executionId, out var session))
            {
                session.IsPaused = true;
                session.CurrentStepId = _currentExecution?.Steps.LastOrDefault()?.StepId;
                _currentSession?.AddLog("Info", "Step over - execution paused", "DebugService");
                
                // Wait for resume.
                while (session.IsPaused)
                {
                    await Task.Delay(100);
                }
            }
        }

        #endregion

        #region Variable watch

        /// <summary>
        /// Sets a watch variable.
        /// </summary>
        public void SetWatchVariable(string executionId, string name, object value)
        {
            if (_sessions.TryGetValue(executionId, out var session))
            {
                session.WatchVariables[name] = value;
            }
        }

        /// <summary>
        /// Gets watch variables.
        /// </summary>
        public Dictionary<string, object> GetWatchVariables(string executionId)
        {
            if (_sessions.TryGetValue(executionId, out var session))
            {
                return session.WatchVariables;
            }
            return new Dictionary<string, object>();
        }

        #endregion

        #region Log replay

        /// <summary>
        /// Gets execution logs.
        /// </summary>
        public List<ExecutionLogEntry> GetLogs(string executionId)
        {
            if (_sessions.TryGetValue(executionId, out var session))
            {
                return session.Logs;
            }
            return new List<ExecutionLogEntry>();
        }

        /// <summary>
        /// Gets logs across all executions.
        /// </summary>
        public List<ExecutionLogEntry> GetAllLogs()
        {
            var allLogs = new List<ExecutionLogEntry>();
            foreach (var session in _sessions.Values)
            {
                allLogs.AddRange(session.Logs);
            }
            return allLogs.OrderByDescending(l => l.Timestamp).ToList();
        }

        /// <summary>
        /// Replays an execution.
        /// </summary>
        public async Task ReplayExecutionAsync(string executionId, int speed = 1)
        {
            var execution = GetExecution(executionId);
            if (execution == null) return;

            // Create a new debug session for replay.
            var replaySession = new DebugSession
            {
                ExecutionId = executionId
            };
            _sessions[$"replay_{executionId}"] = replaySession;

            foreach (var step in execution.Steps)
            {
                OnStepExecuted?.Invoke(this, step);
                
                // Delay according to speed.
                if (speed > 0)
                {
                    await Task.Delay(Math.Max(10, 1000 / speed));
                }
            }
        }

        #endregion

        #region Performance profiling

        /// <summary>
        /// Gets execution statistics.
        /// </summary>
        public object GetExecutionStats(string executionId)
        {
            var execution = GetExecution(executionId);
            if (execution == null) return null;

            var toolCalls = execution.Steps.FindAll(s => s.StepType == StepType.Action);
            var avgToolTime = toolCalls.Count > 0 
                ? toolCalls.Average(s => s.DurationMs) 
                : 0;

            return new
            {
                execution.ExecutionId,
                execution.AgentName,
                execution.Status,
                TotalSteps = execution.TotalSteps,
                ToolCalls = execution.ToolCalls,
                TotalDurationMs = execution.TotalDurationMs,
                AverageToolCallTime = avgToolTime,
                StartTime = execution.StartTime,
                EndTime = execution.EndTime
            };
        }

        /// <summary>
        /// Gets the slowest steps.
        /// </summary>
        public List<ExecutionStep> GetSlowestSteps(string executionId, int count = 5)
        {
            var execution = GetExecution(executionId);
            if (execution == null) return new List<ExecutionStep>();

            return execution.Steps
                .OrderByDescending(s => s.DurationMs)
                .Take(count)
                .ToList();
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Clears an execution record.
        /// </summary>
        public void ClearExecution(string executionId)
        {
            _executions.Remove(executionId);
            _sessions.Remove(executionId);
        }

        /// <summary>
        /// Clears all records.
        /// </summary>
        public void ClearAll()
        {
            _executions.Clear();
            _sessions.Clear();
            _currentExecution = null;
            _currentSession = null;
        }

        #endregion
    }
}
