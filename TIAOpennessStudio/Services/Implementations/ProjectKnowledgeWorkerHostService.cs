using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    public sealed class ProjectKnowledgeWorkerHostService : IProjectKnowledgeWorkerHostService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        // Rebuild/index operations can take minutes on large projects; the short status client
        // would abort them mid-write and leave the index half-built.
        private static readonly HttpClient HttpLong = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        private readonly object _gate = new object();
        private readonly string _workerExePath;
        private readonly string _authToken;
        private Process _ownedProcess;

        public string BaseUrl { get; }
        public string AuthToken => _authToken;
        public string LogFilePath => GetDefaultLogFilePath();

        public ProjectKnowledgeWorkerHostService(string appBaseDirectory, string baseUrl = null, string authToken = null)
        {
            BaseUrl = NormalizeBaseUrl(baseUrl ?? "http://127.0.0.1:19530");
            _workerExePath = LocateWorkerExecutable(appBaseDirectory);
            _authToken = authToken ?? Guid.NewGuid().ToString("N").Substring(0, 16);
        }

        public async Task<string> GetLogTailAsync(int lines = 50)
        {
            var logPath = LogFilePath;
            if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
                return "(Log file not found: " + (logPath ?? "null") + ")";

            try
            {
                // Read last N lines efficiently
                var allLines = await Task.Run(() =>
                {
                    var result = new List<string>();
                    using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            result.Add(line);
                            if (result.Count > lines * 2) // Keep buffer bounded
                                result.RemoveAt(0);
                        }
                    }
                    // Return last N
                    if (result.Count <= lines) return result;
                    return result.GetRange(result.Count - lines, lines);
                }).ConfigureAwait(false);

                return string.Join(Environment.NewLine, allLines);
            }
            catch (Exception ex)
            {
                return "(Error reading log: " + ex.Message + ")";
            }
        }

        public async Task<bool> IsHealthyAsync()
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/health"))
                using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                {
                    return response.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        public async Task<ProjectKnowledgeWorkerStatus> GetStatusAsync()
        {
            var status = new ProjectKnowledgeWorkerStatus
            {
                WorkerBaseUrl = BaseUrl,
                CachePath = GetDefaultCachePath()
            };

            Process owned;
            lock (_gate)
            {
                owned = _ownedProcess;
            }

            status.IsProcessRunning = owned != null && !owned.HasExited;
            status.OwnedProcessId = status.IsProcessRunning ? owned.Id : 0;

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/status"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            status.Note = "Worker status endpoint returned " + (int)response.StatusCode;
                            return status;
                        }

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        status.IsHealthy = true;
                        status.ProcessState = GetString(root, "process_state");
                        status.TiaState = GetString(root, "tia_state");
                        status.ProjectKey = GetString(root, "project_key") ?? string.Empty;
                        status.HasIndex = GetBool(root, "has_index");
                        status.IsStale = GetBool(root, "is_stale");
                        status.IsIndexing = GetBool(root, "is_indexing");
                        status.DirtyItemCount = GetInt(root, "dirty_count");
                        status.IndexedBlockCount = GetInt(root, "indexed_block_count");
                        status.LastIndexedAt = GetString(root, "last_indexed_at");
                        status.GitBranch = GetString(root, "git_branch");
                        status.GitCommit = GetString(root, "git_commit");
                        status.Note = GetString(root, "note");

                        var cachePath = GetString(root, "cache_path");
                        if (!string.IsNullOrWhiteSpace(cachePath))
                            status.CachePath = cachePath;

                        return status;
                    }
                }
            }
            catch (Exception ex)
            {
                status.Note = "Worker status query failed: " + ex.Message;
                return status;
            }
        }

        public async Task<bool> EnsureStartedAsync()
        {
            if (await IsHealthyAsync().ConfigureAwait(false))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(_workerExePath) || !File.Exists(_workerExePath))
            {
                AppLogger.Log("[KnowledgeWorkerHost] Worker executable not found: " + (_workerExePath ?? "<null>"));
                return false;
            }

            lock (_gate)
            {
                if (_ownedProcess == null || _ownedProcess.HasExited)
                {
                    _ownedProcess = TryStartWorkerProcess();
                }
            }

            // Wait up to 15 seconds for worker to start
            for (int i = 0; i < 75; i++)
            {
                await Task.Delay(200).ConfigureAwait(false);
                if (await IsHealthyAsync().ConfigureAwait(false))
                {
                    AppLogger.Log("[KnowledgeWorkerHost] Worker is healthy at " + BaseUrl);
                    return true;
                }
            }

            AppLogger.Log("[KnowledgeWorkerHost] Worker startup timeout.");
            return false;
        }

        public async Task<bool> EnsureStartedAsync(IProgress<string> progress)
        {
            progress?.Report("Starting knowledge worker...");
            var ok = await EnsureStartedAsync().ConfigureAwait(false);
            progress?.Report(ok ? "Knowledge worker ready." : "Knowledge worker startup failed.");
            return ok;
        }

        public void StopOwnedProcess()
        {
            Process process;
            lock (_gate)
            {
                process = _ownedProcess;
                _ownedProcess = null;
            }

            if (process == null) return;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("[KnowledgeWorkerHost] StopOwnedProcess failed: " + ex.Message);
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }

        // ---- Proxy methods to Worker HTTP API ----

        public async Task<ProjectKnowledgeCacheStatus> GetCacheStatusAsync(CancellationToken ct = default)
        {
            var healthy = await IsHealthyAsync().ConfigureAwait(false);
            if (!healthy)
            {
                return new ProjectKnowledgeCacheStatus
                {
                    Status = "worker_offline",
                    Note = "Knowledge worker is not running."
                };
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/index/status"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return new ProjectKnowledgeCacheStatus { Status = "error", Note = "HTTP " + (int)response.StatusCode };

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        return new ProjectKnowledgeCacheStatus
                        {
                            HasIndex = GetBool(root, "has_index"),
                            IsStale = GetBool(root, "is_stale"),
                            IsIndexing = GetBool(root, "is_indexing"),
                            DirtyItemCount = GetInt(root, "dirty_count"),
                            IndexedBlockCount = GetInt(root, "indexed_block_count"),
                            LastIndexedAt = GetString(root, "last_indexed_at") ?? string.Empty,
                            GitBranch = GetString(root, "git_branch") ?? string.Empty,
                            GitCommit = GetString(root, "git_commit") ?? string.Empty,
                            Note = GetString(root, "note") ?? string.Empty,
                            ProgressMessage = GetString(root, "progress_message") ?? string.Empty,
                            ProgressCurrent = root.TryGetProperty("progress_current", out _) ? GetInt(root, "progress_current") : -1,
                            ProgressTotal = root.TryGetProperty("progress_total", out _) ? GetInt(root, "progress_total") : -1,
                            Status = GetBool(root, "is_stale") ? "stale" :
                                     GetBool(root, "has_index") ? "ready" : "missing"
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                return new ProjectKnowledgeCacheStatus { Status = "error", Note = ex.Message };
            }
        }

        public async Task<ProjectKnowledgeCacheStatus> EnsureCacheReadyAsync(
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default)
        {
            var status = await GetCacheStatusAsync(ct).ConfigureAwait(false);
            if (status.Status == "worker_offline" || status.Status == "error")
                return status;

            if (status.HasIndex && !status.IsStale)
                return status;

            // Need to rebuild - call /index/rebuild
            try
            {
                var query = $"?force={(freshness == ProjectKnowledgeFreshnessModes.ForceRefresh ? "true" : "false")}";
                using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/index/rebuild" + query))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    using (var response = await HttpLong.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            status.Note = "Rebuild failed: HTTP " + (int)response.StatusCode;
                            return status;
                        }
                    }
                }

                return await GetCacheStatusAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                status.Note = "Rebuild error: " + ex.Message;
                return status;
            }
        }

        public async Task<List<PlcKnowledgeSearchHit>> SearchKnowledgeAsync(
            string query, int limit = 5,
            string freshness = ProjectKnowledgeFreshnessModes.PreferFresh,
            CancellationToken ct = default, string projectKey = null)
        {
            if (!await IsHealthyAsync().ConfigureAwait(false))
                throw new InvalidOperationException("knowledge worker is not running");

            async Task<KnowledgeSearchResponse> SendAsync()
            {
                var body = JsonSerializer.Serialize(new { query, limit, freshness, project_key = projectKey });
                using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/query/search"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException(
                                $"knowledge worker search failed: HTTP {(int)response.StatusCode} {json}");
                        return JsonSerializer.Deserialize<KnowledgeSearchResponse>(json, JsonOptions);
                    }
                }
            }

            var payload = await SendAsync().ConfigureAwait(false);

            if (string.Equals(payload?.Status, "no_index", StringComparison.OrdinalIgnoreCase))
            {
                // First query after a restart: build the index once (the worker attaches + selects
                // the project), then retry. Without this the caller would see an empty hit list.
                await RebuildAsync(force: false, ct: ct).ConfigureAwait(false);
                payload = await SendAsync().ConfigureAwait(false);
                if (string.Equals(payload?.Status, "no_index", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "knowledge index is not available for project " + (projectKey ?? "(current)"));
            }

            if (string.Equals(payload?.Status, "project_mismatch", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "knowledge worker is attached to a different project than the caller");

            return payload?.Result?.Hits ?? new List<PlcKnowledgeSearchHit>();
        }

        public async Task<ContextPack> BuildContextPackAsync(
            string query, string rid = null, int topK = 5,
            string freshness = ProjectKnowledgeFreshnessModes.PreferFresh,
            CancellationToken ct = default, string projectKey = null)
        {
            if (!await IsHealthyAsync().ConfigureAwait(false)) return null;

            try
            {
                var body = JsonSerializer.Serialize(new { query, rid, top_k = topK, freshness, project_key = projectKey });
                using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/query/context-pack"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) return null;
                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var payload = JsonSerializer.Deserialize<KnowledgeContextResponse>(json, JsonOptions);
                        return payload?.Result?.Pack;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        public async Task<PlcVariableReferenceResult> QueryVariableReferencesAsync(
            string variablePath, string direction = "both", int topK = 20,
            bool includeDefinitions = true,
            string freshness = ProjectKnowledgeFreshnessModes.PreferFresh,
            CancellationToken ct = default, string projectKey = null)
        {
            var healthy = await IsHealthyAsync().ConfigureAwait(false);
            if (!healthy)
                return new PlcVariableReferenceResult { VariablePath = variablePath, Note = "Worker offline" };

            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    variable_path = variablePath,
                    direction,
                    top_k = topK,
                    include_definitions = includeDefinitions,
                    freshness,
                    project_key = projectKey
                });
                using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/query/variable-references"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return new PlcVariableReferenceResult { VariablePath = variablePath, Note = "HTTP " + (int)response.StatusCode };

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        var result = new PlcVariableReferenceResult
                        {
                            VariablePath = variablePath,
                            Direction = direction
                        };

                        if (root.TryGetProperty("result", out var resultEl))
                        {
                            if (resultEl.TryGetProperty("hits", out var hitsEl) && hitsEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var hitEl in hitsEl.EnumerateArray())
                                {
                                    result.Hits.Add(new PlcVariableReferenceHit
                                    {
                                        BlockName = GetString(hitEl, "blockName") ?? "",
                                        BlockType = GetString(hitEl, "blockType") ?? "",
                                        CanonicalPath = GetString(hitEl, "canonicalPath") ?? "",
                                        UsageKind = GetString(hitEl, "usageKind") ?? "",
                                        DefinitionKind = GetString(hitEl, "definitionKind") ?? "",
                                        NetworkIndex = GetInt(hitEl, "networkIndex"),
                                        SourceRef = GetString(hitEl, "sourceRef") ?? ""
                                    });
                                }
                            }

                            if (resultEl.TryGetProperty("precision", out var prec))
                                result.Precision = prec.GetString() ?? "";
                            if (resultEl.TryGetProperty("note", out var note))
                                result.Note = note.GetString() ?? "";
                            if (resultEl.TryGetProperty("totalHits", out var th) && th.TryGetInt32(out var thv))
                                result.TotalHits = thv;
                        }

                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                return new PlcVariableReferenceResult { VariablePath = variablePath, Note = ex.Message };
            }
        }

        private static readonly JsonSerializerOptions _chainJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public async Task<DependencyChainResult> QueryCallChainAsync(
            string blockName, string direction = "both", int maxDepth = 5, int maxNodeCount = 200,
            string freshness = ProjectKnowledgeFreshnessModes.PreferFresh, CancellationToken ct = default,
            string projectKey = null)
        {
            var healthy = await IsHealthyAsync().ConfigureAwait(false);
            if (!healthy)
                return new DependencyChainResult { Root = blockName, Direction = direction, Note = "Worker offline" };

            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    block_name = blockName,
                    direction,
                    max_depth = maxDepth,
                    max_node_count = maxNodeCount,
                    freshness,
                    project_key = projectKey
                });
                using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/query/call-chain"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return new DependencyChainResult { Root = blockName, Direction = direction, Note = "HTTP " + (int)response.StatusCode };

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("result", out var resultEl))
                        {
                            var parsed = JsonSerializer.Deserialize<DependencyChainResult>(resultEl.GetRawText(), _chainJsonOptions);
                            return parsed ?? new DependencyChainResult { Root = blockName, Direction = direction };
                        }
                        return new DependencyChainResult { Root = blockName, Direction = direction, Note = "No result in response" };
                    }
                }
            }
            catch (Exception ex)
            {
                return new DependencyChainResult { Root = blockName, Direction = direction, Note = ex.Message };
            }
        }

        public async Task<DependencyChainResult> QueryVariableImpactAsync(
            string canonicalPath, string direction = "both", int maxDepth = 5, int maxNodeCount = 200,
            string freshness = ProjectKnowledgeFreshnessModes.PreferFresh, CancellationToken ct = default,
            string projectKey = null)
        {
            var healthy = await IsHealthyAsync().ConfigureAwait(false);
            if (!healthy)
                return new DependencyChainResult { Root = canonicalPath, Direction = direction, Note = "Worker offline" };

            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    canonical_path = canonicalPath,
                    direction,
                    max_depth = maxDepth,
                    max_node_count = maxNodeCount,
                    freshness,
                    project_key = projectKey
                });
                using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/query/variable-impact"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return new DependencyChainResult { Root = canonicalPath, Direction = direction, Note = "HTTP " + (int)response.StatusCode };

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("result", out var resultEl))
                        {
                            var parsed = JsonSerializer.Deserialize<DependencyChainResult>(resultEl.GetRawText(), _chainJsonOptions);
                            return parsed ?? new DependencyChainResult { Root = canonicalPath, Direction = direction };
                        }
                        return new DependencyChainResult { Root = canonicalPath, Direction = direction, Note = "No result in response" };
                    }
                }
            }
            catch (Exception ex)
            {
                return new DependencyChainResult { Root = canonicalPath, Direction = direction, Note = ex.Message };
            }
        }

        public async Task<PlcProjectIndexBuildResult> RebuildAsync(
            bool force = false, CancellationToken ct = default)
        {
            var healthy = await IsHealthyAsync().ConfigureAwait(false);
            if (!healthy)
                return new PlcProjectIndexBuildResult { Failed = -1 };

            try
            {
                var query = $"?force={(force ? "true" : "false")}";
                using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/index/rebuild" + query))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    using (var response = await HttpLong.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return new PlcProjectIndexBuildResult { Failed = -1 };

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        return new PlcProjectIndexBuildResult
                        {
                            Upserted = GetInt(root, "upserted"),
                            SkippedUnchanged = GetInt(root, "skipped"),
                            Failed = GetInt(root, "failed")
                        };
                    }
                }
            }
            catch
            {
                return new PlcProjectIndexBuildResult { Failed = -1 };
            }
        }

        public async Task<CrossReferenceResult> GetCrossReferencesAsync(
            string variableName, string blockName = null, CancellationToken ct = default)
        {
            var result = new CrossReferenceResult { VariableName = variableName };
            if (string.IsNullOrWhiteSpace(variableName)) return result;

            var healthy = await IsHealthyAsync().ConfigureAwait(false);
            if (!healthy) return result;

            try
            {
                var query = "?variable=" + Uri.EscapeDataString(variableName);
                if (!string.IsNullOrWhiteSpace(blockName))
                    query += "&block=" + Uri.EscapeDataString(blockName);

                using (var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/crossref" + query))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) return result;

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        result.DataType = GetString(root, "data_type") ?? string.Empty;
                        result.Scope = GetString(root, "scope") ?? string.Empty;

                        if (root.TryGetProperty("entries", out var entriesEl) &&
                            entriesEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var e in entriesEl.EnumerateArray())
                            {
                                result.Entries.Add(new CrossReferenceEntry
                                {
                                    SourceName = GetString(e, "source_name"),
                                    SourcePath = GetString(e, "source_path"),
                                    ReferenceName = GetString(e, "reference_name"),
                                    ReferencePath = GetString(e, "reference_path"),
                                    LocationName = GetString(e, "location_name"),
                                    ReferenceLocation = GetString(e, "reference_location"),
                                    ReferenceType = GetString(e, "reference_type"),
                                    Access = GetString(e, "access"),
                                    VariableName = GetString(e, "variable_name"),
                                    Scope = GetString(e, "scope"),
                                    BlockName = GetString(e, "block_name"),
                                    BlockType = GetString(e, "block_type"),
                                    NetworkIndex = GetInt(e, "network_index"),
                                    NetworkTitle = GetString(e, "network_title"),
                                    NetworkComment = GetString(e, "network_comment")
                                });
                            }
                        }
                    }
                }
            }
            catch
            {
                // On worker query failure, return the (possibly empty) result already collected rather than throwing, so the UI is unaffected.
            }

            return result;
        }

        public async Task MarkDirtyAsync(
            string blockKey = null, string reason = null, CancellationToken ct = default)
        {
            var healthy = await IsHealthyAsync().ConfigureAwait(false);
            if (!healthy) return;

            try
            {
                var body = JsonSerializer.Serialize(new { block_key = blockKey, reason });
                using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/index/mark-dirty"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        // Fire-and-forget for mark-dirty
                    }
                }
            }
            catch
            {
                // Best-effort
            }
        }

        // ---- Network Knowledge Proxy Methods ----

        /// <summary>Get the project the Worker is currently attached to. Never throws; null when offline.</summary>
        public async Task<WorkerCurrentProjectInfo> GetCurrentProjectInfoAsync(CancellationToken ct = default)
        {
            try
            {
                if (!await IsHealthyAsync().ConfigureAwait(false))
                    return null;

                using (var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/query/current-project"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return null;

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        return new WorkerCurrentProjectInfo
                        {
                            Ok = GetBool(root, "ok"),
                            ProjectKey = GetString(root, "project_key"),
                            ProjectName = GetString(root, "project_name"),
                            IsConnected = GetBool(root, "is_connected"),
                            HasIndex = GetBool(root, "has_index"),
                            IsStale = GetBool(root, "is_stale"),
                            IsIndexing = GetBool(root, "is_indexing")
                        };
                    }
                }
            }
            catch
            {
                // Fail-closed: no project info → callers skip mismatch validation.
                return null;
            }
        }

        /// <summary>Get Network-centric build status from Worker.</summary>
        public async Task<string> GetNetworkBuildStatusAsync(CancellationToken ct = default)
        {
            var healthy = await IsHealthyAsync().ConfigureAwait(false);
            if (!healthy) return "{\"available\":false,\"note\":\"worker_offline\"}";

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/network/build-status"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                return $"{{\"available\":false,\"note\":\"{ex.Message}\"}}";
            }
        }

        /// <summary>Get Network-centric evidence pack from Worker.</summary>
        public async Task<string> GetNetworkEvidencePackAsync(string query, CancellationToken ct = default)
        {
            var healthy = await IsHealthyAsync().ConfigureAwait(false);
            if (!healthy) return "{\"available\":false}";

            try
            {
                var body = JsonSerializer.Serialize(new { query });
                using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/network/evidence-pack"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                return $"{{\"available\":false,\"note\":\"{ex.Message}\"}}";
            }
        }

        private Process TryStartWorkerProcess()
        {
            try
            {
                // Pre-create cache directories so the worker doesn't fail on startup
                var cachePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TIAOpennessStudio", "KnowledgeCache");
                try { Directory.CreateDirectory(Path.Combine(cachePath, "logs")); } catch { }
                try { Directory.CreateDirectory(Path.Combine(cachePath, "snapshots")); } catch { }

                // Extract port from BaseUrl
                var uri = new Uri(BaseUrl);
                int port = uri.Port;

                var psi = new ProcessStartInfo
                {
                    FileName = _workerExePath,
                    Arguments = $"--port {port} --token {_authToken}",
                    WorkingDirectory = Path.GetDirectoryName(_workerExePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                AppLogger.Log("[KnowledgeWorkerHost] Starting worker: " + psi.FileName + " " + psi.Arguments);
                return Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppLogger.Log("[KnowledgeWorkerHost] TryStartWorkerProcess failed: " + ex.Message);
                return null;
            }
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return "http://127.0.0.1:19530";
            return baseUrl.TrimEnd('/');
        }

        private static string LocateWorkerExecutable(string appBaseDirectory)
        {
            var baseDir = string.IsNullOrWhiteSpace(appBaseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : appBaseDirectory;

            var candidates = new[]
            {
                Path.Combine(baseDir, "TIAKnowledgeWorker.exe"),
                Path.Combine(baseDir, "Workers", "TIAKnowledgeWorker.exe"),
                Path.Combine(baseDir, "..", "TIAKnowledgeWorker", "bin", "Debug", "net48", "TIAKnowledgeWorker.exe"),
                Path.Combine(baseDir, "..", "..", "TIAKnowledgeWorker", "bin", "Debug", "net48", "TIAKnowledgeWorker.exe"),
                Path.Combine(baseDir, "..", "..", "..", "TIAKnowledgeWorker", "bin", "Debug", "net48", "TIAKnowledgeWorker.exe"),
                Path.Combine(baseDir, "..", "..", "..", "..", "TIAKnowledgeWorker", "bin", "Debug", "net48", "TIAKnowledgeWorker.exe"),
                Path.Combine(baseDir, "..", "..", "..", "..", "TIAKnowledgeWorker", "bin", "Release", "net48", "TIAKnowledgeWorker.exe")
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    var fullPath = Path.GetFullPath(candidate);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
                catch { }
            }

            return candidates[0];
        }

        private static string GetDefaultCachePath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "TIAOpennessStudio", "KnowledgeCache", "project_index.db");
        }

        private static string GetDefaultLogFilePath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "TIAOpennessStudio", "KnowledgeCache", "logs", "worker.log");
        }

        private sealed class KnowledgeSearchResponse
        {
            public string Status { get; set; }
            public string ProjectKey { get; set; }
            public string ProjectName { get; set; }
            public KnowledgeSearchResult Result { get; set; }
        }

        private sealed class KnowledgeSearchResult
        {
            public List<PlcKnowledgeSearchHit> Hits { get; set; } = new List<PlcKnowledgeSearchHit>();
        }

        private sealed class KnowledgeContextResponse
        {
            public string Status { get; set; }
            public string ProjectKey { get; set; }
            public string ProjectName { get; set; }
            public KnowledgeContextResult Result { get; set; }
        }

        private sealed class KnowledgeContextResult
        {
            public ContextPack Pack { get; set; }
        }

        // JSON helpers
        private static string GetString(JsonElement root, string key)
        {
            if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
            return null;
        }

        private static bool GetBool(JsonElement root, string key)
        {
            if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.True)
                return true;
            return false;
        }

        private static int GetInt(JsonElement root, string key)
        {
            if (root.TryGetProperty(key, out var el) && el.TryGetInt32(out var v))
                return v;
            return 0;
        }
    }
}
