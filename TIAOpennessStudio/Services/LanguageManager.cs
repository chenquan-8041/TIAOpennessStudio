using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Linq;

namespace TIAOpennessStudio.Services
{
    public enum AppLanguage
    {
        English,
        German,
        Chinese
    }

    /// <summary>
    /// Provides UI localization from a built-in English/German/Chinese dictionary.
    /// </summary>
    public class LanguageManager
    {
        private static LanguageManager _instance;
        public static LanguageManager Instance => _instance ?? (_instance = new LanguageManager());

        public AppLanguage CurrentLanguage { get; private set; } = AppLanguage.English;

        public event Action<AppLanguage> LanguageChanged;

        private Dictionary<string, Dictionary<AppLanguage, string>> _translations;

        private const string LanguageSettingKey = "Language";

        private LanguageManager()
        {
            InitializeTranslations();
        }

        /// <summary>
        /// Restores the language used in the previous session. Defaults to English when
        /// the setting is missing or unreadable; never throws.
        /// </summary>
        public void ApplySavedLanguage()
        {
            CurrentLanguage = ParseLanguageCode(ReadLanguageSetting());
        }

        public void SetLanguage(AppLanguage language)
        {
            CurrentLanguage = language;
            WriteLanguageSetting(language);
            LanguageChanged?.Invoke(language);
        }

        private static AppLanguage ParseLanguageCode(string code)
        {
            if (string.Equals(code, "DE", StringComparison.OrdinalIgnoreCase)) return AppLanguage.German;
            if (string.Equals(code, "CN", StringComparison.OrdinalIgnoreCase)) return AppLanguage.Chinese;
            return AppLanguage.English;
        }

        private static string ToLanguageCode(AppLanguage language)
        {
            if (language == AppLanguage.German) return "DE";
            if (language == AppLanguage.Chinese) return "CN";
            return "EN";
        }

        private static string ReadLanguageSetting()
        {
            try
            {
                var settings = Properties.Settings.Default;
                bool hasProperty = settings.Properties
                    .Cast<SettingsProperty>()
                    .Any(p => string.Equals(p.Name, LanguageSettingKey, StringComparison.Ordinal));
                if (!hasProperty) return "EN";

                return (settings[LanguageSettingKey] as string) ?? "EN";
            }
            catch
            {
                return "EN";
            }
        }

        private static void WriteLanguageSetting(AppLanguage language)
        {
            try
            {
                var settings = Properties.Settings.Default;
                bool hasProperty = settings.Properties
                    .Cast<SettingsProperty>()
                    .Any(p => string.Equals(p.Name, LanguageSettingKey, StringComparison.Ordinal));
                if (!hasProperty) return;

                settings[LanguageSettingKey] = ToLanguageCode(language);
                settings.Save();
            }
            catch
            {
                // Do not block the UI on settings persistence failures.
            }
        }

        public string GetString(string key)
        {
            if (_translations.ContainsKey(key))
            {
                var langDict = _translations[key];
                if (langDict.ContainsKey(CurrentLanguage))
                {
                    return langDict[CurrentLanguage];
                }
            }
            return key; // Fallback to key itself
        }

        /// <summary>
        /// Localize and format a key with an English fallback. Never throws: an unknown
        /// key, a malformed template, or a parameter mismatch all return <paramref name="fallback"/>.
        /// </summary>
        public string Format(string key, string fallback, params object[] args)
        {
            string safeFallback = fallback ?? string.Empty;
            try
            {
                string template = GetString(key);
                if (string.IsNullOrEmpty(template) || string.Equals(template, key, StringComparison.Ordinal))
                    return safeFallback;
                if (args == null || args.Length == 0)
                    return template;
                return string.Format(template, args);
            }
            catch (Exception)
            {
                return safeFallback;
            }
        }

