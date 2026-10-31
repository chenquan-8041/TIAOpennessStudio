using System;
using System.Threading.Tasks;
using System.Windows;
using Prism.DryIoc;
using Prism.Events;
using Prism.Ioc;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Implementations;
using TIAOpennessStudio.Services.Implementations.Tools;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.ViewModels;
using TIAOpennessStudio.AICore;
#if TIA_V21
using Siemens = TIAOpennessStudio.Services.Portal;
#endif

namespace TIAOpennessStudio
{
    public partial class App : PrismApplication
    {
        private IBackendHostService _backendHostService;
        private IOllamaHostService _ollamaHostService;
        private IProjectKnowledgeWorkerHostService _knowledgeWorkerHostService;
        private IProjectKnowledgeCacheDaemonService _knowledgeCacheDaemon;
        private UnifiedLogService _unifiedLogService;
        private IThemeService _themeService;
        private MainWindow _shell;
        private TiaContextHttpServer _tiaContextHttpServer;

        private static bool IsTruthy(string s)
            => !string.IsNullOrWhiteSpace(s) &&
               (s.Equals("1") || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase));

        public App()
        {
            var crashLogDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            var crashLogPath = System.IO.Path.Combine(crashLogDir, "crash.log");

            this.DispatcherUnhandledException += (s, e) =>
            {
                try { System.IO.Directory.CreateDirectory(crashLogDir); System.IO.File.AppendAllText(crashLogPath, "[Dispatcher] " + e.Exception.ToString() + "\n\n"); } catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { System.IO.Directory.CreateDirectory(crashLogDir); System.IO.File.AppendAllText(crashLogPath, "[AppDomain] " + e.ExceptionObject.ToString() + "\n\n"); } catch { }
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try { System.IO.Directory.CreateDirectory(crashLogDir); System.IO.File.AppendAllText(crashLogPath, "[Task] " + e.Exception.ToString() + "\n\n"); } catch { }
            };
        }

        /// <summary>
        /// Static accessor to the Prism container, replacing the old App.Services.
        /// Usage: App.Services.Resolve&lt;T&gt;()
        /// </summary>
        public static IContainerProvider Services => ((App)Current).Container;

        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {
            // Theme service
            _themeService = new ThemeService();
            try
            {
                _themeService.ApplySavedTheme();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Theme initialization failed", ex);
            }
            containerRegistry.RegisterSingleton<IThemeService>(c => _themeService);

            // Restore the language used in the previous session (defaults to English)
            try
            {
                LanguageManager.Instance.ApplySavedLanguage();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Language initialization failed", ex);
            }

            // Initialize ChatMessage theme tracking so the Agent icon follows theme switches
            Models.ChatMessage.InitializeThemeTracking(_themeService);

            // AI Config
            var aiConfig = AIConfig.Load();

            // Agent Config (V2 refactor feature flags, defaults to all-off)
            containerRegistry.RegisterSingleton<AgentConfig>(c => aiConfig.Agent ?? AgentConfig.CreateDefault());

            // AI Engine Service (Singleton: AgentOrchestrator / MainViewModel / MainWindow share one instance, so UpdateRuntimeConfig takes effect globally)
            containerRegistry.RegisterSingleton<IAIEngineService>(c => new RestAICodeGenerator(
                aiConfig.ApiKey,
                aiConfig.Endpoint,
                aiConfig.Model,
                aiConfig.Provider,
                aiConfig.UseLocalBackend,
                aiConfig.LocalBackendUrl,
                aiConfig.ReasoningEffort
            ));

            // Backend Host Service
            _backendHostService = new BackendHostService(aiConfig.LocalBackendUrl, AppDomain.CurrentDomain.BaseDirectory);
            containerRegistry.RegisterSingleton<IBackendHostService>(c => _backendHostService);
            if (aiConfig.UseLocalBackend)
            {
                _ = _backendHostService.EnsureStartedAsync();
            }

            // Ollama Host Service
            _ollamaHostService = new OllamaHostService();
            containerRegistry.RegisterSingleton<IOllamaHostService>(c => _ollamaHostService);
            _ = InitializeOllamaAsync();

            // Core Services
            containerRegistry.RegisterSingleton<IUIService, UIService>();
            containerRegistry.RegisterSingleton<IGlobalProgressService>(c =>
                new GlobalProgressService(c.Resolve<IEventAggregator>()));

            // Unified cache path: %LocalAppData%\TIAOpennessStudio\KnowledgeCache\
            // Shared between main process and TIAKnowledgeWorker.exe
            var knowledgeCacheBasePath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TIAOpennessStudio",
                "KnowledgeCache");

