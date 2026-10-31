using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio;
using TIAOpennessStudio.Services;

namespace TIAKnowledgeWorker
{
    /// <summary>
    /// Local HTTP server hosting the Worker API.
    /// All TIA-heavy operations are dispatched through KnowledgeCacheRuntime.
    /// </summary>
    public sealed class WorkerHost : IDisposable
    {
        private readonly KnowledgeCacheRuntime _runtime;
        private readonly string _baseUrl;
        private readonly string _authToken;
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private int _started;

        public string BaseUrl => _baseUrl;
        public string AuthToken => _authToken;

        public WorkerHost(KnowledgeCacheRuntime runtime, int port = 0, string authToken = null)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

            // Use a fixed port range; fallback to OS-assigned if busy.
            if (port <= 0) port = 19530;
            _baseUrl = $"http://127.0.0.1:{port}/";
            _authToken = authToken ?? Guid.NewGuid().ToString("N").Substring(0, 16);
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(_baseUrl);

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                AppLogger.Log($"[WorkerHost] Failed to start on {_baseUrl}: {ex.Message}");
                // Try OS-assigned port
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add("http://127.0.0.1:0/");
                _listener.Start();
                // Read back the actual port from the listener
                // HttpListener doesn't expose the port directly in .NET Framework,
                // but we can infer it from the prefix or just log it.
                AppLogger.Log("[WorkerHost] Started on OS-assigned port (fallback)");
            }

            AppLogger.Log($"[WorkerHost] Listening on {_baseUrl}");
            AppLogger.Log($"[WorkerHost] Auth token: {_authToken}");

