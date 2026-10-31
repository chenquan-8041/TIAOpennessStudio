using System.Collections.Generic;
using System.Threading.Tasks;
using TIAOpennessStudio.Models.Dependency;

namespace TIAOpennessStudio.Services.Abstractions
{
    public class IndexSearchOptions
    {
        public string BlockTypeFilter { get; set; }
        public string LanguageFilter { get; set; }
        public string VariableName { get; set; }
        public string NetworkScope { get; set; }
        public int TopK { get; set; } = 5;
        public bool IncludeUdt { get; set; } = true;
        public bool IncludeBlocks { get; set; } = true;
    }

    public class DependencyRefHit
    {
        public string ProjectKey { get; set; }
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public string Language { get; set; }
        public string CanonicalPath { get; set; }
        public string AccessKind { get; set; }
        public string TargetBlock { get; set; }
        public int NetworkIndex { get; set; }
        public string SourceRef { get; set; }
        public string Version { get; set; }
    }

    /// <summary>One node on a dependency traversal (call chain or variable impact).</summary>
    public class DependencyChainNode
    {
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public int Depth { get; set; }
        /// <summary>"caller" (upstream) or "callee" (downstream) relative to the root.</summary>
        public string Relation { get; set; }
        public int NetworkIndex { get; set; }
        public string SourceRef { get; set; }
        /// <summary>Path from root to this node, e.g. ["FB_Main","FC_Calc"].</summary>
        public List<string> Path { get; set; } = new List<string>();
    }

    /// <summary>Result of a call-chain or variable-impact traversal over plc_call_relation / plc_symbol_doc.</summary>
    public class DependencyChainResult
    {
        public string Root { get; set; }
        public string Direction { get; set; }
        public int TotalNodes { get; set; }
        public int MaxDepthReached { get; set; }
        public bool WasTruncated { get; set; }
        public List<string> CycleBlocks { get; set; } = new List<string>();
        public List<DependencyChainNode> Nodes { get; set; } = new List<DependencyChainNode>();
        public string Note { get; set; }
    }

    /// <summary>
    /// Queries the project dependency graph (call chains and variable impact) against
    /// the v3 index, requiring no TIA STA access.
    /// </summary>
    public interface IProjectIndexService
    {
        // Dependency graph traversal (backed by v3 plc_call_edge / plc_symbol_ref; no TIA STA access)
        DependencyChainResult QueryCallChain(string projectKey, string blockName, string direction = "both", int maxDepth = 5, int maxNodeCount = 200);
        DependencyChainResult QueryVariableImpact(string projectKey, string canonicalPath, string direction = "both", int maxDepth = 5, int maxNodeCount = 200);
    }
}
