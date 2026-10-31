using System;
using System.IO;

namespace TIAOpennessStudio
{
    /// <summary>
    /// Console-only AppLogger for the Worker process.
    /// No WPF dependencies. Writes to Console and optional log file.
    /// </summary>
    public static class AppLogger
    {
        private static readonly object _gate = new object();
        private static string _logFilePath;

        public static void Initialize(string logFilePath = null)
        {
            _logFilePath = logFilePath;
            if (!string.IsNullOrWhiteSpace(_logFilePath))
            {
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
            }
        }

        public static void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string formatted = $"[{timestamp}] {message}";

            Console.WriteLine(formatted);
            System.Diagnostics.Debug.WriteLine(formatted);

            if (!string.IsNullOrWhiteSpace(_logFilePath))
            {
                lock (_gate)
                {
                    try { File.AppendAllText(_logFilePath, formatted + Environment.NewLine); }
                    catch { }
                }
            }
        }

        public static void LogWarning(string message)
        {
            Log($"WARN: {message}");
        }

        public static void LogError(string message, Exception ex = null)
        {
            string err = $"ERROR: {message}";
            if (ex != null) err += $" | Details: {ex.Message}";
            Log(err);
        }
    }
}
