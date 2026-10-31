using System;
using System.Threading.Tasks;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Traces LLM requests, tool executions, and errors for an agent session.
    /// </summary>
    public interface IAgentTracer
    {
        void LogLLMRequest(string prompt, object parameters);
        void LogToolExecution(string toolName, string arguments, string result, TimeSpan duration);
        void LogError(Exception ex, string contextPhase);
        void ExportSession(string filepath);
    }
}
