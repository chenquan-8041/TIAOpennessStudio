using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    public delegate void OpcUaDataChangedHandler(string subscriptionId, OpcUaValueSnapshot snapshot);

    /// <summary>
    /// OPC UA client service for connecting, reading/writing nodes, and subscribing
    /// to data changes.
    /// </summary>
    public interface IOpcUaService
    {
        event Action<OpcUaConnectionState> ConnectionStateChanged;
        event OpcUaDataChangedHandler DataChanged;

        OpcUaConnectionState State { get; }
        bool IsConnected { get; }

        /// <summary>
        /// Write-enable switch (safety gate). Defaults to false, which disallows any
        /// write to a running PLC. WriteNodeAsync / opcua_write_node are only permitted
        /// after the user explicitly enables writing in the OPC UA panel. This is a
        /// second line of defense, independent of the agent confirmation, that prevents
        /// accidental writes to live devices.
        /// </summary>
        bool WriteEnabled { get; set; }

        Task<bool> ConnectAsync(OpcUaConnectionConfig config, CancellationToken ct = default);
        Task DisconnectAsync(CancellationToken ct = default);

        Task<OpcUaValueSnapshot> ReadNodeAsync(string nodeId, CancellationToken ct = default);
        Task<IReadOnlyList<OpcUaValueSnapshot>> ReadNodesAsync(IEnumerable<string> nodeIds, CancellationToken ct = default);

        Task WriteNodeAsync(string nodeId, object value, string dataType = null, CancellationToken ct = default);

        Task<string> SubscribeAsync(IEnumerable<string> nodeIds, int publishingIntervalMs, CancellationToken ct = default);
        Task UnsubscribeAsync(string subscriptionId, CancellationToken ct = default);

        Task<IReadOnlyList<OpcUaNodeInfo>> BrowseAsync(string nodeId = null, CancellationToken ct = default);
        Task<IReadOnlyList<OpcUaNodeInfo>> BrowseRecursiveAsync(string nodeId = null, int maxDepth = 3, CancellationToken ct = default);
    }
}
