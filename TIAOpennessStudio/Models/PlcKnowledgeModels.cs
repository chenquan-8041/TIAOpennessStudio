using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Models
{
    public sealed class PlcKnowledgeSnapshot
    {
        public string ProjectKey { get; set; }
        public string BlockKey { get; set; }
        public PlcBlockDoc Block { get; set; } = new PlcBlockDoc();
        public List<PlcSymbolDoc> Symbols { get; set; } = new List<PlcSymbolDoc>();
        public List<PlcCallRelation> Calls { get; set; } = new List<PlcCallRelation>();
        public List<PlcNetworkDoc> Networks { get; set; } = new List<PlcNetworkDoc>();
    }

    public sealed class PlcNetworkDoc
    {
        public string ProjectKey { get; set; }
        public string BlockKey { get; set; }
        public string NetworkId { get; set; }
        public int NetworkIndex { get; set; }
        public string CompileUnitId { get; set; }
        public string Language { get; set; }
        public string Title { get; set; }
        public string Comment { get; set; }
        public string Inputs { get; set; }
        public string Outputs { get; set; }
        public string SourceText { get; set; }
        public string JsonPayload { get; set; }
        public string ContentHash { get; set; }
        public string SourceRef { get; set; }
        public List<PlcNetworkFact> Facts { get; set; } = new List<PlcNetworkFact>();
    }

    public sealed class PlcNetworkFact
    {
        public string ProjectKey { get; set; }
        public string BlockKey { get; set; }
        public string NetworkId { get; set; }
        public string FactId { get; set; }
        public string Kind { get; set; }
        public string Uid { get; set; }
        public string CanonicalPath { get; set; }
        public string Scope { get; set; }
        public string Direction { get; set; }
        public string DisplayText { get; set; }
        public string SearchText { get; set; }
        public string TargetName { get; set; }
        public string TargetType { get; set; }
        public string InstancePath { get; set; }
        public string SourceRef { get; set; }
    }

    public sealed class PlcBlockDoc
    {
        public string ProjectKey { get; set; }
        public string BlockKey { get; set; }
        public string BlockName { get; set; }
        public string CanonicalBlockName { get; set; }
        public string BlockType { get; set; }
        public string Language { get; set; }
        public string VersionHash { get; set; }
        public string Summary { get; set; }
        public int SymbolCount { get; set; }
        public int CallCount { get; set; }
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class PlcSymbolDoc
    {
        public string ProjectKey { get; set; }
        public string BlockKey { get; set; }
        public string CanonicalPath { get; set; }
        public string SymbolName { get; set; }
        public string OwnerName { get; set; }
        public string OwnerType { get; set; }
        public string Scope { get; set; }
        public string DataType { get; set; }
        public string DefinitionKind { get; set; }
        public string UsageKind { get; set; }
        public int NetworkIndex { get; set; }
        public string SourceRef { get; set; }
    }

    public sealed class PlcCallRelation
    {
        public string ProjectKey { get; set; }
        public string CallerBlockKey { get; set; }
        public string CallerBlockName { get; set; }
        public string CallerBlockType { get; set; }
        public string CalleeName { get; set; }
        public string CalleeBlockType { get; set; }
        public string InstancePath { get; set; }
        public int NetworkIndex { get; set; }
        public string SourceRef { get; set; }
    }

    public sealed class PlcInterfaceMemberDoc
    {
        public string BlockKey { get; set; }
        public string MemberPath { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public string Section { get; set; }
        public string InitialValue { get; set; }
        public string Comment { get; set; }
        public int OrderIndex { get; set; }
    }

    public sealed class PlcKnowledgeSearchHit
    {
        public string ProjectKey { get; set; }
        public string BlockKey { get; set; }
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public string Language { get; set; }
        public string VersionHash { get; set; }
        public string Summary { get; set; }
        public string UpdatedAt { get; set; }
        public int Score { get; set; }
        /// <summary>Carried only by the first hit: coverage/truncation note for this retrieval (matched rows per table vs rows actually returned),
        /// used to eliminate the blind spot where "LIMIT truncated the results but the Agent assumes they are complete". null for other hits.</summary>
        public string SearchNote { get; set; }
        public List<PlcNetworkDoc> MatchedNetworks { get; set; } = new List<PlcNetworkDoc>();
        /// <summary>DB/UDT/FB/FC interface members. DB/UDT have no networks, so this is their only structural evidence.</summary>
        public List<PlcInterfaceMemberDoc> MatchedMembers { get; set; } = new List<PlcInterfaceMemberDoc>();
        public List<PlcSymbolDoc> MatchedSymbols { get; set; } = new List<PlcSymbolDoc>();
        public List<PlcCallRelation> MatchedCalls { get; set; } = new List<PlcCallRelation>();
    }

    public sealed class PlcProjectIndexBuildResult
    {
        public string ProjectKey { get; set; }
        public int BlocksScanned { get; set; }
        public int TypesScanned { get; set; }
        public int Upserted { get; set; }
        public int SkippedUnchanged { get; set; }
        public int Failed { get; set; }
        public int SkippedUnsupported { get; set; }
        public List<string> Errors { get; set; } = new List<string>();

        // Network-centric extraction stats (Phase 1)
        public int NetworksExtracted { get; set; }
        public int NetworksFailed { get; set; }

        // Tag Table stats
        public int TagsIndexed { get; set; }
    }

    public sealed class PlcVariableReferenceHit
    {
        public string ProjectKey { get; set; }
        public string BlockKey { get; set; }
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public string Language { get; set; }
        public string CanonicalPath { get; set; }
        public string SymbolName { get; set; }
        public string Scope { get; set; }
        public string DefinitionKind { get; set; }
        public string UsageKind { get; set; }
        public int NetworkIndex { get; set; }
        public string SourceRef { get; set; }
        public int Score { get; set; }
    }

    public sealed class PlcVariableReferenceResult
    {
        public string VariablePath { get; set; }
        public string Direction { get; set; }
        public string Precision { get; set; }
        public string Note { get; set; }
        public int TotalHits { get; set; }
        public List<PlcVariableReferenceHit> Hits { get; set; } = new List<PlcVariableReferenceHit>();
    }
}
