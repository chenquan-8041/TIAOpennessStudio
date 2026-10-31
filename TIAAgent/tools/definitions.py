import os
import glob
from langchain_core.tools import tool


def _get_workspace_root() -> str:
    current_dir = os.path.dirname(os.path.abspath(__file__))
    return os.path.dirname(os.path.dirname(current_dir))


# These curated tables are concept-level summaries distilled from one documentation
# snapshot (Openness 11/2023). They are deliberately version-independent; a version-
# specific type/method signature must come from query_openness_api(query, version).
_OPENNESS_CONCEPT_NOTE = (
    "Source: curated Openness concept summary (version-independent). "
    "For version-specific API signatures call query_openness_api(query, version)."
)


PLC_API_METHODS = {
    "连接与打开项目": {
        "description": "Connect to an existing TIA process or create a new instance, then open the project.",
        "object_path": "TiaPortal -> Projects -> Project",
        "methods": [
            "TiaPortal.GetProcesses()",
            "TiaPortalProcess.Attach()",
            "TiaPortal(TiaPortalMode.WithUserInterface)",
            "tiaPortal.Projects.Open(FileInfo)",
            "project.Save()",
            "project.Close()"
        ],
        "sample": "var processes = TiaPortal.GetProcesses(); var tia = processes.Any() ? processes.First().Attach() : new TiaPortal(TiaPortalMode.WithUserInterface); var project = tia.Projects.Open(new FileInfo(path));",
        "doc_refs": ["page 84", "page 159", "page 160"]
    },
    "定位PLC软件": {
        "description": "Locate PlcSoftware recursively from the hardware hierarchy.",
        "object_path": "Project.Devices -> Device.DeviceItems -> GetService<SoftwareContainer>() -> PlcSoftware",
        "methods": [
            "device.DeviceItems",
            "((IEngineeringServiceProvider)deviceItem).GetService<SoftwareContainer>()",
            "softwareContainer.Software as PlcSoftware"
        ],
        "sample": "foreach (DeviceItem di in device.DeviceItems) { var sc = ((IEngineeringServiceProvider)di).GetService<SoftwareContainer>(); var plc = sc?.Software as PlcSoftware; }",
        "doc_refs": ["page 71", "page 72", "page 84", "page 160"]
    },
    "块导入导出": {
        "description": "Import, export and find PLC Blocks/Types.",
        "object_path": "PlcSoftware.BlockGroup / PlcSoftware.TypeGroup",
        "methods": [
            "plcSoftware.BlockGroup.Blocks.Import(FileInfo, ImportOptions)",
            "plcSoftware.TypeGroup.Types.Import(FileInfo, ImportOptions)",
            "plcSoftware.BlockGroup.Blocks.Find(name)",
            "block.Export(FileInfo, ExportOptions)",
            "type.Export(FileInfo, ExportOptions)"
        ],
        "sample": "plcSoftware.BlockGroup.Blocks.Import(new FileInfo(xmlPath), ImportOptions.Override);",
        "doc_refs": ["page 123", "page 130"]
    },
    "块与分组管理": {
        "description": "Create and delete Block/Type user groups and manage their objects.",
        "object_path": "PlcBlockUserGroupComposition / PlcTypeUserGroupComposition",
        "methods": [
            "plcSoftware.BlockGroup.Groups.Create(name)",
            "plcSoftware.TypeGroup.Groups.Create(name)",
            "group.Blocks.Find(name)",
            "engineeringObject.Invoke(\"Delete\", args)",
            "block.Delete()"
        ],
        "sample": "var g = plcSoftware.BlockGroup.Groups.Create(\"Main\");",
        "doc_refs": ["page 121", "page 123"]
    },
    "PLC编译": {
        "description": "Compile PlcSoftware or a specific code block and read the compile result.",
        "object_path": "PlcSoftware -> GetService<ICompilable>()",
        "methods": [
            "plcSoftware.GetService<ICompilable>()",
            "compileService.Compile()",
            "block.GetService<ICompilable>()",
            "CompilerResult.Messages"
        ],
        "sample": "ICompilable c = plcSoftware.GetService<ICompilable>(); CompilerResult r = c.Compile();",
        "doc_refs": ["page 168", "page 169", "page 170"]
    },
    "标签表与变量": {
        "description": "Read TagTable and Tags to obtain variable information.",
        "object_path": "PlcSoftware.TagTableGroup -> TagTables -> Tags",
        "methods": [
            "plcSoftware.TagTableGroup.TagTables",
            "tagTable.Tags",
            "tag.Name / tag.DataTypeName"
        ],
        "sample": "foreach (var table in plcSoftware.TagTableGroup.TagTables) { foreach (var tag in table.Tags) { var n = tag.Name; } }",
        "doc_refs": ["page 11", "page 61"]
    },
    "服务发现与泛化调用": {
        "description": "Discover the services available on an accessible object and call them on demand.",
        "object_path": "IEngineeringServiceProvider",
        "methods": [
            "GetService<T>() where T : class, IEngineeringService",
            "GetServiceInfos()",
            "GetAttributeInfos()",
            "GetCompositionInfos()"
        ],
        "sample": "var infos = ((IEngineeringServiceProvider)obj).GetServiceInfos();",
        "doc_refs": ["page 121", "page 135", "page 154"]
    }
}


