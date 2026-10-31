namespace TIAOpennessStudio.Models
{
    public sealed class ProjectKnowledgeWorkerStatus
    {
        public bool IsHealthy { get; set; }
        public bool IsProcessRunning { get; set; }
        public string ProcessState { get; set; } = "offline";
        public string TiaState { get; set; } = "detached";
        public string ProjectKey { get; set; } = string.Empty;
        public bool HasIndex { get; set; }
        public bool IsStale { get; set; }
        public bool IsIndexing { get; set; }
        public int DirtyCount { get; set; }
        public int DirtyItemCount { get; set; }
        public int IndexedBlockCount { get; set; }
        public string LastIndexedAt { get; set; } = string.Empty;
        public string GitBranch { get; set; } = string.Empty;
        public string GitCommit { get; set; } = string.Empty;
        public string CachePath { get; set; } = string.Empty;
        public string WorkerBaseUrl { get; set; } = string.Empty;
        public int OwnedProcessId { get; set; }
        public string Note { get; set; } = string.Empty;
    }

    /// <summary>
    /// The project the knowledge worker is currently attached to, plus basic index state.
    /// Returned by the Worker's GET /query/current-project endpoint. ProjectKey is null
    /// when no real project is attached.
    /// </summary>
    public sealed class WorkerCurrentProjectInfo
    {
        public bool Ok { get; set; }
        public string ProjectKey { get; set; }
        public string ProjectName { get; set; }
        public bool IsConnected { get; set; }
        public bool HasIndex { get; set; }
        public bool IsStale { get; set; }
        public bool IsIndexing { get; set; }
    }
}
