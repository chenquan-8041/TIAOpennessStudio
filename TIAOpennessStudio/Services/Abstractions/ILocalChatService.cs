using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Streams a chat against the local model without the agent execution loop.
    /// </summary>
    public interface ILocalChatService
    {
        Task ChatAsync(
            string prompt,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            Action<AgentStreamEvent> onEvent,
            CancellationToken ct = default);

        Task ChatAsync(
            string prompt,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            List<string> images,
            Action<AgentStreamEvent> onEvent,
            CancellationToken ct = default);
    }
}
