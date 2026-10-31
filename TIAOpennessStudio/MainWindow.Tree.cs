using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Prism.Events;
using TIAOpennessStudio.ViewModels;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Events;

namespace TIAOpennessStudio
{
    // Tree partial: device discovery, PLC tree rendering, and tree context actions.
    public partial class MainWindow
    {
        private TreeViewModel _treeViewModel;
        
        private void InitTreeModule()
        {
            _treeViewModel = _viewModel.TreeViewModel;
            
            // Bind TreeViewModel properties to the UI
            listDevices.ItemsSource = _treeViewModel.Devices;
            listPlcs.ItemsSource = _treeViewModel.PlcList;
            listHmis.ItemsSource = _treeViewModel.HmiList;
            
            AppLogger.Log("[InitTreeModule] Tree module initialized, event subscriptions set up");
            AppLogger.Log($"[InitTreeModule] listPlcs.ItemsSource bound to PlcList (count: {_treeViewModel.PlcList.Count})");
            
            // Subscribe TreeViewModel-specific Prism events (ShowMessageEvent/UpdateProjectStatusEvent/RefreshBlockCountsEvent
            // are already subscribed in InitProjectModule and MainViewModel, so they are not duplicated here)
            _eventAggregator.GetEvent<UpdateMenuEnableStatesEvent>().Subscribe(() => UpdateMenuEnableStates(), ThreadOption.UIThread);
            _eventAggregator.GetEvent<LoadTreeEvent>().Subscribe(() =>
            {
                AppLogger.Log("[LoadTreeEvent] Event received (DTO path), rebuilding tree for current PLC");
                LoadWpfTreeViewForCurrentPlc();
            }, ThreadOption.UIThread);
            
            // Add extra logging for the SelectionChanged event
            listPlcs.SelectionChanged += (s, e) =>
            {
                AppLogger.Log($"[listPlcs.SelectionChanged] Triggered! SelectedIndex={listPlcs.SelectedIndex}, SelectedItem={listPlcs.SelectedItem?.GetType().Name ?? "null"}");
            };

            // Selecting a group in the Blocks tree → auto-sync to the AgentInputBox target chip.
            // Selecting a block itself does not change the chip, because the user may just want to view code, not change the default target.
            treeBlocks.SelectedItemChanged += TreeBlocks_SelectedItemChanged_SyncTargetChip;
        }

        /// <summary>
        /// Selecting a group in the PLC Blocks tree → sync to ChatViewModel;
        /// the AgentInputBox target chip updates automatically via two-way binding.
        /// Selecting a PlcBlockGroup → changes the Block chip; selecting a PlcTypeUserGroup → changes the Type chip.
        /// </summary>
        private void TreeBlocks_SelectedItemChanged_SyncTargetChip(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_chatViewModel == null) return;
            if (!(e.NewValue is TreeViewItem item) || !(item.Tag is PlcTreeNodeDto dto)) return;

            // GroupPath carried by the DTO is relative to BlockGroup/TypeGroup root ("" = root).
            if (dto.Kind == PlcTreeNodeKind.BlockGroup && !string.IsNullOrEmpty(dto.GroupPath))
            {
                _chatViewModel.TargetBlockGroupPath = dto.GroupPath;
            }
            else if (dto.Kind == PlcTreeNodeKind.TypeGroup && !string.IsNullOrEmpty(dto.GroupPath))
            {
                _chatViewModel.TargetTypeGroupPath = dto.GroupPath;
            }
        }

        private static string BuildBlockGroupPath(PlcBlockGroup group)
        {
            var parts = new List<string>();
            object cur = group;
            while (cur is PlcBlockGroup g && !(cur is PlcBlockSystemGroup))
            {
                parts.Insert(0, g.Name);
                cur = g.Parent;
            }
            return string.Join("\\", parts);
        }

        private static string BuildTypeGroupPath(PlcTypeUserGroup group)
        {
            var parts = new List<string>();
            object cur = group;
            while (cur is PlcTypeUserGroup g)
            {
                parts.Insert(0, g.Name);
                cur = g.Parent;
            }
            return string.Join("\\", parts);
        }

        private void IterateThroughDevices(Project project)
        {
            // Device/PLC enumeration now runs on the STA host (GetDeviceTreeAsync); the ViewModel
            // only stores names + opaque handles. The Project argument is ignored (kept for callers).
            _ = IterateThroughDevicesAsync();
        }

