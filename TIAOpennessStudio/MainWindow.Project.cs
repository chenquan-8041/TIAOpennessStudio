using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System.Xml;
using Prism.Events;
using TIAOpennessStudio.ViewModels;
using TIAOpennessStudio.Events;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio
{
    // Project partial: open/close, import/export, compile, and project-level operations.
    public partial class MainWindow
    {
        private ProjectViewModel _projectViewModel;
        private string _targetPath = "";
        
        private void InitProjectModule()
        {
            _projectViewModel = _viewModel.ProjectViewModel;
            
            // Subscribe to ProjectViewModel's Prism events
            // Note: ShowMessageEvent is subscribed only here (not duplicated in InitTreeModule)
            // Note: RefreshTreeEvent is subscribed only in MainViewModel.SubscribeToViewModelEvents() (to avoid duplicate refreshes)
            _eventAggregator.GetEvent<ShowMessageEvent>().Subscribe(msg => MessageBox.Show(msg, LanguageManager.Instance.GetString("Dlg.Info"), MessageBoxButton.OK, MessageBoxImage.Information), ThreadOption.UIThread);
            _eventAggregator.GetEvent<ShowOverlayEvent>().Subscribe(msg => ShowOverlay(msg), ThreadOption.UIThread);
            _eventAggregator.GetEvent<HideOverlayEvent>().Subscribe(() => HideOverlay(), ThreadOption.UIThread);
            _eventAggregator.GetEvent<ProjectStatusChangedEvent>().Subscribe(() => UpdateProjectStatus(), ThreadOption.UIThread);
            _eventAggregator.GetEvent<ImportSuccessfulEvent>().Subscribe(() => ReloadProjectTree(), ThreadOption.UIThread);
            
            // OpenFileDialogRequested stays a direct event (request/response pattern).
            // OpenProject now runs on the TiaStaHost STA thread, so this handler can be
            // invoked off the UI thread — marshal the dialog back to the UI thread.
            _projectViewModel.OpenFileDialogRequested += (filter, ext) =>
            {
                Func<string> show = () =>
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter, DefaultExt = ext };
                    return dialog.ShowDialog() == true ? dialog.FileName : null;
                };
                return Dispatcher.CheckAccess()
                    ? show()
                    : Dispatcher.Invoke(show);
            };
            
            // ChoiceDialogRequested event handler. OpenProject runs on the STA host thread,
            // so marshal the MessageBox back to the UI thread.
            _projectViewModel.ChoiceDialogRequested += (choiceArgs) =>
            {
                Func<string> show = () =>
                {
                    // Build the message
                    StringBuilder message = new StringBuilder();
                    message.AppendLine(choiceArgs.Message);
                    message.AppendLine();
                    message.AppendLine(LanguageManager.Instance.GetString("Dlg.Choose"));

                    for (int i = 0; i < choiceArgs.Options.Count; i++)
                    {
                        var option = choiceArgs.Options[i];
                        message.AppendLine($"{i + 1}. {option.Text}");
                        if (!string.IsNullOrEmpty(option.Description))
                        {
                            message.AppendLine($"   {option.Description}");
                        }
                    }

                    // Show the message box
                    var result = MessageBox.Show(
                        message.ToString(),
                        choiceArgs.Title,
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    // Return the corresponding option ID based on the user's choice
                    if (result == MessageBoxResult.Yes && choiceArgs.Options.Count > 0)
                        return choiceArgs.Options[0].Id;
                    if (result == MessageBoxResult.No && choiceArgs.Options.Count > 1)
                        return choiceArgs.Options[1].Id;
                    if (choiceArgs.Options.Count > 2)
                        return choiceArgs.Options[2].Id;
                    return null;
                };
                return Dispatcher.CheckAccess()
                    ? show()
                    : Dispatcher.Invoke(show);
            };
        }
        
        private void ReloadProjectTree()
        {
            if (project != null)
            {
                // After a successful AI/manual import: not only refresh the device list, but also rebuild the block tree,
                // so newly imported/overwritten blocks appear immediately in treeBlocks.
                RefreshBlocksTreeAfterMutation();
            }
        }
        
        private Task<bool> EnsurePlcSoftwareReadyAsync()
        {
            // Async, STA-safe: never block the UI thread on the STA host (connect / PLC lookup).
            return _projectViewModel.EnsurePlcSoftwareReadyAsync();
        }

        /// <summary>
        /// Resolve the current write target from the selected tree node. The tree now stores
        /// <see cref="PlcTreeNodeDto"/> (plain data + opaque handle) on each node's Tag, so no
        /// COM object is touched here. Only block/type nodes are valid write targets.
        /// </summary>
        private bool TryGetCurrentWriteTarget(out WriteTargetDto target)
        {
            target = null;
            if (!(treeBlocks.SelectedItem is TreeViewItem selected) || selected.Tag == null)
                return false;

            if (selected.Tag is PlcTreeNodeDto dto &&
                (dto.Kind == PlcTreeNodeKind.Block || dto.Kind == PlcTreeNodeKind.Type))
            {
                target = new WriteTargetDto
                {
                    Name = dto.Name,
                    BlockType = string.IsNullOrEmpty(dto.BlockType)
                        ? (dto.Kind == PlcTreeNodeKind.Type ? "UDT" : "")
                        : dto.BlockType,
                    Handle = dto.Handle
                };
                return true;
            }

            return false;
        }

        private string BuildCompileVerifyReport(CompilerResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"State: {result.State}");
            sb.AppendLine($"Errors: {result.ErrorCount}");
            sb.AppendLine($"Warnings: {result.WarningCount}");
            sb.AppendLine();
            sb.AppendLine("Messages:");
            AppendCompilerMessages(result.Messages, sb, 0);
            return sb.ToString();
        }

        private void AppendCompilerMessages(CompilerResultMessageComposition messages, StringBuilder sb, int depth)
        {
            foreach (CompilerResultMessage msg in messages)
            {
                string indent = new string(' ', depth * 2);
                string text = msg.Description ?? "";
                var blockHints = Regex.Matches(text, @"\b(?:FB|FC|DB|OB|UDT)_[A-Za-z0-9_]+\b");
                string hint = "";
                if (blockHints.Count > 0)
                {
                    var names = new List<string>();
                    foreach (Match m in blockHints) names.Add(m.Value);
                    hint = $" [BlockHint: {string.Join(", ", names)}]";
                }
                sb.AppendLine($"{indent}- {msg.State}: {text}{hint}");
                AppendCompilerMessages(msg.Messages, sb, depth + 1);
            }
        }

        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            // Fire-and-forget async; OpenProject is now async to keep Openness COM off the UI thread.
            _ = _projectViewModel.OpenProjectAsync();
        }

        private void AddPath_Click(object sender, RoutedEventArgs e)
        {
            if (software == null)
            {
                MessageBox.Show("Please open a project and select a PLC first.", "No Project");
                return;
            }

            var dialog = new TIAOpennessStudio.Services.InputDialog("Enter Target Path", "Path (e.g. Factory\\Line1):", _targetPath);
            if (dialog.ShowDialog() == true)
            {
                _targetPath = dialog.Answer;
                string[] lst = _targetPath.Split('\\');
            }
        }

        private void ExportXml_Click(object sender, RoutedEventArgs e)
        {
            ExportBlock_Click(sender, e);
        }

        private string GetNextFileName(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            string name = Path.GetFileNameWithoutExtension(filePath);
            string ext = Path.GetExtension(filePath);
            int i = 1;
            while (File.Exists(filePath))
            {
                filePath = Path.Combine(dir, $"{name}_{i}{ext}");
                i++;
            }
            return filePath;
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            _projectViewModel.ReloadProject();
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ChatPanel.PreviewTextBox.Text))
            {
                MessageBox.Show("Please generate code first.");
                return;
            }

            // Active Import button: uses the default target (project root + FB).
            // If the user selected a BlockGroup path on the chip, follow it automatically; otherwise prompt to confirm writing to the root.
            string groupPath = _chatViewModel?.TargetBlockGroupPath ?? "";
            if (string.IsNullOrEmpty(groupPath))
            {
                var lm = LanguageManager.Instance;
                var result = MessageBox.Show(
                    lm.GetString("Import.ConfirmRoot"),
                    lm.GetString("Import.ConfirmRootTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
            }
            await _projectViewModel.ImportCodeAsync(ChatPanel.PreviewTextBox.Text, "AI_Generated_Block", "FB", groupPath);
        }

        private async void CompileProject_Click(object sender, RoutedEventArgs e)
        {
            // Compile runs on the dedicated STA host thread (OpennessService.CompileAsync →
            // OnSta → CompileCoreAsync), which uses the authoritative OpennessService.PlcSoftware.
            // The previous Task.Run(() => CompileProject()) path ran COM on an MTA thread-pool
            // thread AND read ProjectViewModel._plcSoftware (a shadow field that is only filled by
            // EnsurePlcSoftwareReady), so it reported "No PLC Software" even when a PLC was selected.
            var btn = sender as System.Windows.Controls.Button;
            if (btn != null) btn.IsEnabled = false;
            _viewModel.StatusText = LanguageManager.Instance.GetString("Status.Compiling");
            try
            {
                if (!await EnsurePlcSoftwareReadyAsync())
                {
                    MessageBox.Show("No PLC Software available for compilation.", "Compiler");
                    return;
                }
                string report = await _viewModel.TiaService.CompileAsync();
                MessageBox.Show(report, "Compiler");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Compile failed: {ex.Message}", "Compiler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (btn != null) btn.IsEnabled = true;
                _viewModel.StatusText = "";
            }
        }

        private async void SaveProject_Click(object sender, RoutedEventArgs e)
        {
            await _projectViewModel.SaveProjectAsync();
        }

        private async void CloseProject_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Log("[CloseProject_Click] Closing project...");
            
            // Call the ViewModel to close the project (fully async; no UI-thread block on the STA save).
            await _projectViewModel.CloseProjectAsync();
            
            // Clear references in MainWindow
            project = null;
            software = null;
            tiaPortal = null;
            
            // Clear the tree view (without breaking bindings)
            treeBlocks.Items.Clear();
            
            // Clear TreeViewModel collections (the UI updates automatically via bindings)
            _treeViewModel.Devices.Clear();
            _treeViewModel.PlcList.Clear();
            
            UpdateMenuEnableStates();
            
            AppLogger.Log("[CloseProject_Click] Project closed successfully");
        }

        private async void ImportBlock_Click(object sender, RoutedEventArgs e)
        {
            // Tag now carries a PlcTreeNodeDto; the import runs on the STA host thread by group handle.
            if (!((treeBlocks.SelectedItem as TreeViewItem)?.Tag is PlcTreeNodeDto dto) ||
                dto.Kind != PlcTreeNodeKind.BlockGroup)
            {
                MessageBox.Show("Please select a block group.", "Info");
                return;
            }

            string importPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Import");
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                InitialDirectory = importPath
            };
            if (openFileDialog.ShowDialog() != true) return;

            try
            {
                var res = await _viewModel.TiaService.ImportBlockFromFileAsync(dto.Handle, openFileDialog.FileName, false);
                if (res == null) { MessageBox.Show("Import failed.", "Error"); return; }
                if (res.InvalidFile) { MessageBox.Show("Wrong XML file (not a block)?", "Wrong file"); return; }
                if (!string.IsNullOrEmpty(res.Error)) { MessageBox.Show(res.Error, "Error"); return; }

                if (res.AlreadyExists)
                {
                    if (MessageBox.Show("Data block " + res.Name + " exists already. Overwrite?", "Overwrite",
                            MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                        return;
                    res = await _viewModel.TiaService.ImportBlockFromFileAsync(dto.Handle, openFileDialog.FileName, true);
                    if (res == null || !res.Success)
                    {
                        MessageBox.Show(res?.Error ?? "Import failed.", "Error");
                        return;
                    }
                }

                if (res.Success) RefreshBlocksTreeAfterMutation();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error");
            }
        }

        private async void ImportType_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                FilterIndex = 1,
                RestoreDirectory = true
            };
            if (openFileDialog.ShowDialog() != true) return;

            string fPath = openFileDialog.FileName;
            try
            {
                var res = await _viewModel.TiaService.ImportTypeFromFileAsync(fPath, false);
                if (res == null) { MessageBox.Show("Import failed.", "Exception"); return; }
                if (res.InvalidFile) { MessageBox.Show("Wrong XML file (PlcStruct)?", "Wrong file"); return; }
                if (!string.IsNullOrEmpty(res.Error)) { MessageBox.Show(res.Error, "Exception"); return; }

                if (res.AlreadyExists)
                {
                    if (MessageBox.Show("Data type " + res.Name + " exists already. Overwrite?", "Overwrite",
                            MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                        return;
                    res = await _viewModel.TiaService.ImportTypeFromFileAsync(fPath, true);
                    if (res == null || !res.Success)
                    {
                        MessageBox.Show(res?.Error ?? "Import failed.", "Exception");
                        return;
                    }
                }

                if (res.Success)
                {
                    MessageBox.Show("Data type " + Path.GetFileName(fPath) + " has been imported", "Import");
                    RefreshBlocksTreeAfterMutation();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Exception");
            }
        }

        private async void ExportBlock_Click(object sender, RoutedEventArgs e)
        {
            // Tag now carries a PlcTreeNodeDto; resolve the export target by its opaque handle.
            // The COM Export call runs on the STA host thread inside OpennessService.
            if (!((treeBlocks.SelectedItem as TreeViewItem)?.Tag is PlcTreeNodeDto dto) ||
                (dto.Kind != PlcTreeNodeKind.Block && dto.Kind != PlcTreeNodeKind.Type))
            {
                MessageBox.Show("Please select a block or data type to export.", "Info");
                return;
            }

            // Let the user choose an export directory, consistent with Export All
            string targetDir;
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select target folder to export";
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
                targetDir = dialog.SelectedPath;
            }

            if (string.IsNullOrWhiteSpace(targetDir))
            {
                MessageBox.Show("No target folder selected.", "Export");
                return;
            }

            try
            {
                var res = await _viewModel.TiaService.ExportByHandleAsync(dto.Handle, targetDir);
                if (res == null)
                {
                    MessageBox.Show("Export failed.", "Export");
                }
                else if (res.Success)
                {
                    MessageBox.Show("File " + Path.GetFileName(res.FilePath) + " has been exported", "Export");
                }
                else if (res.NotConsistent)
                {
                    MessageBox.Show((dto.Kind == PlcTreeNodeKind.Type ? "Data type " : "Block ") +
                        res.Name + " is not consistent. Please compile", "Export");
                }
                else
                {
                    MessageBox.Show("Error: " + (res.Error ?? "Export failed."));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message);
            }
        }

        /// <summary>
        /// Export all program blocks of the currently selected PLC to a user-chosen directory.
        /// Recursively traverse the BlockGroup and its subgroups, preserving the group directory structure; skip inconsistent (uncompiled) blocks.
        /// Openness calls must run on the UI (STA) thread, so an overlay + Dispatcher gives the UI feedback first,
        /// then the traversal export completes on the same thread, and finally a summary result is shown.
        /// </summary>
        private async void ExportAllBlocks_Click(object sender, RoutedEventArgs e)
        {
            string targetDir;
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select target folder to export all blocks of the current PLC";
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
                targetDir = dialog.SelectedPath;
            }

            if (string.IsNullOrWhiteSpace(targetDir))
            {
                MessageBox.Show("No target folder selected.", "Export All Blocks");
                return;
            }

            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            ShowRegionOverlay(treeBlocksOverlay, txtTreeBlocksOverlayMessage, "Exporting blocks, data types and tag tables...");
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            try
            {
                // The whole traversal + COM Export runs on the STA host thread inside OpennessService.
                var r = await _viewModel.TiaService.ExportAllAsync(targetDir);
                if (r == null || (!r.Success && !string.IsNullOrEmpty(r.Error)))
                {
                    MessageBox.Show(r?.Error ?? "Export failed.", "Export All",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                int totalExported = r.BlocksExported + r.TypesExported + r.TagTablesExported;
                var sb = new StringBuilder();
                sb.AppendLine($"Export completed for PLC '{r.PlcName}'.");
                sb.AppendLine($"Blocks exported: {r.BlocksExported} (skipped {r.BlocksSkipped})");
                sb.AppendLine($"Data types exported: {r.TypesExported} (skipped {r.TypesSkipped})");
                sb.AppendLine($"Tag tables exported: {r.TagTablesExported}");
                sb.AppendLine($"Total exported: {totalExported}, Failed: {r.Failures.Count}");
                sb.AppendLine($"Output: {r.OutputDir}");
                if (r.Failures.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("First failures:");
                    foreach (var f in r.Failures.Take(10)) sb.AppendLine("  - " + f);
                }

                MessageBox.Show(sb.ToString(), "Export All",
                    MessageBoxButton.OK,
                    r.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ExportAll] ERROR: {ex.Message}");
                MessageBox.Show("Export failed: " + ex.Message, "Export All",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                HideRegionOverlay(treeBlocksOverlay);
                System.Windows.Input.Mouse.OverrideCursor = null;
            }
        }

        private void ExportProjectTexts_Click(object sender, RoutedEventArgs e)
        {
            if (project != null)
            {
                try
                {
                    string filePath = (string)Properties.Settings.Default["PathLanguageText"];
                    if (string.IsNullOrEmpty(filePath)) filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Export");

                    Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog();
                    saveFileDialog.Filter = "XML files (*.xlsx)|*.xlsx";
                    saveFileDialog.FilterIndex = 1;
                    saveFileDialog.InitialDirectory = filePath;
                    saveFileDialog.FileName = "TIAProjectTexts.xlsx";

                    if (saveFileDialog.ShowDialog() == true)
                    {
                        Properties.Settings.Default["PathImportBlock"] = saveFileDialog.FileName;
                        Properties.Settings.Default["PathLanguageText"] = Path.GetDirectoryName(saveFileDialog.FileName);
                        Properties.Settings.Default.Save();

                        string finalPath = GetNextFileName(saveFileDialog.FileName);
                        project.ExportProjectTexts(new FileInfo(finalPath), new CultureInfo("de-DE"), new CultureInfo("en-GB"));

                        MessageBox.Show("File " + Path.GetFileName(finalPath) + " has been exported", "Export language file");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Exception");
                }
            }
        }

        private void ImportProjectTexts_Click(object sender, RoutedEventArgs e)
        {
            if (project != null)
            {
                try
                {
                    string filePath = (string)Properties.Settings.Default["PathLanguageText"];
                    if (string.IsNullOrEmpty(filePath)) filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Export");

                    Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
                    openFileDialog.Filter = "XML files (*.xlsx)|*.xlsx";
                    openFileDialog.FilterIndex = 1;
                    openFileDialog.InitialDirectory = filePath;
                    openFileDialog.FileName = "TIAProjectTexts.xlsx";

                    if (openFileDialog.ShowDialog() == true)
                    {
                        project.ImportProjectTexts(new FileInfo(openFileDialog.FileName), true);
                        MessageBox.Show("File " + Path.GetFileName(openFileDialog.FileName) + " has been imported", "Import language file");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Exception");
                }
            }
        }

        private string TryExportToPdf(PlcBlock block, string exportDir, string safeName)
        {
            try
            {
                dynamic dBlock = block;
                var siemensAssembly = typeof(Siemens.Engineering.TiaPortal).Assembly;
                AppLogger.Log($"Searching for PrintService in {siemensAssembly.FullName}");

                var printType = siemensAssembly.GetType("Siemens.Engineering.PrintService");
                if (printType == null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        printType = assembly.GetType("Siemens.Engineering.PrintService");
                        if (printType != null) break;
                    }
                }

                if (printType != null)
                {
                    AppLogger.Log("Found PrintService type, getting service instance...");
                    var printService = dBlock.GetService(printType);
                    if (printService != null)
                    {
                        string pdfFile = System.IO.Path.Combine(exportDir, safeName + ".pdf");
                        AppLogger.Log($"Exporting {block.Name} to PDF...");
                        if (System.IO.File.Exists(pdfFile)) System.IO.File.Delete(pdfFile);

                        printService.Print(new FileInfo(pdfFile));
                        AppLogger.Log($"PDF Export successful: {pdfFile}");
                        return pdfFile;
                    }
                    else
                    {
                        AppLogger.Log("GetService(PrintService) returned null.");
                    }
                }
                else
                {
                    AppLogger.Log("PrintService type NOT FOUND in any loaded assembly.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"PDF Export failed with exception: {ex.Message}");
            }
            return null;
        }
    }
}
