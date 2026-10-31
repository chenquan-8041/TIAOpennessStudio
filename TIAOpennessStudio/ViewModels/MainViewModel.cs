﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Prism.Ioc;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Implementations;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using TIAOpennessStudio.Events;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;

namespace TIAOpennessStudio.ViewModels
{
    public class MainViewModel : BindableBase
    {
        private readonly ITiaOpennessService _tiaService;
        private IAIEngineService _aiService;
        private readonly IPlcKnowledgeIndexService _knowledgeIndex;
        private readonly PlcKnowledgeIndexer _knowledgeIndexer;
        private readonly IUIService _uiService;
        private readonly IAgentOrchestrator _agentOrchestrator;
        private readonly ICodeViewerService _codeViewerService;
        private readonly IBlockVersionService _blockVersionService;
        private readonly IOllamaHostService _ollamaHostService;
        private readonly ITiaVersionAdapter _versionAdapter;
        private readonly IEventAggregator _eventAggregator;
        
        // Child ViewModels
        private readonly ChatViewModel _chatViewModel;
        private readonly AIConfigViewModel _aiConfigViewModel;
        private readonly ProjectViewModel _projectViewModel;
        private readonly TreeViewModel _treeViewModel;
        private readonly GraphViewModel _graphViewModel;
        
        private string _statusText = "Ready";
        private bool _isConnected;
        private string _tiaVersionText = "";
        private Brush _tiaStatusBrush = Brushes.Gray;
        private string _footerSaveStatusText = "...";
        private readonly DispatcherTimer _footerStatusTimer;
        private bool _showCacheStatusInFooter;

        // Global progress indicator state
        private bool _isGlobalProgressVisible;
        private string _globalProgressMessage = "";
        private double _globalProgressPercent = -1;
        private bool _isGlobalProgressIndeterminate = true;
        
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }
        
        public bool IsConnected
        {
            get => _isConnected;
            set => SetProperty(ref _isConnected, value);
        }

        public string TiaVersionText
        {
            get => _tiaVersionText;
            set => SetProperty(ref _tiaVersionText, value);
        }

        public Brush TiaStatusBrush
        {
            get => _tiaStatusBrush;
            set => SetProperty(ref _tiaStatusBrush, value);
        }

        public string FooterSaveStatusText
        {
            get => _footerSaveStatusText;
            set => SetProperty(ref _footerSaveStatusText, value);
        }

        // ---- Global Progress Indicator Properties ----

        public bool IsGlobalProgressVisible
        {
            get => _isGlobalProgressVisible;
            set => SetProperty(ref _isGlobalProgressVisible, value);
        }

        public string GlobalProgressMessage
        {
            get => _globalProgressMessage;
            set => SetProperty(ref _globalProgressMessage, value);
        }

        public double GlobalProgressPercent
        {
            get => _globalProgressPercent;
            set
            {
                SetProperty(ref _globalProgressPercent, value);
                IsGlobalProgressIndeterminate = value < 0;
            }
        }

        public bool IsGlobalProgressIndeterminate
        {
            get => _isGlobalProgressIndeterminate;
            set => SetProperty(ref _isGlobalProgressIndeterminate, value);
        }
        
        // Child ViewModel properties
        public ChatViewModel ChatViewModel => _chatViewModel;
        public AIConfigViewModel AIConfigViewModel => _aiConfigViewModel;
        public ProjectViewModel ProjectViewModel => _projectViewModel;
        public TreeViewModel TreeViewModel => _treeViewModel;
        public GraphViewModel GraphViewModel => _graphViewModel;
        
        public ICommand ConnectCommand { get; }
        public ICommand RefreshTreeCommand { get; }
        public ICommand CopyCodeCommand { get; }
        public ICommand PreviewCodeCommand { get; }
        public ICommand ConfirmStepCommand { get; }
        public ICommand CancelStepCommand { get; }
        public ICommand UndoMessageCommand { get; }

        /// <summary>
        /// Request to show a code diff in the Code Viewer's Diff tab and wait for user confirmation.
        /// Args: (oldCode, newCode, title, planId, stepId)
        /// </summary>
        public event Action<string, string, string, string, string> ShowDiffRequested;

        /// <summary>
        /// Raised when the user clicks the per-message Undo button in chat.
        /// MainWindow.Chat.cs handles this by calling ShowDiffAndWaitAsync then ImportBlockCodeAsync.
        /// Args: (message)
        /// </summary>
        public event Action<ChatMessage> UndoMessageRequested;

