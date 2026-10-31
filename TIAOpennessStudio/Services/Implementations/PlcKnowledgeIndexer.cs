using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Types;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Models.Blocks;
using TIAOpennessStudio.Models.NetworkKnowledge;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Blocks;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// v3 project knowledge indexer. Rebuilds the layered store:
    ///   container tree -> block rows -> LAD/SCL networks -> members -> symbol refs -> call edges.
    ///
    /// Single-writer: all writes go through <see cref="PlcKnowledgeStoreWriter"/>.
    /// Incremental: unchanged blocks (by version hash) are skipped; deleted blocks pruned.
    /// Every discovered block is written unconditionally (completeness anchor), even if
    /// network extraction fails (parse_status records the failure).
    /// </summary>
    public sealed class PlcKnowledgeIndexer
    {
        /// <summary>
        /// Version of the RAG payload contract: what this indexer stores per block and per
        /// network. It participates in every block's version hash, so bumping it re-indexes
        /// the whole project instead of silently keeping rows written under an older policy.
        ///
        /// T12: block snapshot + revision + interface JSON for DB/UDT; SCL stores source
        /// text only (no canonical JSON payload).
        /// </summary>
        public const string IndexContractVersion = "T12";

        private readonly ITiaOpennessService _tiaService;
        private readonly IBlockVersionService _blockVersionService;
        private readonly PlcKnowledgeStoreWriter _writer;
        private readonly PlcNetworkFactExtractor _networkExtractor;
        private readonly PlcKnowledgeCanonicalizer _canonicalizer = new PlcKnowledgeCanonicalizer();

        public PlcKnowledgeIndexer(
            ITiaOpennessService tiaService,
            IBlockVersionService blockVersionService,
            PlcKnowledgeStoreWriter writer,
            PlcNetworkFactExtractor networkExtractor = null)
        {
            _tiaService = tiaService;
            _blockVersionService = blockVersionService;
            _writer = writer;
            _networkExtractor = networkExtractor ?? new PlcNetworkFactExtractor();
        }

        /// <summary>
        /// Marshals the full rebuild onto the dedicated TIA Openness STA thread. All
        /// Openness COM access (PlcSoftware/BlockGroup/PlcBlock/...) MUST run there, else
        /// Openness throws "Cross-thread operation is not valid in Openness within STA".
        /// SQLite writes and XML parsing are plain-data work and run fine on the same thread.
        /// </summary>
        public Task<PlcProjectIndexBuildResult> RebuildAsync(
            bool force = false, Action<string> onProgress = null, CancellationToken ct = default)
        {
            if (TiaStaHost.Instance.CheckAccess())
                return RebuildCoreAsync(force, onProgress, ct);
            return TiaStaHost.Instance.RunAsync(() => RebuildCoreAsync(force, onProgress, ct));
        }

        private async Task<PlcProjectIndexBuildResult> RebuildCoreAsync(
            bool force, Action<string> onProgress, CancellationToken ct)
        {
            if (_tiaService?.PlcSoftware == null)
                throw new InvalidOperationException("No PLC software connected.");

            var projectKey = ResolveProjectKey();
            var projectPath = SafeProjectPath();
            var result = new PlcProjectIndexBuildResult { ProjectKey = projectKey };

            var hasGit = _blockVersionService.TryGetGitSnapshot(out var gitBranch, out var gitCommit);
            gitBranch = hasGit ? gitBranch : "";
            gitCommit = hasGit ? gitCommit : "";

            var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _writer.UpdateProjectState(projectKey, "indexing", 0, gitBranch, gitCommit, "rebuild started");
            AppLogger.Log($"[KnowledgeIndexer] Rebuild started. projectKey={projectKey}, force={force}, git={gitBranch}/{gitCommit}");

            // 1. Container tree + blocks
            onProgress?.Invoke("Scanning PLC blocks...");
            AppLogger.Log("[KnowledgeIndexer] Scanning PLC blocks...");
            RegisterContainer(projectKey, _tiaService.PlcSoftware.BlockGroup, "BlockGroup", null, "", 0);
            foreach (var block in EnumerateBlocks(_tiaService.PlcSoftware.BlockGroup))
            {
                ct.ThrowIfCancellationRequested();
                result.BlocksScanned++;
                var blockType = block.GetType().Name;
                var cacheKey = BuildBlockCacheKey(block, blockType);
                discovered.Add(cacheKey);
                await IndexBlockAsync(projectKey, projectPath, block, blockType, cacheKey, gitBranch, gitCommit, force, result, onProgress)
                    .ConfigureAwait(false);
            }

            AppLogger.Log($"[KnowledgeIndexer] Blocks scanned={result.BlocksScanned}, upserted={result.Upserted}, skipped={result.SkippedUnchanged}, unsupported={result.SkippedUnsupported}, failed={result.Failed}");

            // 2. Types (UDT)
            onProgress?.Invoke("Scanning PLC types...");
            AppLogger.Log("[KnowledgeIndexer] Scanning PLC types (UDT)...");
            RegisterContainer(projectKey, _tiaService.PlcSoftware.TypeGroup, "TypeGroup", null, "", 0);
            foreach (var type in EnumerateTypes(_tiaService.PlcSoftware.TypeGroup))
            {
                ct.ThrowIfCancellationRequested();
                result.TypesScanned++;
                var cacheKey = BuildTypeCacheKey(type);
                discovered.Add(cacheKey);
                await IndexTypeAsync(projectKey, projectPath, type, cacheKey, gitBranch, gitCommit, force, result, onProgress)
                    .ConfigureAwait(false);
            }

            // 3. Stale cleanup
            try
            {
                _writer.PruneMissing(projectKey, discovered);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Prune missing: " + ex.Message);
                AppLogger.LogError("[KnowledgeIndexer] Prune missing failed", ex);
            }

            // 3b. P2b: reconcile the callee_resolved flag of call edges (mark stale edges after rename/delete, never delete)
            try
            {
                var reconciled = _writer.ReconcileCallEdges(projectKey);
                AppLogger.Log($"[KnowledgeIndexer] Call-edge reconcile done. edges={reconciled}");
            }
            catch (Exception ex)
            {
                result.Errors.Add("Reconcile call edges: " + ex.Message);
                AppLogger.LogError("[KnowledgeIndexer] Reconcile call edges failed", ex);
            }

            _writer.UpdateProjectState(projectKey, "ready", result.Upserted + result.SkippedUnchanged, gitBranch, gitCommit, "rebuild completed");
            onProgress?.Invoke($"Index rebuild finished. upserted={result.Upserted}, skipped={result.SkippedUnchanged}, unsupported={result.SkippedUnsupported}, failed={result.Failed}");
            AppLogger.Log($"[KnowledgeIndexer] Rebuild finished. blocks={result.BlocksScanned}, types={result.TypesScanned}, upserted={result.Upserted}, skipped={result.SkippedUnchanged}, unsupported={result.SkippedUnsupported}, failed={result.Failed}, networks(ok/failed)={result.NetworksExtracted}/{result.NetworksFailed}");
            return result;
        }

        /// <summary>
        /// Single-block update entry ("whoever modifies, refreshes"). Re-indexes one
        /// block by name after an edit, without scanning the whole project. Forces the
        /// block through the delete-old-derived + write-new transaction.
        /// </summary>
        public Task<bool> UpdateBlockAsync(string blockName, Action<string> onProgress = null, CancellationToken ct = default)
        {
            if (TiaStaHost.Instance.CheckAccess())
                return UpdateBlockCoreAsync(blockName, onProgress, ct);
            return TiaStaHost.Instance.RunAsync(() => UpdateBlockCoreAsync(blockName, onProgress, ct));
        }

        private async Task<bool> UpdateBlockCoreAsync(string blockName, Action<string> onProgress, CancellationToken ct)
        {
            if (_tiaService?.PlcSoftware == null || string.IsNullOrWhiteSpace(blockName))
                return false;

            var projectKey = ResolveProjectKey();
            var projectPath = SafeProjectPath();

            var block = EnumerateBlocks(_tiaService.PlcSoftware.BlockGroup)
                .FirstOrDefault(b => string.Equals(b.Name, blockName, StringComparison.OrdinalIgnoreCase));
            if (block == null)
                return false;

            var blockType = block.GetType().Name;
            var cacheKey = BuildBlockCacheKey(block, blockType);
            var result = new PlcProjectIndexBuildResult { ProjectKey = projectKey };

            // Ensure the container chain for this block exists before writing it.
            RegisterContainerChain(projectKey, block.Parent as PlcBlockGroup, "BlockGroup");

            await IndexBlockAsync(projectKey, projectPath, block, blockType, cacheKey, "", "", force: true, result, onProgress)
                .ConfigureAwait(false);
            return result.Upserted > 0;
        }

        /// <summary>Register only the ancestor chain of one group (for single-block updates).</summary>
        private void RegisterContainerChain(string projectKey, PlcBlockGroup group, string kind)
        {
            var chain = new List<PlcBlockGroup>();
            var current = group;
            while (current is PlcBlockGroup g && !(g is PlcBlockSystemGroup))
            {
                chain.Add(g);
                current = g.Parent as PlcBlockGroup;
            }
            chain.Reverse();

            string parentId = null;
            var parentPath = "";
            int depth = 0;
            foreach (var g in chain)
            {
                var path = string.IsNullOrEmpty(parentPath) ? (g.Name ?? "") : parentPath + "\\" + g.Name;
                var containerId = kind + ":" + path;
                _writer.UpsertContainer(projectKey, containerId, parentId ?? "", g.Name ?? "", kind, path, depth);
                parentId = containerId;
                parentPath = path;
                depth++;
            }
        }

        // ---- per-block indexing ----

        /// <summary>
        /// Whether the current content hash equals what the store last wrote for this
        /// block/type. Prefers the persisted <c>plc_block.version_hash</c> (survives a
        /// worker restart); falls back to the in-memory previous version only when
        /// nothing is persisted yet (first build after a fresh database).
        /// </summary>
        private bool IsUnchanged(string projectKey, string blockId, string currentVersion,
            string inMemoryPrevious, bool inMemoryChanged)
        {
            if (_writer.TryGetBlockVersionHash(projectKey, blockId, out var persisted) &&
                !string.IsNullOrWhiteSpace(persisted))
            {
                return string.Equals(persisted, currentVersion, StringComparison.OrdinalIgnoreCase);
            }

            return !string.IsNullOrWhiteSpace(inMemoryPrevious) && !inMemoryChanged;
        }

        private async Task IndexBlockAsync(
            string projectKey, string projectPath, PlcBlock block, string blockType, string cacheKey,
            string gitBranch, string gitCommit, bool force,
            PlcProjectIndexBuildResult result, Action<string> onProgress)
        {
            try
            {
                onProgress?.Invoke("Indexing block " + block.Name + "...");
                var xml = await _tiaService.ExportBlockXmlAsync(block).ConfigureAwait(false);

                var language = TryGetProgrammingLanguage(block);
                // Include the extractor version and the RAG payload contract in the hash so a
                // parser or storage-policy upgrade re-indexes the block instead of silently
                // keeping the old payload.
                var version = _blockVersionService.UpdateBlockVersion(
                    cacheKey, BuildVersionBasis(xml), "", out var changed, out var previousVersion);
                if (!force && IsUnchanged(projectKey, cacheKey, version, previousVersion, changed))
                {
                    result.SkippedUnchanged++;
                    return;
                }

                var snapshot = TryCreateSnapshot(xml, cacheKey, projectKey);
                var record = BuildBlockRecord(projectKey, projectPath, cacheKey, block.Name, blockType, language, version, xml, snapshot, block);
                record.ParentContainerId = ContainerIdFor(projectKey, block.Parent as PlcBlockGroup, "BlockGroup");
                record.GitBranch = gitBranch;
                record.GitCommit = gitCommit;

                _writer.UpsertBlock(record);
                result.Upserted++;

                foreach (var n in record.LadNetworks.Concat(record.SclNetworks))
                {
                    if (n.Status == ParseStatus.Failed) result.NetworksFailed++;
                    else result.NetworksExtracted++;
                }
            }
            catch (Exception ex)
            {
                if (IsProDiagUnsupported(ex))
                {
                    // ProDiag blocks cannot be exported via Openness (by design). Record the
                    // block as unsupported and log a warning instead of treating it as a failure.
                    result.SkippedUnsupported++;
                    AppLogger.LogWarning($"[KnowledgeIndexer] Block skipped (ProDiag not exportable): {block?.Name} ({blockType})");
                    TryWriteFailedBlock(projectKey, projectPath, cacheKey, block?.Name, blockType, gitBranch, gitCommit,
                        "ProDiag language is not supported for export", "unsupported");
                    return;
                }

                result.Failed++;
                result.Errors.Add("Block " + block?.Name + ": " + ex.Message);
                AppLogger.LogError($"[KnowledgeIndexer] Index block failed: {block?.Name} ({blockType})", ex);
                // Completeness anchor: still record the block as failed so it is not missing.
                TryWriteFailedBlock(projectKey, projectPath, cacheKey, block?.Name, blockType, gitBranch, gitCommit, ex.Message);
            }
        }

        private async Task IndexTypeAsync(
            string projectKey, string projectPath, PlcType type, string cacheKey,
            string gitBranch, string gitCommit, bool force,
            PlcProjectIndexBuildResult result, Action<string> onProgress)
        {
            try
            {
                onProgress?.Invoke("Indexing type " + type.Name + "...");
                // Export via the object directly: avoids ExportBlockCodeAsync's internal
                // TypeGroup.Types.Find(name), which does not recurse into child groups
                // and so reports grouped UDTs as "not found in project" during indexing.
                var xml = await _tiaService.ExportTypeCodeAsync(type).ConfigureAwait(false);
                var version = _blockVersionService.UpdateBlockVersion(cacheKey, BuildVersionBasis(xml), "", out var changed, out var previousVersion);
                if (!force && IsUnchanged(projectKey, cacheKey, version, previousVersion, changed))
                {
                    result.SkippedUnchanged++;
                    return;
                }

                var record = new BlockWriteRecord
                {
                    ProjectKey = projectKey,
                    BlockId = cacheKey,
                    Name = type.Name,
                    CanonicalName = _canonicalizer.CanonicalizeBlockName(type.Name),
                    BlockType = "UDT",
                    Language = "UDT",
                    VersionHash = version,
                    ParentContainerId = ContainerIdFor(projectKey, type.Parent as PlcTypeGroup, "TypeGroup"),
                    SourceProjectPath = projectPath,
                    SourceBlockPath = cacheKey,
                    Summary = $"UDT {type.Name}",
                    GitBranch = gitBranch,
                    GitCommit = gitCommit
                };
                var snapshot = TryCreateSnapshot(xml, cacheKey, projectKey);
                ApplySnapshot(record, snapshot);
                ApplyInterfaceSnapshot(record, snapshot);
                PopulateInterfaceMembers(record, xml);
                _writer.UpsertBlock(record);
                result.Upserted++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add("Type " + type?.Name + ": " + ex.Message);
                AppLogger.LogError($"[KnowledgeIndexer] Index type failed: {type?.Name}", ex);
            }
        }

        /// <summary>Assemble a full block record (networks split by language, members, refs, edges).</summary>
        private BlockWriteRecord BuildBlockRecord(
            string projectKey, string projectPath, string cacheKey, string name, string blockType,
            string language, string version, string xml, BlockSnapshot snapshot, PlcBlock block)
        {
            var record = new BlockWriteRecord
            {
                ProjectKey = projectKey,
                BlockId = cacheKey,
                Name = name,
                CanonicalName = _canonicalizer.CanonicalizeBlockName(name),
                BlockType = MapBlockType(blockType, block),
                Language = language,
                VersionHash = version,
                SourceProjectPath = projectPath,
                SourceBlockPath = cacheKey,
                Summary = $"{blockType} {name}",
                ParseStatus = "ok"
            };
            ApplySnapshot(record, snapshot);
            ApplyInterfaceSnapshot(record, snapshot);

            if (string.IsNullOrWhiteSpace(xml))
            {
                record.ParseStatus = "partial";
                record.FailureReason = "Empty XML export";
                return record;
            }

            // DB has no networks: only interface members.
            if (string.Equals(record.BlockType, "DB", StringComparison.OrdinalIgnoreCase))
            {
                PopulateInterfaceMembers(record, xml);
                return record;
            }

            // Logic blocks: extract networks (LAD/FBD/SCL/STL/GRAPH), split by language.
            var networks = _networkExtractor.ExtractNetworks(projectKey, cacheKey, name, xml, language);
            foreach (var net in networks)
            {
                if (string.Equals(net.Language, "SCL", StringComparison.OrdinalIgnoreCase))
                    record.SclNetworks.Add(net);
                else
                    record.LadNetworks.Add(net);

                AppendSymbolRefs(record, net);
                AppendCallEdges(record, net);
            }

            // SCL source text is reconstructed from the <StructuredText> token tree inside
            // PlcNetworkFactExtractor (TiaXmlParseHelper.RebuildStructuredText) — the SAME
            // reconstruction the block viewer uses — so SourceText and ContentHash are already
            // correct and consistent for every SCL network (including SCL embedded in LAD blocks).
            // No separate block-level GenerateSource pass is needed here.

            // FB/FC interface members (Input/Output/InOut/Static/Temp/Return).
            if (string.Equals(record.BlockType, "FB", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.BlockType, "FC", StringComparison.OrdinalIgnoreCase))
                PopulateInterfaceMembers(record, xml);

            return record;
        }

        /// <summary>
        /// Copy the block-level facts that only the snapshot can provide: raw XML (write-back
        /// skeleton), revision (same sha256 the read contract exposes), source profile, block
        /// attributes, number and language. A block whose snapshot parse failed still indexes
        /// its networks; it simply has no verifiable snapshot/revision.
        /// </summary>
        private static void ApplySnapshot(BlockWriteRecord record, BlockSnapshot snapshot)
        {
            if (record == null || snapshot == null) return;

            record.RawXml = snapshot.RawXml;
            record.Revision = snapshot.Revision;
            record.SourceProfile = snapshot.SourceProfile;
            record.BlockAttributesJson = BuildAttributesJson(snapshot);

            if (string.IsNullOrWhiteSpace(record.Language))
                record.Language = snapshot.Language;
            if (string.IsNullOrWhiteSpace(record.BlockNumber) && snapshot.Number > 0)
                record.BlockNumber = snapshot.Number.ToString();
            if (string.IsNullOrWhiteSpace(record.Name))
                record.Name = snapshot.Name;
        }

        /// <summary>
        /// DB/UDT have no networks, so their canonical interface projection is the only
        /// structural payload the index can carry; store it verbatim from the read contract.
        /// Logic blocks keep their networks as evidence and do not duplicate the interface.
        /// </summary>
        private static void ApplyInterfaceSnapshot(BlockWriteRecord record, BlockSnapshot snapshot)
        {
            if (record == null || snapshot == null) return;
            var isDb = string.Equals(record.BlockType, "DB", StringComparison.OrdinalIgnoreCase);
            var isUdt = string.Equals(record.BlockType, "UDT", StringComparison.OrdinalIgnoreCase);
            if (!isDb && !isUdt) return;

            try
            {
                record.InterfaceJson = BlockSnapshotService.BuildInterface(snapshot);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[KnowledgeIndexer] Interface projection failed for {record.BlockId}: {ex.Message}");
            }
        }

        /// <summary>Block attributes as the same object shape the canonical IR uses (blockAttributes).</summary>
        private static string BuildAttributesJson(BlockSnapshot snapshot)
        {
            if (snapshot?.Attributes == null || snapshot.Attributes.Count == 0) return "{}";

            var attributes = new JsonObject();
            foreach (var attribute in snapshot.Attributes)
            {
                attributes[attribute.Name] = attribute.IsComplex
                    ? "<" + attribute.Name + ":" + attribute.ChildCount + ">"
                    : (attribute.Value ?? "");
            }
            return attributes.ToJsonString();
        }

        private static BlockSnapshot TryCreateSnapshot(string xml, string blockId, string projectKey)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                return BlockSnapshotService.FromXml(xml, blockId, projectKey);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[KnowledgeIndexer] Snapshot parse failed for {blockId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Basis of the incremental version hash: content + the parser version that produced
        /// the facts + the RAG payload contract. Any of the three changing forces a rebuild.
        /// </summary>
        private static string BuildVersionBasis(string xml)
        {
            return (xml ?? "")
                   + "\n//extractor:" + PlcNetworkFactExtractor.CurrentParserVersion
                   + "\n//contract:" + IndexContractVersion;
        }


        private void AppendSymbolRefs(BlockWriteRecord record, PlcNetworkDocument net)
        {
            foreach (var acc in net.Accesses)
            {
                if (string.IsNullOrWhiteSpace(acc.CanonicalPath)) continue;
                record.SymbolRefs.Add(new SymbolRefRecord
                {
                    RefId = Guid.NewGuid().ToString("N"),
                    NetworkId = net.NetworkId,
                    NetworkOrder = net.NetworkOrder,
                    CanonicalPath = acc.CanonicalPath,
                    SymbolName = LastSegment(acc.CanonicalPath),
                    Scope = acc.Scope,
                    Direction = acc.Direction.ToString(),
                    SourceRef = acc.SourceRef
                });
            }
        }

        private void AppendCallEdges(BlockWriteRecord record, PlcNetworkDocument net)
        {
            foreach (var call in net.CallSites)
            {
                if (string.IsNullOrWhiteSpace(call.RawCalleeName)) continue;
                record.CallEdges.Add(new CallEdgeRecord
                {
                    EdgeId = Guid.NewGuid().ToString("N"),
                    CalleeName = call.RawCalleeName,
                    CalleeBlockType = call.RawBlockType,
                    InstancePath = call.RawInstancePath,
                    NetworkId = net.NetworkId,
                    NetworkOrder = net.NetworkOrder,
                    SourceRef = call.SourceRef
                });
            }
        }

        /// <summary>Parse &lt;Member&gt; elements from block/type XML into interface members (flat path).</summary>
        private void PopulateInterfaceMembers(BlockWriteRecord record, string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return;
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                int order = 0;
                foreach (var section in doc.Descendants().Where(x => string.Equals(x.Name.LocalName, "Section", StringComparison.OrdinalIgnoreCase)))
                {
                    var sectionName = section.Attribute("Name")?.Value ?? "";
                    foreach (var member in section.Elements().Where(x => string.Equals(x.Name.LocalName, "Member", StringComparison.OrdinalIgnoreCase)))
                    {
                        WalkMember(record, member, sectionName, parentPath: "", parentMemberId: "", ref order);
                    }
                }
            }
            catch
            {
                // Member parsing is best-effort; block row is still written.
            }
        }

        private void WalkMember(BlockWriteRecord record, System.Xml.Linq.XElement member,
            string section, string parentPath, string parentMemberId, ref int order)
        {
            var name = member.Attribute("Name")?.Value ?? "";
            var dataType = member.Attribute("Datatype")?.Value ?? member.Attribute("DataType")?.Value ?? "";
            var path = string.IsNullOrEmpty(parentPath) ? name : parentPath + "." + name;
            var memberId = Guid.NewGuid().ToString("N");

            var comment = member.Descendants()
                .FirstOrDefault(x => string.Equals(x.Name.LocalName, "MultiLanguageText", StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? "";
            var initial = member.Elements().FirstOrDefault(x => string.Equals(x.Name.LocalName, "StartValue", StringComparison.OrdinalIgnoreCase))?.Value ?? "";

            record.Members.Add(new InterfaceMemberRecord
            {
                MemberId = memberId,
                ParentMemberId = parentMemberId,
                MemberPath = path,
                Name = name,
                DataType = dataType,
                Section = section,
                InitialValue = initial,
                Comment = comment,
                OrderIndex = order++
            });

            // Nested struct members
            foreach (var child in member.Elements().Where(x => string.Equals(x.Name.LocalName, "Member", StringComparison.OrdinalIgnoreCase)))
            {
                WalkMember(record, child, section, path, memberId, ref order);
            }
        }

        private void TryWriteFailedBlock(string projectKey, string projectPath, string cacheKey, string name,
            string blockType, string gitBranch, string gitCommit, string reason, string parseStatus = "failed")
        {
            try
            {
                _writer.UpsertBlock(new BlockWriteRecord
                {
                    ProjectKey = projectKey,
                    BlockId = cacheKey,
                    Name = name ?? cacheKey,
                    CanonicalName = _canonicalizer.CanonicalizeBlockName(name),
                    BlockType = blockType,
                    SourceProjectPath = projectPath,
                    SourceBlockPath = cacheKey,
                    ParseStatus = parseStatus,
                    FailureReason = reason,
                    GitBranch = gitBranch,
                    GitCommit = gitCommit
                });
            }
            catch
            {
                // best-effort
            }
        }

        // ---- container registration ----

        private void RegisterContainer(string projectKey, object group, string kind, string parentId, string parentPath, int depth)
        {
            string name = GroupName(group);
            var path = string.IsNullOrEmpty(parentPath) ? name : parentPath + "\\" + name;
            var containerId = kind + ":" + path;
            _writer.UpsertContainer(projectKey, containerId, parentId ?? "", name, kind, path, depth);

            foreach (var child in ChildGroups(group))
                RegisterContainer(projectKey, child, kind, containerId, path, depth + 1);
        }

        private string ContainerIdFor(string projectKey, object group, string kind)
        {
            if (group == null) return "";
            var path = kind == "BlockGroup"
                ? BuildBlockGroupPath(group as PlcBlockGroup)
                : BuildTypeGroupPath(group as PlcTypeGroup);
            return string.IsNullOrEmpty(path) ? (kind + ":" + GroupName(group)) : (kind + ":" + path);
        }

        private static string GroupName(object group)
        {
            switch (group)
            {
                case PlcBlockGroup bg: return bg.Name ?? "";
                case PlcTypeGroup tg: return tg.Name ?? "";
                default: return "";
            }
        }

        private static IEnumerable<object> ChildGroups(object group)
        {
            if (group is PlcBlockGroup bg)
                foreach (PlcBlockUserGroup g in bg.Groups) yield return g;
            else if (group is PlcTypeGroup tg)
                foreach (PlcTypeUserGroup g in tg.Groups) yield return g;
        }

        // ---- helpers (mirrors PlcProjectIndexingService) ----

        private static string MapBlockType(string clrTypeName, PlcBlock block)
        {
            // Match on the type-name suffix, never on a substring: "GlobalDB" contains "ob"
            // (Gl-OB-al), so an IndexOf("OB") check labelled every global DB as an OB and the
            // block lost its interface members (DB/UDT interface snapshots are only kept for DB).
            var n = clrTypeName ?? "";
            var shortName = n.Substring(n.LastIndexOf('.') + 1);
            if (shortName.EndsWith("DataBlock", StringComparison.OrdinalIgnoreCase)
                || shortName.EndsWith("DB", StringComparison.OrdinalIgnoreCase)) return "DB";
            if (shortName.EndsWith("OB", StringComparison.OrdinalIgnoreCase)) return "OB";
            if (shortName.EndsWith("FB", StringComparison.OrdinalIgnoreCase)) return "FB";
            if (shortName.EndsWith("FC", StringComparison.OrdinalIgnoreCase)) return "FC";
            return n;
        }

        private static string LastSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var idx = path.LastIndexOf('.');
            return idx >= 0 && idx < path.Length - 1 ? path.Substring(idx + 1) : path;
        }

        private string ResolveProjectKey()
        {
            try
            {
                var path = _tiaService.Project?.Path?.FullName;
                if (!string.IsNullOrWhiteSpace(path))
                    return _blockVersionService.GetProjectCacheKey(path);
            }
            catch { }
            return _blockVersionService.GetProjectCacheKey(_tiaService.Project?.Name);
        }

        private string SafeProjectPath()
        {
            try { return _tiaService.Project?.Path?.FullName ?? ""; }
            catch { return ""; }
        }

        // Public static helpers: also used by the worker's external-change scanner
        // (TIAKnowledgeWorker) so scan keys stay identical to index keys.
        public static IEnumerable<PlcBlock> EnumerateBlocks(PlcBlockGroup group)
            => EnumerateBlocksCore(group, new HashSet<int>());

        private static IEnumerable<PlcBlock> EnumerateBlocksCore(PlcBlockGroup group, HashSet<int> visited)
        {
            if (group == null || !visited.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(group)))
                yield break;
            foreach (PlcBlock block in group.Blocks) yield return block;
            foreach (PlcBlockUserGroup child in group.Groups)
                foreach (var nested in EnumerateBlocksCore(child, visited)) yield return nested;
        }

        private static IEnumerable<PlcType> EnumerateTypes(PlcTypeGroup group)
            => EnumerateTypesCore(group, new HashSet<int>());

        private static IEnumerable<PlcType> EnumerateTypesCore(PlcTypeGroup group, HashSet<int> visited)
        {
            if (group == null || !visited.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(group)))
                yield break;
            foreach (PlcType type in group.Types) yield return type;
            foreach (PlcTypeUserGroup child in group.Groups)
                foreach (var nested in EnumerateTypesCore(child, visited)) yield return nested;
        }

        public static string BuildBlockCacheKey(PlcBlock block, string blockType)
        {
            var groupPath = block?.Parent is PlcBlockGroup group ? BuildBlockGroupPath(group) : "";
            return string.IsNullOrWhiteSpace(groupPath)
                ? $"{blockType}:{block?.Name}"
                : $"{blockType}:{groupPath}\\{block?.Name}";
        }

        private static string BuildTypeCacheKey(PlcType type)
        {
            var groupPath = type?.Parent is PlcTypeGroup group ? BuildTypeGroupPath(group) : "";
            return string.IsNullOrWhiteSpace(groupPath)
                ? $"PlcType:{type?.Name}"
                : $"PlcType:{groupPath}\\{type?.Name}";
        }

        private static string BuildBlockGroupPath(PlcBlockGroup group)
        {
            var parts = new Stack<string>();
            var current = group;
            while (current is PlcBlockGroup g && !(g is PlcBlockSystemGroup))
            {
                if (!string.IsNullOrWhiteSpace(g.Name)) parts.Push(g.Name);
                current = g.Parent as PlcBlockGroup;
            }
            return string.Join("\\", parts);
        }

        private static string BuildTypeGroupPath(PlcTypeGroup group)
        {
            var parts = new Stack<string>();
            var current = group;
            while (current is PlcTypeGroup g && !(g is PlcTypeSystemGroup))
            {
                if (!string.IsNullOrWhiteSpace(g.Name)) parts.Push(g.Name);
                current = g.Parent as PlcTypeGroup;
            }
            return string.Join("\\", parts);
        }

        /// <summary>True if the exception is the Openness "ProDiag not exportable" limitation.</summary>
        private static bool IsProDiagUnsupported(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e.Message != null && e.Message.IndexOf("ProDiag", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string TryGetProgrammingLanguage(PlcBlock block)
        {
            try
            {
                dynamic dyn = block;
                return dyn.ProgrammingLanguage.ToString();
            }
            catch { return ""; }
        }
    }
}
