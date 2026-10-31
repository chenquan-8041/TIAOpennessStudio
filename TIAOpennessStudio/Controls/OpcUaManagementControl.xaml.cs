using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Prism.Events;
using Prism.Ioc;
using TIAOpennessStudio.Events;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.ViewModels;

namespace TIAOpennessStudio.Controls
{
    public partial class OpcUaManagementControl : UserControl
    {
        private OpcUaManagementViewModel _vm;

        public OpcUaManagementControl()
        {
            // Important: set our own VM first to cut off DataContext inheritance from the parent MainViewModel.
            // Otherwise, at the instant elements enter the visual tree before DataContext is set, WPF evaluates
            // all bindings (AddMonitorCommand / EndpointUrl / ...) against the inherited MainViewModel and reports
            // "property not found on MainViewModel". This happens before Loaded, so correcting it afterward
            // cannot eliminate that error. A parameterless VM has all properties, so bindings resolve correctly from the start.
            _vm = new OpcUaManagementViewModel();
            DataContext = _vm;

            InitializeComponent();

            // On Loaded the DI container is ready; upgrade to a usable VM with IOpcUaService.
            Loaded += OnLoaded;

            // Three-language localization: apply initially + refresh on language change.
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
            ApplyLanguage();
            Unloaded += (_, __) => LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged(AppLanguage lang) => ApplyLanguage();

        /// <summary>Applies the current language strings to the named controls (theme-aware three-language display).</summary>
        private void ApplyLanguage()
        {
            var lm = LanguageManager.Instance;
            if (lblTitle != null) lblTitle.Text = lm.GetString("Opc.Title");
            if (btnConnect != null) btnConnect.Content = lm.GetString("Opc.Connect");
            if (btnDisconnect != null) btnDisconnect.Content = lm.GetString("Opc.Disconnect");
            if (lblEndpoint != null) lblEndpoint.Text = lm.GetString("Opc.Endpoint");
            // Read/Write/WriteValue have moved into the node template (inline read/write), so they are no longer localized by name here.
            if (btnMonitorChecked != null)
            {
                btnMonitorChecked.Content = lm.GetString("Opc.MonitorChecked");
                btnMonitorChecked.ToolTip = lm.GetString("Opc.MonitorCheckedTip");
            }
            if (txtManualNode != null)
            {
                txtManualNode.Tag = lm.GetString("Opc.ManualNodeHint");
                txtManualNode.ToolTip = lm.GetString("Opc.ManualNodeTip");
            }
            if (btnMonitorManual != null) btnMonitorManual.Content = lm.GetString("Opc.MonitorManual");
            if (chkWriteEnabled != null)
            {
                chkWriteEnabled.Content = lm.GetString("Opc.AllowWrite");
                chkWriteEnabled.ToolTip = lm.GetString("Opc.AllowWriteTip");
            }
            if (lblHint != null) lblHint.Text = lm.GetString("Opc.Hint");
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // If it is already a service-backed instance, do not inject again
            if (_vm != null && _vm.HasService)
                return;

            try
            {
                var svc = App.Services?.Resolve<IOpcUaService>();
                if (svc != null)
                {
                    _vm = new OpcUaManagementViewModel(svc);
                    _vm.OpenMonitorTabRequested += OnOpenMonitorTabRequested;
                    DataContext = _vm;
                }
            }
            catch (System.Exception ex)
            {
                AppLogger.Log($"[OpcUaManagementControl] DataContext injection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The VM requests opening the live monitor page: build the central monitor control, publish an event
        /// via the EventAggregator, and let MainWindow host it in the central tabsCode area (consistent with other features).
        /// </summary>
        private void OnOpenMonitorTabRequested(string title, IReadOnlyList<OpcUaNodeItem> nodes)
        {
            try
            {
                var svc = _vm?.Service ?? App.Services?.Resolve<IOpcUaService>();
                if (svc == null) return;

                var monitorVm = new OpcUaMonitorViewModel(svc, nodes);
                var monitorControl = new OpcUaMonitorControl(monitorVm);

                var aggregator = App.Services?.Resolve<IEventAggregator>();
                aggregator?.GetEvent<ShowOpcUaMonitorEvent>()
                    .Publish(new OpcUaMonitorEventArgs(title, monitorControl));
            }
            catch (System.Exception ex)
            {
                AppLogger.Log($"[OpcUaManagementControl] Open monitor tab failed: {ex.Message}");
            }
        }

        /// <summary>Lazily load child nodes when a TreeView node expands (drill down into DB/UDT/members).</summary>
        private async void NodeTree_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is OpcUaNodeItem node && _vm != null)
            {
                await _vm.LoadChildrenAsync(node);
            }
        }

        /// <summary>Sync the TreeView selection to VM.SelectedNode when it changes, enabling read/write/monitor commands.</summary>
        private void NodeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_vm != null && e.NewValue is OpcUaNodeItem node)
            {
                _vm.SelectedNode = node;
            }
        }
    }
}