        /// <summary>
        /// Tooltip text for the Undo button, kept in sync with the current UI language.
        /// </summary>
        private string _undoButtonTooltip;
        public string UndoButtonTooltip
        {
            get => _undoButtonTooltip;
            private set { _undoButtonTooltip = value; RaisePropertyChanged(); }
        }

        private void RefreshUndoTooltip()
        {
            UndoButtonTooltip = LanguageManager.Instance.GetString("Chat.Undo.Tooltip");
        }
        
        // Focused state properties (instead of exposing service interfaces directly)
        public Project Project => _tiaService.Project;
        public TiaPortal TiaPortal => _tiaService.TiaPortal;
        public PlcSoftware PlcSoftware => _tiaService.PlcSoftware;
        
        public void SetProject(Project value) => _tiaService.SetProject(value);
        public void SetTiaPortal(TiaPortal value) => _tiaService.SetTiaPortal(value);
        public void SetPlcSoftware(PlcSoftware value) => _tiaService.SetPlcSoftware(value);
        
        // TIA service (used directly by MainWindow)
        public ITiaOpennessService TiaService => _tiaService;
        
        // AI-related (used by MainWindow.AI.cs diagnose/write-back/compile flows)
        public IAIEngineService AIService => _aiService;
        public IAgentOrchestrator AgentOrchestrator => _agentOrchestrator;
        public IOllamaHostService OllamaHostService => _ollamaHostService;
        
        // Code viewer and version management services
        public ICodeViewerService CodeViewerService => _codeViewerService;
        public IBlockVersionService BlockVersionService => _blockVersionService;
        
        // Dialog service (for complex dialogs invoked from the View layer)
        public IDialogService DialogService { get; }
        
        // Index service operations (v3: uses the layered knowledge base)
        // Single-block update: whoever modifies is responsible for refreshing. Trigger one single-block rebuild by block name (delete old derived row + write new).
        public void UpsertBlockIndex(string projectKey, string blockKey, string name, string blockType, string language, string version, string filePath, string content, string gitBranch, string gitCommit)
        {
            if (_knowledgeIndexer == null || string.IsNullOrWhiteSpace(name)) return;
            _ = _knowledgeIndexer.UpdateBlockAsync(name);
        }

        public List<PlcKnowledgeSearchHit> SearchIndex(string projectKey, string query, int maxItems)
            => _knowledgeIndex?.Search(projectKey, query, maxItems) ?? new List<PlcKnowledgeSearchHit>();

        public bool HasIndexService => _knowledgeIndex != null;
        
        // Code viewer delegating methods
        public void ShowCodeViewer(string name, string type, object content)
        {
            _eventAggregator.GetEvent<ShowCodeViewerEvent>().Publish(new CodeViewerEventArgs(name, type, content));
        }
        
        public System.Windows.UIElement CreateMultiTabView(string xmlContent, string mainHeader, object mainContent, string sclContent = null, string jsonIrContent = null, bool isSafety = false)
        {
            return _codeViewerService.CreateMultiTabView(xmlContent, mainHeader, mainContent, sclContent, jsonIrContent, isSafety);
        }
        
        public string TryGenerateSource(object item)
        {
            return _codeViewerService.TryGenerateSource(item, _tiaService.PlcSoftware);
        }
        
        public bool IsSclContent(string content)
        {
            return _codeViewerService.IsSclContent(content);
        }
        
        // Version management delegating methods
        public string ComputeContentVersion(string content)
        {
            return _blockVersionService.ComputeContentVersion(content);
        }
        
        public string UpdateBlockVersion(string cacheKey, string xmlContent, string exportFile, out bool changed, out string previousVersion)
        {
            return _blockVersionService.UpdateBlockVersion(cacheKey, xmlContent, exportFile, out changed, out previousVersion);
        }
        