            containerRegistry.RegisterSingleton<ProjectIndexService>(c => new ProjectIndexService(knowledgeCacheBasePath));
            containerRegistry.RegisterSingleton<IProjectIndexService>(c => c.Resolve<ProjectIndexService>());
            containerRegistry.RegisterSingleton<IPlcKnowledgeIndexService, PlcKnowledgeIndexService>();
            containerRegistry.RegisterSingleton<IPlanningKnowledgeService>(c =>
                new PlanningKnowledgeService(c.Resolve<ProjectIndexService>()));
            containerRegistry.RegisterSingleton<PlcDependencyParser>();
            containerRegistry.RegisterSingleton<IProjectKnowledgeWorkerHostService>(c => new ProjectKnowledgeWorkerHostService(AppDomain.CurrentDomain.BaseDirectory));

            // ---- Network-centric Knowledge Model (v2.0) ----
            containerRegistry.RegisterSingleton<PlcNetworkFactExtractor>();
            // v3: persistence moved to PlcKnowledgeStore (single-writer SQLite).
            // INetworkDocumentStore is the in-memory snapshot/facade layer only.
            containerRegistry.RegisterSingleton<INetworkDocumentStore>(c =>
                new InMemoryNetworkDocumentStore());
            containerRegistry.RegisterSingleton<ICrossReferenceProvider>(c =>
                new OpennessCrossReferenceProvider(c.Resolve<ITiaOpennessService>()));
            containerRegistry.RegisterSingleton<ProjectSnapshotManager>(c =>
                new ProjectSnapshotManager(
                    c.Resolve<INetworkDocumentStore>(),
                    c.Resolve<PlcNetworkFactExtractor>()));
            containerRegistry.RegisterSingleton<NetworkKnowledgeFacade>(c =>
                new NetworkKnowledgeFacade(
                    c.Resolve<ProjectSnapshotManager>(),
                    c.Resolve<INetworkDocumentStore>(),
                    c.Resolve<PlcNetworkFactExtractor>(),
                    null, // NetworkAnnotationService - optional
                    c.Resolve<ICrossReferenceProvider>()));

            // ---- External encrypted knowledge base: official coding standards + user-mounted private libraries ----
            containerRegistry.RegisterSingleton<TIAOpennessStudio.Services.Standards.StandardsKnowledgeStore>(c =>
                new TIAOpennessStudio.Services.Standards.StandardsKnowledgeStore(knowledgeCacheBasePath));
            containerRegistry.RegisterSingleton<TIAOpennessStudio.Services.Standards.StandardsRegistry>(c =>
                new TIAOpennessStudio.Services.Standards.StandardsRegistry(knowledgeCacheBasePath));

            // ---- v3 Layered Knowledge Store (single-writer, WAL, read/write split) ----
            containerRegistry.RegisterSingleton<PlcKnowledgeStore>(c => new PlcKnowledgeStore(knowledgeCacheBasePath));
            containerRegistry.RegisterSingleton<PlcKnowledgeStoreWriter>(c =>
                new PlcKnowledgeStoreWriter(c.Resolve<PlcKnowledgeStore>()));
            containerRegistry.RegisterSingleton<PlcKnowledgeIndexer>(c =>
                new PlcKnowledgeIndexer(
                    c.Resolve<ITiaOpennessService>(),
                    c.Resolve<IBlockVersionService>(),
                    c.Resolve<PlcKnowledgeStoreWriter>(),
                    c.Resolve<PlcNetworkFactExtractor>()));

            // In-process indexing + cache orchestration + auto-warmup daemon.
            // The daemon rebuilds the cache automatically when TIA connects; the manual
            // "Rebuild Index" button remains available (it goes through the worker).
            containerRegistry.RegisterSingleton<IPlcProjectIndexingService>(c =>
                new PlcProjectIndexingService(c.Resolve<PlcKnowledgeIndexer>()));
            containerRegistry.RegisterSingleton<IProjectKnowledgeCacheService>(c =>
                new ProjectKnowledgeCacheService(
                    c.Resolve<ITiaOpennessService>(),
                    c.Resolve<IBlockVersionService>(),
                    c.Resolve<ProjectIndexService>(),
                    c.Resolve<IPlcKnowledgeIndexService>(),
                    c.Resolve<IPlcProjectIndexingService>(),
                    c.Resolve<IGlobalProgressService>()));
            containerRegistry.RegisterSingleton<IProjectKnowledgeCacheDaemonService>(c =>
                new ProjectKnowledgeCacheDaemonService(
                    c.Resolve<ITiaOpennessService>(),
                    c.Resolve<IProjectKnowledgeCacheService>(),
                    c.Resolve<PlcKnowledgeIndexer>()));

