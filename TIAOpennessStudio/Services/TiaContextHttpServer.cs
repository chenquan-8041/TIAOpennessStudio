using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TIAOpennessStudio.Models.Blocks;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Blocks;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Local HTTP server (http://127.0.0.1:8001/) that exposes the current TIA
    /// project context (block code / variable types / block list) back to the Python
    /// ReAct agent.
    ///
    /// This class is the skeleton covering:
    ///   - Start/Stop lifecycle (idempotent)
    ///   - A background accept loop
    ///   - Route dispatch (/health implemented; the /api/* endpoints are filled in)
    ///   - Graceful degradation when the port is already occupied (log, don't throw,
    ///     the app keeps running)
    ///
    /// The Openness API is STA-thread-affine; all code that touches
    /// <see cref="ITiaOpennessService.PlcSoftware"/> must marshal back to the TIA UI
    /// thread via <see cref="_runOnTiaSta"/>, and must not touch it directly on an
    /// <see cref="HttpListener"/> worker thread.
    /// </summary>
    public sealed partial class TiaContextHttpServer : IDisposable
    {
        private const int Port = 8001;

        private readonly ITiaOpennessService _tia;
        private readonly Func<Func<Task>, Task> _runOnTiaSta;
        private readonly IProjectKnowledgeWorkerHostService _workerHost;
        private readonly TIAOpennessStudio.Services.Standards.StandardsRegistry _standards;  // coding-standard KB lookup (nullable)
        private readonly ProjectIndexService _projectIndex;   // repair_experience store (nullable)
        private readonly string _bindHost;   // "127.0.0.1" (default/local only) or "+" (all interfaces/remote-capable)
        private readonly string _authToken;  // non-null => enforce Bearer auth; null => local allow (preserves current behavior)
        private readonly string _prefix;
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = false };

        /// <summary>Compact tool payload options: keeps Chinese block names readable in JSON output.</summary>
        private static readonly JsonSerializerOptions _compactJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly IUserInteractionService _interactionService; // kept for external callers
        private readonly IAgentAccessMode _accessMode;  // optional: gates read-only elevation
        private readonly TIAOpennessStudio.Services.WriteGate.WriteGate _writeGate;   // null => every gated write is refused
        private readonly IHmiBuilderService _hmiBuilder;   // offline HMI XML/JSON compose (nullable)
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptLoop;
        private readonly object _gate = new object();
        private bool _disabled;   // true => a port conflict etc. disabled the server; subsequent Start/Stop no-op
        private bool _started;

        // Baseline captured by compile_snapshot, consumed by compile_diff: lets the agent separate
        // errors that already existed in the project from errors introduced by its latest write.
        private CompilationResult _compileBaseline;
        private string _compileBaselineId;

        // Whole-block reads are paged by default: a single 70k-char dump is re-sent on every
        // later model call and dominates the agent's context budget.
        private const int DefaultBlockReadChars = 20000;

        /// <param name="tia">Provides access to the current TIA project (PlcSoftware etc.).</param>
        /// <param name="runOnTiaSta">
        ///     Helper that marshals a work delegate back to the TIA UI (STA) thread;
        ///     when null it degrades to a direct await (only for unit tests without a
        ///     WPF context).
        /// </param>
        /// <param name="workerHost">
        ///     The knowledge worker host service; the RAG retrieval tools (search_project /
        ///     build_context_pack / query_variable_references) call the Worker's real index
        ///     through it. When null these tools return an "unavailable" note.
        /// </param>
        /// <param name="exposeRemotely">
        ///     true => bind all interfaces ("+") to allow remote/cloud access (requires
        ///     authToken + urlacl/firewall); false (default) => bind 127.0.0.1 only,
        ///     secure by default.
        /// </param>
        /// <param name="authToken">
        ///     Non-null => all non-/health requests must carry "Authorization: Bearer
        ///     &lt;token&gt;"; null/empty => no check (preserves the existing local-only
        ///     experience). Must be provided when exposing remotely.
        /// </param>
        /// <param name="standards">
        ///     The coding-standard KB registry (official + user-mounted libraries); the MCP
        ///     tools list_standard_topics / read_standard_section look through it. When null
        ///     those tools return an "unavailable" note and degrade without blocking.
        /// </param>
        /// <param name="interaction">
        ///     Asks the user before any tool changes the TIA project (import_block, build_lad_*, delete_block,
        ///     /api/import_scl). When null, every one of those writes is refused: a write nobody can approve
        ///     must not run. Read-only tools and compile are unaffected.
        /// </param>
        public TiaContextHttpServer(
            ITiaOpennessService tia,
            Func<Func<Task>, Task> runOnTiaSta,
            IProjectKnowledgeWorkerHostService workerHost = null,
            bool exposeRemotely = false,
            string authToken = null,
            TIAOpennessStudio.Services.Standards.StandardsRegistry standards = null,
            ProjectIndexService projectIndex = null,
            IUserInteractionService interaction = null,
            IAgentAccessMode accessMode = null,
            IHmiBuilderService hmiBuilder = null,
            int approvalTimeoutSeconds = 0,
            int approvalHardTimeoutSeconds = 0,
            int approvalMaxAttempts = 0)
        {
            _tia = tia ?? throw new ArgumentNullException(nameof(tia));
            _runOnTiaSta = runOnTiaSta ?? (work => work());
            _workerHost = workerHost;
            _standards = standards;
            _projectIndex = projectIndex;
            _interactionService = interaction;
            _accessMode = accessMode;
            _hmiBuilder = hmiBuilder;
            _writeGate = interaction == null
                ? null
                : new TIAOpennessStudio.Services.WriteGate.WriteGate(
                    interaction, LookupOnStaAsync, approvalTimeoutSeconds, null, accessMode, approvalHardTimeoutSeconds,
                    approvalMaxAttempts);
            _bindHost = exposeRemotely ? "+" : "127.0.0.1";
            _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken.Trim();
            _prefix = $"http://{_bindHost}:{Port}/";
        }

        /// <summary>
        /// Starts the HttpListener and the background accept loop. Idempotent: repeated
        /// calls are ignored. Does not throw when the port is occupied (logs and keeps
        /// the server disabled).
        /// </summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_started || _disabled) return;

                _listener = new HttpListener();
                _listener.Prefixes.Add(_prefix);
                _cts = new CancellationTokenSource();

                try
                {
                    _listener.Start();
                }
                catch (HttpListenerException ex)
                {
                    AppLogger.Log($"[TiaCtxHttp] cannot bind {_prefix} (occupied or needs urlacl for remote), server disabled");
                    AppLogger.Log($"[TiaCtxHttp] start failed: {ex.Message} (code={ex.ErrorCode})");
                    _disabled = true;
                    SafeCloseListener();
                    SafeDisposeCts();
                    return;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[TiaCtxHttp] unexpected error during Start: {ex.Message}");
                    _disabled = true;
                    SafeCloseListener();
                    SafeDisposeCts();
                    return;
                }

                _started = true;
                AppLogger.Log($"[TiaCtxHttp] listening on {_prefix} (auth={( _authToken != null ? "on" : "off")})");

                var token = _cts.Token;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(token));
            }
        }

        /// <summary>
        /// Stops the HttpListener and cancels the background accept loop. Idempotent:
        /// repeated calls are safe.
        /// </summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (!_started) { _disabled = false; return; }
                _started = false;
            }

            try { _cts?.Cancel(); } catch { }

            try { _listener?.Stop(); } catch (Exception ex) { AppLogger.Log("[TiaCtxHttp] Stop error: " + ex.Message); }
            try { _listener?.Close(); } catch { }

            SafeDisposeCts();
            _listener = null;
            AppLogger.Log("[TiaCtxHttp] stopped");
        }

        public void Dispose() => Stop();

        private void SafeCloseListener()
        {
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        private void SafeDisposeCts()
        {
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) { break; }   // triggered by Stop()
                catch (ObjectDisposedException) { break; } // listener already closed
                catch (Exception ex)
                {
                    AppLogger.Log("[TiaCtxHttp] accept error: " + ex.Message);
                    break;
                }

                // Fire-and-forget: handle each request on the thread pool so the accept loop is not blocked.
                _ = Task.Run(() => HandleRequestSafeAsync(ctx));
            }
        }

        private async Task HandleRequestSafeAsync(HttpListenerContext ctx)
        {
            try
            {
                await HandleRequestAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Log("[TiaCtxHttp] handler error: " + ex.Message);
                try
                {
                    WriteJson(ctx.Response, 500, new { error = ex.Message });
                }
                catch { /* the client may have disconnected */ }
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
                try { ctx.Response.Close(); } catch { }
            }
        }

        /// <summary>
        /// Route dispatcher. /health returns ok; /api/* endpoints expose the TIA project
        /// context. All logic that touches PlcSoftware marshals back to the TIA UI thread
        /// via _runOnTiaSta.
        /// </summary>
        // ---------------------------------------------------------------------
        // Write gate: every entry that changes the TIA project calls AuthorizeWriteAsync BEFORE it goes to the
        // STA thread. The policy table (WriteToolPolicies) is the single source of truth, shared by REST and MCP.
        // ---------------------------------------------------------------------

        /// <summary>Short STA read used to describe a write on the approval card. Never held while the user decides.</summary>
        private async Task<TIAOpennessStudio.Services.WriteGate.BlockLookupResult> LookupOnStaAsync(string name, bool isType)
        {
            TIAOpennessStudio.Services.WriteGate.BlockLookupResult found = null;
            await _runOnTiaSta(() =>
            {
                found = TIAOpennessStudio.Services.WriteGate.BlockLookup.Find(_tia.PlcSoftware, name, isType);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            return found;
        }

        private static string RidOf(HttpListenerContext ctx)
        {
            try { return ctx?.Request?.Headers["X-Request-Id"]; }
            catch { return null; }
        }

        /// <summary>Anything that is not loopback. If we cannot tell, treat it as remote (the stricter reading).</summary>
        private static bool IsRemoteCaller(HttpListenerContext ctx)
        {
            try { return ctx?.Request == null || !ctx.Request.IsLocal; }
            catch { return true; }
        }

        private static TIAOpennessStudio.Services.WriteGate.WriteRequest NewWrite(
            HttpListenerContext ctx, string tool, string blockName, string blockType = null, string code = null,
            string groupPath = null, string formatLabel = null)
        {
            return new TIAOpennessStudio.Services.WriteGate.WriteRequest
            {
                Tool = tool, BlockName = blockName, BlockType = blockType, Code = code,
                GroupPath = groupPath, FormatLabel = formatLabel,
                Rid = RidOf(ctx), IsRemote = IsRemoteCaller(ctx)
            };
        }

        /// <summary>Returns null when the write may run, otherwise the refusal to hand back to the caller.</summary>
        private async Task<TIAOpennessStudio.Services.WriteGate.WriteGateDecision> AuthorizeWriteAsync(
            TIAOpennessStudio.Services.WriteGate.WriteRequest req)
        {
            if (_writeGate == null)
            {
                AppLogger.Log("[WriteGate] refused '" + req.Tool + "': no interaction service wired into the :8001 server");
                return TIAOpennessStudio.Services.WriteGate.WriteGateDecision.Refuse(
                    Abstractions.InteractionStatus.Unavailable, "approval_unavailable",
                    "approval_unavailable: there is no confirmation window to ask the user, so this write was not run. " +
                    "Tell the user it needs their approval in the app.");
            }

            var decision = await _writeGate.AuthorizeAsync(req).ConfigureAwait(false);

            // One line per gated write, whatever the outcome: which tool, which run, allowed or why not.
            // Names and code are deliberately not logged here; the interaction audit line has the card title.
            AppLogger.Log("[WriteGate] decision tool=" + req.Tool
                + " rid=" + (string.IsNullOrWhiteSpace(req.Rid) ? "-" : TIAOpennessStudio.Services.WriteGate.CardText.Clean(req.Rid, 64))
                + " remote=" + req.IsRemote
                + " allowed=" + decision.Allowed
                + " status=" + decision.Status
                + (decision.Allowed ? "" : " code=" + decision.Code));
            return decision.Allowed ? null : decision;
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            // Bearer auth: only enforced when _authToken is set; /health is always exempt (for liveness probes).
            if (_authToken != null && !string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
            {
                var authHeader = ctx.Request.Headers["Authorization"];
                if (string.IsNullOrEmpty(authHeader) ||
                    !authHeader.Equals($"Bearer {_authToken}", StringComparison.Ordinal))
                {
                    WriteJson(ctx.Response, 401, new { error = "unauthorized" });
                    return;
                }
            }

            switch (path)
            {
                case "/health":
                    WriteJson(ctx.Response, 200, new { status = "ok" });
                    return;

                case "/api/block_code":
                    await HandleBlockCodeAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/variable_type":
                    await HandleVariableTypeAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/project_blocks":
                    await HandleProjectBlocksAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/tags":
                    await HandleTagsAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/db_structure":
                    await HandleDbStructureAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/udt_types":
                    await HandleUdtTypesAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/bootstrap":
                    await HandleBootstrapAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/delete_block":
                    await HandleDeleteBlockAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/compile_result":
                    await HandleCompileResultAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/hardware_search":
                    await HandleHardwareSearchAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/import_scl":
                    await HandleImportSclAsync(ctx).ConfigureAwait(false);
                    return;

                // Agent tool endpoints (POST, called by the Python LangGraph Agent)
                case "/api/tool/export_block":
                    await HandleToolExportBlockAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/tool/compile":
                    await HandleToolCompileAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/tool/compile_structured":
                    await HandleToolCompileStructuredAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/tool/compile_block":
                    await HandleToolCompileBlockAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/tool/ask_user":
                    await HandleToolAskUserAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/tool/import_block":
                    await HandleToolImportBlockAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/tool/search_project":
                    await HandleToolSearchProjectAsync(ctx).ConfigureAwait(false);
                    return;

                // Structured knowledge retrieval endpoints (C-4): share InvokeKnowledgeToolAsync with /mcp
                case "/api/tool/build_context_pack":
                    await HandleKnowledgeToolAsync(ctx, "build_context_pack").ConfigureAwait(false);
                    return;

                case "/api/tool/query_variable_references":
                    await HandleKnowledgeToolAsync(ctx, "query_variable_references").ConfigureAwait(false);
                    return;

                case "/api/tool/query_call_chain":
                    await HandleKnowledgeToolAsync(ctx, "query_call_chain").ConfigureAwait(false);
                    return;

                case "/api/tool/query_variable_impact":
                    await HandleKnowledgeToolAsync(ctx, "query_variable_impact").ConfigureAwait(false);
                    return;

                case "/api/tool/get_current_project":
                    await HandleKnowledgeToolAsync(ctx, "get_current_project").ConfigureAwait(false);
                    return;

                case "/api/tool/get_environment_profile":
                    await HandleToolEnvironmentProfileAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/tool/describe_knowledge_schema":
                    await HandleToolDescribeKnowledgeSchemaAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/tool/record_repair_experience":
                    await HandleToolRecordRepairExperienceAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/tool/query_repair_experience":
                    await HandleToolQueryRepairExperienceAsync(ctx).ConfigureAwait(false);
                    return;

                case "/api/tool/feedback_repair_experience":
                    await HandleToolFeedbackRepairExperienceAsync(ctx).ConfigureAwait(false);
                    return;

                case "/mcp":
                    await HandleMcpJsonRpcAsync(ctx).ConfigureAwait(false);
                    return;

                default:
                    WriteJson(ctx.Response, 404, new { error = "Not Found", path });
                    return;
            }
        }

        // ---------------------------------------------------------------------
        // Business endpoint implementations
        // ---------------------------------------------------------------------

        /// <summary>
        /// GET /api/block_code?name=&lt;blockName&gt;[&amp;format=ir]
        /// Returns raw XML by default; format=ir returns the Agent IR JSON
        /// (canonical JSON; the exact shape the write tools accept). Block not found or export failure => 200 +
        /// {"error": "Block '<name>' not found in project."}
        /// </summary>
        private async Task HandleBlockCodeAsync(HttpListenerContext ctx)
        {
            string name = GetQueryValue(ctx.Request, "name");
            string format = GetQueryValue(ctx.Request, "format") ?? "raw";
            int.TryParse(GetQueryValue(ctx.Request, "network_start"), out int networkStart);
            int.TryParse(GetQueryValue(ctx.Request, "network_count"), out int networkCount);
            if (string.IsNullOrWhiteSpace(name))
            {
                WriteJson(ctx.Response, 400, new { error = "Missing required query parameter 'name'." });
                return;
            }

            string code = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    code = await _tia.ExportBlockCodeAsync(name).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                }
            }).ConfigureAwait(false);

            if (code != null)
            {
                if (string.Equals(format, "ir", StringComparison.OrdinalIgnoreCase))
                {
                    // Convert XML -> IR on a background thread (pure CPU, no STA needed)
                    string irJson = null;
                    string irSummary = null;
                    bool irOk = await Task.Run(() =>
                        TiaXmlAgentReferenceBuilder.TryBuildJsonIrRange(code, networkStart, networkCount, out irJson, out irSummary)
                    ).ConfigureAwait(false);

                    if (irOk)
                        WriteJson(ctx.Response, 200, new { ir = irJson, summary = irSummary, format = "ir" });
                    else
                        WriteJson(ctx.Response, 200, new { error = "Failed to convert block XML to Agent IR.", code });
                }
                else
                {
                    WriteJson(ctx.Response, 200, new { code });
                }
            }
            else
            {
                // ExportBlockCodeAsync throws InvalidOperationException "Block '<name>' not found in project."
                // Reuse its message directly; other exceptions follow the same contract to avoid leaking internals.
                string msg = string.IsNullOrEmpty(errorMsg)
                    ? "Block '" + name + "' not found in project."
                    : errorMsg;
                WriteJson(ctx.Response, 200, new { error = msg });
            }
        }

        /// <summary>
        /// GET /api/variable_type?variable=&lt;name&gt;&amp;block=&lt;blockName&gt;?
        /// Looks in TagTableGroup first; if not found and block is non-empty, looks in that
        /// block's interface; if still not found => {"error":"Variable '<>' not found"}.
        /// </summary>
        private async Task HandleVariableTypeAsync(HttpListenerContext ctx)
        {
            string variable = GetQueryValue(ctx.Request, "variable");
            string block = GetQueryValue(ctx.Request, "block") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(variable))
            {
                WriteJson(ctx.Response, 400, new { error = "Missing required query parameter 'variable'." });
                return;
            }

            string typeName = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    typeName = await _tia.FindVariableTypeAsync(variable, block).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                }
            }).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(typeName))
            {
                WriteJson(ctx.Response, 200, new { type = typeName });
            }
            else if (!string.IsNullOrEmpty(errorMsg))
            {
                WriteJson(ctx.Response, 200, new { error = errorMsg });
            }
            else
            {
                WriteJson(ctx.Response, 200, new { error = "Variable '" + variable + "' not found" });
            }
        }

        /// <summary>
        /// GET /api/project_blocks → {"blocks": ["Name: FB", ...]} (up to 100 items)
        /// </summary>
        private async Task HandleProjectBlocksAsync(HttpListenerContext ctx)
        {
            IList<string> blocks = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    blocks = await CollectBlocksAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                }
            }).ConfigureAwait(false);

            if (blocks != null)
            {
                WriteJson(ctx.Response, 200, new { blocks });
            }
            else
            {
                WriteJson(ctx.Response, 200, new { error = errorMsg ?? "Failed to enumerate blocks." });
            }
        }

        // ---------------------------------------------------------------------
        // MCP extension endpoints (Phase B: complete the MCP call chain)
        // ---------------------------------------------------------------------

        /// <summary>
        /// GET /api/tags?limit=500 → {"tags": ["TableName.TagName: DataType @ Address", ...]}
        /// </summary>
        private async Task HandleTagsAsync(HttpListenerContext ctx)
        {
            int limit = 500;
            var limitStr = GetQueryValue(ctx.Request, "limit");
            if (!string.IsNullOrWhiteSpace(limitStr) && int.TryParse(limitStr, out var parsed))
                limit = Math.Max(1, Math.Min(parsed, 1000));

            IList<string> tags = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    tags = await _tia.ListTagTablesAsync(limit).ConfigureAwait(false);
                }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (tags != null)
                WriteJson(ctx.Response, 200, new { tags });
            else
                WriteJson(ctx.Response, 200, new { error = errorMsg ?? "Failed to list tags." });
        }

        /// <summary>
        /// GET /api/db_structure?name=DB1 → {"name":"DB1", "members": ["Var1: Int", ...]}
        /// </summary>
        private async Task HandleDbStructureAsync(HttpListenerContext ctx)
        {
            string name = GetQueryValue(ctx.Request, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                WriteJson(ctx.Response, 400, new { error = "Missing required query parameter 'name'." });
                return;
            }

            IList<string> members = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    members = await _tia.GetDbStructureAsync(name).ConfigureAwait(false);
                }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (members != null)
                WriteJson(ctx.Response, 200, new { name, members });
            else
                WriteJson(ctx.Response, 200, new { error = errorMsg ?? $"DB '{name}' not found." });
        }

        /// <summary>
        /// GET /api/udt_types?limit=200 → {"udts": ["MyUDT (12 members)", ...]}
        /// </summary>
        private async Task HandleUdtTypesAsync(HttpListenerContext ctx)
        {
            int limit = 200;
            var limitStr = GetQueryValue(ctx.Request, "limit");
            if (!string.IsNullOrWhiteSpace(limitStr) && int.TryParse(limitStr, out var parsed))
                limit = Math.Max(1, Math.Min(parsed, 500));

            IList<string> udts = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    udts = await _tia.ListUdtTypesAsync(limit).ConfigureAwait(false);
                }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (udts != null)
                WriteJson(ctx.Response, 200, new { udts });
            else
                WriteJson(ctx.Response, 200, new { error = errorMsg ?? "Failed to list UDT types." });
        }

        /// <summary>
        /// GET /api/bootstrap → BootstrapInfo JSON
        /// </summary>
        private async Task HandleBootstrapAsync(HttpListenerContext ctx)
        {
            BootstrapInfo info = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    info = await _tia.BootstrapAsync().ConfigureAwait(false);
                }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (info != null)
                WriteJson(ctx.Response, 200, info);
            else
                WriteJson(ctx.Response, 200, new { error = errorMsg ?? "Bootstrap failed." });
        }

        /// <summary>
        /// GET|POST /api/tool/get_environment_profile → live EnvironmentProfileInfo JSON.
        /// Same payload as the MCP tool; the Python side calls this before planning/execution.
        /// </summary>
        private async Task HandleToolEnvironmentProfileAsync(HttpListenerContext ctx)
        {
            EnvironmentProfileInfo info = null;
            string errorMsg = null;
            await _runOnTiaSta(() =>
            {
                try { info = _tia.GetEnvironmentProfile(); }
                catch (Exception ex) { errorMsg = ex.Message; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (info != null)
                WriteJson(ctx.Response, 200, info);
            else
                WriteJson(ctx.Response, 200, new { error = errorMsg ?? "Environment profile unavailable." });
        }

        /// <summary>
        /// POST /api/tool/compile_block {"block_name":"FB_Motor"} → structured result for that block only.
        /// Same payload as the MCP tool; the executor uses it as the gentle post-write check.
        /// </summary>
        private async Task HandleToolCompileBlockAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { ok = false, error = "Use POST" });
                return;
            }

            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            string blockName = null;
            try
            {
                using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body))
                {
                    if (doc.RootElement.TryGetProperty("block_name", out var el))
                        blockName = el.GetString();
                }
            }
            catch { /* malformed body falls through to the validation below */ }

            if (string.IsNullOrWhiteSpace(blockName))
            {
                WriteJson(ctx.Response, 400, new { ok = false, error = "block_name is required" });
                return;
            }

            CompilationResult result = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try { result = await _tia.CompileBlockAsync(blockName).ConfigureAwait(false); }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (result == null)
            {
                WriteJson(ctx.Response, 200, new { ok = false, error = errorMsg ?? "compile_block failed" });
                return;
            }

            WriteJson(ctx.Response, 200, BlockCompileDto(result));
        }

        /// <summary>Structured payload shared by the MCP and REST compile_block channels.</summary>
        private static object BlockCompileDto(CompilationResult r)
            => new
            {
                success = r.Success,
                error_count = r.Errors?.Count ?? 0,
                warning_count = r.Warnings?.Count ?? 0,
                errors = (r.Errors ?? new List<CompilationError>())
                    .Select(e => new { block = e.BlockName ?? "Unknown", message = e.Message ?? "" }).ToList(),
                warnings = (r.Warnings ?? new List<CompilationWarning>())
                    .Select(w => new { block = w.BlockName ?? "Unknown", message = w.Message ?? "" }).ToList(),
            };

        /// <summary>
        /// POST /api/tool/ask_user {"question":"...","detail":"...","hint":"..."}
        /// → {"status":"answered|denied|timed_out|cancelled|unavailable","answer":"..."}
        /// Only used when a compile error names no object (no subject): the agent asks the user
        /// which block/type the error belongs to instead of guessing or testing every candidate.
        /// </summary>
        private async Task HandleToolAskUserAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { ok = false, error = "Use POST" });
                return;
            }

            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            string question = null, detail = null, hint = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.TryGetProperty("question", out var q)) question = q.GetString();
                if (doc.RootElement.TryGetProperty("detail", out var d)) detail = d.GetString();
                if (doc.RootElement.TryGetProperty("hint", out var h)) hint = h.GetString();
            }
            catch { /* malformed body falls through to the validation below */ }

            if (string.IsNullOrWhiteSpace(question))
            {
                WriteJson(ctx.Response, 400, new { ok = false, error = "question is required" });
                return;
            }

            if (_interactionService == null)
            {
                WriteJson(ctx.Response, 200, new
                {
                    status = "unavailable",
                    answer = (string)null,
                    reason = "no interaction service wired into the :8001 server",
                });
                return;
            }

            var request = new Abstractions.InteractionRequest
            {
                Kind = Abstractions.InteractionKind.Input,
                Title = TIAOpennessStudio.Services.WriteGate.CardText.Clean(question, 200),
                Summary = string.IsNullOrWhiteSpace(detail)
                    ? null
                    : TIAOpennessStudio.Services.WriteGate.CardText.CleanMultiline(detail, 20, 2000),
                InputHint = string.IsNullOrWhiteSpace(hint)
                    ? null
                    : TIAOpennessStudio.Services.WriteGate.CardText.Clean(hint, 200),
                Risk = Abstractions.InteractionRisk.Low,
                Source = "Agent",
                Rid = TIAOpennessStudio.Services.WriteGate.CardText.Clean(RidOf(ctx), 64),
            };

            string status;
            string answer = null;
            string reason = null;
            try
            {
                var result = await _interactionService
                    .RequestAsync(request, CancellationToken.None, allowWithoutRun: true)
                    .ConfigureAwait(false);
                status = AskStatusName(result.Status);
                answer = result.IsPositive ? result.Value : null;
                reason = result.Reason;
            }
            catch (Exception ex)
            {
                status = "unavailable";
                reason = ex.Message;
            }

            WriteJson(ctx.Response, 200, new { status, answer, reason });
        }

        private static string AskStatusName(Abstractions.InteractionStatus status)
            => status switch
            {
                Abstractions.InteractionStatus.Answered => "answered",
                Abstractions.InteractionStatus.AutoApproved => "auto_approved",
                Abstractions.InteractionStatus.Denied => "denied",
                Abstractions.InteractionStatus.TimedOut => "timed_out",
                Abstractions.InteractionStatus.Cancelled => "cancelled",
                _ => "unavailable",
            };

        /// <summary>
        /// POST /api/delete_block {"name":"Block1"} → {"deleted":true, "name":"Block1"}
        /// </summary>
        private async Task HandleDeleteBlockAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { error = "Method not allowed. Use POST." });
                return;
            }

            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            string name = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body ?? "{}");
                if (doc.RootElement.TryGetProperty("name", out var nameEl))
                    name = nameEl.GetString();
            }
            catch { /* JSON parse error handled below */ }

            if (string.IsNullOrWhiteSpace(name))
            {
                WriteJson(ctx.Response, 400, new { error = "Missing required field 'name' in JSON body." });
                return;
            }

            var refusal = await AuthorizeWriteAsync(NewWrite(ctx, "delete_block", name)).ConfigureAwait(false);
            if (refusal != null)
            {
                WriteJson(ctx.Response, 200, new { deleted = false, name, error = refusal.Message, code = refusal.Code });
                return;
            }

            bool deleted = false;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    deleted = await _tia.DeleteBlockAsync(name).ConfigureAwait(false);
                }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (deleted)
                WriteJson(ctx.Response, 200, new { deleted = true, name });
            else
                WriteJson(ctx.Response, 200, new { deleted = false, name, error = errorMsg ?? "Block not found or could not be deleted." });
        }

        /// <summary>
        /// GET /api/compile_result → CompilationResult JSON
        /// </summary>
        private async Task HandleCompileResultAsync(HttpListenerContext ctx)
        {
            CompilationResult result = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    result = await _tia.GetCompilationResultAsync().ConfigureAwait(false);
                }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (result != null)
                WriteJson(ctx.Response, 200, result);
            else
                WriteJson(ctx.Response, 200, new { error = errorMsg ?? "Compilation failed." });
        }

        /// <summary>
        /// GET /api/hardware_search?keyword=CPU1214 → {"items": [{...}, ...]}
        /// </summary>
        private async Task HandleHardwareSearchAsync(HttpListenerContext ctx)
        {
            string keyword = GetQueryValue(ctx.Request, "keyword");
            if (string.IsNullOrWhiteSpace(keyword))
            {
                WriteJson(ctx.Response, 400, new { error = "Missing required query parameter 'keyword'." });
                return;
            }

            List<HardwareCatalogItem> items = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    items = await _tia.SearchHardwareCatalogAsync(keyword).ConfigureAwait(false);
                }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (items != null)
                WriteJson(ctx.Response, 200, new { items });
            else
                WriteJson(ctx.Response, 200, new { error = errorMsg ?? "Hardware search failed." });
        }

        /// <summary>
        /// POST /api/import_scl {"blockName":"Block1", "sclCode":"..."} → {"imported":true, "blockName":"Block1"}
        /// Finds the target block in PlcSoftware.BlockGroup, then delegates to ImportSclAsync.
        /// </summary>
        private async Task HandleImportSclAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { error = "Method not allowed. Use POST." });
                return;
            }

            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            string blockName = null, sclCode = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body ?? "{}");
                var root = doc.RootElement;
                if (root.TryGetProperty("blockName", out var bn)) blockName = bn.GetString();
                if (root.TryGetProperty("sclCode", out var sc)) sclCode = sc.GetString();
            }
            catch { /* JSON parse error handled below */ }

            if (string.IsNullOrWhiteSpace(blockName) || string.IsNullOrWhiteSpace(sclCode))
            {
                WriteJson(ctx.Response, 400, new { error = "Missing required fields: blockName, sclCode." });
                return;
            }

            // blockType is empty on purpose: ImportSclAsync always takes the SCL-source path.
            var refusal = await AuthorizeWriteAsync(NewWrite(ctx, "import_scl", blockName, "", sclCode)).ConfigureAwait(false);
            if (refusal != null)
            {
                WriteJson(ctx.Response, 200, new { imported = false, blockName, error = refusal.Message, code = refusal.Code });
                return;
            }

            bool imported = false;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    var plcSw = _tia.PlcSoftware;
                    if (plcSw == null) { errorMsg = "No PLC Software connected."; return; }

                    // Find block recursively in BlockGroup
                    dynamic FindRecursive(dynamic group, string n)
                    {
                        if (group == null) return null;
                        try
                        {
                            var b = group.Blocks.Find(n);
                            if (b != null) return b;
                            foreach (var g in group.Groups)
                            {
                                var found = FindRecursive(g, n);
                                if (found != null) return found;
                            }
                        }
                        catch { }
                        return null;
                    }

                    var block = FindRecursive(plcSw.BlockGroup, blockName);
                    if (block == null) { errorMsg = $"Block '{blockName}' not found in project."; return; }

                    imported = await _tia.ImportSclAsync(block as Siemens.Engineering.SW.Blocks.PlcBlock, sclCode).ConfigureAwait(false);
                }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (imported)
                WriteJson(ctx.Response, 200, new { imported = true, blockName });
            else
                WriteJson(ctx.Response, 200, new { imported = false, blockName, error = errorMsg ?? "Import failed." });
        }

        // ─────────────────────────────────────────────────────────────────
        // New: Agent tool endpoints (for the Python LangGraph Agent to call over HTTP)
        // Unified return shape: {"ok": true, "result": "..."} or {"ok": false, "error": "..."}
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Slice a whole-block read into a page and report its position, so the agent can
        /// continue with offset instead of pulling the whole block into every later model call.
        /// </summary>
        private static string PaginateText(string text, int offset, int length, string what)
        {
            text = text ?? string.Empty;
            if (offset < 0) offset = 0;
            if (length <= 0) length = DefaultBlockReadChars;

            int total = text.Length;
            if (offset >= total)
            {
                return "char_start: " + offset + ", char_end: " + offset +
                       ", content_length: " + total + ", has_more: false\n\n" +
                       "(offset is past the end of " + what + ")";
            }

            int end = Math.Min(total, offset + length);
            bool hasMore = end < total;

            var sb = new System.Text.StringBuilder();
            sb.Append("char_start: ").Append(offset)
              .Append(", char_end: ").Append(end)
              .Append(", content_length: ").Append(total)
              .Append(", has_more: ").Append(hasMore ? "true" : "false")
              .AppendLine();
            sb.AppendLine();
            sb.Append(text, offset, end - offset);
            if (hasMore)
                sb.AppendLine().Append("(more content follows — call the same tool again with offset=")
                  .Append(end).Append(")");
            return sb.ToString();
        }

        // Argument helpers shared by get_block_content and get_db_structure. One live XML export feeds
        // every projection, so the four formats can never disagree about the same block.
        /// <summary>
        /// Read the first non-empty string among <paramref name="keys"/>. Models sometimes send
        /// camelCase (``blockName``) or a legacy key; accepting the alias is cheaper than a wasted
        /// round trip, and the tool contract stays snake_case for everything else.
        /// </summary>
        private static string ReadTextArgument(JsonElement arguments, params string[] keys)
        {
            if (arguments.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in keys)
            {
                if (arguments.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                {
                    var value = el.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            return null;
        }

        /// <summary>
        /// Error text that names the required argument, the accepted ones and what the caller actually
        /// sent — so the model can fix its call instead of guessing the key again.
        /// </summary>
        private static string MissingArgumentMessage(JsonElement arguments, string required, params string[] accepted)
        {
            var sent = new List<string>();
            if (arguments.ValueKind == JsonValueKind.Object)
                foreach (var p in arguments.EnumerateObject()) sent.Add(p.Name);
            return "'" + required + "' is required. Accepted arguments: " + string.Join(", ", accepted)
                   + ". Received: " + (sent.Count == 0 ? "(none)" : string.Join(", ", sent)) + ".";
        }

        private async Task<(string resultText, string errorText)> HandleGetBlockContentAsync(
            JsonElement arguments, string forcedFormat, bool envelope)
        {
            string blockName = ReadTextArgument(arguments, "block_name", "blockName", "name");
            if (string.IsNullOrWhiteSpace(blockName))
                return (null, MissingArgumentMessage(arguments, "block_name",
                    "block_name", "format", "offset", "length", "network_start", "network_count"));

            var format = forcedFormat;
            if (string.IsNullOrWhiteSpace(format))
            {
                format = arguments.ValueKind == JsonValueKind.Object
                         && arguments.TryGetProperty("format", out var formatEl)
                         && formatEl.ValueKind == JsonValueKind.String
                    ? formatEl.GetString()
                    : "ir";
            }
            format = (format ?? "").Trim().ToLowerInvariant();
            if (format != "ir" && format != "source" && format != "xml" && format != "interface")
                return (null, "format must be one of: ir | source | xml | interface");

            var offset = ReadIntArgument(arguments, "offset");
            var length = ReadIntArgument(arguments, "length");
            var networkStart = ReadIntArgument(arguments, "network_start");
            var networkCount = ReadIntArgument(arguments, "network_count");

            string xml = null;
            string exportError = null;
            await _runOnTiaSta(async () =>
            {
                try { xml = await _tia.ExportBlockCodeAsync(blockName).ConfigureAwait(false); }
                catch (Exception ex) { exportError = ex.Message; }
            }).ConfigureAwait(false);

            if (xml == null)
                return (null, exportError ?? ("Block '" + blockName + "' not found in project."));

            // Projections are pure CPU work (XML parse + canonical JSON/source rebuild); keep them
            // off the STA host thread so a big block never blocks TIA access. The snapshot is built
            // for every format because the envelope carries its revision (patch precondition).
            var projected = await Task.Run(() =>
            {
                BlockSnapshot snapshot = null;
                string text = null;
                string summary = null;
                string error = null;
                try
                {
                    snapshot = BlockSnapshotService.FromXml(xml, null, null);
                    switch (format)
                    {
                        case "xml":
                            text = PaginateText(snapshot.RawXml, offset, length, "block '" + blockName + "'");
                            break;

                        case "source":
                            text = BlockSnapshotService.BuildSource(snapshot);
                            if (text == null)
                            {
                                error = "Block '" + blockName + "' has no SCL/STL text projection (language: "
                                        + (snapshot.Language ?? "") + "). Use format=ir or format=xml.";
                            }
                            else
                            {
                                text = PaginateText(text, offset, length, "block '" + blockName + "' source");
                            }
                            break;

                        case "interface":
                            text = BlockSnapshotService.BuildInterface(snapshot);
                            if (text == null)
                            {
                                error = "Block '" + blockName
                                        + "' has no interface projection (GRAPH exposes members through format=ir).";
                            }
                            break;

                        default: // ir
                            if (!BlockSnapshotService.BuildIr(snapshot, networkStart, networkCount, out text, out summary))
                                error = "Failed to convert block XML to Agent IR.";
                            break;
                    }
                }
                catch (Exception ex)
                {
                    error = "Block projection failed: " + ex.Message;
                }
                return (snapshot, text, summary, error);
            }).ConfigureAwait(false);

            if (projected.error != null)
                return (null, projected.error);
            if (!envelope)
                return (projected.text, null);

            var snapshotResult = projected.snapshot;
            var payload = new JsonObject
            {
                ["format"] = format,
                ["block_name"] = blockName,
                ["block_type"] = snapshotResult?.BlockType ?? "",
                ["xml_block_kind"] = snapshotResult?.XmlBlockKind ?? "",
                ["is_instance_db"] = snapshotResult?.IsInstanceDb ?? false,
                ["source_profile"] = snapshotResult?.SourceProfile ?? "",
                ["revision"] = snapshotResult?.Revision ?? "",
                ["summary"] = projected.summary ?? ""
            };

            if (format == "ir" || format == "interface")
            {
                try { payload["content"] = JsonNode.Parse(projected.text); }
                catch { payload["content"] = projected.text; }
            }
            else
            {
                payload["content"] = projected.text;
            }
            return (payload.ToJsonString(_compactJsonOptions), null);
        }

        private static int ReadIntArgument(JsonElement arguments, string name)
        {
            if (arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty(name, out var element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out var value))
            {
                return value;
            }
            return 0;
        }

        /// <summary>
        /// POST /api/tool/export_block {"block_name":"FB_Motor"}
        /// Exports a block's source code (XML); optional format=ir returns Agent IR JSON.
        /// </summary>
        private async Task HandleToolExportBlockAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { ok = false, error = "Use POST" });
                return;
            }

            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            string blockName = null;
            string format = "raw";
            int networkStart = 0, networkCount = 0;
            try
            {
                using var doc = JsonDocument.Parse(body ?? "{}");
                var root = doc.RootElement;
                if (root.TryGetProperty("block_name", out var bn)) blockName = bn.GetString();
                if (root.TryGetProperty("format", out var fmt)) format = fmt.GetString() ?? "raw";
                if (root.TryGetProperty("network_start", out var ns) && ns.TryGetInt32(out var nsv)) networkStart = nsv;
                if (root.TryGetProperty("network_count", out var nc) && nc.TryGetInt32(out var ncv)) networkCount = ncv;
            }
            catch { }

            if (string.IsNullOrWhiteSpace(blockName))
            {
                WriteJson(ctx.Response, 400, new { ok = false, error = "block_name is required" });
                return;
            }

            string code = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try { code = await _tia.ExportBlockCodeAsync(blockName).ConfigureAwait(false); }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (code != null)
            {
                if (string.Equals(format, "ir", StringComparison.OrdinalIgnoreCase))
                {
                    string irJson = null, irSummary = null;
                    bool irOk = await Task.Run(() =>
                        TiaXmlAgentReferenceBuilder.TryBuildJsonIrRange(code, networkStart, networkCount, out irJson, out irSummary)
                    ).ConfigureAwait(false);

                    if (irOk)
                        WriteJson(ctx.Response, 200, new { ok = true, result = irJson, summary = irSummary, format = "ir" });
                    else
                        WriteJson(ctx.Response, 200, new { ok = false, error = "Failed to convert XML to Agent IR.", raw_code = code });
                }
                else
                {
                    WriteJson(ctx.Response, 200, new { ok = true, result = code });
                }
            }
            else
            {
                WriteJson(ctx.Response, 200, new { ok = false, error = errorMsg ?? "Export failed for block: " + blockName });
            }
        }

        /// <summary>
        /// POST /api/tool/compile {}
        /// Compiles the current TIA project and returns the compile result.
        /// </summary>
        private async Task HandleToolCompileAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { ok = false, error = "Use POST" });
                return;
            }

            string result = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try { result = await _tia.CompileAsync().ConfigureAwait(false); }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (result != null)
                WriteJson(ctx.Response, 200, new { ok = true, result });
            else
                WriteJson(ctx.Response, 200, new { ok = false, error = errorMsg ?? "Compile failed." });
        }

        /// <summary>
        /// POST /api/tool/compile_structured {}
        /// Compiles the current TIA project and returns a **structured** result, so the
        /// agent's fix loop can judge by field (rather than heuristics over plain text).
        /// The envelope's <c>result</c> field is a snake_case JSON string:
        /// <code>
        /// {"success":bool,"error_count":int,"warning_count":int,
        ///  "errors":[{"block":str,"message":str,"line":int}],
        ///  "warnings":[{"block":str,"message":str}]}
        /// </code>
        /// Note: localization precision is capped at **block level**. Siemens compile messages
        /// do not expose line numbers, so <see cref="CompilationError.LineNumber"/> is
        /// currently always 0 — the agent must not locate by line.
        /// </summary>
        private async Task HandleToolCompileStructuredAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { ok = false, error = "Use POST" });
                return;
            }

            CompilationResult compileResult = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try { compileResult = await _tia.GetCompilationResultAsync().ConfigureAwait(false); }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (compileResult == null)
            {
                WriteJson(ctx.Response, 200, new { ok = false, error = errorMsg ?? "Compile failed." });
                return;
            }

            var errors = compileResult.Errors ?? new List<CompilationError>();
            var warnings = compileResult.Warnings ?? new List<CompilationWarning>();
            var structured = new
            {
                success = compileResult.Success,
                error_count = errors.Count,
                warning_count = warnings.Count,
                errors = errors.Select(e => new
                {
                    block = e.BlockName ?? "Unknown",
                    message = e.Message ?? "",
                    line = e.LineNumber   // always 0 (block-level localization); see the method docs
                }).ToList(),
                warnings = warnings.Select(w => new
                {
                    block = w.BlockName ?? "Unknown",
                    message = w.Message ?? ""
                }).ToList(),
            };

            // Serialize the result as a string to keep the same {ok, result} envelope as the
            // other /api/tool/* tools, so Python's `_post_tia_tool` gets a string it can json.loads.
            WriteJson(ctx.Response, 200, new { ok = true, result = JsonSerializer.Serialize(structured, _jsonOptions) });
        }

        /// <summary>
        /// POST /api/tool/record_repair_experience
        /// {"project_key":"...", "symptom":"...", "compiler_error":"...", "causal_text":"...",
        ///  "confidence":0.8, "generality":0.9}
        /// Records one repair experience (error -> root cause -> fix), upserting by the
        /// normalized signature of compiler_error (falling back to symptom).
        /// </summary>
        private async Task HandleToolRecordRepairExperienceAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { ok = false, error = "Use POST" });
                return;
            }

            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            string projectKey = null, symptom = null, causalText = null, compilerError = null;
            double confidence = 0.0, generality = 0.0;
            try
            {
                using var doc = JsonDocument.Parse(body ?? "{}");
                var root = doc.RootElement;
                if (root.TryGetProperty("project_key", out var pk)) projectKey = pk.GetString();
                if (root.TryGetProperty("symptom", out var s)) symptom = s.GetString();
                if (root.TryGetProperty("compiler_error", out var ce)) compilerError = ce.GetString();
                if (root.TryGetProperty("causal_text", out var c)) causalText = c.GetString();
                if (root.TryGetProperty("confidence", out var conf) && conf.TryGetDouble(out var cv)) confidence = cv;
                if (root.TryGetProperty("generality", out var gen) && gen.TryGetDouble(out var gv)) generality = gv < 0 ? 0 : (gv > 1 ? 1 : gv);
            }
            catch { }

            if (_projectIndex == null)
            {
                WriteJson(ctx.Response, 200, new { ok = false, error = "repair_experience store unavailable" });
                return;
            }
            if (string.IsNullOrWhiteSpace(projectKey) || string.IsNullOrWhiteSpace(symptom) || string.IsNullOrWhiteSpace(causalText))
            {
                WriteJson(ctx.Response, 400, new { ok = false, error = "project_key, symptom, causal_text are required" });
                return;
            }

            try
            {
                _projectIndex.RecordRepairExperience(projectKey, symptom, causalText, compilerError, confidence, generality);
                WriteJson(ctx.Response, 200, new { ok = true, result = "recorded" });
            }
            catch (Exception ex)
            {
                WriteJson(ctx.Response, 200, new { ok = false, error = ex.Message });
            }
        }

        /// <summary>
        /// POST /api/tool/query_repair_experience {"project_key":"...", "query":"", "limit":3}
        /// Retrieves repair experiences by project, returning symptom/causal_text/confidence/count.
        /// </summary>
        private async Task HandleToolQueryRepairExperienceAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { ok = false, error = "Use POST" });
                return;
            }

            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            string projectKey = null, query = "";
            int limit = 3;
            try
            {
                using var doc = JsonDocument.Parse(body ?? "{}");
                var root = doc.RootElement;
                if (root.TryGetProperty("project_key", out var pk)) projectKey = pk.GetString();
                if (root.TryGetProperty("query", out var q)) query = q.GetString() ?? "";
                if (root.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv)) limit = lv;
            }
            catch { }

            if (_projectIndex == null)
            {
                WriteJson(ctx.Response, 200, new { ok = false, error = "repair_experience store unavailable" });
                return;
            }
            if (string.IsNullOrWhiteSpace(projectKey))
            {
                // Fall back to the currently open TIA project when the tool does not pass project_key (C# side isolation).
                projectKey = await SafeGetCurrentProjectKeyAsync().ConfigureAwait(false);
            }

            try
            {
                var entries = _projectIndex.SearchRepairExperience(projectKey, query, limit);
                var result = entries.Select(e => new
                {
                    symptom = e.Symptom,
                    causal_text = e.CausalText,
                    signature = e.Signature,
                    confidence = e.Confidence,
                    success_count = e.SuccessCount,
                    failure_count = e.FailureCount,
                }).ToList();
                WriteJson(ctx.Response, 200, new { ok = true, result = JsonSerializer.Serialize(result, _jsonOptions) });
            }
            catch (Exception ex)
            {
                WriteJson(ctx.Response, 200, new { ok = false, error = ex.Message });
            }
        }

        /// <summary>
        /// POST /api/tool/feedback_repair_experience {"signatures":["..."], "success":true}
        /// Bumps the success/failure counter of the repair experiences identified by the
        /// given normalized signatures, closing the evaluation feedback loop. Called after
        /// a compile-fix round resolves (or exhausts).
        /// </summary>
        private async Task HandleToolFeedbackRepairExperienceAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { ok = false, error = "Use POST" });
                return;
            }

            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            var signatures = new List<string>();
            var success = true;
            try
            {
                using var doc = JsonDocument.Parse(body ?? "{}");
                var root = doc.RootElement;
                if (root.TryGetProperty("signatures", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) signatures.Add(s);
                    }
                }
                if (root.TryGetProperty("success", out var sc)) success = sc.ValueKind != JsonValueKind.False;
            }
            catch { }

            if (_projectIndex == null)
            {
                WriteJson(ctx.Response, 200, new { ok = false, error = "repair_experience store unavailable" });
                return;
            }

            try
            {
                _projectIndex.FeedbackRepairExperience(signatures, success);
                WriteJson(ctx.Response, 200, new { ok = true, result = "feedback recorded" });
            }
            catch (Exception ex)
            {
                WriteJson(ctx.Response, 200, new { ok = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Returns the full file path of the current TIA project as project_key (so repair
        /// experience tools isolate by current project). Falls back to "default-project"
        /// when there is no project. Never throws.
        /// </summary>
        private async Task<string> SafeGetCurrentProjectKeyAsync()
        {
            try
            {
                string key = null;
                await _runOnTiaSta(() =>
                {
                    key = _tia?.Project?.Path?.FullName;
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(key) ? "default-project" : key;
            }
            catch (Exception)
            {
                return "default-project";
            }
        }

        /// <summary>
        /// POST /api/tool/import_block {"block_name":"FB_Motor", "block_type":"FB", "code":"...", "group_path":""}
        /// Imports code into the specified block of the TIA Portal project.
        /// </summary>
        private async Task HandleToolImportBlockAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { ok = false, error = "Use POST" });
                return;
            }

            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            string blockName = null, blockType = null, code = null, groupPath = "";
            try
            {
                using var doc = JsonDocument.Parse(body ?? "{}");
                var root = doc.RootElement;
                if (root.TryGetProperty("block_name", out var bn)) blockName = bn.GetString();
                if (root.TryGetProperty("block_type", out var bt)) blockType = bt.GetString();
                if (root.TryGetProperty("code", out var c)) code = c.GetString();
                if (root.TryGetProperty("group_path", out var gp)) groupPath = gp.GetString() ?? "";
            }
            catch { }

            if (string.IsNullOrWhiteSpace(blockName) || string.IsNullOrWhiteSpace(blockType) || string.IsNullOrWhiteSpace(code))
            {
                WriteJson(ctx.Response, 400, new { ok = false, error = "block_name, block_type, code are required" });
                return;
            }

            var refusal = await AuthorizeWriteAsync(NewWrite(ctx, "import_block", blockName, blockType, code, groupPath)).ConfigureAwait(false);
            if (refusal != null)
            {
                // ok:false + error is what the Python client turns into "Error: ..." for the agent.
                WriteJson(ctx.Response, 200, new { ok = false, error = refusal.Message, code = refusal.Code });
                return;
            }

            string result = null;
            string errorMsg = null;
            await _runOnTiaSta(async () =>
            {
                try
                {
                    result = await _tia.ImportBlockCodeAsync(blockName, blockType, code, groupPath).ConfigureAwait(false);
                }
                catch (Exception ex) { errorMsg = ex.Message; }
            }).ConfigureAwait(false);

            if (result != null)
                WriteJson(ctx.Response, 200, new { ok = true, result });
            else
                WriteJson(ctx.Response, 200, new { ok = false, error = errorMsg ?? "Import failed for block: " + blockName });
        }

        /// <summary>
        /// POST /api/tool/search_project {"query":"Motor","limit":10}
        /// Searches blocks, variables, etc. in the TIA project knowledge index (the Worker's
        /// real SQLite index). Shares the same <see cref="InvokeKnowledgeToolAsync"/> entry
        /// point as /mcp's search_project, so both channels behave identically (eliminates
        /// the historical "REST shell / MCP real" dual-implementation drift).
        /// </summary>
        private Task HandleToolSearchProjectAsync(HttpListenerContext ctx)
            => HandleKnowledgeToolAsync(ctx, "search_project");

        /// <summary>
        /// Generic REST handler for the knowledge-index (RAG) tools. Parses the POST body as
        /// the tool arguments, dispatches through the shared <see cref="InvokeKnowledgeToolAsync"/>
        /// (same entry the MCP channel uses), and wraps the result in the standard
        /// {ok, result|error} envelope that the Python `_post_tia_tool` client expects.
        ///
        /// Used by /api/tool/search_project, /build_context_pack, /query_variable_references,
        /// /query_call_chain and /query_variable_impact.
        /// </summary>
        private async Task HandleKnowledgeToolAsync(HttpListenerContext ctx, string toolName)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { ok = false, error = "Use POST" });
                return;
            }

            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            JsonElement args;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                args = doc.RootElement.Clone();
            }
            catch
            {
                WriteJson(ctx.Response, 400, new { ok = false, error = "Invalid JSON body" });
                return;
            }

            var invocation = await InvokeKnowledgeToolAsync(toolName, args).ConfigureAwait(false);
            if (invocation.IsError)
                WriteJson(ctx.Response, 200, new { ok = false, error = invocation.Error });
            else
                WriteJson(ctx.Response, 200, new { ok = true, result = invocation.Result });
        }

        /// <summary>
        /// Result of a knowledge-tool invocation. Exactly one of <see cref="Result"/> or
        /// <see cref="Error"/> is populated. This is the single shared shape returned by
        /// <see cref="InvokeKnowledgeToolAsync"/> so REST handlers and MCP cases can present
        /// it in their own envelope without duplicating the invocation logic.
        /// </summary>
        private readonly struct KnowledgeToolInvocation
        {
            public string Result { get; }
            public string Error { get; }
            public bool IsError => Error != null;

            private KnowledgeToolInvocation(string result, string error)
            {
                Result = result;
                Error = error;
            }

            public static KnowledgeToolInvocation Ok(string result) => new KnowledgeToolInvocation(result, null);
            public static KnowledgeToolInvocation Fail(string error) => new KnowledgeToolInvocation(null, error ?? "unknown error");
        }

        /// <summary>
        /// Single source of truth for the knowledge-index (RAG) tools backed by
        /// <see cref="_workerHost"/>: search_project / build_context_pack /
        /// query_variable_references / query_call_chain / query_variable_impact.
        ///
        /// Both the REST endpoints (/api/tool/*) and the MCP tool-call dispatcher call this
        /// method, so the two channels can never drift apart again. Argument extraction,
        /// null-guarding, invocation and JSON serialization all live here; callers only wrap
        /// the returned <see cref="KnowledgeToolInvocation"/> in their own response envelope.
        ///
        /// These tools query the Worker's SQLite index only — they never touch the TIA STA
        /// thread, so they are safe to run on any HTTP worker thread.
        /// </summary>
        /// <param name="toolName">Knowledge tool name (see list above).</param>
        /// <param name="arguments">Parsed JSON arguments object.</param>
        private async Task<KnowledgeToolInvocation> InvokeKnowledgeToolAsync(string toolName, JsonElement arguments)
        {
            if (_workerHost == null)
                return KnowledgeToolInvocation.Fail("knowledge worker not available");

            string GetString(string name) =>
                arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : null;

            int GetInt(string name, int fallback) =>
                arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty(name, out var el) && el.TryGetInt32(out var v)
                    ? v
                    : fallback;

            try
            {
                // Project-awareness: resolve the caller's project (main process) and the
                // project the Worker is actually attached to. Both are fail-closed: any
                // failure yields null and the mismatch check is skipped.
                string requestedKey = null;
                try
                {
                    var k = await SafeGetCurrentProjectKeyAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(k) && k != "default-project")
                        requestedKey = k;
                }
                catch { }

                TIAOpennessStudio.Models.WorkerCurrentProjectInfo proj = null;
                try { proj = await _workerHost.GetCurrentProjectInfoAsync().ConfigureAwait(false); } catch { }

                if (toolName == "get_current_project")
                {
                    return KnowledgeToolInvocation.Ok(JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        project_key = proj?.ProjectKey,
                        project_name = proj?.ProjectName,
                        is_connected = proj?.IsConnected ?? false,
                        has_index = proj?.HasIndex ?? false,
                        is_stale = proj?.IsStale ?? false,
                        is_indexing = proj?.IsIndexing ?? false
                    }, _jsonOptions));
                }

                // Mismatch: do NOT auto-switch; return an explicit notice with empty hits.
                if (requestedKey != null && !string.IsNullOrWhiteSpace(proj?.ProjectKey)
                    && !string.Equals(requestedKey, proj.ProjectKey, StringComparison.OrdinalIgnoreCase))
                {
                    return KnowledgeToolInvocation.Ok(JsonSerializer.Serialize(new
                    {
                        status = "project_mismatch",
                        requested_key = requestedKey,
                        attached_key = proj.ProjectKey,
                        attached_name = proj.ProjectName,
                        hits = new object[0]
                    }, _jsonOptions));
                }

                // Uniform success envelope: {status, project_key, project_name, result}.
                KnowledgeToolInvocation WrapOk(object result) => KnowledgeToolInvocation.Ok(JsonSerializer.Serialize(new
                {
                    status = "ok",
                    project_key = proj?.ProjectKey,
                    project_name = proj?.ProjectName,
                    result
                }, _jsonOptions));

                switch (toolName)
                {
                    case "search_project":
                    {
                        var query = GetString("query");
                        if (string.IsNullOrWhiteSpace(query))
                            return KnowledgeToolInvocation.Fail("query is required");
                        var limit = GetInt("limit", 10);
                        var hits = await _workerHost.SearchKnowledgeAsync(query, limit, projectKey: requestedKey).ConfigureAwait(false);
                        return WrapOk(hits ?? new List<TIAOpennessStudio.Models.PlcKnowledgeSearchHit>());
                    }

                    case "build_context_pack":
                    {
                        var query = GetString("query");
                        if (string.IsNullOrWhiteSpace(query))
                            return KnowledgeToolInvocation.Fail("query is required");
                        var topK = GetInt("top_k", 5);
                        var pack = await _workerHost.BuildContextPackAsync(query, null, topK, projectKey: requestedKey).ConfigureAwait(false);
                        return WrapOk(pack);
                    }

                    case "query_variable_references":
                    {
                        var varPath = GetString("variable_path");
                        if (string.IsNullOrWhiteSpace(varPath))
                            return KnowledgeToolInvocation.Fail("variable_path is required");
                        var direction = GetString("direction") ?? "both";
                        var topK = GetInt("top_k", 20);
                        var refs = await _workerHost.QueryVariableReferencesAsync(varPath, direction, topK, projectKey: requestedKey).ConfigureAwait(false);
                        return WrapOk(refs);
                    }

                    case "query_call_chain":
                    {
                        var ccBlock = GetString("block_name");
                        if (string.IsNullOrWhiteSpace(ccBlock))
                            return KnowledgeToolInvocation.Fail("block_name is required");
                        var ccDir = GetString("direction") ?? "both";
                        var ccDepth = GetInt("max_depth", 5);
                        var ccNodes = GetInt("max_node_count", 200);
                        var chain = await _workerHost.QueryCallChainAsync(ccBlock, ccDir, ccDepth, ccNodes, projectKey: requestedKey).ConfigureAwait(false);
                        return WrapOk(chain);
                    }

                    case "query_variable_impact":
                    {
                        var viPath = GetString("canonical_path");
                        if (string.IsNullOrWhiteSpace(viPath))
                            return KnowledgeToolInvocation.Fail("canonical_path is required");
                        var viDir = GetString("direction") ?? "both";
                        var viDepth = GetInt("max_depth", 5);
                        var viNodes = GetInt("max_node_count", 200);
                        var impact = await _workerHost.QueryVariableImpactAsync(viPath, viDir, viDepth, viNodes, projectKey: requestedKey).ConfigureAwait(false);
                        return WrapOk(impact);
                    }

                    default:
                        return KnowledgeToolInvocation.Fail($"unknown knowledge tool: {toolName}");
                }
            }
            catch (Exception ex)
            {
                return KnowledgeToolInvocation.Fail(ex.Message);
            }
        }

        /// <summary>
        /// POST /api/tool/describe_knowledge_schema {"table":"plc_block"}  (table optional)
        /// Returns the data dictionary of the RAG knowledge index (table structure / field
        /// meaning / allowed values / relations). Purely static: the dictionary is provided by
        /// <see cref="KnowledgeSchemaDoc"/> and does not touch the live project or the STA thread.
        /// Empty table returns the full dictionary; a given table returns only that table entry.
        /// </summary>
        private async Task HandleToolDescribeKnowledgeSchemaAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { ok = false, error = "Use POST" });
                return;
            }

            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            string table = null;
            try
            {
                using var doc = JsonDocument.Parse(body ?? "{}");
                if (doc.RootElement.TryGetProperty("table", out var t)) table = t.GetString();
            }
            catch { }

            try
            {
                var text = KnowledgeSchemaDoc.Describe(table);
                WriteJson(ctx.Response, 200, new { ok = true, result = text });
            }
            catch (Exception ex)
            {
                WriteJson(ctx.Response, 200, new { ok = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Recursively walks <c>PlcSoftware.BlockGroup</c>, producing a list like
        /// <c>"Name: FB"</c>. Stops as soon as <c>list.Count &gt;= limit</c>; also returns
        /// immediately when the recursion depth exceeds <paramref name="maxDepth"/> (avoids a
        /// stack overflow / timeout from abnormally deep groups).
        /// Note: <see cref="HandleProjectBlocksAsync"/> already calls this inside
        /// <see cref="_runOnTiaSta"/>, so this method must not marshal STA again (avoiding
        /// double dispatch).
        /// </summary>
        private Task<IList<string>> CollectBlocksAsync(int limit = 100, int maxDepth = 10)
        {
            IList<string> list = new List<string>();
            var plcSw = _tia.PlcSoftware;
            if (plcSw == null) return Task.FromResult(list);

            // Use a local function + dynamic to handle both PlcBlockSystemGroup (top-level) and
            // PlcBlockUserGroup (subgroups); both expose .Blocks and .Groups but with different
            // API types, and dynamic keeps us independent of Siemens.Engineering type signatures.
            void Walk(dynamic group, int depth)
            {
                if (group == null) return;
                if (depth > maxDepth || list.Count >= limit) return;

                try
                {
                    foreach (var b in group.Blocks)
                    {
                        if (list.Count >= limit) return;
                        string name = (b?.Name as string) ?? string.Empty;
                        list.Add(name + ": " + InferShortType((object)b));
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[TiaCtxHttp] CollectBlocksAsync Blocks iteration failed at depth {depth}: {ex.Message}");
                }

                try
                {
                    foreach (var g in group.Groups)
                    {
                        if (list.Count >= limit) return;
                        Walk(g, depth + 1);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[TiaCtxHttp] CollectBlocksAsync Groups iteration failed at depth {depth}: {ex.Message}");
                }
            }

            try
            {
                Walk(plcSw.BlockGroup, 0);
            }
            catch (Exception ex)
            {
                AppLogger.Log("[TiaCtxHttp] CollectBlocksAsync top-level failure: " + ex.Message);
            }

            return Task.FromResult(list);
        }

        /// <summary>
        /// Lightweight PLC program directory: flatten the live block/type tree into
        /// { plc, groups[], blocks[{name,type,number,language,path}], types[], tag_tables[], truncated }.
        /// Structure only — no XML export, no knowledge index, no writes. Used by the agent to decide
        /// create-vs-modify, find a free block number, or pick a group path before a write.
        /// </summary>
        private async Task<object> CollectProgramTreeAsync(bool includeTypes, bool includeTagTables, int maxBlocks)
        {
            var tree = await _tia.GetBlockTreeAsync().ConfigureAwait(false);
            if (tree == null)
            {
                // Returning an empty directory would read as "the project has no blocks" and could make
                // the agent re-create existing blocks. Fail loudly instead (the caller turns it into errorText).
                throw new InvalidOperationException(
                    "No PLC software is available to read. Open a project and select a PLC (or make sure the " +
                    "project contains exactly one PLC), then call get_program_tree again.");
            }
            var groups = new List<string>();
            var blocks = new List<object>();
            var types = new List<object>();
            var tagTables = new List<object>();
            bool truncated = false;

            void Walk(PlcTreeNodeDto node, bool inTypes, bool inTags)
            {
                if (node?.Children == null) return;
                foreach (var child in node.Children)
                {
                    if (child == null) continue;
                    switch (child.Kind)
                    {
                        case PlcTreeNodeKind.BlockGroup:
                            if (!inTypes && !inTags)
                            {
                                if (!string.IsNullOrEmpty(child.GroupPath)) groups.Add(child.GroupPath);
                                Walk(child, false, false);
                            }
                            break;

                        case PlcTreeNodeKind.Block:
                            if (!inTypes && !inTags)
                            {
                                if (blocks.Count >= maxBlocks) { truncated = true; }
                                else
                                {
                                    blocks.Add(new
                                    {
                                        name = child.Name,
                                        type = child.BlockType,
                                        number = child.Number,
                                        language = child.Language,
                                        path = child.GroupPath,
                                    });
                                }
                            }
                            break;

                        case PlcTreeNodeKind.TypeGroup:
                            if (includeTypes) Walk(child, true, false);
                            break;

                        case PlcTreeNodeKind.Type:
                            if (inTypes && includeTypes)
                                types.Add(new { name = child.Name, path = child.GroupPath });
                            break;

                        case PlcTreeNodeKind.TagTableGroup:
                            if (includeTagTables) Walk(child, false, true);
                            break;

                        case PlcTreeNodeKind.TagTable:
                            if (inTags && includeTagTables)
                                tagTables.Add(new { name = child.Name, path = child.GroupPath });
                            break;

                        default:
                            Walk(child, inTypes, inTags);
                            break;
                    }
                }
            }

            if (tree != null) Walk(tree, false, false);

            return new
            {
                plc = tree?.Name ?? "",
                groups,
                blocks,
                types,
                tag_tables = tagTables,
                truncated,
            };
        }

        /// <summary>
        /// Infers the short block type string (FB / FC / DB / OB / BLOCK) from the
        /// <see cref="object.GetType()"/> name. Rules match the style of
        /// <c>ProjectViewModel.ResolveBlockType</c>:
        /// <list type="bullet">
        /// <item>Type name equals "FB"/"FC"/"OB" → direct mapping (those are the standard Openness class names)</item>
        /// <item>Name contains "FunctionBlock" → FB (checked before Function to avoid swallowing)</item>
        /// <item>Name contains "Organization" → OB</item>
        /// <item>Name contains "DataBlock" or ends with "DB" (e.g. GlobalDB/InstanceDB/ArrayDB) → DB</item>
        /// <item>Name contains "Function" → FC</item>
        /// <item>Otherwise → BLOCK (fallback)</item>
        /// </list>
        /// </summary>
        private static string InferShortType(object block)
        {
            if (block == null) return "BLOCK";

            string typeName;
            try
            {
                typeName = block.GetType().Name ?? string.Empty;
            }
            catch
            {
                return "BLOCK";
            }

            if (typeName.Equals("OB", StringComparison.OrdinalIgnoreCase)) return "OB";
            if (typeName.Equals("FB", StringComparison.OrdinalIgnoreCase)) return "FB";
            if (typeName.Equals("FC", StringComparison.OrdinalIgnoreCase)) return "FC";

            string low = typeName.ToLowerInvariant();
            // FunctionBlock must be checked first (contains the "function" substring)
            if (low.Contains("functionblock")) return "FB";
            if (low.Contains("organization")) return "OB";
            // DB family: DataBlock / GlobalDB / InstanceDB / ArrayDB
            if (low.Contains("datablock") || low.EndsWith("db")) return "DB";
            if (low.Contains("function")) return "FC";
            return "BLOCK";
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets the requested query-string parameter, URL-decoded; missing or empty => null.
        /// </summary>
        private static string GetQueryValue(HttpListenerRequest request, string key)
        {
            // HttpListenerRequest.QueryString decodes percent-encoded values with the
            // system default code page (GBK on Chinese Windows), corrupting UTF-8
            // Chinese parameters such as block names ("考试" -> "鑰冭"). Parse the raw
            // query ourselves and decode explicitly as UTF-8, mirroring the fix already
            // applied to ReadRequestBodyAsync for POST bodies.
            string pathAndQuery = request.Url.PathAndQuery ?? "";
            int q = pathAndQuery.IndexOf('?');
            if (q < 0) return null;
            foreach (var pair in pathAndQuery.Substring(q + 1).Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string rawKey = eq < 0 ? pair : pair.Substring(0, eq);
                string decodedKey;
                try { decodedKey = HttpUtility.UrlDecode(rawKey, Encoding.UTF8); }
                catch { decodedKey = rawKey; }
                if (!string.Equals(decodedKey, key, StringComparison.OrdinalIgnoreCase)) continue;
                string rawValue = eq < 0 ? "" : pair.Substring(eq + 1);
                try
                {
                    return HttpUtility.UrlDecode(rawValue, Encoding.UTF8);
                }
                catch
                {
                    return rawValue;
                }
            }
            return null;
        }

        /// <summary>
        /// Reads the request body as a string. Used by POST endpoints.
        /// </summary>
        private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request)
        {
            if (!request.HasEntityBody) return null;
            // JSON request bodies are UTF-8 (RFC 8259). The Python backend sends
            // Content-Type: application/json without an explicit charset, so
            // HttpListenerRequest.ContentEncoding would fall back to the system
            // default code page (GBK on zh-CN Windows) and mangle non-ASCII block
            // names / code comments. Always decode as UTF-8.
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        // ---------------------------------------------------------------------
        // MCP JSON-RPC 2.0 endpoint — built-in MCP server
        // Supports tools/list and tools/call, bridging the existing REST API to the MCP protocol
        // ---------------------------------------------------------------------

        private static readonly List<object> _mcpToolDefinitions = new List<object>
        {
            new { name = "get_block_content", description = "Read one block/type through the unified contract. format: 'ir' (default; canonical JSON, the LAD/FBD/GRAPH write shape; pages with network_start/network_count), 'source' (reconstructed SCL/STL network body text, pages with offset/length), 'xml' (raw Openness export XML, pages with offset/length), 'interface' (canonical interface tree for DB/UDT/FB/FC). Returns {format, block_name, block_type, xml_block_kind, is_instance_db, source_profile, revision, summary, content}; pass revision as base_revision to the patch tools.", inputSchema = new { type = "object", properties = new { block_name = new { type = "string", description = "Block or PLC data type name" }, format = new { type = "string", description = "'ir' (default) | 'source' | 'xml' | 'interface'" }, network_start = new { type = "integer", description = "1-based first network for format=ir (optional)" }, network_count = new { type = "integer", description = "Max networks for format=ir (optional)" }, offset = new { type = "integer", description = "Character offset for format=source|xml (optional)" }, length = new { type = "integer", description = "Max characters for format=source|xml (optional)" } }, required = new[] { "block_name" } } },
            new { name = "patch_block_network", description = "Patch ONE network body of an existing block without rebuilding it. Read the block first with get_block_content (format='ir' or 'source'), then pass its revision as base_revision. 'body' is the canonical node tree (networks[].body) or a full network entry {language, body}. Only the target CompileUnit NetworkSource changes: block attributes, other networks, titles/comments and ObjectLists are preserved. GRAPH blocks are rejected (use build_graph_fb). A stale revision or a cross-version export is refused. Requires approval.", inputSchema = new { type = "object", properties = new { block_name = new { type = "string", description = "Existing block name" }, base_revision = new { type = "string", description = "revision returned by get_block_content for this block" }, compile_unit_id = new { type = "string", description = "CompileUnit ID from networks[].compileUnitId (preferred)" }, network_order = new { type = "integer", description = "1-based CompileUnit order when compile_unit_id is omitted" }, body = new { type = "object", description = "Canonical network body node tree (networks[].body)" } }, required = new[] { "block_name", "base_revision", "body" } } },
            new { name = "patch_block_interface", description = "Patch ONLY the interface of an existing block/type (DB/UDT/FB/FC) without rebuilding it. Read the block first with get_block_content (format='interface' or 'ir'), then pass its revision as base_revision. 'interface' is the canonical Interface node tree. Block attributes (MemoryLayout, InstanceOfName, SetENOAutomatically...), networks and ObjectLists are preserved. A stale revision or a cross-version export is refused. Requires approval.", inputSchema = new { type = "object", properties = new { block_name = new { type = "string", description = "Existing block or UDT name" }, base_revision = new { type = "string", description = "revision returned by get_block_content for this block" }, @interface = new { type = "object", description = "Canonical Interface node tree" } }, required = new[] { "block_name", "base_revision", "interface" } } },
            new { name = "import_block", description = "Import SCL/XML code into TIA Portal as a PLC block", inputSchema = new { type = "object", properties = new { block_name = new { type = "string", description = "Block name" }, block_type = new { type = "string", description = "Block type (FB/FC/DB/OB/UDT)" }, code = new { type = "string", description = "SCL or XML code content" }, group_path = new { type = "string", description = "Target group path (optional)" } }, required = new[] { "block_name", "block_type", "code" } } },
            new { name = "compile", description = "Compile the whole PLC software and return a structured verdict: {success, error_count, warning_count, errors[]{block,message,line}, warnings[], unknown_block_suspects[]}. Errors include TIA's nested messages with the best-recovered block name (Unknown when TIA does not name one). Judge pass/fail by field, not by parsing text. Read-only (the project is not changed).", inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() } },
            new { name = "build_lad_fc", description = "Build a native LAD/FBD FC block from a JSON spec and import it. Use this (NOT SCL) when the user asks for ladder/LAD or FBD logic. A network accepts either networks[].body (canonical node tree from get_block_content(format='ir'); free contacts/coils/compare/timers supported) or networks[].callJson (one FC/FB call). 'json': { blockName, blockNumber, language?(LAD|FBD), inputs[]{name,datatype,comment?}, outputs[], networks[]{ title?, comment?, language?, body? | callJson? } }. The generated XML is parsed back and compared before import; a mismatch is returned as an error.", inputSchema = new { type = "object", properties = new { json = new { type = "object", description = "LAD/FBD FC block specification (blockName, blockNumber, language, inputs, outputs, networks[])" }, group_path = new { type = "string", description = "Target group path (optional)" } }, required = new[] { "json" } } },
            new { name = "build_lad_fb", description = "Build a native LAD/FBD FB block from a JSON spec and import it. Same network contract as build_lad_fc (networks[].body or callJson), plus FB interface sections: 'json' adds language?(LAD|FBD), inouts[], statics[], temps[] (each {name,datatype,comment?}). Use this (NOT SCL) when the user asks for a ladder/FBD FB with internal state. The generated XML is parsed back and compared before import.", inputSchema = new { type = "object", properties = new { json = new { type = "object", description = "LAD/FBD FB block specification (blockName, blockNumber, language, inputs, outputs, inouts, statics, temps, networks[])" }, group_path = new { type = "string", description = "Target group path (optional)" } }, required = new[] { "json" } } },
            new { name = "build_graph_fb", description = "Build a native S7-GRAPH FB (step sequence) from a JSON spec and import it into TIA Portal. Use this when the user asks for GRAPH / sequential function chart logic. 'json': { blockName, blockNumber, inputs[]{name,datatype}, outputs[]{name,datatype}, sequence:{ titleZhCn?, commentZhCn?, steps[]{ number, name, init (exactly one true), maxStepTime?, warningTime?, actions[]{qualifier,event?,operand,parameter?} }, transitions[]{ number, name?, language(LAD|FBD)?, condition{ parts[]{name,uid?}, accesses[]{scope,symbolPath[]|constantValue,uid?}, wires[]{from{kind(Powerrail|NameCon|IdentCon|OpenCon),uid?,pin?},to{kind,uid?,pin?}} } }, branches[]{ number, type(AltBegin|AltEnd|SimBegin|SimEnd), cardinality }, connections[]{ from{kind(Step|Transition|Branch),number,in?|out?}, to{kind(Step|Transition|Branch|End),number,in?|out?}, linkType(Direct|Jump)? } } }. Every transition must declare a condition; a missing condition, or a sequence with no actions, is rejected at compose time. Each step action operand is the TIA text form INCLUDING quotes (e.g. \"Q0.0\" or \"TagName\"); a delay/timer value goes in 'parameter'. The block is compiled after import; a compile failure is reported as an error. get_block_content(format='ir') returns exactly this JSON shape for a GRAPH block (system interface members already removed), so read -> edit -> build_graph_fb round-trips.", inputSchema = new { type = "object", properties = new { json = new { type = "object", description = "GRAPH FB specification (blockName, blockNumber, interface members, sequence)" }, group_path = new { type = "string", description = "Target group path (optional)" } }, required = new[] { "json" } } },
            new { name = "build_udt", description = "Build a PLC UDT (user-defined type / PlcStruct) from a JSON spec and import it into TIA Portal. Use this (NOT import_block with SCL) when creating a PLC data type. The 'json' object: { name, members[]{ name, datatype, externalWritable?, commentZhCn? } } or, instead of members[], { name, interface } where interface is the canonical Interface node tree returned by get_block_content(format='interface') (the whole tree, including nested structs and comments, is written back). The XML Engineering header uses the detected TIA version automatically.", inputSchema = new { type = "object", properties = new { json = new { type = "object", description = "UDT spec (name, members[] or interface)" }, group_path = new { type = "string", description = "Target type-group path (optional; use list_type_groups to discover)" } }, required = new[] { "json" } } },
            new { name = "build_global_db", description = "Build a global data block (DB) from a JSON spec and import it into TIA Portal. Use this (NOT import_block with SCL) when creating a global DB. Instance DBs are rejected (use patch_block_interface for an instance DB). The 'json' object: { dbName, dbNumber, staticMembers[]{ name, datatype, externalWritable?, commentZhCn?, startValue? } } or, instead of staticMembers[], { dbName, dbNumber, interface } where interface is the canonical Interface node tree returned by get_block_content. memory_layout defaults to the IR's MemoryLayout, otherwise Standard; an explicit override is rejected on TIA versions whose XML import rejects the block-level MemoryLayout element (V15.1 does), so omit it there and let TIA keep the project default. The XML Engineering header uses the detected TIA version automatically.", inputSchema = new { type = "object", properties = new { json = new { type = "object", description = "Global DB spec (dbName, dbNumber, staticMembers[] or interface)" }, memory_layout = new { type = "string", description = "'Standard' or 'Optimized' (optional; defaults to the IR's layout, else Standard)" }, group_path = new { type = "string", description = "Target block-group path (optional; use list_block_groups to discover)" } }, required = new[] { "json" } } },
            new { name = "build_ob", description = "Build a valid organization block (OB) and import it. An OB cannot be created from an SCL external source (TIA rejects the number and the 20-byte start-information interface), so this tool composes the legal OB envelope and tokenizes the body itself. 'json': { blockName, obNumber (1 = program cycle, 30-38 = cyclic interrupt), body: SCL statements WITHOUT the ORGANIZATION_BLOCK/VAR_TEMP wrapper, temps[]: {name,datatype} declared temp variables referenced as #name, titleZhCn?, commentZhCn?, allow_overwrite? }. The body runs in the OB context. Number is validated against existing blocks; a same-name block requires allow_overwrite=true.", inputSchema = new { type = "object", properties = new { json = new { type = "object", description = "OB spec (blockName, obNumber, body, temps[])" }, group_path = new { type = "string", description = "Target group path (optional)" } }, required = new[] { "json" } } },
            new { name = "describe_knowledge_schema", description = "Describe the structure of the RAG knowledge index (the local SQLite tables that back search_project / query_variable_references / query_call_chain / query_variable_impact / build_context_pack). Returns a data dictionary: each table's purpose, every column's meaning and allowed values, and the AST/syntax/call-graph relations between tables. Call this FIRST when you are unsure what the RAG tools can retrieve, what a field means, or which value to pass (e.g. direction, block_type, section). No live TIA access; pure static schema.", inputSchema = new { type = "object", properties = new { table = new { type = "string", description = "Optional table name to describe just one table (e.g. 'plc_block', 'plc_call_edge'). Empty = full data dictionary." } }, required = Array.Empty<string>() } },
            new { name = "search_project", description = "RAG search over the TIA project knowledge index (blocks, networks, symbols/variables, calls, cross-references). Keyword + structured retrieval backed by the local SQLite index in the knowledge worker.", inputSchema = new { type = "object", properties = new { query = new { type = "string", description = "Search query" }, limit = new { type = "integer", description = "Max results (default 10)" } }, required = new[] { "query" } } },
            new { name = "list_standard_topics", description = "List the currently available coding-standard libraries (official + mounted) as a bookmark outline: heading level, title, rule_id, page, absolute doc_offset, content_length, and next_doc_offset. doc_offset is the absolute position of that heading in the library's virtual document. Pick a bookmark by title/doc_offset, then call read_standard_section (doc_offset=... or rule_id+offset).", inputSchema = new { type = "object", properties = new { library_id = new { type = "string", description = "Optional library id to list only that library; empty lists all libraries" } }, required = Array.Empty<string>() } },
            new { name = "read_standard_section", description = "Read a standard section from a bookmark. Start at an absolute doc_offset (from list_standard_topics) or at rule_id+offset. By default the read stops at the next heading (capped at 20000 chars) and returns next_heading_offset / next_heading_doc_offset / next_rule_id, so you can jump to the next bookmark instead of guessing. Pass length explicitly to override the 20000-char cap.", inputSchema = new { type = "object", properties = new { library_id = new { type = "string", description = "Library id (from list_standard_topics)" }, rule_id = new { type = "string", description = "Topic rule_id (from list_standard_topics); omit when using doc_offset" }, doc_offset = new { type = "integer", description = "Absolute bookmark position from list_standard_topics (preferred over rule_id+offset)" }, offset = new { type = "integer", description = "Offset relative to the rule (default 0); ignored when doc_offset is given" }, length = new { type = "integer", description = "Max characters to read (default: to the next heading, at most 20000)" } }, required = new[] { "library_id" } } },
            new { name = "build_context_pack", description = "Build a RAG context pack for a query: top-K retrieved code/network evidence plus dependency context, ready to inject into an LLM prompt.", inputSchema = new { type = "object", properties = new { query = new { type = "string", description = "Search query / task description" }, top_k = new { type = "integer", description = "Max evidence fragments (default 5)" } }, required = new[] { "query" } } },
            new { name = "query_variable_references", description = "Query all read/write references of a variable across the project from the structured knowledge index.", inputSchema = new { type = "object", properties = new { variable_path = new { type = "string", description = "Variable canonical path or name" }, direction = new { type = "string", description = "'read' | 'write' | 'both' (default both)" }, top_k = new { type = "integer", description = "Max references (default 20)" } }, required = new[] { "variable_path" } } },
            new { name = "query_call_chain", description = "Traverse the block call graph from a block: upstream callers and/or downstream callees, multi-hop, with depth/node limits and cycle detection. Backed by the structured call-relation index (no live TIA access).", inputSchema = new { type = "object", properties = new { block_name = new { type = "string", description = "Root block name" }, direction = new { type = "string", description = "'caller' (upstream) | 'callee' (downstream) | 'both' (default)" }, max_depth = new { type = "integer", description = "Max traversal depth (default 5, max 20)" }, max_node_count = new { type = "integer", description = "Max nodes before truncation (default 200)" } }, required = new[] { "block_name" } } },
            new { name = "query_variable_impact", description = "Trace the impact of a variable: locate blocks that directly read/write it, then follow the call graph upstream/downstream (multi-hop) to reveal affected blocks. Backed by the structured index (no live TIA access).", inputSchema = new { type = "object", properties = new { canonical_path = new { type = "string", description = "Variable canonical path or name" }, direction = new { type = "string", description = "'caller' | 'callee' | 'both' (default)" }, max_depth = new { type = "integer", description = "Max traversal depth (default 5, max 20)" }, max_node_count = new { type = "integer", description = "Max nodes before truncation (default 200)" } }, required = new[] { "canonical_path" } } },
            new { name = "get_current_project", description = "Get the TIA project the knowledge worker is currently attached to (project key, name, index state). Use this to verify which project is being searched before calling search_project or other RAG tools.", inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() } },
            new { name = "get_environment_profile", description = "Live environment precheck (never cached): TIA version, project/PLC names, device/CPU order number, inferred CPU family (S7-300/400 | S7-1200 | S7-1500 | Unknown), the full device list, and an environment fingerprint. Call it BEFORE planning or writing, and again when resuming a plan: the user may switch the CPU or TIA version mid-session, which invalidates work planned for the old environment. Facts only — feature support is derived from plc_family by the caller. Read-only, no approval needed.", inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() } },
            new { name = "get_program_tree", description = "Lightweight PLC program directory (structure only, no source code, no knowledge index, no write): program block groups plus every block with name/type/number/language/group_path, and optionally PLC data types and tag tables. Call this BEFORE creating or modifying a block to decide create-vs-modify, to pick a free block number (e.g. a legal OB number), or to find the target group path. One live TIA pass; no XML export.", inputSchema = new { type = "object", properties = new { include_types = new { type = "boolean", description = "Include PLC data types (UDTs). Default true." }, include_tag_tables = new { type = "boolean", description = "Include PLC tag tables. Default false." }, max_blocks = new { type = "integer", description = "Max block entries to return (default 2000)." } }, required = Array.Empty<string>() } },
            new { name = "list_block_groups", description = "List the PLC program block group paths (directories under BlockGroup) so a write can target one via group_path. Empty result = only the root group exists.", inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() } },
            new { name = "list_type_groups", description = "List the PLC data-type (UDT) group paths (directories under TypeGroup) so a UDT write can target one via group_path. Empty result = only the root group exists.", inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() } },
            new { name = "list_tags", description = "List PLC tag tables and tags", inputSchema = new { type = "object", properties = new { limit = new { type = "integer", description = "Max entries (default 500)" } }, required = Array.Empty<string>() } },
            new { name = "get_db_structure", description = "Get the member structure of a Data Block", inputSchema = new { type = "object", properties = new { name = new { type = "string", description = "DB name" } }, required = new[] { "name" } } },
            new { name = "list_udt_types", description = "List all User-Defined Types in the project", inputSchema = new { type = "object", properties = new { limit = new { type = "integer", description = "Max entries (default 200)" } }, required = Array.Empty<string>() } },
            new { name = "delete_block", description = "Delete a PLC block from the project", inputSchema = new { type = "object", properties = new { block_name = new { type = "string", description = "Block name to delete" } }, required = new[] { "block_name" } } },
            new { name = "query_variable_type", description = "Query the data type of a variable / UDT field in the TIA project.", inputSchema = new { type = "object", properties = new { variable = new { type = "string", description = "Variable name" }, block = new { type = "string", description = "Optional block name" } }, required = new[] { "variable" } } },
            new { name = "repair_inconsistent_types", description = "Repair blocks/PLC types that TIA marks 'inconsistent (needs compile)' and cannot be exported: candidates come from the knowledge index snapshots, the last indexed XML is patched losslessly (interface-only datatype replacement), imported, then the project is compiled. Refuses when there is no snapshot, the member is a nested struct, or it carries a start value. Write-gated (approval per type); pass dry_run=true to only report what would change.", inputSchema = new { type = "object", properties = new { replacements = new { type = "object", description = "Map of currently unsupported datatype -> replacement, taken from the platform matrix (e.g. {\"DTL\":\"DATE_AND_TIME\",\"LDT\":\"DATE_AND_TIME\",\"WSTRING\":\"STRING\"})." }, dry_run = new { type = "boolean", description = "true = report candidates/changes without writing." } }, required = new[] { "replacements" } } },
            new { name = "compile_snapshot", description = "Compile the PLC software and store the resulting error set as the baseline for compile_diff. Call this BEFORE writing, so post-write errors can be split into pre-existing vs newly introduced. No approval needed (compile does not change project source).", inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() } },
            new { name = "compile_diff", description = "Compile again and compare with the last compile_snapshot baseline. Returns new_errors (introduced since the snapshot), pre_existing_errors (already in the project before), resolved_errors, and counts. Fix only new_errors; do NOT modify blocks for pre_existing errors. If baseline_missing=true, call compile_snapshot and retry. No approval needed.", inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() } },
            new { name = "compile_block", description = "Compile ONE PLC block (not the whole PLC software) and return that block's structured messages. Use it after writing a block as a gentle check: it is local and cheap, does not touch other blocks the user may be editing, and cannot trip over Safety programs the way a whole-PLC compile can. Read-only (the project is not changed), no approval needed.", inputSchema = new { type = "object", properties = new { block_name = new { type = "string", description = "Name of the block to compile" } }, required = new[] { "block_name" } } },
            new { name = "query_repair_experience", description = "Query the accumulated repair experience (compile error -> root cause -> fix) for the current project.", inputSchema = new { type = "object", properties = new { query = new { type = "string", description = "Error signature / keywords" }, limit = new { type = "integer", description = "Max entries (default 3)" } }, required = new[] { "query" } } },
            new { name = "probe_openness_capabilities", description = "Probe the actually available Openness API surface of the currently loaded TIA Portal version (namespaces, types, methods). Use this to decide whether an operation (e.g. HMI Unified, hardware config, OPC UA) is supported before attempting it. Reflection over assembly metadata only; does not touch the live project.", inputSchema = new { type = "object", properties = new { namespace_filter = new { type = "string", description = "Optional substring to filter namespaces/types (e.g. 'HmiUnified', 'HW', 'OpcUa'). Empty = summary of all namespaces." }, detail_level = new { type = "string", description = "'summary' (default: namespaces + type names) or 'full' (include method/property/enum members; use with namespace_filter to limit size)." } }, required = Array.Empty<string>() } },
            new { name = "build_hmi_screen", description = "Offline-compose an HMI screen from a design JSON spec. 'classic' returns XML; 'unified' returns an apply-design JSON. Does NOT write to TIA.", inputSchema = new { type = "object", properties = new { json = new { type = "string", description = "HMI screen design JSON (Screen: Name/Width/Height + Items[])." }, target = new { type = "string", description = "HMI type: 'classic' (WinCC Basic/Comfort, default) or 'unified' (WinCC Unified)." }, tia_version = new { type = "string", description = "Target TIA version for the XML Engineering header (classic only), e.g. '16' (maps to V16). Defaults to V18." } }, required = new[] { "json" } } },
            new { name = "build_hmi_tag_table", description = "Offline-compose an HMI tag table from a JSON spec. 'classic' returns XML; 'unified' returns a tag-plan JSON. Does NOT write to TIA.", inputSchema = new { type = "object", properties = new { json = new { type = "string", description = "HMI tag table JSON (Name + Tags[])." }, target = new { type = "string", description = "HMI type: 'classic' (default) or 'unified' (WinCC Unified)." }, tia_version = new { type = "string", description = "Target TIA version for the XML Engineering header (classic only), e.g. '16' (maps to V16). Defaults to V18." } }, required = new[] { "json" } } },
            new { name = "reflect_generation", description = "Reference reflection for ANY generated TIA artifact. Exports the reference object(s) from the live project, obtains the candidate (from the project, an XML argument/path, or an HMI design JSON), and compares their SIMATIC ML shape (root object class, CompositionName markers, AttributeList cardinality, attribute/link vocabulary). Kinds: plc_block, plc_type (FC/FB/DB/OB/UDT reference block), plc_tag_table, hmi_screen, hmi_tag_table. Read-only on the project; only XML files are written. Typical use: pick an existing hand-made object as reference, generate/import the new object, then compare candidate_name against reference_name before trusting the generator.", inputSchema = new { type = "object", properties = new { kind = new { type = "string", description = "plc_block | plc_type | plc_tag_table | hmi_screen | hmi_tag_table." }, reference_name = new { type = "string", description = "Existing object used as reference. Required for plc_block/plc_type. HMI/tag tables auto-pick up to three when omitted." }, reference_names = new { type = "array", description = "Multiple reference object names (preferred for HMI when the type vocabulary spans several screens)." }, candidate_name = new { type = "string", description = "Existing object to export as the candidate (e.g. the block/screen just generated)." }, xml = new { type = "string", description = "Candidate XML supplied directly." }, xml_path = new { type = "string", description = "Path to a candidate XML file." }, json = new { type = "string", description = "(HMI only) design JSON; the tool composes the candidate XML with the matching builder." }, software_path = new { type = "string", description = "HMI device/software name, e.g. 'IA1' (HMI kinds). PLC tag tables use the connected PLC software." }, tia_version = new { type = "string", description = "(HMI json compose) target TIA version, e.g. '15.1'." }, target = new { type = "string", description = "'classic' (default)." }, output_dir = new { type = "string", description = "Optional directory for exported reference/candidate XML." } }, required = new[] { "kind" } } },
            new { name = "configure_device", description = "Create a new device/station in the open TIA project from a hardware-catalog TypeIdentifier (requires user approval).", inputSchema = new { type = "object", properties = new { type_identifier = new { type = "string", description = "Hardware catalog TypeIdentifier, e.g. 'OrderNumber:6ES7 518-4AP00-0AB0/V2.1'." }, name = new { type = "string", description = "Device name (optional; derived from type_identifier if omitted)." } }, required = new[] { "type_identifier" } } },
            new { name = "set_device_attribute", description = "Set an attribute on a device item by name (requires user approval).", inputSchema = new { type = "object", properties = new { device_item_name = new { type = "string", description = "Device item name." }, attribute_name = new { type = "string", description = "Attribute name." }, value = new { type = "string", description = "New value." } }, required = new[] { "device_item_name", "attribute_name", "value" } } },
            new { name = "add_module", description = "Insert a module under a parent device item (requires user approval).", inputSchema = new { type = "object", properties = new { parent_device_item_name = new { type = "string", description = "Parent device item name (rack/station)." }, module_name = new { type = "string", description = "Module name." }, order_number = new { type = "string", description = "Module order number (MLFB)." } }, required = new[] { "parent_device_item_name", "module_name", "order_number" } } },
            new { name = "configure_subnet", description = "Create a subnet and connect a device item to it (requires user approval).", inputSchema = new { type = "object", properties = new { device_item_name = new { type = "string", description = "Device item name." }, subnet_name = new { type = "string", description = "Subnet name." }, subnet_type = new { type = "string", description = "Subnet type, e.g. 'Profinet' or 'Ethernet' (default Profinet)." } }, required = new[] { "device_item_name", "subnet_name" } } }
        };

        /// <summary>Flatten a target-group DTO tree into a depth-first list of group paths (root omitted; empty list = only the root group).</summary>
        private static List<string> FlattenGroupPaths(IEnumerable<TargetGroupNodeDto> groups)
        {
            var paths = new List<string>();
            void Walk(IEnumerable<TargetGroupNodeDto> nodes)
            {
                foreach (var n in nodes)
                {
                    if (n != null && !string.IsNullOrEmpty(n.Path))
                        paths.Add(n.Path);
                    if (n != null && n.Children != null && n.Children.Count > 0)
                        Walk(n.Children);
                }
            }
            Walk(groups ?? new List<TargetGroupNodeDto>());
            return paths;
        }

        // ---------------------------------------------------------------------
        // compile_diff support
        // ---------------------------------------------------------------------

        private static string CompileErrorKey(CompilationError e)
            => ((e?.BlockName ?? "Unknown").Trim() + "|" + (e?.Message ?? "").Trim()).ToLowerInvariant();

        private static object CompileErrorDto(CompilationError e)
            => new
            {
                block = string.IsNullOrWhiteSpace(e?.BlockName) ? "Unknown" : e.BlockName,
                message = e?.Message ?? "",
            };

        /// <summary>
        /// Compare the current compile errors with the snapshot taken before the write. Matching is a
        /// multiset over (block, message): duplicated errors (e.g. the same DTL error twice) are handled,
        /// and errors that already existed in the project are never attributed to the latest change.
        /// </summary>
        private static object BuildCompileDiff(CompilationResult baseline, string baselineId, CompilationResult current)
        {
            var now = current?.Errors ?? new List<CompilationError>();
            var remaining = new List<CompilationError>(baseline?.Errors ?? new List<CompilationError>());
            var preExisting = new List<CompilationError>();
            var added = new List<CompilationError>();

            foreach (var e in now)
            {
                string key = CompileErrorKey(e);
                int match = remaining.FindIndex(x => CompileErrorKey(x) == key);
                if (match >= 0)
                {
                    preExisting.Add(e);
                    remaining.RemoveAt(match);
                }
                else
                {
                    added.Add(e);
                }
            }

            return new
            {
                baseline_id = baselineId,
                baseline_missing = baseline == null,
                success = current?.Success ?? false,
                total_errors = now.Count,
                new_count = added.Count,
                pre_existing_count = preExisting.Count,
                resolved_count = remaining.Count,
                new_errors = added.Select(CompileErrorDto).ToList(),
                pre_existing_errors = preExisting.Select(CompileErrorDto).ToList(),
                resolved_errors = remaining.Select(CompileErrorDto).ToList(),
            };
        }

        private async Task HandleMcpJsonRpcAsync(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, new { error = "Method not allowed. Use POST." });
                return;
            }

            string body = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                WriteJson(ctx.Response, 400, new { jsonrpc = "2.0", id = (string)null, error = new { code = -32600, message = "Empty request body" } });
                return;
            }

            JsonElement root;
            try
            {
                root = JsonDocument.Parse(body).RootElement;
            }
            catch
            {
                WriteJson(ctx.Response, 400, new { jsonrpc = "2.0", id = (string)null, error = new { code = -32700, message = "Parse error" } });
                return;
            }

            string id = root.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
            string method = root.TryGetProperty("method", out var methodEl) ? methodEl.GetString() : null;

            if (string.IsNullOrEmpty(method))
            {
                WriteJson(ctx.Response, 200, new { jsonrpc = "2.0", id, error = new { code = -32600, message = "Missing 'method' field" } });
                return;
            }

            switch (method)
            {
                case "initialize":
                    WriteJson(ctx.Response, 200, new { jsonrpc = "2.0", id, result = new { protocolVersion = "2024-11-05", serverInfo = new { name = "TIA Portal Openness MCP", version = "1.0.0" }, capabilities = new { tools = new { } } } });
                    return;

                case "tools/list":
                    WriteJson(ctx.Response, 200, new { jsonrpc = "2.0", id, result = new { tools = _mcpToolDefinitions } });
                    return;

                case "tools/call":
                    await HandleMcpToolCallAsync(ctx, root, id).ConfigureAwait(false);
                    return;

                default:
                    WriteJson(ctx.Response, 200, new { jsonrpc = "2.0", id, error = new { code = -32601, message = $"Method '{method}' not found" } });
                    return;
            }
        }

        private async Task HandleMcpToolCallAsync(HttpListenerContext ctx, JsonElement root, string id)
        {
            string toolName = null;
            JsonElement arguments = default;

            if (root.TryGetProperty("params", out var paramsEl))
            {
                if (paramsEl.TryGetProperty("name", out var nameEl))
                    toolName = nameEl.GetString();
                if (paramsEl.TryGetProperty("arguments", out var argsEl))
                    arguments = argsEl;
            }

            if (string.IsNullOrEmpty(toolName))
            {
                WriteJson(ctx.Response, 200, new { jsonrpc = "2.0", id, error = new { code = -32602, message = "Missing params.name (tool name)" } });
                return;
            }

            string resultText = null;
            string errorText = null;

            try
            {
                switch (toolName)
                {
                    case "probe_openness_capabilities":
                        // Pure reflection capability probe: no live project access, no STA, never blocks the UI.
                        try
                        {
                            var nsFilter = arguments.TryGetProperty("namespace_filter", out var nfEl) ? nfEl.GetString() : null;
                            var detail = arguments.TryGetProperty("detail_level", out var dlEl) ? dlEl.GetString() : "summary";
                            bool full = string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase);

                            var manifest = await OpennessCapabilityProbe.GetCapabilitiesAsync().ConfigureAwait(false);
                            resultText = BuildCapabilityReport(manifest, nsFilter, full);
                        }
                        catch (Exception ex) { errorText = ex.Message; }
                        break;

                    case "describe_knowledge_schema":
                        // Purely static data dictionary: describes tables/fields/relations of the RAG index. No live project, no STA.
                        try
                        {
                            var table = arguments.TryGetProperty("table", out var tblEl) ? tblEl.GetString() : null;
                            resultText = KnowledgeSchemaDoc.Describe(table);
                        }
                        catch (Exception ex) { errorText = ex.Message; }
                        break;

                    case "get_block_content":
                        (resultText, errorText) = await HandleGetBlockContentAsync(arguments, null, true).ConfigureAwait(false);
                        break;

                    case "patch_block_network":
                        (resultText, errorText) = await HandlePatchBlockNetworkAsync(arguments, RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "patch_block_interface":
                        (resultText, errorText) = await HandlePatchBlockInterfaceAsync(arguments, RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "repair_inconsistent_types":
                        (resultText, errorText) = await HandleRepairInconsistentTypesAsync(arguments, RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "export_block":
                        (resultText, errorText) = await HandleGetBlockContentAsync(arguments, "xml", false).ConfigureAwait(false);
                        break;

                    case "import_block":
                        var ibName = arguments.TryGetProperty("block_name", out var ibn) ? ibn.GetString() : null;
                        var ibType = arguments.TryGetProperty("block_type", out var ibt) ? ibt.GetString() : "";
                        var ibCode = arguments.TryGetProperty("code", out var ibc) ? ibc.GetString() : null;
                        var ibGroup = arguments.TryGetProperty("group_path", out var ibg) ? ibg.GetString() : "";
                        if (string.IsNullOrWhiteSpace(ibName) || string.IsNullOrWhiteSpace(ibCode)) { errorText = "block_name and code are required"; break; }
                        var ibRefusal = await AuthorizeWriteAsync(NewWrite(ctx, "import_block", ibName, ibType, ibCode, ibGroup)).ConfigureAwait(false);
                        if (ibRefusal != null) { errorText = ibRefusal.Message; break; }
                        await _runOnTiaSta(async () =>
                        {
                            try { resultText = await _tia.ImportBlockCodeAsync(ibName, ibType, ibCode, ibGroup).ConfigureAwait(false); }
                            catch (Exception ex) { errorText = ex.Message; }
                        }).ConfigureAwait(false);
                        break;

                    case "compile":
                    {
                        // Single whole-project compile tool: structured verdict (success/errors/warnings),
                        // so callers judge by field instead of parsing text. Replaces the former
                        // compile_project_structured duplicate.
                        CompilationResult compileResult = null;
                        await _runOnTiaSta(async () =>
                        {
                            try { compileResult = await _tia.GetCompilationResultAsync().ConfigureAwait(false); }
                            catch (Exception ex) { errorText = ex.Message; }
                        }).ConfigureAwait(false);
                        if (compileResult == null)
                        {
                            errorText ??= "Compile failed.";
                            break;
                        }

                        var errors = compileResult.Errors ?? new List<CompilationError>();
                        var warnings = compileResult.Warnings ?? new List<CompilationWarning>();

                        // TIA does not name the owner of a type-level error ("Data type DTL is
                        // unknown."). Probe the snapshot candidates so the agent gets real object
                        // names instead of guessing a block.
                        var suspects = new List<string>();
                        if (errors.Any(e => string.IsNullOrWhiteSpace(e.BlockName)
                                            || string.Equals(e.BlockName, "Unknown", StringComparison.OrdinalIgnoreCase)))
                        {
                            var projectKey = await GetProjectKeyAsync().ConfigureAwait(false);
                            suspects = (await ProbeInconsistentTargetsAsync(projectKey).ConfigureAwait(false))
                                .Select(t => t.BlockId)
                                .ToList();
                        }

                        var structured = new
                        {
                            success = compileResult.Success,
                            error_count = errors.Count,
                            warning_count = warnings.Count,
                            errors = errors.Select(e => new { block = e.BlockName ?? "Unknown", message = e.Message ?? "", line = e.LineNumber }).ToList(),
                            warnings = warnings.Select(w => new { block = w.BlockName ?? "Unknown", message = w.Message ?? "" }).ToList(),
                            unknown_block_suspects = suspects,
                        };
                        resultText = JsonSerializer.Serialize(structured, _jsonOptions);
                        break;
                    }

                    case "build_lad_fc":
                        (resultText, errorText) = await HandleBuildLadAsync(arguments, "ladfc", RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "build_lad_fb":
                        (resultText, errorText) = await HandleBuildLadAsync(arguments, "ladfb", RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "build_graph_fb":
                        (resultText, errorText) = await HandleBuildGraphAsync(arguments, RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "build_udt":
                        (resultText, errorText) = await HandleBuildUdtAsync(arguments, RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "build_global_db":
                        (resultText, errorText) = await HandleBuildGlobalDbAsync(arguments, RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "build_ob":
                        (resultText, errorText) = await HandleBuildObAsync(arguments, RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "build_hmi_screen":
                        (resultText, errorText) = HandleBuildHmiAsync(arguments, "screen");
                        break;

                    case "build_hmi_tag_table":
                        (resultText, errorText) = HandleBuildHmiAsync(arguments, "tag_table");
                        break;

                    case "reflect_generation":
                        (resultText, errorText) = await HandleReflectGenerationAsync(arguments).ConfigureAwait(false);
                        break;

                    case "configure_device":
                        (resultText, errorText) = await HandleConfigureDeviceAsync(arguments, RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "set_device_attribute":
                        (resultText, errorText) = await HandleHardwareWriteAsync(arguments, "set_device_attribute", RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "add_module":
                        (resultText, errorText) = await HandleHardwareWriteAsync(arguments, "add_module", RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "configure_subnet":
                        (resultText, errorText) = await HandleHardwareWriteAsync(arguments, "configure_subnet", RidOf(ctx), IsRemoteCaller(ctx)).ConfigureAwait(false);
                        break;

                    case "search_project":
                    {
                        // RAG search: goes through the unified InvokeKnowledgeToolAsync (same entry as REST /api/tool/search_project).
                        var inv = await InvokeKnowledgeToolAsync("search_project", arguments).ConfigureAwait(false);
                        if (inv.IsError) errorText = inv.Error; else resultText = inv.Result;
                        break;
                    }

                    case "list_standard_topics":
                    {
                        // Bookmark outline: no rule body, just heading/bookmark positions for the
                        // agent to choose from. Purely local encrypted KB, no STA.
                        if (_standards == null) { resultText = "No coding-standard library mounted."; break; }
                        var listLibraryId = arguments.TryGetProperty("library_id", out var llib) ? llib.GetString() : null;
                        try { resultText = _standards.ListLibrariesAndTopics(listLibraryId); }
                        catch (Exception ex) { errorText = ex.Message; }
                        break;
                    }

                    case "read_standard_section":
                    {
                        // Heading-bounded read: start at an absolute doc_offset (preferred) or at
                        // rule_id+offset; the read stops at the next heading by default.
                        if (_standards == null) { resultText = "No coding-standard library mounted."; break; }
                        var libraryId = arguments.TryGetProperty("library_id", out var libEl) ? libEl.GetString() : null;
                        var ruleId = arguments.TryGetProperty("rule_id", out var ridEl) ? ridEl.GetString() : null;
                        var offset = arguments.TryGetProperty("offset", out var offEl) && offEl.TryGetInt32(out var offV) ? offV : 0;
                        var length = arguments.TryGetProperty("length", out var lenEl) && lenEl.TryGetInt32(out var lenV) ? lenV : 0;
                        var docOffset = arguments.TryGetProperty("doc_offset", out var doe) && doe.TryGetInt32(out var doev) ? doev : -1;
                        if (string.IsNullOrWhiteSpace(libraryId) ||
                            (string.IsNullOrWhiteSpace(ruleId) && docOffset < 0))
                        {
                            errorText = "library_id and either rule_id or doc_offset are required";
                            break;
                        }
                        try { resultText = _standards.ReadSectionChunk(libraryId, ruleId, offset, length, docOffset); }
                        catch (Exception ex) { errorText = ex.Message; }
                        break;
                    }

                    case "build_context_pack":
                    {
                        // RAG context pack: retrieved hits + dependency context, ready to inject into a prompt.
                        var inv = await InvokeKnowledgeToolAsync("build_context_pack", arguments).ConfigureAwait(false);
                        if (inv.IsError) errorText = inv.Error; else resultText = inv.Result;
                        break;
                    }

                    case "query_variable_references":
                    {
                        // Variable cross-references: every place a variable is read/written in the project (from the structured index).
                        var inv = await InvokeKnowledgeToolAsync("query_variable_references", arguments).ConfigureAwait(false);
                        if (inv.IsError) errorText = inv.Error; else resultText = inv.Result;
                        break;
                    }

                    case "query_call_chain":
                    {
                        // Call-chain traversal: a block's upstream callers / downstream callees (multi-hop, from the structured index).
                        var inv = await InvokeKnowledgeToolAsync("query_call_chain", arguments).ConfigureAwait(false);
                        if (inv.IsError) errorText = inv.Error; else resultText = inv.Result;
                        break;
                    }

                    case "query_variable_impact":
                    {
                        // Variable impact chain: locate blocks that directly read/write the variable, then diffuse upstream/downstream along the call graph (multi-hop).
                        var inv = await InvokeKnowledgeToolAsync("query_variable_impact", arguments).ConfigureAwait(false);
                        if (inv.IsError) errorText = inv.Error; else resultText = inv.Result;
                        break;
                    }

                    case "get_current_project":
                    {
                        // Project-awareness: returns the Worker's attached project + index state.
                        var inv = await InvokeKnowledgeToolAsync("get_current_project", arguments).ConfigureAwait(false);
                        if (inv.IsError) errorText = inv.Error; else resultText = inv.Result;
                        break;
                    }

                    case "get_environment_profile":
                    {
                        // Live environment facts (TIA version + CPU family + fingerprint).
                        // Read-only; the project may change between calls, so nothing is cached.
                        await _runOnTiaSta(() =>
                        {
                            try { resultText = JsonSerializer.Serialize(_tia.GetEnvironmentProfile(), _jsonOptions); }
                            catch (Exception ex) { errorText = ex.Message; }
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);
                        break;
                    }

                    case "get_block_scl_code":
                        (resultText, errorText) = await HandleGetBlockContentAsync(arguments, "source", false).ConfigureAwait(false);
                        break;

                    case "query_variable_type":
                    {
                        var qvtVar = arguments.TryGetProperty("variable", out var qvtV) ? qvtV.GetString() : null;
                        var qvtBlock = arguments.TryGetProperty("block", out var qvtB) ? qvtB.GetString() : "";
                        if (string.IsNullOrWhiteSpace(qvtVar)) { errorText = "variable is required"; break; }
                        await _runOnTiaSta(async () =>
                        {
                            try { resultText = await _tia.FindVariableTypeAsync(qvtVar, qvtBlock).ConfigureAwait(false); }
                            catch (Exception ex) { errorText = ex.Message; }
                        }).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(resultText) && string.IsNullOrWhiteSpace(errorText))
                            errorText = $"Variable '{qvtVar}' not found";
                        break;
                    }

                    case "export_block_ir":
                        (resultText, errorText) = await HandleGetBlockContentAsync(arguments, "ir", false).ConfigureAwait(false);
                        break;

                    case "compile_snapshot":
                    {
                        await _runOnTiaSta(async () =>
                        {
                            try
                            {
                                var snapshot = await _tia.GetCompilationResultAsync().ConfigureAwait(false);
                                _compileBaseline = snapshot;
                                _compileBaselineId = DateTime.Now.ToString("yyyyMMddTHHmmss");
                                var errors = snapshot.Errors ?? new List<CompilationError>();
                                resultText = JsonSerializer.Serialize(new
                                {
                                    baseline_id = _compileBaselineId,
                                    success = snapshot.Success,
                                    error_count = errors.Count,
                                    errors = errors.Select(e => new { block = e.BlockName ?? "Unknown", message = e.Message ?? "" }).ToList(),
                                }, _jsonOptions);
                            }
                            catch (Exception ex) { errorText = ex.Message; }
                        }).ConfigureAwait(false);
                        break;
                    }

                    case "compile_diff":
                    {
                        await _runOnTiaSta(async () =>
                        {
                            try
                            {
                                var current = await _tia.GetCompilationResultAsync().ConfigureAwait(false);
                                resultText = JsonSerializer.Serialize(
                                    BuildCompileDiff(_compileBaseline, _compileBaselineId, current), _jsonOptions);
                            }
                            catch (Exception ex) { errorText = ex.Message; }
                        }).ConfigureAwait(false);
                        break;
                    }

                    case "compile_block":
                    {
                        var cbName = arguments.TryGetProperty("block_name", out var cbEl) ? cbEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(cbName)) { errorText = "block_name is required"; break; }
                        await _runOnTiaSta(async () =>
                        {
                            try
                            {
                                // One block only: never the whole PLC, so a user editing elsewhere is not disturbed.
                                var r = await _tia.CompileBlockAsync(cbName).ConfigureAwait(false);
                                resultText = JsonSerializer.Serialize(BlockCompileDto(r), _jsonOptions);
                            }
                            catch (Exception ex) { errorText = ex.Message; }
                        }).ConfigureAwait(false);
                        break;
                    }

                    case "query_repair_experience":
                    {
                        var inv = await InvokeKnowledgeToolAsync("query_repair_experience", arguments).ConfigureAwait(false);
                        if (inv.IsError) errorText = inv.Error; else resultText = inv.Result;
                        break;
                    }

                    case "get_program_tree":
                    {
                        bool includeTypes = true;
                        bool includeTagTables = false;
                        int maxBlocks = 2000;
                        if (arguments.ValueKind == JsonValueKind.Object)
                        {
                            if (arguments.TryGetProperty("include_types", out var itEl) &&
                                (itEl.ValueKind == JsonValueKind.True || itEl.ValueKind == JsonValueKind.False))
                                includeTypes = itEl.GetBoolean();
                            if (arguments.TryGetProperty("include_tag_tables", out var ttEl) &&
                                (ttEl.ValueKind == JsonValueKind.True || ttEl.ValueKind == JsonValueKind.False))
                                includeTagTables = ttEl.GetBoolean();
                            if (arguments.TryGetProperty("max_blocks", out var mbEl) && mbEl.TryGetInt32(out var mb) && mb > 0)
                                maxBlocks = mb;
                        }
                        await _runOnTiaSta(async () =>
                        {
                            try
                            {
                                var tree = await CollectProgramTreeAsync(includeTypes, includeTagTables, maxBlocks).ConfigureAwait(false);
                                resultText = JsonSerializer.Serialize(tree, _jsonOptions);
                            }
                            catch (Exception ex) { errorText = ex.Message; }
                        }).ConfigureAwait(false);
                        break;
                    }

                    case "list_block_groups":
                    case "list_type_groups":
                    {
                        bool _isTypeGroups = string.Equals(toolName, "list_type_groups", StringComparison.Ordinal);
                        await _runOnTiaSta(async () =>
                        {
                            try
                            {
                                var _groups = _isTypeGroups
                                    ? await _tia.GetTypeGroupTreeAsync().ConfigureAwait(false)
                                    : await _tia.GetBlockGroupTreeAsync().ConfigureAwait(false);
                                resultText = JsonSerializer.Serialize(FlattenGroupPaths(_groups), _jsonOptions);
                            }
                            catch (Exception ex) { errorText = ex.Message; }
                        }).ConfigureAwait(false);
                        break;
                    }

                    case "list_tags":
                        var tagLimit = arguments.TryGetProperty("limit", out var tlEl) && tlEl.TryGetInt32(out var tl) ? tl : 500;
                        await _runOnTiaSta(async () =>
                        {
                            try
                            {
                                var tags = await _tia.ListTagTablesAsync(tagLimit).ConfigureAwait(false);
                                resultText = tags != null ? string.Join("\n", tags) : "No tags";
                            }
                            catch (Exception ex) { errorText = ex.Message; }
                        }).ConfigureAwait(false);
                        break;

                    case "get_db_structure":
                        var dbName = ReadTextArgument(arguments, "name", "block_name", "blockName");
                        if (string.IsNullOrWhiteSpace(dbName))
                        {
                            errorText = MissingArgumentMessage(arguments, "name", "name");
                            break;
                        }
                        await _runOnTiaSta(async () =>
                        {
                            try
                            {
                                var members = await _tia.GetDbStructureAsync(dbName).ConfigureAwait(false);
                                resultText = members != null ? string.Join("\n", members) : "DB not found";
                            }
                            catch (Exception ex) { errorText = ex.Message; }
                        }).ConfigureAwait(false);
                        break;

                    case "list_udt_types":
                        var udtLimit = arguments.TryGetProperty("limit", out var ulEl) && ulEl.TryGetInt32(out var ul) ? ul : 200;
                        await _runOnTiaSta(async () =>
                        {
                            try
                            {
                                var udts = await _tia.ListUdtTypesAsync(udtLimit).ConfigureAwait(false);
                                resultText = udts != null ? string.Join("\n", udts) : "No UDTs";
                            }
                            catch (Exception ex) { errorText = ex.Message; }
                        }).ConfigureAwait(false);
                        break;

                    case "delete_block":
                        var delName = arguments.TryGetProperty("block_name", out var dnEl) ? dnEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(delName)) { errorText = "block_name is required"; break; }
                        var delRefusal = await AuthorizeWriteAsync(NewWrite(ctx, "delete_block", delName)).ConfigureAwait(false);
                        if (delRefusal != null) { errorText = delRefusal.Message; break; }
                        await _runOnTiaSta(async () =>
                        {
                            try
                            {
                                var deleted = await _tia.DeleteBlockAsync(delName).ConfigureAwait(false);
                                resultText = deleted ? $"Block '{delName}' deleted successfully." : $"Block '{delName}' not found or could not be deleted.";
                            }
                            catch (Exception ex) { errorText = ex.Message; }
                        }).ConfigureAwait(false);
                        break;

                    default:
                        errorText = $"Unknown tool: {toolName}";
                        break;
                }
            }
            catch (Exception ex)
            {
                errorText = ex.Message;
            }

            if (errorText != null)
            {
                WriteJson(ctx.Response, 200, new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "text", text = $"Error: {errorText}" } }, isError = true } });
            }
            else
            {
                WriteJson(ctx.Response, 200, new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "text", text = resultText ?? "" } } } });
            }
        }

        /// <summary>
        /// Renders the capability manifest into compact text for AI consumption.
        /// summary: namespaces + type names (controls tokens); full: includes method/property/
        /// enum members. namespace_filter focuses one area (e.g. "HmiUnified") to avoid
        /// returning the whole surface at once.
        /// </summary>
        private static string BuildCapabilityReport(OpennessCapabilityManifest m, string nsFilter, bool full)
        {
            if (m == null) return "Capability manifest not available.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# TIA Openness Capability Report");
            sb.AppendLine($"Engineering.dll version: {m.EngineeringDllVersion}");
            sb.AppendLine($"Assemblies: {string.Join(", ", m.Assemblies ?? new List<string>())}");
            sb.AppendLine($"Total public types: {m.TotalTypeCount}   (source: {(m.FromCache ? "cache" : "live scan")})");
            if (!string.IsNullOrWhiteSpace(nsFilter))
                sb.AppendLine($"Filter: '{nsFilter}'");
            sb.AppendLine($"Detail: {(full ? "full" : "summary")}");
            sb.AppendLine();

            var namespaces = m.Namespaces ?? new List<OpennessNamespaceInfo>();

            bool Match(string s) =>
                string.IsNullOrWhiteSpace(nsFilter) ||
                (s != null && s.IndexOf(nsFilter, StringComparison.OrdinalIgnoreCase) >= 0);

            // Output size cap (prevents token explosion).
            const int maxLines = 1200;
            int lineCount = 0;
            bool truncated = false;

            foreach (var ns in namespaces)
            {
                var nsMatches = Match(ns.Namespace);
                var types = ns.Types ?? new List<OpennessTypeInfo>();

                // If the namespace itself doesn't match, see whether any type matches.
                var shownTypes = nsMatches
                    ? types
                    : types.Where(t => Match(t.FullName) || Match(t.Name)).ToList();

                if (shownTypes.Count == 0) continue;

                sb.AppendLine($"## {ns.Namespace}  ({shownTypes.Count} types)");
                lineCount++;

                foreach (var t in shownTypes)
                {
                    if (lineCount >= maxLines) { truncated = true; break; }

                    var baseInfo = string.IsNullOrEmpty(t.BaseType) ? "" : $" : {ShortName(t.BaseType)}";
                    sb.AppendLine($"- [{t.Kind}] {t.Name}{baseInfo}");
                    lineCount++;

                    if (!full) continue;

                    if (t.EnumValues != null && t.EnumValues.Count > 0)
                    {
                        sb.AppendLine($"    values: {string.Join(", ", t.EnumValues)}");
                        lineCount++;
                    }
                    if (t.Methods != null && t.Methods.Count > 0)
                    {
                        sb.AppendLine($"    methods: {string.Join(", ", t.Methods)}");
                        lineCount++;
                    }
                    if (t.Properties != null && t.Properties.Count > 0)
                    {
                        sb.AppendLine($"    props: {string.Join(", ", t.Properties)}");
                        lineCount++;
                    }
                }

                if (truncated) break;
                sb.AppendLine();
            }

            if (truncated)
                sb.AppendLine($"\n... (output truncated at {maxLines} lines; narrow the query with namespace_filter).");

            return sb.ToString();
        }

        private static string ShortName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return fullName;
            var idx = fullName.LastIndexOf('.');
            return idx >= 0 && idx < fullName.Length - 1 ? fullName.Substring(idx + 1) : fullName;
        }

        private void WriteJson(HttpListenerResponse response, int statusCode, object payload)
        {
            string body = JsonSerializer.Serialize(payload, _jsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            using (var stream = response.OutputStream)
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
