using System;

namespace TIAOpennessStudio.Models.NetworkKnowledge
{
    /// <summary>
    /// Represents a background knowledge build task.
    /// </summary>
    public sealed class BuildJob
    {
        public string JobId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 12);
        public string ProjectKey { get; set; }
        public BuildState State { get; set; } = BuildState.Queued;
        public string CurrentPhase { get; set; } = "queued";
        public string CurrentObject { get; set; }

        public int ExpectedObjects { get; set; }
        public int CompletedObjects { get; set; }
        public int WarningCount { get; set; }
        public int FailureCount { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public bool Force { get; set; }
        public string SourceVersionHash { get; set; }

        /// <summary>Reference to the candidate snapshot being built.</summary>
        public string CandidateSnapshotId { get; set; }

        /// <summary>If failed or cancelled, the reason.</summary>
        public string TerminalReason { get; set; }
    }

    public enum BuildState
    {
        Queued,
        Discovering,
        Extracting,
        Resolving,
        Enriching,
        Annotating,
        Validating,
        Publishing,
        Completed,
        Failed,
        Cancelled
    }
}
