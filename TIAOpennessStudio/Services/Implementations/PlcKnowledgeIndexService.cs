using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// v3 read service over the layered knowledge store (read-only connections, WAL).
    /// Reads the new tables (plc_block / plc_network_lad / plc_network_scl /
    /// plc_symbol_ref / plc_call_edge) and adapts to the existing hit models so
    /// downstream (worker, agent tools, cache service) is unchanged.
    /// </summary>
    public sealed class PlcKnowledgeIndexService : IPlcKnowledgeIndexService
    {
        private readonly PlcKnowledgeStore _store;
        private readonly TIAOpennessStudio.Services.TokenBudget _tokenBudget;

        public PlcKnowledgeIndexService(PlcKnowledgeStore store, TIAOpennessStudio.Services.TokenBudget tokenBudget = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _tokenBudget = tokenBudget ?? new TIAOpennessStudio.Services.TokenBudget();
        }

        public List<PlcKnowledgeSearchHit> Search(string projectKey, string query, int limit = 5)
        {
            var pk = (projectKey ?? "").Trim();
            var tokens = Tokenize(query);
            if (tokens.Count == 0) return new List<PlcKnowledgeSearchHit>();
            limit = Math.Max(1, Math.Min(limit <= 0 ? 5 : limit, 50));

            using (var conn = _store.OpenRead())
            {
                var coverage = new SearchCoverage();

                // P1c: FTS and LIKE are no longer mutually exclusive. FTS (BM25) handles semantic text recall, LIKE handles identifier
                // fragment recall (FTS does not index identifiers). Both paths rank independently, then are interleaved without short-circuiting each other;
                // when FTS is unavailable it degrades to LIKE-only, so retrieval is never broken by a stale DB or a build without FTS5.
                List<PlcKnowledgeSearchHit> ftsHits = null;
                if (PlcKnowledgeStore.IsSearchFtsAvailable(conn))
                {
                    try
                    {
                        ftsHits = SearchViaFts(conn, pk, tokens, limit);
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException)
                    {
                        ftsHits = null; // MATCH syntax/FTS exception → LIKE only
                    }
                }

                var likeHits = SearchViaLike(conn, pk, tokens, limit, coverage);
                var merged = MergeHits(ftsHits, likeHits, limit);
                AttachInterfaceMembers(conn, pk, merged);
                var note = coverage.ToNote();
                if (note.Length > 0 && merged.Count > 0) merged[0].SearchNote = note;
                return merged;
            }
        }

        /// <summary>Interleave the two result sets by their own ranks (FTS first), de-duplicated by BlockKey.</summary>
        private static List<PlcKnowledgeSearchHit> MergeHits(List<PlcKnowledgeSearchHit> ftsHits, List<PlcKnowledgeSearchHit> likeHits, int limit)
        {
            if (ftsHits == null || ftsHits.Count == 0) return likeHits ?? new List<PlcKnowledgeSearchHit>();
            if (likeHits == null || likeHits.Count == 0) return ftsHits;
            var merged = new List<PlcKnowledgeSearchHit>(limit);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i = 0, j = 0;
            while (merged.Count < limit && (i < ftsHits.Count || j < likeHits.Count))
            {
                if (i < ftsHits.Count) { if (seen.Add(ftsHits[i].BlockKey)) merged.Add(ftsHits[i]); i++; }
                if (merged.Count >= limit) break;
                if (j < likeHits.Count) { if (seen.Add(likeHits[j].BlockKey)) merged.Add(likeHits[j]); j++; }
            }
            return merged;
        }

        // ---- Retrieval coverage stats (P1a: eliminate silent LIMIT truncation) ----
        private const int SearchRowBudget = 200;
        private const int PerBlockNetworkCap = 20;

        /// <summary>Per-table "matched total rows vs actually returned rows". matched=-1 means that table was not counted.</summary>
        private sealed class SearchCoverage
        {
            public int BlocksMatched = -1, BlocksReturned;
            public int LadMatched = -1, LadReturned;
            public int SclMatched = -1, SclReturned;
            public int CallsMatched = -1, CallsReturned;

            private static string Seg(string name, int matched, int returned)
            {
                if (matched < 0) return name + "=" + returned;
                return matched > returned
                    ? name + "=" + returned + "/" + matched + "(截断)"
                    : name + "=" + returned + "/" + matched;
            }

            public string ToNote()
            {
                if (BlocksMatched < 0 && LadMatched < 0 && SclMatched < 0 && CallsMatched < 0) return "";
                var truncated =
                    (BlocksMatched >= 0 && BlocksMatched > BlocksReturned) ||
                    (LadMatched >= 0 && LadMatched > LadReturned) ||
                    (SclMatched >= 0 && SclMatched > SclReturned) ||
                    (CallsMatched >= 0 && CallsMatched > CallsReturned);
                var note = "检索覆盖(返回/匹配): " + Seg("blocks", BlocksMatched, BlocksReturned) + ", "
                    + Seg("lad", LadMatched, LadReturned) + ", "
                    + Seg("scl", SclMatched, SclReturned) + ", "
                    + Seg("calls", CallsMatched, CallsReturned);
                if (truncated)
                    note += "。结果不完整: 已按相关性排序、每块网络行上限 " + PerBlockNetworkCap + " 条；" +
                        "请用更精确的关键词缩小范围，或用 get_block_content 逐块取全量最新代码";
                return note;
            }
        }

        /// <summary>
        /// FTS5 (BM25) recall over semantic text docs (block name/summary + network
        /// title/comment). Returns block-aggregated hits enriched with full detail via
        /// the shared network/call readers, so the downstream hit contract is unchanged.
        /// </summary>
        private List<PlcKnowledgeSearchHit> SearchViaFts(SqliteConnection conn, string pk, List<string> tokens, int limit)
        {
            var match = BuildFtsMatch(tokens);
            if (string.IsNullOrWhiteSpace(match)) return new List<PlcKnowledgeSearchHit>();

            // Best (lowest) bm25 rank per block; fetch a wider net than `limit` so
            // detail enrichment + final ordering has room.
            var ranked = new List<(string BlockId, double Rank)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT block_id, bm25(plc_search_fts) AS rank " +
                    "FROM plc_search_fts " +
                    "WHERE project_key=@pk AND plc_search_fts MATCH @q " +
                    "ORDER BY rank LIMIT @fetch;";
                cmd.Parameters.AddWithValue("@pk", pk);
                cmd.Parameters.AddWithValue("@q", match);
                cmd.Parameters.AddWithValue("@fetch", Math.Max(limit * 5, 25));
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var blockId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        if (string.IsNullOrEmpty(blockId) || !seen.Add(blockId)) continue; // first = best rank
                        var rank = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
                        ranked.Add((blockId, rank));
                    }
                }
            }
            if (ranked.Count == 0) return new List<PlcKnowledgeSearchHit>();

            // Enrich each matched block with its row + networks + calls (reuse readers).
            var hits = new Dictionary<string, PlcKnowledgeSearchHit>(StringComparer.OrdinalIgnoreCase);
            var blockIds = ranked.Select(r => r.BlockId).Take(Math.Max(limit * 3, 15)).ToList();
            LoadBlocksByIds(conn, pk, blockIds, hits);
            LoadNetworksByBlockIds(conn, pk, blockIds, hits, "LAD");
            LoadNetworksByBlockIds(conn, pk, blockIds, hits, "SCL");
            LoadCallsByBlockIds(conn, pk, blockIds, hits);

            // BM25: lower = more relevant. Convert to a descending score for the hit model
            // (keeps the existing "higher Score = better" convention downstream).
            var rankByBlock = ranked.ToDictionary(r => r.BlockId, r => r.Rank, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in hits)
            {
                var rank = rankByBlock.TryGetValue(kv.Key, out var rv) ? rv : 0.0;
                kv.Value.Score = (int)Math.Round(10000.0 / (1.0 + Math.Abs(rank)));
            }

            return hits.Values
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.UpdatedAt, StringComparer.Ordinal)
                .Take(limit)
                .ToList();
        }

        /// <summary>Legacy LIKE-based recall (always runs; merged with FTS hits by caller).</summary>
        private List<PlcKnowledgeSearchHit> SearchViaLike(SqliteConnection conn, string pk, List<string> tokens, int limit, SearchCoverage coverage)
        {
            var hits = new Dictionary<string, PlcKnowledgeSearchHit>(StringComparer.OrdinalIgnoreCase);
            SearchBlocks(conn, pk, tokens, hits, coverage);
            SearchLadNetworks(conn, pk, tokens, hits, coverage);
            SearchSclNetworks(conn, pk, tokens, hits, coverage);
            SearchCalls(conn, pk, tokens, hits, coverage);

            return hits.Values
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.UpdatedAt, StringComparer.Ordinal)
                .Take(limit)
                .ToList();
        }

        /// <summary>
        /// Build an FTS5 MATCH expression from tokens: each token as a prefix query,
        /// OR-combined for recall. Tokens are quoted to neutralize FTS5 operator chars
        /// (so user input can't inject MATCH syntax).
        /// </summary>
        private static string BuildFtsMatch(List<string> tokens)
        {
            var parts = new List<string>();
            foreach (var t in tokens)
            {
                var cleaned = t.Replace("\"", " ").Trim();
                if (cleaned.Length < 1) continue;
                // "token"* = quoted (literal) prefix match; quoting blocks FTS operators.
                parts.Add("\"" + cleaned + "\"*");
            }
            return string.Join(" OR ", parts);
        }

        public ContextPack BuildContextPack(string projectKey, string query, string rid = null, int topK = 5)
        {
            var pack = new ContextPack
            {
                Rid = string.IsNullOrWhiteSpace(rid) ? Guid.NewGuid().ToString("N").Substring(0, 8) : rid,
                CreatedAt = DateTime.UtcNow
            };

            var hits = Search(projectKey, query, topK);
            var searchNote = hits.Count > 0 ? hits[0].SearchNote : null;
            foreach (var hit in hits)
            {
                var content = FormatHit(hit);
                pack.CodeFragments.Add(new CodeFragment
                {
                    BlockName = hit.BlockName,
                    BlockType = hit.BlockType,
                    Language = hit.Language,
                    Summary = hit.Summary,
                    Content = content,
                    Source = hit.MatchedNetworks.Count > 0 ? "plc-network-evidence" : "plc-knowledge",
                    RelevanceScore = hit.Score,
                    TokenEstimate = _tokenBudget.EstimateTokens(content)
                });
            }

            pack.Metadata["knowledge_source"] = "plc-index-v3";
            pack.Metadata["query"] = query ?? string.Empty;
            if (!string.IsNullOrEmpty(searchNote)) pack.Metadata["search_coverage"] = searchNote;
            pack.TotalTokenEstimate = _tokenBudget.EstimateTokens(pack.ToPromptString());
            return pack;
        }

        public PlcVariableReferenceResult QueryVariableReferences(string projectKey, string variablePath,
            string direction = "both", int topK = 20, bool includeDefinitions = true)
        {
            var pk = (projectKey ?? "").Trim();
            var dir = string.IsNullOrWhiteSpace(direction) ? "both" : direction.Trim().ToLowerInvariant();
            var result = new PlcVariableReferenceResult
            {
                VariablePath = variablePath,
                Direction = dir,
                Precision = "symbol-reference"
            };
            if (string.IsNullOrWhiteSpace(variablePath)) { result.Note = "variable_path is required."; return result; }

            topK = Math.Max(1, Math.Min(topK <= 0 ? 20 : topK, 200));
            var symbolName = LastSegment(variablePath);

            var dirClause = dir == "read" ? " AND (r.direction='Read' OR r.direction='ReadWrite')"
                          : dir == "write" ? " AND (r.direction='Write' OR r.direction='ReadWrite')"
                          : "";
            var matchClause = "(r.canonical_path=@path OR r.canonical_path LIKE @suffix OR r.symbol_name=@name)";

            // P1a: true total matched rows (not affected by fetch/topK truncation), making truncation visible to the Agent.
            int totalRows;
            using (var conn = _store.OpenRead())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM plc_symbol_ref r WHERE r.project_key=@pk AND " + matchClause + dirClause + ";";
                cmd.Parameters.AddWithValue("@pk", pk);
                cmd.Parameters.AddWithValue("@path", variablePath);
                cmd.Parameters.AddWithValue("@suffix", "%." + variablePath);
                cmd.Parameters.AddWithValue("@name", symbolName);
                var v = cmd.ExecuteScalar();
                totalRows = v == null ? 0 : Convert.ToInt32(v);
            }

            var hits = new List<PlcVariableReferenceHit>();
            using (var conn = _store.OpenRead())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT r.block_id, r.network_id, r.network_order, r.canonical_path, r.symbol_name, r.scope, r.direction, r.source_ref, " +
                    "b.name AS block_name, b.block_type, b.language " +
                    "FROM plc_symbol_ref r " +
                    "LEFT JOIN plc_block b ON b.project_key=r.project_key AND b.block_id=r.block_id " +
                    "WHERE r.project_key=@pk AND " + matchClause +
                    dirClause + " LIMIT @fetch;";
                cmd.Parameters.AddWithValue("@pk", pk);
                cmd.Parameters.AddWithValue("@path", variablePath);
                cmd.Parameters.AddWithValue("@suffix", "%." + variablePath);
                cmd.Parameters.AddWithValue("@name", symbolName);
                cmd.Parameters.AddWithValue("@fetch", Math.Max(topK * 5, 40));
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        hits.Add(new PlcVariableReferenceHit
                        {
                            ProjectKey = pk,
                            BlockKey = Str(reader, "block_id"),
                            BlockName = Str(reader, "block_name"),
                            BlockType = Str(reader, "block_type"),
                            Language = Str(reader, "language"),
                            CanonicalPath = Str(reader, "canonical_path"),
                            SymbolName = Str(reader, "symbol_name"),
                            Scope = Str(reader, "scope"),
                            UsageKind = Str(reader, "direction"),
                            NetworkIndex = Int(reader, "network_order"),
                            SourceRef = Str(reader, "source_ref")
                        });
                    }
                }
            }

            result.Hits = hits
                .GroupBy(h => string.Join("|", h.BlockKey, h.CanonicalPath, h.NetworkIndex, h.SourceRef))
                .Select(g => g.First())
                .Take(topK)
                .ToList();
            // TotalHits = true matched rows in the DB (before truncation); Hits.Count = returned count after de-dup + topK.
            result.TotalHits = totalRows;
            if (totalRows > result.Hits.Count)
                result.Note = $"共匹配 {totalRows} 行引用记录，去重后返回 {result.Hits.Count} 条（结果不完整，可调大 top_k 重查）";
            return result;
        }

        // ---- T13: interface evidence for interface-bearing blocks ----

        /// <summary>Block types whose interface is indexed into plc_interface_member.</summary>
        private static readonly HashSet<string> InterfaceBlockTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DB", "UDT", "FB", "FC" };

        /// <summary>
        /// Attach interface members to every hit whose block type has an interface.
        /// DB/UDT carry no networks, so without this partition a DB/UDT hit would contain
        /// only its header line and the Agent would have to call export_block for structure
        /// that is already indexed. Loaded once for the whole result set (one query).
        /// </summary>
        private static void AttachInterfaceMembers(SqliteConnection conn, string pk, List<PlcKnowledgeSearchHit> hits)
        {
            if (hits == null || hits.Count == 0) return;

            var blockIds = hits
                .Where(h => InterfaceBlockTypes.Contains(h.BlockType ?? ""))
                .Select(h => h.BlockKey)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (blockIds.Count == 0) return;

            var byBlock = new Dictionary<string, List<PlcInterfaceMemberDoc>>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                var inClause = BuildInClause(cmd, blockIds);
                cmd.CommandText =
                    "SELECT block_id, member_path, name, data_type, section, initial_value, comment, order_index " +
                    "FROM plc_interface_member " +
                    "WHERE project_key=@pk AND block_id IN (" + inClause + ") " +
                    "ORDER BY block_id, order_index;";
                cmd.Parameters.AddWithValue("@pk", pk);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var blockId = Str(reader, "block_id");
                        if (!byBlock.TryGetValue(blockId, out var members))
                        {
                            members = new List<PlcInterfaceMemberDoc>();
                            byBlock[blockId] = members;
                        }
                        members.Add(new PlcInterfaceMemberDoc
                        {
                            BlockKey = blockId,
                            MemberPath = Str(reader, "member_path"),
                            Name = Str(reader, "name"),
                            DataType = Str(reader, "data_type"),
                            Section = Str(reader, "section"),
                            InitialValue = Str(reader, "initial_value"),
                            Comment = Str(reader, "comment"),
                            OrderIndex = Int(reader, "order_index")
                        });
                    }
                }
            }

            foreach (var hit in hits)
            {
                if (byBlock.TryGetValue(hit.BlockKey ?? "", out var members))
                    hit.MatchedMembers = members;
            }
        }

        // ---- searchers ----

        private void SearchBlocks(SqliteConnection conn, string pk, List<string> tokens, Dictionary<string, PlcKnowledgeSearchHit> hits, SearchCoverage coverage)
        {
            // P1b/P2a: count true matches to make truncation visible; block_number is included in the match columns so FC123/FB100 are queryable by block number.
            var cols = new[] { "name", "canonical_name", "summary", "block_number" };
            coverage.BlocksMatched = CountMatching(conn, pk, tokens, "plc_block", "project_key", cols);
            using (var cmd = conn.CreateCommand())
            {
                var where = BuildWhere(cmd, tokens, cols);
                cmd.CommandText =
                    "SELECT block_id, name, block_type, language, version_hash, summary, updated_at " +
                    "FROM plc_block WHERE project_key=@pk AND (" + where + ") LIMIT " + SearchRowBudget + ";";
                cmd.Parameters.AddWithValue("@pk", pk);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        coverage.BlocksReturned++;
                        var hit = GetOrCreate(hits, Str(reader, "block_id"), reader);
                        hit.Score += ScoreText(Str(reader, "name"), tokens, 80);
                        hit.Score += ScoreText(Str(reader, "summary"), tokens, 20);
                    }
                }
            }
        }

        private void SearchLadNetworks(SqliteConnection conn, string pk, List<string> tokens, Dictionary<string, PlcKnowledgeSearchHit> hits, SearchCoverage coverage)
            => SearchNetworks(conn, pk, tokens, hits, coverage, "LAD");

        private void SearchSclNetworks(SqliteConnection conn, string pk, List<string> tokens, Dictionary<string, PlcKnowledgeSearchHit> hits, SearchCoverage coverage)
            => SearchNetworks(conn, pk, tokens, hits, coverage, "SCL");

        /// <summary>
        /// P1b: network-row search. The main path uses window functions (SQLite 3.25+) to do it in one pass: relevance ordering (by token×column
        /// weighted hit count, replacing the old updated_at time order) + per-block row quota (a single block no longer consumes the whole budget) +
        /// COUNT(*) OVER() for the true total match count (truncation visible). Builds without window functions fall back to the old query + a separate COUNT.
        /// </summary>
        private void SearchNetworks(SqliteConnection conn, string pk, List<string> tokens, Dictionary<string, PlcKnowledgeSearchHit> hits, SearchCoverage coverage, string lang)
        {
            var table = lang == "LAD" ? "plc_network_lad" : "plc_network_scl";
            var varCols = lang == "LAD"
                ? "n.inputs, n.outputs, n.source_text, n.json_payload"
                : "n.read_vars AS inputs, n.write_vars AS outputs, n.source_text, n.json_payload";
            var matchCols = lang == "LAD"
                ? new[] { "n.title", "n.comment", "n.inputs", "n.outputs", "n.call_summary" }
                : new[] { "n.title", "n.comment", "n.read_vars", "n.write_vars", "n.call_summary" };
            var weighted = lang == "LAD"
                ? new[] { ("n.title", 3), ("n.comment", 1), ("n.inputs", 2), ("n.outputs", 2), ("n.call_summary", 1) }
                : new[] { ("n.title", 3), ("n.comment", 1), ("n.read_vars", 2), ("n.write_vars", 2), ("n.call_summary", 1) };
            var fromJoin = table + " n INNER JOIN plc_block b ON b.project_key=n.project_key AND b.block_id=n.block_id";

            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    var where = BuildWhere(cmd, tokens, matchCols);
                    var rel = BuildRelevanceExpr(tokens.Count, weighted);
                    cmd.CommandText =
                        "SELECT * FROM (SELECT b.block_id, b.name, b.block_type, b.language, b.summary, b.updated_at, " +
                        "n.network_id, n.network_order, n.compile_unit_id, n.title, n.comment, " + varCols + ", n.source_ref, " +
                        rel + " AS rel, " +
                        "ROW_NUMBER() OVER (PARTITION BY n.block_id ORDER BY " + rel + " DESC, n.network_order) AS rn, " +
                        "COUNT(*) OVER () AS total_matches " +
                        "FROM " + fromJoin + " WHERE n.project_key=@pk AND (" + where + ")) " +
                        "WHERE rn <= " + PerBlockNetworkCap + " ORDER BY rel DESC, block_id, network_order LIMIT " + SearchRowBudget + ";";
                    cmd.Parameters.AddWithValue("@pk", pk);
                    ReadNetworkRows(cmd, pk, tokens, hits, lang, coverage);
                    return;
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // Window functions unavailable → fall back to the old query; total matches are filled by a separate COUNT, retrieval is not interrupted.
            }

            var matchedFallback = CountMatching(conn, pk, tokens, fromJoin, "n.project_key", matchCols);
            if (lang == "LAD") coverage.LadMatched = matchedFallback; else coverage.SclMatched = matchedFallback;
            using (var cmd = conn.CreateCommand())
            {
                var where = BuildWhere(cmd, tokens, matchCols);
                cmd.CommandText =
                    "SELECT b.block_id, b.name, b.block_type, b.language, b.summary, b.updated_at, " +
                    "n.network_id, n.network_order, n.compile_unit_id, n.title, n.comment, " + varCols + ", n.source_ref " +
                    "FROM " + fromJoin + " WHERE n.project_key=@pk AND (" + where + ") ORDER BY b.updated_at DESC, n.network_order LIMIT " + SearchRowBudget + ";";
                cmd.Parameters.AddWithValue("@pk", pk);
                ReadNetworkRows(cmd, pk, tokens, hits, lang, coverage);
            }
        }

        /// <summary>token×column weighted-hit expression (reuses the @t{i} parameters already bound by BuildWhere).</summary>
        private static string BuildRelevanceExpr(int tokenCount, params (string Col, int Weight)[] cols)
        {
            var terms = new List<string>();
            for (int i = 0; i < tokenCount; i++)
                foreach (var (col, w) in cols)
                    terms.Add("CASE WHEN " + col + " LIKE @t" + i + " THEN " + w + " ELSE 0 END");
            return terms.Count == 0 ? "0" : string.Join(" + ", terms);
        }

        /// <summary>True matched row count with the same WHERE as the search (not affected by LIMIT).</summary>
        private static int CountMatching(SqliteConnection conn, string pk, List<string> tokens, string fromClause, string pkColumn, params string[] columns)
        {
            using (var cmd = conn.CreateCommand())
            {
                var where = BuildWhere(cmd, tokens, columns);
                cmd.CommandText = "SELECT COUNT(*) FROM " + fromClause + " WHERE " + pkColumn + "=@pk AND (" + where + ");";
                cmd.Parameters.AddWithValue("@pk", pk);
                var v = cmd.ExecuteScalar();
                return v == null ? 0 : Convert.ToInt32(v);
            }
        }

        private void ReadNetworkRows(SqliteCommand cmd, string pk, List<string> tokens, Dictionary<string, PlcKnowledgeSearchHit> hits, string lang, SearchCoverage coverage)
        {
            int returned = 0, matched = -1;
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (matched < 0 && HasColumn(reader, "total_matches")) matched = Int(reader, "total_matches");
                    returned++;
                    var hit = GetOrCreate(hits, Str(reader, "block_id"), reader);
                    var net = new PlcNetworkDoc
                    {
                        ProjectKey = pk,
                        BlockKey = Str(reader, "block_id"),
                        NetworkId = Str(reader, "network_id"),
                        NetworkIndex = Int(reader, "network_order"),
                        CompileUnitId = Str(reader, "compile_unit_id"),
                        // Report the real block language (FBD/STL/GRAPH included); the table itself
                        // only splits text (SCL) from graphical networks.
                        Language = HasColumn(reader, "language") ? Str(reader, "language") : lang,
                        Title = Str(reader, "title"),
                        Comment = Str(reader, "comment"),
                        Inputs = Str(reader, "inputs"),
                        Outputs = Str(reader, "outputs"),
                        SourceText = HasColumn(reader, "source_text") ? Str(reader, "source_text") : "",
                        JsonPayload = HasColumn(reader, "json_payload") ? Str(reader, "json_payload") : "",
                        SourceRef = Str(reader, "source_ref")
                    };
                    hit.MatchedNetworks.Add(net);
                    hit.Score += ScoreText(net.Title, tokens, 60);
                    hit.Score += ScoreText(Str(reader, "inputs"), tokens, 30);
                    hit.Score += ScoreText(Str(reader, "outputs"), tokens, 30);
                }
            }
            if (lang == "LAD") { if (matched >= 0) coverage.LadMatched = matched; coverage.LadReturned = returned; }
            else { if (matched >= 0) coverage.SclMatched = matched; coverage.SclReturned = returned; }
        }

        private void SearchCalls(SqliteConnection conn, string pk, List<string> tokens, Dictionary<string, PlcKnowledgeSearchHit> hits, SearchCoverage coverage)
        {
            var cols = new[] { "e.callee_name", "e.instance_path" };
            var fromJoin = "plc_call_edge e INNER JOIN plc_block b ON b.project_key=e.project_key AND b.block_id=e.caller_block_id";
            coverage.CallsMatched = CountMatching(conn, pk, tokens, fromJoin, "e.project_key", cols);
            using (var cmd = conn.CreateCommand())
            {
                var where = BuildWhere(cmd, tokens, cols);
                cmd.CommandText =
                    "SELECT b.block_id, b.name, b.block_type, b.language, b.summary, b.updated_at, " +
                    "e.callee_name, e.callee_block_type, e.instance_path, e.network_order " +
                    "FROM " + fromJoin + " " +
                    "WHERE e.project_key=@pk AND (" + where + ") LIMIT " + SearchRowBudget + ";";
                cmd.Parameters.AddWithValue("@pk", pk);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        coverage.CallsReturned++;
                        var hit = GetOrCreate(hits, Str(reader, "block_id"), reader);
                        hit.MatchedCalls.Add(new PlcCallRelation
                        {
                            ProjectKey = pk,
                            CallerBlockKey = Str(reader, "block_id"),
                            CallerBlockName = Str(reader, "name"),
                            CalleeName = Str(reader, "callee_name"),
                            CalleeBlockType = Str(reader, "callee_block_type"),
                            InstancePath = Str(reader, "instance_path"),
                            NetworkIndex = Int(reader, "network_order")
                        });
                        hit.Score += ScoreText(Str(reader, "callee_name"), tokens, 50);
                    }
                }
            }
        }

        // ---- FTS detail loaders (fetch by explicit block_id list, not LIKE) ----

        private static string BuildInClause(SqliteCommand cmd, IReadOnlyList<string> blockIds)
        {
            var ph = new List<string>(blockIds.Count);
            for (int i = 0; i < blockIds.Count; i++)
            {
                var p = "@b" + i;
                cmd.Parameters.AddWithValue(p, blockIds[i]);
                ph.Add(p);
            }
            return string.Join(",", ph);
        }

        private void LoadBlocksByIds(SqliteConnection conn, string pk, IReadOnlyList<string> blockIds, Dictionary<string, PlcKnowledgeSearchHit> hits)
        {
            if (blockIds.Count == 0) return;
            using (var cmd = conn.CreateCommand())
            {
                var inClause = BuildInClause(cmd, blockIds);
                cmd.CommandText =
                    "SELECT block_id, name, block_type, language, version_hash, summary, updated_at " +
                    "FROM plc_block WHERE project_key=@pk AND block_id IN (" + inClause + ");";
                cmd.Parameters.AddWithValue("@pk", pk);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        GetOrCreate(hits, Str(reader, "block_id"), reader);
            }
        }

        private void LoadNetworksByBlockIds(SqliteConnection conn, string pk, IReadOnlyList<string> blockIds, Dictionary<string, PlcKnowledgeSearchHit> hits, string lang)
        {
            if (blockIds.Count == 0) return;
            var table = lang == "LAD" ? "plc_network_lad" : "plc_network_scl";
            var varCols = lang == "LAD"
                ? "n.inputs, n.outputs, n.source_text, n.json_payload"
                : "n.read_vars AS inputs, n.write_vars AS outputs, n.source_text, n.json_payload";
            using (var cmd = conn.CreateCommand())
            {
                var inClause = BuildInClause(cmd, blockIds);
                cmd.CommandText =
                    "SELECT b.block_id, b.name, b.block_type, b.language, b.summary, b.updated_at, " +
                    "n.network_id, n.network_order, n.compile_unit_id, n.title, n.comment, " + varCols + ", n.source_ref " +
                    "FROM " + table + " n INNER JOIN plc_block b ON b.project_key=n.project_key AND b.block_id=n.block_id " +
                    "WHERE n.project_key=@pk AND n.block_id IN (" + inClause + ") ORDER BY n.network_order;";
                cmd.Parameters.AddWithValue("@pk", pk);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var hit = GetOrCreate(hits, Str(reader, "block_id"), reader);
                        hit.MatchedNetworks.Add(new PlcNetworkDoc
                        {
                            ProjectKey = pk,
                            BlockKey = Str(reader, "block_id"),
                            NetworkId = Str(reader, "network_id"),
                            NetworkIndex = Int(reader, "network_order"),
                            CompileUnitId = Str(reader, "compile_unit_id"),
                            Language = HasColumn(reader, "language") ? Str(reader, "language") : lang,
                            Title = Str(reader, "title"),
                            Comment = Str(reader, "comment"),
                            Inputs = Str(reader, "inputs"),
                            Outputs = Str(reader, "outputs"),
                            SourceText = Str(reader, "source_text"),
                            JsonPayload = Str(reader, "json_payload"),
                            SourceRef = Str(reader, "source_ref")
                        });
                    }
                }
            }
        }

        private void LoadCallsByBlockIds(SqliteConnection conn, string pk, IReadOnlyList<string> blockIds, Dictionary<string, PlcKnowledgeSearchHit> hits)
        {
            if (blockIds.Count == 0) return;
            using (var cmd = conn.CreateCommand())
            {
                var inClause = BuildInClause(cmd, blockIds);
                cmd.CommandText =
                    "SELECT b.block_id, b.name, b.block_type, b.language, b.summary, b.updated_at, " +
                    "e.callee_name, e.callee_block_type, e.instance_path, e.network_order " +
                    "FROM plc_call_edge e INNER JOIN plc_block b ON b.project_key=e.project_key AND b.block_id=e.caller_block_id " +
                    "WHERE e.project_key=@pk AND e.caller_block_id IN (" + inClause + ");";
                cmd.Parameters.AddWithValue("@pk", pk);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var hit = GetOrCreate(hits, Str(reader, "block_id"), reader);
                        hit.MatchedCalls.Add(new PlcCallRelation
                        {
                            ProjectKey = pk,
                            CallerBlockKey = Str(reader, "block_id"),
                            CallerBlockName = Str(reader, "name"),
                            CalleeName = Str(reader, "callee_name"),
                            CalleeBlockType = Str(reader, "callee_block_type"),
                            InstancePath = Str(reader, "instance_path"),
                            NetworkIndex = Int(reader, "network_order")
                        });
                    }
                }
            }
        }

        // ---- helpers ----

        private static PlcKnowledgeSearchHit GetOrCreate(Dictionary<string, PlcKnowledgeSearchHit> hits, string blockId, SqliteDataReader reader)
        {
            if (!hits.TryGetValue(blockId, out var hit))
            {
                hit = new PlcKnowledgeSearchHit
                {
                    BlockKey = blockId,
                    BlockName = Str(reader, "name"),
                    BlockType = Str(reader, "block_type"),
                    Language = Str(reader, "language"),
                    Summary = HasColumn(reader, "summary") ? Str(reader, "summary") : "",
                    UpdatedAt = HasColumn(reader, "updated_at") ? Str(reader, "updated_at") : ""
                };
                hits[blockId] = hit;
            }
            return hit;
        }

        private static string BuildWhere(SqliteCommand cmd, List<string> tokens, params string[] columns)
        {
            var clauses = new List<string>();
            for (int i = 0; i < tokens.Count; i++)
            {
                var p = "@t" + i;
                cmd.Parameters.AddWithValue(p, "%" + tokens[i] + "%");
                clauses.Add("(" + string.Join(" OR ", columns.Select(c => c + " LIKE " + p)) + ")");
            }
            return clauses.Count == 0 ? "1=1" : string.Join(" OR ", clauses);
        }

        private static int ScoreText(string text, List<string> tokens, int weight)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            int score = 0;
            foreach (var t in tokens)
                if (text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) score += weight;
            return score;
        }

        /// <summary>
        /// P2a: besides whitespace/punctuation tokenization, further expand identifiers into sub-words at underscore and camel-case boundaries
        /// ("FC_MotorControl" → FC_MotorControl, FC, Motor, Control), fixing retrieval failures caused by underscore/
        /// camel-case naming differences. Original tokens are always kept; sub-words only add recall, never remove it.
        /// </summary>
        private static List<string> Tokenize(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<string>();
            var tokens = new List<string>();
            foreach (var raw in query.Split(new[] { ' ', '\t', '\n', '\r', ',', ';', '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (raw.Length >= 2) tokens.Add(raw);
                foreach (var part in SplitIdentifierParts(raw))
                    tokens.Add(part);
            }
            return tokens
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
        }

        /// <summary>Produce sub-words at underscore and camel-case boundaries (including the uppercase-acronym turn in "SKControl"→"SK"+"Control").</summary>
        private static IEnumerable<string> SplitIdentifierParts(string token)
        {
            int start = 0;
            for (int i = 1; i < token.Length; i++)
            {
                var prev = token[i - 1]; var cur = token[i];
                bool boundary = cur == '_'
                    || (char.IsLower(prev) && char.IsUpper(cur))
                    || (char.IsUpper(prev) && char.IsUpper(cur) && i + 1 < token.Length && char.IsLower(token[i + 1]));
                if (!boundary) continue;
                if (i - start >= 2) yield return token.Substring(start, i - start);
                start = cur == '_' ? i + 1 : i;
            }
            if (token.Length - start >= 2 && start > 0) yield return token.Substring(start);
        }

        private static string LastSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var idx = path.LastIndexOf('.');
            return idx >= 0 && idx < path.Length - 1 ? path.Substring(idx + 1) : path;
        }

        private static string Str(SqliteDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? "" : r.GetValue(i)?.ToString() ?? "";
        }

        private static int Int(SqliteDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            if (r.IsDBNull(i)) return 0;
            return int.TryParse(r.GetValue(i)?.ToString(), out var v) ? v : 0;
        }

        private static bool HasColumn(SqliteDataReader r, string col)
        {
            for (int i = 0; i < r.FieldCount; i++)
                if (string.Equals(r.GetName(i), col, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// T13 evidence layout. Each hit is partitioned so the Agent can tell structure from
        /// code from JSON without guessing which field means what:
        ///   Members:     indexed interface members (DB/UDT/FB/FC); DB/UDT have nothing else
        ///   Source:      reconstructed text-language code (SCL/STL), stored verbatim
        ///   JsonPreview: capped canonical JSON of a graphical/STL network (never the write payload)
        /// </summary>
        private static string FormatHit(PlcKnowledgeSearchHit hit)
        {
            var lines = new List<string>
            {
                "[" + hit.BlockName + "/" + hit.BlockType + "/" + hit.Language + "] " + (hit.Summary ?? "")
            };

            if (hit.MatchedMembers != null && hit.MatchedMembers.Count > 0)
            {
                lines.Add("Members:");
                foreach (var member in hit.MatchedMembers.Take(MemberLineCap))
                    lines.Add("  " + FormatMember(member));
                if (hit.MatchedMembers.Count > MemberLineCap)
                    lines.Add("  ... " + (hit.MatchedMembers.Count - MemberLineCap)
                              + " more; get_block_content / export_block_ir returns the full interface");
            }

            foreach (var net in hit.MatchedNetworks.OrderBy(x => x.NetworkIndex))
            {
                lines.Add($"Network {net.NetworkIndex} [{net.NetworkId}] {net.Language} source={net.SourceRef}");
                if (!string.IsNullOrWhiteSpace(net.Title)) lines.Add("  Title: " + net.Title);
                if (!string.IsNullOrWhiteSpace(net.Comment)) lines.Add("  Comment: " + net.Comment);
                if (!string.IsNullOrWhiteSpace(net.Inputs)) lines.Add("  Inputs: " + net.Inputs);
                if (!string.IsNullOrWhiteSpace(net.Outputs)) lines.Add("  Outputs: " + net.Outputs);

                // Source partition: the SCL payload is stored as source text only (T12), so this
                // is the exact evidence the Agent analyses and patches against.
                if (!string.IsNullOrWhiteSpace(net.SourceText))
                {
                    lines.Add("  Source:");
                    foreach (var sourceLine in net.SourceText.Trim()
                                 .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                        lines.Add("    " + sourceLine.TrimEnd());
                }

                if (!string.IsNullOrWhiteSpace(net.JsonPayload))
                    lines.Add("  JsonPreview: " + CompactWhitespace(Truncate(net.JsonPayload, JsonPayloadCharLimit)));
            }

            foreach (var call in hit.MatchedCalls.Take(5))
            {
                var target = call.CalleeName;
                if (!string.IsNullOrWhiteSpace(call.CalleeBlockType)) target += " (" + call.CalleeBlockType + ")";
                if (!string.IsNullOrWhiteSpace(call.InstancePath)) target += " via " + call.InstancePath;
                lines.Add("Call: " + target);
            }
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>One interface member line: [Section] path : type [= start value].</summary>
        private static string FormatMember(PlcInterfaceMemberDoc member)
        {
            var path = string.IsNullOrWhiteSpace(member.MemberPath) ? member.Name : member.MemberPath;
            var line = string.IsNullOrWhiteSpace(member.Section) ? "" : "[" + member.Section + "] ";
            line += (path ?? "") + " : " + (member.DataType ?? "");
            if (!string.IsNullOrWhiteSpace(member.InitialValue)) line += " = " + member.InitialValue;
            return line.TrimEnd();
        }

        /// <summary>Members emitted per block; beyond this the Agent fetches the full interface.</summary>
        private const int MemberLineCap = 30;

        /// <summary>Canonical JSON can be large (whole SCL/GRAPH bodies); cap the evidence text.</summary>
        private const int JsonPayloadCharLimit = 4000;

        private static string Truncate(string s, int limit)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= limit) return s;
            return s.Substring(0, limit) + "…<truncated>";
        }

        private static string CompactWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            bool pendingSpace = false;
            foreach (var ch in s.Trim())
            {
                if (char.IsWhiteSpace(ch)) { pendingSpace = true; continue; }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
