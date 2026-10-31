using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TIAOpennessStudio
{
    /// <summary>
    /// Thread-safe application logger.
    ///
    /// Design rules (UI-freeze prevention):
    ///   - Log() is called from ANY thread (UI, background, async continuations).
    ///   - UI updates use Dispatcher.BeginInvoke (async, fire-and-forget) so the
    ///     caller is NEVER blocked waiting for the UI thread.
    ///   - Lines are batched: up to <see cref="MaxBatchSize"/> pending lines are
    ///     flushed in a single AppendText call, capping the per-tick UI work.
    ///   - File I/O runs under a lock on the calling thread; it is intentionally
    ///     synchronous because callers are already off the UI thread (background
    ///     workers, async tasks).  If that ever changes, move WriteToFile to a
    ///     dedicated writer thread.
    /// </summary>
    public static class AppLogger
    {
        private static TextBox _logTextBox;
        private static Dispatcher _dispatcher;
        private static readonly List<string> _buffer = new List<string>();
        private static readonly object _gate = new object();
        private static bool _initialized;

        // Pending lines waiting to be flushed to the TextBox.
        private static readonly ConcurrentQueue<string> _pendingLines = new ConcurrentQueue<string>();
        // Whether a BeginInvoke flush is already queued (avoid queuing many redundant flushes).
        private static volatile bool _flushQueued;
        // Maximum lines to append in one UI-thread flush to avoid janky frames.
        private const int MaxBatchSize = 50;

        // File logging
        private static string _logFilePath;
        private static readonly object _fileGate = new object();
        private const int MaxLogFileSize = 5 * 1024 * 1024; // 5 MB
        private const int MaxLogFiles = 3;

        public static void Initialize(TextBox logTextBox, Dispatcher dispatcher, string logDirectory = null)
        {
            _logTextBox = logTextBox;
            _dispatcher = dispatcher;
            _initialized = true;

            // Setup file logging
            if (!string.IsNullOrWhiteSpace(logDirectory))
            {
                try
                {
                    Directory.CreateDirectory(logDirectory);
                    _logFilePath = Path.Combine(logDirectory, "app.log");
                    WriteToFile("=== TIAOpennessStudio session started ===");
                }
                catch
                {
                    // File logging is best-effort; never crash on IO errors
                    _logFilePath = null;
                }
            }

            // Flush early-buffered logs (collected before Initialize was called)
            lock (_gate)
            {
                foreach (var msg in _buffer)
                    _pendingLines.Enqueue(msg);
                _buffer.Clear();
            }
            ScheduleFlush();
        }

        public static void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string formatted = $"[{timestamp}] {message}";

            if (!_initialized || _logTextBox == null || _dispatcher == null)
            {
                // Buffer early logs (before UI is ready)
                lock (_gate) { _buffer.Add(formatted); }
                System.Diagnostics.Debug.WriteLine(formatted);
                WriteToFile(formatted);
                return;
            }

            // Enqueue for async UI update — never block the caller.
            _pendingLines.Enqueue(formatted);
            ScheduleFlush();

            // File I/O is fast and off-UI-thread in normal operation; keep it synchronous.
            WriteToFile(formatted);
        }

        public static void LogWarning(string message)
        {
            Log($"WARN: {message}");
        }

        public static void LogError(string message, Exception ex = null)
        {
            string err = $"ERROR: {message}";
            if (ex != null)
            {
                err += $" | Details: {ex.Message}";
                WriteToFile($"{err}\n  StackTrace: {ex.StackTrace}");
            }
            Log(err);
        }

        // ── Private helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Queue a single async flush on the UI thread, unless one is already pending.
        /// Uses DispatcherPriority.Background so it yields to user input.
        /// </summary>
        private static void ScheduleFlush()
        {
            if (_flushQueued) return;
            _flushQueued = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushToTextBox));
        }

        /// <summary>
        /// Called on the UI thread. Drains up to <see cref="MaxBatchSize"/> pending
        /// lines into the TextBox in one AppendText call.
        /// </summary>
        private static void FlushToTextBox()
        {
            _flushQueued = false;
            if (_logTextBox == null) return;

            var batch = new System.Text.StringBuilder();
            int count = 0;
            while (count < MaxBatchSize && _pendingLines.TryDequeue(out var line))
            {
                batch.Append(line).Append("\r\n");
                count++;
            }

            if (count == 0) return;

            _logTextBox.AppendText(batch.ToString());
            _logTextBox.ScrollToEnd();

            // If more lines are waiting, schedule another flush.
            if (!_pendingLines.IsEmpty)
                ScheduleFlush();
        }

        private static void WriteToFile(string line)
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;

            try
            {
                lock (_fileGate)
                {
                    // Rotate if file exceeds max size
                    var fileInfo = new FileInfo(_logFilePath);
                    if (fileInfo.Exists && fileInfo.Length >= MaxLogFileSize)
                    {
                        RotateLogFiles();
                    }

                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Best-effort; silently ignore file I/O errors
            }
        }

        private static void RotateLogFiles()
        {
            try
            {
                // Shift: .2 → .3, .1 → .2, .log → .1
                for (int i = MaxLogFiles - 1; i >= 1; i--)
                {
                    var oldPath = _logFilePath + "." + i;
                    var newPath = _logFilePath + "." + (i + 1);
                    if (File.Exists(oldPath))
                    {
                        if (File.Exists(newPath))
                            File.Delete(newPath);
                        File.Move(oldPath, newPath);
                    }
                }

                // Rotate current log to .1
                if (File.Exists(_logFilePath))
                {
                    var rotatedPath = _logFilePath + ".1";
                    if (File.Exists(rotatedPath))
                        File.Delete(rotatedPath);
                    File.Move(_logFilePath, rotatedPath);
                }
            }
            catch
            {
                // Best-effort rotation; continue logging even if rotation fails
            }
        }
    }
}
