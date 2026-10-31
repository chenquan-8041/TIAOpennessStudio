using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.Services.Abstractions
{
    public interface ITiaOpennessService
    {
        bool IsConnected { get; }
        TiaPortal TiaPortal { get; }
        Project Project { get; }
        PlcSoftware PlcSoftware { get; }
        
        Task<bool> ConnectAsync(string version = null);
        Task<bool> AttachToRunningTiaPortalAsync();
        bool ConnectToTiaPortal();
        bool AttachToRunningTiaPortal();
        /// <summary>Async, STA-safe PLC lookup. Returns true when a PLC was found. Prefer this over FindPlcSoftware() on the UI thread.</summary>
        Task<bool> FindPlcSoftwareAsync();
        Task<List<DeviceItem>> GetProjectTreeAsync();
        /// <summary>STA-safe device-name list (plain strings). Prefer this over GetProjectTreeAsync for cross-thread callers.</summary>
        Task<List<string>> GetProjectTreeNamesAsync();
        Task<string> ExportBlockXmlAsync(PlcBlock block);
        /// <summary>
        /// Export an already-held PlcType (UDT) object. Exporting by object instead of
        /// name avoids TypeGroup.Types.Find(name) missing UDTs nested in sub-groups,
        /// because Find does not recurse into sub-groups.
        /// </summary>
        Task<string> ExportTypeCodeAsync(PlcType type);
        Task<bool> ImportSclAsync(PlcBlock target, string sclCode);
        Task<string> ImportBlockCodeAsync(string blockName, string blockType, string code);

        /// <summary>
        /// Import code into a specific block / type group. Path uses backslash separator and is relative
        /// to PlcSoftware.BlockGroup or PlcSoftware.TypeGroup. Empty path = root group.
        /// Missing intermediate groups are created automatically.
        /// </summary>
        /// <param name="blockName">Logical block name (used only for messaging / temp file).</param>
        /// <param name="blockType">"FB" / "FC" / "DB" / "OB" / "UDT". UDT routes to TypeGroup.</param>
        /// <param name="code">SCL or XML payload.</param>
        /// <param name="groupPath">Backslash-delimited group path. "" or null means root.</param>
        Task<string> ImportBlockCodeAsync(string blockName, string blockType, string code, string groupPath);
        Task<string> CompileAsync();
        Task<string> ExportBlockCodeAsync(string blockName);

        /// <summary>
        /// Group path of an existing PLC block (backslash-delimited, relative to BlockGroup).
        /// Returns "" when the block lives in the root group and null when it cannot be found.
        /// Used by patch writes so a block is never relocated by re-import.
        /// </summary>
        Task<string> GetBlockGroupPathAsync(string blockName);

        /// <summary>
        /// Group path of an existing PLC data type (UDT), relative to TypeGroup.
        /// Returns "" for the root group and null when it cannot be found.
        /// </summary>
        Task<string> GetTypeGroupPathAsync(string typeName);

        /// <summary>
        /// Looks up a variable's data type. Searches PlcSoftware.TagTableGroup by name
        /// (case-insensitive) first; if not found and <paramref name="block"/> is
        /// non-null, also searches that block's interface (VAR_INPUT/OUTPUT/...).
        /// Returns the type name (e.g. "Bool", "Real") on success, or null/empty
        /// (without throwing) when not found. Callers may still receive null when
        /// PlcSoftware is not connected; exceptions are thrown only on severe errors.
        /// </summary>
        /// <param name="variable">Variable name (case-insensitive).</param>
        /// <param name="block">Optional block name; when non-null, search resumes in that block's interface.</param>
        Task<string> FindVariableTypeAsync(string variable, string block);
        
        bool OpenProject(FileInfo projectPath);
        PlcSoftware FindPlcSoftware();
        string GetProjectContext();
        /// <summary>
        /// Read the environment facts live from the open TIA project (never cached): TIA version,
        /// project/PLC names, device/CPU order number, inferred CPU family and a fingerprint.
        /// The user may switch the CPU or TIA version mid-session, so callers must re-read this
        /// instead of remembering an earlier result. Never throws; unknown facts stay empty and
        /// <see cref="EnvironmentProfileInfo.Live"/> turns false when the read fails.
        /// </summary>
        EnvironmentProfileInfo GetEnvironmentProfile();
        /// <summary>
        /// Compile one PLC block (not the whole PLC software) and return only that block's messages.
        /// Used as the gentle post-write check: it is local and cheap, does not touch other blocks the
        /// user may be editing, and cannot trip over Safety programs the way a whole-PLC compile can.
        /// Throws when the block does not exist or cannot be compiled; callers map that to a warning.
        /// </summary>
        Task<CompilationResult> CompileBlockAsync(string blockName);
        /// <summary>
        /// Resolve the culture code (e.g. "zh-CN") to use for the MultilingualText emitted by the PLC
        /// type builders. Returns the first installed project language (zh-CN → en-US → de-DE), falling
        /// back to "zh-CN". STA-safe; avoids emitting a culture the project does not have (Openness
        /// rejects unknown cultures on import).
        /// </summary>
        Task<string> ResolveProjectCultureAsync();

        // ── DTO-facade API (STA-safe; no COM objects cross the thread boundary) ──
        /// <summary>Project summary as plain data (Name/Path/IsModified/IsOpen).</summary>
        Task<ProjectInfoDto> GetProjectInfoAsync();
        /// <summary>Device/PLC tree as DTOs; registers PlcSoftware handles internally.</summary>
        Task<List<DeviceNodeDto>> GetDeviceTreeAsync();
        /// <summary>Set the active PlcSoftware by the handle issued from GetDeviceTreeAsync.</summary>
        Task<bool> SelectPlcByHandleAsync(string plcHandle);
        /// <summary>Block/type counts for the status bar; uses current PlcSoftware when handle omitted.</summary>
        Task<BlockCountsDto> GetBlockCountsAsync(string plcHandle = null);
        /// <summary>Full PLC block/type/tag tree as DTOs (handles for later actions); null when no PLC.</summary>
        Task<PlcTreeNodeDto> GetBlockTreeAsync(string plcHandle = null);
        /// <summary>
        /// HMI software tree (screens + tag tables) as DTOs, keyed by the handle issued from
        /// GetDeviceTreeAsync. Null when the handle is invalid or the software is not HMI.
        /// </summary>
        Task<PlcTreeNodeDto> GetHmiTreeAsync(string hmiHandle);
        /// <summary>Extract all variables of an HMI tag table (by tree handle), streaming batches through <paramref name="progress"/>. STA-safe.</summary>
        Task<TagTableContentDto> GetHmiTagTableContentAsync(string handle, IProgress<IReadOnlyList<TagEntryDto>> progress);
        /// <summary>Blocks group tree as DTOs for the write-target chip (STA-safe; empty when no PLC).</summary>
        Task<List<TargetGroupNodeDto>> GetBlockGroupTreeAsync();
        /// <summary>Types (UDT) group tree as DTOs for the write-target chip (STA-safe; empty when no PLC).</summary>
        Task<List<TargetGroupNodeDto>> GetTypeGroupTreeAsync();
        /// <summary>Delete a block/type/group by its tree handle (on STA thread).</summary>
        Task<bool> DeleteByHandleAsync(string handle);
        /// <summary>Whether a group handle still has children/blocks blocking deletion.</summary>
        Task<bool> GroupHasChildrenAsync(string handle);
        /// <summary>Export a block/type (by tree handle) to XML + metadata for the code viewer.</summary>
        Task<BlockContentDto> GetBlockContentAsync(string handle);
        /// <summary>Extract all tags from a tag table (by tree handle), streaming batches through <paramref name="progress"/>. STA-safe.</summary>
        Task<TagTableContentDto> GetTagTableContentAsync(string handle, IProgress<IReadOnlyList<TagEntryDto>> progress);
        /// <summary>Export a block/type (by tree handle) to an XML file under targetDir. STA-safe.</summary>
        Task<ExportResultDto> ExportByHandleAsync(string handle, string targetDir);
        /// <summary>Create a subgroup under a block/type group (by handle). STA-safe.</summary>
        Task<GroupCreateResultDto> CreateGroupByHandleAsync(string parentHandle, string name);
        /// <summary>Import a block XML file into a block group (by handle). STA-safe.</summary>
        Task<ImportResultDto> ImportBlockFromFileAsync(string groupHandle, string filePath, bool overwrite);
        /// <summary>Import a PLC data type (UDT) XML file into the current PLC's TypeGroup. STA-safe.</summary>
        Task<ImportResultDto> ImportTypeFromFileAsync(string filePath, bool overwrite);
        /// <summary>Export every block/type/tag table of the current PLC under targetDir. STA-safe.</summary>
        Task<ExportAllResultDto> ExportAllAsync(string targetDir);
        /// <summary>Save the current project (if modified) on the STA host thread. STA-safe.</summary>
        Task<bool> SaveProjectAsync();
        
        void SetTiaPortal(TiaPortal portal);
        void SetProject(Project project);
        void SetPlcSoftware(PlcSoftware software);
        
        event EventHandler<bool> ConnectionStatusChanged;

        /// <summary>Raised after a block is imported/modified via Openness (arg = block name).</summary>
        event EventHandler<string> BlockChanged;

        /// <summary>
        /// Raised after a successful block write-back. Carries a pre-import snapshot for undo.
        /// </summary>
        event EventHandler<BlockWrittenEventArgs> BlockWritten;

        // ===== MCP high-priority tool migrations =====

        /// <summary>
        /// Bootstrap tool for the first call: returns TIA version, connection state, and recommended next tool.
        /// </summary>
        Task<BootstrapInfo> BootstrapAsync();

        /// <summary>
        /// Attach to an already-open TIA Portal project.
        /// </summary>
        Task<bool> AttachToOpenProjectAsync(string projectName);

        /// <summary>
        /// Delete a PLC block.
        /// </summary>
        Task<bool> DeleteBlockAsync(string blockName);

        /// <summary>
        /// List all tag tables (including sub-groups) as "TableName.TagName: DataType @ Address".
        /// </summary>
        Task<IList<string>> ListTagTablesAsync(int limit = 500);

        /// <summary>
        /// Get a DB block's structure: exports XML, parses Member nodes, and returns "Name: Datatype" lines.
        /// </summary>
        Task<IList<string>> GetDbStructureAsync(string dbName);

        /// <summary>
        /// List all UDT types (with member counts) as "TypeName (12 members)".
        /// </summary>
        Task<IList<string>> ListUdtTypesAsync(int limit = 200);

        /// <summary>
        /// Get the compilation result details.
        /// </summary>
        Task<CompilationResult> GetCompilationResultAsync();

        /// <summary>
        /// Search the hardware catalog.
        /// </summary>
        Task<List<HardwareCatalogItem>> SearchHardwareCatalogAsync(string searchTerm);

        /// <summary>
        /// Create a new device/station in the open project from a hardware-catalog TypeIdentifier
        /// (e.g. "OrderNumber:6ES7 518-4AP00-0AB0/V2.1"). STA-safe. Returns a human-readable status.
        /// </summary>
        Task<string> AddDeviceAsync(string typeIdentifier, string deviceName);

        /// <summary>Set an attribute on a device item (by name). STA-safe.</summary>
        Task<string> SetDeviceItemAttributeAsync(string deviceItemName, string attributeName, string value);

        /// <summary>Insert a module under a parent device item (by name). STA-safe.</summary>
        Task<string> AddModuleAsync(string parentDeviceItemName, string moduleName, string orderNumber);

        /// <summary>Create a subnet and connect a device item to it. STA-safe.</summary>
        Task<string> ConfigureSubnetAsync(string deviceItemName, string subnetName, string subnetType);

        /// <summary>
        /// Get all cross-reference usages of a variable.
        /// </summary>
        /// <param name="variableName">Variable name.</param>
        /// <param name="blockName">Optional: restrict search to this block.</param>
        Task<CrossReferenceResult> GetCrossReferencesAsync(string variableName, string blockName = null);
    }

    // ===== MCP-related data models =====

    public class BootstrapInfo
    {
        public string TiaVersion { get; set; }
        public bool OpennessGroupValid { get; set; }
        public bool IsConnected { get; set; }
        public string CurrentProjectName { get; set; }
        public string RecommendedNextTool { get; set; }
        public List<string> KnownLimitations { get; set; }
    }

    /// <summary>
    /// Live environment facts of the currently open TIA project. Facts only — feature support
    /// (optimized access / DTL / R_TRIG) is derived by the consumers from <see cref="PlcFamily"/>,
    /// so there is exactly one rule table per runtime.
    /// </summary>
    public class EnvironmentProfileInfo
    {
        public string TiaVersion { get; set; }
        public string ProjectKey { get; set; }
        public string ProjectName { get; set; }
        public string PlcName { get; set; }
        public string DeviceName { get; set; }
        public string CpuName { get; set; }
        public string OrderNumber { get; set; }
        public string TypeIdentifier { get; set; }
        /// <summary>S7-300/400 | S7-1200 | S7-1500 | Unknown.</summary>
        public string PlcFamily { get; set; }
        /// <summary>SHA1(tia_version|family|order_number|project_key), first 12 hex chars.</summary>
        public string Fingerprint { get; set; }
        public string DetectedAt { get; set; }
        /// <summary>False when the live read failed; the remaining fields are then best-effort.</summary>
        public bool Live { get; set; }
        /// <summary>Every device in the project (bootstrap parity); top-level fields above point at the selected PLC.</summary>
        public List<EnvironmentDeviceInfo> Devices { get; set; } = new List<EnvironmentDeviceInfo>();
    }

    public class EnvironmentDeviceInfo
    {
        public string Name { get; set; }
        public string TypeIdentifier { get; set; }
        public string CpuName { get; set; }
        public string OrderNumber { get; set; }
        public string PlcFamily { get; set; }
    }

    public class CompilationResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public List<CompilationError> Errors { get; set; }
        public List<CompilationWarning> Warnings { get; set; }
    }

    public class CompilationError
    {
        public string BlockName { get; set; }
        public string Message { get; set; }
        public int LineNumber { get; set; }
    }

    public class CompilationWarning
    {
        public string BlockName { get; set; }
        public string Message { get; set; }
    }

    public class HardwareCatalogItem
    {
        public string Name { get; set; }
        public string TypeIdentifier { get; set; }
        public string OrderNumber { get; set; }
        public string Version { get; set; }
    }
}