        private void InitializeTranslations()
        {
            _translations = new Dictionary<string, Dictionary<AppLanguage, string>>();

            Add("Explorer", "Explorer", "Explorer", "资源管理器");
            Add("SearchFiles", "Search files...", "Dateien suchen...", "搜索文件...");
            Add("TIA Portal", "TIA Portal", "TIA Portal", "TIA Portal");
            Add("SelectCodePlaceholder", "Select a block or file to view code", "Wählen Sie einen Baustein oder eine Datei aus, um den Code anzuzeigen", "选择块或文件以查看代码");

            // File Explorer Context Menu
            Add("MenuOpen", "Open", "Öffnen", "打开");
            Add("MenuImportToTia", "Import to TIA", "In TIA importieren", "导入到 TIA");
            Add("MenuNewSclFile", "New SCL File", "Neue SCL-Datei", "新建 SCL 文件");
            Add("MenuNewFolder", "New Folder", "Neuer Ordner", "新建文件夹");
            Add("MenuRename", "Rename", "Umbenennen", "重命名");
            Add("MenuDelete", "Delete", "Löschen", "删除");
            Add("MenuRefresh", "Refresh", "Aktualisieren", "刷新");

            // Common UI Elements
            Add("Open Project", "Open Project", "Projekt öffnen", "打开项目");
            Add("Device", "Device", "Station", "设备");
            Add("PLC", "PLC", "SPS", "PLC");
            Add("Blocks", "Blocks", "Bausteine", "程序块");
            Add("Reload", "Reload", "Neu laden", "重新加载");
            Add("Add path", "Add path", "Pfad hinzufügen", "添加路径");
            Add("Test XML", "Test XML", "XML Testen", "测试 XML");
            Add("Combine", "Combine", "Kombinieren", "合并");
            Add("Languages Test", "Languages Test", "Sprachtest", "语言测试");
            Add("Code Viewer", "Code Viewer", "Code Anzeige", "代码查看器");

            // TreeView / Block Types
            Add("Data Type", "Data Type", "Datentyp", "数据类型");
            Add("Safety Data Block", "Safety Data Block", "Sicherer Datenbaustein", "安全数据块");
            Add("Safety Function Block", "Safety Function Block", "Sicherer Funktionsbaustein", "安全功能块");
            Add("Safety Organization Block", "Safety Organization Block", "Sicherer Organisationsbaustein", "安全组织块");
            Add("Data Block", "Data Block", "Datenbaustein", "数据块");
            Add("Function", "Function", "Funktion", "功能(FC)");
            Add("Function Block", "Function Block", "Funktionsbaustein", "功能块(FB)");
            Add("Organization Block", "Organization Block", "Organisationsbaustein", "组织块(OB)");
            
            // Menu Items
            Add("Project", "Project", "Projekt", "项目");
            Add("Open", "Open", "Öffnen", "打开");
            Add("Compile", "Compile", "Übersetzen", "编译");
            Add("Close", "Close", "Schließen", "关闭");
            Add("Save", "Save", "Speichern", "保存");
            Add("Reload", "Reload", "Neu laden", "重新加载");
            Add("KnowledgeCacheManager", "Knowledge Cache Manager", "Wissenscache-Manager", "知识缓存管理器");
            Add("Exit", "Exit", "Beenden", "退出");

            Add("Import", "Import", "Importieren", "导入");
            Add("PLC Blocks", "PLC Blocks", "Bausteine", "程序块");
            Add("PLC Data types", "PLC Data types", "Datentypen", "数据类型");
            Add("Project Texts", "Project Texts", "Projekttexte", "项目文本");

            Add("Export", "Export", "Exportieren", "导出");
            Add("Export Selected", "Export Selected", "Ausgewählte exportieren", "导出选中项");
            Add("Export XML", "Export XML", "XML exportieren", "导出 XML");

            Add("Settings", "Settings", "Einstellungen", "设置");
            Add("Target Path", "Target Path", "Zielpfad", "目标路径");
            Add("Model Config", "Model Config", "Modellkonfiguration", "模型配置");
            Add("Preferences", "Preferences", "Einstellungen", "首选项");
            Add("Start Python Backend", "Start Python Backend", "Python-Backend starten", "启动 Python 后端");
            Add("Library Backup && Compare", "Library Versioning", "Bibliotheksversionierung", "库版本管理");
            Add("Language", "Language", "Sprache", "语言");
            Add("English", "English", "Englisch", "英语");

            Add("View", "View", "Ansicht", "视图");
            Add("Output Log", "Output Log", "Ausgabeprotokoll", "输出日志");
            Add("Theme", "Theme", "Design", "主题");
            Add("Dark", "Dark", "Dunkel", "深色");
            Add("Light", "Light", "Hell", "浅色");
       
            Add("Help", "Help", "Hilfe", "帮助");
            Add("About", "About", "Über", "关于");
            Add("German", "German", "Deutsch", "德语");
            Add("Chinese", "Chinese", "Chinesisch", "中文");

            Add("AI", "AI", "KI", "AI");
            Add("Start AI Service", "Start AI Service", "KI Dienst starten", "启动 AI 服务");
 
            Add("Help", "Help", "Hilfe", "帮助");
            Add("About", "About", "Über", "关于");

            // AI Chat
            Add("Code Generator", "Code Generator", "Code Generator", "代码生成器");
            Add("Configuration", "Configuration", "Konfiguration", "配置");
            Add("Prompt", "Describe the function block...", "Beschreiben Sie den Funktionsbaustein...", "描述您想要创建的功能块...");
            Add("Generate", "Generate", "Generieren", "生成");
            Add("Import to TIA", "Import to TIA", "In TIA importieren", "导入到 TIA");
            Add("Include Context", "Include Project Context", "Projektkontext einbeziehen", "包含项目上下文");
            Add("Use Real AI", "Use Real AI", "Echte KI verwenden", "模型选择");
            Add("Provider", "Provider", "Anbieter", "提供商");
            Add("Endpoint", "Endpoint", "Endpunkt", "端点");
            Add("API Key", "API Key", "API Schlüssel", "API 密钥");
            Add("Model", "Model", "Modell", "模型");
            Add("Status: Ready", "Status: Ready", "Status: Bereit", "状态: 就绪");
            Add("Status: Generating...", "Status: Generating...", "Status: Generiere...", "状态: 生成中...");
            Add("Status: Error", "Status: Error", "Status: Fehler", "状态: 错误");

            // Status bar AI write-target chips
            Add("Status.Target.Blocks", "AI-generated/modified Blocks (FB / FC / DB / OB) are written into this group; click to switch", "Vom KI erzeugte/geänderte Bausteine (FB / FC / DB / OB) werden in diese Gruppe geschrieben; klicken zum Wechseln", "AI 生成 / 修改的 Blocks (FB / FC / DB / OB) 会写入此分组,点击切换");
            Add("Status.Target.Types", "AI-generated/modified Types (UDT) are written into this group; click to switch", "Vom KI erzeugte/geänderte Typen (UDT) werden in diese Gruppe geschrieben; klicken zum Wechseln", "AI 生成 / 修改的 Types (UDT) 会写入此分组,点击切换");
            Add("Status.Target.Clear", "Clear and go back to the project root", "Zurücksetzen auf die Projektwurzel", "清空,回到项目根");

            // Runtime status messages (status bar + overlay)
            Add("Status.Connecting", "Connecting to TIA Portal...", "Verbindung zum TIA Portal wird hergestellt...", "正在连接TIA Portal...");
            Add("Status.Connected", "Connected to TIA Portal", "Mit TIA Portal verbunden", "已连接到TIA Portal");
            Add("Status.ConnectFailed", "Connection failed", "Verbindung fehlgeschlagen", "连接失败");
            Add("Status.ConnectError", "Connection error: {0}", "Verbindungsfehler: {0}", "连接错误: {0}");
            Add("Status.RefreshingTree", "Refreshing project tree...", "Projektbaum wird aktualisiert...", "正在刷新项目树...");
            Add("Status.TreeRefreshed", "Project tree refreshed", "Projektbaum aktualisiert", "树已刷新");
            Add("Status.RefreshError", "Refresh error: {0}", "Aktualisierungsfehler: {0}", "刷新错误: {0}");
            // LAD editor placeholder
            Add("Lad.SelectBlock", "Select a LAD/FBD block in the project tree", "Wählen Sie einen LAD/FBD-Baustein im Projektbaum", "请在项目树中选中 LAD/FBD 块");
            Add("Status.Compiling", "Compiling...", "Kompiliere...", "正在编译...");
            Add("Status.ViewMode", "Status: View Mode", "Status: Ansichtsmodus", "状态: 查看模式");
            Add("Status.EditMode", "Status: Edit Mode Enabled", "Status: Bearbeitungsmodus aktiv", "状态: 编辑模式已启用");
            // Quick-command mode hints (status bar after clicking a command chip)
            Add("Cmd.Mode.Generate", "Mode: Generate new code (type a description, then press Enter)", "Modus: Neuen Code erzeugen (Beschreibung eingeben, dann Enter drücken)", "模式: 生成新代码 (输入描述后按 Enter)");
            Add("Cmd.Mode.Modify", "Mode: Modify existing code (describe the change)", "Modus: Bestehenden Code ändern (Änderung beschreiben)", "模式: 修改现有代码 (描述需要的修改)");
            Add("Cmd.Mode.Explain", "Mode: Explain code (enter code or a block name)", "Modus: Code erklären (Code oder Bausteinname eingeben)", "模式: 解释代码 (输入代码或块名)");
            Add("Cmd.Mode.Diagnose", "Mode: Diagnose an issue (describe the symptom)", "Modus: Problem diagnostizieren (Symptom beschreiben)", "模式: 诊断问题 (描述问题现象)");
            Add("Cmd.Mode.Compile", "Mode: Compile check (enter a block name)", "Modus: Kompilierung prüfen (Bausteinname eingeben)", "模式: 编译验证 (输入块名)");
            Add("Cmd.Mode.Trace", "Mode: Trace a variable (enter a variable name)", "Modus: Variable verfolgen (Variablennamen eingeben)", "模式: 追踪变量 (输入变量名)");
            // Shared dialog strings
            Add("Dlg.Info", "Information", "Information", "信息");
            Add("Dlg.Choose", "Choose:", "Auswahl:", "请选择:");
            Add("Import.ConfirmRoot", "No Blocks target group is selected yet.\n\nWrite to the project root? (Recommended: click the Blocks chip in the status bar and pick a specific group first)", "Es ist noch keine Blocks-Zielgruppe ausgewählt.\n\nIn die Projektwurzel schreiben? (Empfohlen: zuerst das Blocks-Chip in der Statusleiste anklicken und eine Gruppe wählen)", "还没有选择 Blocks 目标分组.\n\n是否写入项目根目录? (推荐先在状态栏点 Blocks chip 选一个具体分组)");
            Add("Import.ConfirmRootTitle", "Confirm import target", "Importziel bestätigen", "确认导入目标");
            Add("Connected to: {0}", "Connected to: {0}", "Verbunden mit: {0}", "已连接到: {0}");
            Add("No running TIA Portal found. Start new instance?", "No running TIA Portal found. Start new instance?", "Kein laufendes TIA Portal gefunden. Neue Instanz starten?", "未找到正在运行的 TIA Portal。是否启动新实例？");
            Add("AdminWarning", "(Note: If TIA Portal is running as Administrator, this application must also run as Administrator to connect.)", "(Hinweis: Wenn TIA Portal als Administrator ausgeführt wird, muss diese Anwendung ebenfalls als Administrator ausgeführt werden.)", "(注意：如果 TIA Portal 以管理员身份运行，此应用程序也必须以管理员身份运行才能连接。)");
            Add("Target Path", "Target Path", "Zielpfad", "目标路径");
            Add("Actions", "Actions", "Aktionen", "操作");
            Add("Export XML", "Export XML", "XML Exportieren", "导出 XML");

            Add("Connecting to TIA Portal...", "Connecting to TIA Portal...", "Verbindung zum TIA Portal wird hergestellt...", "正在连接到 TIA Portal...");
            Add("Starting TIA Portal...", "Starting TIA Portal...", "Starte TIA Portal...", "正在启动 TIA Portal...");
            Add("TIA Portal Connected. Checking Project...", "TIA Portal Connected. Checking Project...", "TIA Portal verbunden. Überprüfe Projekt...", "TIA Portal 已连接。正在检查项目...");
            Add("Opening Project...", "Opening Project...", "Öffne Projekt...", "正在打开项目...");
            Add("Reading Project Structure...", "Reading Project Structure...", "Lese Projektstruktur...", "正在读取项目结构...");
            Add("Please enter a description for the function block.", "Please enter a description for the function block.", "Bitte geben Sie eine Beschreibung für den Funktionsbaustein ein.", "请输入功能块的描述。");
            Add("Show Sidebar", "Show Sidebar", "Seitenleiste anzeigen", "显示侧边栏");
            Add("Hide Sidebar", "Hide Sidebar", "Seitenleiste ausblenden", "隐藏侧边栏");
            Add("New Chat", "New Chat", "Neuer Chat", "新建对话");
            Add("Chat History", "Chat History", "Chat Verlauf", "历史对话");

            // Library Compare Window
            Add("LC.Title", "Library Versioning", "Bibliotheksversionierung", "库版本管理");
            Add("LC.Header", "Library Versioning", "Bibliotheksversionierung", "库版本管理");
            Add("LC.Subtitle", "User-level operation. Independent from AI internal version/index management.", "Benutzerebene. Unabhaengig von interner KI Versions-/Indexverwaltung.", "用户级操作。与 AI 内部版本/索引管理相互独立。");
            Add("LC.Tab.Backup", "Backup", "Sicherung", "备份");
            Add("LC.Tab.Compare", "Compare", "Vergleich", "对比");
            Add("LC.Common.Browse", "Browse...", "Durchsuchen...", "浏览...");
            Add("LC.Backup.SourceFile", "Source File", "Quelldatei", "源文件");
            Add("LC.Backup.Root", "Backup Root", "Sicherungsordner", "备份根目录");
            Add("LC.Backup.Create", "Create Backup", "Sicherung erstellen", "创建备份");
            Add("LC.Backup.RefreshHistory", "Refresh History", "Verlauf aktualisieren", "刷新历史");
            Add("LC.Backup.RestoreSelected", "Restore Selected", "Auswahl wiederherstellen", "恢复所选");
            Add("LC.Backup.History", "Backup History", "Sicherungsverlauf", "备份历史");
            Add("LC.Compare.LeftFile", "Left File", "Linke Datei", "左侧文件");
            Add("LC.Compare.RightFile", "Right File", "Rechte Datei", "右侧文件");
            Add("LC.Compare.Run", "Run Compare", "Vergleich starten", "开始对比");
            Add("LC.Compare.ExportXml", "Export XML", "XML exportieren", "导出 XML");
            Add("LC.Compare.Filter", "Filter", "Filter", "筛选");
            Add("LC.Filter.All", "All", "Alle", "全部");
            Add("LC.Filter.Diff", "Only Differences", "Nur Unterschiede", "仅差异");
            Add("LC.Filter.Left", "Only Left", "Nur links", "仅左侧");
            Add("LC.Filter.Right", "Only Right", "Nur rechts", "仅右侧");
            Add("LC.Filter.Same", "Same", "Gleich", "相同");
            Add("LC.Col.Category", "Category", "Kategorie", "类别");
            Add("LC.Col.Path", "Path", "Pfad", "路径");
            Add("LC.Col.Name", "Name", "Name", "名称");
            Add("LC.Col.Left", "Left", "Links", "左侧");
            Add("LC.Col.Right", "Right", "Rechts", "右侧");
            Add("LC.Col.Status", "Status", "Status", "状态");

            // Code Viewer Tab Context Menu
            Add("Tab.Close", "Close", "Schließen", "关闭");
            Add("Tab.CloseOthers", "Close Others", "Andere schließen", "关闭其他");
            Add("Tab.CloseRight", "Close to the Right", "Rechts schließen", "关闭右侧");
            Add("Tab.CloseAll", "Close All", "Alle schließen", "全部关闭");
            Add("Tab.CopyName", "Copy Name", "Name kopieren", "复制名称");

            // LAD element right-click menu
            Add("Lad.CopyVariableName", "Copy Variable Name", "Variablenname kopieren", "复制变量名");
            Add("Lad.ViewCrossReference", "View Cross References", "Querverweise anzeigen", "查看交叉引用");
            Add("Lad.ContextMenuTip", "Right-click for menu", "Rechtsklick für Menü", "右键打开菜单");
            Add("Lad.SafetyProgramBadge", "Safety Program (F-Program)", "Sicherheitsprogramm (F-Programm)", "安全程序 (F-Program)");

            // OPC UA panel
            Add("Opc.Title", "OPC UA", "OPC UA", "OPC UA");
            Add("Opc.Connect", "Connect", "Verbinden", "连接");
            Add("Opc.Disconnect", "Disconnect", "Trennen", "断开");
            Add("Opc.Endpoint", "Endpoint:", "Endpunkt:", "端点:");
            Add("Opc.Read", "Read", "Lesen", "读取");
            Add("Opc.Write", "Write", "Schreiben", "写入");
            Add("Opc.MonitorChecked", "Monitor", "Überwachen", "监控勾选");
            Add("Opc.MonitorCheckedTip", "Check variables then click to open a live monitor page in the center area (multi-select).", "Variablen anhaken und klicken, um eine Live-Überwachungsseite im mittleren Bereich zu öffnen (Mehrfachauswahl).", "勾选变量后点此，在中央区域打开实时监控页面（可多选）。");
            Add("Opc.WriteValueHint", "Value to write", "Zu schreibender Wert", "写入值");
            Add("Opc.WriteValueTip", "Enter the value to write, then press Enter or click Write", "Wert eingeben und Enter drücken oder auf Schreiben klicken", "输入要写入的值，回车或点 Write 写入");
            Add("Opc.ManualNodeHint", "Enter NodeId, e.g. ns=3;s=\"DB\".\"Member\"", "NodeId eingeben, z.B. ns=3;s=\"DB\".\"Element\"", "手动输入 NodeId，如 ns=3;s=\"DB名\".\"成员\"");
            Add("Opc.ManualNodeTip", "Enter a known NodeId to monitor directly (comma-separated for multiple). Bypasses browsing.", "Bekannte NodeId zur direkten Überwachung eingeben (mehrere durch Komma getrennt). Umgeht das Durchsuchen.", "直接输入已知 NodeId 进行监控，可用逗号分隔多个。绕过浏览树。");
            Add("Opc.MonitorManual", "Monitor", "Überwachen", "监控");
            Add("Opc.Hint", "Expand: DeviceSet→PLC→DataBlocksGlobal→DB→members; system variables are under the Server node. Variables (with type) have a checkbox; check them and click Monitor. If DataBlocksGlobal is empty, enable 'Accessible from OPC UA' for that DB in TIA.",
                "Erweitern: DeviceSet→PLC→DataBlocksGlobal→DB→Elemente; Systemvariablen befinden sich unter dem Server-Knoten. Variablen (mit Typ) haben ein Kontrollkästchen; anhaken und Überwachen klicken. Wenn DataBlocksGlobal leer ist, aktivieren Sie 'Aus OPC UA erreichbar' für diesen DB in TIA.",
                "逐层展开：DeviceSet→PLC→DataBlocksGlobal→DB→成员；系统变量在 Server 节点下。变量(带类型)前有勾选框，勾选后点“监控勾选”。若 DataBlocksGlobal 下为空，需在 TIA 里为该 DB 勾选“可从 OPC UA 访问”。");

            // OPC UA live monitor page
            Add("Opc.MonitorTitle", "Data Access View", "Data Access View", "实时监控视图");
            Add("Opc.Col.Name", "Name", "Name", "名称");
            Add("Opc.Col.NodeId", "NodeId", "NodeId", "NodeId");
            Add("Opc.Col.Value", "Value", "Wert", "值");
            Add("Opc.Col.Write", "Write", "Schreiben", "写入");
            Add("Opc.Col.Type", "Type", "Typ", "类型");
            Add("Opc.Col.Quality", "Quality", "Qualität", "质量");
            Add("Opc.Col.Timestamp", "Timestamp", "Zeitstempel", "时间戳");
            Add("Opc.MonitorFooter", "Values refresh live as the PLC changes · closing this page cancels the subscription", "Werte werden live aktualisiert · Schließen dieser Seite beendet das Abonnement", "值随 PLC 变化实时刷新 · 关闭本页自动取消订阅");
            Add("Opc.AllowWrite", "Allow write", "Schreiben erlauben", "允许写入");
            Add("Opc.AllowWriteTip", "Safety switch: only when checked can values be written to the running PLC (high-risk). Off by default.", "Sicherheitsschalter: Nur wenn aktiviert, können Werte auf die laufende SPS geschrieben werden (hohes Risiko). Standardmäßig aus.", "安全开关：勾选后才允许写运行中 PLC（高风险操作）。默认关闭。");

            // OPC UA monitor tab titles and status (incl. the {0} placeholder)
            Add("Opc.MonitorTabMulti", "Live Monitor ({0})", "Live-Überwachung ({0})", "实时监控({0})");
            Add("Opc.Status.NotConnected", "Not connected, cannot subscribe", "Nicht verbunden, Abonnement nicht möglich", "未连接，无法订阅");
            Add("Opc.Status.NoVariables", "No variables to monitor", "Keine Variablen zum Überwachen", "没有可监控的变量");
            Add("Opc.Status.Subscribed", "Subscribed to {0} variable(s), interval {1} ms", "{0} Variable(n) abonniert, Intervall {1} ms", "已订阅 {0} 个变量，刷新间隔 {1} ms");
            Add("Opc.Status.SubscribeFailed", "Subscription failed: {0}", "Abonnement fehlgeschlagen: {0}", "订阅失败: {0}");
            Add("Opc.Status.NoMonitorTarget", "No variables to monitor: check variables, or expand to a variable and select it", "Keine Variablen: Variablen anhaken oder bis zur Variablenebene erweitern und auswählen", "没有可监控的变量：请勾选变量，或先展开到变量层再选中");

            // Chat message undo (revert write-back)
            Add("Chat.Undo.Tooltip",        "Revert this write-back",       "Schreibvorgang rückgängig machen",  "撤回此次写入");
            Add("Chat.Undo.DiffTitle",      "Revert: {0}",                  "Rückgängig: {0}",                   "撤回: {0}");
            Add("Chat.Undo.Confirmed",      "✅ Reverted changes to {0}",   "✅ Änderungen an {0} rückgängig",  "✅ 已撤回对 {0} 的修改");

            // Agent interaction drawer (approve / choose / input)
            Add("Interact.Allow",           "Allow",                        "Erlauben",                          "允许");
            Add("Interact.Deny",            "Deny",                         "Ablehnen",                          "拒绝");
            Add("Interact.Submit",          "Submit",                       "Senden",                            "提交");
            Add("Interact.Remember",        "Don't ask again in this task", "In dieser Aufgabe nicht erneut fragen", "本次任务内不再询问");
            Add("Interact.ShowDetail",      "Show details",                 "Details anzeigen",                  "查看详情");
            Add("Interact.HideDetail",      "Hide details",                 "Details ausblenden",                "收起详情");
            Add("Interact.From",            "Requested by: {0}",            "Angefordert von: {0}",              "请求来源：{0}");
            Add("Interact.Queue",           "{0} of {1}",                   "{0} von {1}",                       "第 {0} 个，共 {1} 个");
            Add("Interact.Countdown",       "Auto-deny in {0} s",           "Automatisch ablehnen in {0} s",     "{0} 秒后自动拒绝");
            Add("Interact.AgentAsks",       "Agent asks",                   "Agent fragt",                       "Agent 提问");
            Add("Interact.NeedsApproval",   "Needs your approval",          "Ihre Genehmigung erforderlich",     "需要你的授权");
            Add("Interact.HighRisk",        "Irreversible action",          "Nicht umkehrbare Aktion",           "不可撤销的操作");
            Add("Interact.Expired",         "Expired — still approvable ({0} s)", "Abgelaufen — weiterhin freigebbar ({0} s)", "已超时，仍可批准（剩余 {0} 秒）");
            Add("Interact.BatchTitle",      "{0} writes need approval",     "{0} Schreibvorgänge brauchen Freigabe", "{0} 项写入待批准");
            Add("Interact.BatchSummary",    "Queued writes were merged into one card. Allowing runs all of them.", "Gesammelte Schreibvorgänge wurden zusammengefasst. Erlauben führt alle aus.", "本轮排队的写操作已合并为一张卡；允许即执行全部。");
            Add("Interact.AllowAll",        "Allow all",                    "Alle erlauben",                     "全部允许");
            Add("Interact.DenyAll",         "Deny all",                     "Alle ablehnen",                     "全部拒绝");

            // Access-mode switch (input box toggle) and elevation card
            Add("Access.ReadOnly",          "Read-only",                    "Nur lesen",                         "只读分析");
            Add("Access.AllowEdit",         "Allow writes",                 "Schreiben erlauben",                "允许写入");
            Add("Access.ReadOnly.Tip",      "Agent can analyse but not change the project. Tap to allow writes.", "Agent kann analysieren, aber nicht schreiben. Antippen, um Schreiben zu erlauben.", "Agent 只能分析，不能改写项目。点击切换为允许写入。");
            Add("Access.AllowEdit.Tip",     "Agent may write to the project. Tap to restrict to read-only.",      "Agent kann in das Projekt schreiben. Antippen, um nur lesen einzustellen.", "Agent 可以改写项目。点击切换为只读。");
            Add("Access.ElevationTitle",    "Agent wants to write — are you sure?", "Agent möchte schreiben — sind Sie sicher?", "Agent 请求写入（当前为只读模式）");
            Add("Access.ElevationSummary",  "You started this task in read-only mode. The agent now wants to write to your project. Allow this write?", "Diese Aufgabe wurde im Nur-Lesen-Modus gestartet. Der Agent möchte jetzt schreiben. Diese Schreiboperation erlauben?", "这次任务以只读模式启动。Agent 现在请求改写项目，是否允许本次写入？");
            Add("Access.ElevationAllow",    "Allow this write",             "Diesen Schreibvorgang erlauben",    "允许本次写入");
            Add("Access.ElevationDeny",     "Keep read-only",               "Nur lesen beibehalten",             "保持只读");


            // Write gate: approval card for agent writes to the TIA project (text is built from facts, not from model output)
            Add("Gate.Title.Write",         "Agent wants to write to your project", "Agent möchte in Ihr Projekt schreiben", "Agent 请求写入你的项目");
            Add("Gate.Title.Delete",        "Agent wants to delete a block",        "Agent möchte einen Baustein löschen",  "Agent 请求删除程序块");
            Add("Gate.Kind.Block",          "block",                        "Baustein",                          "程序块");

            // Write gate: hardware-config write card (dedicated wording, no block semantics)
            Add("Gate.Title.HardwareWrite", "Agent wants to change the hardware configuration", "Agent möchte die Hardware-Konfiguration ändern", "Agent 请求修改设备组态");
            Add("Gate.Kind.Device",          "device",                       "Gerät",                             "设备");
            Add("Gate.Line.HardwareApply",   "Hardware config: {0} — {1}",   "Hardware-Konfiguration: {0} — {1}", "组态写入：{0} —— {1}");
            Add("Gate.Root",                "Blocks (root)",                "Bausteine (Stammordner)",           "程序块（根目录）");
            Add("Gate.Line.Create",         "Create {0} \"{1}\"",           "{0} \"{1}\" anlegen",               "新建 {0} \"{1}\"");
            Add("Gate.Line.Overwrite",      "Overwrite existing {0} \"{1}\"", "Vorhandenen {0} \"{1}\" überschreiben", "覆盖已有的 {0} \"{1}\"");
            Add("Gate.Line.Unchecked",      "Write {0} \"{1}\" (could not check whether it exists)", "{0} \"{1}\" schreiben (Existenz nicht geprüft)", "写入 {0} \"{1}\"（未能确认是否已存在）");
            Add("Gate.Line.Delete",         "Delete {0} \"{1}\"",           "{0} \"{1}\" löschen",               "删除 {0} \"{1}\"");
            Add("Gate.Line.DeleteMissing",  "Delete \"{1}\" (not found in the project)", "\"{1}\" löschen (im Projekt nicht gefunden)", "删除 \"{1}\"（项目中未找到）");
            Add("Gate.Line.Where",          "currently in: {0}",            "aktuell in: {0}",                   "当前位置：{0}");
            Add("Gate.Line.More",           "...and {0} more",              "...und {0} weitere",                "……另有 {0} 个");
            Add("Gate.Line.NameDiffers",    "The content declares \"{0}\"; the request said \"{1}\". \"{0}\" is what will be written.", "Der Inhalt deklariert \"{0}\"; angefragt war \"{1}\". Geschrieben wird \"{0}\".", "内容里声明的名称是 \"{0}\"，请求里写的是 \"{1}\"。实际写入的是 \"{0}\"。");
            Add("Gate.Line.Unreadable",     "The content could not be read; the names above are only what the request claimed.", "Der Inhalt konnte nicht gelesen werden; die Namen oben sind nur die Angaben der Anfrage.", "无法解析内容；上面的名称只是请求里声明的，未经核实。");
            Add("Gate.Line.Truncated",      "The content declares more blocks than are listed here.", "Der Inhalt deklariert mehr Bausteine, als hier aufgeführt sind.", "内容里声明的块比这里列出的更多。");
            Add("Gate.Remote",              "remote client",                "Remote-Client",                     "远程客户端");
            Add("Gate.Line.Target",         "Into: {0}",                    "Ziel: {0}",                         "写入位置：{0}");
            Add("Gate.Line.Content",        "Content: {0}, {1} lines",      "Inhalt: {0}, {1} Zeilen",           "内容：{0}，{1} 行");
            Add("Gate.Line.Retry",          "Approval retry {0}/{1}: the previous card timed out without an answer.", "Freigabe erneut {0}/{1}: die vorherige Karte wurde ohne Antwort abgelaufen.", "第 {0}/{1} 次审批：上一次卡片超时未响应");
            Add("Chat.Undo.Cancelled",      "Revert cancelled.",             "Rückgängig abgebrochen.",           "已取消撤回。");
            Add("Chat.Undo.NoSnapshot",     "No snapshot available for {0}","Kein Snapshot für {0} vorhanden",   "没有 {0} 的可用快照");
            Add("Chat.Undo.Disabled",       "Cannot revert while generating","Kann während Generierung nicht rückgängig gemacht werden","生成进行中，无法撤回");

            // Agent plan notices: the backend sends code + English fallback; these are the UI translations.
            Add("Agent.plan.started",              "📋 Plan started: {0} blocks",                      "📋 Plan gestartet: {0} Bausteine",                                "📋 计划开始执行：共 {0} 个块");
            Add("Agent.plan.startedDetail",        "📋 Project plan started ({0} blocks): {1}",        "📋 Projektplan gestartet ({0} Bausteine): {1}",                   "📋 项目计划已启动（{0} 块）：{1}");
            Add("Agent.delivery.summary",          "{0} blocks",                                        "{0} Bausteine",                                                   "共 {0} 个块");
            Add("Agent.plan.resume",               "🔄 Found unfinished plan {0}; resuming from the checkpoint", "🔄 Unvollständiger Plan {0} gefunden; Fortsetzung vom Prüfpunkt", "🔄 检测到未完成计划 {0}，从断点继续执行");
            Add("Agent.plan.detected",             "📋 Project-level long task detected; planned {0} blocks", "📋 Projektweite Langzeitaufgabe erkannt; {0} Bausteine geplant", "📋 检测到项目级长任务，已规划 {0} 个块");
            Add("Agent.plan.task.generating",      "⏳ Generating {0} block `{1}`...",                  "⏳ Erzeuge {0}-Baustein `{1}`...",                                 "⏳ 生成 {0} 块 `{1}`...");
            Add("Agent.plan.task.failed",          "❌ {0} block `{1}` failed: {2}",                    "❌ {0}-Baustein `{1}` fehlgeschlagen: {2}",                       "❌ {0} 块 `{1}` 失败：{2}");
            Add("Agent.plan.task.continue",        "🔁 Retrying block \"{0}\"; resuming from the checkpoint...", "🔁 Baustein „{0}“ wird erneut versucht; Fortsetzung vom Prüfpunkt...", "🔁 已选择重试块「{0}」，从断点继续...");
            Add("Agent.plan.task.skip",            "⏭ Skipped block \"{0}\"; continuing with the remaining tasks...", "⏭ Baustein „{0}“ übersprungen; fahre mit den übrigen Aufgaben fort...", "⏭ 已跳过块「{0}」，继续后续任务...");
            Add("Agent.plan.task.unhandled",       "⚠️ Failed task not handled; the plan stays paused. Handle it, then re-send to resume from the checkpoint.", "⚠️ Fehlgeschlagene Aufgabe nicht behandelt; der Plan bleibt pausiert. Nach der Behandlung erneut senden, um vom Prüfpunkt fortzusetzen.", "⚠️ 失败任务未处理，计划保持暂停。处理后重新发送即可从断点续跑。");
            Add("Agent.plan.milestone.waiting",    "⏸ Milestone \"{0}\" completed; awaiting approval", "⏸ Meilenstein „{0}“ abgeschlossen; warte auf Freigabe",            "⏸ 里程碑「{0}」已完成，等待审批");
            Add("Agent.plan.milestone.unanswered", "⚠️ Milestone approval was not completed; the plan is paused. Approve, then re-send to resume from the checkpoint.", "⚠️ Freigabe des Meilensteins nicht abgeschlossen; der Plan ist pausiert. Nach der Freigabe erneut senden, um vom Prüfpunkt fortzusetzen.", "⚠️ 里程碑审批未完成，计划已暂停。完成审批后重新发送即可从断点续跑。");
            Add("Agent.tool.soft_limit",           "⚠️ This run has made {0} tool calls and keeps going; execution is now governed by the token budget and auto-compaction.", "⚠️ Dieser Lauf hat {0} Werkzeugaufrufe erreicht und läuft weiter; die Ausführung wird nun durch Token-Budget und automatische Kompression gesteuert.", "⚠️ 本轮工具调用已达 {0} 次，任务继续执行；后续由 Token 预算与自动压缩统一管控。");
            Add("Agent.manual.title",              "Manual action required",                            "Manuelle Aktion erforderlich",                                    "需要手动操作");
            Add("Agent.write.safety_password",     "TIA's safety program (F-CPU) access-protection password blocks all Openness changes. Open TIA Portal → Safety Administration → Access Protection → Revoke the password, then retry.", "Das Zugriffsschutz-Kennwort des Sicherheitsprogramms (F-CPU) blockiert alle Openness-Änderungen. Öffnen Sie TIA Portal → Sicherheitsprogrammverwaltung → Zugriffsschutz → Kennwort widerrufen, dann erneut versuchen.", "TIA 安全程序（F-CPU）的访问保护密码阻止了所有 Openness 修改。请打开 TIA Portal → 安全程序管理（Safety Administration）→ 访问保护（Access Protection）→ Revoke 撤销密码，然后重试。");
            Add("Agent.plan.milestone.approved",   "✅ Milestone approved; resuming from the checkpoint...", "✅ Meilenstein freigegeben; Fortsetzung vom Prüfpunkt...",      "✅ 里程碑已批准，从断点继续执行...");
            Add("Agent.plan.milestone.adjust",     "🔄 Milestone needs adjustment; replanning the unfinished part and continuing...", "🔄 Meilenstein erfordert Anpassung; unfertiger Teil wird neu geplant und fortgesetzt...", "🔄 里程碑需调整，重规划未完成部分并继续...");
            Add("Agent.plan.milestone.rejected",   "🛑 Milestone rejected; project generation terminated (the plan is kept for later).", "🛑 Meilenstein abgelehnt; Projekterzeugung beendet (der Plan bleibt für später erhalten).", "🛑 里程碑已拒绝，项目生成终止（计划已保留，可下次继续）。");
            Add("Agent.plan.completed",            "✅ Plan completed",                                   "✅ Plan abgeschlossen",                                           "✅ 计划执行完成");
            Add("Agent.plan.terminated",           "🛑 Project generation terminated (the plan is kept for later).", "🛑 Projekterzeugung abgebrochen (der Plan bleibt für später erhalten).", "🛑 已终止项目生成（计划保留，可下次继续）。");


            // Knowledge Base panel
            Add("Kb.ActivityTip", "Knowledge Base (Coding Standards / Private Libraries)", "Wissensdatenbank (Codierstandards / Private Bibliotheken)", "知识库 (编码标准 / 私有资料库)");
            Add("Kb.Title", "Knowledge Base", "Wissensdatenbank", "知识库");
            Add("Kb.Official", "Default Knowledge Base", "Standard-Wissensdatenbank", "默认知识库");
            Add("Kb.Official.Loaded", "Loaded (encrypted, read-only)", "Geladen (verschlüsselt, schreibgeschützt)", "已加载（加密，只读）");
            Add("Kb.Official.Missing", "Not installed (release package should contain standards.kb)", "Nicht installiert (Release-Paket sollte standards.kb enthalten)", "未安装（发行包中应包含 standards.kb）");
            Add("Kb.Btn.Mount", "Mount Library (.kb)", "Bibliothek einbinden (.kb)", "挂载知识库 (.kb)");
            Add("Kb.Btn.MountTip", "Mount an encrypted or plaintext knowledge base file", "Verschlüsselte oder Klartext-Wissensdatenbank einbinden", "挂载已加密或明文的知识库文件");
            Add("Kb.Btn.Create", "Create Encrypted Library from Markdown", "Verschlüsselte Bibliothek aus Markdown erstellen", "从 Markdown 创建加密库");
            Add("Kb.Btn.CreateTip", "Pick your standard .md and encrypt it into a .kb with a passphrase", "Standard-.md auswählen und mit Passwort zu .kb verschlüsseln", "选择你的标准 .md，用口令加密生成 .kb");
            Add("Kb.Btn.Unlock", "Unlock Selected (passphrase)", "Ausgewählte entsperren (Passwort)", "解锁选中库 (口令)");
            Add("Kb.Btn.UnlockTip", "Enter passphrase for a locked library after restart", "Passwort für gesperrte Bibliothek nach Neustart eingeben", "为重启后未解锁的口令库输入口令");
            Add("Kb.Btn.Unmount", "Remove Selected", "Ausgewählte entfernen", "移除选中库");
            Add("Kb.Btn.Browse", "Browse", "Öffnen", "浏览");
            Add("Kb.Status.Disabled", "Disabled", "Deaktiviert", "已禁用");
            Add("Kb.Status.Unlocked", "Unlocked", "Entsperrt", "已解锁");
            Add("Kb.Status.Locked", "Locked (passphrase required)", "Gesperrt (Passwort erforderlich)", "未解锁（需口令）");
            Add("Kb.Status.Ready", "Ready", "Bereit", "可用");
            Add("Kb.NotReady", "Knowledge service not ready", "Wissensdienst nicht bereit", "知识库服务未就绪");
            // Knowledge base browser page (rule table + detail)
            Add("Kb.View.Col.Category", "Category", "Kategorie", "类别");
            Add("Kb.View.Col.Scope", "Scope", "Umfang", "范围");
            Add("Kb.View.Col.Title", "Title", "Titel", "标题");
            Add("Kb.View.Content", "Content", "Inhalt", "内容");
            Add("Kb.View.Examples", "Example", "Beispiel", "示例");
            Add("Kb.View.Loaded", "Loaded {0} rules from {1}", "{0} Regeln aus {1} geladen", "已从 {1} 加载 {0} 条规则");
            Add("Kb.View.RuleCount", "{0} rules", "{0} Regeln", "{0} 条规则");
            // Dialog text
            Add("Kb.Dlg.EncTitle", "Encryption Method", "Verschlüsselungsmethode", "知识库加密方式");
            Add("Kb.Dlg.EncQuestion", "Is this library passphrase-encrypted?\n\nYes = passphrase library\nNo = plaintext (unencrypted)\nCancel = abort", "Ist diese Bibliothek passwortverschlüsselt?\n\nJa = Passwort-Bibliothek\nNein = Klartext (unverschlüsselt)\nAbbrechen = abbrechen", "该库是否用口令加密？\n\n是 = 口令加密库\n否 = 明文库（不加密）\n取消 = 放弃");
            Add("Kb.Dlg.EnterPass", "Enter the decryption passphrase for this library:", "Entschlüsselungspasswort für diese Bibliothek eingeben:", "输入该知识库的解密口令：");
            Add("Kb.Dlg.SetPass", "Set a passphrase for this library (needed to decrypt, keep it safe):", "Passwort für diese Bibliothek festlegen (zum Entschlüsseln nötig, gut aufbewahren):", "为该知识库设置口令（解密时需要，请牢记）：");
            Add("Kb.Dlg.Remember", "Remember passphrase for auto-unlock next time?\n(Passphrase will be encrypted with your Windows account and stored locally)", "Passwort für automatisches Entsperren merken?\n(Passwort wird mit Ihrem Windows-Konto verschlüsselt lokal gespeichert)", "记住口令以便下次自动解锁？\n（口令将用 Windows 账户加密保存在本机）");
            Add("Kb.Dlg.RememberTitle", "Remember Passphrase", "Passwort merken", "记住口令");
            Add("Kb.Dlg.BindMachineTitle", "Lock to This Machine", "An dieses Gerät binden", "锁定到本机");
            Add("Kb.Dlg.BindMachine", "Lock this library to this machine?\n\nYes = can ONLY be opened on this computer (even with the correct passphrase it won't open elsewhere)\nNo = portable (correct passphrase opens it on any computer)", "Diese Bibliothek an dieses Gerät binden?\n\nJa = kann NUR auf diesem Computer geöffnet werden (auch mit korrektem Passwort nicht anderswo)\nNein = portabel (korrektes Passwort öffnet sie auf jedem Computer)", "将该库锁定到本机？\n\n是 = 只能在本台电脑打开（即使口令正确，换电脑也无法打开）\n否 = 可移植（口令正确即可在任意电脑打开）");
            Add("Kb.Msg.MountOk", "Mounted successfully.", "Erfolgreich eingebunden.", "挂载成功。");
            Add("Kb.Msg.MountFail", "Mount failed: ", "Einbinden fehlgeschlagen: ", "挂载失败：");
            Add("Kb.Msg.NoRules", "No rules parsed from this file.", "Keine Regeln aus dieser Datei gelesen.", "未从该文件解析到任何规则。");
            Add("Kb.Msg.CreateOk", "Encrypted library created and mounted.", "Verschlüsselte Bibliothek erstellt und eingebunden.", "已生成加密库并挂载。");
            Add("Kb.Msg.CreateFail", "Create failed: ", "Erstellen fehlgeschlagen: ", "创建失败：");
            Add("Kb.Msg.NoPass", "This library needs no passphrase.", "Diese Bibliothek benötigt kein Passwort.", "该库无需口令。");
            Add("Kb.Msg.AlreadyUnlocked", "This library is already unlocked.", "Diese Bibliothek ist bereits entsperrt.", "该库已解锁。");
            Add("Kb.Msg.UnlockOk", "Unlocked successfully.", "Erfolgreich entsperrt.", "解锁成功。");
            Add("Kb.Msg.UnlockFail", "Unlock failed (wrong passphrase?): ", "Entsperren fehlgeschlagen (falsches Passwort?): ", "解锁失败（口令错误？）：");
            Add("Kb.Msg.ConfirmRemove", "Remove library \"{0}\"? (file will not be deleted)", "Bibliothek \"{0}\" entfernen? (Datei wird nicht gelöscht)", "移除库 \"{0}\"？（不会删除文件）");
            Add("Kb.Common.Ok", "OK", "OK", "确定");
            Add("Kb.Common.Cancel", "Cancel", "Abbrechen", "取消");
            Add("Kb.Common.Title", "Knowledge Base", "Wissensdatenbank", "知识库");

            // Chat History management
            Add("Chat.History.Manage", "Manage", "Verwalten", "管理");
            Add("Chat.History.SelectAll", "Select All", "Alle auswählen", "全选");
            Add("Chat.History.Invert", "Invert", "Umkehren", "反选");
            Add("Chat.History.DeleteSelected", "Delete Selected", "Ausgewählte löschen", "删除所选");
            Add("Chat.History.Finish", "Finish", "Fertig", "完成");
            Add("Chat.History.DeleteConfirm", "Delete the selected {0} history record(s)?", "Ausgewählte {0} Verlaufsdatensätze löschen?", "确定要删除选中的 {0} 条历史记录吗？");
            Add("Chat.History.DeleteTitle", "Confirm Delete", "Löschen bestätigen", "确认删除");
            Add("Chat.History.NoSelection", "Please select at least one history record.", "Bitte mindestens einen Verlaufsdatensatz auswählen.", "请先勾选要删除的历史记录。");
            Add("Chat.History.NoHistory", "No chat history found.", "Kein Chat-Verlauf gefunden.", "未找到聊天历史记录。");
            Add("Chat.History.TabTitle", "Chat History", "Chat-Verlauf", "历史对话");
            Add("Chat.History.CloseTip", "Close Chat History", "Chat-Verlauf schließen", "关闭历史对话");

            // Chat message list (empty state, role labels, plan card, dev trace)
            Add("Chat.EmptyTitle", "Start a conversation with the Agent", "Starten Sie eine Unterhaltung mit dem Agenten", "与 Agent 开始对话");
            Add("Chat.EmptySubtitle", "Describe the PLC code you want to generate or change", "Beschreiben Sie den PLC-Code, den Sie erzeugen oder ändern möchten", "描述您想要生成或修改的 PLC 代码");
            Add("Chat.Thinking", "Thinking...", "Denke nach...", "思考中...");
            Add("Chat.Thought", "Reasoning", "Denkprozess", "思考过程");
            // Code block header actions
            Add("Code.Copy", "Copy", "Kopieren", "复制");
            Add("Code.CopyTip", "Copy this code block", "Diesen Codeblock kopieren", "复制这段代码");
            Add("Code.Insert", "Insert", "Einfügen", "插入");
            Add("Code.InsertTip", "Insert at the cursor of the chat input box", "An der Cursorposition im Eingabefeld einfügen", "插入到聊天输入框的光标处");
            Add("Code.Save", "To Folder", "In Ordner", "存到文件夹");
            Add("Code.SaveTip", "Save as a file into the selected Explorer folder", "Als Datei im ausgewählten Explorer-Ordner speichern", "另存为文件到 Explorer 选中的目录");
            Add("Code.Copied", "Copied", "Kopiert", "已复制");
            Add("Code.Inserted", "Inserted", "Eingefügt", "已插入");
            Add("Code.Saved", "Saved", "Gespeichert", "已保存");
            Add("Code.SaveFailed", "Failed", "Fehlgeschlagen", "保存失败");
            Add("Chat.Role.Agent", "Agent", "Agent", "Agent");
            Add("Chat.Role.You", "You", "Sie", "你");
            Add("Chat.DevTraceTip", "Developer Trace (Raw Prompt / Tool Calls / TIA API)", "Entwickler-Trace (Roh-Prompt / Tool-Aufrufe / TIA-API)", "开发者追踪（原始提示词 / 工具调用 / TIA API）");
            Add("Chat.ExecutionPlan", "Execution Plan", "Ausführungsplan", "执行计划");
            Add("Chat.ConfirmExecution", "Confirm Execution", "Ausführung bestätigen", "确认执行");

            // Diff preview
            Add("Diff.Title", "Code Diff", "Codeunterschiede", "代码差异");
            Add("Diff.View.SideBySide", "Side by Side", "Seite an Seite", "并排对比");
            Add("Diff.View.Unified", "Unified", "Zusammengeführt", "统一视图");
            Add("Diff.View.ToggleTip", "Toggle side-by-side / unified view", "Zwischen Seite-an-Seite- und zusammengeführter Ansicht wechseln", "切换并排/统一视图");
            Add("Diff.Edit", "Edit", "Bearbeiten", "编辑");
            Add("Diff.Edit.Tip", "Edit the modified code manually", "Den geänderten Code manuell bearbeiten", "手动编辑修改后的代码");
            Add("Diff.Apply", "Apply", "Übernehmen", "应用");
            Add("Diff.Reject", "Reject", "Ablehnen", "拒绝");
            Add("Diff.Original", "Original", "Original", "原始代码");
            Add("Diff.Modified", "Modified", "Geändert", "修改后");
            Add("Diff.Status.Default", "Review the changes, then click Apply to write back to TIA Portal.", "Prüfen Sie die Änderungen und klicken Sie dann auf Übernehmen, um sie in TIA Portal zurückzuschreiben.", "检查变更后点击“应用”写回 TIA Portal。");

            // Import target choice dialog
            Add("Import.Dlg.Title", "Where to write the generated code?", "Wohin soll der generierte Code geschrieben werden?", "生成的代码写入哪里？");
            Add("Import.Dlg.Heading", "Code generation complete", "Codegenerierung abgeschlossen", "代码已生成完毕");
            Add("Import.Dlg.Subtitle", "You have not selected a target group in the StatusBar yet. Choose how to proceed:", "Sie haben in der Statusleiste noch keine Zielgruppe ausgewählt. Wählen Sie, wie fortgefahren werden soll:", "您还没有在 StatusBar 选择目标分组，请选择处理方式：");
            Add("Import.Dlg.SubtitleWithBlock", "Generated {0} \"{1}\". No target group is selected in the StatusBar yet. Choose how to proceed:", "{0} \"{1}\" wurde generiert. In der Statusleiste ist noch keine Zielgruppe ausgewählt. Wählen Sie, wie fortgefahren werden soll:", "已生成 {0} \"{1}\"，您还没有在 StatusBar 选择目标分组。请选择处理方式：");
            Add("Import.Dlg.TiaRoot", "Write to the TIA project root", "In das TIA-Projektstammverzeichnis schreiben", "写入 TIA 项目根目录");
            Add("Import.Dlg.TiaRootHint", "The code is written to PlcSoftware.BlockGroup (project root) via Openness", "Der Code wird über Openness in PlcSoftware.BlockGroup (Projektstamm) geschrieben", "代码将通过 Openness 写入 PlcSoftware.BlockGroup (项目根)");
            Add("Import.Dlg.PickGroup", "Choose a TIA target group", "TIA-Zielgruppe auswählen", "选择 TIA 目标分组");
            Add("Import.Dlg.PickGroupHint", "Open the StatusBar Blocks chip and pick a specific subgroup from the tree, then import", "Öffnen Sie das Blocks-Chip in der Statusleiste, wählen Sie eine Untergruppe im Baum aus und importieren Sie", "打开 StatusBar Blocks chip，从树中挑选具体子分组后导入");
            Add("Import.Dlg.LocalFile", "Save to a local file", "In eine lokale Datei speichern", "保存到本地文件");
            Add("Import.Dlg.LocalFileHint", "Default: GeneratedCode\\<BlockName>.scl; the path can be changed in the save dialog", "Standard: GeneratedCode\\<BlockName>.scl; der Pfad kann im Speichern-Dialog geändert werden", "默认 GeneratedCode\\<BlockName>.scl，可在保存对话框中改路径");

            // Agent input box
            Add("Input.ModeHint", "Chat with the AI assistant to generate PLC code", "Chatten Sie mit dem KI-Assistenten, um SPS-Code zu erzeugen", "与 AI 助手对话生成 PLC 代码");
            Add("Input.AtTip", "Reference files (@)", "Dateien referenzieren (@)", "引用文件 (@)");
            Add("Input.HashTip", "Quick commands (#)", "Schnellbefehle (#)", "快捷指令 (#)");
            Add("Input.ImageTip", "Paste or upload an image", "Bild einfügen oder hochladen", "粘贴或上传图片");
            Add("Input.ClearImages", "✕ Clear", "✕ Leeren", "✕ 清除");
            Add("Input.Compress", "Compress", "Komprimieren", "压缩");
            Add("Input.CompressTip", "Compress the message history to free up tokens", "Den Nachrichtenverlauf komprimieren, um Tokens freizugeben", "压缩历史消息以释放 Token");
            Add("Input.SendTip", "Send (Enter)", "Senden (Enter)", "发送 (Enter)");
            Add("Input.StopTip", "Stop generating", "Generierung stoppen", "停止生成");
            Add("Input.Cmd.Generate", "Generate new code", "Neuen Code erzeugen", "生成新代码");
            Add("Input.Cmd.Modify", "Modify existing code", "Vorhandenen Code ändern", "修改现有代码");
            Add("Input.Cmd.Explain", "Explain code", "Code erklären", "解释代码");
            Add("Input.Cmd.Diagnose", "Diagnose issues", "Probleme diagnostizieren", "诊断问题");
            Add("Input.Cmd.Compile", "Compile to verify", "Durch Übersetzen prüfen", "编译验证");
            Add("Input.Cmd.Trace", "Trace variables", "Variablen verfolgen", "追踪变量");
        }

        private void Add(string key, string en, string de, string cn)
        {
            _translations[key] = new Dictionary<AppLanguage, string>
            {
                { AppLanguage.English, en },
                { AppLanguage.German, de },
                { AppLanguage.Chinese, cn }
            };
        }
    }
}
