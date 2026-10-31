using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Models.NetworkKnowledge
{
    /// <summary>
    /// A Network is the first-class semantic unit within a Logic Block (OB/FC/FB).
    /// It holds deterministic facts extracted from LAD XML and optional semantic annotations.
    /// </summary>
    public sealed class PlcNetworkDocument
    {
        /// <summary>Stable identity independent of display name and order.</summary>
        public string NetworkId { get; set; }

        /// <summary>Stable ID of the owning Logic Block.</summary>
        public string BlockId { get; set; }

        /// <summary>Project-scoped key.</summary>
        public string ProjectKey { get; set; }

        /// <summary>1-based source order within the block. Updated on insert/delete but identity stays.</summary>
        public int NetworkOrder { get; set; }

        /// <summary>CompileUnit ID from TIA XML for source traceability.</summary>
        public string CompileUnitId { get; set; }

        /// <summary>Network title from XML (may be empty).</summary>
        public string Title { get; set; }

        /// <summary>Network comment from XML (may be empty).</summary>
        public string Comment { get; set; }

        /// <summary>Programming language: LAD, FBD, SCL, etc.</summary>
        public string Language { get; set; }

        /// <summary>SHA-256 prefix of the FlgNet XML fragment (LAD) or StructuredText body (SCL) for change detection.</summary>
        public string ContentHash { get; set; }

        /// <summary>Raw source text for text-based languages (SCL StructuredText body). Empty for LAD.</summary>
        public string SourceText { get; set; }

        /// <summary>Version of the deterministic parser that produced these facts.</summary>
        public string ParserVersion { get; set; }

        /// <summary>
        /// Canonical body JSON: LadJson for LAD/FBD/SCL/STL and GraphJsonBridge for GRAPH. This is
        /// the stored source of truth for the network; the flat columns and relation rows are
        /// projections derived from it. Empty when canonical projection failed (legacy payload is
        /// used as a fallback).
        /// </summary>
        public string CanonicalJson { get; set; }

        /// <summary>When this record was last indexed.</summary>
        public DateTime IndexedAtUtc { get; set; }

        // ---- Deterministic Facts ----

        public List<AccessNode> Accesses { get; set; } = new List<AccessNode>();
        public List<LogicPart> Parts { get; set; } = new List<LogicPart>();
        public List<WireEdge> Wires { get; set; } = new List<WireEdge>();
        public List<CallSiteNode> CallSites { get; set; } = new List<CallSiteNode>();
        public List<ParameterBindingFact> ParameterBindings { get; set; } = new List<ParameterBindingFact>();

        // ---- Parse Status ----

        /// <summary>Whether deterministic extraction succeeded.</summary>
        public ParseStatus Status { get; set; } = ParseStatus.Pending;

        /// <summary>If Status is Failed, the reason.</summary>
        public string FailureReason { get; set; }
    }

    public enum ParseStatus
    {
        Pending,
        Succeeded,
        Failed
    }
}
