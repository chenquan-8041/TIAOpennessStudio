using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using Prism.Events;
using Prism.Mvvm;
using TIAOpennessStudio.Events;

namespace TIAOpennessStudio.ViewModels
{
    /// <summary>
    /// ViewModel for Project operations - handles project open/close, import/export, compile, etc.
    /// UI-specific code (file dialogs, message boxes) is abstracted via events.
    /// </summary>
    public class ProjectViewModel : BindableBase
    {
        private readonly ITiaOpennessService _tiaService;
        private readonly IAIEngineService _aiService;
        private readonly IEventAggregator _eventAggregator;

        private Project _project;
        private TiaPortal _tiaPortal;
        private PlcSoftware _plcSoftware;
        private string _targetPath = "";
        private string _projectName = "";
        private bool _isProjectOpen;
        private bool _isPlcSoftwareReady;
        private FileInfo _lastFoundProjectPath;

        #region Properties

        public Project Project
        {
            get => _project;
            set
            {
                if (SetProperty(ref _project, value))
                {
                    ProjectName = _project?.Name ?? "";
                    IsProjectOpen = _project != null;
                }
            }
        }

        public TiaPortal TiaPortal
        {
            get => _tiaPortal;
            set => SetProperty(ref _tiaPortal, value);
        }

        public PlcSoftware PlcSoftware
        {
            get => _plcSoftware;
            set
            {
                if (SetProperty(ref _plcSoftware, value))
                {
                    IsPlcSoftwareReady = _plcSoftware != null;
                }
            }
        }

        public string TargetPath
        {
            get => _targetPath;
            set => SetProperty(ref _targetPath, value);
        }

        public string ProjectName
        {
            get => _projectName;
            set => SetProperty(ref _projectName, value);
        }

        public bool IsProjectOpen
        {
            get => _isProjectOpen;
            set => SetProperty(ref _isProjectOpen, value);
        }

        public bool IsPlcSoftwareReady
        {
            get => _isPlcSoftwareReady;
            set => SetProperty(ref _isPlcSoftwareReady, value);
        }

        #endregion

        #region Events

        /// <summary>Raised when a file dialog should be opened.</summary>
        public event Func<string, string, string> OpenFileDialogRequested;
        public event Func<ChoiceDialogEventArgs, string> ChoiceDialogRequested;

        #endregion

        public ProjectViewModel(ITiaOpennessService tiaService, IAIEngineService aiService, IEventAggregator eventAggregator)
        {
            _tiaService = tiaService;
            _aiService = aiService;
            _eventAggregator = eventAggregator;
        }

        #region Project Management

        [Obsolete("Blocks the UI thread on the STA host (connect/find). Use EnsurePlcSoftwareReadyAsync instead.")]
        public bool EnsurePlcSoftwareReady()
        {
            if (_tiaService.TiaPortal == null)
            {
                if (_tiaPortal != null) _tiaService.SetTiaPortal(_tiaPortal);
                else if (!_tiaService.ConnectToTiaPortal()) return false;
            }

            if (_tiaService.PlcSoftware == null)
            {
                if (_plcSoftware != null) _tiaService.SetPlcSoftware(_plcSoftware);
                else _tiaService.FindPlcSoftware();
            }

            if (_tiaService.PlcSoftware == null)
            {
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish("No PLC Software found. Please open a project and select a PLC.");
                return false;
            }

            if (_plcSoftware == null) _plcSoftware = _tiaService.PlcSoftware;
            return true;
        }

        /// <summary>
        /// Async, STA-safe variant of <see cref="EnsurePlcSoftwareReady"/>. Connect and
        /// PLC-lookup are awaited (never blocked) so the UI thread never forms a reciprocal
        /// UI&lt;-&gt;STA wait. Prefer this from UI-thread event handlers.
        /// </summary>
        public async Task<bool> EnsurePlcSoftwareReadyAsync()
        {
            if (_tiaService.TiaPortal == null)
            {
                if (_tiaPortal != null) _tiaService.SetTiaPortal(_tiaPortal);
                else if (!await _tiaService.ConnectAsync()) return false;
            }

            if (_tiaService.PlcSoftware == null)
            {
                if (_plcSoftware != null) _tiaService.SetPlcSoftware(_plcSoftware);
                else await _tiaService.FindPlcSoftwareAsync();
            }

            if (_tiaService.PlcSoftware == null)
            {
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish("No PLC Software found. Please open a project and select a PLC.");
                return false;
            }

            if (_plcSoftware == null) _plcSoftware = _tiaService.PlcSoftware;
            return true;
        }

