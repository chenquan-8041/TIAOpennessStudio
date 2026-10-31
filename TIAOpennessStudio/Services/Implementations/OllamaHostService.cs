using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Manages the local Ollama process and wraps its HTTP API (health, model
    /// listing, pull/delete, and size/details queries).
    /// </summary>
    public sealed class OllamaHostService : IOllamaHostService
    {
        private static readonly HttpClient Http = new HttpClient();  // No global timeout; each method uses CancellationToken as needed
        private readonly string _baseUrl;
        private readonly string _ollamaPath;
        private readonly object _gate = new object();
        private Process _ownedOllamaProcess;

        public OllamaHostService(string baseUrl = "http://localhost:11434")
        {
            _baseUrl = NormalizeBaseUrl(baseUrl);
            _ollamaPath = LocateOllamaExecutable();
        }

        public async Task<bool> IsHealthyAsync()
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                using (var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl.TrimEnd('/') + "/api/tags"))
                using (var resp = await Http.SendAsync(req, cts.Token))
                {
                    return resp.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> EnsureStartedAsync()
        {
            // Fast check: return within ~3 s if it is already running
            if (await IsHealthyAsync()) return true;

            lock (_gate)
            {
                if (_ownedOllamaProcess == null || _ownedOllamaProcess.HasExited)
                {
                    _ownedOllamaProcess = TryStartOllamaProcess();
                }
            }

            // Wait for the service to start (up to 30 s, each poll has a 3 s timeout)
            for (int i = 0; i < 60; i++)
            {
                if (await IsHealthyAsync())
                {
                    AppLogger.Log("[OllamaHost] Ollama service is ready.");
                    return true;
                }
                await Task.Delay(500);
            }

            AppLogger.Log("[OllamaHost] Ollama service failed to start within timeout.");
            return false;
        }

        public async Task<List<string>> GetInstalledModelsAsync()
        {
            var result = new List<string>();
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl.TrimEnd('/') + "/api/tags"))
                using (var resp = await Http.SendAsync(req))
                {
                    if (!resp.IsSuccessStatusCode) return result;

                    var json = await resp.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    var models = doc.RootElement.GetProperty("models");

                    foreach (var m in models.EnumerateArray())
                    {
                        var modelName = m.GetProperty("name").GetString();
                        if (!string.IsNullOrEmpty(modelName))
                        {
                            result.Add(modelName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OllamaHost] Failed to get installed models: {ex.Message}");
            }
            return result;
        }

        public async Task<bool> IsModelAvailableAsync(string model)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl.TrimEnd('/') + "/api/tags"))
                using (var resp = await Http.SendAsync(req))
                {
                    if (!resp.IsSuccessStatusCode) return false;

                    var json = await resp.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    var models = doc.RootElement.GetProperty("models");

                    foreach (var m in models.EnumerateArray())
                    {
                        var modelName = m.GetProperty("name").GetString();
                        if (modelName != null && modelName.StartsWith(model, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OllamaHost] Failed to check model availability: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Get model size in bytes from Ollama API (/api/show).
        /// Returns 0 if the model is not found or API fails.
        /// </summary>
        public async Task<long> GetModelSizeAsync(string model)
        {
            try
            {
                var requestBody = new { name = model };
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl.TrimEnd('/') + "/api/show"))
                {
                    req.Content = content;
                    using (var resp = await Http.SendAsync(req))
                    {
                        if (!resp.IsSuccessStatusCode) return 0;

                        var respJson = await resp.Content.ReadAsStringAsync();
                        var doc = JsonDocument.Parse(respJson);
                        if (doc.RootElement.TryGetProperty("size", out var sizeEl))
                            return sizeEl.GetInt64();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OllamaHost] Failed to get model size for '{model}': {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// Get model details (parameter count, quantization, context length, etc.).
        /// Returns a human-readable string or empty on failure.
        /// </summary>
        public async Task<string> GetModelDetailsAsync(string model)
        {
            try
            {
                var requestBody = new { name = model };
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl.TrimEnd('/') + "/api/show"))
                {
                    req.Content = content;
                    using (var resp = await Http.SendAsync(req))
                    {
                        if (!resp.IsSuccessStatusCode) return "";

                        var respJson = await resp.Content.ReadAsStringAsync();
                        var doc = JsonDocument.Parse(respJson);
                        var sb = new StringBuilder();

                        // Parameter count → e.g. "3B"
                        if (doc.RootElement.TryGetProperty("details", out var details))
                        {
                            if (details.TryGetProperty("parameter_size", out var ps))
                                sb.Append(ps.GetString() ?? "");
                            if (details.TryGetProperty("quantization_level", out var ql))
                                sb.Append($" ({ql.GetString()})");
                        }
                        // Family / base model
                        if (doc.RootElement.TryGetProperty("model_info", out var info))
                        {
                            if (sb.Length > 0) sb.Append(" · ");
                            if (info.TryGetProperty("general.architecture", out var arch))
                                sb.Append($"arch: {arch.GetString()}");
                        }
                        return sb.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OllamaHost] Failed to get model details for '{model}': {ex.Message}");
            }
            return "";
        }

        /// <summary>
        /// Delete an installed model via Ollama API.
        /// </summary>
        public async Task<bool> DeleteModelAsync(string model)
        {
            try
            {
                var requestBody = new { name = model };
                var json = JsonSerializer.Serialize(requestBody);
                var request = new HttpRequestMessage(HttpMethod.Delete, _baseUrl.TrimEnd('/') + "/api/delete")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                using (var resp = await Http.SendAsync(request))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        AppLogger.Log($"[OllamaHost] Model '{model}' deleted successfully.");
                        return true;
                    }
                    var err = await resp.Content.ReadAsStringAsync();
                    AppLogger.Log($"[OllamaHost] Failed to delete model '{model}': {resp.StatusCode} — {err}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OllamaHost] Error deleting model '{model}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Pick the best model from the installed list that fits within the given memory budget.
        /// Returns the model name, or empty string if none fit.
        /// </summary>
        public async Task<string> PickBestModelAsync(long availableMemoryBytes)
        {
            var installed = await GetInstalledModelsAsync();
            if (installed.Count == 0) return "";

            // Conservative size estimates (bytes) for models whose API doesn't report size.
            // The real size is fetched first via /api/show; this table is only a fallback.
            var sizeEstimates = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["qwen2.5:0.5b"]       = 600_000_000,
                ["qwen2.5:1.5b"]       = 1_200_000_000,
                ["qwen2.5:3b"]         = 2_500_000_000,
                ["qwen2.5:7b"]         = 5_000_000_000,
                ["qwen3:1.7b"]         = 1_400_000_000,
                ["qwen3:4b"]           = 3_200_000_000,
                ["qwen3:8b"]           = 6_000_000_000,
                ["qwen3.5:9b"]         = 6_600_000_000,
                ["qwen3.5:14b"]        = 10_000_000_000,
                ["qwen3.8"]            = 6_700_000_000,  // LisyNeko/qwen3.8-9b-coder family
                ["codellama:7b"]       = 4_800_000_000,
                ["llama3.1:8b"]        = 5_500_000_000,
                ["llama3.2:3b"]        = 2_500_000_000,
                ["llama3.2:1b"]        = 800_000_000,
                ["deepseek-r1:1.5b"]   = 1_200_000_000,
                ["deepseek-r1:7b"]     = 5_000_000_000,
                ["deepseek-r1:8b"]     = 5_500_000_000,
                ["phi3:3.8b"]          = 2_800_000_000,
                ["mistral:7b"]         = 5_000_000_000,
            };

            // Reserve 40% of available memory for OS + TIA Portal + Ollama overhead
            var budget = (long)(availableMemoryBytes * 0.6);

            string bestModel = "";
            long bestSize = 0;

            foreach (var modelName in installed)
            {
                // 1) Try exact size from Ollama API (works for all installed models).
                var actualSize = await GetModelSizeAsync(modelName);

                // 2) If API returned 0 (older Ollama / endpoint gap), estimate by prefix.
                if (actualSize <= 0)
                {
                    foreach (var kv in sizeEstimates)
                    {
                        if (modelName.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            actualSize = kv.Value;
                            break;
                        }
                    }
                }

                // 3) If still unknown, use a conservative 8 GB estimate so the model
                //    is not silently skipped — it may still fit the budget.
                if (actualSize <= 0)
                    actualSize = 8_000_000_000;

                if (actualSize <= budget && actualSize > bestSize)
                {
                    bestSize = actualSize;
                    bestModel = modelName;
                }
            }

            if (!string.IsNullOrEmpty(bestModel))
                AppLogger.Log($"[OllamaHost] Best model: {bestModel} (~{bestSize / 1_000_000}MB), budget: {budget / 1_000_000}MB, system: {availableMemoryBytes / 1_000_000}MB");

            return bestModel;
        }

        public async Task<bool> PullModelAsync(string model)
        {
            try
            {
                AppLogger.Log($"[OllamaHost] Pulling model: {model}...");

                var requestBody = new { name = model, stream = false };
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl.TrimEnd('/') + "/api/pull"))
                {
                    req.Content = content;
                    using (var resp = await Http.SendAsync(req))
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            AppLogger.Log($"[OllamaHost] Model {model} pulled successfully.");
                            return true;
                        }
                        else
                        {
                            AppLogger.Log($"[OllamaHost] Failed to pull model {model}: {resp.StatusCode}");
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OllamaHost] Error pulling model: {ex.Message}");
                return false;
            }
        }

        public void StopOwnedProcess()
        {
            Process p = null;
            lock (_gate)
            {
                p = _ownedOllamaProcess;
                _ownedOllamaProcess = null;
            }
            if (p == null) return;

            try
            {
                if (!p.HasExited)
                {
                    AppLogger.Log("[OllamaHost] Stopping Ollama process...");
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
            catch
            {
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }

        private Process TryStartOllamaProcess()
        {
            if (string.IsNullOrWhiteSpace(_ollamaPath) || !File.Exists(_ollamaPath))
            {
                AppLogger.Log("[OllamaHost] Ollama executable not found. Please install Ollama from https://ollama.com/download");
                return null;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ollamaPath,
                    Arguments = "serve",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var p = Process.Start(psi);
                if (p != null)
                {
                    p.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                            AppLogger.Log("[OllamaHost] " + e.Data);
                    };
                    p.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                            AppLogger.Log("[OllamaHost][ERR] " + e.Data);
                    };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    AppLogger.Log("[OllamaHost] Ollama process started.");
                }

                return p;
            }
            catch (Exception ex)
            {
                AppLogger.LogError("OllamaHost start failed", ex);
                return null;
            }
        }

        private static string NormalizeBaseUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "http://127.0.0.1:11434";
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

        private static string LocateOllamaExecutable()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var projectDir = new DirectoryInfo(baseDir);
            
            // Walk upward to find the project root (until a .csproj or solution file is found)
            for (int i = 0; i < 10 && projectDir != null; i++)
            {
                if (File.Exists(Path.Combine(projectDir.FullName, "TIAOpennessStudio.csproj")) ||
                    File.Exists(Path.Combine(projectDir.FullName, "TIAOpennessStudio.sln")))
                {
                    break;
                }
                projectDir = projectDir.Parent;
            }

            if (projectDir != null)
            {
                // Prefer an Ollama binary inside the project directory
                var projectPaths = new[]
                {
                    Path.Combine(projectDir.FullName, "Tools", "Ollama", "ollama.exe"),
                    Path.Combine(projectDir.FullName, "Ollama", "ollama.exe"),
                    Path.Combine(projectDir.FullName, "ollama.exe")
                };

                foreach (var path in projectPaths)
                {
                    if (File.Exists(path))
                    {
                        AppLogger.Log($"[OllamaHost] Found Ollama at: {path}");
                        return path;
                    }
                }
            }

            // Fall back to the system install paths
            var systemPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Ollama", "ollama.exe"),
                "ollama.exe" // assumed to be on PATH
            };

            foreach (var path in systemPaths)
            {
                if (File.Exists(path))
                {
                    AppLogger.Log($"[OllamaHost] Found Ollama at: {path}");
                    return path;
                }
            }

            AppLogger.Log("[OllamaHost] Ollama executable not found. Please place ollama.exe in the project directory (Tools/Ollama/ or root/) or install Ollama from https://ollama.com/download");
            return string.Empty;
        }
    }
}
