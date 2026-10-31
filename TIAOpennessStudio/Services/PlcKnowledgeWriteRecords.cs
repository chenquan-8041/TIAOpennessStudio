using System.Collections.Generic;
using TIAOpennessStudio.Models.NetworkKnowledge;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Everything needed to write one block and its derived rows in a single
    /// transaction. Built by the indexing service, consumed by the writer.
    /// </summary>
    public sealed class BlockWriteRecord
    {
        public string ProjectKey { get; set; }
        public string BlockId { get; set; }              // path key "FC:Group\\Name"
        public string ParentContainerId { get; set; }
        public string Name { get; set; }
        public string CanonicalName { get; set; }
        public string BlockType { get; set; }            // OB|FB|FC|DB|UDT|Tag
        public string BlockNumber { get; set; }
        public string Language { get; set; }
        public string VersionHash { get; set; }
        public string Revision { get; set; }             // T11: sha256 revision of RawXml (same as get_block_content)
        public string SourceProfile { get; set; }        // T11: TIA version the XML was exported from
        public string RawXml { get; set; }               // T11: exact export -> plc_block_snapshot
        public string BlockAttributesJson { get; set; }  // T11: block-level attributes JSON
        public string InterfaceJson { get; set; }        // T12: canonical interface projection (DB/UDT)
        public string Summary { get; set; }
        public string ParseStatus { get; set; } = "ok";  // ok|failed|partial
        public string FailureReason { get; set; }
        public string SourceProjectPath { get; set; }    // XML locator
        public string SourceBlockPath { get; set; }       // XML locator
        public string GitBranch { get; set; }
        public string GitCommit { get; set; }

        public List<PlcNetworkDocument> LadNetworks { get; } = new List<PlcNetworkDocument>();
        public List<PlcNetworkDocument> SclNetworks { get; } = new List<PlcNetworkDocument>();
        public List<InterfaceMemberRecord> Members { get; } = new List<InterfaceMemberRecord>();
        public List<SymbolRefRecord> SymbolRefs { get; } = new List<SymbolRefRecord>();
        public List<CallEdgeRecord> CallEdges { get; } = new List<CallEdgeRecord>();
    }

    public sealed class InterfaceMemberRecord
    {
        public string MemberId { get; set; }
        public string ParentMemberId { get; set; }
        public string MemberPath { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public string Section { get; set; }
        public string InitialValue { get; set; }
        public string Comment { get; set; }
        public int OrderIndex { get; set; }
    }

    public sealed class SymbolRefRecord
    {
        public string RefId { get; set; }
        public string NetworkId { get; set; }
        public int NetworkOrder { get; set; }
        public string CanonicalPath { get; set; }
        public string SymbolName { get; set; }
        public string Scope { get; set; }
        public string Direction { get; set; }            // Read|Write|ReadWrite
        public string SourceRef { get; set; }
    }

    public sealed class CallEdgeRecord
    {
        public string EdgeId { get; set; }
        public string CalleeName { get; set; }
        public string CalleeBlockType { get; set; }
        public string InstancePath { get; set; }
        public string NetworkId { get; set; }
        public int NetworkOrder { get; set; }
        public string SourceRef { get; set; }
    }
}