CSHARP_METHOD_TEMPLATES = {
    "连接与打开项目": """using Siemens.Engineering;
    using System;
    using System.IO;
    using System.Linq;

    public static class OpennessProjectRuntime
    {
        public static (TiaPortal tiaPortal, Project project) ConnectOrOpenProject(string projectPath, bool withUi = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectPath))
                    throw new ArgumentException("projectPath 不能为空");

                if (!File.Exists(projectPath))
                    throw new FileNotFoundException("未找到项目文件", projectPath);

                TiaPortal tiaPortal;
                var process = TiaPortal.GetProcesses().FirstOrDefault();
                tiaPortal = process != null
                    ? process.Attach()
                    : new TiaPortal(withUi ? TiaPortalMode.WithUserInterface : TiaPortalMode.WithoutUserInterface);

                var project = tiaPortal.Projects.Count > 0
                    ? tiaPortal.Projects[0]
                    : tiaPortal.Projects.Open(new FileInfo(projectPath));

                return (tiaPortal, project);
            }
            catch (Exception)
            {
                throw;
            }
        }
    }""",
        "定位PLC软件": """using Siemens.Engineering;
    using Siemens.Engineering.HW;
    using Siemens.Engineering.HW.Features;
    using Siemens.Engineering.SW;
    using System;

    public static class OpennessPlcLocator
    {
        public static PlcSoftware FindPlcSoftware(Project project)
        {
            try
            {
                if (project == null) throw new ArgumentNullException(nameof(project));
                foreach (Device device in project.Devices)
                {
                    var plc = FindPlcSoftwareFromItems(device.DeviceItems);
                    if (plc != null) return plc;
                }
                return null;
            }
            catch (Exception)
            {
                throw;
            }
        }

        private static PlcSoftware FindPlcSoftwareFromItems(DeviceItemComposition items)
        {
            foreach (DeviceItem item in items)
            {
                var container = ((IEngineeringServiceProvider)item).GetService<SoftwareContainer>();
                if (container?.Software is PlcSoftware plcSoftware) return plcSoftware;
                var nested = FindPlcSoftwareFromItems(item.DeviceItems);
                if (nested != null) return nested;
            }
            return null;
        }
    }""",
        "块导入导出": """using Siemens.Engineering.SW;
    using Siemens.Engineering.SW.Blocks;
    using System;
    using System.IO;

    public static class OpennessBlockTransfer
    {
        public static void ImportBlockAndExport(PlcSoftware plcSoftware, string importXml, string exportXml, string blockName)
        {
            try
            {
                if (plcSoftware == null) throw new ArgumentNullException(nameof(plcSoftware));
                if (!File.Exists(importXml)) throw new FileNotFoundException("导入文件不存在", importXml);
                if (string.IsNullOrWhiteSpace(blockName)) throw new ArgumentException("blockName 不能为空");

                plcSoftware.BlockGroup.Blocks.Import(new FileInfo(importXml), ImportOptions.Override);
                var block = plcSoftware.BlockGroup.Blocks.Find(blockName) as PlcBlock;
                if (block == null) throw new InvalidOperationException($"导入后未找到块: {blockName}");

                var exportDir = Path.GetDirectoryName(exportXml);
                if (!string.IsNullOrWhiteSpace(exportDir) && !Directory.Exists(exportDir))
                    Directory.CreateDirectory(exportDir);

                block.Export(new FileInfo(exportXml), ExportOptions.None);
            }
            catch (Exception)
            {
                throw;
            }
        }
    }""",
        "块与分组管理": """using Siemens.Engineering.SW;
    using Siemens.Engineering.SW.Blocks;
    using System;

    public static class OpennessGroupManager
    {
        public static PlcBlockUserGroup EnsureBlockGroup(PlcSoftware plcSoftware, string groupName)
        {
            try
            {
                if (plcSoftware == null) throw new ArgumentNullException(nameof(plcSoftware));
                if (string.IsNullOrWhiteSpace(groupName)) throw new ArgumentException("groupName 不能为空");

                var existing = plcSoftware.BlockGroup.Groups.Find(groupName);
                return existing ?? plcSoftware.BlockGroup.Groups.Create(groupName);
            }
            catch (Exception)
            {
                throw;
            }
        }
    }""",
        "PLC编译": """using Siemens.Engineering.Compiler;
    using Siemens.Engineering.SW;
    using System;
    using System.Collections.Generic;

    public static class OpennessCompiler
    {
        public static (CompilerResult result, List<string> messages) CompilePlcSoftware(PlcSoftware plcSoftware)
        {
            try
            {
                if (plcSoftware == null) throw new ArgumentNullException(nameof(plcSoftware));

                var compileService = plcSoftware.GetService<ICompilable>();
                if (compileService == null) throw new InvalidOperationException("未获取到 ICompilable 服务");

                var result = compileService.Compile();
                var lines = new List<string>();
                CollectMessages(result.Messages, lines);
                return (result, lines);
            }
            catch (Exception)
            {
                throw;
            }
        }

        private static void CollectMessages(CompilerResultMessageComposition messages, List<string> output)
        {
            foreach (CompilerResultMessage msg in messages)
            {
                output.Add($\"{msg.State}: {msg.Description}\");
                CollectMessages(msg.Messages, output);
            }
        }
    }""",
        "标签表与变量": """using Siemens.Engineering.SW;
    using Siemens.Engineering.SW.Tags;
    using System;
    using System.Collections.Generic;

    public static class OpennessTagReader
    {
        public static List<string> ReadPlcTags(PlcSoftware plcSoftware)
        {
            try
            {
                if (plcSoftware == null) throw new ArgumentNullException(nameof(plcSoftware));

                var result = new List<string>();
                foreach (PlcTagTable table in plcSoftware.TagTableGroup.TagTables)
                    foreach (PlcTag tag in table.Tags)
                        result.Add($\"{table.Name}.{tag.Name}:{tag.DataTypeName}\");

                return result;
            }
            catch (Exception)
            {
                throw;
            }
        }
    }""",
        "服务发现与泛化调用": """using Siemens.Engineering;
    using System;
    using System.Linq;

    public static class OpennessServiceInspector
    {
        public static string[] ListServiceTypes(IEngineeringServiceProvider provider)
        {
            try
            {
                if (provider == null) throw new ArgumentNullException(nameof(provider));
                return provider.GetServiceInfos()
                    .Select(s => s.Type?.FullName ?? "Unknown")
                    .OrderBy(x => x)
                    .ToArray();
            }
            catch (Exception)
            {
                throw;
            }
        }
    }"""
}

