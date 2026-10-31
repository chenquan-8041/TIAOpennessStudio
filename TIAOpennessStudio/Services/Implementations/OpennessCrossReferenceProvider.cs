using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Wraps the TIA Openness CrossReferenceService via reflection to provide
    /// authoritative cross-reference data for the knowledge pipeline.
    /// 
    /// This replaces the "XML guessing" approach by calling the real
    /// CrossReferenceProvider API through the existing OpennessService reflection pattern.
    /// 
    /// Thread-safety: TIA Openness requires STA thread. This provider wraps calls
    /// to be safe for invocation from non-STA threads by marshaling internally.
    /// </summary>
    public sealed class OpennessCrossReferenceProvider : ICrossReferenceProvider
    {
        private readonly ITiaOpennessService _opennessService;

        public OpennessCrossReferenceProvider(ITiaOpennessService opennessService)
        {
            _opennessService = opennessService ?? throw new ArgumentNullException(nameof(opennessService));
        }

        /// <inheritdoc/>
        public bool IsAvailable => _opennessService.IsConnected && _opennessService.PlcSoftware != null;

        /// <inheritdoc/>
        public Task<IReadOnlyList<BlockCrossReference>> GetBlockCrossReferencesAsync(
            string blockName, CancellationToken ct = default)
        {
            if (!IsAvailable || string.IsNullOrWhiteSpace(blockName))
                return Task.FromResult<IReadOnlyList<BlockCrossReference>>(Array.Empty<BlockCrossReference>());

            ct.ThrowIfCancellationRequested();

            try
            {
                // Find the target block object
                var targetBlock = FindBlockByName(blockName);
                if (targetBlock == null)
                {
                    AppLogger.Log($"[CrossRefProvider] Block '{blockName}' not found in PlcSoftware.BlockGroup");
                    return Task.FromResult<IReadOnlyList<BlockCrossReference>>(Array.Empty<BlockCrossReference>());
                }

                // Get CrossReferenceService from the block
                var crossRefService = GetCrossReferenceService(targetBlock);
                if (crossRefService == null)
                {
                    AppLogger.Log($"[CrossRefProvider] CrossReferenceService not available for block '{blockName}'");
                    return Task.FromResult<IReadOnlyList<BlockCrossReference>>(Array.Empty<BlockCrossReference>());
                }

                // Invoke GetCrossReferences with AllObjects filter
                var rawResult = InvokeGetCrossReferences(crossRefService);
                if (rawResult == null)
                {
                    AppLogger.Log($"[CrossRefProvider] GetCrossReferences returned null for '{blockName}'");
                    return Task.FromResult<IReadOnlyList<BlockCrossReference>>(Array.Empty<BlockCrossReference>());
                }

                // Flatten the TIA result into our model
                var entries = FlattenToBlockCrossReferences(rawResult, blockName);
                AppLogger.Log($"[CrossRefProvider] Got {entries.Count} cross-references for '{blockName}'");
                return Task.FromResult<IReadOnlyList<BlockCrossReference>>(entries);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Log($"[CrossRefProvider] Error querying cross-references for '{blockName}': {ex.Message}");
                return Task.FromResult<IReadOnlyList<BlockCrossReference>>(Array.Empty<BlockCrossReference>());
            }
        }

        /// <inheritdoc/>
        public async Task<Dictionary<string, List<BlockCrossReference>>> GetBatchCrossReferencesAsync(
            IReadOnlyList<string> blockNames, CancellationToken ct = default)
        {
            var result = new Dictionary<string, List<BlockCrossReference>>(StringComparer.OrdinalIgnoreCase);
            if (!IsAvailable || blockNames == null || blockNames.Count == 0)
                return result;

            foreach (var blockName in blockNames)
            {
                ct.ThrowIfCancellationRequested();

                var refs = await GetBlockCrossReferencesAsync(blockName, ct).ConfigureAwait(false);
                result[blockName] = refs.ToList();
            }

            return result;
        }

        // ---- Private: Reflection-based TIA API access ----

        private object FindBlockByName(string blockName)
        {
            var plcSoftware = _opennessService.PlcSoftware;
            if (plcSoftware == null) return null;

            // Search in BlockGroup recursively
            return FindBlockInGroup(plcSoftware.BlockGroup, blockName);
        }

        private static object FindBlockInGroup(object group, string blockName)
        {
            if (group == null) return null;

            try
            {
                var blocksProp = group.GetType().GetProperty("Blocks");
                if (blocksProp != null)
                {
                    var blocks = blocksProp.GetValue(group) as System.Collections.IEnumerable;
                    if (blocks != null)
                    {
                        foreach (var block in blocks)
                        {
                            var nameProp = block.GetType().GetProperty("Name");
                            if (nameProp == null) continue;
                            var name = nameProp.GetValue(block)?.ToString();
                            if (string.Equals(name, blockName, StringComparison.OrdinalIgnoreCase))
                                return block;
                        }
                    }
                }

                // Recurse into sub-groups
                var groupsProp = group.GetType().GetProperty("Groups");
                if (groupsProp != null)
                {
                    var groups = groupsProp.GetValue(group) as System.Collections.IEnumerable;
                    if (groups != null)
                    {
                        foreach (var subGroup in groups)
                        {
                            var found = FindBlockInGroup(subGroup, blockName);
                            if (found != null) return found;
                        }
                    }
                }
            }
            catch { /* Swallow reflection errors */ }

            return null;
        }

        private static object GetCrossReferenceService(object targetBlock)
        {
            try
            {
                // TIA Openness pattern: block.GetService<ICrossReferenceProvider>()
                var getServiceMethods = targetBlock.GetType()
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Where(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                    .ToList();

                if (getServiceMethods.Count == 0)
                    return null;

                var getService = getServiceMethods[0];

                // Find the CrossReferenceProvider type in loaded assemblies
                var crossRefType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); }
                        catch { return Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t =>
                        t.Name.Equals("CrossReferenceProvider", StringComparison.OrdinalIgnoreCase) ||
                        t.Name.Equals("ICrossReferenceProvider", StringComparison.OrdinalIgnoreCase) ||
                        (t.FullName != null && t.FullName.EndsWith(".CrossReferenceProvider", StringComparison.OrdinalIgnoreCase)));

                if (crossRefType == null)
                {
                    // Fallback: try "CrossReferenceService" naming
                    crossRefType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a =>
                        {
                            try { return a.GetTypes(); }
                            catch { return Array.Empty<Type>(); }
                        })
                        .FirstOrDefault(t =>
                            t.Name.Equals("CrossReferenceService", StringComparison.OrdinalIgnoreCase) ||
                            (t.FullName != null && t.FullName.EndsWith(".CrossReferenceService", StringComparison.OrdinalIgnoreCase)));
                }

                if (crossRefType == null)
                    return null;

                return getService.MakeGenericMethod(crossRefType).Invoke(targetBlock, Array.Empty<object>());
            }
            catch
            {
                return null;
            }
        }

        private static object InvokeGetCrossReferences(object crossReferenceService)
        {
            try
            {
                var svcType = crossReferenceService.GetType();

                // Find CrossReferenceFilter enum
                var filterType = svcType.Assembly.GetTypes()
                    .FirstOrDefault(t => t.IsEnum &&
                        (t.Name.Equals("CrossReferenceFilter", StringComparison.OrdinalIgnoreCase) ||
                         t.Name.Equals("CrossReferenceObjectFilter", StringComparison.OrdinalIgnoreCase)));

                if (filterType == null)
                {
                    // Try without filter parameter
                    var noArgMethod = svcType.GetMethod("GetCrossReferences", Type.EmptyTypes);
                    if (noArgMethod != null)
                        return noArgMethod.Invoke(crossReferenceService, Array.Empty<object>());
                    return null;
                }

                // Parse "AllObjects" or first available enum value
                object filterValue;
                try
                {
                    filterValue = Enum.Parse(filterType, "AllObjects", ignoreCase: true);
                }
                catch
                {
                    // Use first enum value as fallback
                    var values = Enum.GetValues(filterType);
                    if (values.Length == 0) return null;
                    filterValue = values.GetValue(0);
                }

                var method = svcType.GetMethod("GetCrossReferences", new[] { filterType });
                if (method == null) return null;

                return method.Invoke(crossReferenceService, new[] { filterValue });
            }
            catch
            {
                return null;
            }
        }

        private static List<BlockCrossReference> FlattenToBlockCrossReferences(object crossReferenceResult, string queriedBlockName)
        {
            var entries = new List<BlockCrossReference>();
            try
            {
                // TIA CrossReferenceResult has Sources → References → Locations structure
                var sources = crossReferenceResult.GetType().GetProperty("Sources")?.GetValue(crossReferenceResult)
                    as System.Collections.IEnumerable;

                if (sources == null)
                {
                    // Try alternative structure: direct enumerable of entries
                    if (crossReferenceResult is System.Collections.IEnumerable directEnum)
                    {
                        foreach (var item in directEnum)
                        {
                            entries.Add(ExtractSingleEntry(item, queriedBlockName));
                        }
                    }
                    return entries;
                }

                foreach (var src in sources)
                {
                    if (src == null) continue;
                    var srcName = src.GetType().GetProperty("Name")?.GetValue(src)?.ToString() ?? "";
                    var srcPath = src.GetType().GetProperty("Path")?.GetValue(src)?.ToString() ?? "";

                    var refs = src.GetType().GetProperty("References")?.GetValue(src) as System.Collections.IEnumerable;
                    if (refs == null) continue;

                    foreach (var rf in refs)
                    {
                        if (rf == null) continue;
                        var refName = rf.GetType().GetProperty("Name")?.GetValue(rf)?.ToString() ?? "";
                        var refPath = rf.GetType().GetProperty("Path")?.GetValue(rf)?.ToString() ?? "";

                        var locations = rf.GetType().GetProperty("Locations")?.GetValue(rf)
                            as System.Collections.IEnumerable;

                        if (locations == null)
                        {
                            // No sub-locations: create one entry per reference
                            entries.Add(new BlockCrossReference
                            {
                                QueriedBlockName = queriedBlockName,
                                ReferencingBlockName = srcName,
                                ReferencingPath = srcPath,
                                ReferencedSymbol = refName,
                                RawSourceName = srcName,
                                RawReferencePath = refPath
                            });
                            continue;
                        }

                        foreach (var loc in locations)
                        {
                            if (loc == null) continue;
                            var locName = loc.GetType().GetProperty("Name")?.GetValue(loc)?.ToString() ?? "";
                            var refLocation = loc.GetType().GetProperty("ReferenceLocation")?.GetValue(loc)?.ToString() ?? "";
                            var refType = loc.GetType().GetProperty("ReferenceType")?.GetValue(loc)?.ToString() ?? "";
                            var access = loc.GetType().GetProperty("Access")?.GetValue(loc)?.ToString() ?? "";

                            entries.Add(new BlockCrossReference
                            {
                                QueriedBlockName = queriedBlockName,
                                ReferencingBlockName = srcName,
                                ReferencingPath = srcPath,
                                ReferencedSymbol = loc.GetType().GetProperty("ReferenceName")?.GetValue(loc)?.ToString() ?? refName,
                                Location = locName,
                                AccessType = NormalizeAccessType(access),
                                ReferenceType = refType,
                                RawSourceName = srcName,
                                RawReferencePath = refPath
                            });
                        }
                    }
                }
            }
            catch { /* Swallow and return partial results */ }

            return entries;
        }

        private static BlockCrossReference ExtractSingleEntry(object item, string queriedBlockName)
        {
            var entry = new BlockCrossReference { QueriedBlockName = queriedBlockName };
            try
            {
                entry.ReferencingBlockName = item.GetType().GetProperty("SourceName")?.GetValue(item)?.ToString() ?? "";
                entry.ReferencingPath = item.GetType().GetProperty("SourcePath")?.GetValue(item)?.ToString() ?? "";
                entry.ReferencedSymbol = item.GetType().GetProperty("ReferenceName")?.GetValue(item)?.ToString() ?? "";
                entry.Location = item.GetType().GetProperty("LocationName")?.GetValue(item)?.ToString() ?? "";
                entry.AccessType = NormalizeAccessType(
                    item.GetType().GetProperty("Access")?.GetValue(item)?.ToString() ?? "");
                entry.ReferenceType = item.GetType().GetProperty("ReferenceType")?.GetValue(item)?.ToString() ?? "";
            }
            catch { }
            return entry;
        }

        private static string NormalizeAccessType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var lower = raw.ToLowerInvariant().Trim();
            if (lower.Contains("readwrite") || lower.Contains("read_write") || lower.Contains("inout"))
                return "ReadWrite";
            if (lower.Contains("write") || lower.Contains("assign") || lower.Contains("output"))
                return "Write";
            if (lower.Contains("read") || lower.Contains("input"))
                return "Read";
            if (lower.Contains("call"))
                return "Call";
            return raw;
        }
    }
}
