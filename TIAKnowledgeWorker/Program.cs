using System;
using System.Threading;

namespace TIAKnowledgeWorker
{
    /// <summary>
    /// Entry point for TIAKnowledgeWorker.exe.
    /// Starts the WorkerHost and waits for shutdown signal.
    /// </summary>
    public static class Program
    {
        private static readonly ManualResetEventSlim ShutdownSignal = new ManualResetEventSlim(false);

        public static void Main(string[] args)
        {
            Console.Title = "TIA Knowledge Worker";
            Console.WriteLine("=== TIA Knowledge Worker ===");
            Console.WriteLine();

            int port = ParsePort(args);
            string authToken = ParseAuthToken(args);

            try
            {
                // Initialize file logging (resilient to directory creation failures)
                var logDir = System.IO.Path.Combine(GetCachePath(), "logs");
                try { System.IO.Directory.CreateDirectory(logDir); }
                catch (Exception ex) { Console.WriteLine($"[Warn] Cannot create log dir: {ex.Message}"); }

                var logFile = System.IO.Path.Combine(logDir, "worker.log");
                try { TIAOpennessStudio.AppLogger.Initialize(logFile); }
                catch { Console.WriteLine("[Warn] Cannot initialize file logger, using console only."); }

                TIAOpennessStudio.AppLogger.Log("=== TIA Knowledge Worker starting ===");
                TIAOpennessStudio.AppLogger.Log($"[Init] Cache path: {GetCachePath()}");
                TIAOpennessStudio.AppLogger.Log($"[Init] Log file: {logFile}");

                // Openness DLL is not copied into bin (Private=false). Bind it at runtime from the
                // installed TIA PublicAPI folder, so the worker uses the same Openness API version as
                // the machine's TIA Portal instead of the version it was compiled against.
                try
                {
                    var apiResolution = new TIAOpennessStudio.Services.Implementations.TiaApiPathResolver()
                        .Resolve(AppDomain.CurrentDomain.BaseDirectory);
                    if (!string.IsNullOrWhiteSpace(apiResolution.ApiPath))
                    {
                        var opennessApiPath = apiResolution.ApiPath;
                        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
                        {
                            int comma = args.Name.IndexOf(',');
                            var simpleName = comma < 0 ? args.Name : args.Name.Substring(0, comma);
                            if (!string.Equals(simpleName, "Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
                                return null;

                            var dllPath = System.IO.Path.Combine(opennessApiPath, "Siemens.Engineering.dll");
                            return System.IO.File.Exists(dllPath)
                                ? System.Reflection.Assembly.LoadFrom(dllPath)
                                : (System.Reflection.Assembly)null;
                        };
                        TIAOpennessStudio.AppLogger.Log(
                            $"[Init] Openness runtime source: {opennessApiPath} (V{apiResolution.Version})");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Warn] Openness DLL resolver setup failed: " + ex.Message);
                }

                // Resolve the installed TIA version once: the canonical codec, schema registry and
                // RAG indexer all read TiaVersionAdapter.CurrentMajorVersion. Without this the
                // worker would always fall back to the V15.1 baseline profile.
                try
                {
                    var versionAdapter = new TIAOpennessStudio.Services.Implementations.TiaVersionAdapter();
                    versionAdapter.DetectAndInitialize();
                    TIAOpennessStudio.AppLogger.Log(
                        $"[Init] TIA version: V{TIAOpennessStudio.Services.Implementations.TiaVersionAdapter.CurrentVersion} (major {versionAdapter.DetectedMajorVersion})");
                }
                catch (Exception ex)
                {
                    TIAOpennessStudio.AppLogger.Log($"[Warn] TIA version detection failed, using baseline profile: {ex.Message}");
                }

                // Self-register this executable into the TIA Openness whitelist so that
                // attaching to a running TIA Portal does not prompt for manual authorization.
                // No-op unless this process is elevated; safe to fail silently.
                try
                {
                    TIAOpennessStudio.Services.TiaOpennessWhitelistService.AutoRegisterIfAdmin();
                    TIAOpennessStudio.AppLogger.Log("[Init] Whitelist auto-register checked.");
                }
                catch (Exception ex)
                {
                    TIAOpennessStudio.AppLogger.Log($"[Warn] Whitelist auto-register failed: {ex.Message}");
                }

                // Initialize STA executor for TIA Openness
                Console.WriteLine("[Init] Starting STA executor...");
                using var staExecutor = new TiaStaExecutor();

                // Initialize runtime (services composition)
                Console.WriteLine("[Init] Initializing knowledge cache runtime...");
                Console.WriteLine($"[Init] Cache path: {GetCachePath()}");
                using var runtime = new KnowledgeCacheRuntime(staExecutor);

                // Start HTTP host
                Console.WriteLine("[Init] Starting HTTP host...");
                using var host = new WorkerHost(runtime, port, authToken);
                host.Start();

                // Start passive external-change scanner (yields to all real STA work)
                runtime.StartExternalChangeScanner();

                Console.WriteLine();
                Console.WriteLine($"Worker is running on {host.BaseUrl}");
                Console.WriteLine($"Auth token: {host.AuthToken}");
                Console.WriteLine($"Cache path: {runtime.CacheBasePath}");
                Console.WriteLine();
                Console.WriteLine("Press Ctrl+C or close this window to stop.");

                // Wait for shutdown
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("\n[Shutdown] Received Ctrl+C, stopping...");
                    ShutdownSignal.Set();
                };

                ShutdownSignal.Wait();
                Console.WriteLine("[Shutdown] Worker stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fatal] {ex}");
                // ExitCode alone does not terminate the process: the STA executor thread keeps
                // it alive, so a failed start lingers as an orphan holding the SQLite index.
                // Later starts then fail with SQLITE_IOERR / CANTOPEN on the same database.
                Environment.Exit(1);
            }

            // Normal shutdown: the using blocks above already disposed the host/runtime/executor,
            // but a lingering STA thread must not keep the process alive either.
            Environment.Exit(0);
        }

        private static int ParsePort(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out var port) && port > 0 && port < 65536)
                        return port;
                }
                if (args[i].StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                {
                    var val = args[i].Substring("--port=".Length);
                    if (int.TryParse(val, out var port) && port > 0 && port < 65536)
                        return port;
                }
            }
            return 19530; // default port
        }

        private static string ParseAuthToken(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--token", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    return args[i + 1];
                if (args[i].StartsWith("--token=", StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring("--token=".Length);
            }
            return null; // auto-generate
        }

        private static string GetCachePath()
        {
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TIAOpennessStudio",
                "KnowledgeCache");
        }
    }
}
