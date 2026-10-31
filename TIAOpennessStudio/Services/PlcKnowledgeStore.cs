using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TIAOpennessStudio.Services
{
    /// <summary>  
    /// Layout:
    ///   plc_container        directory/group hierarchy tree (self-referencing)
    ///   plc_block            every FC/FB/DB/OB/UDT/Tag as one row (completeness anchor)
    ///   plc_block_snapshot   the exact XML export + revision the index was built from
    ///   plc_network_lad      one row per LAD network, with inputs(contacts)/outputs(coils) projections
    ///   plc_network_scl      one row per SCL network, read_vars/write_vars projections
    ///   plc_interface_member DB/FB/UDT interface members (self-referencing for nested struct)
    ///   plc_symbol_ref       per-reference symbol usage (cross-reference detail)
    ///   plc_call_edge        block-to-block call graph
    ///   project_index_state  project-level cache state
    ///
    /// This class owns the ONLY write connection. Readers (worker / UI) open
    /// read-only connections against the same WAL database.
    /// </summary>
    public sealed class PlcKnowledgeStore
    {
        /// <summary>Default delimiter for inputs/outputs/read_vars/write_vars projections.
        /// Chosen because PLC symbol names never contain " | ".</summary>
        public const string DefaultDelimiter = " | ";

        private readonly string _dbPath;
        private readonly string _writeConnectionString;
        private readonly string _readConnectionString;
        private readonly object _writeSync = new object();

        public string DatabasePath => _dbPath;

        public PlcKnowledgeStore(string appBaseDirectory)
        {
            var baseDir = string.IsNullOrWhiteSpace(appBaseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : appBaseDirectory;
            var indexDir = Path.Combine(baseDir, "Index");
            Directory.CreateDirectory(indexDir);
            _dbPath = Path.Combine(indexDir, "project_index.db");

            _writeConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            _readConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            EnsureSchema();
        }

        /// <summary>All tables this store owns. Used by the completeness check.</summary>
        private static readonly string[] RequiredTables =
        {
            "plc_container", "plc_block", "plc_block_snapshot", "plc_network_lad", "plc_network_scl",
            "plc_interface_member", "plc_symbol_ref", "plc_call_edge", "project_index_state"
        };

        /// <summary>Open a read-only connection (readers: worker/UI/search).
        /// Guards against a missing/incomplete schema by ensuring all tables exist first.</summary>
        public SqliteConnection OpenRead()
        {
            EnsureSchemaComplete();
            var conn = new SqliteConnection(_readConnectionString);
            conn.Open();
            return conn;
        }

        /// <summary>
        /// Check whether every required table exists; if any is missing, (re)create the
        /// full schema. Returns the list of tables that were missing (empty = all present).
        /// Cheap enough to call before reads/writes as a self-healing guard.
        /// </summary>
        public IReadOnlyList<string> EnsureSchemaComplete()
        {
            List<string> missing;
            lock (_writeSync)
            {
                using (var conn = new SqliteConnection(_writeConnectionString))
                {
                    conn.Open();
                    missing = FindMissingTables(conn, RequiredTables);
                }
            }

            if (missing.Count > 0)
            {
                EnsureSchema();
            }
            return missing;
        }

        private static List<string> FindMissingTables(SqliteConnection conn, string[] required)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read()) existing.Add(reader.GetString(0));
            }

            var missing = new List<string>();
            foreach (var t in required)
                if (!existing.Contains(t)) missing.Add(t);
            return missing;
        }

        /// <summary>
        /// Detect and create every table/index if missing. Idempotent.
        /// Called on construction and safe to call again after external DB edits.
        /// </summary>
        public void EnsureSchema()
        {
            lock (_writeSync)
            {
                using (var conn = new SqliteConnection(_writeConnectionString))
                {
                    conn.Open();

                    // Read/write split relies on WAL: writers don't block readers.
                    Exec(conn, "PRAGMA journal_mode=WAL;");
                    Exec(conn, "PRAGMA synchronous=NORMAL;");
                    Exec(conn, "PRAGMA foreign_keys=ON;");

                    CreateContainerTable(conn);
                    CreateBlockTable(conn);
                    CreateBlockSnapshotTable(conn);
                    CreateNetworkLadTable(conn);
                    CreateNetworkSclTable(conn);
                    CreateInterfaceMemberTable(conn);
                    CreateSymbolRefTable(conn);
                    CreateCallEdgeTable(conn);
                    EnsureSchemaMigrations(conn);
                    CreateProjectStateTable(conn);
                    CreateSearchFtsTable(conn);
                }
            }
        }

        /// <summary>
        /// FTS5 full-text index for natural-language intent recall (G-1, P1-5 step 1).
        ///
        /// Scope (intentional): indexes ONLY semantic text fields — block name/summary,
        /// network title/comment/call_summary. It does NOT index identifiers
        /// (canonical_path / block_id / symbol names): full-text on identifiers is pure
        /// waste and degrades ranking. Structured/relational queries (call graph, symbol
        /// refs, type lookups) keep using the fact tables directly and never touch FTS.
        ///
        /// One row per searchable doc; <c>doc_kind</c> distinguishes block vs LAD/SCL
        /// network. Uses <c>unicode61 remove_diacritics 2</c> so German umlauts
        /// (ü→u) and mixed-language text recall correctly. BM25 (SQLite built-in) gives
        /// term-frequency / field-length aware ranking, unlike the LIKE fallback.
        ///
        /// FTS5 is a built-in SQLite module (ships with Microsoft.Data.Sqlite). If a
        /// pathological build lacks it, creation is caught and skipped; search then
        /// falls back to the LIKE path — never fatal.
        /// </summary>
        private static void CreateSearchFtsTable(SqliteConnection conn)
        {
            try
            {
                Exec(conn,
                    "CREATE VIRTUAL TABLE IF NOT EXISTS plc_search_fts USING fts5(" +
                    "project_key UNINDEXED," +   // project isolation filter, not matched
                    "block_id UNINDEXED," +      // links back to fact tables, not matched
                    "doc_kind UNINDEXED," +      // 'block' | 'lad_net' | 'scl_net'
                    "text_body," +               // semantic text (name+summary / title+comment+call_summary)
                    "tokenize = 'unicode61 remove_diacritics 2'" +
                    ");");
            }
            catch (SqliteException ex)
            {
                // FTS5 not compiled in — non-fatal. Search will use the LIKE fallback.
                System.Diagnostics.Debug.WriteLine($"[PlcKnowledgeStore] FTS5 unavailable, skipping full-text index: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether the <c>plc_search_fts</c> virtual table exists and is usable.
        /// Callers use this to decide between the FTS path and the LIKE fallback.
        /// Cheap; safe to call per search.
        /// </summary>
        public static bool IsSearchFtsAvailable(SqliteConnection conn)
        {
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='plc_search_fts' LIMIT 1;";
                    return cmd.ExecuteScalar() != null;
                }
            }
            catch (SqliteException)
            {
                return false;
            }
        }

        private static void CreateContainerTable(SqliteConnection conn)
        {
            Exec(conn,
                "CREATE TABLE IF NOT EXISTS plc_container (" +
                "project_key TEXT NOT NULL," +
                "container_id TEXT NOT NULL," +
                "parent_container_id TEXT," +
                "name TEXT NOT NULL," +
                "kind TEXT NOT NULL," +               // BlockGroup | TypeGroup
                "path TEXT NOT NULL," +
                "depth INTEGER NOT NULL DEFAULT 0," +
                "updated_at TEXT," +
                "PRIMARY KEY(project_key, container_id)" +
                ");");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_container_parent ON plc_container(project_key, parent_container_id);");
        }

        private static void CreateBlockTable(SqliteConnection conn)
        {
            Exec(conn,
                "CREATE TABLE IF NOT EXISTS plc_block (" +
                "project_key TEXT NOT NULL," +
                "block_id TEXT NOT NULL," +            // path key: "FC:Group\\Name"
                "parent_container_id TEXT," +
                "name TEXT NOT NULL," +
                "canonical_name TEXT," +
                "block_type TEXT NOT NULL," +          // OB|FB|FC|DB|UDT|Tag
                "block_number TEXT," +
                "language TEXT," +
                "version_hash TEXT," +
                "revision TEXT," +                      // T11: content revision (sha256) of the indexed XML
                "source_profile TEXT," +                // T11: TIA version the XML was exported from
                "network_count INTEGER DEFAULT 0," +
                "member_count INTEGER DEFAULT 0," +
                "summary TEXT," +
                "parse_status TEXT," +                 // ok|failed|partial
                "failure_reason TEXT," +
                "source_project_path TEXT," +          // XML locator: .ap project path
                "source_block_path TEXT," +            // XML locator: in-project block path
                "updated_at TEXT," +
                "git_branch TEXT," +
                "git_commit TEXT," +
                "PRIMARY KEY(project_key, block_id)" +
                ");");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_block_container ON plc_block(project_key, parent_container_id);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_block_name ON plc_block(project_key, canonical_name);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_block_type ON plc_block(project_key, block_type);");
        }

        /// <summary>
        /// T11: the exact XML export the index row was built from, plus the revision that
        /// identifies it and the block-level attributes that have no other home in the
        /// relational schema.
        ///
        /// Kept out of <c>plc_block</c> on purpose:
        ///   - raw_xml is large (GRAPH/LAD blocks reach megabytes) while plc_block must stay
        ///     a lean "one row per block" completeness anchor that list queries scan;
        ///   - the snapshot is a content artifact (replaced wholesale on every re-index),
        ///     not block metadata.
        /// The revision is the same sha256 revision the read contract exposes
        /// (get_block_content / export_block_ir), so an indexed snapshot and a live export
        /// can be compared without re-parsing.
        /// </summary>
        private static void CreateBlockSnapshotTable(SqliteConnection conn)
        {
            Exec(conn,
                "CREATE TABLE IF NOT EXISTS plc_block_snapshot (" +
                "project_key TEXT NOT NULL," +
                "block_id TEXT NOT NULL," +            // path key, matches plc_block.block_id
                "revision TEXT NOT NULL," +            // sha256 of raw_xml
                "source_profile TEXT," +               // TIA version of the export
                "raw_xml TEXT," +                      // exact export used to build this index
                "block_attributes_json TEXT," +        // block-level attributes (MemoryLayout, SetENOAutomatically, ...)
                "interface_json TEXT," +               // T12: canonical interface projection (DB/UDT; NULL otherwise)
                "indexed_at TEXT," +
                "PRIMARY KEY(project_key, block_id)" +
                ");");
        }

        private static void CreateNetworkLadTable(SqliteConnection conn)
        {
            Exec(conn,
                "CREATE TABLE IF NOT EXISTS plc_network_lad (" +
                "project_key TEXT NOT NULL," +
                "network_id TEXT NOT NULL," +
                "block_id TEXT NOT NULL," +
                "network_order INTEGER NOT NULL," +
                "compile_unit_id TEXT," +
                "title TEXT," +
                "comment TEXT," +
                "inputs TEXT," +                       // all contact variables, delimiter-joined
                "outputs TEXT," +                      // all coil variables, delimiter-joined
                "delimiter TEXT DEFAULT ' | '," +
                "call_summary TEXT," +                 // FB/FC calls (TON/CTU...), delimiter-joined
                "source_text TEXT," +                  // T11: source projection (SCL/STL text; empty for graphical)
                "json_payload TEXT," +                 // full PlcNetworkDocument JSON
                "content_hash TEXT," +
                "source_ref TEXT," +
                "parse_status TEXT," +
                "failure_reason TEXT," +
                "indexed_at TEXT," +
                "PRIMARY KEY(project_key, network_id)" +
                ");");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_lad_block ON plc_network_lad(project_key, block_id, network_order);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_lad_inputs ON plc_network_lad(project_key, inputs);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_lad_outputs ON plc_network_lad(project_key, outputs);");
        }

        private static void CreateNetworkSclTable(SqliteConnection conn)
        {
            Exec(conn,
                "CREATE TABLE IF NOT EXISTS plc_network_scl (" +
                "project_key TEXT NOT NULL," +
                "network_id TEXT NOT NULL," +
                "block_id TEXT NOT NULL," +
                "network_order INTEGER NOT NULL," +
                "compile_unit_id TEXT," +
                "title TEXT," +
                "comment TEXT," +
                "read_vars TEXT," +                    // variables read, delimiter-joined
                "write_vars TEXT," +                   // assignment targets, delimiter-joined
                "delimiter TEXT DEFAULT ' | '," +
                "call_summary TEXT," +
                "source_text TEXT," +                  // raw SCL body (agent reads directly)
                "json_payload TEXT," +
                "content_hash TEXT," +
                "source_ref TEXT," +
                "parse_status TEXT," +
                "failure_reason TEXT," +
                "indexed_at TEXT," +
                "PRIMARY KEY(project_key, network_id)" +
                ");");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_scl_block ON plc_network_scl(project_key, block_id, network_order);");
        }

        private static void CreateInterfaceMemberTable(SqliteConnection conn)
        {
            Exec(conn,
                "CREATE TABLE IF NOT EXISTS plc_interface_member (" +
                "project_key TEXT NOT NULL," +
                "block_id TEXT NOT NULL," +
                "member_id TEXT NOT NULL," +
                "parent_member_id TEXT," +             // self-ref for nested Struct
                "member_path TEXT NOT NULL," +         // full path a.b.c
                "name TEXT NOT NULL," +
                "data_type TEXT," +
                "section TEXT," +                      // Input|Output|InOut|Static|Temp|Constant
                "initial_value TEXT," +
                "comment TEXT," +
                "order_index INTEGER," +
                "PRIMARY KEY(project_key, block_id, member_id)" +
                ");");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_member_block ON plc_interface_member(project_key, block_id);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_member_path ON plc_interface_member(project_key, member_path);");
        }

        private static void CreateSymbolRefTable(SqliteConnection conn)
        {
            Exec(conn,
                "CREATE TABLE IF NOT EXISTS plc_symbol_ref (" +
                "project_key TEXT NOT NULL," +
                "ref_id TEXT NOT NULL," +
                "block_id TEXT NOT NULL," +
                "network_id TEXT," +
                "network_order INTEGER," +
                "canonical_path TEXT NOT NULL," +
                "symbol_name TEXT," +
                "scope TEXT," +
                "direction TEXT," +                    // Read|Write|ReadWrite
                "source_ref TEXT," +
                "PRIMARY KEY(project_key, ref_id)" +
                ");");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_symref_path ON plc_symbol_ref(project_key, canonical_path);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_symref_dir ON plc_symbol_ref(project_key, canonical_path, direction);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_symref_block ON plc_symbol_ref(project_key, block_id);");
        }

        private static void CreateCallEdgeTable(SqliteConnection conn)
        {
            Exec(conn,
                "CREATE TABLE IF NOT EXISTS plc_call_edge (" +
                "project_key TEXT NOT NULL," +
                "edge_id TEXT NOT NULL," +
                "caller_block_id TEXT NOT NULL," +
                "caller_name TEXT," +
                "callee_name TEXT NOT NULL," +
                "callee_block_type TEXT," +
                "instance_path TEXT," +
                "network_id TEXT," +
                "network_order INTEGER," +
                "source_ref TEXT," +
                "callee_resolved INTEGER DEFAULT -1," +  // -1 unreconciled | 1 resolvable to an indexed block | 0 unresolvable (system block/renamed/deleted)
                "PRIMARY KEY(project_key, edge_id)" +
                ");");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_call_callee ON plc_call_edge(project_key, callee_name);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_call_caller ON plc_call_edge(project_key, caller_block_id);");
        }

        /// <summary>
        /// Column migrations for existing databases. CREATE TABLE IF NOT EXISTS never adds
        /// columns to a table that already exists, so every new column must also be applied
        /// with ALTER TABLE (idempotent, guarded by PRAGMA table_info).
        ///
        /// T11: columns added here are plc_block.revision / plc_block.source_profile and
        /// plc_network_lad.source_text; <c>plc_block_snapshot</c> is a new table and is
        /// created directly by CREATE TABLE.
        ///
        /// Dirty marking: a block row indexed before the snapshot era has no revision, so
        /// its snapshot cannot be verified against a live export. Such rows get
        /// version_hash=NULL ("content version unknown"), which makes the next incremental
        /// rebuild re-export them instead of trusting an unverifiable snapshot.
        /// Rows with a revision keep their hash and stay incremental.
        /// </summary>
        private static void EnsureSchemaMigrations(SqliteConnection conn)
        {
            EnsureColumn(conn, "plc_block", "revision", "TEXT");
            EnsureColumn(conn, "plc_block", "source_profile", "TEXT");
            EnsureColumn(conn, "plc_network_lad", "source_text", "TEXT");
            EnsureColumn(conn, "plc_block_snapshot", "interface_json", "TEXT");

            // P2b: appended later than the base schema.
            EnsureColumn(conn, "plc_call_edge", "callee_resolved", "INTEGER DEFAULT -1");

            Exec(conn, "UPDATE plc_block SET version_hash=NULL WHERE revision IS NULL OR revision='';");
        }

        private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
        {
            if (HasColumn(conn, table, column)) return;
            Exec(conn, "ALTER TABLE " + table + " ADD COLUMN " + column + " " + definition + ";");
        }

        private static bool HasColumn(SqliteConnection conn, string table, string column)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(" + table + ");";
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void CreateProjectStateTable(SqliteConnection conn)
        {
            Exec(conn,
                "CREATE TABLE IF NOT EXISTS project_index_state (" +
                "project_key TEXT NOT NULL," +
                "status TEXT," +                       // ready|indexing|stale|missing
                "last_indexed_at TEXT," +
                "last_reason TEXT," +
                "block_count INTEGER DEFAULT 0," +
                "git_branch TEXT," +
                "git_commit TEXT," +
                "updated_at TEXT," +
                "PRIMARY KEY(project_key)" +
                ");");
        }

        private static void Exec(SqliteConnection conn, string sql)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
    }
}
