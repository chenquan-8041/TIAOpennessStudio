using System;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Thin adapter over the v3 <see cref="PlcKnowledgeIndexer"/> (single-writer,
    /// layered knowledge store). Kept so existing callers of
    /// <see cref="IPlcProjectIndexingService"/> (ProjectKnowledgeCacheService) work
    /// unchanged. All rebuild logic lives in PlcKnowledgeIndexer.
    /// </summary>
    public sealed class PlcProjectIndexingService : IPlcProjectIndexingService
    {
        private readonly PlcKnowledgeIndexer _knowledgeIndexer;

        public PlcProjectIndexingService(PlcKnowledgeIndexer knowledgeIndexer)
        {
            _knowledgeIndexer = knowledgeIndexer ?? throw new ArgumentNullException(nameof(knowledgeIndexer));
        }

        public Task<PlcProjectIndexBuildResult> RebuildAsync(bool force = false, Action<string> onProgress = null, CancellationToken ct = default)
            => _knowledgeIndexer.RebuildAsync(force, onProgress, ct);
    }
}