PLC_RUNTIME_DIAGNOSTIC_APIS = {
    "运行模式与在线状态": {
        "description": "Used to inspect PLC operating mode, online/offline available services and state-switching capability.",
        "object_path": "PlcSoftware / Controller target -> IEngineeringServiceProvider",
        "methods": [
            "GetServiceInfos()",
            "GetService<T>()",
            "provider.GetServiceInfos().Select(x => x.Type.FullName)",
            "GoOffline()/GoOnline() (service-dependent: discover the provider and call only when the service exists)"
        ],
        "doc_refs": ["page 1919", "page 1920", "page 121", "page 154"],
        "note": "Service names may differ between TIA versions; do service discovery first, then call runtime state switching."
    },
    "编译诊断状态": {
        "description": "Obtain PLC compile results through ICompilable and use them for diagnostics.",
        "object_path": "PlcSoftware -> GetService<ICompilable>() -> Compile()",
        "methods": [
            "plcSoftware.GetService<ICompilable>()",
            "compileService.Compile()",
            "CompilerResult.Messages",
            "CompilerResultMessage.State / Description"
        ],
        "doc_refs": ["page 168", "page 169", "page 170", "page 1917"],
        "note": "Suitable as an entry point for \"current runnability diagnostics\"; not directly equivalent to the CPU run LED state."
    },
    "系统诊断导入导出": {
        "description": "Read/import system diagnostics settings and check the import/export result state.",
        "object_path": "Project -> SystemdiagnosticsSettingsProvider",
        "methods": [
            "project.GetService<SystemdiagnosticsSettingsProvider>()",
            "settingsProvider.Export(FileInfo)",
            "settingsProvider.Import(FileInfo)",
            "SystemdiagnosticsSettingsExportImportResult.State"
        ],
        "doc_refs": ["page 174"],
        "note": "Suitable for diagnostics configuration sync scenarios; not a CPU online runtime state query."
    }
}

