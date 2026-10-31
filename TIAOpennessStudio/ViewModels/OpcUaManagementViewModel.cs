using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.ViewModels
{
    public class OpcUaManagementViewModel : INotifyPropertyChanged
    {
        private readonly IOpcUaService _opcUaService;
        public ObservableCollection<OpcUaNodeItem> Nodes { get; } = new ObservableCollection<OpcUaNodeItem>();
        public ObservableCollection<OpcUaNodeItem> MonitorItems { get; } = new ObservableCollection<OpcUaNodeItem>();

        /// <summary>Exposes the underlying service so the central real-time monitor page can reuse the same connection for subscribe/read/write.</summary>
        public IOpcUaService Service => _opcUaService;

        /// <summary>
        /// Write-enable switch (safety gate). Off by default — Agent/UI may only write to a running PLC when the user explicitly checks it.
        /// Bound to a UI checkbox and forwarded to the underlying service's WriteEnabled.
        /// </summary>
        public bool WriteEnabled
        {
            get => _opcUaService != null && _opcUaService.WriteEnabled;
            set
            {
                if (_opcUaService != null) _opcUaService.WriteEnabled = value;
                OnPropertyChanged();
                StatusMessage = value
                    ? "⚠ 已启用写入：Agent/UI 可写运行中 PLC，请谨慎操作"
                    : "写入已禁用（安全默认）";
            }
        }

        /// <summary>
        /// Request to open an OPC UA real-time monitor tab in the center area.
        /// Args: (tab title, collection of variable nodes to monitor). Subscribed by MainWindow and hosted into tabsCode.
        /// </summary>
        public event Action<string, IReadOnlyList<OpcUaNodeItem>> OpenMonitorTabRequested;
        private string _endpointUrl = "opc.tcp://localhost:4840";
        public string EndpointUrl { get => _endpointUrl; set { _endpointUrl = value; OnPropertyChanged(); } }
        private bool _isConnected;
        public bool IsConnected 
        { 
            get => _isConnected; 
            set 
            { 
                _isConnected = value; 
                OnPropertyChanged();
                (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (DisconnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (BrowseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (OpenMonitorCommand as RelayCommand)?.RaiseCanExecuteChanged();
            } 
        }
        private string _statusMessage = "Disconnected";
        public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }
        private bool _isLoading;
        public bool IsLoading { get => _isLoading; set { _isLoading = value; OnPropertyChanged(); } }
        private OpcUaNodeItem _selectedNode;
        public OpcUaNodeItem SelectedNode 
        { 
            get => _selectedNode; 
            set 
            { 
                _selectedNode = value; 
                OnPropertyChanged();
                (AddMonitorCommand as RelayCommand)?.RaiseCanExecuteChanged();
            } 
        }
        private string _manualNodeId = "";
        /// <summary>Manually entered NodeId (monitor a known variable directly, bypassing browse).</summary>
        public string ManualNodeId
        {
            get => _manualNodeId;
            set { _manualNodeId = value; OnPropertyChanged(); (MonitorManualCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
        }

        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand BrowseCommand { get; }
        public ICommand AddMonitorCommand { get; }
        public ICommand RemoveMonitorCommand { get; }
        /// <summary>Open the selected variable node in a real-time monitor tab in the center area.</summary>
        public ICommand OpenMonitorCommand { get; }
        /// <summary>Open real-time monitoring directly using the manually entered NodeId.</summary>
        public ICommand MonitorManualCommand { get; }
        /// <summary>True when a real IOpcUaService is wired in (not the design/placeholder VM).</summary>
        public bool HasService => _opcUaService != null;
        public OpcUaManagementViewModel() : this(null) { }
        public OpcUaManagementViewModel(IOpcUaService svc)
        {
            _opcUaService = svc;
            ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected);
            DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);
            BrowseCommand = new AsyncRelayCommand(BrowseAsync, () => IsConnected);
            AddMonitorCommand = new RelayCommand(AddMonitor, () => SelectedNode != null);
            RemoveMonitorCommand = new RelayCommand(RemoveMonitor, () => MonitorItems.Any(m => m.IsSelected));
            OpenMonitorCommand = new RelayCommand(OpenMonitor, () => IsConnected);
            MonitorManualCommand = new RelayCommand(MonitorManual, () => IsConnected && !string.IsNullOrWhiteSpace(ManualNodeId));
            
            // Subscribe to OPC UA data change events (TR8 - UI thread marshaling)
            if (_opcUaService != null)
            {
                _opcUaService.DataChanged += OnOpcUaDataChanged;
            }
        }
        
        /// <summary>
        /// Handles OPC UA data change notifications with UI thread marshaling (TR8)
        /// </summary>
        private void OnOpcUaDataChanged(string subscriptionId, OpcUaValueSnapshot snapshot)
        {
            // Marshal to UI thread using Dispatcher (TR8)
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                var monitorItem = MonitorItems.FirstOrDefault(m => m.NodeId == snapshot.NodeId);
                if (monitorItem != null)
                {
                    monitorItem.Value = snapshot.Value?.ToString() ?? "null";
                    monitorItem.DataType = snapshot.DataType ?? "";
                }
            });
        }
        private async Task ConnectAsync()
        {
            if (_opcUaService == null) { StatusMessage = "OPC UA service not available"; return; }
            IsLoading = true; StatusMessage = "Connecting...";
            try { var ok = await _opcUaService.ConnectAsync(new OpcUaConnectionConfig { EndpointUrl = EndpointUrl }, System.Threading.CancellationToken.None); IsConnected = ok; StatusMessage = ok ? "Connected" : "Failed"; if (ok) await BrowseAsync(); }
            catch (Exception ex) { StatusMessage = "Error: " + ex.Message; }
            finally { IsLoading = false; }
        }
        private async Task DisconnectAsync() { if (_opcUaService == null) return; await _opcUaService.DisconnectAsync(); IsConnected = false; Nodes.Clear(); StatusMessage = "Disconnected"; }
        private async Task BrowseAsync()
        {
            if (_opcUaService == null || !IsConnected) return; 
            IsLoading = true;
            try 
            { 
                var nodeInfos = await _opcUaService.BrowseAsync(null, System.Threading.CancellationToken.None); 
                Nodes.Clear(); 
                foreach (var nodeInfo in nodeInfos) 
                {
                    Nodes.Add(CreateNodeItem(nodeInfo));
                }
                StatusMessage = "Browsed " + Nodes.Count + " nodes"; 
            }
            catch (Exception ex) { StatusMessage = "Error: " + ex.Message; }
            finally { IsLoading = false; }
        }

        /// <summary>Convert an OpcUaNodeInfo into a UI node item. Non-variable nodes get a placeholder child so the expand arrow shows, enabling lazy loading.</summary>
        private OpcUaNodeItem CreateNodeItem(OpcUaNodeInfo info)
        {
            var item = new OpcUaNodeItem
            {
                NodeId = info.NodeId,
                DisplayName = info.DisplayName,
                DataType = info.DataType ?? "",
                NodeClass = info.NodeClass
            };
            // Non-variables (Object/Folder, etc.) likely have children; add a placeholder so the TreeView shows an expand arrow.
            if (!item.IsVariable)
            {
                item.Children.Add(PlaceholderChild);
            }
            return item;
        }

        /// <summary>Placeholder child marker (used for lazy loading).</summary>
        private static readonly OpcUaNodeItem PlaceholderChild = new OpcUaNodeItem { DisplayName = "加载中...", NodeId = "__placeholder__" };

        /// <summary>
        /// Lazily load the children of the given node. Called when the TreeView expands; loads only once.
        /// This allows drilling down from the root to DataBlocksGlobal → DB → UDT instance → members.
        /// </summary>
        public async Task LoadChildrenAsync(OpcUaNodeItem parent)
        {
            if (parent == null || parent.ChildrenLoaded || _opcUaService == null || !IsConnected) return;
            parent.ChildrenLoaded = true;

            IsLoading = true;
            try
            {
                var children = await _opcUaService.BrowseAsync(parent.NodeId, System.Threading.CancellationToken.None);
                parent.Children.Clear();
                foreach (var childInfo in children)
                {
                    parent.Children.Add(CreateNodeItem(childInfo));
                }
                StatusMessage = $"Browsed {parent.Children.Count} children under {parent.DisplayName}";
            }
            catch (Exception ex)
            {
                parent.ChildrenLoaded = false; // Allow retry
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsLoading = false; }
        }

        /// <summary>
        /// Open a real-time monitor tab. Prefer all checked (IsSelected) variables across the whole tree (multi-select);
        /// if nothing is checked, fall back to the currently selected node (the variable itself, or its expanded variable subtree).
        /// </summary>
        private void OpenMonitor()
        {
            var vars = new List<OpcUaNodeItem>();

            // 1) Multi-select: collect checked variable nodes across the whole tree.
            foreach (var root in Nodes)
                CollectCheckedVariables(root, vars);

            // 2) Fall back to the current selection when nothing is checked.
            if (vars.Count == 0 && SelectedNode != null)
            {
                if (SelectedNode.IsVariable) vars.Add(SelectedNode);
                else CollectVariableLeaves(SelectedNode, vars);
            }

            if (vars.Count == 0)
            {
                StatusMessage = Services.LanguageManager.Instance.GetString("Opc.Status.NoMonitorTarget");
                return;
            }

            // Deduplicate by NodeId.
            vars = vars.GroupBy(v => v.NodeId).Select(g => g.First()).ToList();

            // Title taken from node properties: a single variable uses its display name; multiple use the localized "Real-time Monitor (N)".
            string title = vars.Count == 1
                ? (string.IsNullOrWhiteSpace(vars[0].DisplayName) ? vars[0].NodeId : vars[0].DisplayName)
                : string.Format(Services.LanguageManager.Instance.GetString("Opc.MonitorTabMulti"), vars.Count);

            OpenMonitorTabRequested?.Invoke(title, vars);
        }

        /// <summary>Open monitoring directly with manually entered NodeIds (bypassing the browse tree). Supports multiple separated by commas/newlines.</summary>
        private void MonitorManual()
        {
            if (string.IsNullOrWhiteSpace(ManualNodeId)) return;

            var ids = ManualNodeId
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct()
                .ToList();

            if (ids.Count == 0) return;

            var vars = ids.Select(id => new OpcUaNodeItem
            {
                NodeId = id,
                DisplayName = id,
                NodeClass = "Variable"
            }).ToList();

            var title = ids.Count == 1
                ? ids[0]
                : string.Format(Services.LanguageManager.Instance.GetString("Opc.MonitorTabMulti"), ids.Count);
            OpenMonitorTabRequested?.Invoke(title, vars);
        }

        /// <summary>Recursively collect checked (IsSelected) variables in a subtree.</summary>
        private void CollectCheckedVariables(OpcUaNodeItem node, List<OpcUaNodeItem> sink)
        {
            if (node == null || node.NodeId == "__placeholder__") return;
            if (node.IsVariable && node.IsSelected) sink.Add(node);
            foreach (var c in node.Children)
                CollectCheckedVariables(c, sink);
        }

        /// <summary>Recursively collect loaded variable leaves under a node's subtree.</summary>
        private void CollectVariableLeaves(OpcUaNodeItem node, List<OpcUaNodeItem> sink)
        {
            foreach (var c in node.Children)
            {
                if (c.NodeId == "__placeholder__") continue;
                if (c.IsVariable) sink.Add(c);
                else CollectVariableLeaves(c, sink);
            }
        }

        private void AddMonitor() { if (SelectedNode == null || MonitorItems.Any(m => m.NodeId == SelectedNode.NodeId)) return; MonitorItems.Add(new OpcUaNodeItem { NodeId = SelectedNode.NodeId, DisplayName = SelectedNode.DisplayName, Value = SelectedNode.Value, DataType = SelectedNode.DataType }); }
        private void RemoveMonitor() { var items = MonitorItems.Where(m => m.IsSelected).ToList(); foreach (var i in items) MonitorItems.Remove(i); }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
    public class OpcUaNodeItem : INotifyPropertyChanged
    {
        public string NodeId { get; set; }
        public string DisplayName { get; set; }
        private string _value = "";
        public string Value { get => _value; set { _value = value; OnPropertyChanged(); } }
        private string _dataType = "";
        public string DataType { get => _dataType; set { _dataType = value; OnPropertyChanged(); } }
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

        /// <summary>The value to write for this row (entered in the Write column of the Data Access View table; Enter/button triggers the write).</summary>
        private string _writeInput = "";
        public string WriteInput { get => _writeInput; set { _writeInput = value; OnPropertyChanged(); } }

        /// <summary>Node class: Object / Variable, etc. Used to decide whether values can be read/subscribed.</summary>
        public string NodeClass { get; set; }

        /// <summary>Whether this is a variable node with a readable real-time value.</summary>
        public bool IsVariable => string.Equals(NodeClass, "Variable", StringComparison.OrdinalIgnoreCase);

        /// <summary>Node type icon color (variable = teal dot, container = amber square), UaExpert-style type distinction.</summary>
        public string IconColor => IsVariable ? "#4EC9B0" : "#D7BA7D";

        /// <summary>Quality code (Good/Bad/Uncertain).</summary>
        private string _quality = "";
        public string Quality { get => _quality; set { _quality = value; OnPropertyChanged(); } }

        /// <summary>Source timestamp.</summary>
        private string _timestamp = "";
        public string Timestamp { get => _timestamp; set { _timestamp = value; OnPropertyChanged(); } }

        /// <summary>Child node collection for the tree (lazy loaded).</summary>
        public ObservableCollection<OpcUaNodeItem> Children { get; } = new ObservableCollection<OpcUaNodeItem>();

        /// <summary>Whether children have already been loaded, to avoid re-browsing.</summary>
        public bool ChildrenLoaded { get; set; }

        private bool _isExpanded;
        public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute; private readonly Func<bool> _canExecute;
        public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute = null) { _execute = execute; _canExecute = canExecute; }
        public event EventHandler CanExecuteChanged;
        public bool CanExecute(object p) => _canExecute?.Invoke() ?? true;
        public async void Execute(object p) => await _execute();
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Async command with a parameter, supplied via CommandParameter (e.g. the current row's data item in XAML).</summary>
    public class AsyncRelayCommand<T> : ICommand
    {
        private readonly Func<T, Task> _execute; private readonly Func<T, bool> _canExecute;
        public AsyncRelayCommand(Func<T, Task> execute, Func<T, bool> canExecute = null) { _execute = execute; _canExecute = canExecute; }
        public event EventHandler CanExecuteChanged
        {
            add { System.Windows.Input.CommandManager.RequerySuggested += value; }
            remove { System.Windows.Input.CommandManager.RequerySuggested -= value; }
        }
        public bool CanExecute(object p) => _canExecute?.Invoke(p is T t ? t : default) ?? true;
        public async void Execute(object p) { if (p is T t) await _execute(t); }
        public void RaiseCanExecuteChanged() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}