using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// A single repair experience entry (error -> root cause -> fix), type-level and
    /// symbol-agnostic.
    /// </summary>
    public sealed class RepairExperienceEntry
    {
        public string ProjectKey { get; set; }
        public string ExperienceKey { get; set; }
        public string Symptom { get; set; }
        public string CausalText { get; set; }
        public string Signature { get; set; }
        public string Scope { get; set; }
        public string Hardware { get; set; }
        public double Confidence { get; set; }
        public double Generality { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Owns the auxiliary tables that are NOT part of the v3 layered knowledge store
    /// (<see cref="PlcKnowledgeStore"/>): planning experience, project cache state, and
    /// dirty tracking. Also serves dependency-graph traversal and offline variable
    /// reference lookup by reading the v3 tables (plc_block / plc_call_edge / plc_symbol_ref).
    ///
    /// The old knowledge tables (block_index, plc_block_doc, plc_network_doc,
    /// plc_network_fact, plc_symbol_doc, plc_call_relation, plc_variable_usage,
    /// plc_dataflow_edge) were removed — the layered store supersedes them.
    /// </summary>
    public sealed class ProjectIndexService : IProjectIndexService
    {
        private readonly string _connectionString;
        private readonly object _sync = new object();

        private static readonly string[] RequiredTables =
        {
            "planning_knowledge", "repair_experience", "project_cache_state", "project_dirty_entry"
        };

        public ProjectIndexService(string appBaseDirectory)
        {
            var baseDir = string.IsNullOrWhiteSpace(appBaseDirectory) ? AppDomain.CurrentDomain.BaseDirectory : appBaseDirectory;
            var indexDir = System.IO.Path.Combine(baseDir, "Index");
            System.IO.Directory.CreateDirectory(indexDir);
            var dbPath = System.IO.Path.Combine(indexDir, "project_index.db");
            _connectionString = $"Data Source={dbPath}";
            Initialize();
        }

        /// <summary>
        /// Self-healing guard: if any owned table is missing (e.g. db was deleted or
        /// tampered with externally), recreate the full schema. Cheap; called before
        /// each public operation.
        /// </summary>
        private void EnsureTables()
        {
            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    foreach (var t in RequiredTables)
                    {
                        if (!TableExists(conn, t))
                        {
                            conn.Close();
                            Initialize();
                            return;
                        }
                    }
                }
            }
        }

        private void Initialize()
        {
            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();

                    Exec(conn,
                        "CREATE TABLE IF NOT EXISTS planning_knowledge (" +
                        "project_key TEXT NOT NULL," +
                        "knowledge_key TEXT NOT NULL," +
                        "task_intent TEXT," +
                        "prompt_pattern TEXT," +
                        "target_kinds TEXT," +
                        "used_tools TEXT," +
                        "plan_summary TEXT," +
                        "success INTEGER," +
                        "completed_steps INTEGER DEFAULT 0," +
                        "total_steps INTEGER DEFAULT 0," +
                        "deviation_count INTEGER DEFAULT 0," +
                        "execution_status TEXT," +
                        "success_count INTEGER DEFAULT 0," +
                        "failure_count INTEGER DEFAULT 0," +
                        "updated_at TEXT," +
                        "PRIMARY KEY(project_key, knowledge_key)" +
                        ");");
                    Exec(conn, "CREATE INDEX IF NOT EXISTS idx_planning_project_intent ON planning_knowledge(project_key, task_intent, success);");

                    Exec(conn,
                        "CREATE TABLE IF NOT EXISTS repair_experience (" +
                        "project_key TEXT NOT NULL," +
                        "experience_key TEXT NOT NULL," +
                        "symptom TEXT NOT NULL," +
                        "causal_text TEXT NOT NULL," +
                        "signature TEXT," +
                        "scope TEXT DEFAULT 'project'," +
                        "hardware TEXT," +
                        "confidence REAL DEFAULT 0," +
                        "generality REAL DEFAULT 0," +
                        "success_count INTEGER DEFAULT 0," +
                        "failure_count INTEGER DEFAULT 0," +
                        "updated_at TEXT," +
                        "PRIMARY KEY(project_key, experience_key)" +
                        ");");
                    Exec(conn, "CREATE INDEX IF NOT EXISTS idx_repair_symptom ON repair_experience(project_key, symptom);");
                    // idx_repair_signature is created conditionally in MigrateRepairExperienceColumns
                    // (only after confirming the column exists), to avoid crashing on old databases
                    // that were created before the 'signature' column was added.

                    Exec(conn,
                        "CREATE TABLE IF NOT EXISTS project_cache_state (" +
                        "project_key TEXT NOT NULL," +
                        "status TEXT," +
                        "last_indexed_at TEXT," +
                        "last_refresh_reason TEXT," +
                        "git_branch TEXT," +
                        "git_commit TEXT," +
                        "updated_at TEXT," +
                        "PRIMARY KEY(project_key)" +
                        ");");

                    Exec(conn,
                        "CREATE TABLE IF NOT EXISTS project_dirty_entry (" +
                        "project_key TEXT NOT NULL," +
                        "block_key TEXT NOT NULL," +
                        "reason TEXT," +
                        "updated_at TEXT," +
                        "PRIMARY KEY(project_key, block_key)" +
                        ");");
                    Exec(conn, "CREATE INDEX IF NOT EXISTS idx_project_dirty_updated ON project_dirty_entry(project_key, updated_at);");
                    MigrateRepairExperienceColumns(conn);
                }
            }
        }

        // ---- Block snapshots (plc_block_snapshot) ----

        /// <summary>Read-only snapshot row used by the type-repair flow (raw XML + revision).</summary>
        public sealed class KnowledgeBlockSnapshotRow
        {
            public string ProjectKey { get; set; }
            public string BlockId { get; set; }
            public string Revision { get; set; }
            public string SourceProfile { get; set; }
            public string RawXml { get; set; }
            public string InterfaceJson { get; set; }
        }

        /// <summary>
        /// Block/type ids the index holds a snapshot for. The repair flow uses this as the
        /// candidate set: a block that cannot be exported cannot be discovered any other way.
        /// Returns an empty list when the table is missing (index never built).
        /// </summary>
        public List<string> ListSnapshotBlockIds(string projectKey)
        {
            var ids = new List<string>();
            var pk = NormalizeProjectKey(projectKey);
            if (string.IsNullOrEmpty(pk)) return ids;
            lock (_sync)
            {
                try
                {
                    using (var conn = new SqliteConnection(_connectionString))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText =
                                "SELECT block_id FROM plc_block_snapshot WHERE project_key=@project_key ORDER BY block_id;";
                            cmd.Parameters.AddWithValue("@project_key", pk);
                            using (var reader = cmd.ExecuteReader())
                                while (reader.Read())
                                    ids.Add(reader["block_id"]?.ToString() ?? string.Empty);
                        }
                    }
                }
                catch (SqliteException)
                {
                    // No index yet: the caller degrades to "no candidates".
                }
            }
            return ids;
        }

        /// <summary>The last indexed snapshot of one block/type, or null when none exists.</summary>
        public KnowledgeBlockSnapshotRow GetBlockSnapshot(string projectKey, string blockId)
        {
            var pk = NormalizeProjectKey(projectKey);
            if (string.IsNullOrEmpty(pk) || string.IsNullOrEmpty(blockId)) return null;
            lock (_sync)
            {
                try
                {
                    using (var conn = new SqliteConnection(_connectionString))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText =
                                "SELECT project_key, block_id, revision, source_profile, raw_xml, interface_json " +
                                "FROM plc_block_snapshot WHERE project_key=@project_key AND block_id=@block_id;";
                            cmd.Parameters.AddWithValue("@project_key", pk);
                            cmd.Parameters.AddWithValue("@block_id", blockId);
                            using (var reader = cmd.ExecuteReader())
                            {
                                if (!reader.Read()) return null;
                                return new KnowledgeBlockSnapshotRow
                                {
                                    ProjectKey = reader["project_key"]?.ToString(),
                                    BlockId = reader["block_id"]?.ToString(),
                                    Revision = reader["revision"]?.ToString(),
                                    SourceProfile = reader["source_profile"]?.ToString(),
                                    RawXml = reader["raw_xml"]?.ToString(),
                                    InterfaceJson = reader["interface_json"]?.ToString()
                                };
                            }
                        }
                    }
                }
                catch (SqliteException)
                {
                    return null;
                }
            }
        }

        // ---- Project cache state / dirty tracking ----

        public ProjectKnowledgeCacheStatus GetProjectCacheStatus(string projectKey)
        {
            EnsureTables();
            var pk = NormalizeProjectKey(projectKey);
            var status = new ProjectKnowledgeCacheStatus { ProjectKey = pk };

            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT status, last_indexed_at, last_refresh_reason, git_branch, git_commit " +
                            "FROM project_cache_state WHERE project_key=@project_key;";
                        cmd.Parameters.AddWithValue("@project_key", pk);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                status.Status = reader["status"]?.ToString() ?? status.Status;
                                status.LastIndexedAt = reader["last_indexed_at"]?.ToString() ?? string.Empty;
                                status.Note = reader["last_refresh_reason"]?.ToString() ?? string.Empty;
                                status.GitBranch = reader["git_branch"]?.ToString() ?? string.Empty;
                                status.GitCommit = reader["git_commit"]?.ToString() ?? string.Empty;
                            }
                        }
                    }

                    // v3: block count comes from the layered store's plc_block table.
                    status.IndexedBlockCount = TableExists(conn, "plc_block")
                        ? ExecuteScalarInt(conn, "SELECT COUNT(*) FROM plc_block WHERE project_key=@project_key;", pk)
                        : 0;
                    status.DirtyItemCount = ExecuteScalarInt(conn,
                        "SELECT COUNT(*) FROM project_dirty_entry WHERE project_key=@project_key;", pk);
                }
            }

            status.HasIndex = status.IndexedBlockCount > 0;
            status.IsIndexing = string.Equals(status.Status, "indexing", StringComparison.OrdinalIgnoreCase);
            status.IsStale = status.DirtyItemCount > 0 || string.Equals(status.Status, "stale", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(status.Status))
                status.Status = status.HasIndex ? "ready" : "missing";
            if (!status.HasIndex && !status.IsIndexing && !status.IsStale)
                status.Status = "missing";

            return status;
        }

        public void UpdateProjectCacheState(string projectKey, string status, string lastIndexedAt = null, string gitBranch = "", string gitCommit = "", string reason = "")
        {
            EnsureTables();
            var pk = NormalizeProjectKey(projectKey);
            var updatedAt = DateTime.UtcNow.ToString("o");
            var indexedAt = string.IsNullOrWhiteSpace(lastIndexedAt) ? updatedAt : lastIndexedAt;

            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        UpsertProjectCacheStateCore(conn, tx, pk, status, indexedAt, gitBranch, gitCommit, reason);
                        tx.Commit();
                    }
                }
            }
        }

        public void MarkProjectDirty(string projectKey, string blockKey = null, string reason = "")
        {
            EnsureTables();
            var pk = NormalizeProjectKey(projectKey);
            var dirtyBlockKey = string.IsNullOrWhiteSpace(blockKey) ? "__project__" : blockKey.Trim();
            var updatedAt = DateTime.UtcNow.ToString("o");

            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText =
                                "INSERT INTO project_dirty_entry(project_key, block_key, reason, updated_at) " +
                                "VALUES(@project_key, @block_key, @reason, @updated_at) " +
                                "ON CONFLICT(project_key, block_key) DO UPDATE SET reason=excluded.reason, updated_at=excluded.updated_at;";
                            cmd.Parameters.AddWithValue("@project_key", pk);
                            cmd.Parameters.AddWithValue("@block_key", dirtyBlockKey);
                            cmd.Parameters.AddWithValue("@reason", reason ?? string.Empty);
                            cmd.Parameters.AddWithValue("@updated_at", updatedAt);
                            cmd.ExecuteNonQuery();
                        }
                        UpsertProjectCacheStateCore(conn, tx, pk, "stale", null, null, null, reason);
                        tx.Commit();
                    }
                }
            }
        }

        public void ClearProjectDirty(string projectKey, string blockKey = null)
        {
            EnsureTables();
            var pk = NormalizeProjectKey(projectKey);
            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        ClearProjectDirtyCore(conn, tx, pk, blockKey);
                        tx.Commit();
                    }
                }
            }
        }

        // ---- Planning knowledge ----

        public List<PlanningKnowledgeEntry> SearchPlanningKnowledge(string projectKey, string query, string taskIntent = null, int limit = 3)
        {
            EnsureTables();
            var pk = NormalizeProjectKey(projectKey);
            var tokens = ExtractKeywords(query);
            limit = Math.Max(1, Math.Min(limit <= 0 ? 3 : limit, 20));
            var entries = new List<PlanningKnowledgeEntry>();

            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT project_key, knowledge_key, task_intent, prompt_pattern, target_kinds, used_tools, plan_summary, " +
                            "success, completed_steps, total_steps, deviation_count, execution_status, success_count, failure_count, updated_at " +
                            "FROM planning_knowledge WHERE project_key=@project_key " +
                            "AND (@task_intent='' OR task_intent=@task_intent) " +
                            "ORDER BY success DESC, success_count DESC, updated_at DESC LIMIT @fetch_limit;";
                        cmd.Parameters.AddWithValue("@project_key", pk);
                        cmd.Parameters.AddWithValue("@task_intent", taskIntent ?? string.Empty);
                        cmd.Parameters.AddWithValue("@fetch_limit", Math.Max(limit * 10, 20));
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var entry = ReadPlanningKnowledge(reader);
                                if (tokens.Count == 0 || tokens.Any(t =>
                                    ContainsIgnoreCase(entry.PromptPattern, t) ||
                                    ContainsIgnoreCase(entry.PlanSummary, t) ||
                                    ContainsIgnoreCase(entry.TargetKinds, t) ||
                                    ContainsIgnoreCase(entry.UsedTools, t)))
                                {
                                    entries.Add(entry);
                                }
                            }
                        }
                    }
                }
            }
            return entries.Take(limit).ToList();
        }

        public void RecordPlanningKnowledge(
            string projectKey, string taskIntent, string promptPattern, string targetKinds, string usedTools,
            string planSummary, bool success, int completedSteps, int totalSteps, int deviationCount, string executionStatus)
        {
            EnsureTables();
            var pk = NormalizeProjectKey(projectKey);
            var now = DateTime.UtcNow.ToString("o");
            var knowledgeKey = BuildPlanningKnowledgeKey(taskIntent, targetKinds, usedTools);

            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "INSERT INTO planning_knowledge(" +
                            "project_key, knowledge_key, task_intent, prompt_pattern, target_kinds, used_tools, plan_summary, " +
                            "success, completed_steps, total_steps, deviation_count, execution_status, success_count, failure_count, updated_at) " +
                            "VALUES(@project_key, @knowledge_key, @task_intent, @prompt_pattern, @target_kinds, @used_tools, @plan_summary, " +
                            "@success, @completed_steps, @total_steps, @deviation_count, @execution_status, @success_count, @failure_count, @updated_at) " +
                            "ON CONFLICT(project_key, knowledge_key) DO UPDATE SET " +
                            "prompt_pattern=excluded.prompt_pattern, plan_summary=excluded.plan_summary, " +
                            "completed_steps=excluded.completed_steps, total_steps=excluded.total_steps, " +
                            "deviation_count=excluded.deviation_count, execution_status=excluded.execution_status, " +
                            "success=CASE WHEN (planning_knowledge.success_count + excluded.success_count) > 0 THEN 1 ELSE 0 END, " +
                            "success_count=planning_knowledge.success_count + excluded.success_count, " +
                            "failure_count=planning_knowledge.failure_count + excluded.failure_count, " +
                            "updated_at=excluded.updated_at;";
                        cmd.Parameters.AddWithValue("@project_key", pk);
                        cmd.Parameters.AddWithValue("@knowledge_key", knowledgeKey);
                        cmd.Parameters.AddWithValue("@task_intent", taskIntent ?? string.Empty);
                        cmd.Parameters.AddWithValue("@prompt_pattern", LimitPlanningText(promptPattern, 1000));
                        cmd.Parameters.AddWithValue("@target_kinds", LimitPlanningText(targetKinds, 500));
                        cmd.Parameters.AddWithValue("@used_tools", LimitPlanningText(usedTools, 1000));
                        cmd.Parameters.AddWithValue("@plan_summary", LimitPlanningText(planSummary, 2000));
                        cmd.Parameters.AddWithValue("@success", success ? 1 : 0);
                        cmd.Parameters.AddWithValue("@completed_steps", Math.Max(0, completedSteps));
                        cmd.Parameters.AddWithValue("@total_steps", Math.Max(0, totalSteps));
                        cmd.Parameters.AddWithValue("@deviation_count", Math.Max(0, deviationCount));
                        cmd.Parameters.AddWithValue("@execution_status", executionStatus ?? string.Empty);
                        cmd.Parameters.AddWithValue("@success_count", success ? 1 : 0);
                        cmd.Parameters.AddWithValue("@failure_count", success ? 0 : 1);
                        cmd.Parameters.AddWithValue("@updated_at", now);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        // ---- Repair experience (self-evolving SCL repair knowledge) ----

        public void RecordRepairExperience(string projectKey, string symptom, string causalText, string compilerError, double confidence, double generality)
        {
            EnsureTables();
            var pk = NormalizeProjectKey(projectKey);
            var now = DateTime.UtcNow.ToString("o");
            var rawSignature = NormalizeCompileErrorSignature(compilerError);
            var signature = string.IsNullOrWhiteSpace(rawSignature)
                ? NormalizeCompileErrorSignature(symptom)
                : rawSignature;
            var experienceKey = BuildRepairExperienceKey(signature);

            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "INSERT INTO repair_experience(" +
                            "project_key, experience_key, symptom, causal_text, signature, scope, hardware, confidence, generality, success_count, failure_count, updated_at) " +
                            "VALUES(@project_key, @experience_key, @symptom, @causal_text, @signature, @scope, @hardware, @confidence, @generality, @success_count, @failure_count, @updated_at) " +
                            "ON CONFLICT(project_key, experience_key) DO UPDATE SET " +
                            "symptom=excluded.symptom, causal_text=excluded.causal_text, signature=excluded.signature, confidence=excluded.confidence, generality=excluded.generality, " +
                            "success_count=repair_experience.success_count + excluded.success_count, " +
                            "updated_at=excluded.updated_at;";
                        cmd.Parameters.AddWithValue("@project_key", pk);
                        cmd.Parameters.AddWithValue("@experience_key", experienceKey);
                        cmd.Parameters.AddWithValue("@symptom", LimitPlanningText(symptom, 500));
                        cmd.Parameters.AddWithValue("@causal_text", LimitPlanningText(causalText, 4000));
                        cmd.Parameters.AddWithValue("@signature", signature);
                        cmd.Parameters.AddWithValue("@scope", "project");
                        cmd.Parameters.AddWithValue("@hardware", string.Empty);
                        cmd.Parameters.AddWithValue("@confidence", confidence);
                        cmd.Parameters.AddWithValue("@generality", generality);
                        cmd.Parameters.AddWithValue("@success_count", 1);
                        cmd.Parameters.AddWithValue("@failure_count", 0);
                        cmd.Parameters.AddWithValue("@updated_at", now);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>
        /// Records a real-world outcome for recalled repair experiences: bumps
        /// success_count or failure_count by signature (the normalized abstract error key,
        /// shared across projects). This closes the evaluation feedback loop that gates
        /// recall quality — primary (generality) signal from the LLM, posterior from here.
        /// </summary>
        public void FeedbackRepairExperience(List<string> signatures, bool success)
        {
            if (signatures == null || signatures.Count == 0) return;
            var sigs = signatures
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sigs.Count == 0) return;

            EnsureTables();
            var column = success ? "success_count" : "failure_count";
            var now = DateTime.UtcNow.ToString("o");

            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        foreach (var sig in sigs)
                        {
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = tx;
                                cmd.CommandText =
                                    "UPDATE repair_experience SET " + column + "=" + column + "+1, updated_at=@updated_at " +
                                    "WHERE signature=@signature;";
                                cmd.Parameters.AddWithValue("@updated_at", now);
                                cmd.Parameters.AddWithValue("@signature", sig);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        tx.Commit();
                    }
                }
            }
        }

        public List<RepairExperienceEntry> SearchRepairExperience(string projectKey, string query, int limit = 3)
        {
            EnsureTables();
            var pk = NormalizeProjectKey(projectKey);
            var cap = Math.Max(1, Math.Min(limit <= 0 ? 3 : limit, 20));
            var signature = NormalizeCompileErrorSignature(query);
            var keywords = ExtractKeywords(query);
            var entries = new List<RepairExperienceEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    // Tier 1 — same project first (highest trust: the error pattern is bound
                    // to this project's naming/conventions, so no generality gate here).
                    SearchRepairTier(conn, pk, signature, keywords, cap, sameProject: true, seen, entries);
                    // Tier 2 — cross-project fallback. Gated by the LLM's generality score so
                    // only transferable (project-agnostic) error patterns leak across projects.
                    if (entries.Count < cap)
                    {
                        SearchRepairTier(conn, pk, signature, keywords, cap, sameProject: false, seen, entries);
                    }
                }
            }
            return entries.Take(cap).ToList();
        }

        // Lowest generality (the LLM's 0~1 transferability self-score) at which a repair
        // experience may be reused across projects. Below it the pattern is treated as
        // project-specific and stays within its own project.
        private const double CrossProjectGeneralityMin = 0.5;

        private const string RepairSelectColumns =
            "project_key, experience_key, symptom, causal_text, signature, scope, hardware, " +
            "confidence, generality, success_count, failure_count, updated_at";

        /// <summary>
        /// Runs one recall tier. sameProject tiers are unrestricted; cross-project tiers
        /// are gated by generality so only transferable patterns leak across projects.
        /// Fill order within a tier: exact signature match first, then keyword fallback.
        /// </summary>
        private static void SearchRepairTier(
            SqliteConnection conn, string pk, string signature, List<string> keywords,
            int cap, bool sameProject, HashSet<string> seen, List<RepairExperienceEntry> results)
        {
            if (cap <= 0 || results.Count >= cap) return;

            var projectClause = sameProject ? "project_key=@project_key" : "project_key<>@project_key";
            var orderBy = sameProject
                ? "success_count DESC, confidence DESC, updated_at DESC"
                : "generality DESC, success_count DESC, updated_at DESC";

            // Pass 1: exact normalized-signature match (idx_repair_signature).
            if (!string.IsNullOrEmpty(signature))
            {
                var where = projectClause + " AND signature=@signature"
                    + (sameProject ? "" : " AND generality>=@generality_min");
                AddRepairRows(conn, where, orderBy, pk, signature,
                    withGeneralityGate: !sameProject, keywords, requireKeyword: false,
                    cap, seen, results);
                if (results.Count >= cap) return;
            }

            // Pass 2: keyword fallback over symptom/causal_text for abstracted near-misses.
            var whereFallback = projectClause
                + (sameProject ? "" : " AND generality>=@generality_min");
            AddRepairRows(conn, whereFallback, orderBy, pk, signature: null,
                withGeneralityGate: !sameProject, keywords, requireKeyword: true,
                cap, seen, results);
        }

        private static void AddRepairRows(
            SqliteConnection conn, string where, string orderBy,
            string pk, string signature, bool withGeneralityGate,
            List<string> keywords, bool requireKeyword,
            int cap, HashSet<string> seen, List<RepairExperienceEntry> results)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT " + RepairSelectColumns + " FROM repair_experience WHERE " + where +
                    " ORDER BY " + orderBy + " LIMIT @fetch_limit;";
                cmd.Parameters.AddWithValue("@project_key", pk);
                if (!string.IsNullOrEmpty(signature))
                    cmd.Parameters.AddWithValue("@signature", signature);
                if (withGeneralityGate)
                    cmd.Parameters.AddWithValue("@generality_min", CrossProjectGeneralityMin);
                cmd.Parameters.AddWithValue("@fetch_limit", Math.Max(cap * 10, 20));
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (results.Count >= cap) break;
                        var entry = ReadRepairExperience(reader);
                        if (seen.Contains(entry.ExperienceKey)) continue;
                        if (requireKeyword && keywords.Count > 0 &&
                            !keywords.Any(t =>
                                ContainsIgnoreCase(entry.Symptom, t) ||
                                ContainsIgnoreCase(entry.CausalText, t)))
                        {
                            continue;
                        }
                        if (seen.Add(entry.ExperienceKey)) results.Add(entry);
                    }
                }
            }
        }

        private static string BuildRepairExperienceKey(string symptom)
        {
            var basis = (symptom ?? string.Empty).Trim().ToLowerInvariant();
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(basis));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static RepairExperienceEntry ReadRepairExperience(SqliteDataReader reader)
        {
            DateTime updatedAt;
            double confidence = 0.0;
            double generality = 0.0;
            double.TryParse(reader["confidence"]?.ToString(), out confidence);
            double.TryParse(reader["generality"]?.ToString(), out generality);
            return new RepairExperienceEntry
            {
                ProjectKey = reader["project_key"]?.ToString() ?? "",
                ExperienceKey = reader["experience_key"]?.ToString() ?? "",
                Symptom = reader["symptom"]?.ToString() ?? "",
                CausalText = reader["causal_text"]?.ToString() ?? "",
                Signature = reader["signature"]?.ToString() ?? "",
                Scope = reader["scope"]?.ToString() ?? "project",
                Hardware = reader["hardware"]?.ToString() ?? "",
                Confidence = confidence,
                Generality = generality,
                SuccessCount = ConvertToInt(reader["success_count"]),
                FailureCount = ConvertToInt(reader["failure_count"]),
                UpdatedAt = DateTime.TryParse(reader["updated_at"]?.ToString(), out updatedAt) ? updatedAt : DateTime.MinValue
            };
        }

        // ---- Offline variable reference lookup (reads v3 plc_symbol_ref) ----

        public List<VariableReferenceLocation> QueryVariableReferenceLocations(string projectKey, string canonicalPath)
        {
            var result = new List<VariableReferenceLocation>();
            if (string.IsNullOrWhiteSpace(canonicalPath)) return result;
            var pk = NormalizeProjectKey(projectKey);

            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    if (!TableExists(conn, "plc_symbol_ref")) return result;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT b.name AS block_name, b.block_type, b.language, " +
                            "r.network_order AS network_index, r.direction, r.canonical_path, r.source_ref, " +
                            "COALESCE(l.title, s.title) AS network_title, COALESCE(l.comment, s.comment) AS network_comment " +
                            "FROM plc_symbol_ref r " +
                            "LEFT JOIN plc_block b ON b.project_key=r.project_key AND b.block_id=r.block_id " +
                            "LEFT JOIN plc_network_lad l ON l.project_key=r.project_key AND l.network_id=r.network_id " +
                            "LEFT JOIN plc_network_scl s ON s.project_key=r.project_key AND s.network_id=r.network_id " +
                            "WHERE r.project_key=@project_key AND (r.canonical_path=@path OR r.canonical_path LIKE @path_suffix) " +
                            "ORDER BY b.name, r.network_order LIMIT 100;";
                        cmd.Parameters.AddWithValue("@project_key", pk);
                        cmd.Parameters.AddWithValue("@path", canonicalPath);
                        cmd.Parameters.AddWithValue("@path_suffix", "%." + canonicalPath);
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                result.Add(new VariableReferenceLocation
                                {
                                    BlockName = reader["block_name"]?.ToString() ?? "",
                                    BlockType = reader["block_type"]?.ToString() ?? "",
                                    Language = reader["language"]?.ToString() ?? "",
                                    NetworkIndex = ConvertToInt(reader["network_index"]),
                                    NetworkTitle = reader["network_title"]?.ToString() ?? "",
                                    NetworkComment = reader["network_comment"]?.ToString() ?? "",
                                    Direction = reader["direction"]?.ToString() ?? "",
                                    Kind = "reference",
                                    DisplayText = reader["canonical_path"]?.ToString() ?? "",
                                    SourceRef = reader["source_ref"]?.ToString() ?? ""
                                });
                            }
                        }
                    }
                }
            }
            return result;
        }

        public sealed class VariableReferenceLocation
        {
            public string BlockName { get; set; }
            public string BlockType { get; set; }
            public string Language { get; set; }
            public int NetworkIndex { get; set; }
            public string NetworkTitle { get; set; }
            public string NetworkComment { get; set; }
            public string Direction { get; set; }
            public string Kind { get; set; }
            public string DisplayText { get; set; }
            public string SourceRef { get; set; }
        }

        // ---- Dependency graph traversal (reads v3 plc_call_edge / plc_symbol_ref) ----

        public DependencyChainResult QueryCallChain(string projectKey, string blockName, string direction = "both", int maxDepth = 5, int maxNodeCount = 200)
        {
            var pk = NormalizeProjectKey(projectKey);
            direction = NormalizeDirection(direction);
            maxDepth = Math.Max(1, Math.Min(maxDepth, 20));
            maxNodeCount = Math.Max(1, Math.Min(maxNodeCount, 1000));

            var result = new DependencyChainResult { Root = blockName, Direction = direction };
            if (string.IsNullOrWhiteSpace(blockName)) { result.Note = "blockName is required."; return result; }

            bool truncated = false; int maxDepthReached = 0;
            var cycleBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    LoadCallAdjacency(conn, pk, out var forward, out var reverse, out var typeByName);
                    var exists = forward.ContainsKey(blockName) || reverse.ContainsKey(blockName) || typeByName.ContainsKey(blockName);
                    if (!exists)
                    {
                        result.Note = $"Block '{blockName}' has no call relations in the index (or was not indexed).";
                        return result;
                    }
                    if (direction == "callee" || direction == "both")
                        BfsCallGraph(forward, "callee", new[] { blockName }, typeByName, maxDepth, maxNodeCount, result.Nodes, visited, cycleBlocks, ref truncated, ref maxDepthReached);
                    if ((direction == "caller" || direction == "both") && !truncated)
                        BfsCallGraph(reverse, "caller", new[] { blockName }, typeByName, maxDepth, maxNodeCount, result.Nodes, visited, cycleBlocks, ref truncated, ref maxDepthReached);
                }
            }

            result.TotalNodes = result.Nodes.Count;
            result.MaxDepthReached = maxDepthReached;
            result.WasTruncated = truncated;
            result.CycleBlocks = cycleBlocks.ToList();
            if (result.TotalNodes == 0) result.Note = $"Block '{blockName}' has no {direction} call relations.";
            return result;
        }

        public DependencyChainResult QueryVariableImpact(string projectKey, string canonicalPath, string direction = "both", int maxDepth = 5, int maxNodeCount = 200)
        {
            var pk = NormalizeProjectKey(projectKey);
            direction = NormalizeDirection(direction);
            maxDepth = Math.Max(1, Math.Min(maxDepth, 20));
            maxNodeCount = Math.Max(1, Math.Min(maxNodeCount, 1000));

            var result = new DependencyChainResult { Root = canonicalPath, Direction = direction };
            if (string.IsNullOrWhiteSpace(canonicalPath)) { result.Note = "canonicalPath is required."; return result; }

            bool truncated = false; int maxDepthReached = 0;
            var cycleBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (_sync)
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    if (!TableExists(conn, "plc_symbol_ref")) { result.Note = $"No references found for '{canonicalPath}'."; return result; }

                    var seedBlocks = new List<string>();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT DISTINCT b.name AS block_name, b.block_type, r.network_order AS network_index, r.source_ref, r.direction AS usage_kind " +
                            "FROM plc_symbol_ref r " +
                            "INNER JOIN plc_block b ON b.project_key=r.project_key AND b.block_id=r.block_id " +
                            "WHERE r.project_key=@pk AND (r.canonical_path=@path OR r.canonical_path LIKE @suffix) " +
                            "ORDER BY b.name LIMIT @limit;";
                        cmd.Parameters.AddWithValue("@pk", pk);
                        cmd.Parameters.AddWithValue("@path", canonicalPath.Trim());
                        cmd.Parameters.AddWithValue("@suffix", "%." + canonicalPath.Trim());
                        cmd.Parameters.AddWithValue("@limit", maxNodeCount);
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var bn = reader["block_name"]?.ToString() ?? "";
                                if (string.IsNullOrWhiteSpace(bn)) continue;
                                if (!visited.Add("seed|" + bn)) continue;
                                seedBlocks.Add(bn);
                                result.Nodes.Add(new DependencyChainNode
                                {
                                    BlockName = bn,
                                    BlockType = reader["block_type"]?.ToString() ?? "",
                                    Depth = 0,
                                    Relation = "direct-ref",
                                    NetworkIndex = ConvertToInt(reader["network_index"]),
                                    SourceRef = reader["source_ref"]?.ToString() ?? "",
                                    Path = new List<string> { bn }
                                });
                            }
                        }
                    }

                    if (seedBlocks.Count == 0) { result.Note = $"No references found for '{canonicalPath}'."; return result; }

                    LoadCallAdjacency(conn, pk, out var forward, out var reverse, out var typeByName);
                    if (direction == "callee" || direction == "both")
                        BfsCallGraph(forward, "callee", seedBlocks, typeByName, maxDepth, maxNodeCount, result.Nodes, visited, cycleBlocks, ref truncated, ref maxDepthReached);
                    if ((direction == "caller" || direction == "both") && !truncated)
                        BfsCallGraph(reverse, "caller", seedBlocks, typeByName, maxDepth, maxNodeCount, result.Nodes, visited, cycleBlocks, ref truncated, ref maxDepthReached);
                }
            }

            result.TotalNodes = result.Nodes.Count;
            result.MaxDepthReached = maxDepthReached;
            result.WasTruncated = truncated;
            result.CycleBlocks = cycleBlocks.ToList();
            return result;
        }

        private sealed class CallEdge
        {
            public string Callee;
            public string CalleeType;
            public int NetworkIndex;
            public string SourceRef;
        }

        private void LoadCallAdjacency(SqliteConnection conn, string pk,
            out Dictionary<string, List<CallEdge>> forward,
            out Dictionary<string, List<CallEdge>> reverse,
            out Dictionary<string, string> blockTypeByName)
        {
            forward = new Dictionary<string, List<CallEdge>>(StringComparer.OrdinalIgnoreCase);
            reverse = new Dictionary<string, List<CallEdge>>(StringComparer.OrdinalIgnoreCase);
            blockTypeByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!TableExists(conn, "plc_call_edge")) return;

            // P2b: block name/type come from plc_block as the authoritative source. callee_name is string-matched with no foreign key,
            // so old edges remain after a block is renamed/deleted; deriving names from edges alone would falsely treat a "renamed old block" as existing.
            // Edge derivation is only a type fallback for system blocks (such as TON, which are not in the index).
            if (TableExists(conn, "plc_block"))
            {
                using (var bcmd = conn.CreateCommand())
                {
                    bcmd.CommandText = "SELECT name, block_type FROM plc_block WHERE project_key=@pk;";
                    bcmd.Parameters.AddWithValue("@pk", pk);
                    using (var breader = bcmd.ExecuteReader())
                    {
                        while (breader.Read())
                        {
                            var bn = breader["name"]?.ToString() ?? "";
                            var bt = breader["block_type"]?.ToString() ?? "";
                            if (!string.IsNullOrWhiteSpace(bn) && !blockTypeByName.ContainsKey(bn)) blockTypeByName[bn] = bt;
                        }
                    }
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT e.caller_name AS caller_block_name, b.block_type AS caller_block_type, " +
                    "e.callee_name, e.callee_block_type, e.network_order AS network_index, e.source_ref " +
                    "FROM plc_call_edge e " +
                    "LEFT JOIN plc_block b ON b.project_key=e.project_key AND b.block_id=e.caller_block_id " +
                    "WHERE e.project_key=@pk;";
                cmd.Parameters.AddWithValue("@pk", pk);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var caller = reader["caller_block_name"]?.ToString() ?? "";
                        var callee = reader["callee_name"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(caller) || string.IsNullOrWhiteSpace(callee)) continue;

                        var callerType = reader["caller_block_type"]?.ToString() ?? "";
                        var calleeType = reader["callee_block_type"]?.ToString() ?? "";
                        var netIdx = ConvertToInt(reader["network_index"]);
                        var srcRef = reader["source_ref"]?.ToString() ?? "";

                        if (!string.IsNullOrWhiteSpace(callerType) && !blockTypeByName.ContainsKey(caller)) blockTypeByName[caller] = callerType;
                        if (!string.IsNullOrWhiteSpace(calleeType) && !blockTypeByName.ContainsKey(callee)) blockTypeByName[callee] = calleeType;

                        if (!forward.TryGetValue(caller, out var fwd)) { fwd = new List<CallEdge>(); forward[caller] = fwd; }
                        fwd.Add(new CallEdge { Callee = callee, CalleeType = calleeType, NetworkIndex = netIdx, SourceRef = srcRef });

                        if (!reverse.TryGetValue(callee, out var rev)) { rev = new List<CallEdge>(); reverse[callee] = rev; }
                        rev.Add(new CallEdge { Callee = caller, CalleeType = callerType, NetworkIndex = netIdx, SourceRef = srcRef });
                    }
                }
            }
        }

        private void BfsCallGraph(
            Dictionary<string, List<CallEdge>> adjacency, string relation,
            IEnumerable<string> seeds, Dictionary<string, string> blockTypeByName,
            int maxDepth, int maxNodeCount,
            List<DependencyChainNode> outNodes, HashSet<string> globalVisited,
            HashSet<string> cycleBlocks, ref bool truncated, ref int maxDepthReached)
        {
            var queue = new Queue<(string name, int depth, List<string> path)>();
            foreach (var seed in seeds) queue.Enqueue((seed, 0, new List<string> { seed }));

            while (queue.Count > 0)
            {
                var (current, depth, path) = queue.Dequeue();
                if (depth >= maxDepth) continue;
                if (!adjacency.TryGetValue(current, out var edges)) continue;

                foreach (var edge in edges)
                {
                    if (outNodes.Count >= maxNodeCount) { truncated = true; return; }
                    var next = edge.Callee;
                    if (string.IsNullOrWhiteSpace(next)) continue;
                    if (path.Contains(next, StringComparer.OrdinalIgnoreCase)) { cycleBlocks.Add(next); continue; }

                    var childDepth = depth + 1;
                    var childPath = new List<string>(path) { next };
                    var dedupKey = relation + "|" + next + "|" + childDepth;
                    if (globalVisited.Add(dedupKey))
                    {
                        blockTypeByName.TryGetValue(next, out var bt);
                        outNodes.Add(new DependencyChainNode
                        {
                            BlockName = next,
                            BlockType = bt ?? edge.CalleeType ?? "",
                            Depth = childDepth,
                            Relation = relation,
                            NetworkIndex = edge.NetworkIndex,
                            SourceRef = edge.SourceRef,
                            Path = childPath
                        });
                        if (childDepth > maxDepthReached) maxDepthReached = childDepth;
                    }
                    queue.Enqueue((next, childDepth, childPath));
                }
            }
        }

        // ---- helpers ----

        private static void UpsertProjectCacheStateCore(SqliteConnection conn, SqliteTransaction tx,
            string projectKey, string status, string lastIndexedAt, string gitBranch, string gitCommit, string reason)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO project_cache_state(project_key, status, last_indexed_at, last_refresh_reason, git_branch, git_commit, updated_at) " +
                    "VALUES(@project_key, @status, @last_indexed_at, @last_refresh_reason, @git_branch, @git_commit, @updated_at) " +
                    "ON CONFLICT(project_key) DO UPDATE SET " +
                    "status=excluded.status, " +
                    "last_indexed_at=COALESCE(excluded.last_indexed_at, project_cache_state.last_indexed_at), " +
                    "last_refresh_reason=excluded.last_refresh_reason, " +
                    "git_branch=COALESCE(excluded.git_branch, project_cache_state.git_branch), " +
                    "git_commit=COALESCE(excluded.git_commit, project_cache_state.git_commit), " +
                    "updated_at=excluded.updated_at;";
                cmd.Parameters.AddWithValue("@project_key", projectKey ?? string.Empty);
                cmd.Parameters.AddWithValue("@status", status ?? string.Empty);
                cmd.Parameters.AddWithValue("@last_indexed_at", string.IsNullOrWhiteSpace(lastIndexedAt) ? (object)DBNull.Value : lastIndexedAt);
                cmd.Parameters.AddWithValue("@last_refresh_reason", reason ?? string.Empty);
                cmd.Parameters.AddWithValue("@git_branch", string.IsNullOrWhiteSpace(gitBranch) ? (object)DBNull.Value : gitBranch);
                cmd.Parameters.AddWithValue("@git_commit", string.IsNullOrWhiteSpace(gitCommit) ? (object)DBNull.Value : gitCommit);
                cmd.Parameters.AddWithValue("@updated_at", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
        }

        private static void ClearProjectDirtyCore(SqliteConnection conn, SqliteTransaction tx, string projectKey, string blockKey)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                if (string.IsNullOrWhiteSpace(blockKey))
                {
                    cmd.CommandText = "DELETE FROM project_dirty_entry WHERE project_key=@project_key;";
                    cmd.Parameters.AddWithValue("@project_key", projectKey ?? string.Empty);
                }
                else
                {
                    cmd.CommandText = "DELETE FROM project_dirty_entry WHERE project_key=@project_key AND (block_key=@block_key OR block_key='__project__');";
                    cmd.Parameters.AddWithValue("@project_key", projectKey ?? string.Empty);
                    cmd.Parameters.AddWithValue("@block_key", blockKey.Trim());
                }
                cmd.ExecuteNonQuery();
            }
        }

        private static string BuildPlanningKnowledgeKey(string taskIntent, string targetKinds, string usedTools)
        {
            var basis = string.Join("|",
                (taskIntent ?? string.Empty).Trim().ToLowerInvariant(),
                (targetKinds ?? string.Empty).Trim().ToLowerInvariant(),
                (usedTools ?? string.Empty).Trim().ToLowerInvariant());
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(basis));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static PlanningKnowledgeEntry ReadPlanningKnowledge(SqliteDataReader reader)
        {
            DateTime updatedAt;
            return new PlanningKnowledgeEntry
            {
                ProjectKey = reader["project_key"]?.ToString() ?? "",
                KnowledgeKey = reader["knowledge_key"]?.ToString() ?? "",
                TaskIntent = reader["task_intent"]?.ToString() ?? "",
                PromptPattern = reader["prompt_pattern"]?.ToString() ?? "",
                TargetKinds = reader["target_kinds"]?.ToString() ?? "",
                UsedTools = reader["used_tools"]?.ToString() ?? "",
                PlanSummary = reader["plan_summary"]?.ToString() ?? "",
                Success = ConvertToInt(reader["success"]) != 0,
                CompletedSteps = ConvertToInt(reader["completed_steps"]),
                TotalSteps = ConvertToInt(reader["total_steps"]),
                DeviationCount = ConvertToInt(reader["deviation_count"]),
                ExecutionStatus = reader["execution_status"]?.ToString() ?? "",
                SuccessCount = ConvertToInt(reader["success_count"]),
                FailureCount = ConvertToInt(reader["failure_count"]),
                UpdatedAt = DateTime.TryParse(reader["updated_at"]?.ToString(), out updatedAt) ? updatedAt : DateTime.MinValue
            };
        }

        private static bool ContainsIgnoreCase(string value, string token)
            => !string.IsNullOrWhiteSpace(value) && value.IndexOf(token ?? "", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string LimitPlanningText(string value, int maxLength)
        {
            var text = value ?? string.Empty;
            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }

        private static List<string> ExtractKeywords(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<string>();
            return query
                .Split(new[] { ' ', '\t', '\n', '\r', ',', ';', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }

        private static string NormalizeProjectKey(string projectKey)
            => string.IsNullOrWhiteSpace(projectKey) ? "default-project" : projectKey.Trim();

        // Canonical recall key for a compile error, shared by the record and query paths so
        // the "raw error vs abstracted symptom" vocabulary gap does not prevent recall. Any
        // concrete symbol is replaced, keeping the signature transferable across projects.
        private static string NormalizeCompileErrorSignature(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var text = raw.ToLowerInvariant();
            // Quoted identifiers ('...' / "...") -> <id> (drop concrete block/variable names).
            text = System.Text.RegularExpressions.Regex.Replace(text, @"'[^']*'|\""[^\""]*\""", "<id>");
            // Number literals -> <num>.
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\d+(\.\d+)?", "<num>");
            // Collapse whitespace and trim; cap length to keep the indexed column bounded.
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            const int maxLength = 160;
            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }

        private static string NormalizeDirection(string direction)
        {
            var d = (direction ?? "both").Trim().ToLowerInvariant();
            switch (d)
            {
                case "up": case "upstream": case "caller": case "callers": return "caller";
                case "down": case "downstream": case "callee": case "callees": return "callee";
                default: return "both";
            }
        }

        private static int ExecuteScalarInt(SqliteConnection conn, string sql, string projectKey)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@project_key", projectKey);
                return ConvertToInt(cmd.ExecuteScalar());
            }
        }

        private static bool TableExists(SqliteConnection conn, string tableName)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;";
                cmd.Parameters.AddWithValue("@name", tableName);
                return cmd.ExecuteScalar() != null;
            }
        }

        private static bool ColumnExists(SqliteConnection conn, string tableName, string columnName)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({tableName});";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var name = reader["name"]?.ToString() ?? "";
                        if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            return false;
        }

        // Adds newer repair_experience columns to databases created by an older build
        // (CREATE TABLE IF NOT EXISTS does not add columns to an existing table).
        private static void MigrateRepairExperienceColumns(SqliteConnection conn)
        {
            // If the table was dropped externally, recreate it with the full current schema.
            if (!TableExists(conn, "repair_experience"))
            {
                Exec(conn,
                    "CREATE TABLE repair_experience (" +
                    "project_key TEXT NOT NULL," +
                    "experience_key TEXT NOT NULL," +
                    "symptom TEXT NOT NULL," +
                    "causal_text TEXT NOT NULL," +
                    "signature TEXT," +
                    "scope TEXT DEFAULT 'project'," +
                    "hardware TEXT," +
                    "confidence REAL DEFAULT 0," +
                    "generality REAL DEFAULT 0," +
                    "success_count INTEGER DEFAULT 0," +
                    "failure_count INTEGER DEFAULT 0," +
                    "updated_at TEXT," +
                    "PRIMARY KEY(project_key, experience_key)" +
                    ");");
                Exec(conn, "CREATE INDEX IF NOT EXISTS idx_repair_symptom ON repair_experience(project_key, symptom);");
                Exec(conn, "CREATE INDEX IF NOT EXISTS idx_repair_signature ON repair_experience(project_key, signature);");
                return;
            }

            // Table exists but may have been created by an older build without these columns.
            try
            {
                if (!ColumnExists(conn, "repair_experience", "signature"))
                    Exec(conn, "ALTER TABLE repair_experience ADD COLUMN signature TEXT;");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ProjectIndex] Migration add column 'signature' failed (ignored): {ex.Message}");
            }

            try
            {
                if (!ColumnExists(conn, "repair_experience", "generality"))
                    Exec(conn, "ALTER TABLE repair_experience ADD COLUMN generality REAL DEFAULT 0;");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ProjectIndex] Migration add column 'generality' failed (ignored): {ex.Message}");
            }

            // Only create the index after confirming the column exists.
            try
            {
                if (ColumnExists(conn, "repair_experience", "signature"))
                    Exec(conn, "CREATE INDEX IF NOT EXISTS idx_repair_signature ON repair_experience(project_key, signature);");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ProjectIndex] Migration create index 'idx_repair_signature' failed (ignored): {ex.Message}");
            }
        }

        private static int ConvertToInt(object value)
        {
            if (value == null || value == DBNull.Value) return 0;
            if (value is long l) return (int)l;
            if (value is int i) return i;
            return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
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
