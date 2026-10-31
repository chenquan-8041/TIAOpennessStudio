using System;
using System.Threading.Tasks;
using System.Windows;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System.IO;
using TIAOpennessStudio.Services;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Windows.Media;
using TIAOpennessStudio.ViewModels;
using TIAOpennessStudio.LadRendering.Rendering;
using TIAOpennessStudio.LadRendering.Editor;
using TIAOpennessStudio.Controls;
using TIAOpennessStudio.Services.Abstractions;
using Prism.Events;
using TIAOpennessStudio.Events;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace TIAOpennessStudio
{
    public partial class MainWindow : Window
    {
        private MainViewModel _viewModel;
        private Project project
        {
            get => _viewModel.Project;
            set => _viewModel.SetProject(value);
        }
        private TiaPortal tiaPortal
        {
            get => _viewModel.TiaPortal;
            set => _viewModel.SetTiaPortal(value);
        }
        private PlcSoftware software
        {
            get => _viewModel.PlcSoftware;
            set => _viewModel.SetPlcSoftware(value);
        }
        private IAIEngineService aiEngine => _viewModel.AIService;
        private IAIEngineService aiGenerator
        {
            get => _viewModel.AIService;
            set
            {
                if (value == null) return;
                _viewModel.UpdateAIService(value);
            }
        }
        private IAgentOrchestrator agentOrchestrator => _viewModel.AgentOrchestrator;
        private readonly StepDataOne step = new StepDataOne();

        // LAD editing related
        private LadEditorWrapper _ladEditorWrapper;
        private IEventAggregator _eventAggregator;

        public MainWindow(MainViewModel viewModel, IEventAggregator eventAggregator)
        {
            InitializeComponent();
            TIAOpennessStudio.Services.TiaOpennessWhitelistService.AutoRegisterIfAdmin();
            _viewModel = viewModel;
            _eventAggregator = eventAggregator;
            this.DataContext = _viewModel;

            // Initialize compatibility fields (InitProjectModule first!)
            InitProjectModule();
            InitChatModule();
            InitTreeModule();
            InitGraphModule();
            BindChatPanelEvents();
            SubscribeToMainViewModelEvents();
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
            OnLanguageChanged(LanguageManager.Instance.CurrentLanguage);
            UpdateMenuEnableStates();

            // Initialize the LAD editor placeholder view
            InitializeLadEditorPlaceholder();
            this.SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                _hwndSource = HwndSource.FromHwnd(hwnd);
                _hwndSource.AddHook(WndProc);
            };
        }

        private void SubscribeToMainViewModelEvents()
        {
            // Subscribe to MainViewModel Prism events (ShowCodeViewer and other UI interaction events)
            _eventAggregator.GetEvent<ShowCodeViewerEvent>().Subscribe(e => ShowCodeViewerInTab(e.Name, e.Type, e.Content), ThreadOption.UIThread);

            // Subscribe to OPC UA live monitor page requests: hosted as a central tab, consistent with other features
            _eventAggregator.GetEvent<ShowOpcUaMonitorEvent>().Subscribe(
                e => ShowCodeViewerInTab(e.Title, "OpcUaMonitor", e.Content), ThreadOption.UIThread);

            // Subscribe to diff preview requests
            _viewModel.ShowDiffRequested += OnShowDiffRequested;

            // Subscribe to cross-reference navigation requests (user clicks an item in the Cross Reference panel)
            LadWpfRenderer.NavigateToReference += OnNavigateToCrossReference;
        }

        /// <summary>
        /// Handle cross-reference navigation: open the target block and show the corresponding network.
        /// </summary>
        private void OnNavigateToCrossReference(string blockName, int networkIndex)
        {
            if (string.IsNullOrWhiteSpace(blockName)) return;

            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    AppLogger.Log($"[CrossRef Navigate] Navigating to block '{blockName}' network {networkIndex}");

                    // Find and select the target block node in the block tree
                    var targetNode = FindTreeNodeByBlockName(treeBlocks, blockName);
                    if (targetNode != null)
                    {
                        // Select the node and trigger the double-click effect
                        targetNode.IsSelected = true;
                        targetNode.BringIntoView();

                        // Simulate double-click open
                        treeBlocks_MouseDoubleClick(treeBlocks, null);
                        AppLogger.Log($"[CrossRef Navigate] Opened block '{blockName}' via tree selection");
                    }
                    else
                    {
                        AppLogger.Log($"[CrossRef Navigate] Block '{blockName}' not found in tree");
                        _viewModel.StatusText = $"Cross-reference: block '{blockName}' not found in project tree";
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[CrossRef Navigate] Error: {ex.Message}");
                }
            });
        }

        /// <summary>Recursively search tree nodes for a node whose Tag is PlcBlock and whose name matches</summary>
        private static TreeViewItem FindTreeNodeByBlockName(ItemsControl parent, string blockName)
        {
            foreach (var item in parent.Items)
            {
                TreeViewItem tvi = item as TreeViewItem;
                if (tvi == null) continue;

                // Check whether the Tag is PlcBlock and the name matches
                if (tvi.Tag != null)
                {
                    try
                    {
                        var nameProp = tvi.Tag.GetType().GetProperty("Name");
                        if (nameProp != null)
                        {
                            var name = nameProp.GetValue(tvi.Tag)?.ToString();
                            if (string.Equals(name, blockName, StringComparison.OrdinalIgnoreCase))
                            {
                                var typeName = tvi.Tag.GetType().Name;
                                if (typeName.Contains("Block") || typeName.Contains("OB") ||
                                    typeName.Contains("FB") || typeName.Contains("FC") ||
                                    typeName.Contains("DB"))
                                    return tvi;
                            }
                        }
                    }
                    catch { }
                }

                // Recursively search child nodes
                var found = FindTreeNodeByBlockName(tvi, blockName);
                if (found != null) return found;
            }

            return null;
        }

        private void OnShowDiffRequested(string oldCode, string newCode, string title, string planId, string stepId)
        {
            ShowDiffTab(oldCode, newCode, title);

            // Clear old subscriptions to avoid duplicate bindings
            diffPreviewControl.Applied -= OnDiffApplied;
            diffPreviewControl.Rejected -= OnDiffRejected;

            // User clicks Apply → confirm write
            diffPreviewControl.Applied += OnDiffApplied;
            // User clicks Reject → cancel write
            diffPreviewControl.Rejected += OnDiffRejected;

            void OnDiffApplied(object s, EventArgs e)
            {
                diffPreviewControl.Applied -= OnDiffApplied;
                diffPreviewControl.Rejected -= OnDiffRejected;
                _viewModel.AgentOrchestrator.ConfirmStep(planId, stepId, true);
                CloseDiff_Click(null, null);
            }

            void OnDiffRejected(object s, EventArgs e)
            {
                diffPreviewControl.Applied -= OnDiffApplied;
                diffPreviewControl.Rejected -= OnDiffRejected;
                _viewModel.AgentOrchestrator.ConfirmStep(planId, stepId, false);
                CloseDiff_Click(null, null);
            }
        }

        private HwndSource _hwndSource;

        private const int WM_NCLBUTTONDBLCLK = 0x00A3;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCLBUTTONDBLCLK)
            {
                Maximize_Click(this, new RoutedEventArgs());
                handled = true;
            }
            return IntPtr.Zero;
        }
        private double _normalLeft;
        private double _normalTop;
        private double _normalWidth;
        private double _normalHeight;
        private bool _isMaximized = false;

        private System.Windows.Point _treeDragStartPoint;
        private string _lastDiagnosticProposal = "";


        private async void MnuBlockDelete_Click(object sender, RoutedEventArgs e)
        {
            // DTO path: delete by handle on the STA host thread.
            if ((treeBlocks.SelectedItem as TreeViewItem)?.Tag is PlcTreeNodeDto dto)
            {
                if (dto.Kind != PlcTreeNodeKind.Block && dto.Kind != PlcTreeNodeKind.Type) return;
                string label = dto.Kind == PlcTreeNodeKind.Type ? "data type" : "block";
                if (MessageBox.Show($"Do you really want to delete the {label} {dto.Name}?",
                        $"Delete {label}", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                try
                {
                    await _viewModel.TiaService.DeleteByHandleAsync(dto.Handle);
                    RefreshBlocksTreeAfterMutation();
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Error"); }
                return;
            }

            // ── Legacy COM path below (unreachable once the tree is DTO-based) ──
            var item = treeBlocks.SelectedItem as TreeViewItem;
            if (item != null && item.Tag is PlcBlock block)
            {
                if (MessageBox.Show($"Do you really want to delete the block {block.Name}?", "Delete block", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    try
                    {
                        block.Delete();
                        IterateThroughDevices(project);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
            }
            else if (item != null && item.Tag is PlcStruct type)
            {
                if (MessageBox.Show($"Do you really want to delete the data type {type.Name}?", "Delete data type", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    try
                    {
                        type.Delete();
                        IterateThroughDevices(project);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
            }
        }

        private async void MenuGroupDelete_Click(object sender, RoutedEventArgs e)
        {
            // DTO path: group delete by handle on the STA host thread.
            if ((treeBlocks.SelectedItem as TreeViewItem)?.Tag is PlcTreeNodeDto gdto)
            {
                if (gdto.Kind != PlcTreeNodeKind.BlockGroup && gdto.Kind != PlcTreeNodeKind.TypeGroup) return;
                try
                {
                    if (await _viewModel.TiaService.GroupHasChildrenAsync(gdto.Handle))
                    {
                        MessageBox.Show($"The group {gdto.Name} has sub items! Delete those first.", "Delete group");
                        return;
                    }
                    await _viewModel.TiaService.DeleteByHandleAsync(gdto.Handle);
                    RefreshBlocksTreeAfterMutation();
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Error"); }
                return;
            }

            // ── Legacy COM path below (unreachable once the tree is DTO-based) ──
            var item = treeBlocks.SelectedItem as TreeViewItem;
            if (item == null) return;

            if (item.Tag is PlcBlockGroup group1)
            {
                if (group1.Blocks.Count > 0)
                {
                    MessageBox.Show($"The group {group1.Name} has sub blocks! Delete these blocks first.", "Delete group");
                    return;
                }
            }
            else if (item.Tag is PlcTypeUserGroup group2)
            {
                if (group2.Groups.Count > 0)
                {
                    MessageBox.Show($"The type group {group2.Name} has sub blocks! Delete these blocks first.", "Delete group");
                    return;
                }
            }

            var selectedProjectObject = item.Tag;
            try
            {
                var engineeringObject = selectedProjectObject as IEngineeringObject;
                engineeringObject?.Invoke("Delete", new Dictionary<Type, object>());
                IterateThroughDevices(project);
            }
            catch (Exception)
            {
                // Ignore
            }
        }

        // GroupBlockExists/GroupTypeExists removed: the duplicate-name check now lives in
        // OpennessService.CreateGroupByHandle* and runs on the STA host thread.

        private async void MenuGroupAdd_Click(object sender, RoutedEventArgs e)
        {
            // Tag now carries a PlcTreeNodeDto; the group is created by its opaque handle on the
            // STA host thread. Covers both the system root groups and user subgroups (Block/Type).
            if (!((treeBlocks.SelectedItem as TreeViewItem)?.Tag is PlcTreeNodeDto dto) ||
                (dto.Kind != PlcTreeNodeKind.BlockGroup && dto.Kind != PlcTreeNodeKind.TypeGroup))
                return;

            string name = Microsoft.VisualBasic.Interaction.InputBox("Enter new group name", "New group", "");
            if (string.IsNullOrEmpty(name)) return;

            try
            {
                var res = await _viewModel.TiaService.CreateGroupByHandleAsync(dto.Handle, name);
                if (res == null || (!res.Success && !res.AlreadyExists))
                {
                    MessageBox.Show(res?.Error ?? "Failed to create group.", "Exception");
                    return;
                }
                if (res.AlreadyExists)
                {
                    MessageBox.Show(name + " exist already", "Name exist already");
                    return;
                }
                RefreshBlocksTreeAfterMutation();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Exception");
            }
        }

        /// <summary>
        /// Whether to try exporting PDF as a fallback preview when opening a block.
        /// Default false: PrintService.Print() runs synchronously on the UI thread and is very slow, which is the main cause of double-click lag.
        /// Set to true to restore the old behavior (print to PDF preview when a block cannot be shown as source/graphics).
        /// </summary>
        private const bool EnablePdfPreviewOnOpen = false;

        private async void treeBlocks_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // DTO path: Tag now carries a PlcTreeNodeDto (plain data + opaque handle).
            // Export runs on the STA host thread inside OpennessService; the UI thread only
            // parses/renders the returned XML (no COM touched here).
            if ((treeBlocks.SelectedItem as TreeViewItem)?.Tag is PlcTreeNodeDto nodeDto)
            {
                await OpenTreeNodeDtoAsync(nodeDto);
                return;
            }
        }


        /// <summary>
        /// Open a block/type tree node (DTO) in the code viewer. The COM export runs on the
        /// STA host thread (OpennessService.GetBlockContentAsync); XML parsing/rendering runs
        /// on the UI thread and never touches a COM object.
        /// </summary>
        private async Task OpenTreeNodeDtoAsync(PlcTreeNodeDto nodeDto)
        {
            if (nodeDto.Kind == PlcTreeNodeKind.TagTable || nodeDto.Kind == PlcTreeNodeKind.HmiTagTable)
            {
                // Fetch tag content on the STA thread via handle; render purely from DTO.
                await OpenTagTableFromDtoAsync(nodeDto);
                return;
            }
            if (nodeDto.Kind != PlcTreeNodeKind.Block && nodeDto.Kind != PlcTreeNodeKind.Type)
                return;

            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            ShowRegionOverlay(codeViewerOverlay, txtCodeViewerOverlayMessage, "Exporting from Openness...");
            try
            {
                string title = nodeDto.Name;
                string typeStr = nodeDto.Kind == PlcTreeNodeKind.Type ? "Type" : "Block";

                var bc = await _viewModel.TiaService.GetBlockContentAsync(nodeDto.Handle);
                if (bc == null || !string.IsNullOrEmpty(bc.Error))
                    throw new Exception(bc?.Error ?? "Export failed.");

                string xmlContent = bc.Xml ?? "";
                string lang = bc.Language ?? "";
                object content;

                if (nodeDto.Kind == PlcTreeNodeKind.Type)
                {
                    content = XmlCodeParser.Parse(xmlContent);
                    if (content is string st && string.IsNullOrWhiteSpace(st)) content = xmlContent;
                }
                else if (lang == "LAD" || lang == "FBD")
                {
                    ShowRegionOverlay(codeViewerOverlay, txtCodeViewerOverlayMessage, $"Rendering {lang}...");
                    var snapshot = new TIAOpennessStudio.Services.GraphSnapshot { XmlContent = xmlContent };
                    _ladEditorWrapper = new LadEditorWrapper(snapshot, _viewModel.TiaService, nodeDto.Name, lang, 0);
                    TiaXmlAgentReferenceBuilder.TryBuildJsonIr(xmlContent, out var jsonIr, out _);
                    content = _viewModel.CreateMultiTabView(xmlContent, lang, _ladEditorWrapper.Render(), null, jsonIr, bc.IsSafety);
                }
                else if (lang == "GRAPH")
                {
                    ShowRegionOverlay(codeViewerOverlay, txtCodeViewerOverlayMessage, "Rendering GRAPH...");
                    TiaXmlAgentReferenceBuilder.TryBuildJsonIr(xmlContent, out var graphJsonIr, out _);
                    content = _viewModel.CreateMultiTabView(xmlContent, "GRAPH", GraphRenderer.Render(xmlContent), null, graphJsonIr);
                }
                else
                {
                    ShowRegionOverlay(codeViewerOverlay, txtCodeViewerOverlayMessage, "Parsing source...");
                    content = XmlCodeParser.Parse(xmlContent);
                    if (content is string s2 && string.IsNullOrWhiteSpace(s2)) content = xmlContent;
                }

                if (content != null)
                {
                    _viewModel.ShowCodeViewer(title, typeStr, content);
                    _viewModel.StatusText = $"Status: Ready - {title}";
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Failed to open block/type (DTO)", ex);
                MessageBox.Show("Error reading block content: " + (ex.Message ?? ""));
                _viewModel.StatusText = "Status: Open failed";
            }
            finally
            {
                HideRegionOverlay(codeViewerOverlay);
                System.Windows.Input.Mouse.OverrideCursor = null;
            }
        }

        // TryGenerateSource has been moved to CodeViewerService

        // ShowCodeViewer has been moved to CodeViewerService

        // RenderContentToGrid has been moved to CodeViewerService

        // CreateCodeEditor has been moved to CodeViewerService

        // IsSclContent has been moved to CodeViewerService

        // UpdateFoldings has been moved to CodeViewerService

        private double _savedSidebarWidth = 250;
        private bool _sidebarCollapsed = false;

        private void Sidebar_Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (_sidebarCollapsed)
            {
                // Expand: restore saved width
                colSidebar.Width = new GridLength(_savedSidebarWidth);
                SidebarSplitter.Visibility = Visibility.Visible;
                _sidebarCollapsed = false;
                if (btnToggleSidebar != null) btnToggleSidebar.ToolTip = "Hide Sidebar";
            }
            else
            {
                // Collapse: save current width, then set to 0
                _savedSidebarWidth = colSidebar.Width.Value > 0 ? colSidebar.Width.Value : 250;
                colSidebar.Width = new GridLength(0);
                SidebarSplitter.Visibility = Visibility.Collapsed;
                _sidebarCollapsed = true;
                if (btnToggleSidebar != null) btnToggleSidebar.ToolTip = "Show Sidebar";
            }
        }

        private void ActivityBar_Checked(object sender, RoutedEventArgs e)
        {
            if (gridTiaPortal == null || gridFileExplorer == null) return;

            if (btnTabTiaPortal != null && btnTabTiaPortal.IsChecked == true)
            {
                gridTiaPortal.Visibility = Visibility.Visible;
                gridFileExplorer.Visibility = Visibility.Collapsed;
                gridMcp.Visibility = Visibility.Collapsed;
                if (gridOpcUa != null) gridOpcUa.Visibility = Visibility.Collapsed;
                if (gridKnowledge != null) gridKnowledge.Visibility = Visibility.Collapsed;
            }
            else if (btnTabFileExplorer != null && btnTabFileExplorer.IsChecked == true)
            {
                gridTiaPortal.Visibility = Visibility.Collapsed;
                gridFileExplorer.Visibility = Visibility.Visible;
                gridMcp.Visibility = Visibility.Collapsed;
                if (gridOpcUa != null) gridOpcUa.Visibility = Visibility.Collapsed;
                if (gridKnowledge != null) gridKnowledge.Visibility = Visibility.Collapsed;
                LoadFileExplorer();
            }
            else if (btnTabMcp != null && btnTabMcp.IsChecked == true)
            {
                gridTiaPortal.Visibility = Visibility.Collapsed;
                gridFileExplorer.Visibility = Visibility.Collapsed;
                gridMcp.Visibility = Visibility.Visible;
                if (gridOpcUa != null) gridOpcUa.Visibility = Visibility.Collapsed;
                if (gridKnowledge != null) gridKnowledge.Visibility = Visibility.Collapsed;
            }
            else if (btnTabOpcUa != null && btnTabOpcUa.IsChecked == true)
            {
                gridTiaPortal.Visibility = Visibility.Collapsed;
                gridFileExplorer.Visibility = Visibility.Collapsed;
                gridMcp.Visibility = Visibility.Collapsed;
                gridOpcUa.Visibility = Visibility.Visible;
                if (gridKnowledge != null) gridKnowledge.Visibility = Visibility.Collapsed;
            }
            else if (btnTabKnowledge != null && btnTabKnowledge.IsChecked == true)
            {
                gridTiaPortal.Visibility = Visibility.Collapsed;
                gridFileExplorer.Visibility = Visibility.Collapsed;
                gridMcp.Visibility = Visibility.Collapsed;
                if (gridOpcUa != null) gridOpcUa.Visibility = Visibility.Collapsed;
                if (gridKnowledge != null) gridKnowledge.Visibility = Visibility.Visible;
            }

            // If sidebar is collapsed, expand it when clicking a tab
            if (_sidebarCollapsed && sender is RadioButton btn && btn.IsChecked == true)
            {
                Sidebar_Toggle_Click(null, null);
            }
        }

        private void ShowOverlay(string message)
        {
            if (txtOverlayMessage != null) txtOverlayMessage.Text = message;
            if (OverlayGrid != null)
            {
                OverlayGrid.Visibility = Visibility.Visible;
                AllowUIToUpdate();
            }
        }

        private void HideOverlay()
        {
            if (OverlayGrid != null) OverlayGrid.Visibility = Visibility.Collapsed;
        }

        // ComputeContentVersion has been moved to BlockVersionService

        // UpdateBlockVersion has been moved to BlockVersionService

        // GetProjectCacheKey has been moved to BlockVersionService

        // BuildIndexedContext has been moved to BlockVersionService

        // TryGetGitSnapshot has been moved to BlockVersionService

        // FindGitWorkingDirectory has been moved to BlockVersionService

        // RunGit has been moved to BlockVersionService

        // ApplyContextBudget has been moved to BlockVersionService

        private void AddReferenceToAi(string source, string content, string displayName = null)
        {
            _chatViewModel.AddReference(source, content, displayName);
            RefreshReferenceChips();
        }

        // ── Pending reference placeholders (view-only) ─────────────────────
        // Exporting a block for the AI context takes a moment (STA export + IR conversion).
        // A pending chip appears the instant the user asks and is replaced by the real chip when
        // done. It is intentionally NOT part of ChatViewModel.AiReferences, so a placeholder can
        // never leak into the LLM context if the user sends while an export is still running.
        private sealed class PendingAiReference
        {
            public string Id;
            public string DisplayName;
        }
        private readonly List<PendingAiReference> _pendingAiRefs = new List<PendingAiReference>();

        private string AddPendingAiReference(string displayName)
        {
            var id = Guid.NewGuid().ToString("N");
            _pendingAiRefs.Add(new PendingAiReference { Id = id, DisplayName = displayName });
            RefreshReferenceChips();
            return id;
        }

        private void RemovePendingAiReference(string id)
        {
            _pendingAiRefs.RemoveAll(p => p.Id == id);
            RefreshReferenceChips();
        }

        /// <summary>
        /// Runs an async reference builder with immediate feedback: pending placeholder chip +
        /// wait cursor on click; the real chip replaces the placeholder when the content is ready.
        /// </summary>
        private async Task AddReferenceToAiWithFeedbackAsync(string source, string displayName, Func<Task<string>> buildContent)
        {
            string pendingId = AddPendingAiReference(displayName);
            var oldCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.AppStarting;   // arrow + hourglass: busy, but input still works
            try
            {
                string content = await buildContent().ConfigureAwait(true);
                _chatViewModel.AddReference(source, content, displayName);
            }
            finally
            {
                Mouse.OverrideCursor = oldCursor;
                // One re-render swaps placeholder -> real chip with no flicker.
                RemovePendingAiReference(pendingId);
            }
        }

        /// <summary>
        /// Recursively search the TreeView for a block node matching the name.
        /// </summary>
        private object FindBlockInTree(string blockName)
        {
            if (string.IsNullOrWhiteSpace(blockName)) return null;
            return FindBlockInTreeItems(treeBlocks.Items, blockName);
        }

        private static object FindBlockInTreeItems(System.Collections.IList items, string blockName)
        {
            if (items == null) return null;
            foreach (var item in items)
            {
                if (item is System.Windows.Controls.TreeViewItem tvi)
                {
                    // Tags now carry PlcTreeNodeDto; match block/type nodes by name.
                    if (tvi.Tag is PlcTreeNodeDto dto &&
                        (dto.Kind == PlcTreeNodeKind.Block || dto.Kind == PlcTreeNodeKind.Type) &&
                        dto.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase))
                        return tvi.Tag;
                    // Legacy COM fallback for any not-yet-migrated nodes.
                    if (tvi.Tag is PlcBlock b && b.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase))
                        return tvi.Tag;
                    if (tvi.Tag is PlcStruct s && s.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase))
                        return tvi.Tag;
                    if (tvi.Tag is PlcType t && t.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase))
                        return tvi.Tag;
                    var found = FindBlockInTreeItems(tvi.Items, blockName);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private void RefreshReferenceChips()
        {
            var wrap = ChatPanel.ReferencesPanel;
            var border = ChatPanel.ReferencesBorder;
            if (wrap == null || border == null) return;
            wrap.Children.Clear();
            var references = _chatViewModel.AiReferences;
            border.Visibility = (references.Count > 0 || _pendingAiRefs.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in references)
            {
                var chip = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(8, 4, 4, 4),
                    MaxWidth = 360
                };

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var icon = new TextBlock
                {
                    Text = "🗎",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CDCFE")),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var prefix = new TextBlock
                {
                    Text = GetReferencePrefix(item.Source),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A6E22E")),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11
                };
                var tb = new TextBlock
                {
                    Text = item.DisplayName,
                    Foreground = Brushes.White,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 240
                };
                var close = new Button
                {
                    Content = "✕",
                    Width = 16,
                    Height = 16,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C5C5C5")),
                    Tag = item.Id
                };
                close.Click += RemoveReferenceChip_Click;
                row.Children.Add(icon);
                row.Children.Add(prefix);
                row.Children.Add(tb);
                row.Children.Add(close);
                chip.Child = row;
                wrap.Children.Add(chip);
            }

            // Pending placeholders: hourglass with a gentle pulse, italic dimmed name, no remove
            // button (nothing to remove yet — the real reference does not exist).
            foreach (var pending in _pendingAiRefs)
            {
                var chip = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(8, 4, 8, 4),
                    MaxWidth = 360,
                    Opacity = 0.85
                };

                var hourglass = new TextBlock
                {
                    Text = "⏳",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCDCAA")),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                // Breathing pulse on the hourglass: the cheapest possible "still working" signal.
                hourglass.BeginAnimation(OpacityProperty, new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.25,
                    Duration = new Duration(TimeSpan.FromMilliseconds(700)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(hourglass);
                row.Children.Add(new TextBlock
                {
                    Text = pending.DisplayName,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C5C5C5")),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 240
                });
                chip.Child = row;
                wrap.Children.Add(chip);
            }
        }

        private string GetReferencePrefix(string source)
        {
            if (string.Equals(source, "FileDrop", StringComparison.OrdinalIgnoreCase)) return "[File]";
            if (string.Equals(source, "CodeSelection", StringComparison.OrdinalIgnoreCase)) return "[Sel]";
            if (string.Equals(source, "CodeViewer", StringComparison.OrdinalIgnoreCase)) return "[Code]";
            if (string.Equals(source, "TreeContextMenu", StringComparison.OrdinalIgnoreCase)) return "[Tree]";
            if (string.Equals(source, "TreeDrag", StringComparison.OrdinalIgnoreCase)) return "[Drag]";
            return "[Ref]";
        }

        private void RemoveReferenceChip_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || btn.Tag == null) return;
            string id = btn.Tag.ToString();
            _chatViewModel.RemoveReference(id);
            RefreshReferenceChips();
        }



        // BuildAiReferenceFromTreeTag(object) removed: COM-tag based reference builder. The DTO
        // path uses BuildAiReferenceFromDtoAsync (context menu) and a lightweight DTO string (drag).

        private void treeBlocks_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _treeDragStartPoint = e.GetPosition(null);
        }

        private void treeBlocks_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

            var current = e.GetPosition(null);
            if (Math.Abs(current.X - _treeDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _treeDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            if (treeBlocks.SelectedItem is TreeViewItem tvi && tvi.Tag is PlcTreeNodeDto dto)
            {
                // Drag must start synchronously on the mouse gesture, so use a lightweight
                // reference built from the DTO (no COM, no async export). The full source is
                // added via the context-menu "Add To AI Context" path when needed.
                string content = $"{dto.Kind}: {dto.Name}";
                var data = new DataObject(DataFormats.Text, content);
                DragDrop.DoDragDrop(treeBlocks, data, DragDropEffects.Copy);
            }
        }

        private void AllowUIToUpdate()
        {
            System.Windows.Threading.DispatcherFrame frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new System.Windows.Threading.DispatcherOperationCallback(delegate (object parameter)
            {
                ((System.Windows.Threading.DispatcherFrame)parameter).Continue = false;
                return null;
            }), frame);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }


        /// <summary>
        /// Show the lblNoCode placeholder hint when all tabs are hidden
        /// </summary>
        private void UpdateNoCodeVisibility()
        {
            if (lblNoCode == null || tabsCode == null) return;

            bool anyVisible = false;
            foreach (TabItem item in tabsCode.Items)
            {
                if (item.Visibility == Visibility.Visible)
                {
                    anyVisible = true;
                    break;
                }
            }

            if (!anyVisible)
            {
                tabsCode.Visibility = Visibility.Collapsed;
                lblNoCode.Visibility = Visibility.Visible;
            }
        }

        private void CloseOutput_Click(object sender, RoutedEventArgs e)
        {
            var tab = FindName("tabOutput") as TabItem;
            if (tab != null)
            {
                tab.Visibility = Visibility.Collapsed;
                UpdateNoCodeVisibility();
            }
        }

        private void CloseSettings_Click(object sender, RoutedEventArgs e)
        {
            if (tabSettings != null) tabSettings.Visibility = Visibility.Collapsed;
            UpdateNoCodeVisibility();
        }

        private void ClosePreferences_Click(object sender, RoutedEventArgs e)
        {
            if (tabPreferences != null) tabPreferences.Visibility = Visibility.Collapsed;
            UpdateNoCodeVisibility();
        }

        private void CloseDiff_Click(object sender, RoutedEventArgs e)
        {
            if (tabDiff != null) tabDiff.Visibility = Visibility.Collapsed;
            UpdateNoCodeVisibility();
        }

        /// <summary>
        /// Show code differences in the Code Viewer's Diff tab.
        /// Uses DiffPreviewControl (DiffPlex engine + split/unified view).
        /// </summary>
        public void ShowDiffTab(string oldCode, string newCode, string title = "Code Diff")
        {
            if (tabDiff == null || diffPreviewControl == null) return;

            diffPreviewControl.SetDiff(oldCode, newCode);
            diffPreviewControl.TitleText = title;

            tabDiff.Visibility = Visibility.Visible;
            tabsCode.Visibility = Visibility.Visible;
            tabsCode.SelectedItem = tabDiff;
            if (lblNoCode != null) lblNoCode.Visibility = Visibility.Collapsed;
        }

        private void ShowOutput_Click(object sender, RoutedEventArgs e)
        {
            var tab = FindName("tabOutput") as TabItem;
            if (tab != null)
            {
                tab.Visibility = Visibility.Visible;
                if (tabsCode != null)
                {
                    tabsCode.Visibility = Visibility.Visible;
                    tabsCode.SelectedItem = tab;
                }
                if (lblNoCode != null) lblNoCode.Visibility = Visibility.Collapsed;
            }
        }

        #region LAD edit toolbar events

        private void LadEditToolbar_EditModeChanged(object sender, bool isEditMode)
        {
            if (isEditMode)
            {
                _viewModel.StatusText = LanguageManager.Instance.GetString("Status.EditMode");
            }
            else
            {
                _viewModel.StatusText = LanguageManager.Instance.GetString("Status.ViewMode");
            }
        }

        private void LadEditToolbar_EditStatusChanged(object sender, string status)
        {
            _viewModel.StatusText = $"Status: {status}";
        }

        /// <summary>
        /// Initialize the LAD editor placeholder view
        /// Shows an empty LAD editor at startup, prompting the user to select a LAD block
        /// </summary>
        private void InitializeLadEditorPlaceholder()
        {
            // Show the localized prompt in the empty code viewer area.
            tabsCode.Visibility = System.Windows.Visibility.Collapsed;
            lblNoCode.Visibility = System.Windows.Visibility.Visible;
            lblNoCode.Text = LanguageManager.Instance.GetString("Lad.SelectBlock");
        }

        #endregion

        private void OnLanguageChanged(AppLanguage lang)
        {
            var lm = LanguageManager.Instance;

            // Update Menu Items
            if (mnuProject != null) mnuProject.Header = lm.GetString("Project");
            if (mnuOpen != null) mnuOpen.Header = lm.GetString("Open");
            if (mnuCompile != null) mnuCompile.Header = lm.GetString("Compile"); // Need to add key
            if (mnuSave != null) mnuSave.Header = lm.GetString("Save");
            if (mnuReload != null) mnuReload.Header = lm.GetString("Reload");
            if (mnuCacheManager != null) mnuCacheManager.Header = lm.GetString("KnowledgeCacheManager");
            if (mnuClose != null) mnuClose.Header = lm.GetString("Close");
            if (mnuExit != null) mnuExit.Header = lm.GetString("Exit");

            if (mnuImport != null) mnuImport.Header = lm.GetString("Import");
            if (mnuImportBlock != null) mnuImportBlock.Header = lm.GetString("PLC Blocks"); // Need to add key
            if (mnuImportType != null) mnuImportType.Header = lm.GetString("PLC Data types"); // Need to add key
            if (mnuImportTexts != null) mnuImportTexts.Header = lm.GetString("Project Texts"); // Need to add key

            if (mnuExport != null) mnuExport.Header = lm.GetString("Export");
            if (mnuExportSelected != null) mnuExportSelected.Header = lm.GetString("Export Selected"); // Need to add key
            if (mnuExportXml != null) mnuExportXml.Header = lm.GetString("Export XML");
            if (mnuExportTexts != null) mnuExportTexts.Header = lm.GetString("Project Texts");

            if (mnuSettings != null) mnuSettings.Header = lm.GetString("Settings"); // Need to add key
            if (mnuTargetPath != null) mnuTargetPath.Header = lm.GetString("Target Path"); // Need to add key
            if (mnuAiConfig != null) mnuAiConfig.Header = lm.GetString("Model Config"); // Need to add key
            if (mnuStartBackend != null) mnuStartBackend.Header = lm.GetString("Start Python Backend");
            if (mnuLibraryBackupCompare != null) mnuLibraryBackupCompare.Header = lm.GetString("Library Backup && Compare");
            if (mnuPreferences != null) mnuPreferences.Header = lm.GetString("Preferences");
            if (mnuLanguage != null) mnuLanguage.Header = lm.GetString("Language");

            if (mnuView != null) mnuView.Header = "_" + lm.GetString("View");
            if (mnuShowOutput != null) mnuShowOutput.Header = lm.GetString("Output Log");
            if (mnuTheme != null) mnuTheme.Header = "_" + lm.GetString("Theme");
            if (mnuThemeDark != null) mnuThemeDark.Header = lm.GetString("Dark");
            if (mnuThemeLight != null) mnuThemeLight.Header = lm.GetString("Light");
            // mnuGraph / mnuGenStepSeq removed with the LAD toolbar rework (TODO v2.0).

            if (mnuHelp != null) mnuHelp.Header = "_" + lm.GetString("Help"); // Need to add key
            if (mnuAbout != null) mnuAbout.Header = "_" + lm.GetString("About"); // Need to add key

            // Update UI texts
            if (lblDevice != null) lblDevice.Text = lm.GetString("Device");
            if (lblPlc != null) lblPlc.Text = lm.GetString("PLC");
            if (lblBlocks != null) lblBlocks.Text = lm.GetString("Blocks");
            if (lblExplorer != null) lblExplorer.Text = lm.GetString("Explorer");
            if (btnTabTiaPortal != null) btnTabTiaPortal.ToolTip = lm.GetString("TIA Portal");
            if (btnTabFileExplorer != null) btnTabFileExplorer.ToolTip = lm.GetString("Explorer");
            if (txtFileSearch != null) txtFileSearch.Tag = lm.GetString("SearchFiles");
            if (lblNoCode != null) lblNoCode.Text = lm.GetString("SelectCodePlaceholder");

            if (ChatPanel != null)
            {
                ChatPanel.TitleLabel.Text = lm.GetString("Code Generator");
                ChatPanel.NewChatButton.ToolTip = lm.GetString("New Chat");
                ChatPanel.ChatHistoryButton.ToolTip = lm.GetString("Chat History");
            }
            if (lblCodeViewer != null) lblCodeViewer.Text = lm.GetString("Code Viewer");

            // File Explorer Context Menu
            if (mnuFileExplorerOpen != null) mnuFileExplorerOpen.Header = lm.GetString("MenuOpen");
            if (mnuFileExplorerSave != null) mnuFileExplorerSave.Header = lm.GetString("Save");
            if (mnuFileExplorerImport != null) mnuFileExplorerImport.Header = lm.GetString("MenuImportToTia");
            if (mnuFileExplorerNewFile != null) mnuFileExplorerNewFile.Header = lm.GetString("MenuNewSclFile");
            if (mnuFileExplorerNewFolder != null) mnuFileExplorerNewFolder.Header = lm.GetString("MenuNewFolder");
            if (mnuFileExplorerRename != null) mnuFileExplorerRename.Header = lm.GetString("MenuRename");
            if (mnuFileExplorerDelete != null) mnuFileExplorerDelete.Header = lm.GetString("MenuDelete");
            if (mnuFileExplorerRefresh != null) mnuFileExplorerRefresh.Header = lm.GetString("MenuRefresh");

            // Status bar AI write-target chips (labels themselves are set by RefreshStatusBarTargetChips)
            if (StatusBlockTargetChip != null) StatusBlockTargetChip.ToolTip = lm.GetString("Status.Target.Blocks");
            if (StatusTypeTargetChip != null) StatusTypeTargetChip.ToolTip = lm.GetString("Status.Target.Types");
            if (StatusBlockTargetClearBtn != null) StatusBlockTargetClearBtn.ToolTip = lm.GetString("Status.Target.Clear");
            if (StatusTypeTargetClearBtn != null) StatusTypeTargetClearBtn.ToolTip = lm.GetString("Status.Target.Clear");

            // Other controls are now in Menu or removed, so their text update logic is handled by Menu or not needed
        }

        private void UpdateMenuEnableStates()
        {
            bool hasProject = project != null;
            bool hasSoftware = software != null;
            object selectedTag = (treeBlocks?.SelectedItem as TreeViewItem)?.Tag;

            // Tags now carry PlcTreeNodeDto; decide menu state from Kind (no COM access).
            var selectedDto = selectedTag as PlcTreeNodeDto;
            bool canExportSelected = selectedDto != null &&
                (selectedDto.Kind == PlcTreeNodeKind.Block || selectedDto.Kind == PlcTreeNodeKind.Type);
            bool canImportBlock = selectedDto != null && selectedDto.Kind == PlcTreeNodeKind.BlockGroup;

            if (mnuCompile != null) mnuCompile.IsEnabled = hasSoftware;
            if (mnuSave != null) mnuSave.IsEnabled = hasProject;
            if (mnuReload != null) mnuReload.IsEnabled = hasProject;
            if (mnuClose != null) mnuClose.IsEnabled = hasProject;

            if (mnuImportBlock != null) mnuImportBlock.IsEnabled = hasSoftware && canImportBlock;
            if (mnuImportType != null) mnuImportType.IsEnabled = hasSoftware;
            if (mnuImportTexts != null) mnuImportTexts.IsEnabled = hasProject;

            if (mnuExportSelected != null) mnuExportSelected.IsEnabled = hasSoftware && canExportSelected;
            if (mnuExportXml != null) mnuExportXml.IsEnabled = hasSoftware && canExportSelected;
            if (mnuExportTexts != null) mnuExportTexts.IsEnabled = hasProject;

            if (mnuTargetPath != null) mnuTargetPath.IsEnabled = hasSoftware;
        }

        private void treeBlocks_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            UpdateMenuEnableStates();
        }

        // CreateMultiTabView has been moved to CodeViewerService

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("TIA Openness Studio\nBased on Siemens Openness API\n\nWPF desktop tooling with AI assistance.", "About", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenCacheManager_Click(object sender, RoutedEventArgs e)
        {
            if (tabCacheManager != null)
            {
                tabCacheManager.Visibility = Visibility.Visible;
                tabsCode.Visibility = Visibility.Visible;
                tabsCode.SelectedItem = tabCacheManager;
                lblNoCode.Visibility = Visibility.Collapsed;
            }
        }

        private void CloseCacheManager_Click(object sender, RoutedEventArgs e)
        {
            if (tabCacheManager != null) tabCacheManager.Visibility = Visibility.Collapsed;
            UpdateNoCodeVisibility();
        }

        private void OpenDependencyAnalysis(string blockName = null)
        {
            if (tabDependencyAnalysis == null) return;

            tabDependencyAnalysis.Visibility = Visibility.Visible;
            tabsCode.Visibility = Visibility.Visible;
            tabsCode.SelectedItem = tabDependencyAnalysis;
            lblNoCode.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(blockName) &&
                tabDependencyAnalysis.Content is DependencyAnalysisControl control &&
                control.DataContext is DependencyAnalysisViewModel vm)
            {
                _ = vm.SetBlockNameAsync(blockName);
            }
        }

        private void CloseDependencyAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (tabDependencyAnalysis != null) tabDependencyAnalysis.Visibility = Visibility.Collapsed;
            UpdateNoCodeVisibility();
        }

        private void CodeBlock_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (!e.Handled)
            {
                // Scroll the inner TextBox? Not really, we want to scroll the main list.
                // Wait, if it's horizontal, inner scroll is fine. If vertical, we want to bubble.
                // Or simply let WPF bubble it by marking it handled and firing on parent.
                e.Handled = true;
                var eventArg = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = System.Windows.UIElement.MouseWheelEvent,
                    Source = sender
                };
                var parent = ((System.Windows.FrameworkElement)sender).Parent as System.Windows.UIElement;
                if (parent != null)
                {
                    parent.RaiseEvent(eventArg);
                }
            }
        }

        #region Window Event Handlers
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                Maximize_Click(sender, e);
            }
            else
            {
                this.DragMove();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }
        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            if (_isMaximized)
            {
                // Restore window - restore the previously saved size
                this.Left = _normalLeft;
                this.Top = _normalTop;
                this.Width = _normalWidth;
                this.Height = _normalHeight;
                this.WindowState = WindowState.Normal;
                _isMaximized = false;
                MainBorder.Margin = new Thickness(0);
            }
            else
            {
                // Save the current window position and size
                _normalLeft = this.Left;
                _normalTop = this.Top;
                _normalWidth = this.Width;
                _normalHeight = this.Height;

                // Maximize window - use the work area size instead of WindowState.Maximized
                var workArea = SystemParameters.WorkArea;
                this.Left = workArea.Left;
                this.Top = workArea.Top;
                this.Width = workArea.Width;
                this.Height = workArea.Height;
                _isMaximized = true;
                MainBorder.Margin = new Thickness(0);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            CloseProject_Click(sender, e);
            Application.Current.Shutdown();
        }
        #endregion

        #region Project Menu Handlers
        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close_Click(sender, e);
        }
        #endregion
        #region WPF TreeView Helpers
        #endregion

        private void tabsCode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is TabControl tabControl && tabControl == tabsCode)
            {
                bool showLadToolbar = false;

                if (tabsCode.SelectedItem is TabItem selectedTab && selectedTab.Content is Grid contentGrid)
                {
                    if (contentGrid.Children.Count > 0 && contentGrid.Children[0] is TabControl innerTabControl)
                    {
                        if (innerTabControl.Items.Count > 0 && innerTabControl.Items[0] is TabItem firstTab)
                        {
                            string header = firstTab.Header?.ToString();
                            if (header == "LAD" || header == "FBD")
                            {
                                showLadToolbar = true;
                            }
                        }
                    }
                }

                // TODO v2.0: re-enable the LAD edit toolbar (ladEditToolbar removed in current rework)
                _ = showLadToolbar;
            }
        }

        private void ShowCodeViewerInTab(string name, string type, object content)
        {
            AppLogger.Log($"Showing Code Viewer for: {name} (Type: {type})");
            // Check if tab exists
            foreach (TabItem item in tabsCode.Items)
            {
                if (item.Tag?.ToString() == name)
                {
                    // Refresh content
                    Grid existingGrid = item.Content as Grid;
                    if (existingGrid != null)
                    {
                        existingGrid.Children.Clear();
                        if (content is UIElement element)
                        {
                            existingGrid.Children.Add(element);
                        }
                        else
                        {
                            _viewModel.CodeViewerService.RenderContentToGrid(existingGrid, content);
                        }
                    }
                    tabsCode.Visibility = Visibility.Visible;
                    tabsCode.SelectedItem = item;
                    if (lblNoCode != null) lblNoCode.Visibility = Visibility.Collapsed;
                    return;
                }
            }

            // Create new Tab
            TabItem newTab = new TabItem();
            newTab.Tag = name;
            newTab.Style = (Style)FindResource("CodeTabItemStyle");

            // Header with Close Button
            StackPanel headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var headerText = new TextBlock { Text = name, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            headerText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            headerPanel.Children.Add(headerText);

            Button closeBtn = new Button
            {
                Content = "x",
                Padding = new Thickness(0),
                Width = 15,
                Height = 15,
                FontSize = 10,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(5,0,0,0),
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.SetResourceReference(Button.ForegroundProperty, "InputHintBrush");

            closeBtn.Click += (s, e) =>
            {
                tabsCode.Items.Remove(newTab);
                if (tabsCode.Items.Count == 0 || (tabsCode.Items.Count == 1 && tabsCode.Items[0] == tabOutput && tabOutput.Visibility != Visibility.Visible))
                {
                    tabsCode.Visibility = Visibility.Collapsed;
                    if (lblNoCode != null) lblNoCode.Visibility = Visibility.Visible;
                }
            };

            headerPanel.Children.Add(closeBtn);
            newTab.Header = headerPanel;

            // Context menu
            newTab.ContextMenu = BuildTabContextMenu(newTab);

            // Content Grid
            Grid contentGrid = new Grid();
            if (content is UIElement contentElement)
            {
                contentGrid.Children.Add(contentElement);
            }
            else
            {
                _viewModel.CodeViewerService.RenderContentToGrid(contentGrid, content);
            }
            newTab.Content = contentGrid;

            tabsCode.Items.Add(newTab);
            tabsCode.Visibility = Visibility.Visible;
            tabsCode.SelectedItem = newTab;
            if (lblNoCode != null) lblNoCode.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Build the right-click context menu for a Code Viewer tab (three languages)
        /// </summary>
        private ContextMenu BuildTabContextMenu(TabItem targetTab)
        {
            var lang = Services.LanguageManager.Instance;
            var menu = new ContextMenu();

            // Close
            var closeItem = new MenuItem { Header = lang.GetString("Tab.Close") };
            closeItem.Click += (s, e) =>
            {
                tabsCode.Items.Remove(targetTab);
                CheckTabsEmpty();
            };
            menu.Items.Add(closeItem);

            // Close others
            var closeOthersItem = new MenuItem { Header = lang.GetString("Tab.CloseOthers") };
            closeOthersItem.Click += (s, e) =>
            {
                var toRemove = new System.Collections.Generic.List<TabItem>();
                foreach (TabItem item in tabsCode.Items)
                {
                    if (item != targetTab) toRemove.Add(item);
                }
                foreach (var item in toRemove) tabsCode.Items.Remove(item);
                tabsCode.SelectedItem = targetTab;
                CheckTabsEmpty();
            };
            menu.Items.Add(closeOthersItem);

            // Close to the right
            var closeRightItem = new MenuItem { Header = lang.GetString("Tab.CloseRight") };
            closeRightItem.Click += (s, e) =>
            {
                int idx = tabsCode.Items.IndexOf(targetTab);
                var toRemove = new System.Collections.Generic.List<TabItem>();
                for (int i = idx + 1; i < tabsCode.Items.Count; i++)
                {
                    if (tabsCode.Items[i] is TabItem ti) toRemove.Add(ti);
                }
                foreach (var item in toRemove) tabsCode.Items.Remove(item);
                CheckTabsEmpty();
            };
            menu.Items.Add(closeRightItem);

            // Close all
            var closeAllItem = new MenuItem { Header = lang.GetString("Tab.CloseAll") };
            closeAllItem.Click += (s, e) =>
            {
                tabsCode.Items.Clear();
                CheckTabsEmpty();
            };
            menu.Items.Add(closeAllItem);

            menu.Items.Add(new Separator());

            // Copy name
            var copyNameItem = new MenuItem { Header = lang.GetString("Tab.CopyName") };
            copyNameItem.Click += (s, e) =>
            {
                var name = targetTab.Tag?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(name))
                    Clipboard.SetText(name);
            };
            menu.Items.Add(copyNameItem);

            return menu;
        }

        private void CheckTabsEmpty()
        {
            if (tabsCode.Items.Count == 0)
            {
                tabsCode.Visibility = Visibility.Collapsed;
                if (lblNoCode != null) lblNoCode.Visibility = Visibility.Visible;
            }
        }

        // ---- Tag Table Display ----

        

        /// <summary>
        /// Open a tag table from its tree DTO. Tag content is extracted on the STA host and
        /// streamed to the UI in batches (progressive disclosure); no COM is touched here.
        /// </summary>
        private async Task OpenTagTableFromDtoAsync(PlcTreeNodeDto nodeDto)
        {
            string tableName = nodeDto.Name ?? "Tag Table";
            bool isHmi = nodeDto.Kind == PlcTreeNodeKind.HmiTagTable;
            string tabKey = (isHmi ? "HmiTagTable:" : "TagTable:") + tableName;

            // Check if tab already exists
            foreach (TabItem existingTab in tabsCode.Items)
            {
                if (existingTab.Tag?.ToString() == tabKey)
                {
                    tabsCode.SelectedItem = existingTab;
                    tabsCode.Visibility = Visibility.Visible;
                    if (lblNoCode != null) lblNoCode.Visibility = Visibility.Collapsed;
                    return;
                }
            }

            // Create new tab with TagTableViewControl
            var control = new Controls.TagTableViewControl();
            control.BeginLoad(tableName, hmiMode: isHmi);

            var closeBtn = new System.Windows.Controls.Button
            {
                Content = "×",
                FontSize = 10,
                Padding = new Thickness(2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource("InputHintBrush")
            };

            var header = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "📋 " + tableName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
            header.Children.Add(closeBtn);

            var newTab = new TabItem
            {
                Header = header,
                Content = control,
                Tag = tabKey
            };

            closeBtn.Click += (s, e) =>
            {
                tabsCode.Items.Remove(newTab);
                CheckTabsEmpty();
            };

            tabsCode.Items.Add(newTab);
            tabsCode.Visibility = Visibility.Visible;
            tabsCode.SelectedItem = newTab;
            if (lblNoCode != null) lblNoCode.Visibility = Visibility.Collapsed;

            // Progress posts batches back to the UI thread (captured here) as the STA thread extracts them.
            var progress = new Progress<IReadOnlyList<Services.Abstractions.TagEntryDto>>(
                batch => control.AppendBatch(batch));

            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                var dto = isHmi
                    ? await _viewModel.TiaService.GetHmiTagTableContentAsync(nodeDto.Handle, progress)
                    : await _viewModel.TiaService.GetTagTableContentAsync(nodeDto.Handle, progress);
                if (!string.IsNullOrEmpty(dto.Error))
                {
                    control.ShowError(dto.Error);
                    return;
                }
                control.EndLoad();
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
            }
        }

        

        private void ChatPanel_Loaded()
        {

        }
    }
}