        private async System.Threading.Tasks.Task IterateThroughDevicesAsync()
        {
            ShowRegionOverlay(listDevicesOverlay, txtDevicesOverlayMessage, "Loading devices...");
            ShowRegionOverlay(listPlcsOverlay, txtPlcsOverlayMessage, "Loading PLCs...");
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            try
            {
                AppLogger.Log("[IterateThroughDevices] Starting device iteration (DTO path)...");
                await _treeViewModel.IterateDevicesAsync().ConfigureAwait(true);
                AppLogger.Log($"[IterateThroughDevices] Completed. Devices: {_treeViewModel.Devices.Count}, PLCs: {_treeViewModel.PlcList.Count}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[IterateThroughDevices] ERROR: {ex.Message}");
            }
            finally
            {
                HideRegionOverlay(listDevicesOverlay);
                HideRegionOverlay(listPlcsOverlay);
            }
        }

        /// <summary>Rebuild the blocks tree for the currently selected PLC (DTO path).</summary>
        private void LoadWpfTreeViewForCurrentPlc()
        {
            ShowRegionOverlay(treeBlocksOverlay, txtTreeBlocksOverlayMessage, "Loading blocks...");
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            _ = LoadWpfTreeViewDtoAsync(treeBlocks);
        }

        /// <summary>Explicitly re-select a PLC by handle on the STA host, then reload the tree + counts.</summary>
        private async System.Threading.Tasks.Task ReselectAndLoadAsync(string handle)
        {
            try
            {
                bool ok = await _viewModel.TiaService.SelectPlcByHandleAsync(handle).ConfigureAwait(true);
                if (!ok) return;
                LoadWpfTreeViewForCurrentPlc();
                _treeViewModel.UpdateBlockCounts();
                UpdateMenuEnableStates();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ReselectAndLoad] ERROR: {ex.Message}");
            }
        }

        private void UpdateProjectStatus()
        {
            _treeViewModel.UpdateProjectStatus(project);
        }

        private void UpdateBlockCounts()
        {
            _treeViewModel.UpdateBlockCounts();
            // Update UI controls
            cntOB.Text = _treeViewModel.ObCount.ToString();
            cntFB.Text = _treeViewModel.FbCount.ToString();
            cntFC.Text = _treeViewModel.FcCount.ToString();
            cntDB.Text = _treeViewModel.DbCount.ToString();
            cntType.Text = _treeViewModel.TypeCount.ToString();
        }

        // CountBlocks, CountTypes, FindCpuInDeviceItems are now in TreeViewModel

        /// <summary>
        /// Selecting an HMI software entry rebuilds the central tree with its screens /
        /// tag tables (DTO path). Does not touch the active PlcSoftware.
        /// </summary>
        private void Hmi_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (listHmis.SelectedItem is HmiItem hmi && !string.IsNullOrEmpty(hmi.Handle))
            {
                AppLogger.Log($"[Hmi_SelectionChanged] HmiItem selected: {hmi.Name} (kind={hmi.Kind}, handle={hmi.Handle})");
                _ = LoadWpfTreeViewForHmiAsync(hmi.Handle);
            }
        }

        private async System.Threading.Tasks.Task LoadWpfTreeViewForHmiAsync(string hmiHandle)
        {
            ShowRegionOverlay(treeBlocksOverlay, txtTreeBlocksOverlayMessage, "Loading HMI...");
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            try
            {
                var rootDto = await _viewModel.TiaService.GetHmiTreeAsync(hmiHandle).ConfigureAwait(true);
                if (rootDto == null)
                {
                    AppLogger.Log("[LoadWpfTreeViewForHmi] GetHmiTreeAsync returned null");
                    return;
                }
                treeBlocks.Items.Clear();
                treeBlocks.Items.Add(BuildTreeItemFromDto(rootDto, isExpanded: true));
                AppLogger.Log($"[LoadWpfTreeViewForHmi] HMI tree loaded, children: {rootDto.Children.Count}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[LoadWpfTreeViewForHmi] ERROR: {ex.Message}");
            }
            finally
            {
                HideRegionOverlay(treeBlocksOverlay);
            }
        }

        private void Plc_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                AppLogger.Log($"[Plc_SelectionChanged] Event triggered, SelectedItem type: {listPlcs.SelectedItem?.GetType().Name ?? "null"}");
                
                // PlcList is an ObservableCollection<PlcItem> carrying opaque handles (no COM).
                if (listPlcs.SelectedItem is PlcItem plcItem && !string.IsNullOrEmpty(plcItem.Handle))
                {
                    AppLogger.Log($"[Plc_SelectionChanged] PlcItem selected: {plcItem.Name} (handle={plcItem.Handle})");

                    // Setting SelectedPlc triggers OnSelectedPlcChanged → selects the PLC by handle
                    // on the STA host, then publishes LoadTreeEvent → the tree is rebuilt from DTOs.
                    var previousPlc = _treeViewModel.SelectedPlc;
                    _treeViewModel.SelectedPlc = plcItem;

                    // Boundary fallback: if the value is unchanged (SetProperty equality means
                    // OnSelectedPlcChanged won't fire), explicitly reload once by handle.
                    if (ReferenceEquals(previousPlc, plcItem))
                    {
                        AppLogger.Log($"[Plc_SelectionChanged] SelectedPlc unchanged, explicit single load");
                        _ = ReselectAndLoadAsync(plcItem.Handle);
                    }
                }
                else
                {
                    AppLogger.Log($"[Plc_SelectionChanged] No valid PLC selected");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Plc_SelectionChanged] ERROR: {ex.Message}");
                AppLogger.Log($"[Plc_SelectionChanged] Stack trace: {ex.StackTrace}");
                MessageBox.Show($"Error loading PLC blocks: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadWpfTreeView(System.Windows.Controls.TreeView tree, PlcSoftware sw)
        {
            if (sw == null)
            {
                AppLogger.Log("[LoadWpfTreeView] PlcSoftware is null, cannot load tree");
                return;
            }

            AppLogger.Log($"[LoadWpfTreeView] Starting to load tree (DTO path)");

            // Region overlay (covers only the blocks tree, does not block the Code Viewer/Chat on the right)
            ShowRegionOverlay(treeBlocksOverlay, txtTreeBlocksOverlayMessage, "Loading blocks...");
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            // Build the tree as plain-data DTOs on the STA host thread (inside OpennessService),
            // then render purely on the UI thread. The UI never touches a COM object.
            _ = LoadWpfTreeViewDtoAsync(tree);
        }

        private async System.Threading.Tasks.Task LoadWpfTreeViewDtoAsync(System.Windows.Controls.TreeView tree)
        {
            try
            {
                var rootDto = await _viewModel.TiaService.GetBlockTreeAsync().ConfigureAwait(true);
                if (rootDto == null)
                {
                    AppLogger.Log("[LoadWpfTreeView] GetBlockTreeAsync returned null (no PLC)");
                    return;
                }
                tree.Items.Clear();
                var rootItem = BuildTreeItemFromDto(rootDto, isExpanded: true);
                tree.Items.Add(rootItem);
                AppLogger.Log($"[LoadWpfTreeView] Tree loaded (DTO), root children: {rootItem.Items.Count}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[LoadWpfTreeView] ERROR: {ex.Message}");
                MessageBox.Show($"Failed to load PLC blocks tree:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                HideRegionOverlay(treeBlocksOverlay);
            }
        }

        /// <summary>
        /// Rebuild the blocks tree after a mutation (import / delete / add group) using the
        /// DTO path. The tree is rebuilt purely from service-side DTOs on the STA host, so no
        /// COM object is touched on the UI thread. Block counts are refreshed afterwards.
        /// </summary>
        private void RefreshBlocksTreeAfterMutation()
        {
            ShowRegionOverlay(treeBlocksOverlay, txtTreeBlocksOverlayMessage, "Refreshing blocks...");
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            _ = RefreshBlocksTreeAfterMutationAsync();
        }

        private async System.Threading.Tasks.Task RefreshBlocksTreeAfterMutationAsync()
        {
            try
            {
                await LoadWpfTreeViewDtoAsync(treeBlocks).ConfigureAwait(true);
                _treeViewModel.UpdateBlockCounts();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[RefreshBlocksTreeAfterMutation] ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Pure UI-thread rendering of a PlcTreeNodeDto subtree. Tag holds the DTO (which
        /// carries an opaque handle), never a COM object.
        /// </summary>
        private TreeViewItem BuildTreeItemFromDto(PlcTreeNodeDto dto, bool isExpanded = false)
        {
            var item = new TreeViewItem
            {
                Header = CreateTreeHeader(dto.DisplayText, dto.Icon),
                Tag = dto,
                IsExpanded = isExpanded,
                ContextMenu = GetContextMenuForDto(dto),
            };
            foreach (var child in dto.Children)
            {
                // Top two levels (root → group) stay expanded like the legacy tree.
                bool expand = (dto.Kind == PlcTreeNodeKind.Root
                        && (child.Kind == PlcTreeNodeKind.BlockGroup || child.Kind == PlcTreeNodeKind.TypeGroup))
                    || (dto.Kind == PlcTreeNodeKind.HmiRoot
                        && (child.Kind == PlcTreeNodeKind.HmiScreenGroup || child.Kind == PlcTreeNodeKind.HmiTagTableGroup));
                item.Items.Add(BuildTreeItemFromDto(child, expand));
            }
            return item;
        }

        // ---- Region overlay helpers (used by blocks tree, devices list, plcs list) ----
        private void ShowRegionOverlay(System.Windows.Controls.Border overlay, System.Windows.Controls.TextBlock messageText, string message)
        {
            if (messageText != null) messageText.Text = message;
            if (overlay != null) overlay.Visibility = Visibility.Visible;
        }

        private void HideRegionOverlay(System.Windows.Controls.Border overlay)
        {
            if (overlay != null) overlay.Visibility = Visibility.Collapsed;
        }

        // Legacy call name compatibility, forwarded to the generic helper
        private void ShowTreeBlocksOverlay(string message)
            => ShowRegionOverlay(treeBlocksOverlay, txtTreeBlocksOverlayMessage, message);

        private void HideTreeBlocksOverlay()
            => HideRegionOverlay(treeBlocksOverlay);

        private StackPanel CreateTreeHeader(string text, string iconName)
        {
            StackPanel stack = new StackPanel();
            stack.Orientation = Orientation.Horizontal;

            if (!string.IsNullOrEmpty(iconName))
            {
                System.Windows.Controls.Image img = new System.Windows.Controls.Image();
                img.Width = 16;
                img.Height = 16;
                img.Margin = new Thickness(0, 0, 5, 0);

                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ressources", iconName);
                    if (File.Exists(path))
                    {
                        BitmapImage bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(path);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        img.Source = bitmap;
                    }
                }
                catch { }

                stack.Children.Add(img);
            }

            TextBlock tb = new TextBlock();
            tb.Text = text;
            tb.VerticalAlignment = VerticalAlignment.Center;
            tb.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
            stack.Children.Add(tb);

            return stack;
        }

        /// <summary>
        /// DTO-based context menu. Reads only plain data from the PlcTreeNodeDto; no COM
        /// object is touched. Block name / handle are carried by the DTO.
        /// </summary>
        private System.Windows.Controls.ContextMenu GetContextMenuForDto(PlcTreeNodeDto dto)
        {
            var menu = new System.Windows.Controls.ContextMenu();

            if (dto.Kind == PlcTreeNodeKind.Block || dto.Kind == PlcTreeNodeKind.Type)
            {
                var deleteItem = new System.Windows.Controls.MenuItem { Header = "Delete Block" };
                deleteItem.Click += MnuBlockDelete_Click;
                menu.Items.Add(deleteItem);

                var exportItem = new System.Windows.Controls.MenuItem { Header = "Export Block" };
                exportItem.Click += ExportBlock_Click;
                menu.Items.Add(exportItem);

                var addAiItem = new System.Windows.Controls.MenuItem { Header = "Add To AI Context" };
                addAiItem.Click += async (s, e) =>
                {
                    // Feedback-first: the pending chip + wait cursor appear immediately, the real
                    // chip lands when the export (STA) and IR conversion (background) finish.
                    await AddReferenceToAiWithFeedbackAsync("TreeContextMenu", dto.Name,
                        () => BuildAiReferenceFromDtoAsync(dto));
                };
                menu.Items.Add(addAiItem);

                if (dto.Kind == PlcTreeNodeKind.Block)
                {
                    menu.Items.Add(new Separator());
                    var analyzeCallChain = new System.Windows.Controls.MenuItem { Header = "Analyze Call Chain" };
                    analyzeCallChain.Click += (s, e) => OpenDependencyAnalysis(dto.Name);
                    menu.Items.Add(analyzeCallChain);
                }
            }
            else if (dto.Kind == PlcTreeNodeKind.BlockGroup || dto.Kind == PlcTreeNodeKind.TypeGroup)
            {
                var addItem = new System.Windows.Controls.MenuItem { Header = "Add Group" };
                addItem.Click += MenuGroupAdd_Click;
                menu.Items.Add(addItem);

                var deleteItem = new System.Windows.Controls.MenuItem { Header = "Delete Group" };
                deleteItem.Click += MenuGroupDelete_Click;
                menu.Items.Add(deleteItem);
            }

            return menu.Items.Count > 0 ? menu : null;
        }

        /// <summary>Build an AI-context reference from a tree DTO (export runs on STA thread by handle).</summary>
        private async System.Threading.Tasks.Task<string> BuildAiReferenceFromDtoAsync(PlcTreeNodeDto dto)
        {
            if (dto.Kind == PlcTreeNodeKind.Block || dto.Kind == PlcTreeNodeKind.Type)
            {
                try
                {
                    // Resolve by opaque handle (set when the tree was built), not by name.
                    // Name-based ExportBlockCodeAsync only searches the ROOT BlockGroup/TypeGroup
                    // (Blocks.Find/Types.Find do not recurse into sub-groups) and depends on the
                    // currently-selected PlcSoftware, so blocks living in a sub-group reported
                    // "not found in project". GetBlockContentAsync(handle) resolves the exact COM
                    // object on the STA thread regardless of group nesting or current selection.
                    if (!string.IsNullOrEmpty(dto.Handle))
                    {
                        var content = await _viewModel.TiaService.GetBlockContentAsync(dto.Handle).ConfigureAwait(true);
                        if (content != null && string.IsNullOrEmpty(content.Error))
                        {
                            // The XML is projected by the snapshot service: SCL/STL become plain source
                            // text, LAD/FBD/GRAPH become the canonical JSON IR (same contract as the
                            // get_block_content tool). A huge raw XML dump is only a last-resort fallback.
                            if (!string.IsNullOrWhiteSpace(content.Xml))
                            {
                                // XML -> IR conversion is pure CPU and can take noticeable time on big
                                // LAD/FBD blocks. Keep it off the UI thread so the app stays responsive
                                // (the pending chip keeps pulsing while this runs).
                                string xml = content.Xml;
                                string name = dto.Name;
                                string irReference = await System.Threading.Tasks.Task.Run(
                                    () => TiaXmlAgentReferenceBuilder.TryBuildReference(xml, name, out var r) ? r : null);
                                if (irReference != null)
                                    return $"Block: {dto.Name}\n{irReference}";

                                AppLogger.Log($"[BuildAiReferenceFromDto] IR conversion failed for '{dto.Name}', falling back to raw XML");
                                return $"Block: {dto.Name}\n{content.Xml}";
                            }
                        }
                        else if (content != null && !string.IsNullOrEmpty(content.Error))
                        {
                            AppLogger.Log($"[BuildAiReferenceFromDto] content error for '{dto.Name}': {content.Error}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[BuildAiReferenceFromDto] export failed: {ex.Message}");
                }
            }
            return $"{dto.Kind}: {dto.Name}";
        }

        /// <summary>
        /// Build an AI-context reference from a tree node tag. Tags now carry a
        /// <see cref="PlcTreeNodeDto"/>; legacy COM tags are kept as a fallback for any
        /// not-yet-migrated call sites. All COM access is routed through the STA thread
        /// via the DTO export path.
        /// </summary>
        private async System.Threading.Tasks.Task<string> BuildAiReferenceFromTreeTagAsync(object tag)
        {
            if (tag is PlcTreeNodeDto dto)
                return await BuildAiReferenceFromDtoAsync(dto).ConfigureAwait(true);

            // Legacy fallback: resolve a block name from a COM tag and export via STA.
            string name = tag switch
            {
                PlcBlock b => b.Name,
                PlcStruct s => s.Name,
                PlcType t => t.Name,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(name))
            {
                try
                {
                    string code = await _viewModel.TiaService.ExportBlockCodeAsync(name).ConfigureAwait(true);
                    if (!string.IsNullOrWhiteSpace(code))
                        return $"Block: {name}\n{code}";
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[BuildAiReferenceFromTreeTag] export failed: {ex.Message}");
                }
                return $"Block: {name}";
            }

            return tag?.ToString() ?? string.Empty;
        }

    }
}
