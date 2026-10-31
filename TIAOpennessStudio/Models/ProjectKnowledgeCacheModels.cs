using System;

namespace TIAOpennessStudio.Models
{
    public static class ProjectKnowledgeFreshnessModes
    {
        public const string CachePreferred = "cache_preferred";
        public const string PreferFresh = "prefer_fresh";
        public const string ForceRefresh = "force_refresh";
    }

    /// <summary>Index/re-index state and progress for a project's knowledge cache.</summary>
    public sealed class ProjectKnowledgeCacheStatus
    {
        public string ProjectKey { get; set; } = string.Empty;
        public bool IsConnected { get; set; }
        public bool HasIndex { get; set; }
        public bool IsStale { get; set; }
        public bool IsIndexing { get; set; }
        public int IndexedBlockCount { get; set; }
        public int DirtyItemCount { get; set; }
        public string Status { get; set; } = "missing";
        public string LastIndexedAt { get; set; } = string.Empty;
        public string GitBranch { get; set; } = string.Empty;
        public string GitCommit { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;

        /// <summary>Rebuild progress: current phase / object being processed (e.g. "Indexing block FB_Motor...").</summary>
        public string ProgressMessage { get; set; } = string.Empty;

        /// <summary>Objects processed so far (block + type + tag table total). -1 means unknown.</summary>
        public int ProgressCurrent { get; set; } = -1;

        /// <summary>Estimated total object count. -1 means unknown (when it cannot be counted up front).</summary>
        public int ProgressTotal { get; set; } = -1;

        public DateTime? LastIndexedAtUtc
        {
            get
            {
                if (DateTime.TryParse(LastIndexedAt, out var parsed))
                {
                    return parsed;
                }

                return null;
            }
        }
    }
}
