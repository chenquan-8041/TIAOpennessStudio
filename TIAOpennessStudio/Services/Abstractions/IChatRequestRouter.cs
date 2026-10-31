using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Routes a chat request to the appropriate backend (local vs. cloud) based on
    /// capability and configuration.
    /// </summary>
    public interface IChatRequestRouter
    {
        Task<ChatRouteResult> RouteAsync(
            string prompt,
            IReadOnlyList<AiReferenceItem> references = null,
            IReadOnlyList<Dictionary<string, string>> history = null,
            CancellationToken ct = default);
    }
}
