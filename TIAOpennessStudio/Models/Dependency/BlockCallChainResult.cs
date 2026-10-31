using System.Collections.Generic;

namespace TIAOpennessStudio.Models.Dependency
{
    public sealed class CallChainNode
    {
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public int Depth { get; set; }
        public int NetworkIndex { get; set; }
        public string SourceRef { get; set; }
        public List<CallChainNode> Children { get; set; } = new List<CallChainNode>();
    }

    public sealed class BlockCallChainResult
    {
        public string RootBlock { get; set; }
        public string Direction { get; set; }
        public int TotalNodes { get; set; }
        public int MaxDepthReached { get; set; }
        public bool WasTruncated { get; set; }
        public List<string> CycleBlocks { get; set; } = new List<string>();
        public List<CallChainNode> Callers { get; set; } = new List<CallChainNode>();
        public List<CallChainNode> Callees { get; set; } = new List<CallChainNode>();
        public string Note { get; set; }
    }
}
