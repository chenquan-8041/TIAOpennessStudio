using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TIAOpennessStudio.AICore;
using TIAOpennessStudio.Events;
using TIAOpennessStudio.Services.Abstractions;
using Prism.Events;
using Prism.Ioc;

namespace TIAOpennessStudio.Controls
{
    public partial class SettingsCardControl : UserControl
    {
        private IAIEngineService _aiService;
        private AIConfig _config;

        public SettingsCardControl()
        {
            InitializeComponent();
            // Wire once here: Loaded fires again every time the Settings tab is
            // re-selected, and attaching in LoadConfig() stacked duplicate
            // handlers so one click launched several connection tests at once.
            btnSaveConfig.Click += OnSaveConfig;
            btnTestConnection.Click += OnTestConnection;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // In XAML parameterless construction _aiService is null; resolve the singleton from the DI container
            if (_aiService == null)
            {
                try { _aiService = App.Services.Resolve<IAIEngineService>(); } catch { }
            }
            LoadConfig();
            LoadAgentInfo();
        }

        private void LoadConfig()
        {
            _config = AIConfig.Load();
            if (_config == null) return;

            // Set execution mode
            var mode = _config.GetEffectiveExecutionMode();
            foreach (ComboBoxItem item in cmbExecutionMode.Items)
            {
                if (item.Tag?.ToString() == mode) { cmbExecutionMode.SelectedItem = item; break; }
            }

            // Set provider
            var provider = _config.GetEffectiveModelProvider();
            foreach (ComboBoxItem item in cmbProvider.Items)
            {
                if (item.Tag?.ToString() == provider) { cmbProvider.SelectedItem = item; break; }
            }

            txtModel.Text = _config.Model ?? _config.SelectedModel ?? "";
            txtApiKey.Password = _config.ApiKey ?? "";
            txtEndpoint.Text = _config.Endpoint ?? "";
            txtLocalBackend.Text = _config.LocalBackendUrl ?? "http://127.0.0.1:8000";
            chkRepairEvolution.IsChecked = _config.Agent?.EnableRepairEvolution == true;

            // Set reasoning effort
            var effort = string.IsNullOrWhiteSpace(_config.ReasoningEffort) ? "medium" : _config.ReasoningEffort.Trim();
            foreach (ComboBoxItem item in cmbReasoningEffort.Items)
            {
                if (string.Equals(item.Tag?.ToString(), effort, StringComparison.OrdinalIgnoreCase))
                { cmbReasoningEffort.SelectedItem = item; break; }
            }

            // Load custom models list
            RefreshCustomModelsList();
        }

        private void RefreshCustomModelsList()
        {
            if (_config?.CustomModels == null || _config.CustomModels.Count == 0)
            {
                customModelsList.ItemsSource = null;
                txtNoCustomModels.Visibility = Visibility.Visible;
                return;
            }

            // Filter out Ollama local models (those containing ":"), to avoid duplication with the Ollama card
            var filtered = new List<string>();
            foreach (var name in _config.CustomModels)
            {
                if (!string.IsNullOrWhiteSpace(name) && !AIConfig.IsLocalLlmModel(name))
                {
                    filtered.Add(name);
                }
            }

            customModelsList.ItemsSource = filtered;
            txtNoCustomModels.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnDeleteCustomModel(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string modelName)
            {
                // Reload the latest config
                _config = AIConfig.Load();
                if (_config == null) return;

                if (_config.RemoveCustomModel(modelName))
                {
                    _config.Save();
                    RefreshCustomModelsList();

                    // Notify the input box to refresh its model dropdown
                    PublishConfigSaved();
                }
            }
        }

        private void PublishConfigSaved()
        {
            try
            {
                var ea = App.Services.Resolve<IEventAggregator>();
                ea.GetEvent<ConfigurationSavedEvent>().Publish(_config);
            }
            catch { }
        }

        private async void LoadAgentInfo()
        {
            txtAgentEngine.Text = "ReAct (Dynamic)";
            txtMaxIterations.Text = "20";
            txtToolCount.Text = "11 tools registered";

            await LoadOllamaInfoAsync();
        }

        // ── Ollama Local Model ──────────────────────────────────────

        private IOllamaHostService _ollamaHost;
        private List<OllamaModelItem> _ollamaItems;

        private async System.Threading.Tasks.Task LoadOllamaInfoAsync()
        {
            try
            {
                _ollamaHost = App.Services.Resolve<IOllamaHostService>();
            }
            catch { }

            if (_ollamaHost == null)
            {
                txtOllamaStatus.Text = "Not available";
                ollamaStatusDot.Fill = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                return;
            }

            await RefreshOllamaAsync();
        }

