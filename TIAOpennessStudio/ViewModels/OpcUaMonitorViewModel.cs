using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.ViewModels
{
    /// <summary>
    /// ViewModel for the central "OPC UA Real-time Monitor" page.
    /// Reuses the same IOpcUaService connection established in the left panel, subscribes to the given variable nodes, and refreshes value/quality/timestamp in real time.
    /// </summary>
    public class OpcUaMonitorViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IOpcUaService _service;
        private string _subscriptionId;
        private bool _disposed;

        /// <summary>The monitored rows.</summary>
        public ObservableCollection<OpcUaNodeItem> Items { get; } = new ObservableCollection<OpcUaNodeItem>();

        private string _status = "";
        public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

        /// <summary>Publishing interval (milliseconds).</summary>
        public int PublishingIntervalMs { get; set; } = 500;

        /// <summary>Write a row's variable: the parameter is that row's OpcUaNodeItem, using its NodeId and WriteInput.</summary>
        public ICommand WriteNodeCommand { get; }

        public OpcUaMonitorViewModel(IOpcUaService service, IEnumerable<OpcUaNodeItem> nodes)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));

            WriteNodeCommand = new AsyncRelayCommand<OpcUaNodeItem>(
                WriteNodeAsync,
                n => _service != null && _service.IsConnected && n != null && !string.IsNullOrEmpty(n.WriteInput));

            foreach (var n in nodes ?? Enumerable.Empty<OpcUaNodeItem>())
            {
                Items.Add(new OpcUaNodeItem
                {
                    NodeId = n.NodeId,
                    DisplayName = n.DisplayName,
                    DataType = n.DataType,
                    NodeClass = n.NodeClass
                });
            }

            _service.DataChanged += OnDataChanged;
        }

        /// <summary>Create the subscription and start real-time refresh. Called by the host control after loading.</summary>
        public async Task StartAsync()
        {
            var lm = Services.LanguageManager.Instance;

            if (_service == null || !_service.IsConnected)
            {
                Status = lm.GetString("Opc.Status.NotConnected");
                return;
            }

            var nodeIds = Items.Select(i => i.NodeId).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
            if (nodeIds.Count == 0)
            {
                Status = lm.GetString("Opc.Status.NoVariables");
                return;
            }

            try
            {
                // Read initial values once so the UI isn't empty before subscription callbacks arrive.
                var snaps = await _service.ReadNodesAsync(nodeIds, CancellationToken.None);
                foreach (var snap in snaps) ApplySnapshot(snap);

                _subscriptionId = await _service.SubscribeAsync(nodeIds, PublishingIntervalMs, CancellationToken.None);
                Status = string.Format(lm.GetString("Opc.Status.Subscribed"), nodeIds.Count, PublishingIntervalMs);
            }
            catch (Exception ex)
            {
                Status = string.Format(lm.GetString("Opc.Status.SubscribeFailed"), ex.Message);
            }
        }

        /// <summary>Write a row's WriteInput to the PLC; the subscription will auto-refresh the value. Writes are gated by the service-side WriteEnabled safety switch.</summary>
        private async Task WriteNodeAsync(OpcUaNodeItem item)
        {
            if (_service == null || item == null || string.IsNullOrEmpty(item.WriteInput)) return;

            var lm = Services.LanguageManager.Instance;
            if (!_service.IsConnected)
            {
                Status = lm.GetString("Opc.Status.NotConnected");
                return;
            }

            try
            {
                await _service.WriteNodeAsync(item.NodeId, item.WriteInput, null, CancellationToken.None);
                Status = "Wrote " + item.WriteInput + " -> " + item.NodeId;
                item.WriteInput = "";
                // If no active subscription refreshes it, read once to update the displayed value
                if (string.IsNullOrEmpty(_subscriptionId))
                {
                    var snap = await _service.ReadNodeAsync(item.NodeId, CancellationToken.None);
                    if (snap != null) ApplySnapshot(snap);
                }
            }
            catch (Exception ex)
            {
                Status = "Write failed: " + ex.Message;
            }
        }

        private void OnDataChanged(string subscriptionId, OpcUaValueSnapshot snapshot)
        {
            // Only handle data for this page's subscription.
            if (_subscriptionId != null && subscriptionId != _subscriptionId) return;

            System.Windows.Application.Current?.Dispatcher?.Invoke(() => ApplySnapshot(snapshot));
        }

        private void ApplySnapshot(OpcUaValueSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrEmpty(snapshot.NodeId)) return;

            // Match NodeId exactly first; if that fails, fall back to a normalized match (trimmed/lowercased)
            // to avoid values not refreshing when the server's NodeId string differs slightly from the browse-time format.
            var item = Items.FirstOrDefault(i => i.NodeId == snapshot.NodeId)
                       ?? Items.FirstOrDefault(i => NormalizeNodeId(i.NodeId) == NormalizeNodeId(snapshot.NodeId));
            if (item == null) return;

            item.Value = snapshot.Value?.ToString() ?? "null";
            if (!string.IsNullOrEmpty(snapshot.DataType)) item.DataType = snapshot.DataType;
            item.Quality = snapshot.Quality.ToString();
            item.Timestamp = snapshot.SourceTimestamp?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "";
        }

        private static string NormalizeNodeId(string nodeId)
            => (nodeId ?? "").Replace(" ", "").Trim().ToLowerInvariant();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _service.DataChanged -= OnDataChanged;
            if (!string.IsNullOrEmpty(_subscriptionId))
            {
                try { _ = _service.UnsubscribeAsync(_subscriptionId); } catch { }
                _subscriptionId = null;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
