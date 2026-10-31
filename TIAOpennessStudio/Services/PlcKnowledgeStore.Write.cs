using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TIAOpennessStudio.Models.NetworkKnowledge;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Write side of the knowledge store. This process is the SINGLE writer.
    /// All block updates use a "delete old derived rows + write new" transaction
    /// keyed on block_id, so removed networks/members never leave orphans.
    /// </summary>
    public sealed class PlcKnowledgeStoreWriter
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly PlcKnowledgeStore _store;
        private readonly string _writeConnectionString;
        private readonly object _writeSync = new object();

        public PlcKnowledgeStoreWriter(PlcKnowledgeStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _writeConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = store.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();
        }

        // ---- Version-hash readback (incremental skip across restarts) ----

        /// <summary>
        /// Read the content version hash last persisted for a block/type. Unlike the
        /// in-memory BlockVersionService cache, this survives a worker restart, so
        /// force=false rebuilds stay incremental after the process is recycled.
        /// Returns false when the row (or its hash) does not exist yet.
        /// </summary>
        public bool TryGetBlockVersionHash(string projectKey, string blockId, out string versionHash)
        {
            versionHash = "";
            try
            {
                _store.EnsureSchemaComplete();
                using (var conn = _store.OpenRead())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT version_hash FROM plc_block WHERE project_key=@pk AND block_id=@bid;";
                    cmd.Parameters.AddWithValue("@pk", projectKey ?? "");
                    cmd.Parameters.AddWithValue("@bid", blockId ?? "");
                    versionHash = cmd.ExecuteScalar() as string ?? "";
                    return !string.IsNullOrEmpty(versionHash);
                }
            }
            catch
            {
                return false;
            }
        }

        // ---- Container (directory tree) ----

        public void UpsertContainer(string projectKey, string containerId, string parentContainerId,
            string name, string kind, string path, int depth)
        {
            _store.EnsureSchemaComplete();
            var now = DateTime.UtcNow.ToString("o");
            lock (_writeSync)
            {
                using (var conn = new SqliteConnection(_writeConnectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "INSERT INTO plc_container(project_key, container_id, parent_container_id, name, kind, path, depth, updated_at) " +
                            "VALUES(@pk,@cid,@parent,@name,@kind,@path,@depth,@now) " +
                            "ON CONFLICT(project_key, container_id) DO UPDATE SET " +
                            "parent_container_id=excluded.parent_container_id, name=excluded.name, kind=excluded.kind, " +
                            "path=excluded.path, depth=excluded.depth, updated_at=excluded.updated_at;";
                        Bind(cmd, "@pk", projectKey);
                        Bind(cmd, "@cid", containerId);
                        Bind(cmd, "@parent", parentContainerId ?? "");
                        Bind(cmd, "@name", name ?? "");
                        Bind(cmd, "@kind", kind ?? "");
                        Bind(cmd, "@path", path ?? "");
                        cmd.Parameters.AddWithValue("@depth", depth);
                        Bind(cmd, "@now", now);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        // ---- Block-level upsert (single transaction, delete-old + write-new) ----

        /// <summary>
        /// Upsert one block and all its derived rows atomically. Whatever this block
        /// previously wrote (networks, members, symbol refs, call edges) is removed
        /// first, then rewritten from <paramref name="record"/>.
        /// </summary>
        public void UpsertBlock(BlockWriteRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            _store.EnsureSchemaComplete();
            var pk = record.ProjectKey ?? "";
            var bk = record.BlockId ?? "";
            var now = DateTime.UtcNow.ToString("o");

            lock (_writeSync)
            {
                using (var conn = new SqliteConnection(_writeConnectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        DeleteBlockDerivedRows(conn, tx, pk, bk);
                        WriteBlockRow(conn, tx, record, now);
                        WriteBlockSnapshot(conn, tx, pk, bk, record, now);

                        foreach (var lad in record.LadNetworks)
                            WriteLadNetwork(conn, tx, pk, bk, lad, now);

                        foreach (var scl in record.SclNetworks)
                            WriteSclNetwork(conn, tx, pk, bk, scl, now);

                        foreach (var m in record.Members)
                            WriteInterfaceMember(conn, tx, pk, bk, m);

                        foreach (var r in record.SymbolRefs)
                            WriteSymbolRef(conn, tx, pk, bk, r);

                        foreach (var e in record.CallEdges)
                            WriteCallEdge(conn, tx, pk, bk, record.Name, e);

                        // Full-text index sync (G-1): rewrite this block's FTS docs.
                        // DeleteBlockDerivedRows already cleared old FTS rows for bk.
                        WriteSearchFtsDocs(conn, tx, record);

                        tx.Commit();
                    }
                }
            }
        }

        /// <summary>Remove a block and all its derived rows (deletion / rename old key).</summary>
        public void RemoveBlock(string projectKey, string blockId)
        {
            lock (_writeSync)
            {
                using (var conn = new SqliteConnection(_writeConnectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        DeleteBlockDerivedRows(conn, tx, projectKey, blockId);
                        ExecDelete(conn, tx, "DELETE FROM plc_block WHERE project_key=@pk AND block_id=@bk;", projectKey, blockId);
                        tx.Commit();
                    }
                }
            }
        }

        /// <summary>Remove any blocks/containers not present in the discovered set (stale cleanup).</summary>
        public void PruneMissing(string projectKey, IReadOnlyCollection<string> discoveredBlockIds)
        {
            lock (_writeSync)
            {
                using (var conn = new SqliteConnection(_writeConnectionString))
                {
                    conn.Open();
                    var existing = new List<string>();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT block_id FROM plc_block WHERE project_key=@pk;";
                        Bind(cmd, "@pk", projectKey);
                        using (var reader = cmd.ExecuteReader())
                            while (reader.Read()) existing.Add(reader.GetString(0));
                    }

                    var discovered = new HashSet<string>(discoveredBlockIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                    foreach (var blockId in existing)
                    {
                        if (discovered.Contains(blockId)) continue;
                        using (var tx = conn.BeginTransaction())
                        {
                            DeleteBlockDerivedRows(conn, tx, projectKey, blockId);
                            ExecDelete(conn, tx, "DELETE FROM plc_block WHERE project_key=@pk AND block_id=@bk;", projectKey, blockId);
                            tx.Commit();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// P2b: reconcile the callee_resolved flag of call edges after a rebuild. callee_name is string-matched with no foreign key,
        /// so callers that did not change keep their old edges after a block is renamed/deleted; here we uniformly mark them based on the current plc_block content:
        /// 1=resolvable to an indexed block, 0=unresolvable (system blocks such as TON/external blocks/renamed/deleted).
        /// Mark only, never delete (fail-closed, no data loss), so the query side can identify stale reverse edges.
        /// </summary>
        public int ReconcileCallEdges(string projectKey)
        {
            _store.EnsureSchemaComplete();
            lock (_writeSync)
            {
                using (var conn = new SqliteConnection(_writeConnectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "UPDATE plc_call_edge SET callee_resolved = CASE WHEN EXISTS (" +
                            "SELECT 1 FROM plc_block b WHERE b.project_key=plc_call_edge.project_key " +
                            "AND (b.name=plc_call_edge.callee_name OR b.canonical_name=plc_call_edge.callee_name)" +
                            ") THEN 1 ELSE 0 END WHERE project_key=@pk;";
                        Bind(cmd, "@pk", projectKey ?? "");
                        return cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        public void UpdateProjectState(string projectKey, string status, int blockCount,
            string gitBranch, string gitCommit, string reason)
        {
            var now = DateTime.UtcNow.ToString("o");
            lock (_writeSync)
            {
                using (var conn = new SqliteConnection(_writeConnectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "INSERT INTO project_index_state(project_key, status, last_indexed_at, last_reason, block_count, git_branch, git_commit, updated_at) " +
                            "VALUES(@pk,@status,@now,@reason,@count,@branch,@commit,@now) " +
                            "ON CONFLICT(project_key) DO UPDATE SET " +
                            "status=excluded.status, last_indexed_at=excluded.last_indexed_at, last_reason=excluded.last_reason, " +
                            "block_count=excluded.block_count, git_branch=excluded.git_branch, git_commit=excluded.git_commit, updated_at=excluded.updated_at;";
                        Bind(cmd, "@pk", projectKey);
                        Bind(cmd, "@status", status ?? "");
                        Bind(cmd, "@now", now);
                        Bind(cmd, "@reason", reason ?? "");
                        cmd.Parameters.AddWithValue("@count", blockCount);
                        Bind(cmd, "@branch", gitBranch ?? "");
                        Bind(cmd, "@commit", gitCommit ?? "");
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        // ---- private row writers ----

        private static void DeleteBlockDerivedRows(SqliteConnection conn, SqliteTransaction tx, string pk, string bk)
        {
            ExecDelete(conn, tx, "DELETE FROM plc_network_lad WHERE project_key=@pk AND block_id=@bk;", pk, bk);
            ExecDelete(conn, tx, "DELETE FROM plc_network_scl WHERE project_key=@pk AND block_id=@bk;", pk, bk);
            ExecDelete(conn, tx, "DELETE FROM plc_interface_member WHERE project_key=@pk AND block_id=@bk;", pk, bk);
            ExecDelete(conn, tx, "DELETE FROM plc_symbol_ref WHERE project_key=@pk AND block_id=@bk;", pk, bk);
            ExecDelete(conn, tx, "DELETE FROM plc_call_edge WHERE project_key=@pk AND caller_block_id=@bk;", pk, bk);
            // The snapshot is a content artifact: it is replaced by the new row below, and
            // removed together with the block. A failed export therefore leaves no stale
            // revision behind for the patch tools to trust.
            ExecDelete(conn, tx, "DELETE FROM plc_block_snapshot WHERE project_key=@pk AND block_id=@bk;", pk, bk);
            DeleteSearchFtsDocs(conn, tx, pk, bk);
        }

        // ---- Full-text (FTS5) sync (G-1, P1-5 step 1) ----
        //
        // Indexes ONLY semantic text: one 'block' doc (name + summary) plus one doc per
        // LAD/SCL network (title + comment + call_summary). Identifiers are never indexed.
        // Kept in the same UpsertBlock transaction as the fact rows, so FTS can't drift
        // from the block content. If the FTS table is absent (FTS5 not compiled in),
        // these are no-ops — the search side falls back to LIKE.

        private static void WriteSearchFtsDocs(SqliteConnection conn, SqliteTransaction tx, BlockWriteRecord r)
        {
            if (!PlcKnowledgeStore.IsSearchFtsAvailable(conn)) return;

            var pk = r.ProjectKey ?? "";
            var bk = r.BlockId ?? "";

            // Block-level doc: name + summary (the block's own semantic text).
            var blockBody = JoinNonEmpty(" ", r.Name, r.CanonicalName, r.Summary);
            if (!string.IsNullOrWhiteSpace(blockBody))
                InsertFtsDoc(conn, tx, pk, bk, "block", blockBody);

            // Per-network docs: title + comment + call_summary (natural-language fields).
            foreach (var lad in r.LadNetworks)
            {
                var body = JoinNonEmpty(" ", lad.Title, lad.Comment);
                if (!string.IsNullOrWhiteSpace(body))
                    InsertFtsDoc(conn, tx, pk, bk, "lad_net", body);
            }
            foreach (var scl in r.SclNetworks)
            {
                var body = JoinNonEmpty(" ", scl.Title, scl.Comment);
                if (!string.IsNullOrWhiteSpace(body))
                    InsertFtsDoc(conn, tx, pk, bk, "scl_net", body);
            }
        }

        private static void InsertFtsDoc(SqliteConnection conn, SqliteTransaction tx, string pk, string bk, string docKind, string body)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO plc_search_fts(project_key, block_id, doc_kind, text_body) " +
                    "VALUES(@pk,@bk,@kind,@body);";
                Bind(cmd, "@pk", pk);
                Bind(cmd, "@bk", bk);
                Bind(cmd, "@kind", docKind);
                Bind(cmd, "@body", body);
                cmd.ExecuteNonQuery();
            }
        }

        private static void DeleteSearchFtsDocs(SqliteConnection conn, SqliteTransaction tx, string pk, string bk)
        {
            if (!PlcKnowledgeStore.IsSearchFtsAvailable(conn)) return;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM plc_search_fts WHERE project_key=@pk AND block_id=@bk;";
                Bind(cmd, "@pk", pk);
                Bind(cmd, "@bk", bk);
                cmd.ExecuteNonQuery();
            }
        }

        private static string JoinNonEmpty(string sep, params string[] parts)
        {
            return string.Join(sep, System.Array.FindAll(parts, p => !string.IsNullOrWhiteSpace(p)));
        }

        private static void WriteBlockRow(SqliteConnection conn, SqliteTransaction tx, BlockWriteRecord r, string now)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO plc_block(project_key, block_id, parent_container_id, name, canonical_name, block_type, block_number, " +
                    "language, version_hash, revision, source_profile, network_count, member_count, summary, parse_status, failure_reason, " +
                    "source_project_path, source_block_path, updated_at, git_branch, git_commit) " +
                    "VALUES(@pk,@bk,@parent,@name,@canon,@type,@num,@lang,@ver,@rev,@profile,@nc,@mc,@summary,@ps,@fr,@spp,@sbp,@now,@branch,@commit) " +
                    "ON CONFLICT(project_key, block_id) DO UPDATE SET " +
                    "parent_container_id=excluded.parent_container_id, name=excluded.name, canonical_name=excluded.canonical_name, " +
                    "block_type=excluded.block_type, block_number=excluded.block_number, language=excluded.language, " +
                    "version_hash=excluded.version_hash, revision=excluded.revision, source_profile=excluded.source_profile, " +
                    "network_count=excluded.network_count, member_count=excluded.member_count, " +
                    "summary=excluded.summary, parse_status=excluded.parse_status, failure_reason=excluded.failure_reason, " +
                    "source_project_path=excluded.source_project_path, source_block_path=excluded.source_block_path, " +
                    "updated_at=excluded.updated_at, git_branch=excluded.git_branch, git_commit=excluded.git_commit;";
                Bind(cmd, "@pk", r.ProjectKey);
                Bind(cmd, "@bk", r.BlockId);
                Bind(cmd, "@parent", r.ParentContainerId ?? "");
                Bind(cmd, "@name", r.Name ?? "");
                Bind(cmd, "@canon", r.CanonicalName ?? "");
                Bind(cmd, "@type", r.BlockType ?? "");
                Bind(cmd, "@num", r.BlockNumber ?? "");
                Bind(cmd, "@lang", r.Language ?? "");
                Bind(cmd, "@ver", r.VersionHash ?? "");
                Bind(cmd, "@rev", r.Revision ?? "");
                Bind(cmd, "@profile", r.SourceProfile ?? "");
                cmd.Parameters.AddWithValue("@nc", r.LadNetworks.Count + r.SclNetworks.Count);
                cmd.Parameters.AddWithValue("@mc", r.Members.Count);
                Bind(cmd, "@summary", r.Summary ?? "");
                Bind(cmd, "@ps", r.ParseStatus ?? "ok");
                Bind(cmd, "@fr", r.FailureReason ?? "");
                Bind(cmd, "@spp", r.SourceProjectPath ?? "");
                Bind(cmd, "@sbp", r.SourceBlockPath ?? "");
                Bind(cmd, "@now", now);
                Bind(cmd, "@branch", r.GitBranch ?? "");
                Bind(cmd, "@commit", r.GitCommit ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// T11: persist the exact XML export a block's index row was built from.
        /// Written only when the record carries both the XML and its revision; a block whose
        /// export failed has no verifiable snapshot, and its previous snapshot was already
        /// removed by <see cref="DeleteBlockDerivedRows"/>.
        /// </summary>
        private static void WriteBlockSnapshot(SqliteConnection conn, SqliteTransaction tx, string pk, string bk, BlockWriteRecord r, string now)
        {
            if (string.IsNullOrEmpty(r.RawXml) || string.IsNullOrEmpty(r.Revision)) return;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO plc_block_snapshot(project_key, block_id, revision, source_profile, raw_xml, block_attributes_json, interface_json, indexed_at) " +
                    "VALUES(@pk,@bk,@rev,@profile,@xml,@attrs,@iface,@now) " +
                    "ON CONFLICT(project_key, block_id) DO UPDATE SET " +
                    "revision=excluded.revision, source_profile=excluded.source_profile, raw_xml=excluded.raw_xml, " +
                    "block_attributes_json=excluded.block_attributes_json, interface_json=excluded.interface_json, indexed_at=excluded.indexed_at;";
                Bind(cmd, "@pk", pk);
                Bind(cmd, "@bk", bk);
                Bind(cmd, "@rev", r.Revision);
                Bind(cmd, "@profile", r.SourceProfile ?? "");
                Bind(cmd, "@xml", r.RawXml);
                Bind(cmd, "@attrs", r.BlockAttributesJson ?? "{}");
                Bind(cmd, "@iface", r.InterfaceJson ?? "");
                Bind(cmd, "@now", now);
                cmd.ExecuteNonQuery();
            }
        }

        private void WriteLadNetwork(SqliteConnection conn, SqliteTransaction tx, string pk, string bk, PlcNetworkDocument net, string now)
        {
            var delimiter = DelimiterFor(net);
            var inputs = ProjectContacts(net, delimiter);
            var outputs = ProjectCoils(net, delimiter);
            var callSummary = ProjectCalls(net, delimiter);

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO plc_network_lad(project_key, network_id, block_id, network_order, compile_unit_id, title, comment, " +
                    "inputs, outputs, delimiter, call_summary, source_text, json_payload, content_hash, source_ref, parse_status, failure_reason, indexed_at) " +
                    "VALUES(@pk,@nid,@bk,@order,@cu,@title,@comment,@inputs,@outputs,@delim,@calls,@src,@json,@hash,@sref,@ps,@fr,@now);";
                Bind(cmd, "@pk", pk);
                Bind(cmd, "@nid", net.NetworkId);
                Bind(cmd, "@bk", bk);
                cmd.Parameters.AddWithValue("@order", net.NetworkOrder);
                Bind(cmd, "@cu", net.CompileUnitId ?? "");
                Bind(cmd, "@title", net.Title ?? "");
                Bind(cmd, "@comment", net.Comment ?? "");
                Bind(cmd, "@inputs", inputs);
                Bind(cmd, "@outputs", outputs);
                Bind(cmd, "@delim", delimiter);
                Bind(cmd, "@calls", callSummary);
                Bind(cmd, "@src", net.SourceText ?? "");
                Bind(cmd, "@json", string.IsNullOrEmpty(net.CanonicalJson) ? JsonSerializer.Serialize(net, JsonOpts) : net.CanonicalJson);
                Bind(cmd, "@hash", net.ContentHash ?? "");
                Bind(cmd, "@sref", $"net:{net.NetworkOrder}|cu:{net.CompileUnitId}");
                Bind(cmd, "@ps", net.Status == ParseStatus.Failed ? "failed" : "ok");
                Bind(cmd, "@fr", net.FailureReason ?? "");
                Bind(cmd, "@now", now);
                cmd.ExecuteNonQuery();
            }
        }

        private void WriteSclNetwork(SqliteConnection conn, SqliteTransaction tx, string pk, string bk, PlcNetworkDocument net, string now)
        {
            var delimiter = DelimiterFor(net);
            var readVars = ProjectAccessByDirection(net, delimiter, AccessDirection.Read);
            var writeVars = ProjectAccessByDirection(net, delimiter, AccessDirection.Write);
            var callSummary = ProjectCalls(net, delimiter);

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO plc_network_scl(project_key, network_id, block_id, network_order, compile_unit_id, title, comment, " +
                    "read_vars, write_vars, delimiter, call_summary, source_text, json_payload, content_hash, source_ref, parse_status, failure_reason, indexed_at) " +
                    "VALUES(@pk,@nid,@bk,@order,@cu,@title,@comment,@rv,@wv,@delim,@calls,@src,@json,@hash,@sref,@ps,@fr,@now);";
                Bind(cmd, "@pk", pk);
                Bind(cmd, "@nid", net.NetworkId);
                Bind(cmd, "@bk", bk);
                cmd.Parameters.AddWithValue("@order", net.NetworkOrder);
                Bind(cmd, "@cu", net.CompileUnitId ?? "");
                Bind(cmd, "@title", net.Title ?? "");
                Bind(cmd, "@comment", net.Comment ?? "");
                Bind(cmd, "@rv", readVars);
                Bind(cmd, "@wv", writeVars);
                Bind(cmd, "@delim", delimiter);
                Bind(cmd, "@calls", callSummary);
                Bind(cmd, "@src", net.SourceText ?? "");
                // T12 payload policy: SCL is stored as reconstructed source text only. The
                // canonical token tree duplicates the same content at ~15x the size and is
                // never used for SCL write-back (patch_block_network rebuilds from source).
                Bind(cmd, "@json", "");
                Bind(cmd, "@hash", net.ContentHash ?? "");
                Bind(cmd, "@sref", $"net:{net.NetworkOrder}|cu:{net.CompileUnitId}");
                Bind(cmd, "@ps", net.Status == ParseStatus.Failed ? "failed" : "ok");
                Bind(cmd, "@fr", net.FailureReason ?? "");
                Bind(cmd, "@now", now);
                cmd.ExecuteNonQuery();
            }
        }

        private static void WriteInterfaceMember(SqliteConnection conn, SqliteTransaction tx, string pk, string bk, InterfaceMemberRecord m)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO plc_interface_member(project_key, block_id, member_id, parent_member_id, member_path, name, " +
                    "data_type, section, initial_value, comment, order_index) " +
                    "VALUES(@pk,@bk,@mid,@parent,@path,@name,@dt,@section,@init,@comment,@order);";
                Bind(cmd, "@pk", pk);
                Bind(cmd, "@bk", bk);
                Bind(cmd, "@mid", m.MemberId);
                Bind(cmd, "@parent", m.ParentMemberId ?? "");
                Bind(cmd, "@path", m.MemberPath ?? "");
                Bind(cmd, "@name", m.Name ?? "");
                Bind(cmd, "@dt", m.DataType ?? "");
                Bind(cmd, "@section", m.Section ?? "");
                Bind(cmd, "@init", m.InitialValue ?? "");
                Bind(cmd, "@comment", m.Comment ?? "");
                cmd.Parameters.AddWithValue("@order", m.OrderIndex);
                cmd.ExecuteNonQuery();
            }
        }

        private static void WriteSymbolRef(SqliteConnection conn, SqliteTransaction tx, string pk, string bk, SymbolRefRecord r)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO plc_symbol_ref(project_key, ref_id, block_id, network_id, network_order, canonical_path, symbol_name, scope, direction, source_ref) " +
                    "VALUES(@pk,@rid,@bk,@nid,@order,@path,@name,@scope,@dir,@sref);";
                Bind(cmd, "@pk", pk);
                Bind(cmd, "@rid", r.RefId);
                Bind(cmd, "@bk", bk);
                Bind(cmd, "@nid", r.NetworkId ?? "");
                cmd.Parameters.AddWithValue("@order", r.NetworkOrder);
                Bind(cmd, "@path", r.CanonicalPath ?? "");
                Bind(cmd, "@name", r.SymbolName ?? "");
                Bind(cmd, "@scope", r.Scope ?? "");
                Bind(cmd, "@dir", r.Direction ?? "");
                Bind(cmd, "@sref", r.SourceRef ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        private static void WriteCallEdge(SqliteConnection conn, SqliteTransaction tx, string pk, string bk, string callerName, CallEdgeRecord e)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO plc_call_edge(project_key, edge_id, caller_block_id, caller_name, callee_name, callee_block_type, instance_path, network_id, network_order, source_ref) " +
                    "VALUES(@pk,@eid,@caller,@callerName,@callee,@ctype,@inst,@nid,@order,@sref);";
                Bind(cmd, "@pk", pk);
                Bind(cmd, "@eid", e.EdgeId);
                Bind(cmd, "@caller", bk);
                Bind(cmd, "@callerName", callerName ?? "");
                Bind(cmd, "@callee", e.CalleeName ?? "");
                Bind(cmd, "@ctype", e.CalleeBlockType ?? "");
                Bind(cmd, "@inst", e.InstancePath ?? "");
                Bind(cmd, "@nid", e.NetworkId ?? "");
                cmd.Parameters.AddWithValue("@order", e.NetworkOrder);
                Bind(cmd, "@sref", e.SourceRef ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        // ---- projection helpers (LAD contacts=inputs, coils=outputs) ----

        private static readonly HashSet<string> CoilInstructions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Coil", "SCoil", "RCoil", "SetCoil", "ResetCoil", "Set_BF", "Reset_BF"
        };

        private static string DelimiterFor(PlcNetworkDocument net) => PlcKnowledgeStore.DefaultDelimiter;

        /// <summary>Inputs = variables read by contacts (Read/ReadWrite accesses).</summary>
        private static string ProjectContacts(PlcNetworkDocument net, string delimiter)
        {
            var names = net.Accesses
                .Where(a => a.Direction == AccessDirection.Read || a.Direction == AccessDirection.ReadWrite)
                .Select(a => a.CanonicalPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return string.Join(delimiter, names);
        }

        /// <summary>Outputs = variables driven by coils (Write/ReadWrite accesses).</summary>
        private static string ProjectCoils(PlcNetworkDocument net, string delimiter)
        {
            var names = net.Accesses
                .Where(a => a.Direction == AccessDirection.Write || a.Direction == AccessDirection.ReadWrite)
                .Select(a => a.CanonicalPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return string.Join(delimiter, names);
        }

        private static string ProjectAccessByDirection(PlcNetworkDocument net, string delimiter, AccessDirection dir)
        {
            var names = net.Accesses
                .Where(a => a.Direction == dir || a.Direction == AccessDirection.ReadWrite)
                .Select(a => a.CanonicalPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return string.Join(delimiter, names);
        }

        private static string ProjectCalls(PlcNetworkDocument net, string delimiter)
        {
            var names = net.CallSites
                .Select(c => string.IsNullOrWhiteSpace(c.RawInstancePath)
                    ? c.RawCalleeName
                    : $"{c.RawCalleeName}({c.RawInstancePath})")
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return string.Join(delimiter, names);
        }

        private static void Bind(SqliteCommand cmd, string name, string value)
            => cmd.Parameters.AddWithValue(name, value ?? string.Empty);

        private static void ExecDelete(SqliteConnection conn, SqliteTransaction tx, string sql, string pk, string bk)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                Bind(cmd, "@pk", pk);
                Bind(cmd, "@bk", bk);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
