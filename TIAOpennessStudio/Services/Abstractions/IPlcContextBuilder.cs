using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Builds context packs and related-context snippets for prompt injection.
    /// </summary>
    public interface IPlcContextBuilder
    {
        ContextPack BuildContextPack(string projectKey, string query, string rid = null, int topK = 5);
        string BuildRelatedContext(string projectKey, string query, int remainingTokens, int topK = 5);
    }
}
