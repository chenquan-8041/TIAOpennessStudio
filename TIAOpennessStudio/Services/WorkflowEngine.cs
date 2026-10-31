using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Workflow engine — supports conditional branching and parallel execution.
    /// </summary>
    public class WorkflowEngine : IWorkflowEngine
    {
        private readonly ISkillManager _skillManager;
        private readonly BuiltInToolsAdapter _toolsAdapter;
        private readonly Dictionary<string, FlowDefinition> _flows = new Dictionary<string, FlowDefinition>();
        private readonly Dictionary<string, FlowInstance> _runningInstances = new Dictionary<string, FlowInstance>();

        public event EventHandler<FlowNode> OnNodeEnter;
        public event EventHandler<FlowNode> OnNodeExit;
        public event EventHandler<string> OnError;

        public WorkflowEngine(ISkillManager skillManager, BuiltInToolsAdapter toolsAdapter)
        {
            _skillManager = skillManager;
            _toolsAdapter = toolsAdapter;
        }

        #region Flow management

        /// <summary>
        /// Registers a flow.
        /// </summary>
        public void RegisterFlow(FlowDefinition flow)
        {
            if (string.IsNullOrEmpty(flow.Name))
            {
                throw new ArgumentException("Flow name cannot be empty");
            }

            if (_flows.ContainsKey(flow.Name.ToLower()))
            {
                throw new InvalidOperationException($"Flow '{flow.Name}' already exists");
            }

            _flows[flow.Name.ToLower()] = flow;
        }

        /// <summary>
        /// Updates a flow.
        /// </summary>
        public void UpdateFlow(FlowDefinition flow)
        {
            _flows[flow.Name.ToLower()] = flow;
        }

        /// <summary>
        /// Deletes a flow.
        /// </summary>
        public void DeleteFlow(string flowName)
        {
            _flows.Remove(flowName.ToLower());
        }

        /// <summary>
        /// Gets a flow.
        /// </summary>
        public FlowDefinition GetFlow(string flowName)
        {
            _flows.TryGetValue(flowName.ToLower(), out var flow);
            return flow;
        }

        /// <summary>
        /// Gets all flows.
        /// </summary>
        public List<FlowDefinition> GetAllFlows()
        {
            return _flows.Values.ToList();
        }

        #endregion

        #region Flow execution

        /// <summary>
        /// Executes a flow asynchronously.
        /// </summary>
        public async Task<FlowExecutionResult> ExecuteAsync(string flowName, Dictionary<string, object> inputParams = null, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.Now;
            var result = new FlowExecutionResult();

            try
            {
                var flow = GetFlow(flowName);
                if (flow == null)
                {
                    throw new InvalidOperationException($"Flow '{flowName}' not found");
                }

                // Create the flow instance.
                var instance = new FlowInstance
                {
                    FlowId = flow.Id,
                    FlowDefinition = flow,
                    StartTime = DateTime.Now,
                    Status = FlowInstanceStatus.Running
                };

                // Initialize the context.
                if (inputParams != null)
                {
                    foreach (var param in inputParams)
                    {
                        instance.Context[param.Key] = param.Value;
                    }
                }

                // Initialize flow variables.
                foreach (var variable in flow.Variables)
                {
                    instance.Context[variable.Key] = variable.Value;
                }

                _runningInstances[instance.InstanceId] = instance;

                // Start from the initial node.
                var currentNode = flow.Nodes.FirstOrDefault(n => n.Id == flow.StartNodeId);
                
                while (currentNode != null && instance.Status == FlowInstanceStatus.Running)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        instance.Status = FlowInstanceStatus.Cancelled;
                        break;
                    }

                    // Execute the current node.
                    OnNodeEnter?.Invoke(this, currentNode);
                    
                    var nextNodeId = await ExecuteNodeAsync(instance, currentNode, cancellationToken);
                    
                    OnNodeExit?.Invoke(this, currentNode);
                    
                    result.ExecutedNodes.Add(currentNode);

                    // Advance to the next node.
                    if (!string.IsNullOrEmpty(nextNodeId))
                    {
                        currentNode = flow.Nodes.FirstOrDefault(n => n.Id == nextNodeId);
                    }
                    else
                    {
                        currentNode = null;
                    }
                }

                instance.EndTime = DateTime.Now;

                if (instance.Status == FlowInstanceStatus.Running)
                {
                    instance.Status = FlowInstanceStatus.Completed;
                }

                result.Success = instance.Status == FlowInstanceStatus.Completed;
                result.Result = instance.Result;
                result.Error = instance.Error;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                OnError?.Invoke(this, ex.Message);
            }
            finally
            {
                result.ExecutionTimeMs = (long)(DateTime.Now - startTime).TotalMilliseconds;
            }

            return result;
        }

        /// <summary>
        /// Executes a single node.
        /// </summary>
        private async Task<string> ExecuteNodeAsync(FlowInstance instance, FlowNode node, CancellationToken cancellationToken)
        {
            instance.CurrentNodeId = node.Id;
            instance.ExecutedNodes.Add(node.Id);

            switch (node.Type)
            {
                case NodeType.Start:
                    return node.NextNodes.FirstOrDefault();

                case NodeType.End:
                    instance.Status = FlowInstanceStatus.Completed;
                    return null;

                case NodeType.Action:
                    return await ExecuteActionNodeAsync(instance, node, cancellationToken);

                case NodeType.Condition:
                    return ExecuteConditionNode(instance, node);

                case NodeType.Parallel:
                    return await ExecuteParallelNodeAsync(instance, node, cancellationToken);

                case NodeType.Loop:
                    return ExecuteLoopNode(instance, node);

                case NodeType.Switch:
                    return ExecuteSwitchNode(instance, node);

                case NodeType.TryCatch:
                    return await ExecuteTryCatchNodeAsync(instance, node, cancellationToken);

                case NodeType.Delay:
                    return await ExecuteDelayNodeAsync(instance, node, cancellationToken);

                default:
                    return node.NextNodes.FirstOrDefault();
            }
        }

        #endregion

        #region Node execution

        /// <summary>
        /// Executes an action node.
        /// </summary>
        private async Task<string> ExecuteActionNodeAsync(FlowInstance instance, FlowNode node, CancellationToken cancellationToken)
        {
            var skillName = node.Properties.ContainsKey("skill") ? node.Properties["skill"].ToString() : null;
            var toolName = node.Properties.ContainsKey("tool") ? node.Properties["tool"].ToString() : null;
            
            object actionResult = null;

            try
            {
                if (!string.IsNullOrEmpty(skillName))
                {
                    // Execute the skill.
                    var parameters = ResolveParameters(node.Properties, instance.Context);
                    var skillResult = _skillManager.ExecuteSkill(skillName, parameters);
                    actionResult = skillResult.Result;
                }
                else if (!string.IsNullOrEmpty(toolName))
                {
                    // Execute the built-in tool.
                    var parameters = ResolveParameters(node.Properties, instance.Context);
                    var toolResult = await _toolsAdapter.ExecuteToolAsync(toolName, parameters);
                    actionResult = toolResult.Result;
                }

                // Store the result in the context.
                var resultVarName = node.Properties.ContainsKey("resultVariable") 
                    ? node.Properties["resultVariable"].ToString() 
                    : "actionResult";
                instance.Context[resultVarName] = actionResult;
            }
            catch (Exception ex)
            {
                instance.Error = ex.Message;
                instance.Status = FlowInstanceStatus.Failed;
                OnError?.Invoke(this, $"Node '{node.Name}' error: {ex.Message}");
            }

            return node.NextNodes.FirstOrDefault();
        }

        /// <summary>
        /// Executes a condition node.
        /// </summary>
        private string ExecuteConditionNode(FlowInstance instance, FlowNode node)
        {
            try
            {
                var condition = node.ConditionExpression;
                
                // Resolve variables in the condition expression.
                foreach (var key in instance.Context.Keys)
                {
                    condition = condition.Replace($"{{{key}}}", instance.Context[key]?.ToString() ?? "null");
                }

                // Evaluate the condition.
                var dt = new DataTable();
                var result = dt.Compute(condition, null);
                var conditionResult = Convert.ToBoolean(result);

                // Store the condition result in the context.
                instance.Context[$"{node.Id}_condition"] = conditionResult;

                // Return the next node based on the result.
                if (conditionResult && node.NextNodes.Count > 0)
                {
                    return node.NextNodes[0];
                }
                else if (!conditionResult && node.NextNodes.Count > 1)
                {
                    return node.NextNodes[1];
                }

                return node.DefaultNextNode;
            }
            catch (Exception ex)
            {
                instance.Error = $"Condition evaluation error: {ex.Message}";
                return node.DefaultNextNode;
            }
        }

        /// <summary>
        /// Executes a parallel node.
        /// </summary>
        private async Task<string> ExecuteParallelNodeAsync(FlowInstance instance, FlowNode node, CancellationToken cancellationToken)
        {
            var parallelNodes = node.ParallelNodes;
            var mode = node.ParallelMode;

            // Resolve the node definitions to run in parallel.
            var flow = instance.FlowDefinition;
            var nodeDefs = parallelNodes.Select(id => flow.Nodes.FirstOrDefault(n => n.Id == id)).ToList();

            // Run in parallel.
            var tasks = nodeDefs.Select(n => ExecuteNodeAsync(instance, n, cancellationToken)).ToList();
            var results = await Task.WhenAll(tasks);

            switch (mode)
            {
                case ParallelMode.All:
                    // Continue only after all finish.
                    return node.NextNodes.FirstOrDefault();

                case ParallelMode.Any:
                    // Continue once any one finishes.
                    return node.NextNodes.FirstOrDefault();

                case ParallelMode.Race:
                    // Continue with the first to finish.
                    return node.NextNodes.FirstOrDefault();

                default:
                    return node.NextNodes.FirstOrDefault();
            }
        }

        /// <summary>
        /// Executes a loop node.
        /// </summary>
        private string ExecuteLoopNode(FlowInstance instance, FlowNode node)
        {
            var loopVar = node.LoopVariable ?? "loopIndex";
            var maxCount = node.MaxLoopCount;

            // Get or initialize the loop counter.
            if (!instance.LoopCounters.ContainsKey(loopVar))
            {
                instance.LoopCounters[loopVar] = 0;
            }

            var currentCount = instance.LoopCounters[loopVar];

            if (currentCount < maxCount)
            {
                // Increment the counter.
                instance.LoopCounters[loopVar] = currentCount + 1;
                instance.Context[loopVar] = currentCount;

                // Return the first node of the loop body.
                return node.NextNodes.FirstOrDefault();
            }
            else
            {
                // Loop finished.
                return node.DefaultNextNode;
            }
        }

        /// <summary>
        /// Executes a Switch node.
        /// </summary>
        private string ExecuteSwitchNode(FlowInstance instance, FlowNode node)
        {
            var switchValue = node.Properties.ContainsKey("value") 
                ? node.Properties["value"].ToString() 
                : null;

            // Resolve the variable.
            if (!string.IsNullOrEmpty(switchValue) && switchValue.StartsWith("{") && switchValue.EndsWith("}"))
            {
                var varName = switchValue.Substring(1, switchValue.Length - 2);
                switchValue = instance.Context.ContainsKey(varName) 
                    ? instance.Context[varName]?.ToString() 
                    : null;
            }

            // Find the matching branch.
            var caseIndex = 0;
            foreach (var caseValue in node.Properties.Values)
            {
                if (caseValue?.ToString() == switchValue)
                {
                    if (caseIndex < node.NextNodes.Count)
                    {
                        return node.NextNodes[caseIndex];
                    }
                }
                caseIndex++;
            }

            // Return the default branch.
            return node.DefaultNextNode;
        }

        /// <summary>
        /// Executes a TryCatch node.
        /// </summary>
        private Task<string> ExecuteTryCatchNodeAsync(FlowInstance instance, FlowNode node, CancellationToken cancellationToken)
        {
            try
            {
                // Run the Try part.
                if (node.NextNodes.Count > 0)
                {
                    return Task.FromResult(node.NextNodes[0]);
                }
            }
            catch (Exception ex)
            {
                // Store the error.
                var errorVar = node.ErrorVariable ?? "error";
                instance.Context[errorVar] = ex.Message;

                // Run the Catch part.
                if (node.CatchNodes.Count > 0)
                {
                    return Task.FromResult(node.CatchNodes[0]);
                }
            }

            // Run the Finally part.
            if (node.FinallyNodes.Count > 0)
            {
                return Task.FromResult(node.FinallyNodes[0]);
            }

            return Task.FromResult(node.DefaultNextNode);
        }

        /// <summary>
        /// Executes a delay node.
        /// </summary>
        private async Task<string> ExecuteDelayNodeAsync(FlowInstance instance, FlowNode node, CancellationToken cancellationToken)
        {
            var delayMs = 1000;
            if (node.Properties.ContainsKey("delay"))
            {
                var delayValue = node.Properties["delay"];
                if (delayValue is int)
                {
                    delayMs = (int)delayValue;
                }
                else if (int.TryParse(delayValue?.ToString(), out var parsed))
                {
                    delayMs = parsed;
                }
            }

            await Task.Delay(delayMs, cancellationToken);
            return node.NextNodes.FirstOrDefault();
        }

        #endregion

        #region Helper methods

        /// <summary>
        /// Resolves parameters.
        /// </summary>
        private Dictionary<string, object> ResolveParameters(Dictionary<string, object> properties, Dictionary<string, object> context)
        {
            var resolved = new Dictionary<string, object>();

            foreach (var prop in properties)
            {
                if (prop.Value is string strValue)
                {
                    // Substitute variables.
                    foreach (var key in context.Keys)
                    {
                        strValue = strValue.Replace($"{{{key}}}", context[key]?.ToString() ?? "");
                    }
                    resolved[prop.Key] = strValue;
                }
                else
                {
                    resolved[prop.Key] = prop.Value;
                }
            }

            return resolved;
        }

        /// <summary>
        /// Cancels execution.
        /// </summary>
        public void CancelExecution(string instanceId)
        {
            if (_runningInstances.TryGetValue(instanceId, out var instance))
            {
                instance.Status = FlowInstanceStatus.Cancelled;
            }
        }

        /// <summary>
        /// Gets running instances.
        /// </summary>
        public List<FlowInstance> GetRunningInstances()
        {
            return _runningInstances.Values.Where(i => i.Status == FlowInstanceStatus.Running).ToList();
        }

        #endregion
    }
}