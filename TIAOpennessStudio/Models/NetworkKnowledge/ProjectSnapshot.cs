using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Models.NetworkKnowledge
{
    /// <summary>
    /// Represents an immutable project knowledge snapshot.
    /// Only a Published snapshot is used by UI/RAG/Agent queries.
    /// </summary>
    public sealed class ProjectSnapshot
    {
        public string SnapshotId { get; set; }
        public string ProjectKey { get; set; }
        public SnapshotState State { get; set; } = SnapshotState.Building;
        public CompletenessStatus Completeness { get; set; } = CompletenessStatus.Incomplete;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? PublishedAtUtc { get; set; }

        /// <summary>Source versions frozen at discovery time.</summary>
        public Dictionary<string, string> ObjectVersions { get; set; } = new Dictionary<string, string>();

        /// <summary>All Network documents in this snapshot.</summary>
        public List<PlcNetworkDocument> Networks { get; set; } = new List<PlcNetworkDocument>();

        /// <summary>Global relations resolved after full discovery.</summary>
        public List<GlobalRelationEdge> Relations { get; set; } = new List<GlobalRelationEdge>();

        /// <summary>Coverage tracking per build phase.</summary>
        public CoverageManifest Coverage { get; set; } = new CoverageManifest();

        /// <summary>Warnings that don't block publishing.</summary>
        public List<BuildWarning> Warnings { get; set; } = new List<BuildWarning>();

        /// <summary>Fatal errors that block publishing.</summary>
        public List<string> FatalErrors { get; set; } = new List<string>();
    }

    public enum SnapshotState
    {
        Building,
        Validating,
        ReadyToPublish,
        Published,
        Failed,
        Cancelled
    }

    public enum CompletenessStatus
    {
        Incomplete,
        Complete,
        CompleteWithWarnings
    }

    public sealed class GlobalRelationEdge
    {
        public string RelationId { get; set; }
        public string SourceBlockId { get; set; }
        public string SourceNetworkId { get; set; }
        public string TargetBlockId { get; set; }
        public string TargetNetworkId { get; set; }
        public RelationType Type { get; set; }
        public ResolutionStatus Resolution { get; set; } = ResolutionStatus.Unresolved;
        public string ResolutionReason { get; set; }

        /// <summary>For FB calls: instance DB stable ID.</summary>
        public string InstanceId { get; set; }

        /// <summary>Raw target text from source (for unresolved relations).</summary>
        public string RawTargetText { get; set; }

        /// <summary>Evidence source ref.</summary>
        public string SourceRef { get; set; }
    }

    public enum RelationType
    {
        BlockCall,
        VariableRead,
        VariableWrite,
        ParameterBinding,
        InstanceBinding,
        TypeReference
    }

    public sealed class CoverageManifest
    {
        public CoveragePhase Discovery { get; set; } = new CoveragePhase();
        public CoveragePhase ObjectParsing { get; set; } = new CoveragePhase();
        public CoveragePhase NetworkExtraction { get; set; } = new CoveragePhase();
        public CoveragePhase FactExtraction { get; set; } = new CoveragePhase();
        public CoveragePhase GlobalResolution { get; set; } = new CoveragePhase();
        public CoveragePhase CrossRefEnrichment { get; set; } = new CoveragePhase();
        public CoveragePhase SemanticAnnotation { get; set; } = new CoveragePhase();
        public CoveragePhase Validation { get; set; } = new CoveragePhase();
    }

    public sealed class CoveragePhase
    {
        public int Expected { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
        public int Warnings { get; set; }
        public double Percent => Expected == 0 ? 100.0 : (double)Completed / Expected * 100.0;
        public bool IsComplete => Expected == 0 || Completed >= Expected;
    }

    public sealed class BuildWarning
    {
        public string Category { get; set; }
        public string Phase { get; set; }
        public string AffectedId { get; set; }
        public string Reason { get; set; }
        public string Evidence { get; set; }
    }
}
