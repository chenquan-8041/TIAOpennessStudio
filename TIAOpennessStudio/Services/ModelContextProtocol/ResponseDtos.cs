using System.Collections.Generic;

namespace ModelContextProtocol
{
    /// <summary>Generic JSON-report envelope returned by read-only/reflection probes.</summary>
    public class ResponseJsonReport
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }
    }

    /// <summary>Describes a single reflected member (property or method) of a TIA object.</summary>
    public class ObjectMember
    {
        public string Kind { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Signature { get; set; }
    }

    /// <summary>Reflection-based object description (members of a resolved object).</summary>
    public class ResponseObjectDescribe
    {
        public string Message { get; set; }
        public string ObjectKind { get; set; }
        public string ObjectPath { get; set; }
        public string TypeName { get; set; }
        public IReadOnlyList<ObjectMember> Members { get; set; }
    }

    /// <summary>Reflection-based property value read.</summary>
    public class ResponseObjectValue
    {
        public string Message { get; set; }
        public string ObjectKind { get; set; }
        public string ObjectPath { get; set; }
        public string ValueType { get; set; }
        public object Value { get; set; }
    }

    /// <summary>Reflection-based enumeration of a collection property's items.</summary>
    public class ResponseObjectChildren
    {
        public string Message { get; set; }
        public string ObjectKind { get; set; }
        public string ObjectPath { get; set; }
        public string Collection { get; set; }
        public IReadOnlyList<string> Items { get; set; }
    }

    /// <summary>A single network attribute of a device item.</summary>
    public class NetworkAttribute
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string DataType { get; set; }
        public bool IsWritable { get; set; }
    }

    /// <summary>Read-only probe result for a global library file.</summary>
    public class ResponseGlobalLibraryProbe
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string LibraryPath { get; set; }
        public string ResolvedLibraryFile { get; set; }
        public string LibraryType { get; set; }
        public string Error { get; set; }
        public List<string> Members { get; set; }
        public List<string> MasterCopies { get; set; }
        public List<string> Types { get; set; }
        public List<string> Folders { get; set; }
        public IReadOnlyList<string> Warnings { get; set; }
        public object Raw { get; set; }
    }

    /// <summary>Result of importing a global-library master copy into an HMI screen.</summary>
    public class ResponseGlobalLibraryImport
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string LibraryPath { get; set; }
        public string ResolvedLibraryFile { get; set; }
        public string MasterCopyName { get; set; }
        public string HmiSoftwarePath { get; set; }
        public string ScreenName { get; set; }
        public string ImportedItemName { get; set; }
        public List<string> Attempts { get; set; }
        public List<string> ReadbackItems { get; set; }
        public List<string> Warnings { get; set; }
        public string Error { get; set; }
        public object Raw { get; set; }
    }
}