using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Models.Dependency
{
    public sealed class PlcBlockDependencyRecord
    {
        public string ProjectKey { get; set; }
        public string BlockKey { get; set; }
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public string Language { get; set; }
        public string VersionHash { get; set; }
        public string ExportPath { get; set; }
        public DateTime IndexedAtUtc { get; set; }
        public List<PlcCallEdge> Calls { get; set; } = new List<PlcCallEdge>();
        public List<PlcSymbolReference> References { get; set; } = new List<PlcSymbolReference>();
        public List<PlcSymbolDefinition> Definitions { get; set; } = new List<PlcSymbolDefinition>();
    }
}
