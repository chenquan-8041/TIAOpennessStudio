using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Records agent execution traces for the Dev Mode debugging panel.
    /// </summary>
    public class AgentTracer : IAgentTracer
    {
        private readonly string _logFilePath;
        private static readonly object _lock = new object();
        private readonly List<object> _currentSessionLogs = new List<object>();

        public AgentTracer()
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
            _logFilePath = Path.Combine(logDir, "agent-trace.jsonl");
        }

        private void WriteLog(object logEntry)
        {
            try
            {
                string json = JsonSerializer.Serialize(logEntry, new JsonSerializerOptions { WriteIndented = false });
                
                lock (_lock)
                {
                    _currentSessionLogs.Add(logEntry);
                    File.AppendAllText(_logFilePath, json + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentTracer] Failed to write log: {ex.Message}");
            }
        }

        public void LogLLMRequest(string prompt, object parameters)
        {
            WriteLog(new
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Type = "LLMRequest",
                Prompt = prompt,
                Parameters = parameters
            });
        }

        public void LogToolExecution(string toolName, string arguments, string result, TimeSpan duration)
        {
            WriteLog(new
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Type = "ToolExecution",
                ToolName = toolName,
                Arguments = arguments,
                Result = result,
                DurationMs = duration.TotalMilliseconds
            });
        }

        public void LogError(Exception ex, string contextPhase)
        {
            WriteLog(new
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Type = "Error",
                ContextPhase = contextPhase,
                Message = ex.Message,
                StackTrace = ex.StackTrace
            });
        }

        public void ExportSession(string filepath)
        {
            try
            {
                lock (_lock)
                {
                    string json = JsonSerializer.Serialize(_currentSessionLogs, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(filepath, json);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentTracer] Failed to export session: {ex.Message}");
            }
        }
    }
}