        public string GetProjectCacheKey(string projectPath = null)
        {
            // Auto-detect from TIA project when no explicit path is provided
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                try
                {
                    var path = _tiaService?.Project?.Path?.FullName;
                    if (!string.IsNullOrWhiteSpace(path))
                        return _blockVersionService.GetProjectCacheKey(path);
                }
                catch { }
            }
            return _blockVersionService.GetProjectCacheKey(projectPath);
        }
        
        public bool TryGetGitSnapshot(out string branch, out string commit)
        {
            return _blockVersionService.TryGetGitSnapshot(out branch, out commit);
        }
        

        
        public string ApplyContextBudget(string context, int maxChars = 22000)
        {
            return _blockVersionService.ApplyContextBudget(context, maxChars);
        }

        public async Task<PlcProjectIndexBuildResult> RebuildProjectIndexAsync(bool force = false, System.Action<string> onProgress = null, System.Threading.CancellationToken ct = default)
        {
            var workerHost = App.Services.Resolve<IProjectKnowledgeWorkerHostService>();
            var progress = App.Services.Resolve<IGlobalProgressService>();

            progress?.Show("Building project knowledge index...");

            // Option A: rebuild is a synchronous cross-process HTTP call, so progress is produced inside the Worker subprocess.
            // The main process polls /index/status while waiting and pushes the Worker's live progress to the status bar.
            using (var pollCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var pollTask = PollRebuildProgressAsync(workerHost, progress, onProgress, pollCts.Token);
                try
                {
                    var result = await workerHost.RebuildAsync(force, ct).ConfigureAwait(false);
                    return result;
                }
                finally
                {
                    pollCts.Cancel();
                    try { await pollTask.ConfigureAwait(false); } catch { /* Polling cancellation is expected */ }
                    progress?.Hide();
                }
            }
        }

        /// <summary>
        /// Poll the Worker's /index/status in the background and report live rebuild progress to the global status bar.
        /// Show a percentage when progress_total is known; otherwise show indeterminate progress plus the current item.
        /// </summary>
        private async Task PollRebuildProgressAsync(
            IProjectKnowledgeWorkerHostService workerHost,
            IGlobalProgressService progress,
            System.Action<string> onProgress,
            System.Threading.CancellationToken ct)
        {
            if (progress == null) return;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(600, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    var status = await workerHost.GetCacheStatusAsync(ct).ConfigureAwait(false);
                    if (status == null) continue;

                    var msg = string.IsNullOrWhiteSpace(status.ProgressMessage)
                        ? "Building project knowledge index..."
                        : status.ProgressMessage;

                    if (status.ProgressTotal > 0 && status.ProgressCurrent >= 0)
                    {
                        var pct = Math.Min(100.0, (double)status.ProgressCurrent / status.ProgressTotal * 100.0);
                        progress.Show($"{msg} ({status.ProgressCurrent}/{status.ProgressTotal})", pct);
                    }
                    else if (status.ProgressCurrent > 0)
                    {
                        progress.Show($"{msg} ({status.ProgressCurrent})");
                    }
                    else
                    {
                        progress.Show(msg);
                    }

                    onProgress?.Invoke(msg);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore a single failed poll and continue with the next round
                }
            }
        }

        public Task<ProjectKnowledgeCacheStatus> GetProjectKnowledgeCacheStatusAsync(System.Threading.CancellationToken ct = default)
        {
            var workerHost = App.Services.Resolve<IProjectKnowledgeWorkerHostService>();
            return workerHost.GetCacheStatusAsync(ct);
        }
        

        public MainViewModel(
            ITiaOpennessService tiaService,
            IAIEngineService aiService,
            IPlcKnowledgeIndexService knowledgeIndex,
            PlcKnowledgeIndexer knowledgeIndexer,
            IUIService uiService,
            IAgentOrchestrator agentOrchestrator,
            ICodeViewerService codeViewerService,
            IBlockVersionService blockVersionService,
            IDialogService dialogService,
            IOllamaHostService ollamaHostService,
            ITiaVersionAdapter versionAdapter,
            IEventAggregator eventAggregator,
            ChatViewModel chatViewModel,
            AIConfigViewModel aiConfigViewModel,
            ProjectViewModel projectViewModel,
            TreeViewModel treeViewModel,
            GraphViewModel graphViewModel)
        {
            _tiaService = tiaService;
            _aiService = aiService;
            _knowledgeIndex = knowledgeIndex;
            _knowledgeIndexer = knowledgeIndexer;
            _uiService = uiService;
            _agentOrchestrator = agentOrchestrator;
            _codeViewerService = codeViewerService;
            _blockVersionService = blockVersionService;
            DialogService = dialogService;
            _ollamaHostService = ollamaHostService;
            _versionAdapter = versionAdapter;
            _eventAggregator = eventAggregator;

            // Initialize TIA version info (shown in the status bar)
            try
            {
                _versionAdapter.DetectAndInitialize();
                RefreshTiaStatusText();
            }
            catch
            {
                TiaVersionText = "TIA: N/A";
            }
            
            // Child ViewModels injected via DI
            _chatViewModel = chatViewModel;
            _aiConfigViewModel = aiConfigViewModel;
            _projectViewModel = projectViewModel;
            _treeViewModel = treeViewModel;
            _graphViewModel = graphViewModel;

            _footerStatusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(4)
            };
            _footerStatusTimer.Tick += async (s, e) => await RefreshFooterSaveStatusAsync();
            
            // Initialize commands (Prism DelegateCommand)
            ConnectCommand = new DelegateCommand(async () => await ConnectAsync());
            RefreshTreeCommand = new DelegateCommand(async () => await RefreshTreeAsync(reloadBlockTree: true), () => IsConnected).ObservesProperty(() => IsConnected);
            ConfirmStepCommand = new DelegateCommand<object>(param => 
            {
                if (param is AgentActionStep step)
                {
                    var plan = _agentOrchestrator.GetPlan(step.PlanId);
                    string oldCode = plan?.SharedData.TryGetValue("ReadCode", out var rc) == true ? rc as string : null;
                    string newCode = plan?.SharedData.TryGetValue("PendingWriteCode", out var nc) == true ? nc as string : null;

                    if (!string.IsNullOrEmpty(oldCode) && !string.IsNullOrEmpty(newCode))
                    {
                        // Both old and new code are available; open the Diff tab for user review
                        ShowDiffRequested?.Invoke(oldCode, newCode, $"Diff: {plan?.TargetBlockName ?? "Block"}", step.PlanId, step.Id);
                    }
                    else
                    {
                        // No original code to compare against; confirm directly
                        _agentOrchestrator.ConfirmStep(step.PlanId, step.Id, true);
                    }
                }
            });
            CancelStepCommand = new DelegateCommand<object>(param => 
            {
                if (param is AgentActionStep step)
                {
                    _agentOrchestrator.ConfirmStep(step.PlanId, step.Id, false);
                }
            });
            CopyCodeCommand = new DelegateCommand<object>(msg => 
            {
                if (msg is ChatMessage chatMsg) _chatViewModel.CopyCodeFromMessage(chatMsg);
            });
            PreviewCodeCommand = new DelegateCommand<object>(msg => 
            {
                if (msg is ChatMessage chatMsg) _chatViewModel.PreviewCodeFromMessage(chatMsg);
            });
            UndoMessageCommand = new DelegateCommand<object>(msg =>
            {
                if (msg is ChatMessage chatMsg && chatMsg.CanUndo)
                    UndoMessageRequested?.Invoke(chatMsg);
            });

            // Keep UndoButtonTooltip in sync with language switches
            RefreshUndoTooltip();
            LanguageManager.Instance.LanguageChanged += _ => RefreshUndoTooltip();
            
            // Subscribe to child ViewModel events
            SubscribeToViewModelEvents();
            
            _tiaService.ConnectionStatusChanged += (s, e) =>
            {
                IsConnected = _tiaService.IsConnected;
                RefreshTiaStatusText();
                _ = RefreshFooterSaveStatusAsync();
            };

            _footerStatusTimer.Start();
            _ = RefreshFooterSaveStatusAsync();
        }

        private void RefreshTiaStatusText()
        {
            var version = _versionAdapter.DetectedMajorVersion > 0
                ? $"V{_versionAdapter.DetectedMajorVersion}"
                : "";
            var attached = _tiaService?.IsConnected == true;
            var hasProject = HasOpenProject();
            var hasPlc = HasPlcSoftware();

            if (!attached)
            {
                TiaStatusBrush = Brushes.Gray;
                TiaVersionText = $"TIA {version} ○ Off";
                return;
            }

            if (!hasProject)
            {
                TiaStatusBrush = Brushes.DarkOrange;
                TiaVersionText = $"TIA {version} ◐ Idle";
                return;
            }

            if (!hasPlc)
            {
                TiaStatusBrush = Brushes.Goldenrod;
                TiaVersionText = $"TIA {version} ◐ Proj";
                return;
            }

            TiaStatusBrush = Brushes.LimeGreen;
            TiaVersionText = $"TIA {version} ● Ready";
        }
        
        private void SubscribeToViewModelEvents()
        {
            // Subscribe to AIConfigViewModel's Prism events
            _eventAggregator.GetEvent<ConfigurationSavedEvent>().Subscribe(config =>
            {
                StatusText = $"AI配置已保存: {config.ModelProvider}/{config.Model}";
            });
            
            // Subscribe to ProjectViewModel's Prism events
            // ProjectStatusChangedEvent is already subscribed in MainWindow.Project.cs (to update the UI), so it isn't repeated here
            // This is hit after Reload / compile / import: reload the block tree so changes in TIA are re-read and re-rendered.
            _eventAggregator.GetEvent<RefreshTreeEvent>().Subscribe(async () => await RefreshTreeAsync(reloadBlockTree: true));
            
            // Subscribe to TreeViewModel's Prism events
            _eventAggregator.GetEvent<UpdateProjectStatusEvent>().Subscribe(() =>
            {
                if (_projectViewModel.Project != null)
                {
                    _treeViewModel.UpdateProjectStatus(_projectViewModel.Project);
                    if (_projectViewModel.PlcSoftware != null)
                    {
                        _treeViewModel.UpdateBlockCounts(_projectViewModel.PlcSoftware);
                    }
                }

                RefreshTiaStatusText();
                _ = RefreshFooterSaveStatusAsync();
            });
            _eventAggregator.GetEvent<RefreshBlockCountsEvent>().Subscribe(() =>
            {
                if (_projectViewModel.PlcSoftware != null)
                {
                    _treeViewModel.UpdateBlockCounts(_projectViewModel.PlcSoftware);
                }

                RefreshTiaStatusText();
            });

            // Global progress indicator
            _eventAggregator.GetEvent<GlobalProgressEvent>().Subscribe(payload =>
            {
                IsGlobalProgressVisible = payload.IsVisible;
                GlobalProgressMessage = payload.Message ?? "";
                GlobalProgressPercent = payload.Percent;
            }, ThreadOption.UIThread);
        }
        
        public void UpdateAIService(IAIEngineService aiService)
        {
            if (aiService == null) throw new ArgumentNullException(nameof(aiService));
            _aiService = aiService;
            _aiConfigViewModel.AIService = aiService;
        }
        
        private async Task ConnectAsync()
        {
            var lm = LanguageManager.Instance;
            _eventAggregator.GetEvent<ShowOverlayEvent>().Publish(lm.GetString("Status.Connecting"));
            StatusText = lm.GetString("Status.Connecting");
            try
            {
                bool success = await _tiaService.ConnectAsync();
                if (success)
                {
                    StatusText = lm.GetString("Status.Connected");
                    IsConnected = true;
                    // On first connect, only populate the device/PLC list; don't auto-load the block tree.
                    // The block tree is loaded on demand after the user selects a PLC in the list (avoids loading the first PLC immediately on connect).
                    await RefreshTreeAsync(reloadBlockTree: false);
                }
                else
                {
                    StatusText = lm.GetString("Status.ConnectFailed");
                    IsConnected = false;
                }
            }
            catch (Exception ex)
            {
                var message = lm.Format("Status.ConnectError", "Connection error: {0}", ex.Message);
                StatusText = message;
                IsConnected = false;
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish(message);
            }
            finally
            {
                _eventAggregator.GetEvent<HideOverlayEvent>().Publish();
            }
        }
        
        /// <summary>
        /// Refresh the project tree.
        /// </summary>
        /// <param name="reloadBlockTree">
        /// true = after Reload/compile/import/delete: after rebuilding the device list, reload the block tree (treeBlocks) of the currently selected PLC.
        /// false = first connect: only populate the device/PLC list; the block tree is loaded after the user selects a PLC.
        /// </param>
        private async Task RefreshTreeAsync(bool reloadBlockTree)
        {
            var lm = LanguageManager.Instance;
            StatusText = lm.GetString("Status.RefreshingTree");
            try
            {
                // Device/PLC list is rendered from TreeViewModel.Devices/PlcList (DTO path).
                // The former ProjectTree population held COM DeviceItem objects in a UI-bound
                // collection (STA-unsafe and unused by any XAML binding), so it was removed.

                // If a project is open, refresh the device list
                if (_projectViewModel.Project != null)
                {
                    // Remember the currently selected PLC name. IterateDevices clears/rebuilds PlcList,
                    // which nulls SelectedPlc and creates brand-new PlcItem instances.
                    string previouslySelectedName = _treeViewModel.SelectedPlc?.Name;

                    await _treeViewModel.IterateDevicesAsync();

                    if (reloadBlockTree)
                    {
                        // Re-select the previously selected PLC (matched by name) and force a rebuild of the block tree (treeBlocks).
                        // Note: IterateDevices only rebuilds Devices/PlcList; the block tree is driven by LoadTreeEvent.
                        // If it isn't re-triggered here, after Reload/delete the middle block tree keeps its old TreeViewItem unrefreshed,
                        // and the old TreeViewItem.Tag points to a deleted/invalid Openness object (accessing it again crashes).
                        // Only reload when a PLC was actually selected before, to avoid the connect scenario loading the first PLC.
                        if (!string.IsNullOrEmpty(previouslySelectedName))
                        {
                            var plcToLoad = _treeViewModel.PlcList
                                                .FirstOrDefault(p => p.Name == previouslySelectedName)
                                            ?? _treeViewModel.PlcList.FirstOrDefault();

                            if (plcToLoad != null && !string.IsNullOrEmpty(plcToLoad.Handle))
                            {
                                // Null then assign again so the SelectedPlc setter fires OnSelectedPlcChanged
                                // → selects the PLC by handle on the STA host and publishes LoadTreeEvent
                                // to rebuild the block tree. No COM touched here.
                                _treeViewModel.SelectedPlc = null;
                                _treeViewModel.SelectedPlc = plcToLoad;
                            }
                        }
                    }
                }
                
                StatusText = lm.GetString("Status.TreeRefreshed");
            }
            catch (Exception ex)
            {
                var message = lm.Format("Status.RefreshError", "Refresh error: {0}", ex.Message);
                StatusText = message;
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish(message);
            }
        }
        
        
        // SelectPlcAndLoadTree(PlcSoftware) removed: COM-based entry with no callers. PLC selection
        // now flows through PlcItem.Handle + SelectPlcByHandleAsync.

        private bool HasOpenProject()
        {
            try
            {
                return _tiaService?.Project != null;
            }
            catch
            {
                return false;
            }
        }

        private bool HasPlcSoftware()
        {
            try
            {
                return _tiaService?.PlcSoftware != null;
            }
            catch
            {
                return false;
            }
        }

        private async Task RefreshFooterSaveStatusAsync()
        {
            var saveStatus = BuildShortSaveStatus();
            var cacheStatus = await BuildShortCacheStatusAsync();

            FooterSaveStatusText = _showCacheStatusInFooter && !string.IsNullOrWhiteSpace(cacheStatus)
                ? cacheStatus
                : saveStatus;

            _showCacheStatusInFooter = !_showCacheStatusInFooter;
        }

        private string BuildShortSaveStatus()
        {
            var status = _treeViewModel?.SaveStatus;
            if (string.IsNullOrWhiteSpace(status) || status == "...")
            {
                return "...";
            }

            if (string.Equals(status, "Modified", StringComparison.OrdinalIgnoreCase))
            {
                return "Mod";
            }

            if (string.Equals(status, "Saved", StringComparison.OrdinalIgnoreCase))
            {
                return "Saved";
            }

            return status.Length > 8 ? status.Substring(0, 8) : status;
        }

        private async Task<string> BuildShortCacheStatusAsync()
        {
            try
            {
                var status = await GetProjectKnowledgeCacheStatusAsync();
                if (status == null)
                {
                    return "IDX:Err";
                }

                if (status.IsIndexing)
                {
                    return "IDX:Busy";
                }

                if (status.IsStale)
                {
                    return "IDX:Stale";
                }

                if (status.HasIndex && !status.IsConnected)
                {
                    return "IDX:Cache";
                }

                if (!status.HasIndex && !status.IsConnected)
                {
                    return "IDX:Off";
                }

                if (!status.HasIndex)
                {
                    return "IDX:Miss";
                }

                return "IDX:Warm";
            }
            catch
            {
                return "IDX:Err";
            }
        }
    }
    
    public class CodeViewerEventArgs : EventArgs
    {
        public string Name { get; }
        public string Type { get; }
        public object Content { get; }
        
        public CodeViewerEventArgs(string name, string type, object content)
        {
            Name = name;
            Type = type;
            Content = content;
        }
    }
}