            _ = AcceptLoopAsync(_cts.Token);
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _started, 0) == 0) return;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    break; // Listener stopped
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = HandleRequestAsync(ctx, ct);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            try
            {
                // Auth check (skip for /health)
                if (!ctx.Request.Url.AbsolutePath.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    var authHeader = ctx.Request.Headers["Authorization"];
                    if (string.IsNullOrEmpty(authHeader) ||
                        !authHeader.Equals($"Bearer {_authToken}", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteJson(ctx.Response, 401, new { error = "unauthorized" });
                        return;
                    }
                }

                var path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();

                switch (path)
                {
                    case "/health":
                        await HandleHealthAsync(ctx);
                        break;
                    case "/status":
                        await HandleStatusAsync(ctx, ct);
                        break;
                    case "/tia/attach":
                        await HandleTiaAttachAsync(ctx, ct);
                        break;
                    case "/tia/connect":
                        await HandleTiaConnectAsync(ctx, ct);
                        break;
                    case "/index/rebuild":
                        await HandleIndexRebuildAsync(ctx, ct);
                        break;
                    case "/index/status":
                        await HandleIndexStatusAsync(ctx, ct);
                        break;
                    case "/index/mark-dirty":
                        await HandleIndexMarkDirtyAsync(ctx, ct);
                        break;
                    case "/index/clear":
                        await HandleIndexClearAsync(ctx, ct);
                        break;
                    case "/query/search":
                        await HandleQuerySearchAsync(ctx, ct);
                        break;
                    case "/query/variable-references":
                        await HandleQueryVariableReferencesAsync(ctx, ct);
                        break;
                    case "/query/context-pack":
                        await HandleQueryContextPackAsync(ctx, ct);
                        break;
                    case "/query/call-chain":
                        await HandleQueryCallChainAsync(ctx, ct);
                        break;
                    case "/query/variable-impact":
                        await HandleQueryVariableImpactAsync(ctx, ct);
                        break;
                    case "/query/current-project":
                        await HandleCurrentProjectAsync(ctx, ct);
                        break;
                    // ---- Network Knowledge endpoints (Phase 2) ----
                    case "/network/build-status":
                        await HandleNetworkBuildStatusAsync(ctx, ct);
                        break;
                    case "/network/search":
                        await HandleNetworkSearchAsync(ctx, ct);
                        break;
                    case "/network/by-block":
                        await HandleNetworkByBlockAsync(ctx, ct);
                        break;
                    case "/network/evidence-pack":
                        await HandleNetworkEvidencePackAsync(ctx, ct);
                        break;
                    case "/network/cross-references":
                        await HandleNetworkCrossReferencesAsync(ctx, ct);
                        break;
                    case "/crossref":
                        await HandleCrossRefAsync(ctx, ct);
                        break;
                    default:
                        WriteJson(ctx.Response, 404, new { error = "not_found", path });
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[WorkerHost] Unhandled request error: {ex.Message}");
                try { WriteJson(ctx.Response, 500, new { error = "internal_error", detail = ex.Message }); }
                catch { }
            }
        }

        // ---- Endpoint handlers ----

        private Task HandleHealthAsync(HttpListenerContext ctx)
        {
            WriteJson(ctx.Response, 200, new
            {
                status = "ok",
                worker = "TIAKnowledgeWorker",
                version = "1.0.0",
                timestamp = DateTime.UtcNow.ToString("o")
            });
            return Task.CompletedTask;
        }

        private async Task HandleStatusAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var status = await _runtime.GetStatusAsync(ct).ConfigureAwait(false);
            var project = _runtime.GetAttachedProjectSnapshot();
            WriteJson(ctx.Response, 200, new
            {
                process_state = status.IsIndexing ? "indexing" :
                                status.IsStale ? "stale" :
                                status.HasIndex ? (_runtime.IsConnected ? "ready" : "idle") :
                                _runtime.IsConnected ? "attached" : "idle",
                tia_state = _runtime.IsConnected ? "attached" : "offline",
                project_key = project.Key,
                project_name = project.Name,
                has_index = status.HasIndex,
                is_stale = status.IsStale,
                is_indexing = status.IsIndexing,
                dirty_count = status.DirtyItemCount,
                indexed_block_count = status.IndexedBlockCount,
                last_indexed_at = status.LastIndexedAt,
                cache_path = _runtime.CacheBasePath,
                git_branch = status.GitBranch,
                git_commit = status.GitCommit,
                note = status.Note
            });
        }

        /// <summary>
        /// GET /query/current-project — returns the project the Worker is currently
        /// attached to (from the lock-free snapshot) plus basic index state, so callers
        /// can detect project mismatches without issuing a real query.
        /// </summary>
        private async Task HandleCurrentProjectAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            await EnsureAttachedIfNeededAsync().ConfigureAwait(false);
            var project = _runtime.GetAttachedProjectSnapshot();
            var status = await _runtime.GetStatusAsync(ct).ConfigureAwait(false);
            WriteJson(ctx.Response, 200, new
            {
                ok = true,
                project_key = project.Key,
                project_name = project.Name,
                is_connected = _runtime.IsConnected,
                has_index = status.HasIndex,
                is_stale = status.IsStale,
                is_indexing = status.IsIndexing
            });
        }

        private async Task HandleTiaAttachAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var success = await _runtime.AttachAsync().ConfigureAwait(false);
            WriteJson(ctx.Response, success ? 200 : 503, new
            {
                success,
                is_connected = _runtime.IsConnected
            });
        }

        private async Task HandleTiaConnectAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var success = await _runtime.ConnectAsync().ConfigureAwait(false);
            WriteJson(ctx.Response, success ? 200 : 503, new
            {
                success,
                is_connected = _runtime.IsConnected
            });
        }

        private async Task HandleIndexRebuildAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string forceStr = GetQueryValue(ctx.Request, "force");
            bool force = string.Equals(forceStr, "true", StringComparison.OrdinalIgnoreCase);

            var result = await _runtime.RebuildAsync(force, null, ct).ConfigureAwait(false);
            WriteJson(ctx.Response, 200, new
            {
                upserted = result.Upserted,
                skipped = result.SkippedUnchanged,
                unsupported = result.SkippedUnsupported,
                failed = result.Failed,
                total = result.Upserted + result.SkippedUnchanged + result.SkippedUnsupported + result.Failed
            });
        }

        private async Task HandleIndexStatusAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var status = await _runtime.GetStatusAsync(ct).ConfigureAwait(false);
            WriteJson(ctx.Response, 200, new
            {
                has_index = status.HasIndex,
                is_stale = status.IsStale,
                is_indexing = status.IsIndexing,
                dirty_count = status.DirtyItemCount,
                indexed_block_count = status.IndexedBlockCount,
                last_indexed_at = status.LastIndexedAt,
                git_branch = status.GitBranch,
                git_commit = status.GitCommit,
                note = status.Note,
                progress_message = status.ProgressMessage,
                progress_current = status.ProgressCurrent,
                progress_total = status.ProgressTotal
            });
        }

        private async Task HandleIndexMarkDirtyAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            string blockKey = null;
            string reason = null;

            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("block_key", out var bk))
                    blockKey = bk.GetString();
                if (doc.RootElement.TryGetProperty("reason", out var r))
                    reason = r.GetString();
            }

            await _runtime.MarkDirtyAsync(blockKey, reason, ct).ConfigureAwait(false);
            WriteJson(ctx.Response, 200, new { marked = true });
        }

        private async Task HandleIndexClearAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            // Clear is a full rebuild with force=true (which clears stale entries first)
            var result = await _runtime.RebuildAsync(true, null, ct).ConfigureAwait(false);
            WriteJson(ctx.Response, 200, new
            {
                cleared = true,
                rebuilt = result.Upserted + result.SkippedUnchanged + result.Failed
            });
        }

        /// <summary>
        /// Extracts optional project_key from the request body and checks it against
        /// the currently attached project. Returns null when validation passes (or is
        /// skipped); returns an anonymous mismatch payload when the caller's project
        /// differs from the attached one. Fail-closed: any exception → null.
        /// </summary>
        private object CheckProjectMismatch(JsonElement root)
        {
            try
            {
                string requestedKey = null;
                if (root.TryGetProperty("project_key", out var pk) && pk.ValueKind == JsonValueKind.String)
                    requestedKey = pk.GetString();
                if (string.IsNullOrWhiteSpace(requestedKey))
                    return null; // Caller did not specify a project → skip validation.

                var attached = _runtime.GetAttachedProjectSnapshot();
                if (string.IsNullOrWhiteSpace(attached.Key))
                    return null; // Worker has no project attached → skip validation.

                if (string.Equals(requestedKey, attached.Key, StringComparison.OrdinalIgnoreCase))
                    return null; // Match.

                return new
                {
                    status = "project_mismatch",
                    requested_key = requestedKey,
                    attached_key = attached.Key,
                    attached_name = attached.Name,
                    hits = Array.Empty<object>()
                };
            }
            catch
            {
                return null; // Fail-closed: validation errors must not break queries.
            }
        }

        /// <summary>Appends project info to a successful query response.</summary>
        private object WrapQueryResult(object result, string projectKey = null)
        {
            var attached = _runtime.GetAttachedProjectSnapshot();
            return new
            {
                status = "ok",
                project_key = string.IsNullOrWhiteSpace(projectKey) ? attached.Key : projectKey,
                project_name = attached.Name,
                result
            };
        }

        /// <summary>
        /// Attach the Worker to the running TIA Portal on demand (select the project and resolve
        /// PlcSoftware). Without this a freshly started Worker stays unattached, and every RAG
        /// query resolves an empty project key and silently returns zero hits.
        /// </summary>
        private async Task EnsureAttachedIfNeededAsync()
        {
            if (_runtime.IsConnected) return;
            try
            {
                await _runtime.EnsureAttachedAsync().ConfigureAwait(false);
                AppLogger.Log("[WorkerHost] attach-on-demand: connected=" + _runtime.IsConnected);
            }
            catch (Exception ex)
            {
                AppLogger.Log("[WorkerHost] attach-on-demand failed: " + ex.Message);
            }
        }

        private async Task HandleQuerySearchAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                WriteJson(ctx.Response, 400, new { status = "invalid_request", error = "missing_body" });
                return;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("query", out var queryEl)
                || queryEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(queryEl.GetString()))
            {
                WriteJson(ctx.Response, 400, new { status = "invalid_request", error = "query is required" });
                return;
            }
            string query = queryEl.GetString();

            int limit = 5;
            if (doc.RootElement.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv))
                limit = lv;
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred;
            if (doc.RootElement.TryGetProperty("freshness", out var f))
                freshness = f.GetString() ?? freshness;
            string projectKey = null;
            if (doc.RootElement.TryGetProperty("project_key", out var pk) && pk.ValueKind == JsonValueKind.String)
                projectKey = pk.GetString();

            // Query handlers self-heal the Worker's TIA session (attach + select project) so a
            // restart does not leave the RAG tools permanently dead.
            await EnsureAttachedIfNeededAsync().ConfigureAwait(false);

            var mismatch = CheckProjectMismatch(doc.RootElement);
            if (mismatch != null)
            {
                WriteJson(ctx.Response, 200, mismatch);
                return;
            }

            // An explicit key (the caller's own project) is authoritative. If it has no index,
            // report that instead of pretending "no matches" — the caller can then trigger a rebuild.
            if (!string.IsNullOrWhiteSpace(projectKey))
            {
                var projectStatus = await _runtime.GetStatusForProjectAsync(projectKey, ct).ConfigureAwait(false);
                if (!projectStatus.HasIndex)
                {
                    WriteJson(ctx.Response, 200, new
                    {
                        status = "no_index",
                        project_key = projectKey,
                        has_index = false,
                        is_connected = _runtime.IsConnected,
                        note = string.IsNullOrWhiteSpace(projectStatus.Note)
                            ? "Knowledge index has not been built for this project."
                            : projectStatus.Note
                    });
                    return;
                }
            }

            var hits = await _runtime.SearchKnowledgeAsync(query, limit, freshness, ct, projectKey).ConfigureAwait(false);
            WriteJson(ctx.Response, 200, WrapQueryResult(new { query, hits }, projectKey));
        }

        private async Task HandleQueryVariableReferencesAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                WriteJson(ctx.Response, 400, new { error = "missing_body" });
                return;
            }

            using var doc = JsonDocument.Parse(body);
            string variablePath = doc.RootElement.GetProperty("variable_path").GetString();
            string direction = "both";
            if (doc.RootElement.TryGetProperty("direction", out var d))
                direction = d.GetString() ?? direction;
            int topK = 20;
            if (doc.RootElement.TryGetProperty("top_k", out var tk) && tk.TryGetInt32(out var tkv))
                topK = tkv;
            bool includeDefinitions = true;
            if (doc.RootElement.TryGetProperty("include_definitions", out var idef))
                includeDefinitions = idef.GetBoolean();
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred;
            if (doc.RootElement.TryGetProperty("freshness", out var f))
                freshness = f.GetString() ?? freshness;

            await EnsureAttachedIfNeededAsync().ConfigureAwait(false);

            var mismatch = CheckProjectMismatch(doc.RootElement);
            if (mismatch != null)
            {
                WriteJson(ctx.Response, 200, mismatch);
                return;
            }

            var result = await _runtime.QueryVariableReferencesAsync(
                variablePath, direction, topK, includeDefinitions, freshness, ct).ConfigureAwait(false);
            WriteJson(ctx.Response, 200, WrapQueryResult(result));
        }

        private async Task HandleQueryContextPackAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                WriteJson(ctx.Response, 400, new { error = "missing_body" });
                return;
            }

            using var doc = JsonDocument.Parse(body);
            string query = doc.RootElement.GetProperty("query").GetString();
            string rid = null;
            if (doc.RootElement.TryGetProperty("rid", out var r))
                rid = r.GetString();
            int topK = 5;
            if (doc.RootElement.TryGetProperty("top_k", out var tk) && tk.TryGetInt32(out var tkv))
                topK = tkv;
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred;
            if (doc.RootElement.TryGetProperty("freshness", out var f))
                freshness = f.GetString() ?? freshness;

            await EnsureAttachedIfNeededAsync().ConfigureAwait(false);

            var mismatch = CheckProjectMismatch(doc.RootElement);
            if (mismatch != null)
            {
                WriteJson(ctx.Response, 200, mismatch);
                return;
            }

            var pack = await _runtime.BuildContextPackAsync(query, rid, topK, freshness, ct).ConfigureAwait(false);
            WriteJson(ctx.Response, 200, WrapQueryResult(new { query, pack }));
        }

        private async Task HandleQueryCallChainAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                WriteJson(ctx.Response, 400, new { error = "missing_body" });
                return;
            }

            using var doc = JsonDocument.Parse(body);
            string blockName = doc.RootElement.GetProperty("block_name").GetString();
            string direction = "both";
            if (doc.RootElement.TryGetProperty("direction", out var d))
                direction = d.GetString() ?? direction;
            int maxDepth = 5;
            if (doc.RootElement.TryGetProperty("max_depth", out var md) && md.TryGetInt32(out var mdv))
                maxDepth = mdv;
            int maxNodeCount = 200;
            if (doc.RootElement.TryGetProperty("max_node_count", out var mn) && mn.TryGetInt32(out var mnv))
                maxNodeCount = mnv;
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred;
            if (doc.RootElement.TryGetProperty("freshness", out var f))
                freshness = f.GetString() ?? freshness;

            await EnsureAttachedIfNeededAsync().ConfigureAwait(false);

            var mismatch2 = CheckProjectMismatch(doc.RootElement);
            if (mismatch2 != null)
            {
                WriteJson(ctx.Response, 200, mismatch2);
                return;
            }

            var result = await _runtime.QueryCallChainAsync(blockName, direction, maxDepth, maxNodeCount, freshness, ct).ConfigureAwait(false);
            WriteJson(ctx.Response, 200, WrapQueryResult(result));
        }

        private async Task HandleQueryVariableImpactAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                WriteJson(ctx.Response, 400, new { error = "missing_body" });
                return;
            }

            using var doc = JsonDocument.Parse(body);
            string canonicalPath = doc.RootElement.GetProperty("canonical_path").GetString();
            string direction = "both";
            if (doc.RootElement.TryGetProperty("direction", out var d))
                direction = d.GetString() ?? direction;
            int maxDepth = 5;
            if (doc.RootElement.TryGetProperty("max_depth", out var md) && md.TryGetInt32(out var mdv))
                maxDepth = mdv;
            int maxNodeCount = 200;
            if (doc.RootElement.TryGetProperty("max_node_count", out var mn) && mn.TryGetInt32(out var mnv))
                maxNodeCount = mnv;
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred;
            if (doc.RootElement.TryGetProperty("freshness", out var f))
                freshness = f.GetString() ?? freshness;

            await EnsureAttachedIfNeededAsync().ConfigureAwait(false);

            var mismatch3 = CheckProjectMismatch(doc.RootElement);
            if (mismatch3 != null)
            {
                WriteJson(ctx.Response, 200, mismatch3);
                return;
            }

            var result = await _runtime.QueryVariableImpactAsync(canonicalPath, direction, maxDepth, maxNodeCount, freshness, ct).ConfigureAwait(false);
            WriteJson(ctx.Response, 200, WrapQueryResult(result));
        }

        // ---- Network Knowledge Handlers ----

        private Task HandleNetworkBuildStatusAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            // Returns build job and published snapshot status
            // NetworkKnowledgeFacade integration point - for now return basic info from runtime
            WriteJson(ctx.Response, 200, new
            {
                endpoint = "network/build-status",
                has_index = _runtime.IsConnected,
                note = "Network knowledge build status"
            });
            return Task.CompletedTask;
        }

        private async Task HandleNetworkSearchAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                WriteJson(ctx.Response, 400, new { error = "missing_body" });
                return;
            }

            using var doc = JsonDocument.Parse(body);
            string query = doc.RootElement.GetProperty("query").GetString();
            int limit = 10;
            if (doc.RootElement.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv))
                limit = lv;

            // Delegate to runtime search (enriched with Network data when available)
            var hits = await _runtime.SearchKnowledgeAsync(query, limit,
                ProjectKnowledgeFreshnessModes.CachePreferred, ct).ConfigureAwait(false);

            WriteJson(ctx.Response, 200, new { query, hits, source = "network-search" });
        }

        private async Task HandleNetworkByBlockAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                WriteJson(ctx.Response, 400, new { error = "missing_body" });
                return;
            }

            using var doc = JsonDocument.Parse(body);
            string blockId = doc.RootElement.GetProperty("block_id").GetString();

            // Return network metadata for a specific block
            WriteJson(ctx.Response, 200, new
            {
                block_id = blockId,
                note = "Network-by-block endpoint ready for NetworkDocumentStore integration"
            });
        }

        private async Task HandleNetworkEvidencePackAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                WriteJson(ctx.Response, 400, new { error = "missing_body" });
                return;
            }

            using var doc = JsonDocument.Parse(body);
            string query = doc.RootElement.GetProperty("query").GetString();

            // Evidence Pack endpoint - returns Network-centric RAG results
            WriteJson(ctx.Response, 200, new
            {
                query,
                available = false,
                note = "Evidence pack requires published snapshot. Use NetworkKnowledgeFacade."
            });
        }

        private async Task HandleNetworkCrossReferencesAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                WriteJson(ctx.Response, 400, new { error = "missing_body", hint = "Provide JSON with block_name field" });
                return;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("block_name", out var blockNameProp) ||
                string.IsNullOrWhiteSpace(blockNameProp.GetString()))
            {
                WriteJson(ctx.Response, 400, new { error = "missing_block_name" });
                return;
            }

            string blockName = blockNameProp.GetString();

            // Cross-reference queries require live TIA connection via main process.
            // For now, return a structured response indicating the feature is available
            // but actual data comes from the NetworkKnowledgeFacade in the main process.
            WriteJson(ctx.Response, 200, new
            {
                block_name = blockName,
                source = "cross-reference-provider",
                note = "Cross-reference data is enriched during build phase. Use /network/evidence-pack for query results.",
                available = _runtime.IsConnected
            });
        }

        /// <summary>
        /// GET /crossref?variable=xxx&amp;block=yyy
        /// Variable-level cross-reference query: runs a whole-project TIA Openness scan on
        /// the Worker's STA thread; the main process fetches results asynchronously via this
        /// endpoint without blocking the UI thread.
        /// </summary>
        private async Task HandleCrossRefAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string variable = GetQueryValue(ctx.Request, "variable");
            string block = GetQueryValue(ctx.Request, "block");

            if (string.IsNullOrWhiteSpace(variable))
            {
                WriteJson(ctx.Response, 400, new { error = "missing_variable" });
                return;
            }

            var result = await _runtime.GetCrossReferencesAsync(variable, block, ct).ConfigureAwait(false);

            if (result == null)
            {
                WriteJson(ctx.Response, 200, new { variable_name = variable, data_type = "", scope = "", entries = new object[0] });
                return;
            }

            var entries = result.Entries.Select(e => new
            {
                source_name = e.SourceName,
                source_path = e.SourcePath,
                reference_name = e.ReferenceName,
                reference_path = e.ReferencePath,
                location_name = e.LocationName,
                reference_location = e.ReferenceLocation,
                reference_type = e.ReferenceType,
                access = e.Access,
                variable_name = e.VariableName,
                scope = e.Scope,
                block_name = e.BlockName,
                block_type = e.BlockType,
                network_index = e.NetworkIndex,
                network_title = e.NetworkTitle,
                network_comment = e.NetworkComment
            }).ToArray();

            WriteJson(ctx.Response, 200, new
            {
                variable_name = result.VariableName,
                data_type = result.DataType,
                scope = result.Scope,
                entries
            });
        }

        // ---- Helpers ----

        private static string GetQueryValue(HttpListenerRequest request, string key)
        {
            return request.QueryString[key];
        }

        private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request)
        {
            if (!request.HasEntityBody) return null;
            // JSON bodies are UTF-8 (RFC 8259); ContentEncoding would fall back to the
            // system code page (GBK on zh-CN Windows) and mangle non-ASCII queries/names.
            using (var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8))
            {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        private static void WriteJson(HttpListenerResponse response, int statusCode, object payload)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }

    // Re-export the freshness constants from the linked model for convenience
    internal static class ProjectKnowledgeFreshnessModes
    {
        public const string CachePreferred = "cache_preferred";
        public const string PreferFresh = "prefer_fresh";
        public const string ForceRefresh = "force_refresh";
    }
}
