using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Models.NetworkKnowledge
{
    /// <summary>
    /// A validated semantic annotation produced by the local 7-27B model for a single Network.
    /// </summary>
    public sealed class SemanticAnnotation
    {
        public string NetworkId { get; set; }
        public string Summary { get; set; }
        public string FunctionalCategory { get; set; }
        public string BusinessArea { get; set; }
        public List<string> Preconditions { get; set; } = new List<string>();
        public List<string> Effects { get; set; } = new List<string>();
        public List<string> Tags { get; set; } = new List<string>();
        public double Confidence { get; set; }
        public List<string> EvidenceRefs { get; set; } = new List<string>();

        public string ModelName { get; set; }
        public string ModelVersion { get; set; }
        public string PromptVersion { get; set; }
        public string SchemaVersion { get; set; }
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

        public AnnotationStatus Status { get; set; } = AnnotationStatus.Pending;
        public string FailureReason { get; set; }
        public int AttemptCount { get; set; }
    }

    public enum AnnotationStatus
    {
        Pending,
        Succeeded,
        Failed,
        Skipped
    }
}