        /// <summary>
        /// Async entry point: runs the whole connect + open-project flow on the dedicated
        /// TIA STA host thread so the WPF UI thread is never blocked by Openness COM calls.
        /// UI interactions (dialogs/message boxes) raised from inside are marshaled back to
        /// the UI thread by their respective handlers (see MainWindow.Project.cs).
        /// </summary>
        public Task OpenProjectAsync()
        {
            return TiaStaHost.Instance.RunAsync(() => OpenProject());
        }

        public void OpenProject()
        {
            _eventAggregator.GetEvent<ShowOverlayEvent>().Publish("Connecting to TIA Portal...");
            try
            {
                // If a TiaPortal instance already exists, first check whether it's still valid
                if (_tiaPortal != null)
                {
                    try
                    {
                        // Try accessing the Projects property to verify the connection is valid
                        var projectCount = _tiaPortal.Projects.Count;
                        if (projectCount == 0)
                        {
                            AppLogger.Log("[OpenProject] Existing TIA Portal connection has 0 projects, reconnecting to find a valid project...");
                            _tiaPortal = null;
                        }
                        else
                        {
                            AppLogger.Log($"[OpenProject] Existing TIA Portal connection valid, {projectCount} projects");
                        }
                    }
                    catch
                    {
                        AppLogger.Log("[OpenProject] Existing TIA Portal connection invalid, reconnecting...");
                        _tiaPortal = null;
                    }
                }

                // If there's no valid TiaPortal connection, try to connect
                if (_tiaPortal == null)
                {
                    var processes = TiaPortal.GetProcesses();
                    AppLogger.Log($"[OpenProject] Found {processes.Count} TIA Portal processes.");
                    
                    // Log detailed info for each process
                    for (int i = 0; i < processes.Count; i++)
                    {
                        var process = processes[i];
                        string processName = "Unknown";
                        string processTitle = "Unknown";
                        try
                        {
                            // Get process details
                            var sysProcess = System.Diagnostics.Process.GetProcessById(process.Id);
                            processName = sysProcess.ProcessName;
                            processTitle = sysProcess.MainWindowTitle;
                        }
                        catch { }
                        
                        AppLogger.Log($"[OpenProject] Process {i+1}: ID={process.Id}, Name={processName}, Title='{processTitle}', ProjectPath={(process.ProjectPath == null ? "null" : process.ProjectPath.FullName)}");
                    }
                    
                    if (processes.Count > 0)
                    {
                        // Prefer processes with a valid ProjectPath
                        var orderedProcesses = processes.OrderByDescending(p => p.ProjectPath != null).ToList();

                        foreach (var process in orderedProcesses)
                        {
                            try
                            {
                                AppLogger.Log($"[OpenProject] Trying to attach to process ID: {process.Id}, ProjectPath: {(process.ProjectPath == null ? "null" : process.ProjectPath.FullName)}");
                                var portal = process.Attach();
                                AppLogger.Log($"[OpenProject] Attached to process ID: {process.Id}. Checking projects count...");
                                
                                // Log detailed info about the TIA Portal instance
                                AppLogger.Log($"[OpenProject] Portal type: {portal.GetType().FullName}");
                                AppLogger.Log($"[OpenProject] Portal.ToString(): {portal.ToString()}");
                                
                                // Log detailed state of the Projects collection
                                AppLogger.Log($"[OpenProject] Portal.Projects type: {portal.Projects.GetType().FullName}");
                                AppLogger.Log($"[OpenProject] Portal.Projects.Count: {portal.Projects.Count}");
                                
                                // Try inspecting other properties of the Projects collection
                                try
                                {
                                    var projectsType = portal.Projects.GetType();
                                    AppLogger.Log($"[OpenProject] Projects type properties: {string.Join(", ", projectsType.GetProperties().Select(p => p.Name))}");
                                }
                                catch (Exception propEx)
                                {
                                    AppLogger.Log($"[OpenProject] Failed to get Projects properties: {propEx.Message}");
                                }
                                
                                // Try enumerating the Projects collection (even when Count is 0, there may be hidden projects)
                                int actualProjectCount = 0;
                                try
                                {
                                    foreach (Project p in portal.Projects)
                                    {
                                        actualProjectCount++;
                                        AppLogger.Log($"[OpenProject] Found project in enumeration: {p.Name}, Path: {p.Path.FullName}");
                                    }
                                }
                                catch (Exception enumEx)
                                {
                                    AppLogger.Log($"[OpenProject] Failed to enumerate Projects: {enumEx.Message}");
                                }
                                
                                if (actualProjectCount > 0)
                                {
                                    AppLogger.Log($"[OpenProject] Actual project count from enumeration: {actualProjectCount}");
                                }
                                
                                if (portal.Projects.Count > 0 || actualProjectCount > 0)
                                {
                                    _tiaPortal = portal;
                                    _lastFoundProjectPath = process.ProjectPath;
                                    AppLogger.Log($"[OpenProject] Process ID {process.Id} selected as valid.");
                                    break;
                                }
                                else
                                {
                                    AppLogger.Log($"[OpenProject] Process ID {process.Id} has 0 projects (both Count and enumeration).");
                                    // Stage this instance but don't break; keep looking for a better one
                                    if (_tiaPortal == null)
                                    {
                                        _tiaPortal = portal;
                                        if (process.ProjectPath != null)
                                        {
                                            _lastFoundProjectPath = process.ProjectPath;
                                        }
                                    }
                                    else if (_lastFoundProjectPath == null && process.ProjectPath != null)
                                    {
                                        _tiaPortal = portal;
                                        _lastFoundProjectPath = process.ProjectPath;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Log($"[OpenProject] Failed to attach to process ID: {process.Id}. Error: {ex.Message}");
                                AppLogger.Log($"[OpenProject] Exception type: {ex.GetType().FullName}");
                                if (ex.InnerException != null)
                                {
                                    AppLogger.Log($"[OpenProject] Inner exception: {ex.InnerException.Message}");
                                }
                            }
                        }
                    }
                }

                if (_tiaPortal == null)
                {
                    _eventAggregator.GetEvent<HideOverlayEvent>().Publish();
                    
                    var options = new List<ChoiceOption>
                    {
                        new ChoiceOption("with_ui", "启动新实例 (显示 TIA 界面)"),
                        new ChoiceOption("without_ui", "启动新实例 (后台静默运行)"),
                        new ChoiceOption("cancel", "取消")
                    };
                    
                    var choiceArgs = new ChoiceDialogEventArgs(
                        "未检测到运行的 TIA Portal",
                        "当前没有正在运行的 TIA Portal 实例。\n是否要启动一个新的实例来打开项目文件？",
                        options
                    );
                    
                    string choice = ChoiceDialogRequested?.Invoke(choiceArgs);
                    
                    if (choice == "with_ui" || choice == "without_ui")
                    {
                        string filePath = OpenFileDialogRequested?.Invoke(
                            "TIA Portal Project (*.ap*)|*.ap*|All files (*.*)|*.*",
                            "");

                        if (!string.IsNullOrEmpty(filePath))
                        {
                            try
                            {
                                _eventAggregator.GetEvent<ShowOverlayEvent>().Publish("Starting TIA Portal instance...");
                                TiaPortalMode mode = choice == "with_ui" ? TiaPortalMode.WithUserInterface : TiaPortalMode.WithoutUserInterface;
                                _tiaPortal = new TiaPortal(mode);

                                _eventAggregator.GetEvent<ShowOverlayEvent>().Publish("Opening Project...");
                                Project = _tiaPortal.Projects.Open(new FileInfo(filePath));
                                AppLogger.Log($"[OpenProject] Successfully started new TIA Portal and opened: {filePath}");
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Log($"[OpenProject] Failed to start TIA Portal or open project: {ex.Message}");
                                _eventAggregator.GetEvent<HideOverlayEvent>().Publish();
                                _eventAggregator.GetEvent<ShowMessageEvent>().Publish($"Error starting TIA Portal or opening project:\n{ex.Message}");
                                if (_tiaPortal != null) { _tiaPortal.Dispose(); _tiaPortal = null; }
                                return;
                            }
                        }
                        else
                        {
                            AppLogger.Log("[OpenProject] User cancelled file selection for new instance.");
                            return;
                        }
                    }
                    else
                    {
                        AppLogger.Log("[OpenProject] User cancelled starting new instance.");
                        return;
                    }
                }
                else
                {
                    _eventAggregator.GetEvent<ShowOverlayEvent>().Publish("TIA Portal Connected. Checking Project...");
                }

                // Check whether a project is open
                if (_tiaPortal.Projects.Count == 0)
                {
                    _eventAggregator.GetEvent<HideOverlayEvent>().Publish();
                    
                    // If Projects.Count == 0, this TIA Portal instance has no project loaded,
                    // but we have a cached projectPath to offer to the user
                    if (_lastFoundProjectPath != null && _lastFoundProjectPath.Exists)
                    {
                        // Show the choice dialog
                        var options = new List<ChoiceOption>
                        {
                            new ChoiceOption("open_cached", "打开缓存的项目", $"打开项目: {_lastFoundProjectPath.Name}"),
                            new ChoiceOption("select_file", "选择其他项目文件"),
                            new ChoiceOption("cancel", "取消")
                        };
                        
                        var choiceArgs = new ChoiceDialogEventArgs(
                            "选择打开方式",
                            $"TIA Portal进程已连接，但未检测到打开的项目。\n检测到项目路径: {_lastFoundProjectPath.FullName}",
                            options
                        );
                        
                        string selectedOption = ChoiceDialogRequested?.Invoke(choiceArgs);
                        
                        bool TryOpenProject(FileInfo targetPath)
                        {
                            try
                            {
                                _eventAggregator.GetEvent<ShowOverlayEvent>().Publish("Opening Project...");
                                Project = _tiaPortal.Projects.Open(targetPath);
                                AppLogger.Log($"[OpenProject] Successfully opened project from file: {targetPath.FullName}");
                                return true;
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Log($"[OpenProject] Failed to open project {targetPath.FullName}: {ex.Message}");
                                _eventAggregator.GetEvent<HideOverlayEvent>().Publish();
                                
                                if (ex.Message.Contains("Another project is already open"))
                                {
                                    var msg = $"无法打开项目: TIA Portal 界面中已经有一个项目处于打开状态，但由于某些原因(如缺少组件或未处理的安全弹窗)，Openness API 无法访问它。\n\n请在 TIA Portal 中关闭当前项目，或处理完警告弹窗后再试。";
                                    _eventAggregator.GetEvent<ShowMessageEvent>().Publish(msg);
                                }
                                else
                                {
                                    _eventAggregator.GetEvent<ShowMessageEvent>().Publish($"Error opening project: {ex.Message}");
                                }
                                return false;
                            }
                        }
                        
                        if (selectedOption == "open_cached")
                        {
                            AppLogger.Log($"[OpenProject] User chose to open cached project: {_lastFoundProjectPath.FullName}");
                            if (!TryOpenProject(_lastFoundProjectPath))
                            {
                                // After a failed cached-project open, the error is already shown, so just return here
                                return;
                            }
                        }
                        else if (selectedOption == "select_file")
                        {
                            AppLogger.Log("[OpenProject] User chose to select other project file");
                            string filePath = OpenFileDialogRequested?.Invoke(
                                "TIA Portal Project (*.ap*)|*.ap*|All files (*.*)|*.*",
                                "");

                            if (!string.IsNullOrEmpty(filePath))
                            {
                                TryOpenProject(new FileInfo(filePath));
                            }
                            else
                            {
                                AppLogger.Log("[OpenProject] User cancelled file selection");
                                return;
                            }
                        }
                        else if (selectedOption == "cancel" || selectedOption == null)
                        {
                            AppLogger.Log("[OpenProject] User cancelled operation");
                            return;
                        }
                    }
                    else
                    {
                        // No cached path; let the user choose a file
                        string filePath = OpenFileDialogRequested?.Invoke(
                            "TIA Portal Project (*.ap*)|*.ap*|All files (*.*)|*.*",
                            "");

                        if (!string.IsNullOrEmpty(filePath))
                        {
                            try
                            {
                                _eventAggregator.GetEvent<ShowOverlayEvent>().Publish("Opening Project...");
                                FileInfo projectPath = new FileInfo(filePath);
                                Project = _tiaPortal.Projects.Open(projectPath);
                                AppLogger.Log($"[OpenProject] Opened project from file: {filePath}");
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Log($"[OpenProject] Failed to open project {filePath}: {ex.Message}");
                                _eventAggregator.GetEvent<HideOverlayEvent>().Publish();
                                
                                if (ex.Message.Contains("Another project is already open"))
                                {
                                    var msg = $"无法打开项目: TIA Portal 界面中已经有一个项目处于打开状态，但由于某些原因(如缺少组件或未处理的安全弹窗)，Openness API 无法访问它。\n\n请在 TIA Portal 中关闭当前项目，或处理完警告弹窗后再试。";
                                    _eventAggregator.GetEvent<ShowMessageEvent>().Publish(msg);
                                }
                                else
                                {
                                    _eventAggregator.GetEvent<ShowMessageEvent>().Publish($"Error opening project: {ex.Message}");
                                }
                                return;
                            }
                        }
                        else
                        {
                            AppLogger.Log("[OpenProject] User cancelled file selection");
                            return;
                        }
                    }
                }
                else
                {
                    Project = _tiaPortal.Projects[0];
                    AppLogger.Log($"[OpenProject] Using existing project: {_project.Name}");
                }

                if (_project != null)
                {
                    // Sync to ITiaOpennessService so MainViewModel can perceive the connection state
                    _tiaService.SetTiaPortal(_tiaPortal);
                    _tiaService.SetProject(_project);
                    TiaPortal = _tiaPortal;

                    _eventAggregator.GetEvent<ShowOverlayEvent>().Publish("Reading Project Structure...");
                    _eventAggregator.GetEvent<ProjectStatusChangedEvent>().Publish();
                    _eventAggregator.GetEvent<RefreshTreeEvent>().Publish();
                    _eventAggregator.GetEvent<ShowMessageEvent>().Publish($"Connected to: {_project.Name}");
                    AppLogger.Log($"[OpenProject] Project opened successfully: {_project.Name}");
                }
            }
            catch (Exception ex)
            {
                _eventAggregator.GetEvent<HideOverlayEvent>().Publish();
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish("Error opening project: " + ex.Message);
                AppLogger.Log($"[OpenProject] Error: {ex.Message}");
                AppLogger.Log($"[OpenProject] Stack trace: {ex.StackTrace}");
                _tiaPortal = null;
            }
            finally
            {
                _eventAggregator.GetEvent<HideOverlayEvent>().Publish();
            }
        }

        /// <summary>
        /// Open a project by file path (async overload).
        /// </summary>
        public async Task OpenProject(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish("Project path is empty.");
                return;
            }

            // TIA Openness requires an STA thread, so Task.Run can't be used
            await Task.CompletedTask;
            _eventAggregator.GetEvent<ShowOverlayEvent>().Publish("Connecting to TIA Portal...");
            try
            {
                if (_tiaPortal == null)
                {
                    var processes = TiaPortal.GetProcesses();
                    if (processes.Count > 0)
                    {
                        _tiaPortal = processes[0].Attach();
                    }
                }

                if (_tiaPortal == null)
                {
                    _eventAggregator.GetEvent<HideOverlayEvent>().Publish();
                    _eventAggregator.GetEvent<ShowMessageEvent>().Publish("No running TIA Portal instance found.");
                    return;
                }

                _eventAggregator.GetEvent<ShowOverlayEvent>().Publish("Opening project...");
                FileInfo projectFile = new FileInfo(projectPath);
                Project = _tiaPortal.Projects.Open(projectFile);

                if (_project != null)
                {
                    // Sync to ITiaOpennessService so MainViewModel can perceive the connection state
                    _tiaService.SetTiaPortal(_tiaPortal);
                    _tiaService.SetProject(_project);

                    _eventAggregator.GetEvent<ProjectStatusChangedEvent>().Publish();
                    _eventAggregator.GetEvent<RefreshTreeEvent>().Publish();
                    _eventAggregator.GetEvent<ShowMessageEvent>().Publish($"Opened project: {_project.Name}");
                }
            }
            catch (Exception ex)
            {
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish("Error opening project: " + ex.Message);
            }
            finally
            {
                _eventAggregator.GetEvent<HideOverlayEvent>().Publish();
            }
        }

        public void ReloadProject()
        {
            if (_project == null) return;

            _eventAggregator.GetEvent<ShowOverlayEvent>().Publish("Reloading...");
            try
            {
                _eventAggregator.GetEvent<RefreshTreeEvent>().Publish();
            }
            finally
            {
                _eventAggregator.GetEvent<HideOverlayEvent>().Publish();
            }
        }

        public async Task SaveProjectAsync()
        {
            // IsModified check + Save run on the STA host thread (never touch COM on the UI thread).
            // Fully async: never block the UI thread on the STA-bound Task (that reciprocal
            // UI<->STA wait is what deadlocked project close).
            try
            {
                await _tiaService.SaveProjectAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ProjectViewModel] SaveProject failed: {ex.Message}");
            }
        }

