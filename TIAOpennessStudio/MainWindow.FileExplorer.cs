using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using TIAOpennessStudio.Events;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.ViewModels;

namespace TIAOpennessStudio
{
    public partial class MainWindow
    {
        private string _activeFileExplorerFilePath;
        private TextEditor _activeFileExplorerEditor;

        private void LoadFileExplorer(string filter = "")
        {
            if (treeFileExplorer == null) return;
            treeFileExplorer.Items.Clear();

            string dirPath = GetGeneratedCodeDirectory();
            if (!Directory.Exists(dirPath))
            {
                try { Directory.CreateDirectory(dirPath); } catch { return; }
            }

            var rootNode = CreateDirectoryNode(new DirectoryInfo(dirPath), filter);
            if (rootNode != null)
            {
                treeFileExplorer.Items.Add(rootNode);
                rootNode.IsExpanded = true;
            }
        }

        private string GetGeneratedCodeDirectory()
        {
            return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "GeneratedCode"));
        }

        /// <summary>
        /// Chat code block action: writes the block's text as a file into the folder currently
        /// selected in the Explorer tree (falling back to the GeneratedCode root), then refreshes
        /// the tree and reports the result back to the button that triggered it.
        /// </summary>
        private void SaveCodeBlockToExplorerFolder(SaveCodeBlockPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.Text))
            {
                payload?.Completed?.Invoke(false, "empty");
                return;
            }

            try
            {
                var directory = ResolveExplorerSaveDirectory();
                Directory.CreateDirectory(directory);

                var fileName = BuildUniqueCodeBlockFileName(directory, payload);
                var fullPath = Path.Combine(directory, fileName);
                File.WriteAllText(fullPath, payload.Text, new UTF8Encoding(false));

                LoadFileExplorer(txtFileSearch?.Text ?? "");
                AppLogger.Log($"[CodeBlock] saved {payload.Language} block to {fullPath}");
                payload.Completed?.Invoke(true, fullPath);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Saving code block to Explorer failed", ex);
                payload.Completed?.Invoke(false, ex.Message);
            }
        }

        /// <summary>Selected Explorer folder; a selected file maps to its own directory.</summary>
        private string ResolveExplorerSaveDirectory()
        {
            if (treeFileExplorer?.SelectedItem is TreeViewItem node && node.Tag is string tag && !string.IsNullOrEmpty(tag))
            {
                if (Directory.Exists(tag)) return tag;
                if (File.Exists(tag))
                {
                    var parent = Path.GetDirectoryName(tag);
                    if (!string.IsNullOrEmpty(parent)) return parent;
                }
            }
            return GetGeneratedCodeDirectory();
        }

        /// <summary>Uses the block's own file name when it has one, otherwise a timestamped name.</summary>
        private static string BuildUniqueCodeBlockFileName(string directory, SaveCodeBlockPayload payload)
        {
            var extension = GetExtensionForLanguage(payload.Language);
            var baseName = Path.GetFileNameWithoutExtension((payload.FileName ?? "").Trim());
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "Code_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            }
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                baseName = baseName.Replace(invalid, '_');
            }

            var candidate = baseName + extension;
            int suffix = 2;
            while (File.Exists(Path.Combine(directory, candidate)))
            {
                candidate = $"{baseName} ({suffix++}){extension}";
            }
            return candidate;
        }

        private static string GetExtensionForLanguage(string language)
        {
            switch ((language ?? "").Trim().ToLowerInvariant())
            {
                case "scl": return ".scl";
                case "stl":
                case "awl": return ".awl";
                case "xml":
                case "aml": return ".xml";
                case "json": return ".json";
                case "csharp":
                case "cs": return ".cs";
                case "javascript":
                case "js": return ".js";
                case "typescript":
                case "ts": return ".ts";
                case "python":
                case "py": return ".py";
                case "bash":
                case "sh": return ".sh";
                default: return ".txt";
            }
        }

        private TreeViewItem CreateDirectoryNode(DirectoryInfo directoryInfo, string filter)
        {
            var directoryNode = new TreeViewItem { Header = CreateFileExplorerHeader("\uE8B7", $"{directoryInfo.Name}  ({directoryInfo.GetFileSystemInfos().Length})", true), Tag = directoryInfo.FullName, ToolTip = directoryInfo.FullName };
            bool hasMatchingChild = false;

            try
            {
                foreach (var directory in directoryInfo.GetDirectories())
                {
                    var childDirNode = CreateDirectoryNode(directory, filter);
                    if (childDirNode != null)
                    {
                        directoryNode.Items.Add(childDirNode);
                        hasMatchingChild = true;
                    }
                }

                foreach (var file in directoryInfo.GetFiles())
                {
                    if (string.IsNullOrEmpty(filter) || file.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        directoryNode.Items.Add(new TreeViewItem { Header = CreateFileExplorerHeader("\uE8A5", file.Name, false), Tag = file.FullName, ToolTip = file.FullName });
                        hasMatchingChild = true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FileExplorer] Directory enumeration failed: {ex.Message}");
            }

            if (!string.IsNullOrEmpty(filter) && !hasMatchingChild)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(filter))
            {
                directoryNode.IsExpanded = true;
            }

            return directoryNode;
        }

        private StackPanel CreateFileExplorerHeader(string icon, string text, bool isDirectory)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = isDirectory ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand
            };
            var iconBlock = new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Width = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(isDirectory ? "AccentBrush" : "MutedTextBrush")
            };
            var textBlock = new TextBlock
            {
                Text = text,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextBrush")
            };

            panel.Children.Add(iconBlock);
            panel.Children.Add(textBlock);

            if (!isDirectory)
            {
                panel.MouseEnter += (s, e) =>
                {
                    iconBlock.Foreground = (Brush)FindResource("AccentBrush");
                    textBlock.Foreground = (Brush)FindResource("AccentBrush");
                    textBlock.TextDecorations = TextDecorations.Underline;
                };
                panel.MouseLeave += (s, e) =>
                {
                    iconBlock.Foreground = (Brush)FindResource("MutedTextBrush");
                    textBlock.Foreground = (Brush)FindResource("TextBrush");
                    textBlock.TextDecorations = null;
                };
            }

            return panel;
        }

        private void txtFileSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            LoadFileExplorer(txtFileSearch.Text);
        }

        private void treeFileExplorer_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            OpenSelectedFileExplorerFile();
        }

        private void treeFileExplorer_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var item = FindTreeViewItem(e.OriginalSource as DependencyObject);
            if (item != null)
            {
                item.IsSelected = true;
                item.Focus();
                e.Handled = true;
            }
        }

        private TreeViewItem FindTreeViewItem(DependencyObject source)
        {
            while (source != null && !(source is TreeViewItem))
            {
                source = VisualTreeHelper.GetParent(source);
            }
            return source as TreeViewItem;
        }

        private string GetSelectedFileExplorerPath()
        {
            return (treeFileExplorer.SelectedItem as TreeViewItem)?.Tag as string;
        }

        private bool IsSelectedFileExplorerFile(out string filePath)
        {
            filePath = GetSelectedFileExplorerPath();
            return !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath);
        }

        private bool IsSelectedFileExplorerDirectory(out string directoryPath)
        {
            directoryPath = GetSelectedFileExplorerPath();
            return !string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath);
        }

        private void OpenSelectedFileExplorerFile()
        {
            if (IsSelectedFileExplorerFile(out var filePath))
            {
                OpenFileInCodeViewer(filePath);
            }
        }

        private void FileExplorer_Open_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedFileExplorerFile();
        }

        private void FileExplorer_Save_Click(object sender, RoutedEventArgs e)
        {
            SaveActiveFileExplorerFile();
        }

        private async void FileExplorer_ImportToTia_Click(object sender, RoutedEventArgs e)
        {
            if (!IsSelectedFileExplorerFile(out var filePath))
            {
                MessageBox.Show("Please select a file to import.", "Import to TIA", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await ImportFileExplorerFileToTiaAsync(filePath);
        }

        private void FileExplorer_NewFile_Click(object sender, RoutedEventArgs e)
        {
            var baseDir = IsSelectedFileExplorerDirectory(out var dir) ? dir : Path.GetDirectoryName(GetSelectedFileExplorerPath()) ?? GetGeneratedCodeDirectory();
            var dialog = new InputDialog("New SCL File", "File name:", "NewBlock.scl") { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var fileName = dialog.Answer?.Trim();
            if (string.IsNullOrWhiteSpace(fileName)) return;
            if (Path.GetExtension(fileName) == string.Empty) fileName += ".scl";

            var filePath = Path.Combine(baseDir, fileName);
            if (File.Exists(filePath))
            {
                MessageBox.Show("File already exists.", "New SCL File", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            File.WriteAllText(filePath, string.Empty, new UTF8Encoding(false));
            LoadFileExplorer(txtFileSearch.Text);
            OpenFileInCodeViewer(filePath);
        }

        private void FileExplorer_NewFolder_Click(object sender, RoutedEventArgs e)
        {
            var baseDir = IsSelectedFileExplorerDirectory(out var dir) ? dir : Path.GetDirectoryName(GetSelectedFileExplorerPath()) ?? GetGeneratedCodeDirectory();
            var dialog = new InputDialog("New Folder", "Folder name:", "NewFolder") { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var folderName = dialog.Answer?.Trim();
            if (string.IsNullOrWhiteSpace(folderName)) return;

            Directory.CreateDirectory(Path.Combine(baseDir, folderName));
            LoadFileExplorer(txtFileSearch.Text);
        }

        private void FileExplorer_Rename_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = GetSelectedFileExplorerPath();
            if (string.IsNullOrWhiteSpace(selectedPath) || !File.Exists(selectedPath) && !Directory.Exists(selectedPath)) return;

            var dialog = new InputDialog("Rename", "New name:", Path.GetFileName(selectedPath)) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var newName = dialog.Answer?.Trim();
            if (string.IsNullOrWhiteSpace(newName)) return;

            var targetPath = Path.Combine(Path.GetDirectoryName(selectedPath), newName);
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                MessageBox.Show("Target already exists.", "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (File.Exists(selectedPath)) File.Move(selectedPath, targetPath);
            else Directory.Move(selectedPath, targetPath);

            if (string.Equals(_activeFileExplorerFilePath, selectedPath, StringComparison.OrdinalIgnoreCase))
            {
                _activeFileExplorerFilePath = targetPath;
            }
            LoadFileExplorer(txtFileSearch.Text);
        }

        private void FileExplorer_Delete_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = GetSelectedFileExplorerPath();
            if (string.IsNullOrWhiteSpace(selectedPath) || !File.Exists(selectedPath) && !Directory.Exists(selectedPath)) return;

            if (MessageBox.Show($"Delete '{Path.GetFileName(selectedPath)}'?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            if (File.Exists(selectedPath)) File.Delete(selectedPath);
            else Directory.Delete(selectedPath, true);

            if (string.Equals(_activeFileExplorerFilePath, selectedPath, StringComparison.OrdinalIgnoreCase))
            {
                _activeFileExplorerFilePath = null;
                _activeFileExplorerEditor = null;
            }
            LoadFileExplorer(txtFileSearch.Text);
        }

        private void FileExplorer_Refresh_Click(object sender, RoutedEventArgs e)
        {
            LoadFileExplorer(txtFileSearch.Text);
        }

        private void SaveActiveFileExplorerFile()
        {
            if (string.IsNullOrWhiteSpace(_activeFileExplorerFilePath) || _activeFileExplorerEditor == null)
            {
                MessageBox.Show("Open a file before saving.", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                File.WriteAllText(_activeFileExplorerFilePath, _activeFileExplorerEditor.Text, new UTF8Encoding(false));
                _viewModel.StatusText = $"Status: Saved {Path.GetFileName(_activeFileExplorerFilePath)}";
                LoadFileExplorer(txtFileSearch.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save file:\n{ex.Message}", "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ImportFileExplorerFileToTiaAsync(string filePath)
        {
            try
            {
                SaveActiveFileExplorerFileIfMatches(filePath);

                string code = File.ReadAllText(filePath);
                string blockName = Path.GetFileNameWithoutExtension(filePath);
                string blockType = ExtractBlockTypeFromScl(code) ?? InferBlockTypeFromFileName(blockName);

                // Use the target group selected by the user in the status bar chip (StatusBlockTargetChip / StatusTypeTargetChip);
                // if none is selected, "" means the root group. UDT goes to the Types group, everything else to the Blocks group.
                bool isUdt = string.Equals(blockType, "UDT", StringComparison.OrdinalIgnoreCase);
                string groupPath = "";
                if (_chatViewModel != null)
                {
                    groupPath = isUdt ? (_chatViewModel.TargetTypeGroupPath ?? "")
                                      : (_chatViewModel.TargetBlockGroupPath ?? "");
                }
                AppLogger.Log($"[FileExplorer] Import '{blockName}' (type={blockType}, isUdt={isUdt}) groupPath='{groupPath}' (BlockChip='{_chatViewModel?.TargetBlockGroupPath}', TypeChip='{_chatViewModel?.TargetTypeGroupPath}')");

                ShowOverlay($"Importing {Path.GetFileName(filePath)} to TIA...");
                _viewModel.StatusText = $"Status: Importing {Path.GetFileName(filePath)} to TIA...";

                var importTask = _viewModel.TiaService.ImportBlockCodeAsync(blockName, blockType, code, groupPath);
                var completedTask = await Task.WhenAny(importTask, Task.Delay(TimeSpan.FromSeconds(60)));
                if (completedTask != importTask)
                {
                    throw new TimeoutException("Import timed out. TIA Portal may still be busy. Please check the TIA project state before retrying.");
                }

                string result = await importTask;
                _viewModel.StatusText = $"Status: Imported {Path.GetFileName(filePath)} to TIA";
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish(result ?? "Import Successful!");
                _eventAggregator.GetEvent<ImportSuccessfulEvent>().Publish();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import to TIA failed:\n{ex.Message}", "Import to TIA", MessageBoxButton.OK, MessageBoxImage.Error);
                _viewModel.StatusText = "Status: Import to TIA failed";
            }
            finally
            {
                HideOverlay();
            }
        }

        private void SaveActiveFileExplorerFileIfMatches(string filePath)
        {
            if (_activeFileExplorerEditor != null && string.Equals(_activeFileExplorerFilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(filePath, _activeFileExplorerEditor.Text, new UTF8Encoding(false));
            }
        }

        private string InferBlockTypeFromFileName(string blockName)
        {
            if (blockName.StartsWith("FC", StringComparison.OrdinalIgnoreCase) || blockName.IndexOf("_FC", StringComparison.OrdinalIgnoreCase) >= 0) return "FC";
            if (blockName.StartsWith("DB", StringComparison.OrdinalIgnoreCase) || blockName.IndexOf("_DB", StringComparison.OrdinalIgnoreCase) >= 0) return "DB";
            if (blockName.StartsWith("OB", StringComparison.OrdinalIgnoreCase) || blockName.IndexOf("_OB", StringComparison.OrdinalIgnoreCase) >= 0) return "OB";
            return "FB";
        }

        private string ExtractBlockTypeFromScl(string sclCode)
        {
            if (string.IsNullOrWhiteSpace(sclCode)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(sclCode, @"^\s*(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
            if (match.Success)
            {
                string type = match.Groups[1].Value.ToUpperInvariant();
                if (type == "FUNCTION_BLOCK") return "FB";
                if (type == "FUNCTION") return "FC";
                if (type == "DATA_BLOCK") return "DB";
                if (type == "ORGANIZATION_BLOCK") return "OB";
            }
            return null;
        }

        private void OpenFileInCodeViewer(string filePath)
        {
            try
            {
                string content = File.ReadAllText(filePath);
                string fileName = Path.GetFileName(filePath);
                var editor = _viewModel.CodeViewerService.CreateCodeEditor(content);
                editor.TextChanged += (s, e) =>
                {
                    if (string.Equals(_activeFileExplorerFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        _viewModel.StatusText = $"Status: Editing {fileName}";
                    }
                };
                editor.TextArea.DefaultInputHandler.NestedInputHandlers.Add(new SaveFileInputHandler(editor.TextArea, () => SaveActiveFileExplorerFile()));
                _activeFileExplorerFilePath = filePath;
                _activeFileExplorerEditor = editor;

                _eventAggregator.GetEvent<ShowCodeViewerEvent>().Publish(new CodeViewerEventArgs(fileName, "File", editor));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private sealed class SaveFileInputHandler : TextAreaInputHandler
        {
            private readonly Action _saveAction;

            public SaveFileInputHandler(TextArea textArea, Action saveAction) : base(textArea)
            {
                _saveAction = saveAction;
            }

            public override void Attach()
            {
                CommandBindings.Add(new System.Windows.Input.CommandBinding(System.Windows.Input.ApplicationCommands.Save, (s, e) => _saveAction()));
                base.Attach();
            }
        }
    }
}
