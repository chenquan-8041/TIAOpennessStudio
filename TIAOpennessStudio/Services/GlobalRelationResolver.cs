using System;
using System.Collections.Generic;
using System.Linq;
using TIAOpennessStudio.Models.NetworkKnowledge;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Resolves CallSite targets, DB/UDT member references, and instance bindings
    /// against the full discovered object list. Produces GlobalRelationEdge with explicit
    /// Resolution Status per Requirement 8 and 9.
    /// </summary>
    public sealed class GlobalRelationResolver
    {
        /// <summary>
        /// Resolve all relations from extracted Network documents against a frozen object inventory.
        /// </summary>
        /// <param name="networks">All Network documents in the candidate snapshot.</param>
        /// <param name="objectInventory">Map of blockName (case-insensitive) → blockId.</param>
        /// <param name="objectTypes">Map of blockId → block type (OB, FC, FB, DB, UDT).</param>
        /// <returns>List of resolved global relation edges.</returns>
        public List<GlobalRelationEdge> ResolveAll(
            IReadOnlyList<PlcNetworkDocument> networks,
            Dictionary<string, string> objectInventory,
            Dictionary<string, string> objectTypes)
        {
            var relations = new List<GlobalRelationEdge>();
            var nameToId = new Dictionary<string, string>(objectInventory, StringComparer.OrdinalIgnoreCase);
            var idToType = new Dictionary<string, string>(objectTypes, StringComparer.OrdinalIgnoreCase);

            foreach (var network in networks)
            {
                if (network.Status != ParseStatus.Succeeded) continue;

                // Resolve Call Sites
                foreach (var callSite in network.CallSites)
                {
                    var edge = ResolveCallSite(callSite, network, nameToId, idToType);
                    relations.Add(edge);

                    // Update the CallSite resolution status in-place
                    callSite.Status = edge.Resolution;
                    callSite.ResolvedTargetId = edge.TargetBlockId;
                    callSite.ResolvedInstanceId = edge.InstanceId;
                    callSite.StatusReason = edge.ResolutionReason;
                }

                // Resolve Access → Variable relations (read/write edges)
                foreach (var access in network.Accesses)
                {
                    if (string.IsNullOrWhiteSpace(access.CanonicalPath)) continue;
                    if (access.Direction == AccessDirection.Unresolved) continue;

                    var relType = access.Direction == AccessDirection.Write
                        ? RelationType.VariableWrite
                        : access.Direction == AccessDirection.ReadWrite
                            ? RelationType.VariableWrite // treat as write for graph projection
                            : RelationType.VariableRead;

                    // Try to resolve target block from canonical path (first segment is often block name)
                    var targetBlockId = ResolveTargetBlockFromPath(access.CanonicalPath, nameToId);

                    relations.Add(new GlobalRelationEdge
                    {
                        RelationId = Guid.NewGuid().ToString("N").Substring(0, 12),
                        SourceBlockId = network.BlockId,
                        SourceNetworkId = network.NetworkId,
                        TargetBlockId = targetBlockId ?? "",
                        Type = relType,
                        Resolution = targetBlockId != null ? ResolutionStatus.Resolved : ResolutionStatus.Unresolved,
                        ResolutionReason = targetBlockId == null ? "Target block not in inventory" : null,
                        RawTargetText = access.CanonicalPath,
                        SourceRef = access.SourceRef
                    });
                }

                // Resolve Parameter Bindings
                foreach (var binding in network.ParameterBindings)
                {
                    relations.Add(new GlobalRelationEdge
                    {
                        RelationId = Guid.NewGuid().ToString("N").Substring(0, 12),
                        SourceBlockId = network.BlockId,
                        SourceNetworkId = network.NetworkId,
                        TargetBlockId = binding.CallSiteUid, // Links to call site
                        Type = RelationType.ParameterBinding,
                        Resolution = ResolutionStatus.Resolved,
                        RawTargetText = $"{binding.FormalParameterName}={binding.ActualAccessPath}",
                        SourceRef = binding.Evidence
                    });
                }
            }

            return relations;
        }

        private GlobalRelationEdge ResolveCallSite(
            CallSiteNode callSite,
            PlcNetworkDocument network,
            Dictionary<string, string> nameToId,
            Dictionary<string, string> idToType)
        {
            var edge = new GlobalRelationEdge
            {
                RelationId = Guid.NewGuid().ToString("N").Substring(0, 12),
                SourceBlockId = network.BlockId,
                SourceNetworkId = network.NetworkId,
                Type = RelationType.BlockCall,
                RawTargetText = callSite.RawCalleeName,
                SourceRef = callSite.SourceRef
            };

            // Try resolve callee by name
            if (!nameToId.TryGetValue(callSite.RawCalleeName ?? "", out var targetId))
            {
                edge.Resolution = ResolutionStatus.Unresolved;
                edge.ResolutionReason = $"Block '{callSite.RawCalleeName}' not found in project inventory";
                return edge;
            }

            edge.TargetBlockId = targetId;

            // Validate type compatibility
            var targetType = idToType.TryGetValue(targetId, out var tt) ? tt : "";
            if (IsNonCallableType(targetType))
            {
                edge.Resolution = ResolutionStatus.Unresolved;
                edge.ResolutionReason = $"Target '{callSite.RawCalleeName}' is {targetType}, which is not callable";
                return edge;
            }

            // For FB calls: resolve instance
            if (IsFbType(targetType) || IsFbType(callSite.RawBlockType))
            {
                if (string.IsNullOrWhiteSpace(callSite.RawInstancePath))
                {
                    edge.Resolution = ResolutionStatus.Unresolved;
                    edge.ResolutionReason = "FB call missing instance path";
                    return edge;
                }

                // Try resolve instance DB
                if (nameToId.TryGetValue(callSite.RawInstancePath, out var instanceId))
                {
                    edge.InstanceId = instanceId;
                    edge.Resolution = ResolutionStatus.Resolved;
                }
                else
                {
                    // Instance might be a multi-instance (local path) - still mark resolved for the call itself
                    edge.InstanceId = null;
                    edge.Resolution = ResolutionStatus.Resolved;
                    edge.ResolutionReason = "Instance path may be multi-instance (local)";
                }
            }
            else
            {
                edge.Resolution = ResolutionStatus.Resolved;
            }

            return edge;
        }

        private static string ResolveTargetBlockFromPath(string canonicalPath, Dictionary<string, string> nameToId)
        {
            if (string.IsNullOrWhiteSpace(canonicalPath)) return null;

            // First segment of DB_Main.Motor.Speed is "DB_Main"
            var dotIdx = canonicalPath.IndexOf('.');
            var firstSegment = dotIdx > 0 ? canonicalPath.Substring(0, dotIdx) : canonicalPath;

            // Remove quotes if present
            firstSegment = firstSegment.Trim('"');

            return nameToId.TryGetValue(firstSegment, out var id) ? id : null;
        }

        private static bool IsNonCallableType(string blockType)
        {
            var lower = (blockType ?? "").ToLowerInvariant();
            return lower == "db" || lower == "globaldb" || lower == "instancedb"
                || lower == "udt" || lower == "plctype"
                || lower == "ob";
        }

        private static bool IsFbType(string blockType)
        {
            var lower = (blockType ?? "").ToLowerInvariant();
            return lower == "fb" || lower.Contains("fb");
        }
    }
}