        public async Task CloseProjectAsync()
        {
            if (_project != null)
            {
                await SaveProjectAsync();

                // Clear the PlcSoftware reference
                _plcSoftware = null;
                PlcSoftware = null;
                
                // Clear the Project reference (triggers IsProjectOpen = false)
                Project = null;
                
                // Clear the references in TiaService
                _tiaService.SetPlcSoftware(null);
                _tiaService.SetProject(null);
                
                // Publish events to notify the UI to refresh
                _eventAggregator.GetEvent<RefreshTreeEvent>().Publish();
                _eventAggregator.GetEvent<ProjectStatusChangedEvent>().Publish();
                
                AppLogger.Log("[ProjectViewModel] Project closed in application (TIA Portal project remains open)");
            }
        }

        #endregion

        #region Import/Export

        /// <summary>
        /// Import generated SCL/XML code into TIA Portal.
        /// </summary>
        /// <param name="code">SCL or XML payload (XML is detected by content prefix).</param>
        /// <param name="blockName">Logical block name. Defaults to "AI_Generated_Block".</param>
        /// <param name="blockType">"FB"/"FC"/"DB"/"OB"/"UDT". Defaults to "FB".</param>
        /// <param name="groupPath">Backslash path under BlockGroup/TypeGroup. Empty = root.</param>
        public async Task ImportCodeAsync(string code, string blockName = "AI_Generated_Block", string blockType = "FB", string groupPath = "")
        {
            if (string.IsNullOrEmpty(code))
            {
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish("Please generate code first.");
                return;
            }

            if (!await EnsurePlcSoftwareReadyAsync()) return;

            try
            {
                string payload = code;

                var importTask = _tiaService.ImportBlockCodeAsync(blockName, blockType, payload, groupPath ?? "");
                var completedTask = await Task.WhenAny(importTask, Task.Delay(TimeSpan.FromSeconds(90)));
                if (completedTask != importTask)
                {
                    throw new TimeoutException("TIA import timed out. TIA Portal may still be busy. Please verify the project state before retrying.");
                }

                string result = await importTask;

                _eventAggregator.GetEvent<ShowMessageEvent>().Publish(result ?? "Import Successful!");
                _eventAggregator.GetEvent<ImportSuccessfulEvent>().Publish();
            }
            catch (Exception ex)
            {
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish("Import Failed: " + ex.Message);
            }
        }

