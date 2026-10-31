using System;
using System.Collections.Generic;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Runs registered workflows and reports node transitions and errors.
    /// </summary>
    public interface IWorkflowEngine
    {
        event EventHandler<FlowNode> OnNodeEnter;
        event EventHandler<FlowNode> OnNodeExit;
        event EventHandler<string> OnError;

        void RegisterFlow(FlowDefinition flow);
        void UpdateFlow(FlowDefinition flow);
        void DeleteFlow(string flowName);
        FlowDefinition GetFlow(string flowName);
        List<FlowDefinition> GetAllFlows();
        void CancelExecution(string instanceId);
        List<FlowInstance> GetRunningInstances();
    }
}
