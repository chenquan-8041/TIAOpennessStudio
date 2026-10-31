namespace TIAOpennessStudio.Models.NetworkKnowledge
{
    /// <summary>
    /// A deterministic connection (wire) within a LAD Network.
    /// Connects Access nodes to Part/Call pins via IdentCon and NameCon.
    /// </summary>
    public sealed class WireEdge
    {
        /// <summary>TIA XML UId of the Wire element.</summary>
        public string WireUid { get; set; }

        /// <summary>True if this wire originates from the power rail.</summary>
        public bool FromPowerRail { get; set; }

        /// <summary>Access UId connected via IdentCon (the variable endpoint).</summary>
        public string IdentAccessUid { get; set; }

        /// <summary>Target Part/Call UId connected via NameCon.</summary>
        public string TargetPartUid { get; set; }

        /// <summary>Pin name on the target Part/Call (e.g. "in", "en", "operand").</summary>
        public string TargetPinName { get; set; }

        /// <summary>
        /// Inferred pin direction based on known instruction semantics.
        /// Input means the wire carries data INTO the part (Access is being read).
        /// Output means the wire carries data FROM the part (Access is being written).
        /// </summary>
        public PinDirection PinDirection { get; set; } = PinDirection.Unknown;
    }

    public enum PinDirection
    {
        Unknown,
        Input,
        Output,
        InOut
    }
}
