using System.Collections.Generic;
using TIAOpennessStudio.Models.NetworkKnowledge;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Persistence layer for Network-centric knowledge documents.
    /// Implementations may use SQLite, JSON or in-memory storage.
    /// </summary>
    public interface INetworkDocumentStore
    {
        /// <summary>
        /// Upsert a batch of Network documents for a block.
        /// Replaces all previously stored Networks for the given blockId.
        /// </summary>
        void UpsertNetworks(string projectKey, string blockId, IReadOnlyList<PlcNetworkDocument> networks);

        /// <summary>
        /// Get all Network documents for a specific block.
        /// </summary>
        List<PlcNetworkDocument> GetNetworksByBlock(string projectKey, string blockId);

        /// <summary>
        /// Get a single Network document by its stable ID.
        /// </summary>
        PlcNetworkDocument GetNetwork(string projectKey, string networkId);

        /// <summary>
        /// Get all Network documents for a project (for snapshot building).
        /// </summary>
        List<PlcNetworkDocument> GetAllNetworks(string projectKey);

        /// <summary>
        /// Remove all Networks belonging to a block (for deletion/rename).
        /// </summary>
        void RemoveNetworksByBlock(string projectKey, string blockId);

        /// <summary>
        /// Get network count for a project.
        /// </summary>
        int GetNetworkCount(string projectKey);
    }
}
