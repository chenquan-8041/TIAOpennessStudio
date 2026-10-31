using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>Resolves the installed TIA Openness PublicAPI paths from the Windows registry.</summary>
    public sealed class TiaApiPathResolver : ITiaApiPathResolver
    {
        private const string BundlesKey = @"SOFTWARE\Siemens\Automation\_InstalledSW\Global\Bundles\";

        private sealed class TiaVersionSpec
        {
            public string Version;
            public string PublicApiSubFolder;
            public string LegacyFallbackPath;
            public string OpennessReg32;
            public string OpennessReg64;
        }

        private static readonly TiaVersionSpec[] VersionSpecs =
        {
            new TiaVersionSpec { Version = "21.0", PublicApiSubFolder = "V21", LegacyFallbackPath = @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21", OpennessReg32 = @"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP21\TIA_Opns", OpennessReg64 = @"SOFTWARE\Wow6432Node\Siemens\Automation\_InstalledSW\TIAP21\TIA_Opns" },
            new TiaVersionSpec { Version = "20.0", PublicApiSubFolder = "V20", LegacyFallbackPath = @"C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20", OpennessReg32 = @"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP20\TIA_Opns", OpennessReg64 = @"SOFTWARE\Wow6432Node\Siemens\Automation\_InstalledSW\TIAP20\TIA_Opns" },
            new TiaVersionSpec { Version = "19.0", PublicApiSubFolder = "V19", LegacyFallbackPath = @"C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19", OpennessReg32 = @"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP19\TIA_Opns", OpennessReg64 = @"SOFTWARE\Wow6432Node\Siemens\Automation\_InstalledSW\TIAP19\TIA_Opns" },
            new TiaVersionSpec
            {
                Version = "18.0",
                PublicApiSubFolder = "V18",
                LegacyFallbackPath = @"C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18",
                OpennessReg32 = @"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP18\TIA_Opns",
                OpennessReg64 = @"SOFTWARE\Wow6432Node\Siemens\Automation\_InstalledSW\TIAP18\TIA_Opns"
            },
            new TiaVersionSpec
            {
                Version = "17.0",
                PublicApiSubFolder = "V17",
                LegacyFallbackPath = @"C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17",
                OpennessReg32 = @"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP17\TIA_Opns",
                OpennessReg64 = @"SOFTWARE\Wow6432Node\Siemens\Automation\_InstalledSW\TIAP17\TIA_Opns"
            },
            new TiaVersionSpec
            {
                Version = "16.0",
                PublicApiSubFolder = "V16",
                LegacyFallbackPath = @"C:\Program Files\Siemens\Automation\Portal V16\PublicAPI\V16",
                OpennessReg32 = @"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP16\TIA_Opns",
                OpennessReg64 = @"SOFTWARE\Wow6432Node\Siemens\Automation\_InstalledSW\TIAP16\TIA_Opns"
            },
            new TiaVersionSpec
            {
                Version = "15.1",
                PublicApiSubFolder = "V15.1",
                LegacyFallbackPath = @"C:\Program Files\Siemens\Automation\Portal V15_1\PublicAPI\V15.1",
                OpennessReg32 = @"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP15_1\TIA_Opns",
                OpennessReg64 = @"SOFTWARE\Wow6432Node\Siemens\Automation\_InstalledSW\TIAP15_1\TIA_Opns"
            },
            new TiaVersionSpec
            {
                Version = "15.0",
                PublicApiSubFolder = "V15",
                LegacyFallbackPath = @"C:\Program Files\Siemens\Automation\Portal V15\PublicAPI\V15",
                OpennessReg32 = @"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP15\TIA_Opns",
                OpennessReg64 = @"SOFTWARE\Wow6432Node\Siemens\Automation\_InstalledSW\TIAP15\TIA_Opns"
            },
            new TiaVersionSpec
            {
                Version = "14.0",
                PublicApiSubFolder = "V14",
                LegacyFallbackPath = @"C:\Program Files\Siemens\Automation\Portal V14\PublicAPI\V14",
                OpennessReg32 = @"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP14\TIA_Opns",
                OpennessReg64 = @"SOFTWARE\Wow6432Node\Siemens\Automation\_InstalledSW\TIAP14\TIA_Opns"
            }
        };

        public TiaApiResolution Resolve(string appBaseDirectory)
        {
            var fallbackDir = string.IsNullOrWhiteSpace(appBaseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : appBaseDirectory;

            var bundleNames = GetBundleNames();
            foreach (var spec in VersionSpecs)
            {
                if (!IsVersionInstalled(bundleNames, spec))
                    continue;

                var apiPath = ResolveApiPathFromRegistry(spec) ?? ResolveApiPathFromLegacy(spec);
                if (string.IsNullOrWhiteSpace(apiPath))
                    continue;

                return new TiaApiResolution
                {
                    Version = spec.Version,
                    ApiPath = apiPath,
                    FromRegistry = ResolveApiPathFromRegistry(spec) != null
                };
            }

            return new TiaApiResolution
            {
                Version = string.Empty,
                ApiPath = fallbackDir,
                FromRegistry = false
            };
        }

        private static bool IsVersionInstalled(IReadOnlyList<string> bundleNames, TiaVersionSpec spec)
        {
            if (bundleNames == null || bundleNames.Count == 0) return false;

            // Real bundle names embed the version as "V15" or "V15.1" (e.g. "TIA Portal STEP 7
            // Professional V15.1 - WinCC Advanced V15.1"), not a "TIA Portal V*" prefix.
            // Majors are rendered without the trailing ".0" ("V15", not "V15.0").
            string token = "V" + (spec.Version.EndsWith(".0")
                ? spec.Version.Substring(0, spec.Version.Length - 2)
                : spec.Version);

            foreach (var name in bundleNames)
            {
                int idx = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                // A bare major token ("V15") must not match a patch bundle ("V15.1").
                if (spec.Version.EndsWith(".0"))
                {
                    int after = idx + token.Length;
                    if (after < name.Length && name[after] == '.')
                        continue;
                }
                return true;
            }
            return false;
        }

        private static IReadOnlyList<string> GetBundleNames()
        {
            var key = OpenRegistrySubKey(BundlesKey);
            if (key == null) return Array.Empty<string>();
            using (key)
            {
                return key.GetSubKeyNames().OrderBy(x => x).ToArray();
            }
        }

        private static RegistryKey OpenRegistrySubKey(string keyName)
        {
            var views = new[] { RegistryView.Registry64, RegistryView.Default, RegistryView.Registry32 };
            foreach (var view in views)
            {
                try
                {
                    var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    var key = baseKey.OpenSubKey(keyName);
                    baseKey.Dispose();
                    if (key != null) return key;
                }
                catch
                {
                    // Ignore and try next view
                }
            }
            return null;
        }

        private static string ResolveApiPathFromRegistry(TiaVersionSpec spec)
        {
            var opennessRoot = GetRegistryPath(spec.OpennessReg32, spec.OpennessReg64);
            if (string.IsNullOrWhiteSpace(opennessRoot)) return null;

            var candidates = new[]
            {
                Path.Combine(opennessRoot, "PublicAPI", spec.PublicApiSubFolder),
                Path.Combine(opennessRoot, "Bin", "PublicAPI", spec.PublicApiSubFolder),
                Path.Combine(opennessRoot, "Bin", "PublicAPI")
            };

            return candidates.FirstOrDefault(HasSiemensEngineeringDll);
        }

        private static string ResolveApiPathFromLegacy(TiaVersionSpec spec)
        {
            return HasSiemensEngineeringDll(spec.LegacyFallbackPath) ? spec.LegacyFallbackPath : null;
        }

        private static string GetRegistryPath(string key32, string key64)
        {
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Default };
            foreach (var view in views)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = baseKey.OpenSubKey(key64) ?? baseKey.OpenSubKey(key32))
                    {
                        var path = key?.GetValue("Path") as string;
                        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                        {
                            return path.Trim();
                        }
                    }
                }
                catch
                {
                    // Ignore and continue
                }
            }
            return null;
        }

        private static bool HasSiemensEngineeringDll(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
            var dllPath = Path.Combine(directory, "Siemens.Engineering.dll");
            return File.Exists(dllPath);
        }
    }
}
