using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models.NetworkKnowledge;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Manages Candidate/Published snapshot lifecycle, build jobs, and atomic publishing.
    /// Fulfills Requirements 10, 11, 12 from the spec.
    /// </summary>
    public sealed class ProjectSnapshotManager
    {
        private readonly object _lock = new object();
        private readonly INetworkDocumentStore _networkStore;
        private readonly PlcNetworkFactExtractor _extractor;

        // Active state per project
        private readonly Dictionary<string, ProjectSnapshot> _publishedSnapshots = new Dictionary<string, ProjectSnapshot>();
        private readonly Dictionary<string, ProjectSnapshot> _candidateSnapshots = new Dictionary<string, ProjectSnapshot>();
        private readonly Dictionary<string, BuildJob> _activeJobs = new Dictionary<string, BuildJob>();
        private readonly Dictionary<string, Queue<BuildJob>> _pendingJobs = new Dictionary<string, Queue<BuildJob>>();

        public ProjectSnapshotManager(INetworkDocumentStore networkStore, PlcNetworkFactExtractor extractor = null)
        {
            _networkStore = networkStore ?? throw new ArgumentNullException(nameof(networkStore));
            _extractor = extractor ?? new PlcNetworkFactExtractor();
        }

        /// <summary>Get the current published snapshot for queries. May be null if none published yet.</summary>
        public ProjectSnapshot GetPublishedSnapshot(string projectKey)
        {
            lock (_lock)
            {
                _publishedSnapshots.TryGetValue(projectKey, out var snap);
                return snap;
            }
        }

        /// <summary>Get the current candidate (building) snapshot.</summary>
        public ProjectSnapshot GetCandidateSnapshot(string projectKey)
        {
            lock (_lock)
            {
                _candidateSnapshots.TryGetValue(projectKey, out var snap);
                return snap;
            }
        }

        /// <summary>Get active build job for a project.</summary>
        public BuildJob GetActiveBuildJob(string projectKey)
        {
            lock (_lock)
            {
                _activeJobs.TryGetValue(projectKey, out var job);
                return job;
            }
        }

        /// <summary>
        /// Start or queue a build job. Returns immediately with job ID per Requirement 10.
        /// </summary>
        public BuildJob RequestBuild(string projectKey, bool force = false, string sourceVersionHash = null)
        {
            lock (_lock)
            {
                // Check if existing active job covers the same version
                if (_activeJobs.TryGetValue(projectKey, out var existing))
                {
                    if (!force && existing.SourceVersionHash == sourceVersionHash)
                        return existing; // Merge: same version, reuse

                    // Queue behind active
                    var queued = CreateJob(projectKey, force, sourceVersionHash);
                    if (!_pendingJobs.ContainsKey(projectKey))
                        _pendingJobs[projectKey] = new Queue<BuildJob>();
                    _pendingJobs[projectKey].Enqueue(queued);
                    return queued;
                }

                var job = CreateJob(projectKey, force, sourceVersionHash);
                _activeJobs[projectKey] = job;

                // Create candidate snapshot
                var candidate = new ProjectSnapshot
                {
                    SnapshotId = Guid.NewGuid().ToString("N").Substring(0, 12),
                    ProjectKey = projectKey,
                    State = SnapshotState.Building
                };
                _candidateSnapshots[projectKey] = candidate;
                job.CandidateSnapshotId = candidate.SnapshotId;

                return job;
            }
        }

        /// <summary>Cancel an active or queued build job.</summary>
        public bool CancelBuild(string projectKey, string jobId)
        {
            lock (_lock)
            {
                if (_activeJobs.TryGetValue(projectKey, out var active) && active.JobId == jobId)
                {
                    active.State = BuildState.Cancelled;
                    active.TerminalReason = "Cancelled by user";
                    active.LastUpdatedAtUtc = DateTime.UtcNow;
                    _activeJobs.Remove(projectKey);
                    _candidateSnapshots.Remove(projectKey);
                    PromoteNextJob(projectKey);
                    return true;
                }

                if (_pendingJobs.TryGetValue(projectKey, out var queue))
                {
                    var remaining = new Queue<BuildJob>(queue.Where(j => j.JobId != jobId));
                    _pendingJobs[projectKey] = remaining;
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Record discovery completion: freeze object list and transition to extracting.
        /// </summary>
        public void CompleteDiscovery(string projectKey, Dictionary<string, string> objectVersions, int objectCount)
        {
            lock (_lock)
            {
                if (!_activeJobs.TryGetValue(projectKey, out var job)) return;
                if (!_candidateSnapshots.TryGetValue(projectKey, out var candidate)) return;

                candidate.ObjectVersions = objectVersions ?? new Dictionary<string, string>();
                job.ExpectedObjects = objectCount;
                job.State = BuildState.Extracting;
                job.CurrentPhase = "extracting";
                job.LastUpdatedAtUtc = DateTime.UtcNow;

                candidate.Coverage.Discovery.Expected = objectCount;
                candidate.Coverage.Discovery.Completed = objectCount;
            }
        }

        /// <summary>
        /// Record that a block's networks have been extracted.
        /// </summary>
        public void RecordBlockExtracted(string projectKey, string blockId, List<PlcNetworkDocument> networks, bool success)
        {
            lock (_lock)
            {
                if (!_activeJobs.TryGetValue(projectKey, out var job)) return;
                if (!_candidateSnapshots.TryGetValue(projectKey, out var candidate)) return;

                if (success)
                {
                    job.CompletedObjects++;
                    candidate.Coverage.NetworkExtraction.Completed++;
                    candidate.Networks.AddRange(networks);
                }
                else
                {
                    job.FailureCount++;
                    candidate.Coverage.NetworkExtraction.Failed++;
                }

                job.CurrentObject = blockId;
                job.LastUpdatedAtUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Transition to global resolution phase.
        /// </summary>
        public void BeginGlobalResolution(string projectKey)
        {
            lock (_lock)
            {
                if (!_activeJobs.TryGetValue(projectKey, out var job)) return;
                job.State = BuildState.Resolving;
                job.CurrentPhase = "resolving";
                job.LastUpdatedAtUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Record resolved relations.
        /// </summary>
        public void RecordRelationsResolved(string projectKey, List<GlobalRelationEdge> relations)
        {
            lock (_lock)
            {
                if (!_candidateSnapshots.TryGetValue(projectKey, out var candidate)) return;
                candidate.Relations.AddRange(relations);

                candidate.Coverage.GlobalResolution.Expected = relations.Count;
                candidate.Coverage.GlobalResolution.Completed = relations.Count(r => r.Resolution == ResolutionStatus.Resolved);
                candidate.Coverage.GlobalResolution.Warnings = relations.Count(r => r.Resolution != ResolutionStatus.Resolved);
            }
        }

        /// <summary>
        /// Validate and attempt to publish the candidate snapshot atomically.
        /// Returns true if published, false if blocked.
        /// </summary>
        public bool TryPublish(string projectKey)
        {
            lock (_lock)
            {
                if (!_activeJobs.TryGetValue(projectKey, out var job)) return false;
                if (!_candidateSnapshots.TryGetValue(projectKey, out var candidate)) return false;

                job.State = BuildState.Validating;
                job.CurrentPhase = "validating";
                job.LastUpdatedAtUtc = DateTime.UtcNow;

                // Validate: no fatal errors, extraction complete
                if (candidate.FatalErrors.Count > 0)
                {
                    job.State = BuildState.Failed;
                    job.TerminalReason = "Fatal errors: " + string.Join("; ", candidate.FatalErrors.Take(3));
                    candidate.State = SnapshotState.Failed;
                    _activeJobs.Remove(projectKey);
                    PromoteNextJob(projectKey);
                    return false;
                }

                // Determine completeness
                var hasWarnings = candidate.Warnings.Count > 0
                    || candidate.Coverage.GlobalResolution.Warnings > 0
                    || candidate.Coverage.SemanticAnnotation.Failed > 0;

                candidate.Completeness = hasWarnings
                    ? CompletenessStatus.CompleteWithWarnings
                    : CompletenessStatus.Complete;

                // Atomic publish
                candidate.State = SnapshotState.Published;
                candidate.PublishedAtUtc = DateTime.UtcNow;
                _publishedSnapshots[projectKey] = candidate;
                _candidateSnapshots.Remove(projectKey);

                job.State = BuildState.Completed;
                job.CurrentPhase = "completed";
                job.LastUpdatedAtUtc = DateTime.UtcNow;
                _activeJobs.Remove(projectKey);

                PromoteNextJob(projectKey);
                return true;
            }
        }

        /// <summary>Record a fatal build error that blocks publishing.</summary>
        public void RecordFatalError(string projectKey, string error)
        {
            lock (_lock)
            {
                if (_candidateSnapshots.TryGetValue(projectKey, out var candidate))
                    candidate.FatalErrors.Add(error);
                if (_activeJobs.TryGetValue(projectKey, out var job))
                {
                    job.FailureCount++;
                    job.LastUpdatedAtUtc = DateTime.UtcNow;
                }
            }
        }

        /// <summary>Record a non-fatal warning.</summary>
        public void RecordWarning(string projectKey, string category, string phase, string affectedId, string reason, string evidence = null)
        {
            lock (_lock)
            {
                if (_candidateSnapshots.TryGetValue(projectKey, out var candidate))
                {
                    candidate.Warnings.Add(new BuildWarning
                    {
                        Category = category,
                        Phase = phase,
                        AffectedId = affectedId,
                        Reason = reason,
                        Evidence = evidence
                    });
                }
                if (_activeJobs.TryGetValue(projectKey, out var job))
                {
                    job.WarningCount++;
                    job.LastUpdatedAtUtc = DateTime.UtcNow;
                }
            }
        }

        private void PromoteNextJob(string projectKey)
        {
            if (_pendingJobs.TryGetValue(projectKey, out var queue) && queue.Count > 0)
            {
                var next = queue.Dequeue();
                _activeJobs[projectKey] = next;
                next.State = BuildState.Discovering;
                next.CurrentPhase = "discovering";
                next.LastUpdatedAtUtc = DateTime.UtcNow;

                var candidate = new ProjectSnapshot
                {
                    SnapshotId = Guid.NewGuid().ToString("N").Substring(0, 12),
                    ProjectKey = projectKey,
                    State = SnapshotState.Building
                };
                _candidateSnapshots[projectKey] = candidate;
                next.CandidateSnapshotId = candidate.SnapshotId;
            }
        }

        private static BuildJob CreateJob(string projectKey, bool force, string sourceVersionHash)
        {
            return new BuildJob
            {
                ProjectKey = projectKey,
                Force = force,
                SourceVersionHash = sourceVersionHash ?? "",
                State = BuildState.Discovering,
                CurrentPhase = "discovering"
            };
        }
    }
}
