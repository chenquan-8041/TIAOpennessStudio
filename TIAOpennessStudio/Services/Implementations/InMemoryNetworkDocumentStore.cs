using System.Collections.Generic;
using System.Linq;
using TIAOpennessStudio.Models.NetworkKnowledge;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// In-memory implementation of INetworkDocumentStore.
    /// Suitable for testing and initial integration; production will use SQLite.
    /// </summary>
    public sealed class InMemoryNetworkDocumentStore : INetworkDocumentStore
    {
        private readonly object _lock = new object();

        // Key: projectKey -> blockId -> list of networks
        private readonly Dictionary<string, Dictionary<string, List<PlcNetworkDocument>>> _store
            = new Dictionary<string, Dictionary<string, List<PlcNetworkDocument>>>();

        public void UpsertNetworks(string projectKey, string blockId, IReadOnlyList<PlcNetworkDocument> networks)
        {
            lock (_lock)
            {
                if (!_store.TryGetValue(projectKey, out var blockMap))
                {
                    blockMap = new Dictionary<string, List<PlcNetworkDocument>>();
                    _store[projectKey] = blockMap;
                }

                blockMap[blockId] = new List<PlcNetworkDocument>(networks);
            }
        }

        public List<PlcNetworkDocument> GetNetworksByBlock(string projectKey, string blockId)
        {
            lock (_lock)
            {
                if (_store.TryGetValue(projectKey, out var blockMap)
                    && blockMap.TryGetValue(blockId, out var networks))
                {
                    return new List<PlcNetworkDocument>(networks);
                }
            }
            return new List<PlcNetworkDocument>();
        }

        public PlcNetworkDocument GetNetwork(string projectKey, string networkId)
        {
            lock (_lock)
            {
                if (!_store.TryGetValue(projectKey, out var blockMap))
                    return null;

                foreach (var list in blockMap.Values)
                {
                    var found = list.FirstOrDefault(n => n.NetworkId == networkId);
                    if (found != null) return found;
                }
            }
            return null;
        }

        public List<PlcNetworkDocument> GetAllNetworks(string projectKey)
        {
            lock (_lock)
            {
                if (!_store.TryGetValue(projectKey, out var blockMap))
                    return new List<PlcNetworkDocument>();

                return blockMap.Values.SelectMany(x => x).ToList();
            }
        }

        public void RemoveNetworksByBlock(string projectKey, string blockId)
        {
            lock (_lock)
            {
                if (_store.TryGetValue(projectKey, out var blockMap))
                {
                    blockMap.Remove(blockId);
                }
            }
        }

        public int GetNetworkCount(string projectKey)
        {
            lock (_lock)
            {
                if (!_store.TryGetValue(projectKey, out var blockMap))
                    return 0;
                return blockMap.Values.Sum(list => list.Count);
            }
        }
    }
}
