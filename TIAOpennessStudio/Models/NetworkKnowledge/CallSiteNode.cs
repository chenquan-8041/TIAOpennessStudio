using System.Collections.Generic;

namespace TIAOpennessStudio.Models.NetworkKnowledge
{
    /// <summary>
    /// A deterministic block call occurrence within a LAD Network.
    /// Resolution to actual target block happens in Phase 3 (post-discovery).
    /// </summary>
    public sealed class CallSiteNode
    {
        /// <summary>TIA XML UId of the Call element.</summary>
        public string Uid { get; set; }

        /// <summary>Raw callee name from CallInfo/@Name.</summary>
        public string RawCalleeName { get; set; }

        /// <summary>Block type from CallInfo/@BlockType (FB, FC, etc.).</summary>
        public string RawBlockType { get; set; }

        /// <summary>Instance path from CallInfo (for FB calls).</summary>
        public string RawInstancePath { get; set; }

        /// <summary>Resolved target block Stable ID (populated in Phase 3).</summary>
        public string ResolvedTargetId { get; set; }

        /// <summary>Resolved instance DB Stable ID (populated in Phase 3 for FB).</summary>
        public string ResolvedInstanceId { get; set; }

        /// <summary>Resolution state: resolved, unresolved, ambiguous.</summary>
        public ResolutionStatus Status { get; set; } = ResolutionStatus.Unresolved;

        /// <summary>Reason if unresolved or ambiguous.</summary>
        public string StatusReason { get; set; }

        /// <summary>Call parameters with their section/direction.</summary>
        public List<CallParameterFact> Parameters { get; set; } = new List<CallParameterFact>();

        /// <summary>Source reference for traceability.</summary>
        public string SourceRef { get; set; }
    }

    public sealed class CallParameterFact
    {
        /// <summary>Parameter name (e.g. "EN", "IN1", "OUT").</summary>
        public string Name { get; set; }

        /// <summary>Section from XML: Input, Output, InOut.</summary>
        public string Section { get; set; }

        /// <summary>Connected Access UId (from Wire analysis).</summary>
        public string ConnectedAccessUid { get; set; }

        /// <summary>Connected Access canonical path (convenience).</summary>
        public string ConnectedAccessPath { get; set; }
    }

    public enum ResolutionStatus
    {
        Unresolved,
        Resolved,
        Ambiguous
    }
}
