using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Implementations;

namespace TIAOpennessStudio.Services.Abstractions
{
    public interface IReActLoopEngine
    {
        string ModelName { get; }
        string ModelProvider { get; }
        Task<ReActResult> ExecuteAsync(
            string prompt, string context = "", List<Dictionary<string, string>> history = null,
            Action<AgentStreamEvent> onEvent = null,
            Func<string, string, Task<bool>> onConfirmRequired = null,
            CancellationToken ct = default, ExecutionPlan plan = null);
    }
}