            containerRegistry.RegisterSingleton<IBackupService>(c => new BackupService(AppDomain.CurrentDomain.BaseDirectory));
            containerRegistry.RegisterSingleton<ITiaOpennessService, OpennessService>();
            containerRegistry.RegisterSingleton<ITiaToolService, TiaToolService>();
            containerRegistry.RegisterSingleton<IReActLoopEngine, ReActLoopEngine>();
            containerRegistry.RegisterSingleton<ICloudAgentEngine, CloudAgentEngine>();
            containerRegistry.RegisterSingleton<IPlcContextBuilder, PlcContextBuilder>();
            containerRegistry.RegisterSingleton<ContextAssembler>(c =>
                new ContextAssembler(
                    c.Resolve<IPlcKnowledgeIndexService>(),
                    c.Resolve<ITiaOpennessService>(),
                    c.Resolve<IPlcContextBuilder>()));
            containerRegistry.RegisterSingleton<ITiaVersionAdapter, TiaVersionAdapter>();
            #if TIA_V21
            containerRegistry.RegisterSingleton<ITiaPortalService, TiaPortalService>();
            containerRegistry.RegisterSingleton<Siemens.Portal>(c => new Siemens.Portal(null));
            containerRegistry.RegisterSingleton<InProcessTiaToolProvider>(c =>
                new InProcessTiaToolProvider(
                    c.Resolve<Siemens.Portal>(),
                    c.Resolve<ToolRegistry>(),
                    c.Resolve<ITiaVersionAdapter>()));
#endif
            containerRegistry.RegisterSingleton<ILibraryCompareService, LibraryCompareService>();
            containerRegistry.RegisterSingleton<IAgentOrchestrator, AgentOrchestrator>();
            containerRegistry.RegisterSingleton<IIntentUnderstander, IntentUnderstander>();
            containerRegistry.RegisterSingleton<IAgentPlanner, AgentPlanner>();
            containerRegistry.RegisterSingleton<IAgentReflector, AgentReflector>();
            containerRegistry.RegisterSingleton<IObservationCompressor, ObservationCompressor>();
            containerRegistry.RegisterSingleton<ISemanticCompressor, SemanticCompressor>();
            containerRegistry.RegisterSingleton<IPlanTracker, PlanTracker>();
            containerRegistry.RegisterSingleton<IUnifiedAgentEngine>(c =>
                new UnifiedAgentEngine(
                    c.Resolve<IReActLoopEngine>(),
                    c.Resolve<ICloudAgentEngine>(),
                    c.Resolve<AgentConfig>()));
            containerRegistry.RegisterSingleton<IAgentTracer, AgentTracer>();
            containerRegistry.RegisterSingleton<TIAOpennessStudio.Services.TokenBudget>();

            // ILogger<T> registrations: several services (OpcUaService / McpAdapter / McpToolRegistrySync, etc.)
            // require ILogger<T> in their constructors. Previously the container had no ILogger<> registration, so DryIoc
            // could not construct these services, causing Resolve<IOpcUaService>() to throw and the UI to show "OPC UA service not available".
            // NullLogger<T>.Instance is a public static singleton (its constructor is internal, so it cannot be newed directly);
            // register it per concrete type that actually needs logging — simple, reliable, zero extra dependencies.
            containerRegistry.RegisterInstance<Microsoft.Extensions.Logging.ILogger<OpcUaService>>(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OpcUaService>.Instance);

            containerRegistry.RegisterSingleton<IOpcUaService, OpcUaService>();
            containerRegistry.RegisterSingleton<IDialogService, DialogService>();
            containerRegistry.RegisterSingleton<ICodeViewerService, CodeViewerService>();
            containerRegistry.RegisterSingleton<IBlockVersionService, BlockVersionService>();
            containerRegistry.RegisterSingleton<IChatRequestRouter, ChatRequestRouter>();
            containerRegistry.RegisterSingleton<ILocalChatService, LocalChatService>();
            containerRegistry.RegisterSingleton<ISimpleAnalyzeService, SimpleAnalyzeService>();

            // Quality, Patch, SLO, Eval
            containerRegistry.RegisterSingleton<IQualityGateService, QualityGateService>();
            containerRegistry.RegisterSingleton<IPatchService, PatchService>();
            containerRegistry.RegisterSingleton<ISloService, SloService>();
            containerRegistry.RegisterSingleton<IEvalHarness, EvalHarness>();

