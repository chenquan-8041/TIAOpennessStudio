using System;
using System.Collections.Generic;
using System.Linq;
using TIAOpennessStudio.Models.NetworkKnowledge;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Network-centric RAG pipeline: Object Locator → Network Recall → Graph Expansion → Evidence Pack.
    /// Only operates on a Published Snapshot per Requirement 14.
    /// </summary>
    public sealed class NetworkRetrievalPipeline
    {
        private const int MaxNetworks = 20;
        private const int MaxRelations = 100;
        private const int MaxTokens = 16384;
        private const int MaxHops = 2;
        private const int CharsPerToken = 4;

        /// <summary>
        /// Execute the full retrieval pipeline against a published snapshot.
        /// </summary>
        public EvidencePack Retrieve(ProjectSnapshot snapshot, string query)
        {
            if (snapshot == null || snapshot.State != SnapshotState.Published)
            {
                return EvidencePack.Unavailable("No published snapshot available");
            }

            var pack = new EvidencePack
            {
                SnapshotId = snapshot.SnapshotId,
                Query = query
            };

            // Stage 1: Object/Symbol Locator
            var candidates = LocateObjects(snapshot, query);

            if (candidates.Count == 0)
            {
                // Fallback: lexical match across all Networks
                pack.LocationWarning = "No exact object match; showing lexical candidates";
                candidates = FallbackLexicalMatch(snapshot, query);
            }

            // Stage 2: Network Recall
            var recalledNetworks = RecallNetworks(snapshot, candidates);

            // Stage 3: Graph Expansion (max 2 hops)
            var expanded = ExpandGraph(snapshot, recalledNetworks);

            // Stage 4: Assemble Evidence Pack with budget
            AssembleWithBudget(pack, expanded, snapshot);

            return pack;
        }

        private List<string> LocateObjects(ProjectSnapshot snapshot, string query)
        {
            var blockIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queryLower = (query ?? "").ToLowerInvariant().Trim();
            var queryTokens = queryLower.Split(new[] { ' ', '.', ',', '/', '\\', '_' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var network in snapshot.Networks)
            {
                // Match by block name in Network title or Access paths
                if (MatchesQuery(network, queryTokens))
                {
                    blockIds.Add(network.BlockId);
                }
            }

            // Also match via relations' raw target text
            foreach (var rel in snapshot.Relations)
            {
                if (!string.IsNullOrWhiteSpace(rel.RawTargetText) &&
                    queryTokens.Any(t => rel.RawTargetText.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (!string.IsNullOrWhiteSpace(rel.SourceBlockId))
                        blockIds.Add(rel.SourceBlockId);
                }
            }

            return blockIds.ToList();
        }

        private List<PlcNetworkDocument> RecallNetworks(ProjectSnapshot snapshot, List<string> candidateBlockIds)
        {
            var networks = new List<PlcNetworkDocument>();
            var blockSet = new HashSet<string>(candidateBlockIds, StringComparer.OrdinalIgnoreCase);

            foreach (var net in snapshot.Networks)
            {
                if (blockSet.Contains(net.BlockId))
                    networks.Add(net);
            }

            // Also include Networks referenced by relations targeting candidate blocks
            var relatedNetworkIds = snapshot.Relations
                .Where(r => blockSet.Contains(r.TargetBlockId) || blockSet.Contains(r.SourceBlockId))
                .Select(r => r.SourceNetworkId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var net in snapshot.Networks)
            {
                if (relatedNetworkIds.Contains(net.NetworkId) && !networks.Contains(net))
                    networks.Add(net);
            }

            return networks;
        }

        private List<PlcNetworkDocument> ExpandGraph(ProjectSnapshot snapshot, List<PlcNetworkDocument> seed)
        {
            var included = new HashSet<string>(seed.Select(n => n.NetworkId), StringComparer.OrdinalIgnoreCase);
            var result = new List<PlcNetworkDocument>(seed);
            var frontier = new HashSet<string>(seed.Select(n => n.BlockId), StringComparer.OrdinalIgnoreCase);

            for (int hop = 0; hop < MaxHops && result.Count < MaxNetworks; hop++)
            {
                var nextFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var rel in snapshot.Relations)
                {
                    if (rel.Resolution != ResolutionStatus.Resolved) continue;
                    if (result.Count >= MaxNetworks) break;

                    bool touches = frontier.Contains(rel.SourceBlockId) || frontier.Contains(rel.TargetBlockId);
                    if (!touches) continue;

                    // Find Networks in target block
                    var targetNetworks = snapshot.Networks
                        .Where(n => string.Equals(n.BlockId, rel.TargetBlockId, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(n.BlockId, rel.SourceBlockId, StringComparison.OrdinalIgnoreCase))
                        .Where(n => !included.Contains(n.NetworkId));

                    foreach (var net in targetNetworks)
                    {
                        if (result.Count >= MaxNetworks) break;
                        included.Add(net.NetworkId);
                        result.Add(net);
                        nextFrontier.Add(net.BlockId);
                    }
                }

                frontier = nextFrontier;
                if (frontier.Count == 0) break;
            }

            return result;
        }

        private void AssembleWithBudget(EvidencePack pack, List<PlcNetworkDocument> networks, ProjectSnapshot snapshot)
        {
            int tokenBudget = MaxTokens;
            int networkCount = 0;
            int relationCount = 0;

            var includedBlockIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var net in networks.OrderBy(n => n.NetworkOrder))
            {
                if (networkCount >= MaxNetworks) break;

                var hit = new NetworkHit
                {
                    NetworkId = net.NetworkId,
                    BlockId = net.BlockId,
                    NetworkOrder = net.NetworkOrder,
                    Title = net.Title,
                    AccessCount = net.Accesses.Count,
                    CallSiteCount = net.CallSites.Count,
                    PartCount = net.Parts.Count,
                    WireCount = net.Wires.Count,
                    ParseStatus = net.Status.ToString()
                };

                // Estimate tokens for this hit
                int estimate = EstimateTokens(hit);
                if (tokenBudget - estimate < 0 && networkCount > 0)
                {
                    pack.OmittedNetworkCount++;
                    continue;
                }

                pack.Networks.Add(hit);
                tokenBudget -= estimate;
                networkCount++;
                includedBlockIds.Add(net.BlockId);
            }

            // Include relevant relations
            foreach (var rel in snapshot.Relations)
            {
                if (relationCount >= MaxRelations) break;
                if (!includedBlockIds.Contains(rel.SourceBlockId) && !includedBlockIds.Contains(rel.TargetBlockId))
                    continue;

                pack.Relations.Add(new RelationHit
                {
                    RelationId = rel.RelationId,
                    SourceBlockId = rel.SourceBlockId,
                    TargetBlockId = rel.TargetBlockId,
                    Type = rel.Type.ToString(),
                    Resolution = rel.Resolution.ToString(),
                    RawTarget = rel.RawTargetText
                });
                relationCount++;
            }

            pack.TotalTokenEstimate = MaxTokens - tokenBudget;
            pack.OmittedRelationCount = snapshot.Relations.Count(r =>
                (includedBlockIds.Contains(r.SourceBlockId) || includedBlockIds.Contains(r.TargetBlockId))
                && !pack.Relations.Any(pr => pr.RelationId == r.RelationId));
        }

        private static bool MatchesQuery(PlcNetworkDocument network, string[] tokens)
        {
            foreach (var token in tokens)
            {
                if (network.BlockId?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (network.Title?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (network.Accesses.Any(a => a.CanonicalPath?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                if (network.CallSites.Any(c => c.RawCalleeName?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            }
            return false;
        }

        private List<string> FallbackLexicalMatch(ProjectSnapshot snapshot, string query)
        {
            var tokens = (query ?? "").ToLowerInvariant()
                .Split(new[] { ' ', '.', ',', '/', '\\', '_' }, StringSplitOptions.RemoveEmptyEntries);

            return snapshot.Networks
                .Where(n => MatchesQuery(n, tokens))
                .Select(n => n.BlockId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxNetworks)
                .ToList();
        }

        private static int EstimateTokens(NetworkHit hit)
        {
            int chars = (hit.Title?.Length ?? 0) + (hit.BlockId?.Length ?? 0) + 50
                + hit.AccessCount * 30 + hit.CallSiteCount * 40 + hit.PartCount * 15;
            return chars / CharsPerToken;
        }
    }

    // ---- Evidence Pack Model ----

    public sealed class EvidencePack
    {
        public string SnapshotId { get; set; }
        public string Query { get; set; }
        public string LocationWarning { get; set; }
        public List<NetworkHit> Networks { get; set; } = new List<NetworkHit>();
        public List<RelationHit> Relations { get; set; } = new List<RelationHit>();
        public int TotalTokenEstimate { get; set; }
        public int OmittedNetworkCount { get; set; }
        public int OmittedRelationCount { get; set; }

        public bool IsAvailable => !string.IsNullOrWhiteSpace(SnapshotId);

        public static EvidencePack Unavailable(string reason)
        {
            return new EvidencePack { LocationWarning = reason };
        }
    }

    public sealed class NetworkHit
    {
        public string NetworkId { get; set; }
        public string BlockId { get; set; }
        public int NetworkOrder { get; set; }
        public string Title { get; set; }
        public int AccessCount { get; set; }
        public int CallSiteCount { get; set; }
        public int PartCount { get; set; }
        public int WireCount { get; set; }
        public string ParseStatus { get; set; }
    }

    public sealed class RelationHit
    {
        public string RelationId { get; set; }
        public string SourceBlockId { get; set; }
        public string TargetBlockId { get; set; }
        public string Type { get; set; }
        public string Resolution { get; set; }
        public string RawTarget { get; set; }
    }
}
