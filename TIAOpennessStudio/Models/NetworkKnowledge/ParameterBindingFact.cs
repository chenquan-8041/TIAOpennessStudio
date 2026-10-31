namespace TIAOpennessStudio.Models.NetworkKnowledge
{
    /// <summary>
    /// A deterministic binding between a CallSite parameter pin and a caller-side Access/expression.
    /// Derived from Wire connections to Call pins.
    /// </summary>
    public sealed class ParameterBindingFact
    {
        /// <summary>Call Site UId this binding belongs to.</summary>
        public string CallSiteUid { get; set; }

        /// <summary>Formal parameter name on the callee interface.</summary>
        public string FormalParameterName { get; set; }

        /// <summary>Formal parameter section (Input, Output, InOut).</summary>
        public string FormalSection { get; set; }

        /// <summary>Actual Access UId connected to this parameter via Wire.</summary>
        public string ActualAccessUid { get; set; }

        /// <summary>Actual Access canonical path (convenience).</summary>
        public string ActualAccessPath { get; set; }

        /// <summary>Pin direction inferred from Section: Input=read, Output=write, InOut=readwrite.</summary>
        public AccessDirection InferredDirection { get; set; } = AccessDirection.Unresolved;

        /// <summary>Source evidence (Wire UId or NameCon detail).</summary>
        public string Evidence { get; set; }
    }
}
