using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Implementations;

namespace TIAOpennessStudio.Services.Abstractions
{
    public interface ICloudAgentEngine
    {
        Task<ReActResult> ExecuteCloudAsync(
            string prompt, string context = "", List<Dictionary<string, string>> history = null,
            Action<AgentStreamEvent> onEvent = null, CancellationToken ct = default,
            string planningKnowledge = "", string projectKey = "", string intent = "");
        Task<ReActResult> ExecuteChatPlanAsync(
            string prompt, string context = "", List<Dictionary<string, string>> history = null,
            Action<AgentStreamEvent> onEvent = null, CancellationToken ct = default,
            string planningKnowledge = "", string intent = "", string projectKey = "");
    }
}