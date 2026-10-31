using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    public sealed class BackendHostService : IBackendHostService
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)  // Local localhost health checks; 5 seconds is plenty
        };
        private readonly string _baseUrl;
        private readonly string _scriptPath;
        private readonly string _workingDir;
        private readonly object _gate = new object();
        // Serializes EnsureStartedAsync: startup, App init and the AI settings UI can all call it
        // concurrently, and two callers must not both kill/restart the backend.
        private readonly SemaphoreSlim _ensureLock = new SemaphoreSlim(1, 1);
        private Process _ownedBootstrapProcess;

        // Only python processes whose command line contains this marker are ever treated as a
        // (stale) backend and killed. Never widen this: it is the safety boundary for the kill.
        private const string BackendEntryMarker = @"TIAAgent\run.py";
        private System.Threading.Timer _healthCheckTimer;
        private string _authToken;

        // Health monitoring
        private int _consecutiveFailures;
        private const int MaxConsecutiveFailures = 3;
        private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(30);
        private bool _isRestarting;

        /// <summary>Bearer token shared with the Python backend for auth.</summary>
        public string AuthToken => _authToken;

        public BackendHostService(string localBackendUrl, string appBaseDirectory)
        {
            _baseUrl = NormalizeBaseUrl(localBackendUrl);
            var located = LocateBackendScriptRoot(appBaseDirectory);
            _scriptPath = located.scriptPath;
            _workingDir = located.workingDir;
            // Health check timer is started after first successful startup in EnsureStartedAsync
        }

        public async Task<bool> IsHealthyAsync()
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl.TrimEnd('/') + "/health"))
                using (var resp = await Http.SendAsync(req))
                {
                    return resp.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Probes an authenticated endpoint with our current token. Returns true only on an explicit
        /// 401. Network errors, timeouts and any other status are treated as "not rejected" so a
        /// transient failure can never trigger a kill.
        /// </summary>
        private async Task<bool> IsAuthRejectedAsync()
        {
            try
            {
                var token = Environment.GetEnvironmentVariable("TIA_BACKEND_TOKEN");
                if (string.IsNullOrEmpty(token)) token = _authToken;

                // /providers is read-only and sits behind the auth middleware (unlike /health).
                using (var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl.TrimEnd('/') + "/providers"))
                {
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
                    {
                        return resp.StatusCode == HttpStatusCode.Unauthorized;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[BackendHost] Auth probe failed ({ex.GetType().Name}: {ex.Message}); assuming token is fine.");
                return false;
            }
        }

        /// <summary>
        /// Stops a stale backend left over from a previous session so a fresh one can start with a
        /// new token. Kills ONLY a process that (a) is listening on our backend port and (b) has
        /// <see cref="BackendEntryMarker"/> in its command line. Anything else on the port is left alone.
        /// </summary>
        private async Task<bool> ReplaceStaleBackendAsync()
        {
            try
            {
                // Do not kill something we cannot replace.
                if (string.IsNullOrWhiteSpace(_scriptPath) || !File.Exists(_scriptPath))
                {
                    AppLogger.Log("[BackendHost] start_backend.ps1 not found; refusing to stop the existing backend.");
                    return false;
                }

                if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) || uri.Port <= 0)
                {
                    AppLogger.Log($"[BackendHost] Cannot parse backend port from '{_baseUrl}'.");
                    return false;
                }

                var listenerPids = GetListeningPids(uri.Port);
                if (listenerPids.Count == 0)
                {
                    AppLogger.Log($"[BackendHost] Could not identify the process listening on port {uri.Port}.");
                    return false;
                }

                var backendPids = FindBackendProcessIds();
                var targets = new List<int>();
                foreach (var pid in listenerPids)
                {
                    if (backendPids.Contains(pid)) targets.Add(pid);
                }

                if (targets.Count == 0)
                {
                    AppLogger.Log($"[BackendHost] Port {uri.Port} is held by a process that is not '{BackendEntryMarker}'; not touching it.");
                    return false;
                }

                foreach (var pid in targets)
                {
                    AppLogger.Log($"[BackendHost] Stopping stale backend PID {pid}");
                    KillProcessTree(pid);
                }

                // Wait for the port to be released (up to ~6s).
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(300).ConfigureAwait(false);
                    if (!await IsHealthyAsync().ConfigureAwait(false)) return true;
                }

                AppLogger.Log("[BackendHost] Stale backend is still answering after kill.");
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.LogError("[BackendHost] ReplaceStaleBackend failed", ex);
                return false;
            }
        }

        /// <summary>PIDs of python processes whose command line contains the backend entry script.</summary>
        private static HashSet<int> FindBackendProcessIds()
        {
            var result = new HashSet<int>();
            var marker = BackendEntryMarker.Replace('/', '\\');
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE 'python%'"))
                using (var rows = searcher.Get())
                {
                    foreach (ManagementObject row in rows)
                    {
                        using (row)
                        {
                            var cmd = (row["CommandLine"] as string ?? "").Replace('/', '\\');
                            if (cmd.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                                result.Add(Convert.ToInt32(row["ProcessId"]));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[BackendHost] WMI process query failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// PIDs listening on a local TCP port, parsed from `netstat -ano`. Listening sockets are
        /// recognised by a foreign endpoint ending in ":0", not by the (localizable) state text.
        /// </summary>
        private static HashSet<int> GetListeningPids(int port)
        {
            var result = new HashSet<int>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano -p TCP",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);

                    var suffix = ":" + port;
                    foreach (var rawLine in output.Split('\n'))
                    {
                        var parts = rawLine.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!parts[1].EndsWith(suffix, StringComparison.Ordinal)) continue;
                        if (!parts[2].EndsWith(":0", StringComparison.Ordinal)) continue;
                        if (int.TryParse(parts[parts.Length - 1], out var pid) && pid > 0) result.Add(pid);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[BackendHost] netstat failed: {ex.Message}");
            }
            return result;
        }

        private static void KillProcessTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/PID " + pid + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var killer = Process.Start(psi))
                {
                    killer?.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[BackendHost] taskkill failed for PID {pid}: {ex.Message}");
            }
        }

        public async Task<string> GetMetricsAsync()
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl.TrimEnd('/') + "/metrics"))
                using (var resp = await Http.SendAsync(req))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        return await resp.Content.ReadAsStringAsync();
                    }
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> EnsureStartedAsync()
        {
            await _ensureLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await EnsureStartedCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _ensureLock.Release();
            }
        }

        private async Task<bool> EnsureStartedCoreAsync()
        {
            if (await IsHealthyAsync())
            {
                // /health is unauthenticated, so a healthy backend may still reject our token. That
                // happens when a previous app instance left its Python process running: it holds the
                // old token, this instance has a different (or no) one, and every call returns 401.
                if (!await IsAuthRejectedAsync()) return true;

                AppLogger.Log("[BackendHost] Backend is healthy but rejects our token (401): stale backend from a previous session. Replacing it...");
                if (!await ReplaceStaleBackendAsync())
                {
                    AppLogger.Log("[BackendHost] Could not stop the stale backend; leaving it as is.");
                    return false;
                }
            }

            AppLogger.Log("[BackendHost] Starting backend...");

            lock (_gate)
            {
                if (_ownedBootstrapProcess == null || _ownedBootstrapProcess.HasExited)
                {
                    _ownedBootstrapProcess = TryStartBootstrapProcess();
                }
            }

            // Wait for backend to start, max 20s, polling every 300ms
            for (int i = 0; i < 66; i++)
            {
                await Task.Delay(300);
                if (await IsHealthyAsync())
                {
                    AppLogger.Log($"[BackendHost] Ready after {(i + 1) * 0.3:F1}s");

                    // Start ongoing health check timer
                    if (_healthCheckTimer == null)
                    {
                        _healthCheckTimer = new System.Threading.Timer(
                            async _ => await PerformHealthCheckAsync(),
                            null,
                            HealthCheckInterval,
                            HealthCheckInterval);
                    }
                    _consecutiveFailures = 0;

                    return true;
                }
            }

            AppLogger.Log("[BackendHost] Startup timeout");
            return false;
        }

        public async Task<bool> EnsureStartedAsync(IProgress<string> progress)
        {
            return await EnsureStartedAsync();
        }

        public void StopOwnedProcess()
        {
            if (_healthCheckTimer != null)
            {
                _healthCheckTimer.Dispose();
                _healthCheckTimer = null;
            }

            _authToken = null;

            Process p = null;
            lock (_gate)
            {
                p = _ownedBootstrapProcess;
                _ownedBootstrapProcess = null;
            }
            if (p == null) return;

            try
            {
                if (!p.HasExited)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/PID " + p.Id + " /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (var killer = Process.Start(psi))
                    {
                        killer?.WaitForExit(3000);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[BackendHost] taskkill failed for PID {p?.Id}: {ex.Message}");
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }

        private async Task PerformHealthCheckAsync()
        {
            if (_isRestarting) return;

            bool healthy = await IsHealthyAsync();

            if (healthy)
            {
                if (_consecutiveFailures > 0)
                {
                    AppLogger.Log($"[BackendHost] Health recovered after {_consecutiveFailures} failure(s)");
                }
                _consecutiveFailures = 0;

                // Collect metrics periodically on successful health checks
                var metrics = await GetMetricsAsync();
                if (!string.IsNullOrEmpty(metrics))
                {
                    AppLogger.Log($"[BackendMetrics] {metrics}");
                }
                return;
            }

            _consecutiveFailures++;
            AppLogger.Log($"[BackendHost] Health check failed ({_consecutiveFailures}/{MaxConsecutiveFailures})");

            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                await RestartBackendAsync();
            }
        }

        private async Task RestartBackendAsync()
        {
            _isRestarting = true;
            try
            {
                AppLogger.Log("[BackendHost] Initiating backend restart...");
                StopOwnedProcess();

                // Wait for port release
                await Task.Delay(3000);

                bool started = await EnsureStartedAsync();
                if (started)
                {
                    _consecutiveFailures = 0;
                    AppLogger.Log("[BackendHost] Restart successful");
                }
                else
                {
                    AppLogger.Log("[BackendHost] Restart failed - timeout waiting for startup");
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("[BackendHost] Restart exception", ex);
            }
            finally
            {
                _isRestarting = false;
            }
        }

        private Process TryStartBootstrapProcess()
        {
            if (string.IsNullOrWhiteSpace(_scriptPath) || !File.Exists(_scriptPath))
            {
                AppLogger.Log("[BackendHost] start_backend.ps1 not found");
                return null;
            }

            try
            {
                // Generate a random auth token shared with the Python backend
                _authToken = Guid.NewGuid().ToString("N");

                // Set token on current process so RestAICodeGenerator can read it
                Environment.SetEnvironmentVariable("TIA_BACKEND_TOKEN", _authToken);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + _scriptPath + "\"",
                    WorkingDirectory = _workingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                psi.EnvironmentVariables["TIA_BACKEND_TOKEN"] = _authToken;

                return Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("[BackendHost] Start failed", ex);
                return null;
            }
        }

        private static string NormalizeBaseUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "http://127.0.0.1:8000";
            var u = url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                u = "http://" + u;
            }
            if (u.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                u = u.Replace("localhost", "127.0.0.1");
            }
            return u.TrimEnd('/');
        }

        private static (string scriptPath, string workingDir) LocateBackendScriptRoot(string baseDirectory)
        {
            var dir = new DirectoryInfo(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 12 && dir != null; i++)
            {
                // Primary: script at repo root (legacy)
                var candidateScript = Path.Combine(dir.FullName, "start_backend.ps1");
                var candidateMain = Path.Combine(dir.FullName, "TIAAgent", "run.py");
                if (File.Exists(candidateScript) && File.Exists(candidateMain))
                {
                    return (candidateScript, dir.FullName);
                }

                // Secondary: script moved to utils/ subdirectory
                // Keep it there; moving it back to the repo root breaks startup
                var utilsScript = Path.Combine(dir.FullName, "utils", "start_backend.ps1");
                if (File.Exists(utilsScript) && File.Exists(candidateMain))
                {
                    return (utilsScript, dir.FullName);
                }

                dir = dir.Parent;
            }
            return (string.Empty, string.Empty);
        }
    }
}
