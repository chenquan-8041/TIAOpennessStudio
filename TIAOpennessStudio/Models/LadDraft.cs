using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// G-6: session-level draft for incremental (append-style) LAD authoring.
    ///
    /// Instead of generating a whole LAD block XML in one shot and compiling
    /// immediately, the agent accumulates networks into ONE draft and triggers a
    /// single block compile at commit time. Append only collects network material
    /// (call name + parameters + title/comment); global ID allocation is deferred to
    /// commit, done by the composer's monotonic counter — so cross-network ID clashes
    /// are impossible by construction.
    ///
    /// First-phase scope: <c>ladfc</c> (reuses the existing PlcLadFcBlockXmlComposer).
    /// <c>ladfb</c> is accepted structurally but composing is deferred until the FB
    /// composer path is added (begin returns a clear "not yet" note for ladfb).
    /// </summary>
    public sealed class LadDraft
    {
        public string SessionId { get; set; }

        /// <summary>"ladfc" | "ladfb". Only ladfc composes in phase 1.</summary>
        public string Kind { get; set; } = "ladfc";

        public string BlockName { get; set; }
        public int BlockNumber { get; set; }
        public string BlockTitle { get; set; } = "";
        public string BlockComment { get; set; } = "";

        // Block interface — consistent across the whole draft session.
        public List<LadDraftMember> Inputs { get; set; } = new List<LadDraftMember>();
        public List<LadDraftMember> Outputs { get; set; } = new List<LadDraftMember>();

        /// <summary>Accumulated networks (call-type in phase 1).</summary>
        public List<LadDraftNetwork> Networks { get; set; } = new List<LadDraftNetwork>();

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A block interface member in a draft (Input/Output).</summary>
    public sealed class LadDraftMember
    {
        public string Name { get; set; }
        public string Datatype { get; set; }
        public string Comment { get; set; } = "";
    }

    /// <summary>
    /// One accumulated network: a call-type FlgNet (call name + parameters) plus
    /// optional title/comment. Matches what FlgNetCallXmlBuilder can emit today.
    /// </summary>
    public sealed class LadDraftNetwork
    {
        public string Title { get; set; } = "";
        public string Comment { get; set; } = "";

        /// <summary>The FC/FB being called in this network (call-type network).</summary>
        public string CallName { get; set; }

        public List<LadDraftCallParameter> Parameters { get; set; } = new List<LadDraftCallParameter>();
    }

    /// <summary>One actual parameter wired to the call in a draft network.</summary>
    public sealed class LadDraftCallParameter
    {
        public string ParameterName { get; set; }

        /// <summary>"Input" | "Output" | "InOut".</summary>
        public string Section { get; set; } = "Input";

        public string DataType { get; set; }

        /// <summary>"global" (symbol) or "constant" (literal).</summary>
        public string SourceKind { get; set; } = "global";

        /// <summary>Constant literal value (when SourceKind == "constant").</summary>
        public string ConstantValue { get; set; } = "";

        /// <summary>Symbol path components (when SourceKind == "global"), e.g. ["DB","Member"].</summary>
        public List<string> SymbolPath { get; set; } = new List<string>();
    }
}
