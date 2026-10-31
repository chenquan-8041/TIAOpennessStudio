using System.Collections.Generic;

namespace TIAOpennessStudio.Models.Dependency
{
    public sealed class VariableImpactNode
    {
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public string CanonicalPath { get; set; }
        public string AccessKind { get; set; }
        public int NetworkIndex { get; set; }
        public int Depth { get; set; }
        public List<string> CallPath { get; set; } = new List<string>();
    }

    public sealed class VariableImpactResult
    {
        public string VariablePath { get; set; }
        public string Direction { get; set; }
        public int TotalHits { get; set; }
        public int MaxDepthReached { get; set; }
        public bool WasTruncated { get; set; }
        public List<VariableImpactNode> Nodes { get; set; } = new List<VariableImpactNode>();
        public string Note { get; set; }
    }
}