            // Skill, Debug, Workflow
            containerRegistry.RegisterSingleton<ISkillManager, SkillManager>();
            containerRegistry.RegisterSingleton<IDebugService, DebugService>();
            containerRegistry.RegisterSingleton<BuiltInToolsAdapter>();
            containerRegistry.RegisterSingleton<IWorkflowEngine>(c =>
                new WorkflowEngine(c.Resolve<ISkillManager>(), c.Resolve<BuiltInToolsAdapter>()));

            // HMI Builder
            containerRegistry.RegisterSingleton<IHmiBuilderService, HmiBuilderService>();

            // Ops Services
            containerRegistry.RegisterSingleton<IBackendReleaseService>(c =>
                new BackendReleaseService(AppDomain.CurrentDomain.BaseDirectory));
            containerRegistry.RegisterSingleton<IHealthMonitorService>(c =>
                new HealthMonitorService(c.Resolve<IBackendHostService>(), c.Resolve<ISloService>()));

            // Shared target-group selection (bridges status-bar chip → agent tools)
            containerRegistry.RegisterSingleton<IAgentTargetGroupProvider, AgentTargetGroupProvider>();

            // IAgentTool implementations (TIA operations)
            containerRegistry.RegisterSingleton<ExportBlockTool>();
            containerRegistry.RegisterSingleton<ExportBlockIrTool>();
            containerRegistry.RegisterSingleton<CompileTool>();
            containerRegistry.RegisterSingleton<GetProjectTreeTool>();
            containerRegistry.RegisterSingleton<SearchProjectTool>();
            containerRegistry.RegisterSingleton<QueryVariableReferencesTool>();
            containerRegistry.RegisterSingleton<GetIndexStatusTool>();
            containerRegistry.RegisterSingleton<RefreshProjectIndexTool>();
            containerRegistry.RegisterSingleton<ImportBlockCodeTool>();
            containerRegistry.RegisterSingleton<ResolveXmlFragmentTool>();

            // IAgentTool implementations (PLC build)
            containerRegistry.RegisterSingleton<PlcBuildUdtTool>();
            containerRegistry.RegisterSingleton<PlcBuildTagTableTool>();
            containerRegistry.RegisterSingleton<PlcBuildGlobalDbTool>();
            containerRegistry.RegisterSingleton<PlcBuildFcTool>();
            containerRegistry.RegisterSingleton<PlcBuildFbTool>();
            containerRegistry.RegisterSingleton<PlcBuildLadFcTool>();
            containerRegistry.RegisterSingleton<PlcBuildAndImportTool>();

            // IAgentTool implementations (general purpose)
            containerRegistry.RegisterSingleton<FileReadTool>();
            containerRegistry.RegisterSingleton<FileWriteTool>();
            containerRegistry.RegisterSingleton<FileListTool>();
            containerRegistry.RegisterSingleton<WebSearchTool>();
            containerRegistry.RegisterSingleton<CalculatorTool>();
            containerRegistry.RegisterSingleton<ExecuteCommandTool>();
            containerRegistry.RegisterSingleton<GetSystemInfoTool>();
            containerRegistry.RegisterSingleton<JsonParserTool>();

            // IAgentTool: coding-standard lookup (official library + user-mounted library; read-only, no export)
            containerRegistry.RegisterSingleton<SearchCodingStandardTool>();

            // IAgentTool implementations (OPC UA live read: reads PLC live variables / DB blocks when a connection is active)
            containerRegistry.RegisterSingleton<OpcUaStatusTool>();
            containerRegistry.RegisterSingleton<OpcUaReadNodeTool>();
            containerRegistry.RegisterSingleton<OpcUaBrowseTool>();
            containerRegistry.RegisterSingleton<OpcUaReadDbTool>();
            containerRegistry.RegisterSingleton<OpcUaWriteNodeTool>();

            // G-6: incremental LAD authoring — session draft store + 5 draft tools.
            containerRegistry.RegisterSingleton<ILadDraftStore>(c =>
                new LadDraftStore(AppDomain.CurrentDomain.BaseDirectory));
            containerRegistry.RegisterSingleton<LadDraftBeginTool>();
            containerRegistry.RegisterSingleton<LadDraftAppendNetworkTool>();
            containerRegistry.RegisterSingleton<LadDraftPreviewTool>();
            containerRegistry.RegisterSingleton<LadDraftDiscardTool>();
            containerRegistry.RegisterSingleton<LadDraftCommitTool>();

            // C-6: local tools over existing ITiaOpennessService (previously MCP-only).
            containerRegistry.RegisterSingleton<BootstrapTool>();
            containerRegistry.RegisterSingleton<HardwareSearchTool>();
            containerRegistry.RegisterSingleton<DeleteBlockTool>();