PLC_RUNTIME_ANALYSIS_RECIPES = {
    "变量-数据块-程序逻辑综合诊断": {
        "goal": "Analyze tags, data blocks (DB/UDT) and program block logic together, and output a diagnostic report that can pinpoint problems.",
        "steps": [
            "Locate PlcSoftware and discover the available diagnostics/compile services",
            "Collect the variable list from TagTableGroup.TagTables.Tags",
            "Traverse BlockGroup and TypeGroup and collect DB/FB/FC/UDT names and types",
            "Export key block content for logic analysis: LAD/FBD/GRAPH as canonical JSON "
            "(get_block_content format=\"ir\"); SCL/STL as reconstructed source "
            "(get_block_content format=\"source\"). "
            "Never assume source text exists for a graphical block",
            "Run ICompilable.Compile() and summarize compile warnings and errors",
            "Cross-check: missing variable references, naming/type mismatches between DBs and logic blocks, unused or unmapped objects",
            "Output the structured diagnostic result (Error/Warning/Info)"
        ],
        "version_note": (
            "Workflow-level recipe. API availability is version-specific: verify with "
            "query_openness_api(query, version), or probe_openness_capabilities when the "
            "version has no bundled docs (V18+)."
        ),
        "openness_api": [
            "plcSoftware.TagTableGroup.TagTables -> table.Tags",
            "plcSoftware.BlockGroup.Blocks / Groups",
            "plcSoftware.TypeGroup.Types / Groups",
            "plcSoftware.GetService<ICompilable>().Compile()",
            "block.Export(FileInfo, ExportOptions.None)"
        ],
        "doc_refs": ["page 11", "page 61", "page 121", "page 123", "page 168", "page 169", "page 170", "page 1917"]
    }
}