        public void ExportBlock(string blockName, string blockType)
        {
            // Export logic will be implemented based on requirements
            throw new NotImplementedException("Export functionality to be implemented");
        }

        #endregion

        #region Compile

        // CompileProject / BuildCompileVerifyReport / AppendCompilerMessages removed:
        // UI-thread COM compile via the ProjectViewModel._plcSoftware shadow field. The compile
        // button now calls OpennessService.CompileAsync() (STA host thread, authoritative PlcSoftware).

        #endregion

        #region Block Operations

        public bool NameExists(string name)
        {
            try
            {
                if (_plcSoftware.BlockGroup.Blocks.Find(name) != null) return true;
                if (_plcSoftware.TypeGroup.Types.Find(name) != null) return true;
            }
            catch { }
            return false;
        }

        public string ResolveBlockType(PlcBlock block)
        {
            string name = block?.Name ?? "";
            string typeName = block?.GetType().Name ?? "";
            string low = typeName.ToLowerInvariant();
            if (low.Contains("functionblock")) return "FB";
            if (low.Contains("function")) return "FC";
            if (low.Contains("datablock")) return "DB";
            if (low.Contains("organization")) return "OB";
            if (name.StartsWith("FB_", StringComparison.OrdinalIgnoreCase)) return "FB";
            if (name.StartsWith("FC_", StringComparison.OrdinalIgnoreCase)) return "FC";
            if (name.StartsWith("DB_", StringComparison.OrdinalIgnoreCase)) return "DB";
            if (name.StartsWith("OB_", StringComparison.OrdinalIgnoreCase)) return "OB";
            return "FB";
        }

        #endregion
    }
    
    /// <summary>
    /// Event args for the choice dialog.
    /// </summary>
    public class ChoiceDialogEventArgs : EventArgs
    {
        public string Title { get; }
        public string Message { get; }
        public List<ChoiceOption> Options { get; }
        public string SelectedOption { get; set; }
        
        public ChoiceDialogEventArgs(string title, string message, List<ChoiceOption> options)
        {
            Title = title;
            Message = message;
            Options = options;
        }
    }
    
    /// <summary>
    /// A choice option.
    /// </summary>
    public class ChoiceOption
    {
        public string Id { get; }
        public string Text { get; }
        public string Description { get; }
        
        public ChoiceOption(string id, string text, string description = null)
        {
            Id = id;
            Text = text;
            Description = description;
        }
    }
}