        private async System.Threading.Tasks.Task RefreshOllamaAsync()
        {
            try
            {
                var healthy = await _ollamaHost.IsHealthyAsync();
                if (healthy)
                {
                    ollamaStatusDot.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                    txtOllamaStatus.Text = "Running";
                }
                else
                {
                    ollamaStatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0));
                    txtOllamaStatus.Text = "Stopped";
                }

                // System memory
                var mem = GetAvailableMemory();
                if (mem > 0)
                    txtOllamaMemory.Text = $"System: {mem / (1024 * 1024)} MB available";

                // Installed models
                var installed = await _ollamaHost.GetInstalledModelsAsync();
                var config = AIConfig.Load();
                var defaultModel = config?.DefaultOllamaModel ?? "";

                _ollamaItems = new List<OllamaModelItem>();
                foreach (var name in installed)
                {
                    var size = await _ollamaHost.GetModelSizeAsync(name);
                    var details = await _ollamaHost.GetModelDetailsAsync(name);
                    _ollamaItems.Add(new OllamaModelItem
                    {
                        Name = name,
                        SizeBytes = size,
                        SizeMB = size > 0 ? (double)size / (1024 * 1024) : 0,
                        Details = details ?? "",
                        IsDefault = string.Equals(name, defaultModel, StringComparison.OrdinalIgnoreCase),
                        IsNotDefault = !string.Equals(name, defaultModel, StringComparison.OrdinalIgnoreCase)
                    });
                }

