using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using TIAOpennessStudio.Controls;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Implementations;

namespace TIAOpennessStudio.ViewModels
{
    public class McpManagementViewModel : INotifyPropertyChanged
    {
        private readonly IMcpAdapter _mcpAdapter;
        private readonly McpToolRegistrySync _mcpToolRegistrySync;
        private static readonly string ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp_servers.json");
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        public ObservableCollection<McpServerItem> Servers { get; } = new ObservableCollection<McpServerItem>();
        public ObservableCollection<ToolInfoItem> Tools { get; } = new ObservableCollection<ToolInfoItem>();

        private McpServerItem _selectedServer;
        public McpServerItem SelectedServer
        {
            get => _selectedServer;
            set
            {
                _selectedServer = value;
                OnPropertyChanged();
                RaiseCommandsCanExecuteChanged();
                _ = LoadTools();
            }
        }

        private ToolInfoItem _selectedTool;
        public ToolInfoItem SelectedTool
        {
            get => _selectedTool;
            set { _selectedTool = value; OnPropertyChanged(); (TestToolCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
        }

        private string _searchText = "";
        public string SearchText { get => _searchText; set { _searchText = value; OnPropertyChanged(); FilterTools(); } }

        private string _statusMessage = "";
        public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; set { _isLoading = value; OnPropertyChanged(); } }

        public ICommand AddServerCommand { get; }
        public ICommand EditServerCommand { get; }
        public ICommand RemoveServerCommand { get; }
        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand TestToolCommand { get; }

        public McpManagementViewModel() : this(null, null) { }

        public McpManagementViewModel(IMcpAdapter mcpAdapter, McpToolRegistrySync mcpToolRegistrySync = null)
        {
            _mcpAdapter = mcpAdapter;
            _mcpToolRegistrySync = mcpToolRegistrySync;
            AddServerCommand = new RelayCommand(AddServer);
            EditServerCommand = new RelayCommand(EditServer, () => SelectedServer != null);
            RemoveServerCommand = new RelayCommand(RemoveServer, () => SelectedServer != null);
            ConnectCommand = new RelayCommand(async () => await ConnectAsync(), () => SelectedServer != null && !SelectedServer.IsConnected);
            DisconnectCommand = new RelayCommand(async () => await DisconnectAsync(), () => SelectedServer != null && SelectedServer.IsConnected);
            RefreshCommand = new RelayCommand(async () => await RefreshAsync());
            TestToolCommand = new RelayCommand(async () => await TestToolAsync(), () => SelectedTool != null && SelectedServer != null && SelectedServer.IsConnected);
            LoadSavedServers();
        }

        #region Persistence

        private void LoadSavedServers()
        {
            // Always ensure the built-in MCP Server exists
            EnsureBuiltInServer();

            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    var json = File.ReadAllText(ConfigFilePath);
                    var configs = JsonSerializer.Deserialize<List<McpServerConfig>>(json, JsonOptions);
                    if (configs != null)
                    {
                        foreach (var cfg in configs)
                        {
                            // Skip the built-in server (avoid duplicates)
                            if (cfg.Id == BuiltInServerId) continue;
                            Servers.Add(new McpServerItem
                            {
                                Id = cfg.Id ?? Guid.NewGuid().ToString("N"),
                                Name = cfg.Name ?? "Unnamed",
                                Url = cfg.Url ?? "",
                                TimeoutSeconds = cfg.TimeoutSeconds > 0 ? cfg.TimeoutSeconds : 30,
                                Headers = cfg.Headers ?? new Dictionary<string, string>()
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusMessage = "Failed to load saved servers: " + ex.Message;
            }
        }

        private const string BuiltInServerId = "tia-openness-builtin";

        private void EnsureBuiltInServer()
        {
            if (Servers.Any(s => s.Id == BuiltInServerId)) return;
            Servers.Insert(0, new McpServerItem
            {
                Id = BuiltInServerId,
                Name = "TIA Portal (Built-in)",
                Url = "http://127.0.0.1:8001/mcp",
                TimeoutSeconds = 30,
                Headers = new Dictionary<string, string>()
            });
        }

        private void SaveServers()
        {
            try
            {
                var configs = Servers.Select(s => new McpServerConfig
                {
                    Id = s.Id,
                    Name = s.Name,
                    Url = s.Url,
                    TimeoutSeconds = s.TimeoutSeconds,
                    Headers = s.Headers ?? new Dictionary<string, string>()
                }).ToList();

                var json = JsonSerializer.Serialize(configs, JsonOptions);
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception ex)
            {
                StatusMessage = "Failed to save servers: " + ex.Message;
            }
        }

        #endregion

        #region Server CRUD

        private void AddServer()
        {
            var dialog = new McpServerDialog();
            dialog.Owner = Application.Current?.MainWindow;
            if (dialog.ShowDialog() == true)
            {
                Servers.Add(new McpServerItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = dialog.ServerName,
                    Url = dialog.ServerUrl,
                    TimeoutSeconds = dialog.TimeoutSeconds,
                    Headers = dialog.Headers
                });
                SaveServers();
                StatusMessage = $"Added server: {dialog.ServerName}";
            }
        }

        private void EditServer()
        {
            if (SelectedServer == null) return;
            var dialog = new McpServerDialog(SelectedServer.Name, SelectedServer.Url,
                SelectedServer.TimeoutSeconds, SelectedServer.Headers);
            dialog.Owner = Application.Current?.MainWindow;
            if (dialog.ShowDialog() == true)
            {
                // If already connected, disconnect first
                if (SelectedServer.IsConnected)
                {
                    _ = DisconnectAsync();
                }
                SelectedServer.Name = dialog.ServerName;
                SelectedServer.Url = dialog.ServerUrl;
                SelectedServer.TimeoutSeconds = dialog.TimeoutSeconds;
                SelectedServer.Headers = dialog.Headers;
                SaveServers();
                StatusMessage = $"Updated server: {dialog.ServerName}";
            }
        }

        private void RemoveServer()
        {
            if (SelectedServer == null) return;
            var name = SelectedServer.Name;
            if (SelectedServer.IsConnected)
            {
                _ = DisconnectAsync();
            }
            Servers.Remove(SelectedServer);
            SelectedServer = null;
            SaveServers();
            StatusMessage = $"Removed server: {name}";
        }

        #endregion

        #region Connection

        private async Task ConnectAsync()
        {
            if (SelectedServer == null || _mcpAdapter == null) return;
            var server = SelectedServer;
            IsLoading = true; StatusMessage = "Connecting to " + server.Name + "...";
            try
            {
                var config = new McpServerConfig
                {
                    Id = server.Id,
                    Name = server.Name,
                    Url = server.Url,
                    TimeoutSeconds = server.TimeoutSeconds,
                    Headers = server.Headers ?? new Dictionary<string, string>()
                };

                bool success;
                if (_mcpToolRegistrySync != null)
                {
                    success = await _mcpToolRegistrySync.ConnectAndRegisterAsync(config);
                }
                else
                {
                    success = await _mcpAdapter.ConnectAsync(config);
                }

                // Ensure state is updated on the UI thread
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    server.IsConnected = success;
                    StatusMessage = success ? "Connected to " + server.Name : "Failed to connect to " + server.Name;
                    RaiseCommandsCanExecuteChanged();
                });
                if (success) await LoadTools();
            }
            catch (Exception ex) { StatusMessage = "Error: " + ex.Message; }
            finally { IsLoading = false; }
        }

        private async Task DisconnectAsync()
        {
            if (SelectedServer == null || _mcpAdapter == null) return;
            var server = SelectedServer;
            if (_mcpToolRegistrySync != null)
            {
                await _mcpToolRegistrySync.DisconnectAsync(server.Id);
            }
            else
            {
                await _mcpAdapter.DisconnectAsync(server.Id);
            }
            // Ensure state is updated on the UI thread
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                server.IsConnected = false;
                Tools.Clear();
                StatusMessage = "Disconnected from " + server.Name;
                RaiseCommandsCanExecuteChanged();
            });
        }

