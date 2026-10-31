using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Models.Dependency
{
    public sealed class PlcDependencyIndexSnapshot
    {
        public string ProjectKey { get; set; }
        public DateTime BuiltAtUtc { get; set; }
        public Dictionary<string, PlcBlockDependencyRecord> Blocks { get; set; } = new Dictionary<string, PlcBlockDependencyRecord>();
        public Dictionary<string, List<PlcCallEdge>> CallersByBlock { get; set; } = new Dictionary<string, List<PlcCallEdge>>();
        public Dictionary<string, List<PlcCallEdge>> CalleesByBlock { get; set; } = new Dictionary<string, List<PlcCallEdge>>();
        public Dictionary<string, List<PlcSymbolReference>> RefsByCanonicalPath { get; set; } = new Dictionary<string, List<PlcSymbolReference>>();
        public Dictionary<string, PlcSymbolDefinition> DefinitionsByCanonicalPath { get; set; } = new Dictionary<string, PlcSymbolDefinition>();
    }
}