            // G-4: cross-session user memory store (advisory context, not project fact).
            containerRegistry.RegisterSingleton<IUserMemoryService>(c =>
                new UserMemoryService(AppDomain.CurrentDomain.BaseDirectory));

            // Agent <-> user interaction (approve / choose / input). The presenter host is late-bound:
            // the chat drawer attaches itself once the main window exists. Until then requests fail closed.
            containerRegistry.RegisterSingleton<InteractionPresenterHost>();
            containerRegistry.RegisterSingleton<IUserInteractionService>(c =>
                new UserInteractionService(
                    c.Resolve<InteractionPresenterHost>(),
                    c.Resolve<AgentConfig>().ApprovalTimeoutSeconds,
                    c.Resolve<AgentConfig>().ApprovalHardTimeoutSeconds));

            // Read-only / allow-writes switch (input box toggle). One shared instance: the toggle writes
            // Current, runs bind their starting mode, and the :8001 write gate reads it for elevation.
            containerRegistry.RegisterSingleton<IAgentAccessMode, AgentAccessModeService>();

            // ToolRegistry: centralized tool management for ReActLoopEngine
            containerRegistry.RegisterSingleton<ToolRegistry>(c =>
            {
                var registry = new ToolRegistry();
                registry.Register(c.Resolve<ExportBlockTool>());
                registry.Register(c.Resolve<ExportBlockIrTool>());
                registry.Register(c.Resolve<CompileTool>());
                registry.Register(c.Resolve<GetProjectTreeTool>());
                registry.Register(c.Resolve<SearchProjectTool>());
                registry.Register(c.Resolve<QueryVariableReferencesTool>());
                registry.Register(c.Resolve<GetIndexStatusTool>());
                registry.Register(c.Resolve<RefreshProjectIndexTool>());
                registry.Register(c.Resolve<ImportBlockCodeTool>());
                registry.Register(c.Resolve<ResolveXmlFragmentTool>());
                registry.Register(c.Resolve<PlcBuildUdtTool>());
                registry.Register(c.Resolve<PlcBuildTagTableTool>());
                registry.Register(c.Resolve<PlcBuildGlobalDbTool>());
                registry.Register(c.Resolve<PlcBuildFcTool>());
                registry.Register(c.Resolve<PlcBuildFbTool>());
                registry.Register(c.Resolve<PlcBuildLadFcTool>());
                registry.Register(c.Resolve<PlcBuildAndImportTool>());
                registry.Register(c.Resolve<FileReadTool>());
                registry.Register(c.Resolve<FileWriteTool>());
                registry.Register(c.Resolve<FileListTool>());
                registry.Register(c.Resolve<WebSearchTool>());
                registry.Register(c.Resolve<CalculatorTool>());
                registry.Register(c.Resolve<ExecuteCommandTool>());
                registry.Register(c.Resolve<GetSystemInfoTool>());
                registry.Register(c.Resolve<JsonParserTool>());
                registry.Register(c.Resolve<SearchCodingStandardTool>());
                registry.Register(c.Resolve<OpcUaStatusTool>());
                registry.Register(c.Resolve<OpcUaReadNodeTool>());
                registry.Register(c.Resolve<OpcUaBrowseTool>());
                registry.Register(c.Resolve<OpcUaReadDbTool>());
                registry.Register(c.Resolve<OpcUaWriteNodeTool>());
                registry.Register(c.Resolve<LadDraftBeginTool>());
                registry.Register(c.Resolve<LadDraftAppendNetworkTool>());
                registry.Register(c.Resolve<LadDraftPreviewTool>());
                registry.Register(c.Resolve<LadDraftDiscardTool>());
                registry.Register(c.Resolve<LadDraftCommitTool>());
                registry.Register(c.Resolve<BootstrapTool>());
                registry.Register(c.Resolve<HardwareSearchTool>());
                registry.Register(c.Resolve<DeleteBlockTool>());
                return registry;
            });

            // MCP Adapter & Tool Bridge
            containerRegistry.RegisterSingleton<IMcpAdapter>(c =>
                new McpAdapter(Microsoft.Extensions.Logging.Abstractions.NullLogger<McpAdapter>.Instance));
            containerRegistry.RegisterSingleton<McpToolRegistrySync>();

            // Agent Persistence
            containerRegistry.RegisterSingleton<AgentPersistenceService>();

            // Local LLM Service (Singleton: SetModel must be visible to all consumers)
            containerRegistry.RegisterSingleton<ILocalLlmService>(c =>
            {
                var defaultModel = aiConfig.DefaultOllamaModel ?? "qwen2.5:0.5b";
                return new OllamaLlmService("http://localhost:11434", defaultModel);
            });