PLC_RUNTIME_ANALYSIS_TEMPLATE = """using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections.Generic;
using System.Linq;

public static class PlcRuntimeAnalyzer
{
    public static PlcDiagnosticReport Analyze(PlcSoftware plcSoftware)
    {
        if (plcSoftware == null) throw new ArgumentNullException(nameof(plcSoftware));

        var report = new PlcDiagnosticReport();
        try
        {
            CollectTags(plcSoftware, report);
            CollectBlocksAndTypes(plcSoftware, report);
            CollectCompileDiagnostics(plcSoftware, report);
            CrossCheck(report);
        }
        catch (Exception ex)
        {
            report.Errors.Add("Analyze failed: " + ex.Message);
        }
        return report;
    }

    private static void CollectTags(PlcSoftware plcSoftware, PlcDiagnosticReport report)
    {
        foreach (PlcTagTable table in plcSoftware.TagTableGroup.TagTables)
            foreach (PlcTag tag in table.Tags)
                report.Tags.Add($"{table.Name}.{tag.Name}:{tag.DataTypeName}");
    }

    private static void CollectBlocksAndTypes(PlcSoftware plcSoftware, PlcDiagnosticReport report)
    {
        foreach (PlcBlock block in plcSoftware.BlockGroup.Blocks)
            report.Blocks.Add($"{block.Name}:{block.GetType().Name}");
        foreach (PlcType type in plcSoftware.TypeGroup.Types)
            report.Types.Add($"{type.Name}:{type.GetType().Name}");
    }

    private static void CollectCompileDiagnostics(PlcSoftware plcSoftware, PlcDiagnosticReport report)
    {
        var compilable = plcSoftware.GetService<ICompilable>();
        if (compilable == null)
        {
            report.Warnings.Add("ICompilable service not available.");
            return;
        }

        var result = compilable.Compile();
        FlattenMessages(result.Messages, report);
    }

    private static void FlattenMessages(CompilerResultMessageComposition messages, PlcDiagnosticReport report)
    {
        foreach (CompilerResultMessage msg in messages)
        {
            var line = $"{msg.State}: {msg.Description}";
            if (msg.State.ToString().IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
                report.Errors.Add(line);
            else if (msg.State.ToString().IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0)
                report.Warnings.Add(line);
            else
                report.Infos.Add(line);

            FlattenMessages(msg.Messages, report);
        }
    }

    private static void CrossCheck(PlcDiagnosticReport report)
    {
        if (report.Tags.Count == 0)
            report.Warnings.Add("No PLC tags found.");
        if (report.Blocks.Count == 0)
            report.Errors.Add("No PLC blocks found.");

        var duplicatedBlocks = report.Blocks.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicatedBlocks.Count > 0)
            report.Warnings.Add("Duplicated blocks: " + string.Join(", ", duplicatedBlocks));
    }
}

public class PlcDiagnosticReport
{
    public List<string> Tags { get; } = new List<string>();
    public List<string> Blocks { get; } = new List<string>();
    public List<string> Types { get; } = new List<string>();
    public List<string> Errors { get; } = new List<string>();
    public List<string> Warnings { get; } = new List<string>();
    public List<string> Infos { get; } = new List<string>();
}
"""


def _match_topic(keyword: str) -> str:
    text = (keyword or "").strip().lower()
    if not text:
        return ""
    mapping = {
        "连接": "连接与打开项目",
        "打开": "连接与打开项目",
        "project": "连接与打开项目",
        "softwarecontainer": "定位PLC软件",
        "plcsoftware": "定位PLC软件",
        "deviceitem": "定位PLC软件",
        "import": "块导入导出",
        "export": "块导入导出",
        "block": "块导入导出",
        "group": "块与分组管理",
        "delete": "块与分组管理",
        "compile": "PLC编译",
        "编译": "PLC编译",
        "tag": "标签表与变量",
        "service": "服务发现与泛化调用",
        "getservice": "服务发现与泛化调用",
        # All block kinds (incl. GRAPH/FBD/UDT/DB) go through the same
        # BlockGroup/TypeGroup import-export API.
        "graph": "块导入导出",
        "fbd": "块导入导出",
        "udt": "块导入导出",
        "data block": "块导入导出",
        "datablock": "块导入导出",
        "interface": "块导入导出",
        "canonical": "块导入导出"
    }
    for k, v in mapping.items():
        if k in text:
            return v
    for topic in PLC_API_METHODS.keys():
        if topic in keyword:
            return topic
    return ""


