using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Xml.Linq;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Implementations;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace TIAOpennessStudio.Controls
{
    public partial class LibraryCompareControl : UserControl
    {
        private const string CaptionBackup = "Backup";
        private const string CaptionCompare = "Compare";
        private const string CaptionRestore = "Restore";
        private const string CaptionExportXml = "Export XML";

        private const string FilterTagAll = "all";
        private const string FilterTagDiff = "diff";
        private const string FilterTagLeft = "left";
        private const string FilterTagRight = "right";
        private const string FilterTagSame = "same";
        private const string StatusOnlyLeft = "Only Left";
        private const string StatusOnlyRight = "Only Right";
        private const string StatusSame = "Same";
        private const string StatusVersionDiff = "Version Diff";
        private const string StatusOutdated = "Outdated";
        private const string StatusDiff = "Diff";

        private readonly ILibraryCompareService _compareService;
        private readonly IBackupService _backupService;
        private readonly ObservableCollection<CompareRow> _rows = new ObservableCollection<CompareRow>();
        private readonly ObservableCollection<BackupItem> _history = new ObservableCollection<BackupItem>();
        private readonly ICollectionView _rowsView;

        public LibraryCompareControl(ILibraryCompareService compareService, IBackupService backupService)
        {
            InitializeComponent();
            _compareService = compareService;
            _backupService = backupService;
            _compareService.ProgressUpdated += CompareService_ProgressUpdated;
            _rowsView = CollectionViewSource.GetDefaultView(_rows);
            gridCompareResults.ItemsSource = _rowsView;
            lstBackupHistory.ItemsSource = _history;
            btnExportXml.IsEnabled = false;
            txtBackupRoot.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
            ApplyLanguage();
            RefreshBackupHistory();
        }

        private void CompareService_ProgressUpdated(int percent, object userStatus)
        {
            Dispatcher.Invoke(() =>
            {
                string state = userStatus?.ToString();
                txtProgress.Text = string.IsNullOrWhiteSpace(state) ? "Running..." : state;
            });
        }

        private void BrowseBackupSource_Click(object sender, RoutedEventArgs e)
        {
            AssignPathFromPicker(txtBackupSource);
        }

        private void BrowseBackupRoot_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select backup root folder";
                dialog.SelectedPath = txtBackupRoot.Text;
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    txtBackupRoot.Text = dialog.SelectedPath;
                }
            }
        }

        private void CreateBackup_Click(object sender, RoutedEventArgs e)
        {
            string source = txtBackupSource.Text?.Trim();
            string root = txtBackupRoot.Text?.Trim();
            if (!IsExistingFile(source))
            {
                ShowWarning("Please select a valid source file.", CaptionBackup);
                return;
            }
            if (string.IsNullOrWhiteSpace(root))
            {
                ShowWarning("Please select a backup root folder.", CaptionBackup);
                return;
            }

            Directory.CreateDirectory(root);
            var record = _backupService.CreateBackup(source, root);

            AppendBackupLog($"Backup created: {record.BackupPath}");
            txtProgress.Text = "Backup completed.";
            RefreshBackupHistory();
        }

        private void BrowseLeft_Click(object sender, RoutedEventArgs e)
        {
            AssignPathFromPicker(txtLeftPath);
        }

        private void BrowseRight_Click(object sender, RoutedEventArgs e)
        {
            AssignPathFromPicker(txtRightPath);
        }

        private async void RunCompare_Click(object sender, RoutedEventArgs e)
        {
            string left = txtLeftPath.Text?.Trim();
            string right = txtRightPath.Text?.Trim();
            if (!IsExistingFile(left) || !IsExistingFile(right))
            {
                ShowWarning("Please choose two valid files for compare.", CaptionCompare);
                return;
            }

            SetCompareBusyState(true);
            _rows.Clear();
            txtCompareSummary.Text = "Comparing...";
            txtProgress.Text = "Starting compare...";

            try
            {
                CompareResults result = await Task.Run(() => _compareService.Compare(left, right));
                BuildRows(result);
                txtCompareSummary.Text = $"Types: {result.TypePairs.Count}, Copies: {result.CopyPairs.Count}";
                txtProgress.Text = "Compare finished.";
                ApplyCompareFilter();
                btnExportXml.IsEnabled = _rows.Count > 0;
            }
            catch (Exception ex)
            {
                txtProgress.Text = "Compare failed.";
                ShowError(ex.Message, "Compare Error");
            }
            finally
            {
                SetCompareBusyState(false);
            }
        }

        private void ExportCompareXml_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0)
            {
                ShowInfo("No compare result to export.", CaptionExportXml);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "XML files (*.xml)|*.xml",
                DefaultExt = ".xml",
                AddExtension = true,
                FileName = "library_compare_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xml",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            var doc = new XDocument(
                new XElement("LibraryCompareReport",
                    new XAttribute("generatedAtLocal", DateTime.Now.ToString("o")),
                    new XAttribute("generatedAtUtc", DateTime.UtcNow.ToString("o")),
                    new XAttribute("totalRows", _rows.Count),
                    new XElement("Rows",
                        _rows.Select(r =>
                            new XElement("Row",
                                new XAttribute("category", r.Category ?? string.Empty),
                                new XAttribute("status", r.Status ?? string.Empty),
                                new XElement("Path", r.Path ?? string.Empty),
                                new XElement("Name", r.Name ?? string.Empty),
                                new XElement("Left", r.Left ?? string.Empty),
                                new XElement("Right", r.Right ?? string.Empty)
                            ))
                    )
                )
            );

            doc.Save(dialog.FileName);
            txtProgress.Text = "XML exported: " + dialog.FileName;
        }

        private void BuildRows(CompareResults result)
        {
            if (result == null) return;

            foreach (var pair in result.TypePairs ?? new List<Tuple<ILibraryTypeModel, ILibraryTypeModel>>())
            {
                var left = pair.Item1;
                var right = pair.Item2;
                string status;
                if (left == null) status = StatusOnlyRight;
                else if (right == null) status = StatusOnlyLeft;
                else if (left.LatestVersion != right.LatestVersion) status = StatusVersionDiff;
                else if (left.OutDated || right.OutDated) status = StatusOutdated;
                else status = StatusSame;

                _rows.Add(new CompareRow
                {
                    Category = "Type",
                    Path = left?.FolderPath ?? right?.FolderPath ?? string.Empty,
                    Name = left?.Name ?? right?.Name ?? string.Empty,
                    Left = left?.LatestVersion?.ToString() ?? "-",
                    Right = right?.LatestVersion?.ToString() ?? "-",
                    Status = status
                });
            }

            foreach (var pair in result.CopyPairs ?? new List<Tuple<ILibraryCopyModel, ILibraryCopyModel>>())
            {
                var left = pair.Item1;
                var right = pair.Item2;
                string status;
                if (left == null) status = StatusOnlyRight;
                else if (right == null) status = StatusOnlyLeft;
                else if (!string.Equals(left.Author, right.Author, StringComparison.OrdinalIgnoreCase) ||
                         left.CreationDate != right.CreationDate) status = StatusDiff;
                else status = StatusSame;

                _rows.Add(new CompareRow
                {
                    Category = "Copy",
                    Path = left?.FolderPath ?? right?.FolderPath ?? string.Empty,
                    Name = left?.Name ?? right?.Name ?? string.Empty,
                    Left = left?.CreationDate.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                    Right = right?.CreationDate.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                    Status = status
                });
            }
        }

        private string PickFilePath()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "TIA Library/Project (*.al1;*.ap*)|*.al1;*.ap*|All files (*.*)|*.*",
                CheckFileExists = true
            };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
        }

        private void CompareFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyCompareFilter();
        }

        private void ApplyCompareFilter()
        {
            if (_rowsView == null || cmbCompareFilter == null)
            {
                return;
            }

            string filterTag = ((cmbCompareFilter.SelectedItem as ComboBoxItem)?.Tag as string) ?? FilterTagAll;
            _rowsView.Filter = obj =>
            {
                var row = obj as CompareRow;
                if (row == null) return false;
                if (filterTag == FilterTagAll) return true;
                if (filterTag == FilterTagDiff) return row.Status != StatusSame;
                if (filterTag == FilterTagLeft) return row.Status == StatusOnlyLeft;
                if (filterTag == FilterTagRight) return row.Status == StatusOnlyRight;
                if (filterTag == FilterTagSame) return row.Status == StatusSame;
                return true;
            };
            _rowsView.Refresh();
        }

        private void RefreshHistory_Click(object sender, RoutedEventArgs e)
        {
            RefreshBackupHistory();
        }

        private void BackupHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            btnRestoreBackup.IsEnabled = lstBackupHistory.SelectedItem is BackupItem;
        }

        private void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            var selected = lstBackupHistory.SelectedItem as BackupItem;
            if (selected == null || !IsExistingFile(selected.FilePath))
            {
                ShowWarning("Please select a valid backup file.", CaptionRestore);
                return;
            }

            string target = txtBackupSource.Text?.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                var picked = PickFilePath();
                if (string.IsNullOrWhiteSpace(picked)) return;
                target = picked;
                txtBackupSource.Text = target;
            }

            var result = MessageBox.Show(
                $"Restore backup to target?\n\nBackup: {selected.FilePath}\nTarget: {target}",
                "Confirm Restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            var restoreResult = _backupService.RestoreBackup(selected.FilePath, target, true);
            string safety = string.IsNullOrWhiteSpace(restoreResult.SafetyBackupPath) ? "none" : restoreResult.SafetyBackupPath;
            AppendBackupLog($"Restored: {selected.FilePath} -> {target} (safety backup: {safety})");
            txtProgress.Text = "Restore completed.";
        }

        private void RefreshBackupHistory()
        {
            string root = txtBackupRoot.Text?.Trim();
            if (string.IsNullOrWhiteSpace(root))
            {
                _history.Clear();
                return;
            }

            var records = _backupService.ListBackups(root, 500);
            _history.Clear();
            foreach (var record in records)
            {
                var createdAtLocal = record.CreatedAtUtc.ToLocalTime();
                _history.Add(new BackupItem
                {
                    FilePath = record.BackupPath,
                    Display = $"{createdAtLocal:yyyy-MM-dd HH:mm:ss} | {record.BackupName} | {ToReadableSize(record.SizeBytes)}"
                });
            }
        }

        private static string ToReadableSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("F1") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("F1") + " MB";
            return (mb / 1024.0).ToString("F1") + " GB";
        }

        private static bool IsExistingFile(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }

        private void AssignPathFromPicker(TextBox target)
        {
            var path = PickFilePath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                target.Text = path;
            }
        }

        private void AppendBackupLog(string message)
        {
            txtBackupLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            txtBackupLog.ScrollToEnd();
        }

        private void SetCompareBusyState(bool isBusy)
        {
            btnCompare.IsEnabled = !isBusy;
            if (isBusy)
            {
                btnExportXml.IsEnabled = false;
            }
        }

        private static void ShowWarning(string message, string caption)
        {
            MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static void ShowInfo(string message, string caption)
        {
            MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void ShowError(string message, string caption)
        {
            MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public void Cleanup()
        {
            _compareService.ProgressUpdated -= CompareService_ProgressUpdated;
            LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged(AppLanguage language)
        {
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            var lm = LanguageManager.Instance;

            txtWindowTitle.Text = lm.GetString("LC.Header");
            txtWindowSubtitle.Text = lm.GetString("LC.Subtitle");

            tabBackup.Header = lm.GetString("LC.Tab.Backup");
            tabCompare.Header = lm.GetString("LC.Tab.Compare");

            lblBackupSource.Text = lm.GetString("LC.Backup.SourceFile");
            lblBackupRoot.Text = lm.GetString("LC.Backup.Root");
            btnBrowseBackupSource.Content = lm.GetString("LC.Common.Browse");
            btnBrowseBackupRoot.Content = lm.GetString("LC.Common.Browse");
            btnCreateBackup.Content = lm.GetString("LC.Backup.Create");
            btnRefreshHistory.Content = lm.GetString("LC.Backup.RefreshHistory");
            btnRestoreBackup.Content = lm.GetString("LC.Backup.RestoreSelected");
            txtBackupHistoryTitle.Text = lm.GetString("LC.Backup.History");

            lblCompareLeft.Text = lm.GetString("LC.Compare.LeftFile");
            lblCompareRight.Text = lm.GetString("LC.Compare.RightFile");
            btnBrowseLeft.Content = lm.GetString("LC.Common.Browse");
            btnBrowseRight.Content = lm.GetString("LC.Common.Browse");
            btnCompare.Content = lm.GetString("LC.Compare.Run");
            btnExportXml.Content = lm.GetString("LC.Compare.ExportXml");
            lblFilter.Text = lm.GetString("LC.Compare.Filter");

            colCategory.Header = lm.GetString("LC.Col.Category");
            colPath.Header = lm.GetString("LC.Col.Path");
            colName.Header = lm.GetString("LC.Col.Name");
            colLeft.Header = lm.GetString("LC.Col.Left");
            colRight.Header = lm.GetString("LC.Col.Right");
            colStatus.Header = lm.GetString("LC.Col.Status");

            SetFilterItemText(0, lm.GetString("LC.Filter.All"));
            SetFilterItemText(1, lm.GetString("LC.Filter.Diff"));
            SetFilterItemText(2, lm.GetString("LC.Filter.Left"));
            SetFilterItemText(3, lm.GetString("LC.Filter.Right"));
            SetFilterItemText(4, lm.GetString("LC.Filter.Same"));
        }

        private void SetFilterItemText(int index, string content)
        {
            if (cmbCompareFilter == null || index < 0 || index >= cmbCompareFilter.Items.Count) return;
            var item = cmbCompareFilter.Items[index] as ComboBoxItem;
            if (item != null) item.Content = content;
        }

        private sealed class CompareRow
        {
            public string Category { get; set; }
            public string Path { get; set; }
            public string Name { get; set; }
            public string Left { get; set; }
            public string Right { get; set; }
            public string Status { get; set; }
        }

        private sealed class BackupItem
        {
            public string FilePath { get; set; }
            public string Display { get; set; }
        }
    }
}
