using System;
using System.IO;
using System.Reflection;

using TIAOpennessStudio.Services.Implementations;

namespace TIAOpennessStudio
{
    static class Program
    {
        /// <summary>
        /// Der Haupteinstiegspunkt für die Anwendung.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            TrySetWorkspaceCurrentDirectory();

            // Offline contract probe (no WPF launch, no TIA): freezes/compares the block
            // read contract. WinExe has no console, so the verdict is written to
            // docs/contract/contract-probe-report.txt and returned as the process exit code.
            if (HasArgument(args, "--contract-probe"))
            {
                Environment.ExitCode = TIAOpennessStudio.Services.Blocks.BlockContractProbe
                    .Run(HasArgument(args, "--write-baseline"));
                return;
            }

            try
            {
                var resolver = new TiaApiPathResolver();
                var resolved = resolver.Resolve(AppDomain.CurrentDomain.BaseDirectory);
                Version = resolved.Version ?? string.Empty;
                Api = resolved.ApiPath ?? string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error resolving TIA API path: " + ex.Message);
            }

            if (string.IsNullOrEmpty(Api))
            {
                Api = AppDomain.CurrentDomain.BaseDirectory;
            }

            // Single version mirror for version-gated code (the V21 Portal bridge reads this).
            TiaVersionAdapter.CurrentMajorVersion = ToMajorVersion(Version);
            TiaVersionAdapter.CurrentVersion = Version;

            AppDomain.CurrentDomain.AssemblyResolve += MyResolver;

            // WPF Application Launch
            App app = new App();
            app.InitializeComponent();
            app.Run();
        }

        public static string Version = string.Empty;
        public static string Api = string.Empty;

        private static void TrySetWorkspaceCurrentDirectory()
        {
            var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "TIAOpennessStudio.sln")))
                {
                    Directory.SetCurrentDirectory(current.FullName);
                    return;
                }

                current = current.Parent;
            }
        }

        private static bool HasArgument(string[] args, string expected)
        {
            if (args == null) return false;
            foreach (var arg in args)
            {
                if (string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static int ToMajorVersion(string version)
        {
            if (!string.IsNullOrWhiteSpace(version) && int.TryParse(version.Split('.')[0], out var m))
                return m;
            return 0;
        }

        private static Assembly MyResolver(object sender, ResolveEventArgs args)
        {
            int index = args.Name.IndexOf(',');
            if (index == -1)
            {
                return null;
            }

            string name = args.Name.Substring(0, index) + ".dll";
            //System.Diagnostics.Debug.Print("Assembly: " + name);

            // for Siemens we load the correct dll (version)
            if (name == "Siemens.Engineering.dll")
            {
                string path = Path.Combine(Api, name);

                // see what dll we can use (incomplete)
                if (File.Exists(path))
                {
                    string fullPath = Path.GetFullPath(path);
                    if (File.Exists(fullPath))
                    {
                        return Assembly.LoadFrom(fullPath);
                    }
                }
            }
            return null;
        }
    }
}
