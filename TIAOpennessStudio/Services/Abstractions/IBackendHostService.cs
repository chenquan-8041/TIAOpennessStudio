using System;
using System.Threading.Tasks;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Manages the Python backend host process lifecycle and health.
    /// </summary>
    public interface IBackendHostService
    {
        string AuthToken { get; }
        Task<bool> IsHealthyAsync();
        Task<string> GetMetricsAsync();
        Task<bool> EnsureStartedAsync();
        Task<bool> EnsureStartedAsync(IProgress<string> progress);
        void StopOwnedProcess();
    }
}
