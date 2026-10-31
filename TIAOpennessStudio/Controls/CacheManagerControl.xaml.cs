using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Prism.Ioc;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Controls
{
    public partial class CacheManagerControl : UserControl
    {
        private IProjectKnowledgeWorkerHostService _workerHost;
        private readonly DispatcherTimer _refreshTimer;

        public CacheManagerControl()
        {
            InitializeComponent();

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _refreshTimer.Tick += async (s, e) => await RefreshAllAsync();

            Loaded += OnLoaded;
            IsVisibleChanged += OnIsVisibleChanged;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try { _workerHost = App.Services.Resolve<IProjectKnowledgeWorkerHostService>(); }
            catch { _workerHost = null; }

            await RefreshAllAsync();
            if (IsVisible && chkAutoRefresh.IsChecked == true)
                _refreshTimer.Start();
        }

        private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
            {
                // Refresh immediately when reopened, without waiting for the timer
                await RefreshAllAsync();
                if (chkAutoRefresh.IsChecked == true)
                    _refreshTimer.Start();
            }
            else
            {
                _refreshTimer.Stop();
            }
        }

        private async System.Threading.Tasks.Task RefreshAllAsync()
        {
            if (_workerHost == null)
            {
                txtLog.Text = "Worker host service not available.";
                return;
            }

            try
            {
                runUrl.Text = _workerHost.BaseUrl;
                runLogPath.Text = _workerHost.LogFilePath ?? "(unknown)";

                var healthy = await _workerHost.IsHealthyAsync();
                if (healthy)
                {
                    dotStatus.Fill = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                    txtWorkerState.Text = "Worker: Running";
                    btnStart.IsEnabled = false;
                    btnStop.IsEnabled = true;
                    btnRestart.IsEnabled = true;
                }
                else
                {
                    dotStatus.Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71));
                    txtWorkerState.Text = "Worker: Stopped";
                    btnStart.IsEnabled = true;
                    btnStop.IsEnabled = false;
                    btnRestart.IsEnabled = false;
                    runPid.Text = "--";
                    runToken.Text = "--";
                    runCachePath.Text = "--";
                    runIndexState.Text = "--";
                    runBlockCount.Text = "--";
                    runDirtyCount.Text = "--";
                    return;
                }

                var status = await _workerHost.GetStatusAsync();
                runPid.Text = status.OwnedProcessId > 0 ? status.OwnedProcessId.ToString() : "--";
                runCachePath.Text = status.CachePath ?? "--";

                if (!string.IsNullOrWhiteSpace(status.ProcessState))
                {
                    txtWorkerState.Text = "Worker: " + status.ProcessState;
                    dotStatus.Fill = status.ProcessState switch
                    {
                        "indexing" => new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00)),
                        "ready" => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
                        "stale" => new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71)),
                        _ => dotStatus.Fill
                    };
                }

                runToken.Text = Truncate(_workerHost.AuthToken, 20);

                var cacheStatus = await _workerHost.GetCacheStatusAsync();
                runIndexState.Text = cacheStatus.HasIndex
                    ? (cacheStatus.IsStale ? "Stale" : "Ready")
                    : (cacheStatus.IsIndexing ? "Indexing..." : "Missing");
                runBlockCount.Text = cacheStatus.IndexedBlockCount.ToString();
                runDirtyCount.Text = cacheStatus.DirtyItemCount.ToString();

                if (!string.IsNullOrWhiteSpace(cacheStatus.LastIndexedAt))
                    runIndexState.Text += "  (" + Truncate(cacheStatus.LastIndexedAt, 19) + ")";

                var logTail = await _workerHost.GetLogTailAsync(80);
                if (txtLog.Text != logTail)
                {
                    txtLog.Text = logTail;
                    txtLog.ScrollToEnd();
                }
            }
            catch (Exception ex)
            {
                txtLog.Text = "Error refreshing: " + ex.Message;
            }
        }

        #region Button handlers

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_workerHost == null) return;
            btnStart.IsEnabled = false;
            txtActionStatus.Text = "Starting worker...";
            var ok = await _workerHost.EnsureStartedAsync();
            txtActionStatus.Text = ok ? "Worker started." : "Worker start failed.";
            await RefreshAllAsync();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_workerHost == null) return;
            txtActionStatus.Text = "Stopping worker...";
            _workerHost.StopOwnedProcess();
            txtActionStatus.Text = "Worker stopped.";
            _ = RefreshAllAsync();
        }

        private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            if (_workerHost == null) return;
            btnRestart.IsEnabled = false;
            txtActionStatus.Text = "Restarting worker...";
            _workerHost.StopOwnedProcess();
            await System.Threading.Tasks.Task.Delay(1000);
            await _workerHost.EnsureStartedAsync();
            txtActionStatus.Text = "Worker restarted.";
            btnRestart.IsEnabled = true;
            await RefreshAllAsync();
        }

        private async void BtnRebuild_Click(object sender, RoutedEventArgs e)
        {
            if (_workerHost == null) return;
            btnRebuild.IsEnabled = false;
            txtActionStatus.Text = "Rebuilding index...";

            var progress = Prism.Ioc.ContainerLocator.Container?.Resolve<IGlobalProgressService>();
            progress?.Show("Rebuilding project knowledge index...");
            try
            {
                var result = await _workerHost.RebuildAsync(true);
                txtActionStatus.Text = $"Rebuild: {result.Upserted} upserted, {result.SkippedUnchanged} skipped, {result.Failed} failed.";
            }
            finally
            {
                progress?.Hide();
                btnRebuild.IsEnabled = true;
            }
            await RefreshAllAsync();
        }

        private async void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (_workerHost == null) return;
            var confirm = MessageBox.Show(
                "Clear the entire knowledge cache? This cannot be undone.",
                "Confirm Clear",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            btnClear.IsEnabled = false;
            txtActionStatus.Text = "Clearing cache...";
            var result = await _workerHost.RebuildAsync(true);
            txtActionStatus.Text = $"Cache cleared. Rebuilt: {result.Upserted + result.SkippedUnchanged + result.Failed} blocks.";
            btnClear.IsEnabled = true;
            await RefreshAllAsync();
        }

        private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
        {
            if (_workerHost == null) return;
            var logPath = _workerHost.LogFilePath;
            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                try { Process.Start("notepad.exe", logPath); }
                catch { }
            }
            else
            {
                txtActionStatus.Text = "Log file not found: " + (logPath ?? "null");
            }
        }

        private void BtnOpenDir_Click(object sender, RoutedEventArgs e)
        {
            if (_workerHost == null) return;
            var logDir = Path.GetDirectoryName(_workerHost.LogFilePath);
            if (!string.IsNullOrWhiteSpace(logDir) && Directory.Exists(logDir))
            {
                try { Process.Start("explorer.exe", logDir); }
                catch { }
            }
            else
            {
                txtActionStatus.Text = "Cache directory not found.";
            }
        }

        private async void BtnRefreshLog_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAllAsync();
        }

        #endregion

        private void ChkAutoRefresh_Changed(object sender, RoutedEventArgs e)
        {
            if (_refreshTimer == null) return;
            if (chkAutoRefresh.IsChecked == true)
                _refreshTimer.Start();
            else
                _refreshTimer.Stop();
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }
    }
}