                ollamaModelList.ItemsSource = _ollamaItems;
                txtNoModels.Visibility = _ollamaItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                txtOllamaStatus.Text = "Error: " + ex.Message;
                ollamaStatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            }
        }

        private void OnRefreshOllama(object sender, RoutedEventArgs e)
        {
            _ = RefreshOllamaAsync();
        }

        private void OnSetDefaultModel(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string modelName)
            {
                var config = AIConfig.Load();
                config.DefaultOllamaModel = modelName;
                config.Save();

                // Sync to the singleton OllamaLlmService so consumers pick up the change
                try
                {
                    var llmService = App.Services.Resolve<ILocalLlmService>();
                    llmService?.SetModel(modelName);
                }
                catch { }

                _ = RefreshOllamaAsync();
            }
        }

        private async void OnDeleteModel(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string modelName)
            {
                var result = System.Windows.MessageBox.Show(
                    $"Delete model '{modelName}'?\nThis frees disk space. You can re-download with 'ollama pull {modelName}'.",
                    "Delete Model", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return;

                if (_ollamaHost != null)
                {
                    btn.IsEnabled = false;
                    var deleted = await _ollamaHost.DeleteModelAsync(modelName);
                    if (deleted)
                    {
                        var config = AIConfig.Load();
                        if (string.Equals(config.DefaultOllamaModel, modelName, StringComparison.OrdinalIgnoreCase))
                        {
                            config.DefaultOllamaModel = "";
                            config.Save();

                            // Fall back to first remaining model, or 0.5b default
                            try
                            {
                                var llmService = App.Services.Resolve<ILocalLlmService>();
                                var remaining = await _ollamaHost.GetInstalledModelsAsync();
                                var fallback = remaining.Count > 0 ? remaining[0] : "qwen2.5:0.5b";
                                llmService?.SetModel(fallback);
                            }
                            catch { }
                        }
                        await RefreshOllamaAsync();
                    }
                    else
                    {
                        System.Windows.MessageBox.Show($"Failed to delete '{modelName}'.", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    btn.IsEnabled = true;
                }
            }
        }

        private static long GetAvailableMemory()
        {
            try
            {
                var memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref memStatus))
                    return (long)memStatus.ullAvailPhys;
            }
            catch { }
            return 0;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private void OnRepairEvolutionToggled(object sender, RoutedEventArgs e)
        {
            try
            {
                if (chkRepairEvolution == null) return;
                var config = AIConfig.Load();
                config.Agent = config.Agent ?? AgentConfig.CreateDefault();
                config.Agent.EnableRepairEvolution = chkRepairEvolution.IsChecked == true;
                config.Save();
                // Takes effect at runtime immediately, no restart needed
                App.Services.Resolve<AgentConfig>().EnableRepairEvolution = config.Agent.EnableRepairEvolution;
            }
            catch { }
        }

        private void OnSaveConfig(object sender, RoutedEventArgs e)
        {
            if (_config == null) return;

            // Reload the latest config from disk to avoid overwriting changes made in the Model Configuration dialog (such as CustomModels)
            var latestConfig = AIConfig.Load();
            if (latestConfig != null)
            {
                _config = latestConfig;
            }

            var selectedMode = cmbExecutionMode.SelectedItem as ComboBoxItem;
            var selectedProvider = cmbProvider.SelectedItem as ComboBoxItem;

            _config.ExecutionMode = selectedMode?.Tag?.ToString() ?? "LocalGateway";
            _config.ModelProvider = selectedProvider?.Tag?.ToString() ?? "OpenAI";
            _config.Provider = _config.ModelProvider;
            _config.Model = txtModel.Text;
            _config.SelectedModel = txtModel.Text;
            _config.ApiKey = txtApiKey.Password;
            _config.Endpoint = txtEndpoint.Text;
            _config.ReasoningEffort = (cmbReasoningEffort.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "medium";
            _config.LocalBackendUrl = txtLocalBackend.Text;
            _config.UseLocalBackend = _config.ExecutionMode == "LocalGateway";

            // Record the user-entered model name into CustomModels so it is visible in the input box dropdown
            if (!string.IsNullOrWhiteSpace(_config.Model))
            {
                _config.AddCustomModel(_config.Model);
            }

            _config.Save();

            // Hot-update DI singletons: the _aiService held by AgentOrchestrator must be updated in sync
            _aiService?.UpdateRuntimeConfig(
                apiKey: _config.ApiKey,
                endpoint: _config.Endpoint,
                model: _config.Model,
                provider: _config.Provider,
                reasoningEffort: _config.ReasoningEffort);

            // Notify the input box to refresh its model dropdown
            try
            {
                var ea = App.Services.Resolve<IEventAggregator>();
                ea.GetEvent<ConfigurationSavedEvent>().Publish(_config);
            }
            catch { }

            txtTestResult.Visibility = Visibility.Visible;
            txtTestResult.Text = "Configuration saved successfully.";
            txtTestResult.Foreground = FindResource("TextBrush") as Brush ?? Brushes.White;

            // Refresh the custom models list (the Model name was auto-recorded into CustomModels on Save)
            RefreshCustomModelsList();
        }

        private async void OnTestConnection(object sender, RoutedEventArgs e)
        {
            btnTestConnection.IsEnabled = false;
            btnTestConnection.Content = "Testing...";
            txtTestResult.Visibility = Visibility.Visible;
            txtTestResult.Text = "Testing connection...";

            try
            {
                var selectedProvider = cmbProvider.SelectedItem as ComboBoxItem;
                var provider = selectedProvider?.Tag?.ToString() ?? "OpenAI";
                bool useLocal = cmbExecutionMode.SelectedIndex == 0;

                var testGenerator = new RestAICodeGenerator(
                    txtApiKey.Password,
                    txtEndpoint.Text,
                    txtModel.Text,
                    provider,
                    useLocal,
                    txtLocalBackend.Text);

                if (useLocal)
                {
                    // LocalGateway: use the backend's provider probe — one tiny chat
                    // request with a short timeout. Previously this ran a full
                    // multi-agent /generate (observed >7 min), which looked like a freeze.
                    var test = await testGenerator.TestProviderAsync();
                    bool ok = test != null && test.Ok;
                    txtTestResult.Text = ok
                        ? $"Connection OK: {test.Model} replied \"{(test.Reply ?? "").Trim()}\" in {test.LatencyMs}ms"
                        : $"Connection failed [{test?.Category ?? "unknown"}]: {test?.Detail ?? "(no detail)"}";
                    txtTestResult.Foreground = ok
                        ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
                        : new SolidColorBrush(Color.FromRgb(244, 67, 54));
                    return;
                }

                // Direct mode: probe the endpoint with one minimal generation,
                // capped so the button cannot sit on "Testing..." for minutes.
                var directTask = testGenerator.GenerateSclAsync("Hello, respond with OK");
                var finished = await Task.WhenAny(directTask, Task.Delay(TimeSpan.FromSeconds(30)));
                if (finished != directTask)
                {
                    txtTestResult.Text = "Test failed: timed out after 30s.";
                    txtTestResult.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                    return;
                }

                var result = await directTask;
                bool isError = string.IsNullOrEmpty(result)
                    || result.StartsWith("// API Call Error")
                    || result.StartsWith("// Error")
                    || result.StartsWith("// Agent Error");
                txtTestResult.Text = isError
                    ? "Connection failed: " + (result ?? "(empty)")
                    : "Connection OK: received " + result.Length + " chars";
                txtTestResult.Foreground = isError
                    ? new SolidColorBrush(Color.FromRgb(244, 67, 54))
                    : new SolidColorBrush(Color.FromRgb(76, 175, 80));
            }
            catch (System.Exception ex)
            {
                txtTestResult.Text = "Test failed: " + ex.Message;
                txtTestResult.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            }
            finally
            {
                btnTestConnection.IsEnabled = true;
                btnTestConnection.Content = "Test Connection";
            }
        }
    }

    public class OllamaModelItem
    {
        public string Name { get; set; }
        public long SizeBytes { get; set; }
        public double SizeMB { get; set; }
        public string Details { get; set; }
        public bool IsDefault { get; set; }
        public bool IsNotDefault { get; set; }
    }
}
