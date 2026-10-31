using System.Collections.Generic;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Plain-data DTOs exchanged between the Openness STA layer and the UI/ViewModel
    /// layers. These carry ONLY immutable primitive data (string/int/bool/handle) —
    /// never a live Siemens.Engineering.* COM object. This is the contract that keeps
    /// TIA COM objects confined to the dedicated STA host thread (TiaStaHost) and lets
    /// the WPF UI thread render freely without "Cross-thread operation is not valid in
    /// Openness within STA" exceptions.
    ///
    /// Where the UI later needs to act on a specific COM object (export/import/compile),
    /// it passes back the opaque <c>Handle</c> string; the service resolves the handle to
    /// the real COM object internally, on the STA thread.
    /// </summary>

    /// <summary>Project-level summary (replaces exposing the Project COM object).</summary>
    public sealed class ProjectInfoDto
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool IsModified { get; set; }
        public bool IsOpen { get; set; }
    }

    /// <summary>A device / device-item node (replaces Device / DeviceItem).</summary>
    public sealed class DeviceNodeDto
    {
        public string Name { get; set; } = "";
        public bool IsPlc { get; set; }
        /// <summary>Opaque handle to the backing PlcSoftware when IsPlc is true; else null.</summary>
        public string PlcHandle { get; set; }
        public bool IsHmi { get; set; }
        /// <summary>Opaque handle to the backing HMI software when IsHmi is true; else null.</summary>
        public string HmiHandle { get; set; }
        /// <summary>"Classic" (HmiTarget) or "Unified" (HmiSoftware) when IsHmi is true; "" otherwise.</summary>
        public string HmiKind { get; set; } = "";
        public List<DeviceNodeDto> Children { get; set; } = new List<DeviceNodeDto>();
    }

    /// <summary>A PLC software reference (replaces exposing PlcSoftware). Handle keyed into the service.</summary>
    public sealed class PlcSoftwareDto
    {
        public string Handle { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>Block / type counts for the status bar (replaces walking BlockGroup/TypeGroup on the UI thread).</summary>
    public sealed class BlockCountsDto
    {
        public int Ob { get; set; }
        public int Fb { get; set; }
        public int Fc { get; set; }
        public int Db { get; set; }
        public int Type { get; set; }
    }

    /// <summary>Kind of a node in the PLC block/type tree, or in the HMI screens/tag-table tree.</summary>
    public enum PlcTreeNodeKind
    {
        Root,
        BlockGroup,
        TypeGroup,
        TagTableGroup,
        Block,
        Type,
        TagTable,

        /// <summary>HMI software root (classic HmiTarget or unified HmiSoftware).</summary>
        HmiRoot,
        /// <summary>"Screens" folder under the HMI root.</summary>
        HmiScreenGroup,
        /// <summary>Single HMI screen.</summary>
        HmiScreen,
        /// <summary>"HMI Tags" / tag-table folder under the HMI root.</summary>
        HmiTagTableGroup,
        /// <summary>Single HMI tag table.</summary>
        HmiTagTable
    }

    /// <summary>
    /// A node in the PLC block/type/tag tree (replaces hanging raw COM objects on
    /// TreeViewItem.Tag). The <c>Handle</c> uniquely identifies the backing COM object
    /// inside the service so later export/import/delete can resolve it on the STA thread.
    /// </summary>
    public sealed class PlcTreeNodeDto
    {
        public string Name { get; set; } = "";
        public string DisplayText { get; set; } = "";
        public string Icon { get; set; } = "";
        public PlcTreeNodeKind Kind { get; set; }
        /// <summary>"OB"/"FB"/"FC"/"DB"/"UDT" for block/type nodes; "" otherwise.</summary>
        public string BlockType { get; set; } = "";
        public int Number { get; set; }
        /// <summary>Programming language of a block node ("SCL"/"LAD"/"STL"/"F_LAD"...); "" for non-block nodes or when unavailable.</summary>
        public string Language { get; set; } = "";
        /// <summary>Backslash group path relative to BlockGroup/TypeGroup root ("" = root).</summary>
        public string GroupPath { get; set; } = "";
        /// <summary>Opaque handle to the backing COM object (block/type/group/table).</summary>
        public string Handle { get; set; } = "";
        public List<PlcTreeNodeDto> Children { get; set; } = new List<PlcTreeNodeDto>();
    }

    /// <summary>A write/export target resolved from a tree selection (replaces WriteTarget{object Tag}).</summary>
    public sealed class WriteTargetDto
    {
        public string Name { get; set; } = "";
        public string BlockType { get; set; } = "";
        public string Handle { get; set; } = "";
    }

    /// <summary>A target group node for the write-target chip tree (replaces TargetGroupNode built from COM).</summary>
    public sealed class TargetGroupNodeDto
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool IsRoot { get; set; }
        public List<TargetGroupNodeDto> Children { get; set; } = new List<TargetGroupNodeDto>();
    }

    /// <summary>
    /// Exported block/type content for the code viewer. The COM export happens on the
    /// STA thread; this DTO carries only the resulting XML/source text plus metadata, so
    /// the UI thread can parse/render (XML parsing touches no COM) without cross-thread errors.
    /// </summary>
    public sealed class BlockContentDto
    {
        public string Name { get; set; } = "";
        /// <summary>"LAD"/"FBD"/"GRAPH"/"SCL"/"STL"/"UDT"/"" — normalized (F_LAD→LAD etc.).</summary>
        public string Language { get; set; } = "";
        public bool IsSafety { get; set; }
        public bool IsType { get; set; }
        /// <summary>Exported XML content. Every source/IR projection is derived from this.</summary>
        public string Xml { get; set; }
        public int Number { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Plain-data snapshot of a PLC tag table, extracted on the STA thread.
    /// The UI binds to this DTO only, never touching the underlying COM object.
    /// </summary>
    public sealed class TagTableContentDto
    {
        public string Name { get; set; } = "";
        public int TagCount { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Single tag entry inside a tag table snapshot (PLC or HMI).</summary>
    public sealed class TagEntryDto
    {
        public string Name { get; set; } = "";
        public string DataType { get; set; } = "";
        public string LogicalAddress { get; set; } = "";
        public string Comment { get; set; } = "";
        /// <summary>HMI only: name of the HMI/PLC connection the tag is bound to.</summary>
        public string Connection { get; set; } = "";
        /// <summary>HMI only: PLC name shown in the TIA variable table.</summary>
        public string PlcName { get; set; } = "";
        /// <summary>HMI only: PLC tag the HMI tag is linked to.</summary>
        public string PlcTag { get; set; } = "";
    }

    /// <summary>
    /// Result of exporting a block/type (by tree handle) to an XML file. The export runs on
    /// the STA host; the UI only shows the resulting message / path.
    /// </summary>
    public sealed class ExportResultDto
    {
        public bool Success { get; set; }
        /// <summary>Full path of the written file (when Success).</summary>
        public string FilePath { get; set; } = "";
        /// <summary>True when the block was skipped because it is not consistent (needs compile).</summary>
        public bool NotConsistent { get; set; }
        /// <summary>Error message when the export failed for another reason.</summary>
        public string Error { get; set; } = "";
        /// <summary>Name of the exported block/type (for user messages).</summary>
        public string Name { get; set; } = "";
    }

    /// <summary>Result of creating a subgroup under a block/type group (by handle), run on the STA host.</summary>
    public sealed class GroupCreateResultDto
    {
        public bool Success { get; set; }
        /// <summary>True when a group with the same name already exists under the parent.</summary>
        public bool AlreadyExists { get; set; }
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// Result of importing a block/type XML file (run on the STA host). When the item already
    /// exists and overwrite was not requested, <see cref="AlreadyExists"/> is set and nothing is
    /// imported, so the UI can prompt the user and retry with overwrite=true.
    /// </summary>
    public sealed class ImportResultDto
    {
        public bool Success { get; set; }
        /// <summary>True when the block/type already exists and overwrite was not requested.</summary>
        public bool AlreadyExists { get; set; }
        /// <summary>Parsed name of the block/type being imported (for user messages).</summary>
        public string Name { get; set; } = "";
        /// <summary>True when the XML file is not a recognizable block/type document.</summary>
        public bool InvalidFile { get; set; }
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// Summary of a full-PLC export (blocks + data types + tag tables), produced on the STA host.
    /// </summary>
    public sealed class ExportAllResultDto
    {
        public bool Success { get; set; }
        public string PlcName { get; set; } = "";
        public string OutputDir { get; set; } = "";
        public int BlocksExported { get; set; }
        public int BlocksSkipped { get; set; }
        public int TypesExported { get; set; }
        public int TypesSkipped { get; set; }
        public int TagTablesExported { get; set; }
        public List<string> Failures { get; set; } = new List<string>();
        /// <summary>Set when the whole operation failed (e.g. no PLC selected).</summary>
        public string Error { get; set; } = "";
    }
}