@tool
def search_openness_docs(query: str) -> str:
    """Search for matching Openness PLC API topics and key methods for a query."""
    topic = _match_topic(query)
    if topic and topic in PLC_API_METHODS:
        data = PLC_API_METHODS[topic]
        return (
            _OPENNESS_CONCEPT_NOTE + "\n"
            f"Matched topic: {topic}\n"
            f"Description: {data['description']}\n"
            f"Object path: {data['object_path']}\n"
            f"Key methods: {', '.join(data['methods'])}\n"
            f"Example: {data['sample']}\n"
            f"Documentation refs: {', '.join(data['doc_refs'])}"
        )
    return (
        _OPENNESS_CONCEPT_NOTE + "\n"
        "No exact topic match. Call list_plc_openness_topics first to see the topics, then call "
        "get_plc_openness_api_methods(topic) to get the concrete methods."
    )


@tool
def list_plc_openness_topics() -> str:
    """List the available PLC Openness API topics."""
    topics = "\n".join([f"- {k}: {v['description']}" for k, v in PLC_API_METHODS.items()])
    return _OPENNESS_CONCEPT_NOTE + "\nAvailable PLC Openness API topics:\n" + topics


@tool
def get_plc_openness_api_methods(topic: str) -> str:
    """Output the concrete methods, object paths and call examples for a PLC Openness API topic."""
    normalized = topic.strip()
    if normalized not in PLC_API_METHODS:
        guessed = _match_topic(topic)
        if guessed and guessed in PLC_API_METHODS:
            normalized = guessed
        else:
            return (
                _OPENNESS_CONCEPT_NOTE + "\n"
                "Topic not found. Call list_plc_openness_topics first to get the available topic names."
            )

    data = PLC_API_METHODS[normalized]
    methods = "\n".join([f"  - {m}" for m in data["methods"]])
    return (
        _OPENNESS_CONCEPT_NOTE + "\n"
        f"Topic: {normalized}\n"
        f"Description: {data['description']}\n"
        f"Object path: {data['object_path']}\n"
        f"Concrete methods:\n{methods}\n"
        f"Typical call: {data['sample']}\n"
        f"Documentation reference pages: {', '.join(data['doc_refs'])}"
    )


@tool
def get_plc_openness_csharp_template(topic: str) -> str:
    """Return a directly reusable TIA Openness C# method template for a topic."""
    normalized = topic.strip()
    if normalized not in CSHARP_METHOD_TEMPLATES:
        guessed = _match_topic(topic)
        if guessed and guessed in CSHARP_METHOD_TEMPLATES:
            normalized = guessed
        else:
            return (
                _OPENNESS_CONCEPT_NOTE + "\n"
                "Template not found for this topic. Call list_plc_openness_topics to get a topic, then query again."
            )

    code = CSHARP_METHOD_TEMPLATES[normalized]
    return (
        _OPENNESS_CONCEPT_NOTE + "\n"
        f"Topic: {normalized}\n"
        f"Template note: Below is a C# method template that can be pasted directly and adjusted per project.\n"
        f"C# template:\n{code}"
    )

@tool
def get_plc_runtime_diagnostic_api(topic: str = "") -> str:
    """Return the Openness API method list for PLC runtime/diagnostic topics (per docs)."""
    key = (topic or "").strip()
    if not key:
        lines = []
        for name, data in PLC_RUNTIME_DIAGNOSTIC_APIS.items():
            lines.append(f"- {name}: {data['description']}")
        return _OPENNESS_CONCEPT_NOTE + "\nAvailable PLC runtime/diagnostic topics:\n" + "\n".join(lines)

    matched = ""
    if key in PLC_RUNTIME_DIAGNOSTIC_APIS:
        matched = key
    else:
        k = key.lower()
        if "在线" in key or "离线" in key or "运行" in key or "mode" in k or "online" in k:
            matched = "运行模式与在线状态"
        elif "编译" in key or "compile" in k:
            matched = "编译诊断状态"
        elif "系统诊断" in key or "diagnostic" in k or "export" in k or "import" in k:
            matched = "系统诊断导入导出"

    if not matched:
        return (
            _OPENNESS_CONCEPT_NOTE + "\n"
            "No matching topic found. Call get_plc_runtime_diagnostic_api() first to see the available topics."
        )

    data = PLC_RUNTIME_DIAGNOSTIC_APIS[matched]
    methods = "\n".join([f"  - {m}" for m in data["methods"]])
    return (
        _OPENNESS_CONCEPT_NOTE + "\n"
        f"Topic: {matched}\n"
        f"Description: {data['description']}\n"
        f"Object path: {data['object_path']}\n"
        f"Documented methods:\n{methods}\n"
        f"Documentation reference pages: {', '.join(data['doc_refs'])}\n"
        f"Implementation note: {data['note']}"
    )

