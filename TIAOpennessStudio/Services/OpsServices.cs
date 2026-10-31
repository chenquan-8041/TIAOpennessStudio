using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services
{
    public class ReleaseInfo
    {
        public string Version { get; set; }
        public DateTime ReleasedAt { get; set; }
        public string Changelog { get; set; }
        public string ChangeType { get; set; }
        public List<string> BreakingChanges { get; set; } = new List<string>();
        public Dictionary<string, string> Compatibility { get; set; } = new Dictionary<string, string>();
        public bool IsRollbackTarget { get; set; }
    }

    public class HealthStatus
    {
        public bool IsHealthy { get; set; }
        public DateTime LastCheck { get; set; } = DateTime.UtcNow;
        public int RestartCount { get; set; }
        public DateTime? LastRestart { get; set; }
        public long UptimeSeconds { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
        public Dictionary<string, string> ComponentStatus { get; set; } = new Dictionary<string, string>();
    }

    public class LogRotationPolicy
    {
        public int MaxFileSizeMb { get; set; } = 10;
        public int MaxBackupFiles { get; set; } = 5;
        public string[] LogPatterns { get; set; } = new[] { "*.log" };
        public bool CompressOldLogs { get; set; } = true;
        public int RetentionDays { get; set; } = 30;
    }

    public class BackendReleaseService : IBackendReleaseService
    {
        private readonly string _releaseDir;
        private readonly List<ReleaseInfo> _releases = new List<ReleaseInfo>();
        private ReleaseInfo _currentRelease;

        public BackendReleaseService(string appBaseDirectory)
        {
            _releaseDir = Path.Combine(appBaseDirectory, "Backends");
            Directory.CreateDirectory(_releaseDir);
            InitializeReleases();
        }

        private void InitializeReleases()
        {
            _releases.Add(new ReleaseInfo
            {
                Version = "1.0.0",
                ReleasedAt = DateTime.UtcNow.AddDays(-30),
                Changelog = "Initial release",
                ChangeType = "major"
            });

            _releases.Add(new ReleaseInfo
            {
                Version = "1.1.0",
                ReleasedAt = DateTime.UtcNow.AddDays(-14),
                Changelog = "- Added streaming support\n- Improved error handling\n- Added metrics endpoint",
                ChangeType = "minor"
            });

            _releases.Add(new ReleaseInfo
            {
                Version = "1.2.0",
                ReleasedAt = DateTime.UtcNow.AddDays(-7),
                Changelog = "- Added SLO tracking\n- Added Eval Harness\n- Improved quality gate",
                ChangeType = "minor"
            });

            _releases.Add(new ReleaseInfo
            {
                Version = "1.2.1",
                ReleasedAt = DateTime.UtcNow.AddDays(-1),
                Changelog = "- Bug fix: Fixed streaming interruption issue\n- Performance improvement",
                ChangeType = "patch"
            });

            _currentRelease = _releases.Last();
        }

        public ReleaseInfo GetCurrentRelease() => _currentRelease;

        public List<ReleaseInfo> GetReleaseHistory() => _releases.OrderByDescending(r => r.ReleasedAt).ToList();

        public bool SwitchVersion(string version)
        {
            var target = _releases.FirstOrDefault(r => r.Version == version);
            if (target == null) return false;
            
            _currentRelease = target;
            return true;
        }

        public string GetReleaseNotes(string version)
        {
            var release = _releases.FirstOrDefault(r => r.Version == version);
            if (release == null) return "Version not found";

            var sb = new StringBuilder();
            sb.AppendLine($"# Release {release.Version}");
            sb.AppendLine($"Released: {release.ReleasedAt:yyyy-MM-dd}");
            sb.AppendLine($"Type: {release.ChangeType}");
            sb.AppendLine();
            sb.AppendLine("## Changelog");
            sb.AppendLine(release.Changelog);

            if (release.BreakingChanges.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Breaking Changes");
                foreach (var change in release.BreakingChanges)
                {
                    sb.AppendLine($"- {change}");
                }
            }

            if (release.Compatibility.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Compatibility");
                foreach (var kv in release.Compatibility)
                {
                    sb.AppendLine($"- {kv.Key}: {kv.Value}");
                }
            }

            return sb.ToString();
        }
    }

    public class HealthMonitorService : IHealthMonitorService
    {
        private readonly IBackendHostService _backendHost;
        private readonly ISloService _sloService;
        private readonly object _lock = new object();
        private HealthStatus _currentStatus = new HealthStatus();
        private DateTime _startTime = DateTime.UtcNow;
        private int _consecutiveFailures = 0;
        private const int MaxConsecutiveFailures = 3;
        private const int RestartCooldownSeconds = 60;

        public HealthMonitorService(IBackendHostService backendHost, ISloService sloService)
        {
            _backendHost = backendHost;
            _sloService = sloService;
            _currentStatus.LastCheck = DateTime.UtcNow;
        }

        public HealthStatus GetCurrentStatus()
        {
            lock (_lock)
            {
                _currentStatus.UptimeSeconds = (long)(DateTime.UtcNow - _startTime).TotalSeconds;
                return _currentStatus;
            }
        }

        public async Task<HealthStatus> CheckHealthAsync()
        {
            var status = new HealthStatus();
            var issues = new List<string>();

            try
            {
                var isHealthy = await _backendHost.IsHealthyAsync();
                status.IsHealthy = isHealthy;
                status.ComponentStatus["python_backend"] = isHealthy ? "healthy" : "unhealthy";

                if (!isHealthy)
                {
                    issues.Add("Python backend health check failed");
                    _consecutiveFailures++;
                }
                else
                {
                    _consecutiveFailures = 0;
                }

                var metrics = await _backendHost.GetMetricsAsync();
                if (!string.IsNullOrEmpty(metrics))
                {
                    status.ComponentStatus["python_backend_metrics"] = "available";
                }

                var sloResult = _sloService.EvaluateSlos(DateTime.UtcNow.AddHours(-1));
                if (!sloResult.AllSlosMet)
                {
                    issues.Add($"SLO violations: {sloResult.Violations.Count}");
                }
                status.ComponentStatus["slo"] = sloResult.AllSlosMet ? "healthy" : "degraded";
            }
            catch (Exception ex)
            {
                status.IsHealthy = false;
                issues.Add($"Health check error: {ex.Message}");
                _consecutiveFailures++;
            }

            status.Issues = issues;
            status.LastCheck = DateTime.UtcNow;
            status.RestartCount = _currentStatus.RestartCount;
            status.LastRestart = _currentStatus.LastRestart;
            status.UptimeSeconds = (long)(DateTime.UtcNow - _startTime).TotalSeconds;

            lock (_lock)
            {
                _currentStatus = status;
            }

            return status;
        }

        public void RecordRestart()
        {
            lock (_lock)
            {
                _currentStatus.RestartCount++;
                _currentStatus.LastRestart = DateTime.UtcNow;
                _consecutiveFailures = 0;
            }
            _startTime = DateTime.UtcNow;
        }

        public bool ShouldAutoRestart()
        {
            return _consecutiveFailures >= MaxConsecutiveFailures;
        }

        public void PerformHealthCheckLoop(CancellationToken ct)
        {
            Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                        var status = await CheckHealthAsync();

                        if (ShouldAutoRestart())
                        {
                            AppLogger.Log("[HealthMonitor] Too many consecutive failures, attempting restart...");
                            _backendHost.StopOwnedProcess();
                            await Task.Delay(TimeSpan.FromSeconds(RestartCooldownSeconds), ct);
                            await _backendHost.EnsureStartedAsync();
                            RecordRestart();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"[HealthMonitor] Error in health check loop: {ex.Message}");
                    }
                }
            }, ct);
        }
    }

    public interface ILogRotationService
    {
        void RotateLogs(string logDirectory);
        void CleanupOldLogs(string logDirectory, int retentionDays);
        LogRotationPolicy GetPolicy();
        void SetPolicy(LogRotationPolicy policy);
    }

    public class LogRotationService : ILogRotationService
    {
        private LogRotationPolicy _policy = new LogRotationPolicy();

        public void RotateLogs(string logDirectory)
        {
            if (!Directory.Exists(logDirectory)) return;

            foreach (var pattern in _policy.LogPatterns)
            {
                var files = Directory.GetFiles(logDirectory, pattern);
                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var sizeMb = fileInfo.Length / (1024.0 * 1024.0);

                        if (sizeMb >= _policy.MaxFileSizeMb)
                        {
                            RotateFile(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"[LogRotation] Error rotating {file}: {ex.Message}");
                    }
                }
            }
        }

        private void RotateFile(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var extension = Path.GetExtension(filePath);

            for (int i = _policy.MaxBackupFiles - 1; i > 0; i--)
            {
                var oldFile = Path.Combine(directory, $"{fileName}.{i}{extension}");
                var newFile = Path.Combine(directory, $"{fileName}.{i + 1}{extension}");

                if (File.Exists(oldFile))
                {
                    if (i == _policy.MaxBackupFiles - 1)
                    {
                        File.Delete(oldFile);
                    }
                    else
                    {
                        File.Move(oldFile, newFile);
                    }
                }
            }

            var firstBackup = Path.Combine(directory, $"{fileName}.1{extension}");
            File.Move(filePath, firstBackup);

            AppLogger.Log($"[LogRotation] Rotated {filePath} to {firstBackup}");
        }

        public void CleanupOldLogs(string logDirectory, int retentionDays)
        {
            if (!Directory.Exists(logDirectory)) return;

            var cutoff = DateTime.Now.AddDays(-retentionDays);
            var files = Directory.GetFiles(logDirectory, "*.log*");

            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTime < cutoff)
                    {
                        File.Delete(file);
                        AppLogger.Log($"[LogRotation] Deleted old log: {file}");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[LogRotation] Error deleting old log {file}: {ex.Message}");
                }
            }
        }

        public LogRotationPolicy GetPolicy() => _policy;

        public void SetPolicy(LogRotationPolicy policy) => _policy = policy;
    }
}
