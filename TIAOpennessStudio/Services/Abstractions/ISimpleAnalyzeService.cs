using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Streams a lightweight code-analysis chat against the AI engine without the full
    /// agent execution loop.
    /// </summary>
    public interface ISimpleAnalyzeService
    {
        Task AnalyzeAsync(
            string prompt,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            Action<AgentStreamEvent> onEvent,
            CancellationToken ct = default);

        Task AnalyzeAsync(
            string prompt,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            List<string> images,
            Action<AgentStreamEvent> onEvent,
            CancellationToken ct = default);
    }
}