@tool
def get_plc_runtime_analysis_recipe(topic: str = "变量-数据块-程序逻辑综合诊断") -> str:
    """Return the combined diagnostic workflow, documented APIs and runnable C# template
    for variables + data blocks + program logic."""
    key = (topic or "").strip()
    if not key:
        key = "变量-数据块-程序逻辑综合诊断"

    if key not in PLC_RUNTIME_ANALYSIS_RECIPES:
        return (
            _OPENNESS_CONCEPT_NOTE + "\n"
            "Combined diagnostic topic not found. Currently available: 变量-数据块-程序逻辑综合诊断."
        )

    data = PLC_RUNTIME_ANALYSIS_RECIPES[key]
    steps = "\n".join([f"  {i+1}. {s}" for i, s in enumerate(data["steps"])])
    apis = "\n".join([f"  - {m}" for m in data["openness_api"]])
    return (
        _OPENNESS_CONCEPT_NOTE + "\n"
        f"Topic: {key}\n"
        f"Goal: {data['goal']}\n"
        f"Analysis workflow:\n{steps}\n"
        f"Documented APIs:\n{apis}\n"
        f"Version note: {data['version_note']}\n"
        f"Documentation reference pages: {', '.join(data['doc_refs'])}\n"
        f"Runnable C# template:\n{PLC_RUNTIME_ANALYSIS_TEMPLATE}"
    )


@tool
def query_openness_api(query: str = "", version: str = "", limit: int = 10) -> str:
    """Query the bundled **version-specific** Openness API docs.

    Uses the official per-version Siemens.Engineering.xml shipped in
    docs/PublicAPI/V15.1|V16|V17, so answers differ by TIA version instead of
    coming from one hand-written snapshot.

    Args:
        query: Type/method/property name or keyword, e.g. "ICompilable",
            "GetService", "Export". Empty lists what that version's doc contains.
        version: TIA version, e.g. "15.1" / "V16" / "17". Empty reads the live
            TIA version via get_environment_profile.
        limit: Max matches to return (default 10, max 30).

    Returns:
        Matching members with their XML-doc summary/params for the requested
        version; an explicit message when that version has no bundled docs
        (V18–V21 must use probe_openness_capabilities on the installed TIA).
    """
    import json

    from tools import openness_api_docs
    from tools.tia_client import get_environment_profile

    resolved = openness_api_docs.normalize_version(version)
    source = "explicit"
    if not resolved:
        try:
            profile = json.loads(get_environment_profile())
            resolved = openness_api_docs.normalize_version(str(profile.get("TiaVersion") or ""))
            source = "live environment"
        except Exception:  # noqa: BLE001 - an unavailable live profile is not fatal
            resolved = ""
    if not resolved:
        bundled = ", ".join(f"V{v}" for v in openness_api_docs.available_versions()) or "(none)"
        return (
            "TIA version not specified and the live environment profile is unavailable. "
            f"Pass version=... (bundled docs: {bundled}) or call get_environment_profile first."
        )
    result = openness_api_docs.query(resolved, query, limit)
    if source == "live environment":
        result = f"Target: live TIA {resolved}\n" + result
    return result


