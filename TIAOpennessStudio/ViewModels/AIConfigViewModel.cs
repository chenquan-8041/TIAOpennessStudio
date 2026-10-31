using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TIAOpennessStudio.AICore;
using TIAOpennessStudio.Services.Abstractions;
using Prism.Events;
using Prism.Mvvm;
using TIAOpennessStudio.Events;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.ViewModels
{
    /// <summary>
    /// ViewModel for AI Configuration - handles config load/save, API testing, and backend management.
    /// UI dialog construction remains in MainWindow.AI.cs.
    /// </summary>
    public class AIConfigViewModel : BindableBase
    {
        private readonly IBackendHostService _backendHostService;
        private readonly IEventAggregator _eventAggregator;
        private AIConfig _config;

        private string _executionMode = "LocalGateway";
        private string _modelProvider = "OpenAICompat";
        private string _model = "";
        private string _apiKey = "";
        private string _endpoint = "";
        private string _localBackendUrl = "http://127.0.0.1:8000";
        private bool _includeContext;
        private bool _isTesting;
        private string _testResult = "";
        private string _modeHint = "";

        #region Properties

        public string ExecutionMode
        {
            get => _executionMode;
            set
            {
                if (SetProperty(ref _executionMode, value))
                    UpdateModeHint();
            }
        }

        public string ModelProvider
        {
            get => _modelProvider;
            set
            {
                if (SetProperty(ref _modelProvider, value))
                    UpdateModeHint();
            }
        }

        public string Model
        {
            get => _model;
            set => SetProperty(ref _model, value);
        }

        public string ApiKey
        {
            get => _apiKey;
            set => SetProperty(ref _apiKey, value);
        }

        public string Endpoint
        {
            get => _endpoint;
            set => SetProperty(ref _endpoint, value);
        }

        public string LocalBackendUrl
        {
            get => _localBackendUrl;
            set => SetProperty(ref _localBackendUrl, value);
        }

        public bool IncludeContext
        {
            get => _includeContext;
            set => SetProperty(ref _includeContext, value);
        }

        public bool IsTesting
        {
            get => _isTesting;
            set => SetProperty(ref _isTesting, value);
        }

        public string TestResult
        {
            get => _testResult;
            set => SetProperty(ref _testResult, value);
        }

        public string ModeHint
        {
            get => _modeHint;
            set => SetProperty(ref _modeHint, value);
        }

        public bool IsLocalGateway => string.Equals(_executionMode, "LocalGateway", StringComparison.OrdinalIgnoreCase);

        public AIConfig Config => _config;

        /// <summary>AI Engine Service reference (set by MainViewModel when service is updated)</summary>
        public IAIEngineService AIService { get; set; }

        #endregion

        public AIConfigViewModel(IBackendHostService backendHostService, IEventAggregator eventAggregator)
        {
            _backendHostService = backendHostService;
            _eventAggregator = eventAggregator;
            LoadConfig();
        }

        #region Configuration Management

        public void LoadConfig()
        {
            _config = AIConfig.Load();
            _executionMode = _config.GetEffectiveExecutionMode();
            _modelProvider = _config.GetEffectiveModelProvider();
            _model = _config.Model ?? _config.SelectedModel ?? "";
            _apiKey = _config.ApiKey ?? "";
            _endpoint = _config.Endpoint ?? "";
            _localBackendUrl = _config.LocalBackendUrl ?? "http://127.0.0.1:8000";
            _includeContext = _config.IncludeContext;

            RaisePropertyChanged(nameof(ExecutionMode));
            RaisePropertyChanged(nameof(ModelProvider));
            RaisePropertyChanged(nameof(Model));
            RaisePropertyChanged(nameof(ApiKey));
            RaisePropertyChanged(nameof(Endpoint));
            RaisePropertyChanged(nameof(LocalBackendUrl));
            RaisePropertyChanged(nameof(IncludeContext));
            RaisePropertyChanged(nameof(IsLocalGateway));

            UpdateModeHint();
        }

        public void SaveConfig()
        {
            _config.ExecutionMode = _executionMode;
            _config.ModelProvider = _modelProvider;
            _config.Provider = _modelProvider;
            _config.Model = _model;
            _config.SelectedModel = _model;
            _config.ApiKey = _apiKey;
            _config.Endpoint = _endpoint;
            _config.LocalBackendUrl = _localBackendUrl;
            _config.IncludeContext = _includeContext;
            _config.UseLocalBackend = IsLocalGateway;
            _config.UseRealAI = IsLocalGateway || !string.IsNullOrEmpty(_apiKey) || string.Equals(_modelProvider, "Ollama", StringComparison.OrdinalIgnoreCase);

            _config.Save();
            _eventAggregator.GetEvent<ConfigurationSavedEvent>().Publish(_config);
        }

        public void UpdateModel(string model)
        {
            _model = model;
            RaisePropertyChanged(nameof(Model));
        }

        public void UpdateEndpoint(string endpoint)
        {
            _endpoint = endpoint;
            RaisePropertyChanged(nameof(Endpoint));
        }

        public void UpdateApiKey(string apiKey)
        {
            _apiKey = apiKey;
            RaisePropertyChanged(nameof(ApiKey));
        }

        public void UpdateLocalBackendUrl(string url)
        {
            _localBackendUrl = url;
            RaisePropertyChanged(nameof(LocalBackendUrl));
        }

        #endregion

        #region API Testing

        public async Task<string> TestApiAsync()
        {
            IsTesting = true;
            TestResult = "";
            var diag = new System.Text.StringBuilder();

            try
            {
                diag.AppendLine($"模式: {(IsLocalGateway ? "Local Gateway" : "Direct API")}");
                diag.AppendLine($"提供商: {_modelProvider}");
                diag.AppendLine($"模型: {_model}");
                diag.AppendLine($"端点: {(IsLocalGateway ? _localBackendUrl : _endpoint)}");
                diag.AppendLine($"API Key: {(string.IsNullOrEmpty(_apiKey) ? "(空)" : _apiKey.Substring(0, Math.Min(8, _apiKey.Length)) + "***")}");
                diag.AppendLine();

                if (!IsLocalGateway && _modelProvider != "Ollama" && string.IsNullOrEmpty(_apiKey))
                {
                    diag.AppendLine("[错误] 未配置 API Key");
                    TestResult = diag.ToString();
                    return TestResult;
                }

                // Step 1: Health check (for local gateway)
                if (IsLocalGateway)
                {
                    diag.AppendLine("[1/2] 检查本地后端连接...");
                    try
                    {
                        var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                        var healthResp = await http.GetAsync(_localBackendUrl.TrimEnd('/') + "/health");
                        if (healthResp.IsSuccessStatusCode)
                        {
                            diag.AppendLine("  ✓ 后端服务正常");
                        }
                        else
                        {
                            diag.AppendLine($"  ✗ 后端返回: {healthResp.StatusCode}");
                        }
                    }
                    catch (Exception healthEx)
                    {
                        diag.AppendLine($"  ✗ 后端不可用: {healthEx.Message}");
                        diag.AppendLine("  提示: 请先运行 start_backend.ps1 启动后端服务");
                        TestResult = diag.ToString();
                        return TestResult;
                    }
                }

                // Step 2: Connectivity test.
                // The test is routed through the Python backend's /provider/test
                // endpoint, which uses the SAME LLM factory (adapter) that real
                // traffic uses. This keeps all platform-adapter knowledge in the
                // backend — the C# side never talks to each platform directly.
                diag.AppendLine($"[2/2] 测试 AI 模型响应...");
                IAICodeGenerator tempGenerator = new RestAICodeGenerator(_apiKey, _endpoint, _model, _modelProvider, IsLocalGateway, _localBackendUrl);
                var testResult = await tempGenerator.TestProviderAsync();

                if (testResult != null && testResult.Ok)
                {
                    var latency = testResult.LatencyMs.HasValue ? $"（{testResult.LatencyMs}ms）" : "";
                    diag.AppendLine($"  ✓ 模型响应{latency}: {(testResult.Reply ?? "").Trim()}");
                    diag.AppendLine();
                    diag.AppendLine("测试通过！所有连接正常。");
                }
                else
                {
                    var category = testResult?.Category ?? "unknown";
                    var detail = testResult?.Detail ?? "(无详细信息)";
                    diag.AppendLine($"  ✗ 失败 [{category}]: {detail}");
                    diag.AppendLine();
                    // Friendly diagnosis by the backend-classified category.
                    switch (category)
                    {
                        case "auth":
                            diag.AppendLine("诊断: API Key 无效、过期或无权限");
                            diag.AppendLine("建议: 检查并更新 API Key，确认该 Key 有权访问所选模型");
                            break;
                        case "model_not_found":
                            diag.AppendLine($"诊断: 该 Key 下不存在模型 “{_model}”");
                            diag.AppendLine("建议: 确认模型名拼写，或切换到该账号可用的模型");
                            break;
                        case "network":
                            diag.AppendLine("诊断: 无法连接到平台端点或本地后端");
                            diag.AppendLine("建议: 检查网络 / base_url；若为本地后端，先运行 start_backend.ps1");
                            break;
                        default:
                            diag.AppendLine("诊断: 未能明确分类的错误");
                            diag.AppendLine("建议: 查看上面的失败详情，或稍后重试");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                diag.AppendLine($"  ✗ 异常: {ex.Message}");
            }
            finally
            {
                IsTesting = false;
                TestResult = diag.ToString();
            }

            return TestResult;
        }

        #endregion

        #region Backend Management

        public async Task<bool> StartBackendAsync()
        {
            if (_backendHostService == null)
            {
                _eventAggregator.GetEvent<BackendStatusChangedEvent>().Publish("Backend host service not available");
                return false;
            }

            try
            {
                bool ok = await _backendHostService.EnsureStartedAsync();
                if (ok)
                {
                    _eventAggregator.GetEvent<BackendStatusChangedEvent>().Publish("Python Backend is ready.");
                    return true;
                }
                else
                {
                    _eventAggregator.GetEvent<BackendStatusChangedEvent>().Publish("Failed to start Python backend.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _eventAggregator.GetEvent<BackendStatusChangedEvent>().Publish($"Failed to start backend: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Model Provider Helpers

        public Dictionary<string, List<ModelInfo>> GetAvailableModels()
        {
            return AIConfig.GetAvailableModels();
        }

        public string GetDefaultEndpoint(string provider)
        {
            return AIConfig.GetDefaultEndpointForProvider(provider);
        }

        #endregion

        #region Private Helpers

        private void UpdateModeHint()
        {
            if (string.Equals(_executionMode, "LocalGateway", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_modelProvider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                ModeHint = "Preset: LocalGateway + Ollama. UI -> Local Python Gateway -> Ollama.";
            }
            else
            {
                ModeHint = "Preset: Direct + OpenAI-compatible. UI directly calls the configured endpoint.";
            }
            RaisePropertyChanged(nameof(IsLocalGateway));
        }

        #endregion
    }
}