            // ViewModels
            containerRegistry.Register<MainViewModel>();
            containerRegistry.Register<ChatViewModel>(c =>
            {
                var chatRequestRouter = c.Resolve<IChatRequestRouter>();
                var agentOrchestrator = c.Resolve<IAgentOrchestrator>();
                var localChatService = c.Resolve<ILocalChatService>();
                var simpleAnalyzeService = c.Resolve<ISimpleAnalyzeService>();
                var aiService = c.Resolve<IAIEngineService>();
                var tiaService = c.Resolve<ITiaOpennessService>();
                var targetGroupProvider = c.Resolve<IAgentTargetGroupProvider>();
                var cfg = AIConfig.Load();
                return new ChatViewModel(chatRequestRouter, agentOrchestrator, localChatService, simpleAnalyzeService, aiService, tiaService, cfg, targetGroupProvider,
                    c.Resolve<IUserInteractionService>());
            });
            containerRegistry.Register<AIConfigViewModel>(c =>
            {
                var backendHostService = c.Resolve<IBackendHostService>();
                var eventAggregator = c.Resolve<IEventAggregator>();
                return new AIConfigViewModel(backendHostService, eventAggregator);
            });
            containerRegistry.Register<ProjectViewModel>(c =>
            {
                var tiaService = c.Resolve<ITiaOpennessService>();
                var aiService = c.Resolve<IAIEngineService>();
                var eventAggregator = c.Resolve<IEventAggregator>();
                return new ProjectViewModel(tiaService, aiService, eventAggregator);
            });
            containerRegistry.Register<TreeViewModel>(c =>
            {
                var tiaService = c.Resolve<ITiaOpennessService>();
                var eventAggregator = c.Resolve<IEventAggregator>();
                return new TreeViewModel(tiaService, eventAggregator);
            });
            containerRegistry.Register<GraphViewModel>(c =>
            {
                var eventAggregator = c.Resolve<IEventAggregator>();
                return new GraphViewModel(eventAggregator);
            });
            containerRegistry.Register<DependencyAnalysisViewModel>(c =>
            {
                var workerHost = c.Resolve<IProjectKnowledgeWorkerHostService>();
                return new DependencyAnalysisViewModel(workerHost);
            });
        }

        protected override Window CreateShell()
        {
            _shell = Container.Resolve<MainWindow>();
            return _shell;
        }

        protected override void OnInitialized()
        {
            base.OnInitialized();

            var mainWindow = _shell;

            // Initialize Logger with UI context
            AppLogger.Initialize(mainWindow.FindName("txtOutputLog") as System.Windows.Controls.TextBox, mainWindow.Dispatcher,
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"));

            // Wire up UI-specific service actions
            var uiService = Container.Resolve<IUIService>() as UIService;
            if (uiService != null)
            {
                uiService.ShowOverlayAction = msg =>
                {
                    mainWindow.OverlayGrid.Visibility = Visibility.Visible;
                    mainWindow.txtOverlayMessage.Text = msg;
                };
                uiService.HideOverlayAction = () => mainWindow.OverlayGrid.Visibility = Visibility.Collapsed;
                uiService.SetStatusAction = msg =>
                {
                    if (mainWindow.DataContext is MainViewModel vm)
                        vm.StatusText = msg;
                };
            }

            // Start the local HTTP service (port 8001) to expose TIA project context to the Python ReAct Agent.
            // Port conflicts and other exceptions are caught and logged inside TiaContextHttpServer.Start(),
            try
            {
                // Start knowledge worker process (serves read-only retrieval over HTTP).
                _knowledgeWorkerHostService = Container.Resolve<IProjectKnowledgeWorkerHostService>();
                _ = _knowledgeWorkerHostService.EnsureStartedAsync();

                // Start in-process auto-warmup daemon: rebuilds the knowledge cache
                // automatically when TIA Portal connects (writes the v3 store in-process).
                // The manual "Rebuild Index" button remains available.
                _knowledgeCacheDaemon = Container.Resolve<IProjectKnowledgeCacheDaemonService>();
                _knowledgeCacheDaemon.Start();

                // Start the unified log service (merges Python/Worker logs into OutputLog)
                _unifiedLogService = new UnifiedLogService(_knowledgeWorkerHostService);
                _unifiedLogService.Start();

                var tiaService = Container.Resolve<ITiaOpennessService>();

                // Route all Openness calls through the dedicated STA host thread so the
                // WPF UI thread is never blocked by TIA COM operations.
                // TiaStaHost.Instance is a lazily-initialized singleton that spins up a
                // background STA thread with its own WPF Dispatcher message pump on first
                // access; subsequent calls reuse the same thread.
                Func<Func<Task>, Task> runOnTiaSta = work =>
                    TiaStaHost.Instance.RunAsync(work);

                // MCP external exposure / auth config (secure defaults: local only, no forced auth).
                //   TIA_MCP_EXPOSE_REMOTE = 1/true  → bind to all NICs, allow cloud/remote access
                //   TIA_MCP_AUTH_TOKEN    = <token> → enforce Bearer validation
                // When exposed remotely without a token, a random token is generated and logged to avoid unauthenticated public exposure.
                var exposeRemote = IsTruthy(Environment.GetEnvironmentVariable("TIA_MCP_EXPOSE_REMOTE"));
                var mcpToken = Environment.GetEnvironmentVariable("TIA_MCP_AUTH_TOKEN");
                if (exposeRemote && string.IsNullOrWhiteSpace(mcpToken))
                {
                    mcpToken = Guid.NewGuid().ToString("N");
                    AppLogger.Log("[TiaCtxHttp] remote exposure ON but no TIA_MCP_AUTH_TOKEN set; generated a random token (see below). Set TIA_MCP_AUTH_TOKEN to a fixed value for clients.");
                    AppLogger.Log("[TiaCtxHttp] generated MCP auth token: " + mcpToken);
                }

                // Standards knowledge base (official library + user-mounted library) injection, for the MCP tools list_standard_topics / read_standard_section to query.
                // A resolve failure does not block service startup (the tools will return a "standards library unavailable" hint).
                TIAOpennessStudio.Services.Standards.StandardsRegistry standardsRegistry = null;
                try { standardsRegistry = Services.Resolve<TIAOpennessStudio.Services.Standards.StandardsRegistry>(); }
                catch (Exception ex) { AppLogger.Log("[TiaCtxHttp] StandardsRegistry resolve failed: " + ex.Message); }

                // Repair-experience store (repair_experience) injection; a resolve failure does not block startup, and the related endpoints return "unavailable".
                TIAOpennessStudio.Services.ProjectIndexService projectIndex = null;
                try { projectIndex = Services.Resolve<TIAOpennessStudio.Services.ProjectIndexService>(); }
                catch (Exception ex) { AppLogger.Log("[TiaCtxHttp] ProjectIndexService resolve failed: " + ex.Message); }

                // Agent writes over :8001 (import_block, build_lad_*, delete_block, /api/import_scl) are held
                // until the user approves them in the chat drawer. If this resolve fails the server is built
                // without it, and then it refuses every such write rather than running it unasked.
                IUserInteractionService interaction = null;
                try { interaction = Services.Resolve<IUserInteractionService>(); }
                catch (Exception ex) { AppLogger.Log("[TiaCtxHttp] IUserInteractionService resolve failed, writes will be refused: " + ex.Message); }

                // Read-only elevation: resolves the same shared instance the input-box toggle writes to.
                // A resolve failure degrades to "no elevation check" (writes still gated by the normal card).
                IAgentAccessMode accessMode = null;
                try { accessMode = Services.Resolve<IAgentAccessMode>(); }
                catch (Exception ex) { AppLogger.Log("[TiaCtxHttp] IAgentAccessMode resolve failed, read-only elevation disabled: " + ex.Message); }

                // HMI offline builder (build_hmi_screen / build_hmi_tag_table); resolve failure only degrades those two tools.
                IHmiBuilderService hmiBuilder = null;
                try { hmiBuilder = Services.Resolve<IHmiBuilderService>(); }
                catch (Exception ex) { AppLogger.Log("[TiaCtxHttp] IHmiBuilderService resolve failed: " + ex.Message); }

                // Approval deadlines come from AgentConfig so a slow user can still approve later.
                int approvalSoft = 0, approvalHard = 0, approvalAttempts = 0;
                try
                {
                    var agentConfig = Services.Resolve<AgentConfig>();
                    approvalSoft = agentConfig.ApprovalTimeoutSeconds;
                    approvalHard = agentConfig.ApprovalHardTimeoutSeconds;
                    approvalAttempts = agentConfig.ApprovalMaxAttempts;
                }
                catch (Exception ex) { AppLogger.Log("[TiaCtxHttp] AgentConfig resolve failed, using approval defaults: " + ex.Message); }

                _tiaContextHttpServer = new TiaContextHttpServer(
                    tiaService, runOnTiaSta, _knowledgeWorkerHostService,
                    exposeRemotely: exposeRemote, authToken: mcpToken,
                    standards: standardsRegistry, projectIndex: projectIndex,
                    interaction: interaction, accessMode: accessMode,
                    hmiBuilder: hmiBuilder,
                    approvalTimeoutSeconds: approvalSoft, approvalHardTimeoutSeconds: approvalHard,
                    approvalMaxAttempts: approvalAttempts);
                _tiaContextHttpServer.Start();

                // ── In-Process TIA Tool Provider: registers Portal operations as Agent tools ──
#if TIA_V21
                try
                {
                    var versionAdapter = Container.Resolve<ITiaVersionAdapter>();
                    versionAdapter.DetectAndInitialize();

                    var inProcessProvider = Container.Resolve<InProcessTiaToolProvider>();
                    inProcessProvider.RegisterAvailableTools();
                }
                catch (Exception exTia)
                {
                    AppLogger.Log($"[InProcessTiaToolProvider] initialization skipped: {exTia.Message}");
                }
#endif
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[TiaCtxHttp] failed to initialize: {ex.Message}");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                try
                {
                    _tiaContextHttpServer?.Stop();
                    _tiaContextHttpServer?.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[TiaCtxHttp] stop error on exit: {ex.Message}");
                }
                finally
                {
                    _tiaContextHttpServer = null;
                }

                _backendHostService?.StopOwnedProcess();
                _ollamaHostService?.StopOwnedProcess();
                _knowledgeWorkerHostService?.StopOwnedProcess();
            }
            finally
            {
                base.OnExit(e);
            }
        }

        private async System.Threading.Tasks.Task InitializeOllamaAsync()
        {
            try
            {
                AppLogger.Log("[Ollama] Initializing Ollama service...");

                var started = await _ollamaHostService.EnsureStartedAsync();
                if (!started)
                {
                    AppLogger.Log("[Ollama] Failed to start Ollama service. Local LLM will be unavailable.");
                    return;
                }

                // Determine model: user preference > auto-detection
                var config = AIConfig.Load();
                string model = config?.DefaultOllamaModel;
                long availableMemory = 0;

                if (!string.IsNullOrEmpty(model))
                {
                    AppLogger.Log($"[Ollama] Using user-selected default model: {model}");
                }
                else
                {
                    // Auto-detect best model based on system memory
                    availableMemory = GetAvailablePhysicalMemory();
                    if (availableMemory <= 0)
                        availableMemory = 4L * 1024 * 1024 * 1024;

                    AppLogger.Log($"[Ollama] System available memory: {availableMemory / (1024 * 1024)} MB");
                    model = await _ollamaHostService.PickBestModelAsync(availableMemory);
                }

                if (string.IsNullOrEmpty(model))
                {
                    // PickBestModelAsync returned empty — either no models installed at all,
                    // or all installed models exceeded the memory budget.
                    // Check whether any model is actually installed before attempting a pull.
                    var anyInstalled = await _ollamaHostService.GetInstalledModelsAsync();
                    if (anyInstalled.Count > 0)
                    {
                        // Models exist but didn't fit budget estimate — just use the first one.
                        // The user may have a large model that exceeds the conservative budget
                        // heuristic; Ollama will handle memory pressure itself.
                        model = anyInstalled[0];
                        AppLogger.Log($"[Ollama] All installed models exceeded memory budget estimate; using '{model}' anyway.");
                    }
                    else
                    {
                        // Truly no models installed — prompt user rather than silently pulling
                        // an arbitrary old qwen2.5 model that may no longer be appropriate.
                        AppLogger.Log("[Ollama] No models installed. Please install a model via 'ollama pull <model>' or the Settings panel.");
                        return;
                    }
                }

                // Ensure model is available (might need pull if PickBestModel found a model that was partially removed)
                var modelAvailable = await _ollamaHostService.IsModelAvailableAsync(model);
                if (!modelAvailable)
                {
                    AppLogger.Log($"[Ollama] Model {model} not found. Attempting to pull...");
                    var pulled = await _ollamaHostService.PullModelAsync(model);
                    if (!pulled)
                    {
                        AppLogger.Log($"[Ollama] Failed to pull model {model}. Local LLM will be unavailable.");
                        return;
                    }
                }

                // Update the OllamaLlmService default model
                var llmService = Container.Resolve<ILocalLlmService>();
                llmService?.SetModel(model);

                AppLogger.Log($"[Ollama] Service ready with model {model}.");
            }
            catch (Exception ex)
            {
                AppLogger.LogError("[Ollama] Initialization failed", ex);
            }
        }

        /// <summary>
        /// Get available physical memory in bytes via Windows API.
        /// .NET 4.8 doesn't have GC.GetGCMemoryInfo().
        /// </summary>
        private static long GetAvailablePhysicalMemory()
        {
            try
            {
                var memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref memStatus))
                    return (long)memStatus.ullAvailPhys;
            }
            catch { }
            return 0;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}

