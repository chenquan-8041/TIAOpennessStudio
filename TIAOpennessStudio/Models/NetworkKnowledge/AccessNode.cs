namespace TIAOpennessStudio.Models.NetworkKnowledge
{
    /// <summary>
    /// A deterministic symbol access within a LAD Network.
    /// Direction is resolved from Wire/Pin connections, not guessed from Scope.
    /// </summary>
    public sealed class AccessNode
    {
        /// <summary>TIA XML UId of the Access element.</summary>
        public string Uid { get; set; }

        /// <summary>Canonicalized symbol path (e.g. DB_Main.Motor.Speed).</summary>
        public string CanonicalPath { get; set; }

        /// <summary>Original Scope attribute: GlobalVariable, LocalVariable, TypedConstant, etc.</summary>
        public string Scope { get; set; }

        /// <summary>Data type if determinable from source.</summary>
        public string DataType { get; set; }

        /// <summary>
        /// Direction resolved from Wire/Pin analysis.
        /// Read, Write, ReadWrite, or Unresolved.
        /// </summary>
        public AccessDirection Direction { get; set; } = AccessDirection.Unresolved;

        /// <summary>Evidence for the direction resolution (connected Pin name, etc.).</summary>
        public string DirectionEvidence { get; set; }

        /// <summary>Source reference for traceability.</summary>
        public string SourceRef { get; set; }

        /// <summary>
        /// Whether the direction was verified by the authoritative TIA Openness
        /// CrossReferenceProvider API (true) or inferred from Wire/Pin XML analysis only (false).
        /// </summary>
        public bool CrossRefVerified { get; set; }

        /// <summary>
        /// Original direction before cross-reference enrichment (for audit trail).
        /// Only set when cross-reference enrichment changes the direction.
        /// </summary>
        public AccessDirection? OriginalDirection { get; set; }
    }

    public enum AccessDirection
    {
        Unresolved,
        Read,
        Write,
        ReadWrite
    }
}