# Library Types spec readers. Plain text is read directly; .docx (OpenXML zip)
# and .xlsx (openpyxl) are extracted to text. Legacy .doc/.xls (OLE2 binary) are
# not supported — convert them to .docx/.xlsx/.txt first.
_LIB_TEXT_EXTS = (".txt", ".md", ".markdown")
_LIB_DOCX_EXT = ".docx"
_LIB_XLSX_EXT = ".xlsx"
_LIB_LIST_EXTS = (".pdf",) + _LIB_TEXT_EXTS + (_LIB_DOCX_EXT, _LIB_XLSX_EXT)


def _read_docx_text(path: str) -> str:
    import zipfile
    import xml.etree.ElementTree as ET
    W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
    with zipfile.ZipFile(path) as z:
        with z.open("word/document.xml") as f:
            root = ET.parse(f).getroot()
    paragraphs = []
    for p in root.iter(W + "p"):
        runs = [t.text or "" for t in p.iter(W + "t")]
        paragraphs.append("".join(runs))
    return "\n".join(paragraphs)


def _read_xlsx_text(path: str) -> str:
    import openpyxl
    wb = openpyxl.load_workbook(path, read_only=True, data_only=True)
    try:
        lines = []
        for ws in wb.worksheets:
            lines.append(f"[Sheet: {ws.title}]")
            for row in ws.iter_rows(values_only=True):
                cells = [str(c) if c is not None else "" for c in row]
                if any(cells):
                    lines.append("\t".join(cells))
            lines.append("")
        return "\n".join(lines)
    finally:
        wb.close()


@tool
def list_library_types() -> str:
    """List the FB/FC specification files available under the Library Types directory (PDF / text / Word / Excel formats)."""
    workspace_root = _get_workspace_root()
    lib_dir = os.path.join(workspace_root, "Library Types")
    if not os.path.exists(lib_dir):
        return f"Library Types directory not found at {lib_dir}"

    names = set()
    for ext in _LIB_LIST_EXTS:
        for f in glob.glob(os.path.join(lib_dir, f"*{ext}")):
            names.add(os.path.splitext(os.path.basename(f))[0])
    block_names = sorted(names)
    return "Available blocks:\n" + ", ".join(block_names)


@tool
def get_library_type_details(block_name: str) -> str:
    """Return the specification of a block under Library Types.

    Text (.txt/.md/.markdown) and Word/Excel (.docx/.xlsx) specs are read and
    returned as content; PDF specs return the file path (binary). Preference
    order: text/Word/Excel first, then PDF.
    """
    workspace_root = _get_workspace_root()
    lib_dir = os.path.join(workspace_root, "Library Types")

    max_chars = 30000
    for ext in _LIB_TEXT_EXTS + (_LIB_DOCX_EXT, _LIB_XLSX_EXT):
        fp = os.path.join(lib_dir, f"{block_name}{ext}")
        if not os.path.isfile(fp):
            continue
        try:
            if ext == _LIB_DOCX_EXT:
                content = _read_docx_text(fp)
            elif ext == _LIB_XLSX_EXT:
                content = _read_xlsx_text(fp)
            else:
                with open(fp, "r", encoding="utf-8") as f:
                    content = f.read()
        except Exception as e:
            return f"Failed to read {fp}: {e}"
        truncated = len(content) > max_chars
        if truncated:
            content = content[:max_chars]
        header = f"Specification of {block_name} ({ext})"
        if truncated:
            header += f" [truncated to {max_chars} chars]"
        return f"{header}:\n{content}"

    pdf_path = os.path.join(lib_dir, f"{block_name}.pdf")
    if os.path.isfile(pdf_path):
        return f"Please refer to {pdf_path} for the full specification of {block_name} (PDF)."

    return f"Block specification '{block_name}' not found."


TOOLS = [
    search_openness_docs,
    list_plc_openness_topics,
    get_plc_openness_api_methods,
    get_plc_openness_csharp_template,
    get_plc_runtime_diagnostic_api,
    get_plc_runtime_analysis_recipe,
    list_library_types,
    get_library_type_details,
    query_openness_api,
]
