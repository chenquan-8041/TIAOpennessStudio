using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Unified log service: periodically tails the Python backend and Worker
    /// process logs and forwards them to AppLogger.
    ///
    /// UI-freeze fix: file I/O is done on a background thread (Task.Run).
    /// Only AppLogger.Log calls end up on the UI thread, and those are already
    /// async (BeginInvoke) after the AppLogger rewrite.
    /// The DispatcherTimer is kept only as a lightweight background tick driver;
    /// the actual work is immediately offloaded via Task.Run.
    /// </summary>
    public class UnifiedLogService
    {
        private readonly DispatcherTimer _timer;
        private readonly string _pythonLogPath;
        private long _pythonLastPosition;
        private string _workerLastLog = "";
        private readonly IProjectKnowledgeWorkerHostService _workerHost;

        // Prevent overlapping ticks if I/O takes longer than the interval.
        private int _tickRunning = 0;

        public UnifiedLogService(IProjectKnowledgeWorkerHostService workerHost = null)
        {
            _workerHost = workerHost;
            _pythonLogPath = FindPythonLogPath();
            _pythonLastPosition = 0;

            if (!string.IsNullOrEmpty(_pythonLogPath) && File.Exists(_pythonLogPath))
            {
                _pythonLastPosition = new FileInfo(_pythonLogPath).Length;
                AppLogger.Log("[UnifiedLog] Python log found: " + _pythonLogPath);
            }
            else
            {
                AppLogger.Log("[UnifiedLog] Python log not found: " + (_pythonLogPath ?? "null"));
            }

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _timer.Tick += OnTick;
        }

        public void Start()
        {
            _timer.Start();
            AppLogger.Log("[UnifiedLog] Started polling (2s interval)");
        }

        public void Stop()
        {
            _timer.Stop();
        }

        /// <summary>
        /// Fired on the UI thread by DispatcherTimer, but ALL real work is
        /// immediately handed off to Task.Run so the UI thread is free instantly.
        /// </summary>
        private void OnTick(object sender, EventArgs e)
        {
            // Skip if a previous tick is still running (avoid pile-up on slow disk).
            if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0)
                return;

            Task.Run(async () =>
            {
                try
                {
                    TailPythonLog();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("[UnifiedLog] Python tail error: " + ex.Message);
                }

                try
                {
                    await TailWorkerLogAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("[UnifiedLog] Worker tail error: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _tickRunning, 0);
                }
            });
        }

        /// <summary>
        /// Reads new lines from the Python backend log file.
        /// Runs on a background thread — no UI calls here; AppLogger.Log handles
        /// the async dispatch internally.
        /// </summary>
        private void TailPythonLog()
        {
            if (string.IsNullOrEmpty(_pythonLogPath) || !File.Exists(_pythonLogPath))
                return;

            var fi = new FileInfo(_pythonLogPath);
            if (fi.Length <= _pythonLastPosition)
            {
                // File was rotated or truncated; reset the position
                if (fi.Length < _pythonLastPosition)
                    _pythonLastPosition = 0;
                return;
            }

            using (var fs = new FileStream(_pythonLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(_pythonLastPosition, SeekOrigin.Begin);
                // leaveOpen:true keeps fs open after reader disposes, so we can
                // read fs.Position reliably after all lines are consumed.
                using (var reader = new StreamReader(fs, System.Text.Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            AppLogger.Log("[Python] " + line);
                    }
                }
                // Seek to end to get the true file position regardless of
                // StreamReader's internal read-ahead buffer size.
                _pythonLastPosition = fs.Seek(0, SeekOrigin.End);
            }
        }

        private async Task TailWorkerLogAsync()
        {
            if (_workerHost == null)
                return;

            try
            {
                var logTail = await _workerHost.GetLogTailAsync(30).ConfigureAwait(false);
                if (logTail == null || logTail == _workerLastLog)
                    return;

                // Only emit newly appended lines
                var newLines = ExtractNewLines(_workerLastLog, logTail);
                _workerLastLog = logTail;

                foreach (var line in newLines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        AppLogger.Log("[Worker] " + line.TrimEnd());
                }
            }
            catch
            {
                // Worker may not be started yet; stay silent
            }
        }

        private static string[] ExtractNewLines(string oldText, string newText)
        {
            if (string.IsNullOrEmpty(oldText))
                return (newText ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            if (newText == null || newText.Length <= oldText.Length)
                return Array.Empty<string>();

            var diff = newText.Substring(oldText.Length);
            return diff.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string FindPythonLogPath()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 12 && dir != null; i++)
            {
                var candidate = Path.Combine(dir.FullName, "TIAAgent", "logs", "backend.log");
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