        private async Task RefreshAsync()
        {
            if (_mcpAdapter == null) return;
            foreach (var s in Servers)
            {
                try
                {
                    var status = _mcpAdapter.GetServerStatus(s.Id);
                    s.IsConnected = status?.Status == McpServerStatus.Connected;
                }
                catch { s.IsConnected = false; }
            }
            StatusMessage = "Servers refreshed";
            RaiseCommandsCanExecuteChanged();
            await Task.CompletedTask;
        }

        #endregion

        #region Tools

        private List<ToolInfoItem> _allTools = new List<ToolInfoItem>();

        private async Task LoadTools()
        {
            Tools.Clear(); _allTools.Clear();
            if (SelectedServer == null || !SelectedServer.IsConnected || _mcpAdapter == null) return;
            try
            {
                var tools = await _mcpAdapter.GetAvailableToolsAsync(SelectedServer.Id);
                foreach (var t in tools)
                {
                    var item = new ToolInfoItem { Name = t.Name, Description = t.Description, Parameters = t.InputSchema?.Select(p => new ToolParameterItem { Name = p.Name, Type = p.Type, Description = p.Description, Required = p.Required }).ToList() ?? new List<ToolParameterItem>() };
                    _allTools.Add(item); Tools.Add(item);
                }
                StatusMessage = "Loaded " + Tools.Count + " tools from " + SelectedServer.Name;
            }
            catch (Exception ex) { StatusMessage = "Failed to load tools: " + ex.Message; }
        }

        private void FilterTools()
        {
            Tools.Clear();
            var filtered = string.IsNullOrWhiteSpace(SearchText) ? _allTools : _allTools.Where(t => t.Name.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0 || t.Description.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            foreach (var t in filtered) Tools.Add(t);
        }

        private async Task TestToolAsync()
        {
            if (SelectedTool == null || SelectedServer == null || _mcpAdapter == null) return;
            IsLoading = true; StatusMessage = "Testing " + SelectedTool.Name + "...";
            try
            {
                var result = await _mcpAdapter.ExecuteToolAsync(SelectedServer.Id, SelectedTool.Name, new Dictionary<string, object>());
                StatusMessage = result.Success ? "Tool " + SelectedTool.Name + " succeeded" : "Tool failed: " + result.Error;
            }
            catch (Exception ex) { StatusMessage = "Error: " + ex.Message; }
            finally { IsLoading = false; }
        }

        #endregion

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void RaiseCommandsCanExecuteChanged()
        {
            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EditServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RemoveServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TestToolCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public class McpServerItem : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        private bool _isConnected;
        public bool IsConnected { get => _isConnected; set { _isConnected = value; OnPropertyChanged(); } }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ToolInfoItem
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<ToolParameterItem> Parameters { get; set; } = new List<ToolParameterItem>();
    }

    public class ToolParameterItem
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public bool Required { get; set; }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;
        public RelayCommand(Action execute, Func<bool> canExecute = null) { _execute = execute; _canExecute = canExecute; }

        public event EventHandler CanExecuteChanged
        {
            add { System.Windows.Input.CommandManager.RequerySuggested += value; }
            remove { System.Windows.Input.CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object parameter) => _execute();
        public void RaiseCanExecuteChanged() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
