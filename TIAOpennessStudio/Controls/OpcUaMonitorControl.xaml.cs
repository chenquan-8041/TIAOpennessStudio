using System.Windows.Controls;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.ViewModels;

namespace TIAOpennessStudio.Controls
{
    /// <summary>
    /// OPC UA live monitor page hosted in the central area. Subscribes automatically after loading, and releases the subscription on unload.
    /// </summary>
    public partial class OpcUaMonitorControl : UserControl
    {
        private readonly OpcUaMonitorViewModel _vm;

        public OpcUaMonitorControl(OpcUaMonitorViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = _vm;

            // Three-language localization: apply initially + refresh on language change.
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
            ApplyLanguage();

            Loaded += async (_, __) => await _vm.StartAsync();
            Unloaded += (_, __) =>
            {
                LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;
                _vm.Dispose();
            };
        }

        private void OnLanguageChanged(AppLanguage lang) => ApplyLanguage();

        private void ApplyLanguage()
        {
            var lm = LanguageManager.Instance;
            if (lblMonitorTitle != null) lblMonitorTitle.Text = lm.GetString("Opc.MonitorTitle");
            if (lblFooter != null) lblFooter.Text = lm.GetString("Opc.MonitorFooter");
            if (colName != null) colName.Header = lm.GetString("Opc.Col.Name");
            if (colNodeId != null) colNodeId.Header = lm.GetString("Opc.Col.NodeId");
            if (colValue != null) colValue.Header = lm.GetString("Opc.Col.Value");
            if (colWrite != null) colWrite.Header = lm.GetString("Opc.Col.Write");
            if (colType != null) colType.Header = lm.GetString("Opc.Col.Type");
            if (colQuality != null) colQuality.Header = lm.GetString("Opc.Col.Quality");
            if (colTimestamp != null) colTimestamp.Header = lm.GetString("Opc.Col.Timestamp");
            Resources["OpcWriteValueTip"] = lm.GetString("Opc.WriteValueTip");
        }
    }
}
