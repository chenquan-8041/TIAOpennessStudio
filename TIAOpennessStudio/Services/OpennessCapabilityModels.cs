using System.Collections.Generic;

namespace TIAOpennessStudio.Services
{
    /// <summary>Manifest of Openness capabilities (the API surface the current TIA version actually exposes).</summary>
    public sealed class OpennessCapabilityManifest
    {
        public string EngineeringDllVersion { get; set; } = "";
        public string EngineeringDllPath { get; set; } = "";
        public string ScannedAtUtc { get; set; } = "";
        public bool FromCache { get; set; }
        public int TotalTypeCount { get; set; }

        public List<string> Assemblies { get; set; } = new List<string>();
        public List<OpennessNamespaceInfo> Namespaces { get; set; } = new List<OpennessNamespaceInfo>();
    }

    public sealed class OpennessNamespaceInfo
    {
        public string Namespace { get; set; } = "";
        public List<OpennessTypeInfo> Types { get; set; } = new List<OpennessTypeInfo>();
    }

    public sealed class OpennessTypeInfo
    {
        public string Name { get; set; } = "";
        public string FullName { get; set; } = "";
        /// <summary>class / interface / enum / struct</summary>
        public string Kind { get; set; } = "";
        public string BaseType { get; set; }

        /// <summary>Public method names of non-enum types (deduplicated, sorted, truncated).</summary>
        public List<string> Methods { get; set; }
        /// <summary>Public property names of non-enum types.</summary>
        public List<string> Properties { get; set; }
        /// <summary>Member names of enum types.</summary>
        public List<string> EnumValues { get; set; }
    }
}
