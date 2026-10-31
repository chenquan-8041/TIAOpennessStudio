using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Openness capability probe.
    ///
    /// Uses .NET reflection to scan the Siemens.Engineering* assemblies loaded in the
    /// current process and dynamically determine which namespaces / types / methods the
    /// current TIA version actually exposes, for the AI agent to use as context (more
    /// accurate than hard-coded version thresholds).
    ///
    /// Thread-safe and non-blocking for the UI:
    ///   - It only reads static assembly metadata, unrelated to the TIA project
    ///     instance (COM/STA), so it can run on any thread;
    ///   - The first build runs on the thread pool via Task.Run, and the result is
    ///     cached in memory plus on disk;
    ///   - The disk cache is keyed by the Siemens.Engineering.dll file version, so it
    ///     hits directly after a restart.
    /// </summary>
    public static class OpennessCapabilityProbe
    {
        private static readonly object _gate = new object();
        private static OpennessCapabilityManifest _cached;
        private static Task<OpennessCapabilityManifest> _buildingTask;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        private static string CacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TIAOpennessStudio",
            "CapabilityCache");

        /// <summary>
        /// Gets the capability manifest (async; never blocks the calling thread).
        /// Returns immediately on a memory-cache hit; otherwise builds in the background
        /// on the thread pool, deduplicating concurrent builds.
        /// </summary>
        public static Task<OpennessCapabilityManifest> GetCapabilitiesAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_cached != null)
                    return Task.FromResult(_cached);

                if (_buildingTask != null)
                    return _buildingTask;

                _buildingTask = Task.Run(() =>
                {
                    var manifest = BuildManifest();
                    lock (_gate)
                    {
                        _cached = manifest;
                        _buildingTask = null;
                    }
                    return manifest;
                }, ct);

                return _buildingTask;
            }
        }

        /// <summary>Clears the cache (for version switch / re-probe). Does not remove the disk cache.</summary>
        public static void InvalidateMemoryCache()
        {
            lock (_gate)
            {
                _cached = null;
            }
        }

        /// <summary>
        /// Determines whether a capability is available: prefers the real probe result
        /// (whether the type exists). Returns null when the probe is not ready or the
        /// type is not found (the caller decides whether to fall back to a version
        /// threshold).
        /// </summary>
        public static bool? IsTypeAvailable(string typeFullNameContains)
        {
            OpennessCapabilityManifest m;
            lock (_gate) { m = _cached; }
            if (m == null || m.Namespaces == null) return null;
            if (string.IsNullOrWhiteSpace(typeFullNameContains)) return null;

            foreach (var ns in m.Namespaces)
            {
                if (ns.Types == null) continue;
                foreach (var t in ns.Types)
                {
                    if (t.FullName != null &&
                        t.FullName.IndexOf(typeFullNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            return false;
        }

        // ---- Build ----

        private static OpennessCapabilityManifest BuildManifest()
        {
            // 1) Try the disk cache first.
            var (dllVersion, dllPath) = GetEngineeringDllVersion();
            var cacheFile = BuildCacheFilePath(dllVersion);

            if (!string.IsNullOrEmpty(cacheFile) && File.Exists(cacheFile))
            {
                try
                {
                    var json = File.ReadAllText(cacheFile);
                    var fromDisk = JsonSerializer.Deserialize<OpennessCapabilityManifest>(json, JsonOpts);
                    if (fromDisk != null)
                    {
                        fromDisk.FromCache = true;
                        return fromDisk;
                    }
                }
                catch { /* a corrupted cache triggers a rescan */ }
            }

            // 2) Reflection scan.
            var manifest = ScanAssemblies(dllVersion, dllPath);

            // 3) Write the disk cache (best-effort).
            if (!string.IsNullOrEmpty(cacheFile))
            {
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    File.WriteAllText(cacheFile, JsonSerializer.Serialize(manifest, JsonOpts));
                }
                catch { /* an unwritable disk is non-fatal */ }
            }

            return manifest;
        }

        private static OpennessCapabilityManifest ScanAssemblies(string dllVersion, string dllPath)
        {
            var manifest = new OpennessCapabilityManifest
            {
                EngineeringDllVersion = dllVersion ?? "unknown",
                EngineeringDllPath = dllPath ?? "",
                ScannedAtUtc = DateTime.UtcNow.ToString("o"),
                FromCache = false
            };

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => (a.GetName().Name ?? "").StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.GetName().Name)
                .ToArray();

            manifest.Assemblies = assemblies.Select(a => a.GetName().Name).ToList();

            // Aggregate by namespace.
            var nsMap = new Dictionary<string, OpennessNamespaceInfo>(StringComparer.Ordinal);

            foreach (var asm in assemblies)
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null || !t.IsPublic || t.FullName == null) continue;
                    if (!t.FullName.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) continue;

                    var nsName = t.Namespace ?? "(global)";
                    if (!nsMap.TryGetValue(nsName, out var nsInfo))
                    {
                        nsInfo = new OpennessNamespaceInfo { Namespace = nsName, Types = new List<OpennessTypeInfo>() };
                        nsMap[nsName] = nsInfo;
                    }

                    var kind = t.IsEnum ? "enum"
                        : t.IsInterface ? "interface"
                        : t.IsValueType ? "struct"
                        : t.IsClass ? "class"
                        : "type";

                    var typeInfo = new OpennessTypeInfo
                    {
                        Name = t.Name,
                        FullName = t.FullName,
                        Kind = kind,
                        BaseType = (t.BaseType != null && t.BaseType != typeof(object)) ? t.BaseType.FullName : null
                    };

                    if (t.IsEnum)
                    {
                        try { typeInfo.EnumValues = Enum.GetNames(t).Take(200).ToList(); }
                        catch { }
                    }
                    else
                    {
                        // Public instance/static method names (excluding property accessors and
                        // methods inherited from object).
                        try
                        {
                            typeInfo.Methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                .Where(mi => !mi.IsSpecialName)
                                .Select(mi => mi.Name)
                                .Distinct()
                                .OrderBy(n => n)
                                .Take(200)
                                .ToList();
                        }
                        catch { }

                        try
                        {
                            typeInfo.Properties = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                .Select(pi => pi.Name)
                                .Distinct()
                                .OrderBy(n => n)
                                .Take(200)
                                .ToList();
                        }
                        catch { }
                    }

                    nsInfo.Types.Add(typeInfo);
                }
            }

            manifest.Namespaces = nsMap.Values
                .OrderBy(n => n.Namespace, StringComparer.Ordinal)
                .ToList();

            manifest.TotalTypeCount = manifest.Namespaces.Sum(n => n.Types?.Count ?? 0);

            return manifest;
        }

        private static (string version, string path) GetEngineeringDllVersion()
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Siemens.Engineering", StringComparison.OrdinalIgnoreCase));

                if (asm == null) return (null, null);

                var path = "";
                try { path = asm.Location; } catch { }

                var ver = asm.GetName().Version?.ToString();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    try
                    {
                        var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                        if (!string.IsNullOrWhiteSpace(fvi.FileVersion)) ver = fvi.FileVersion;
                    }
                    catch { }
                }

                return (ver, path);
            }
            catch
            {
                return (null, null);
            }
        }

        private static string BuildCacheFilePath(string dllVersion)
        {
            if (string.IsNullOrWhiteSpace(dllVersion)) return null;
            var safe = dllVersion;
            foreach (var c in Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');
            return Path.Combine(CacheDir, $"openness_caps_{safe}.json");
        }
    }
}
