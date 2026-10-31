using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Manages backend release versions, history, and switching.
    /// </summary>
    public interface IBackendReleaseService
    {
        ReleaseInfo GetCurrentRelease();
        List<ReleaseInfo> GetReleaseHistory();
        bool SwitchVersion(string version);
        string GetReleaseNotes(string version);
    }

    /// <summary>
    /// Monitors backend health and performs restart and auto-recovery.
    /// </summary>
    public interface IHealthMonitorService
    {
        HealthStatus GetCurrentStatus();
        Task<HealthStatus> CheckHealthAsync();
        void RecordRestart();
        bool ShouldAutoRestart();
        void PerformHealthCheckLoop(CancellationToken ct);
    }
}
