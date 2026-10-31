using System.Collections.Generic;
using System.Linq;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Tool definition: metadata mapping a Portal method to an Agent tool.
    /// </summary>
    public sealed class TiaToolDefinition
    {
        /// <summary>Name visible to the agent (e.g. "tia_go_online")</summary>
        public string Name { get; set; }

        /// <summary>Description the agent uses to select the tool</summary>
        public string Description { get; set; }

        /// <summary>JSON Schema string defining the input parameters</summary>
        public string InputSchema { get; set; }

        /// <summary>Minimum supported TIA Portal major version</summary>
        public int MinVersion { get; set; }

        /// <summary>Permission level (Allowed for reads, RequiresConfirmation for writes)</summary>
        public ToolPermissionLevel Permission { get; set; }

        /// <summary>Corresponding method name on the Portal class (for reflection or switch routing)</summary>
        public string PortalMethodName { get; set; }

        /// <summary>Category label used for UI grouping</summary>
        public string Category { get; set; }
    }

    /// <summary>
    /// Central registry for the TIA Portal in-process tool definitions. All
    /// Portal → Agent Tool mappings are maintained here.
    /// </summary>
    public static class TiaToolDefinitionRegistry
    {
        private static readonly List<TiaToolDefinition> _definitions = new List<TiaToolDefinition>();

        static TiaToolDefinitionRegistry()
        {
            RegisterAll();
        }

        /// <summary>Gets all tool definitions</summary>
        public static IReadOnlyList<TiaToolDefinition> AllDefinitions => _definitions;

        /// <summary>Filters the available tool definitions by TIA major version</summary>
        public static IEnumerable<TiaToolDefinition> GetAvailable(int majorVersion)
        {
            return _definitions.Where(d => majorVersion >= d.MinVersion);
        }

        /// <summary>Finds a definition by name</summary>
        public static TiaToolDefinition Find(string name)
        {
            return _definitions.FirstOrDefault(d => d.Name == name);
        }

        private static void RegisterAll()
        {
            // ═══════════════════════════════════════════════════════════════
            // Online / Offline (V17+)
            // ═══════════════════════════════════════════════════════════════

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_get_online_state",
                Description = "[TIA] Get the online/offline state of a PLC. Returns state, reachability, and diagnostic message.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path (e.g. 'PLC_1').""}},""required"":[""software_path""]}",
                MinVersion = 17,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "GetOnlineState",
                Category = "Online"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_go_online",
                Description = "[TIA] Establish an online connection to a PLC. Optional IP address and password for protected PLCs.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path (e.g. 'PLC_1').""}, ""ip_address"":{""type"":""string"",""description"":""Optional IP address override.""}, ""password"":{""type"":""string"",""description"":""Optional PLC password.""}},""required"":[""software_path""]}",
                MinVersion = 17,
                Permission = ToolPermissionLevel.RequiresConfirmation,
                PortalMethodName = "GoOnline",
                Category = "Online"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_go_offline",
                Description = "[TIA] Disconnect from an online PLC connection.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path.""}},""required"":[""software_path""]}",
                MinVersion = 17,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "GoOffline",
                Category = "Online"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_compare_online",
                Description = "[TIA] Compare offline project blocks with the online PLC. Returns differences tree.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path.""}, ""max_depth"":{""type"":""integer"",""description"":""Max tree depth (default 4).""}, ""max_entries"":{""type"":""integer"",""description"":""Max difference entries (default 200).""}},""required"":[""software_path""]}",
                MinVersion = 17,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "CompareSoftwareToOnline",
                Category = "Online"
            });

            // ═══════════════════════════════════════════════════════════════
            // Download (V17+)
            // ═══════════════════════════════════════════════════════════════

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_download_to_plc",
                Description = "[TIA] Download the project to the PLC. Compiles and transfers blocks. Supports stop/start/keep-values options.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path.""}, ""consistent_blocks_only"":{""type"":""boolean"",""description"":""Download only consistent blocks (default true).""}, ""keep_actual_values"":{""type"":""boolean"",""description"":""Keep actual DB values on PLC (default true).""}, ""start_after_download"":{""type"":""boolean"",""description"":""Start PLC after download (default true).""}, ""stop_before_download"":{""type"":""boolean"",""description"":""Stop PLC before download (default true).""}, ""password"":{""type"":""string"",""description"":""PLC password if required.""}},""required"":[""software_path""]}",
                MinVersion = 17,
                Permission = ToolPermissionLevel.RequiresConfirmation,
                PortalMethodName = "DownloadToPlc",
                Category = "Download"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_check_download_readiness",
                Description = "[TIA] Check if the PLC is ready for download. Validates network configuration and compile state.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path.""}},""required"":[""software_path""]}",
                MinVersion = 17,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "CheckDownloadReadiness",
                Category = "Download"
            });

            // ═══════════════════════════════════════════════════════════════
            // Watch / Force Tables (V16+)
            // ═══════════════════════════════════════════════════════════════

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_get_watch_tables",
                Description = "[TIA] List all PLC watch tables and their paths.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path.""}},""required"":[""software_path""]}",
                MinVersion = 16,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "GetPlcWatchTables",
                Category = "WatchTable"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_ensure_watch_entry",
                Description = "[TIA] Add or update a watch table entry. Creates the table if it doesn't exist.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path.""}, ""table_name"":{""type"":""string"",""description"":""Watch table name.""}, ""address"":{""type"":""string"",""description"":""Variable address or symbolic name.""}, ""display_format"":{""type"":""string"",""description"":""Display format (e.g. DEC, HEX, BIN).""}},""required"":[""software_path"",""table_name"",""address""]}",
                MinVersion = 16,
                Permission = ToolPermissionLevel.RequiresConfirmation,
                PortalMethodName = "EnsureWatchTableEntry",
                Category = "WatchTable"
            });

            // ═══════════════════════════════════════════════════════════════
            // Cross-Reference (V16+)
            // ═══════════════════════════════════════════════════════════════

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_get_cross_references",
                Description = "[TIA] Get cross-references for a PLC object. Shows all blocks that read/write the specified variable or call the specified block.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path.""}, ""object_path"":{""type"":""string"",""description"":""Object path (block or tag name).""}, ""object_kind"":{""type"":""string"",""description"":""Object kind: Block, Tag, Type (default: Block).""}, ""filter"":{""type"":""string"",""description"":""Filter: AllObjects, ReadsOnly, WritesOnly (default: AllObjects).""}},""required"":[""software_path"",""object_path""]}",
                MinVersion = 16,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "GetCrossReferences",
                Category = "CrossReference"
            });

            // ═══════════════════════════════════════════════════════════════
            // Alarm Management (V18+)
            // ═══════════════════════════════════════════════════════════════

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_export_alarm_classes",
                Description = "[TIA] Export PLC alarm class definitions to an XML file.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path.""}, ""export_path"":{""type"":""string"",""description"":""File path for the exported XML.""}},""required"":[""software_path"",""export_path""]}",
                MinVersion = 18,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "ExportAlarmClasses",
                Category = "Alarm"
            });

            // ═══════════════════════════════════════════════════════════════
            // Read-only inspection tools (safe, Allowed) — batch 1
            // ═══════════════════════════════════════════════════════════════

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_get_tag_tables",
                Description = "[TIA] List all PLC tag tables for the given PLC software.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path (e.g. 'PLC_1').""}},""required"":[""software_path""]}",
                MinVersion = 16,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "GetPlcTagTables",
                Category = "Inspect"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_get_force_tables",
                Description = "[TIA] List all PLC force tables (read-only listing of table names).",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path.""}},""required"":[""software_path""]}",
                MinVersion = 16,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "GetPlcForceTables",
                Category = "Inspect"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_get_technology_objects",
                Description = "[TIA] List technology objects (e.g. axes, PID) in the PLC software.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path.""}},""required"":[""software_path""]}",
                MinVersion = 16,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "GetTechnologyObjects",
                Category = "Inspect"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_get_hmi_program_info",
                Description = "[TIA] Get HMI program overview (name, program type, screen list) for an HMI software path.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""HMI software path (e.g. 'HMI_RT_1').""}},""required"":[""software_path""]}",
                MinVersion = 16,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "GetHmiProgramInfo",
                Category = "Inspect"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_get_external_sources",
                Description = "[TIA] List PLC external source files (SCL/AWL sources) in the PLC software.",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path.""}},""required"":[""software_path""]}",
                MinVersion = 16,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "GetPlcExternalSources",
                Category = "Inspect"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_get_opcua_config",
                Description = "[TIA] Read the OPC UA server configuration of the PLC software (read-only report).",
                InputSchema = @"{""type"":""object"",""properties"":{""software_path"":{""type"":""string"",""description"":""PLC software path.""}},""required"":[""software_path""]}",
                MinVersion = 18,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "GetOpcUaConfig",
                Category = "Inspect"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_get_project_tree",
                Description = "[TIA] Get the whole project tree (devices, PLCs, HMIs) as a structured text overview.",
                InputSchema = @"{""type"":""object"",""properties"":{},""required"":[]}",
                MinVersion = 16,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "GetProjectTree",
                Category = "Inspect"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_search_hardware_catalog",
                Description = "[TIA] Search the hardware catalog for devices/modules by keyword (e.g. 'CPU 1516', order number).",
                InputSchema = @"{""type"":""object"",""properties"":{""keyword"":{""type"":""string"",""description"":""Search keyword.""}, ""limit"":{""type"":""integer"",""description"":""Max candidates (default 50).""}},""required"":[""keyword""]}",
                MinVersion = 16,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "SearchHardwareCatalog",
                Category = "Inspect"
            });

            _definitions.Add(new TiaToolDefinition
            {
                Name = "tia_get_device_item_tree",
                Description = "[TIA] Get the device item (module/rack) tree under a device item path.",
                InputSchema = @"{""type"":""object"",""properties"":{""device_item_path"":{""type"":""string"",""description"":""Device item path.""}, ""max_depth"":{""type"":""integer"",""description"":""Max tree depth (default 4).""}},""required"":[""device_item_path""]}",
                MinVersion = 16,
                Permission = ToolPermissionLevel.Allowed,
                PortalMethodName = "GetDeviceItemTree",
                Category = "Inspect"
            });
        }
    }
}