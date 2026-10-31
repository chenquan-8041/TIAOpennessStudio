using System;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Models;

using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Blocks;

namespace TIAOpennessStudio.AICore
{
    public interface IAICodeGenerator : IAIEngineService
    {
        /// <summary>
        /// Generates SCL code based on a natural language prompt.
        /// </summary>
        /// <param name="prompt">The user's description of the function.</param>
        /// <param name="context">Optional project context (Tags, UDTs) to guide generation.</param>
        /// <returns>Generated SCL code.</returns>
        new Task<string> GenerateSclAsync(string prompt, string context = "", List<Dictionary<string, string>> history = null, string rid = null, string intent = "");

        /// <summary>
        /// Converts SCL code or logic into TIA Portal compatible XML format.
        /// </summary>
        /// <param name="sclCode">The SCL code.</param>
        /// <param name="blockName">Name of the block to create.</param>
        /// <returns>XML string content.</returns>
        [Obsolete("Use ImportBlockCodeAsync with .scl path directly", false)]
        new Task<string> GenerateXmlAsync(string sclCode, string blockName = "", string blockType = "FB", string rid = null);

        /// <summary>
        /// Generates SCL code and provides streaming feedback.
        /// </summary>
        new Task GenerateSclStreamingAsync(string prompt, Action<string, string> onChunkReceived, string context = "", List<Dictionary<string, string>> history = null, System.Threading.CancellationToken ct = default, string rid = null);

        /// <summary>
        /// Converts TIA XML (LAD/FBD/etc.) to code using XML-AST assisted backend.
        /// </summary>
        new Task<string> ConvertXmlAsync(string xmlContent, string instruction = "", string mode = "scl", string rid = null);
    }

    /// <summary>
    /// Implements AI calls via standard HTTP requests (equivalent to cURL).
    /// </summary>
    public class RestAICodeGenerator : IAICodeGenerator
    {
        private string _apiKey;
        private string _endpoint;
        private string _model;
        private string _provider;
        private string _reasoningEffort = "medium";
        private readonly string _localBackendUrl;
        private readonly bool _useLocalBackend;
        private readonly string _backendAuthToken;
        private static readonly HttpClient client = new HttpClient();
        // Hard cap for the /provider/test probe. The shared HttpClient's default
        // timeout is 100s, far longer than users tolerate on a "Test API" click.
        private const int ProviderTestTimeoutMs = 40000;
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        public RestAICodeGenerator(string apiKey, string endpoint, string model, string provider, bool useLocalBackend = true, string localBackendUrl = "http://127.0.0.1:8000", string reasoningEffort = "medium")
        {
            _apiKey = apiKey;
            _endpoint = endpoint;
            _model = model;
            _provider = provider;
            _reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? "medium" : reasoningEffort.Trim();
            _useLocalBackend = useLocalBackend;
            _localBackendUrl = string.IsNullOrWhiteSpace(localBackendUrl) ? "http://127.0.0.1:8000" : localBackendUrl.Trim();
            _backendAuthToken = Environment.GetEnvironmentVariable("TIA_BACKEND_TOKEN") ?? "";
        }

        private void ApplyAuthHeader(HttpRequestMessage request)
        {
            // Lazily read token in case it was set after construction (e.g. by BackendHostService)
            var token = Environment.GetEnvironmentVariable("TIA_BACKEND_TOKEN") ?? _backendAuthToken;
            if (!string.IsNullOrEmpty(token) && _useLocalBackend)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
        }

        public void UpdateRuntimeConfig(string apiKey = null, string endpoint = null, string model = null, string provider = null, string reasoningEffort = null)
        {
            if (apiKey != null) _apiKey = apiKey;
            if (endpoint != null) _endpoint = endpoint;
            if (model != null)
            {
                _model = model;
                AppLogger.Log($"[AICodeGenerator] Model updated to: {_model}");
            }
            if (provider != null) _provider = provider;
            if (reasoningEffort != null)
            {
                _reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? "medium" : reasoningEffort.Trim();
            }
        }

        /// <inheritdoc />
        public string ModelName => _model;

        /// <inheritdoc />
        public string ModelProvider => BuildBackendProvider();

        private string BuildBackendProvider()
        {
            // Auto-detect Ollama from model name (models with ":" like qwen2.5:7b)
            // This handles the case where the app restarts with an Ollama model
            // but the persisted Provider is still "OpenAI" or "Custom".
            if (AIConfig.IsLocalLlmModel(_model))
                return "ollama";
            if (string.Equals(_provider, "OpenAI", StringComparison.OrdinalIgnoreCase)) return "openai_compat";
            if (string.Equals(_provider, "Ollama", StringComparison.OrdinalIgnoreCase)) return "ollama";
            return "auto";
        }

        private static string ExtractBackendErrorMessage(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText)) return "";
            try
            {
                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;
                if (root.TryGetProperty("detail", out var detail) && detail.ValueKind != JsonValueKind.Null)
                    return detail.GetString() ?? responseText.Trim();
                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    return error.GetString() ?? responseText.Trim();
                return responseText.Trim();
            }
            catch
            {
                return responseText.Trim();
            }
        }
        
        private static string NormalizeBase(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "http://127.0.0.1:8000";
            var u = url.Trim();
            if (!u.StartsWith("http://") && !u.StartsWith("https://")) u = "http://" + u;
            if (u.Contains("localhost")) u = u.Replace("localhost", "127.0.0.1");
            return u.TrimEnd('/');
        }

        private bool IsLocalLikeEndpoint(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return true;
            var u = url.Trim().ToLowerInvariant();
            return u.Contains("127.0.0.1") || u.Contains("localhost");
        }

        private string BuildProviderBaseUrl()
        {
            if (!_useLocalBackend) return _endpoint ?? string.Empty;

            // Auto-detect Ollama from model name — use local Ollama endpoint
            if (AIConfig.IsLocalLlmModel(_model))
                return "http://localhost:11434/v1/chat/completions";

            if (string.IsNullOrWhiteSpace(_endpoint)) return string.Empty;

            var endpointBase = NormalizeBase(_endpoint);
            var backendBase = NormalizeBase(_localBackendUrl);

            // In local-backend mode, _endpoint might (incorrectly) be set to backend itself.
            // Passing backend base_url to Python causes recursive/invalid upstream calls.
            if (string.Equals(endpointBase, backendBase, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return _endpoint ?? string.Empty;
        }

        public async Task<string> GenerateSclAsync(string prompt, string context = "", List<Dictionary<string, string>> history = null, string rid = null, string intent = "")
        {
            // Determine if we should use Python backend (Agent) or direct API (OpenAI)
            // Priority: _useLocalBackend config
            // If _useLocalBackend = true, always use local backend (ignore Endpoint)
            // If _useLocalBackend = false, use direct API with configured Endpoint
            
            AppLogger.Log($"[DEBUG] UseLocalBackend={_useLocalBackend}, Endpoint={_endpoint}");
            
            // If user explicitly wants local backend, force it regardless of Endpoint setting
            bool isPythonBackend = _useLocalBackend;
            
            // If not using local backend, check if Endpoint is valid for direct API
            if (!isPythonBackend)
            {
                if (string.IsNullOrEmpty(_endpoint))
                {
                    // No endpoint configured, fall back to local backend
                    isPythonBackend = true;
                    AppLogger.Log($"[DEBUG] No endpoint configured, falling back to local backend");
                }
            }

            AppLogger.Log($"[DEBUG] isPythonBackend={isPythonBackend}");

            if (!isPythonBackend)
            {
                AppLogger.Log($"[DEBUG] Using direct API mode with endpoint: {_endpoint}");
                return await GenerateSclDirectAsync(prompt, context, history);
            }

            string baseUrl = BuildBackendBaseUrl(_endpoint);
            string endpointUrl = baseUrl.TrimEnd('/') + "/generate";
            
            AppLogger.Log($"[DEBUG] BaseUrl={baseUrl}, EndpointUrl={endpointUrl}");

            var actualRid = string.IsNullOrWhiteSpace(rid) ? Guid.NewGuid().ToString("N").Substring(0, 8) : rid;
            var requestData = new
            {
                api_key = _apiKey,
                model = _model,
                provider = BuildBackendProvider(),
                base_url = BuildProviderBaseUrl(),
                rid = actualRid,
                prompt = prompt,
                context = context,
                history = history ?? new List<Dictionary<string, string>>(),
                intent = intent ?? ""
            };

            var jsonContent = JsonSerializer.Serialize(requestData, _jsonOptions);

            int maxRetries = 3;
            for (int retry = 0; retry < maxRetries; retry++)
            {
                try
                {
                    if (retry == 0)
                    {
                        AppLogger.Log($"[DEBUG] Checking backend health: {baseUrl}/health");
                        bool ok = await CheckBackendHealthAsync(baseUrl, 3000);
                        AppLogger.Log($"[DEBUG] Health check result: {ok}");
                        if (!ok)
                        {
                            AppLogger.Log($"[ERROR] Backend health check failed for {baseUrl}");
                            return "// API Call Error: Backend not available";
                        }
                    }
                    AppLogger.Log($"[POST] Sending request to AI Backend: {endpointUrl} (attempt {retry + 1}/{maxRetries})");
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
                    {
                        Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
                    };
                    ApplyAuthHeader(request);
                    using var response = await client.SendAsync(request);
                    var responseString = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        // Handle 429 Rate Limit with Retry-After backoff
                        if ((int)response.StatusCode == 429 && retry < maxRetries - 1)
                        {
                            int retryAfter = 2; // default
                            try
                            {
                                using var doc = JsonDocument.Parse(responseString);
                                if (doc.RootElement.TryGetProperty("retry_after", out var ra) && ra.TryGetInt32(out var val))
                                    retryAfter = val;
                            }
                            catch { }
                            // Also check Retry-After header
                            if (response.Headers.TryGetValues("Retry-After", out var headerVals))
                            {
                                foreach (var hv in headerVals)
                                {
                                    if (int.TryParse(hv, out var hvInt))
                                    {
                                        retryAfter = Math.Max(retryAfter, hvInt);
                                        break;
                                    }
                                }
                            }
                            AppLogger.Log($"[WARN] Rate limited (429), retrying after {retryAfter}s (attempt {retry + 1}/{maxRetries})");
                            await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                            continue;
                        }
                        AppLogger.Log($"[ERROR] AI Backend Error: {response.StatusCode}");
                        throw new Exception($"Python API Request Failed: {response.StatusCode}\nDetails: {responseString}");
                    }

                    var jsonResponse = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseString);

                    if (jsonResponse != null && jsonResponse.TryGetValue("code", out var codeEl))
                    {
                        string code = codeEl.GetString() ?? string.Empty;

                        if (jsonResponse.TryGetValue("thinking", out var thinkingValue) &&
                            thinkingValue.ValueKind != JsonValueKind.Null &&
                            !string.IsNullOrWhiteSpace(thinkingValue.GetString()))
                        {
                            AppLogger.Log("[AI] Generation successful (captured thinking process)");
                        }
                        return code;
                    }

                    return "// Error: Unexpected response format from Python backend. Response: " + responseString;
                }
                catch (Exception ex) when (!(ex is TaskCanceledException))
                {
                    if (retry < maxRetries - 1)
                    {
                        int delay = (int)Math.Pow(2, retry + 1); // 2s, 4s backoff
                        AppLogger.Log($"[WARN] Request failed, retrying in {delay}s (attempt {retry + 1}/{maxRetries}): {ex.Message}");
                        await Task.Delay(TimeSpan.FromSeconds(delay));
                        continue;
                    }
                    AppLogger.LogError("AI API Call Failed", ex);
                    return $"// API Call Error: {ex.Message}\n// Please ensure the Python backend is running (python run.py)";
                }
            }
            return "// API Call Error: Max retries exceeded";
        }

        private async Task<string> GenerateSclDirectAsync(string prompt, string context, List<Dictionary<string, string>> history)
        {
            try
            {
                // Direct Call to OpenAI Standard API
                var messages = new List<object>
                {
                    new { role = "system", content = "You are an expert Siemens TIA Portal SCL programmer. Output ONLY valid SCL code." }
                };
                if (history != null)
                {
                    foreach (var item in history)
                    {
                        if (item == null) continue;
                        string role = item.ContainsKey("role") ? item["role"] : "";
                        string contentValue = item.ContainsKey("content") ? item["content"] : "";
                        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(contentValue)) continue;
                        var normalizedRole = role.Trim().ToLowerInvariant();
                        if (normalizedRole != "user" && normalizedRole != "assistant") continue;
                        messages.Add(new { role = normalizedRole, content = contentValue });
                    }
                }
                messages.Add(new { role = "user", content = $"Requirement: {prompt}\n\nContext:\n{context}" });

                var requestData = new
                {
                    model = _model,
                    messages = messages,
                    temperature = 0.1
                };

                var jsonContent = JsonSerializer.Serialize(requestData, _jsonOptions);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                AppLogger.Log($"[POST] Sending request to Direct API: {_endpoint}");

                var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                request.Headers.Add("Authorization", $"Bearer {_apiKey}");
                request.Content = content;

                var response = await client.SendAsync(request);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                     throw new Exception($"API Request Failed: {response.StatusCode}\n{responseString}");
                }

                var jsonResponse = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseString);
                // Simple parsing for OpenAI format: choices[0].message.content
                if (jsonResponse != null && jsonResponse.TryGetValue("choices", out var choicesEl) && choicesEl.ValueKind == JsonValueKind.Array)
                {
                    var choices = choicesEl.EnumerateArray();
                    foreach (var choice in choices)
                    {
                        if (choice.TryGetProperty("message", out var messageEl) && messageEl.TryGetProperty("content", out var contentEl))
                        {
                            return contentEl.GetString() ?? "";
                        }
                    }
                }

                return "// Error: Unexpected response format from Direct API. Response: " + responseString;
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Direct API Call Failed", ex);
                return $"// API Call Error: {ex.Message}";
            }
        }

        [Obsolete("Use ImportBlockCodeAsync with .scl path directly", false)]
        public async Task<string> GenerateXmlAsync(string sclCode, string blockName = "", string blockType = "FB", string rid = null)
        {
            // For now, we reuse the template logic since XML generation is deterministic
            // In a real scenario, you might also ask AI to format the XML if the template is insufficient
            await Task.Delay(100); 
            
            // Clean up the SCL code for XML embedding (basic escaping)
            string safeScl = (sclCode ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
            string safeName = string.IsNullOrWhiteSpace(blockName) ? "AI_Block" : blockName;
            string safeType = string.IsNullOrWhiteSpace(blockType) ? "FB" : blockType;

            // This is a basic standard template that wraps SCL logic into a valid TIA Portal Openness XML structure.
            // Future enhancement: Parse SCL interface section to dynamically generate <Interface> XML nodes instead of empty sections.
             string xmlTemplate = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                          <Document>
                            <Engineering version=""V15.1"" />
                            <SW.Blocks.{safeType} ID=""0"">
                              <AttributeList>
                                <AutoNumber>true</AutoNumber>
                                <HeaderAuthor>AI_Gen</HeaderAuthor>
                                <HeaderName>{safeName}</HeaderName>
                                <Interface><Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v3"">
                            <Section Name=""Input"" />
                            <Section Name=""Output"" />
                            <Section Name=""InOut"" />
                            <Section Name=""Static"" />
                            <Section Name=""Temp"" />
                            <Section Name=""Constant"" />
                          </Sections></Interface>
                                <Name>{safeName}</Name>
                                <ProgrammingLanguage>SCL</ProgrammingLanguage>
                              </AttributeList>
                              <ObjectList>
                                <SW.Blocks.CompileUnit ID=""3"" CompositionName=""CompileUnits"">
                                  <AttributeList>
                                    <CompositionName>CompileUnits</CompositionName>
                                    <NetworkSource><FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v3"">
                            <Parts />
                            <Wires />
                          </FlgNet></NetworkSource>
                                    <ProgrammingLanguage>SCL</ProgrammingLanguage>
                                  </AttributeList>
                                  <ObjectList>
                                    <MultilingualText ID=""4"" CompositionName=""Source"">
                                      <ObjectList>
                                        <MultilingualTextItem ID=""5"" CompositionName=""Items"">
                                          <AttributeList>
                                            <Culture>en-US</Culture>
                                            <Text>{safeScl}</Text>
                                          </AttributeList>
                                        </MultilingualTextItem>
                                      </ObjectList>
                                    </MultilingualText>
                                  </ObjectList>
                                </SW.Blocks.CompileUnit>
                              </ObjectList>
                            </SW.Blocks.{safeType}>
                          </Document>";
            return xmlTemplate;
        }

        public async Task GenerateSclStreamingAsync(string prompt, Action<string, string> onChunkReceived, string context = "", List<Dictionary<string, string>> history = null, System.Threading.CancellationToken ct = default, string rid = null)
        {
            // Delegate to ExAsync — the single real implementation.
            // The type-based callback adapter preserves backward compatibility for
            // all existing callers while eliminating duplicate SSE parsing code.
            await GenerateSclStreamingExAsync(prompt, ev =>
            {
                if (ev == null) return;
                switch (ev.EventType)
                {
                    case "thinking":
                    case "content":
                    case "delta":
                        onChunkReceived?.Invoke(ev.EventType, ev.TextContent ?? "");
                        break;
                    case "error":
                        onChunkReceived?.Invoke("error", ev.ErrorMessage ?? ev.TextContent ?? "");
                        break;
                    case "done":
                        onChunkReceived?.Invoke("thinking", "done");
                        break;
                    // tool_request, thought, file_change, command, status, context_ref
                    // are ignored by the legacy callback — they only make sense in
                    // the typed SseEvent pipeline.
                    default:
                        break;
                }
            }, context, history, ct, rid);
        }

        public async Task<string> ConvertXmlAsync(string xmlContent, string instruction = "", string mode = "scl", string rid = null)
        {
            if (string.IsNullOrWhiteSpace(xmlContent)) return "";

            string baseUrl = BuildBackendBaseUrl(_endpoint);
            string endpointUrl = baseUrl.TrimEnd('/') + "/convert_xml";
            var actualRid = string.IsNullOrWhiteSpace(rid) ? Guid.NewGuid().ToString("N").Substring(0, 8) : rid;

            // Project the canonical IR here (one C# contract) so the backend never re-parses
            // TIA XML into a second, lossy IR of its own.
            var projection = await Task.Run(() =>
            {
                string ir = null, summary = null;
                try
                {
                    var snapshot = BlockSnapshotService.FromXml(xmlContent);
                    if (!BlockSnapshotService.BuildIr(snapshot, 1, 0, out ir, out summary))
                    {
                        ir = null;
                        summary = null;
                    }
                }
                catch
                {
                    ir = null;
                    summary = null;
                }
                return (ir, summary);
            }).ConfigureAwait(false);
            var agentIr = projection.ir ?? "";
            var agentIrSummary = projection.summary ?? "";

            var requestData = new
            {
                api_key = _apiKey,
                model = _model,
                provider = BuildBackendProvider(),
                base_url = BuildProviderBaseUrl(),
                rid = actualRid,
                xml = xmlContent,
                instruction = instruction ?? "",
                mode = mode ?? "scl",
                agent_ir = agentIr,
                agent_ir_summary = agentIrSummary
            };
            var content = new StringContent(JsonSerializer.Serialize(requestData, _jsonOptions), Encoding.UTF8, "application/json");

            try
            {
                bool ok = await CheckBackendHealthAsync(baseUrl, 3000);
                if (!ok) return "// xml-to-scl backend unavailable";

                var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl) { Content = content };
                ApplyAuthHeader(request);
                var response = await client.SendAsync(request);
                var responseString = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    var errorMessage = ExtractBackendErrorMessage(responseString);
                    if (!string.IsNullOrWhiteSpace(errorMessage))
                        return "// xml-to-scl error: " + response.StatusCode + " - " + errorMessage;
                    return "// xml-to-scl error: " + response.StatusCode;
                }

                var jsonResponse = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseString);
                if (jsonResponse != null)
                {
                    var sclCode = jsonResponse.TryGetValue("scl_code", out var sclEl) ? sclEl.GetString() ?? "" : "";
                    var analysis = jsonResponse.TryGetValue("analysis", out var anaEl) ? anaEl.GetString() ?? "" : "";

                    if (string.Equals(mode, "analysis", StringComparison.OrdinalIgnoreCase))
                        return !string.IsNullOrWhiteSpace(analysis) ? analysis : sclCode;

                    if (!string.IsNullOrWhiteSpace(sclCode))
                        return sclCode;

                    if (!string.IsNullOrWhiteSpace(analysis))
                        return analysis;
                }

                if (jsonResponse != null && jsonResponse.TryGetValue("error", out var errEl) && errEl.ValueKind != JsonValueKind.Null)
                    return "// xml-to-scl error: " + errEl.ToString();

                return "// xml-to-scl unexpected response";
            }
            catch (Exception ex)
            {
                AppLogger.LogError("ConvertXmlAsync failed", ex);
                return "// xml-to-scl exception: " + ex.Message;
            }
        }

        /// <summary>
        /// Probes whether the current model supports image/multimodal input by calling the Python backend /probe_vision.
        /// Returns a three-state string: "supported" | "unsupported" | "unknown".
        /// The backend sends a tiny embedded PNG with max_tokens=1, so the probe is very cheap.
        /// Any local/network failure maps to "unknown"; it never throws, letting the caller fall back to the built-in list.
        /// Only effective when using the local backend (_useLocalBackend); direct mode returns "unknown".
        /// </summary>
        public async Task<string> ProbeVisionAsync(string rid = null)
        {
            if (!_useLocalBackend) return "unknown";

            string baseUrl = BuildBackendBaseUrl(_endpoint);
            string endpointUrl = baseUrl.TrimEnd('/') + Services.ApiEndpoints.ProbeVision;
            var actualRid = string.IsNullOrWhiteSpace(rid) ? Guid.NewGuid().ToString("N").Substring(0, 8) : rid;
            var requestData = new
            {
                api_key = _apiKey,
                model = _model,
                provider = BuildBackendProvider(),
                base_url = BuildProviderBaseUrl(),
                rid = actualRid,
            };
            var content = new StringContent(JsonSerializer.Serialize(requestData, _jsonOptions), Encoding.UTF8, "application/json");

            try
            {
                bool ok = await CheckBackendHealthAsync(baseUrl, 3000);
                if (!ok) return "unknown";

                using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl) { Content = content };
                ApplyAuthHeader(request);
                var response = await client.SendAsync(request);
                var responseString = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) return "unknown";

                var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseString);
                if (json != null && json.TryGetValue("result", out var resEl) && resEl.ValueKind == JsonValueKind.String)
                {
                    var result = resEl.GetString() ?? "unknown";
                    AppLogger.Log($"[ProbeVision] model={_model} result={result}");
                    return result;
                }
                return "unknown";
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ProbeVision] failed model={_model}: {ex.Message}");
                return "unknown";
            }
        }

        /// <summary>
        /// Test the current provider/model/key/base_url via Python /provider/test.
        /// Sends one tiny real chat request through the backend's LLM factory so
        /// the test exercises the exact adapter real traffic uses. Never throws;
        /// returns a structured result. The api_key is never echoed back.
        /// </summary>
        public async Task<ProviderTestResult> TestProviderAsync(string rid = null)
        {
            if (!_useLocalBackend)
            {
                return new ProviderTestResult
                {
                    Ok = false,
                    Category = "unknown",
                    Model = _model,
                    Detail = "Provider test requires the local backend (direct mode not supported).",
                };
            }

            string baseUrl = BuildBackendBaseUrl(_endpoint);
            string endpointUrl = baseUrl.TrimEnd('/') + Services.ApiEndpoints.ProviderTest;
            var actualRid = string.IsNullOrWhiteSpace(rid) ? Guid.NewGuid().ToString("N").Substring(0, 8) : rid;
            var requestData = new
            {
                api_key = _apiKey,
                model = _model,
                provider = BuildBackendProvider(),
                base_url = BuildProviderBaseUrl(),
                rid = actualRid,
            };
            var content = new StringContent(JsonSerializer.Serialize(requestData, _jsonOptions), Encoding.UTF8, "application/json");

            try
            {
                // Two attempts are enough: the caller already health-checked once
                // before reaching here, so retrying four times only adds latency.
                bool ok = await CheckBackendHealthAsync(baseUrl, 3000, attempts: 2);
                if (!ok)
                {
                    return new ProviderTestResult
                    {
                        Ok = false,
                        Category = "network",
                        Model = _model,
                        Detail = "Backend not reachable. Start the Python backend first.",
                    };
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl) { Content = content };
                ApplyAuthHeader(request);
                // Bound the wait explicitly: the backend's own LLM timeout is much
                // longer than the default HttpClient timeout, so without this the
                // button sits on "Testing..." for well over a minute.
                using var cts = new System.Threading.CancellationTokenSource(ProviderTestTimeoutMs);
                var response = await client.SendAsync(request, cts.Token);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new ProviderTestResult
                    {
                        Ok = false,
                        Category = "unknown",
                        Model = _model,
                        Detail = $"Backend returned {(int)response.StatusCode}.",
                    };
                }

                var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseString);
                var result = new ProviderTestResult { Model = _model };
                if (json != null)
                {
                    if (json.TryGetValue("ok", out var okEl) && (okEl.ValueKind == JsonValueKind.True || okEl.ValueKind == JsonValueKind.False))
                        result.Ok = okEl.GetBoolean();
                    if (json.TryGetValue("category", out var catEl) && catEl.ValueKind == JsonValueKind.String)
                        result.Category = catEl.GetString() ?? "unknown";
                    if (json.TryGetValue("latency_ms", out var latEl) && latEl.ValueKind == JsonValueKind.Number)
                        result.LatencyMs = latEl.GetInt32();
                    if (json.TryGetValue("model", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                        result.Model = mEl.GetString() ?? _model;
                    if (json.TryGetValue("reply", out var rEl) && rEl.ValueKind == JsonValueKind.String)
                        result.Reply = rEl.GetString();
                    if (json.TryGetValue("detail", out var dEl) && dEl.ValueKind == JsonValueKind.String)
                        result.Detail = dEl.GetString();
                }
                AppLogger.Log($"[ProviderTest] model={result.Model} ok={result.Ok} category={result.Category} latency={result.LatencyMs}ms");
                return result;
            }
            catch (OperationCanceledException)
            {
                AppLogger.Log($"[ProviderTest] timeout model={_model} after {ProviderTestTimeoutMs}ms");
                return new ProviderTestResult
                {
                    Ok = false,
                    Category = "network",
                    Model = _model,
                    Detail = $"连接测试超时（{ProviderTestTimeoutMs / 1000} 秒）：平台端点或本地后端未响应。",
                };
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ProviderTest] failed model={_model}: {ex.Message}");
                return new ProviderTestResult
                {
                    Ok = false,
                    Category = "unknown",
                    Model = _model,
                    Detail = ex.Message,
                };
            }
        }

        private string BuildBackendBaseUrl(string endpoint)
        {
            if (_useLocalBackend) return NormalizeBase(_localBackendUrl);
            string url = endpoint;
            if (string.IsNullOrEmpty(url)) return "http://127.0.0.1:8000";
            if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "http://" + url;
            if (url.Contains("localhost")) url = url.Replace("localhost", "127.0.0.1");
            if (url.EndsWith("/generate")) url = url.Substring(0, url.Length - "/generate".Length);
            return url;
        }

        private async Task<bool> CheckBackendHealthAsync(string baseUrl, int timeoutMs, int attempts = 4)
        {
            string healthUrl = baseUrl.TrimEnd('/') + "/health";
            int delayMs = 500;

            for (int i = 1; i <= attempts; i++)
            {
                try
                {
                    var cts = new System.Threading.CancellationTokenSource(timeoutMs);
                    var req = new HttpRequestMessage(HttpMethod.Get, healthUrl);
                    AppLogger.Log($"[DEBUG] Sending health check to: {healthUrl}");
                    using (var resp = await client.SendAsync(req, cts.Token))
                    {
                        AppLogger.Log($"[DEBUG] Health check response: {resp.StatusCode}");
                        if (resp.IsSuccessStatusCode) return true;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[DEBUG] Health check exception (attempt {i}/{attempts}): {ex.GetType().Name}: {ex.Message}");
                }

                if (i < attempts)
                {
                    await Task.Delay(delayMs);
                }
            }
            return false;
        }

        public async Task GenerateSclStreamingExAsync(string prompt, Action<SseEvent> onEventReceived, string context = "", List<Dictionary<string, string>> history = null, System.Threading.CancellationToken ct = default, string rid = null, string intent = "", string planningKnowledge = "", List<string> images = null, string projectKey = "", bool evolutionEnabled = false)
        {
            await StreamFromEndpointAsync("/generate_stream", prompt, onEventReceived, context, history, ct, rid, intent, planningKnowledge, images, projectKey, evolutionEnabled);
        }

        /// <summary>
        /// Stream from Python /chat_plan endpoint (cloud models only).
        /// Same SSE protocol as /generate_stream; the backend runs the multi-agent supervisor.
        /// Ollama/local models never use this endpoint; the backend rejects them fail-closed.
        /// </summary>
        public async Task GenerateChatPlanStreamAsync(string prompt, Action<SseEvent> onEventReceived, string context = "", List<Dictionary<string, string>> history = null, System.Threading.CancellationToken ct = default, string rid = null, string intent = "", string planningKnowledge = "", string projectKey = "")
        {
            await StreamFromEndpointAsync("/chat_plan", prompt, onEventReceived, context, history, ct, rid, intent, planningKnowledge, null, projectKey);
        }

        /// <inheritdoc />
        public async Task<string> PostPlanDecisionAsync(string planId, string phaseId, string decision, string note = null)
        {
            try
            {
                string baseUrl = BuildBackendBaseUrl(_endpoint);
                string url = baseUrl.TrimEnd('/') + "/plan/decide";
                var payload = JsonSerializer.Serialize(new
                {
                    plan_id = planId ?? "",
                    phase_id = phaseId ?? "",
                    decision = decision ?? "",
                    note = note
                }, _jsonOptions);

                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                ApplyAuthHeader(request);

                using var resp = await client.SendAsync(request).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    AppLogger.Log($"[PLAN] /plan/decide failed ({resp.StatusCode}): {body}");
                    return null;
                }
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                return root.TryGetProperty("plan_status", out var ps) ? ps.GetString() : null;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PLAN] /plan/decide error: {ex.Message}");
                return null;
            }
        }

        /// <inheritdoc />
        public async Task<string> PostTaskDecisionAsync(string planId, string taskId, string decision)
        {
            try
            {
                string baseUrl = BuildBackendBaseUrl(_endpoint);
                string url = baseUrl.TrimEnd('/') + "/plan/task_decide";
                var payload = JsonSerializer.Serialize(new
                {
                    plan_id = planId ?? "",
                    task_id = taskId ?? "",
                    decision = decision ?? ""
                }, _jsonOptions);
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                ApplyAuthHeader(request);

                using var resp = await client.SendAsync(request).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    AppLogger.Log($"[PLAN] /plan/task_decide failed ({resp.StatusCode}): {body}");
                    return null;
                }
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                return root.TryGetProperty("plan_status", out var ps) ? ps.GetString() : null;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PLAN] /plan/task_decide error: {ex.Message}");
                return null;
            }
        }

        /// <inheritdoc />
        public async Task<PlanStartResult> StartPlanAsync(string prompt, string context = "", List<Dictionary<string, string>> history = null, string intent = "", string planningKnowledge = "", string projectKey = "", bool verifyCompileAtPlanEnd = false)
        {
            var result = new PlanStartResult { Started = false };
            try
            {
                string baseUrl = BuildBackendBaseUrl(_endpoint);
                string url = baseUrl.TrimEnd('/') + "/plan/run";
                string rid = string.IsNullOrWhiteSpace(Services.RunContext.CurrentRid)
                    ? Guid.NewGuid().ToString("N").Substring(0, 8)
                    : Services.RunContext.CurrentRid;
                var payload = JsonSerializer.Serialize(new
                {
                    api_key = _apiKey,
                    model = _model,
                    provider = BuildBackendProvider(),
                    base_url = BuildProviderBaseUrl(),
                    rid = rid,
                    prompt = prompt,
                    context = context ?? "",
                    history = history ?? new List<Dictionary<string, string>>(),
                    intent = intent ?? "",
                    planning_knowledge = planningKnowledge ?? "",
                    project_key = projectKey ?? "",
                    verify_compile_at_end = verifyCompileAtPlanEnd,
                    reasoning_effort = _reasoningEffort,
                    // The model's real input window. Without this the Python backend
                    // falls back to a conservative 128K default, so a 1M-window model
                    // reports a bogus "96% used" and its compact/clear thresholds are
                    // derived from the wrong window.
                    context_window = TokenBudget.ResolveModelWindow(_model),
                }, _jsonOptions);

                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                ApplyAuthHeader(request);

                using var resp = await client.SendAsync(request).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    AppLogger.Log($"[PLAN] /plan/run failed ({resp.StatusCode}): {body}");
                    return result;
                }
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                result.Started = root.TryGetProperty("started", out var s) && s.GetBoolean();
                result.PlanId = root.TryGetProperty("plan_id", out var p) ? p.GetString() : "";
                result.Status = root.TryGetProperty("status", out var st) ? st.GetString() : "";
                result.Summary = root.TryGetProperty("summary", out var su) ? su.GetString() : "";
                result.Total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PLAN] /plan/run error: {ex.Message}");
                return result;
            }
        }

        /// <inheritdoc />
        public async Task<PlanStatusSnapshot> GetPlanStatusAsync(string planId)
        {
            try
            {
                string baseUrl = BuildBackendBaseUrl(_endpoint);
                string url = baseUrl.TrimEnd('/') + "/plan/" + Uri.EscapeDataString(planId ?? "");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuthHeader(request);

                using var resp = await client.SendAsync(request).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                return new PlanStatusSnapshot
                {
                    PlanId = root.TryGetProperty("plan_id", out var p) ? p.GetString() : planId,
                    Status = root.TryGetProperty("status", out var s) ? s.GetString() : "",
                    Summary = root.TryGetProperty("summary", out var su) ? su.GetString() : "",
                    Total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0,
                    Done = root.TryGetProperty("done", out var d) && d.TryGetInt32(out var dv) ? dv : 0,
                    Failed = root.TryGetProperty("failed", out var f) && f.TryGetInt32(out var fv) ? fv : 0,
                    Current = root.TryGetProperty("current", out var c) ? c.GetString() : "",
                };
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PLAN] /plan/{planId} error: {ex.Message}");
                return null;
            }
        }

        /// <inheritdoc />
        public async Task StreamPlanAsync(string planId, Action<SseEvent> onEventReceived, System.Threading.CancellationToken ct = default)
        {
            try
            {
                string baseUrl = BuildBackendBaseUrl(_endpoint);
                string url = baseUrl.TrimEnd('/') + "/plan/" + Uri.EscapeDataString(planId ?? "") + "/stream";
                using var http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                ApplyAuthHeader(request);

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    onEventReceived?.Invoke(new SseEvent { EventType = "error", ErrorMessage = $"API Error: {response.StatusCode}\n{err}" });
                    return;
                }

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                string currentEvent = "content";
                string currentData = "";
                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    string line;
                    try { line = await reader.ReadLineAsync().ConfigureAwait(false); }
                    catch (IOException) { break; }
                    if (line == null) line = string.Empty;
                    if (line.StartsWith(":")) continue; // SSE heartbeat
                    if (line.StartsWith("event: ")) { currentEvent = line.Substring("event: ".Length).Trim(); continue; }
                    if (line.StartsWith("data: "))
                    {
                        var piece = line.Substring("data: ".Length);
                        currentData = string.IsNullOrEmpty(currentData) ? piece : currentData + "\n" + piece;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        var type = string.IsNullOrEmpty(currentEvent) ? "content" : currentEvent;
                        var rawData = currentData ?? "";
                        if (type == "done")
                        {
                            onEventReceived?.Invoke(new SseEvent { EventType = "done" });
                            break;
                        }
                        onEventReceived?.Invoke(new SseEvent { EventType = type, TextContent = rawData });
                        currentData = "";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client cancelled / disconnected: the background job keeps running; this follow stream ends.
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[PLAN] /plan/{planId}/stream error: {ex.Message}");
                onEventReceived?.Invoke(new SseEvent { EventType = "error", ErrorMessage = ex.Message });
            }
        }

        private async Task StreamFromEndpointAsync(string endpointPath, string prompt, Action<SseEvent> onEventReceived, string context, List<Dictionary<string, string>> history, System.Threading.CancellationToken ct, string rid, string intent, string planningKnowledge = "", List<string> images = null, string projectKey = "", bool evolutionEnabled = false)
        {
            if (!_useLocalBackend)
            {
                // Direct API mode — no streaming, fall back to non-streaming call
                onEventReceived?.Invoke(new SseEvent { EventType = "status", Status = new StatusData { Status = "analyzing", Message = "Using direct API (streaming not supported in direct mode)" } });
                try
                {
                    var result = await GenerateSclDirectAsync(prompt, context, history);
                    onEventReceived?.Invoke(new SseEvent { EventType = "content", TextContent = result });
                    onEventReceived?.Invoke(new SseEvent { EventType = "done" });
                }
                catch (Exception ex)
                {
                    onEventReceived?.Invoke(new SseEvent { EventType = "error", ErrorMessage = $"Direct API Error: {ex.Message}" });
                }
                return;
            }

            // An explicit rid wins; otherwise use the id of the agent run this call belongs to, so the Python log
            // and the tool callbacks it makes carry the same id as the C# run and its approval records.
            var actualRid = !string.IsNullOrWhiteSpace(rid) ? rid
                          : !string.IsNullOrWhiteSpace(Services.RunContext.CurrentRid) ? Services.RunContext.CurrentRid
                          : Guid.NewGuid().ToString("N").Substring(0, 8);
            string baseUrl = BuildBackendBaseUrl(_endpoint);
            string streamUrl = baseUrl.TrimEnd('/') + endpointPath;

            var requestData = new
            {
                api_key = _apiKey,
                model = _model,
                provider = BuildBackendProvider(),
                base_url = BuildProviderBaseUrl(),
                rid = actualRid,
                prompt = prompt,
                context = context,
                history = history ?? new List<Dictionary<string, string>>(),
                stream = true,
                intent = intent ?? "",
                planning_knowledge = planningKnowledge ?? "",
                images = images,
                project_key = projectKey ?? "",
                evolution_enabled = evolutionEnabled,
                reasoning_effort = _reasoningEffort,
                // Real model window (cloud → 1M, local → its own smaller window).
                // Python derives its token budget + compact/clear thresholds from it.
                context_window = TokenBudget.ResolveModelWindow(_model),
            };
            var jsonContent = JsonSerializer.Serialize(requestData, _jsonOptions);

            try
            {
                // Health check before streaming
                AppLogger.Log($"[DEBUG] Stream baseUrl={baseUrl} streamUrl={streamUrl} model={_model}");
                bool ok = await CheckBackendHealthAsync(baseUrl, 3000);
                if (!ok)
                {
                    AppLogger.Log($"[ERROR] Stream health check failed baseUrl={baseUrl}");
                    onEventReceived?.Invoke(new SseEvent { EventType = "error", ErrorMessage = "Backend not available" });
                    return;
                }

                AppLogger.Log($"[POST-STREAM] Starting streaming request to AI Backend...");
                var http = new HttpClient();
                http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                var request = new HttpRequestMessage(HttpMethod.Post, streamUrl)
                {
                    Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
                };
                ApplyAuthHeader(request);

                bool receivedDone = false;
                bool receivedAny = false;
                string breakReason = "";
                var streamStart = DateTime.UtcNow;
                bool loggedFirstChunk = false;
                int receivedChunkCount = 0;

                try
                {
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        AppLogger.Log($"[DEBUG] Stream response status={response.StatusCode}");
                        if (!response.IsSuccessStatusCode)
                        {
                            var err = await response.Content.ReadAsStringAsync();
                            onEventReceived?.Invoke(new SseEvent { EventType = "error", ErrorMessage = $"API Error: {response.StatusCode}\n{err}" });
                            return;
                        }

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(stream))
                        {
                            var toolsAdapter = new BuiltInToolsAdapter();
                            string currentEvent = "content";
                            string currentData = "";

                            while (!reader.EndOfStream && !ct.IsCancellationRequested)
                            {
                                string line;
                                try
                                {
                                    var readTask = reader.ReadLineAsync();
                                    var completed = await Task.WhenAny(readTask, Task.Delay(System.Threading.Timeout.Infinite, ct));
                                    if (completed != readTask)
                                    {
                                        breakReason = "cancellation requested";
                                        break;
                                    }
                                    line = await readTask;
                                }
                                catch (IOException)
                                {
                                    breakReason = "IOException(ReadLineAsync)";
                                    break;
                                }
                                if (line == null)
                                {
                                    // Flush pending SSE frame when server closes connection without trailing blank line
                                    line = string.Empty;
                                }
                                if (line.StartsWith(":")) continue; // SSE comment (heartbeat)
                                if (line.StartsWith("event: "))
                                {
                                    currentEvent = line.Substring("event: ".Length).Trim();
                                    continue;
                                }
                                if (line.StartsWith("data: "))
                                {
                                    var piece = line.Substring("data: ".Length);
                                    currentData = string.IsNullOrEmpty(currentData) ? piece : currentData + "\n" + piece;
                                    continue;
                                }
                                if (string.IsNullOrWhiteSpace(line))
                                {
                                    var type = string.IsNullOrEmpty(currentEvent) ? "content" : currentEvent;
                                    var rawData = currentData ?? "";
                                    receivedAny = true;

                                    if (type == "done")
                                    {
                                        receivedDone = true;
                                        breakReason = "received done event";
                                        onEventReceived?.Invoke(new SseEvent { EventType = "done" });
                                        break;
                                    }

                                    // Decode JSON-encoded data strings
                                    string decodedData = rawData;
                                    try
                                    {
                                        if (!string.IsNullOrEmpty(rawData) && rawData.StartsWith("\""))
                                            decodedData = JsonSerializer.Deserialize<string>(rawData);
                                    }
                                    catch { decodedData = rawData; }

                                    // Handle tool_request from backend (ReAct agent tool calls)
                                    if (type == "tool_request")
                                    {
                                        await HandleToolRequestAsync(rawData, toolsAdapter, onEventReceived);
                                    }
                                    // [NEW] Handle tool_call from backend (Python LangGraph Agent structured events)
                                    else if (type == SseEventTypes.ToolCall)
                                    {
                                        onEventReceived?.Invoke(new SseEvent { EventType = SseEventTypes.ToolCall, TextContent = decodedData });
                                    }
                                    else
                                    {
                                        var sseEvent = ParseSseEvent(type, decodedData);
                                        if (sseEvent != null)
                                        {
                                            onEventReceived?.Invoke(sseEvent);
                                        }
                                        else
                                        {
                                            // Fallback: emit as typed SseEvent
                                            onEventReceived?.Invoke(new SseEvent { EventType = type, TextContent = decodedData });
                                        }

                                        receivedChunkCount++;
                                        if (!loggedFirstChunk)
                                        {
                                            loggedFirstChunk = true;
                                            var firstMs = (int)(DateTime.UtcNow - streamStart).TotalMilliseconds;
                                            AppLogger.Log($"[STREAM] first_chunk_ms={firstMs} type={type} len={decodedData?.Length ?? 0}");
                                        }
                                        else if (receivedChunkCount % 100 == 0)
                                        {
                                            AppLogger.Log($"[STREAM] chunks={receivedChunkCount} elapsed_ms={(int)(DateTime.UtcNow - streamStart).TotalMilliseconds}");
                                        }
                                    }

                                    currentEvent = "content";
                                    currentData = "";
                                }
                            }
                            if (ct.IsCancellationRequested) breakReason = "cancellation requested";
                            if (reader.EndOfStream && string.IsNullOrEmpty(breakReason)) breakReason = "end of stream";
                        }
                    }
                }
                catch (IOException)
                {
                    if (receivedAny || ct.IsCancellationRequested)
                    {
                        if (string.IsNullOrEmpty(breakReason)) breakReason = "IOException(StreamClosed)";
                    }
                    else
                    {
                        throw;
                    }
                }
                finally
                {
                    http.Dispose();
                    AppLogger.Log($"[STREAM] finished receivedAny={receivedAny} receivedDone={receivedDone} breakReason={breakReason}");
                    if (!receivedDone)
                    {
                        onEventReceived?.Invoke(new SseEvent { EventType = "done" });
                        if (!receivedAny)
                            onEventReceived?.Invoke(new SseEvent { EventType = "error", ErrorMessage = "Streaming Error: connection closed with no data" });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AppLogger.Log($"[STREAM] cancelled by user");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ERROR] Stream outer exception: {ex.GetType().Name}: {ex.Message}");
                onEventReceived?.Invoke(new SseEvent { EventType = "error", ErrorMessage = $"Streaming Error: {ex.Message}" });
            }
        }

        /// <summary>Handle a tool_request event from the backend ReAct agent.</summary>
        private async Task HandleToolRequestAsync(string rawData, BuiltInToolsAdapter toolsAdapter, Action<SseEvent> onEventReceived)
        {
            try
            {
                var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rawData);
                string serverId = req != null && req.TryGetValue("serverId", out var sid) ? sid.GetString() ?? "builtin" : "builtin";
                string toolName = req != null && req.TryGetValue("name", out var tn) ? tn.GetString() ?? "" : "";
                var args = req != null && req.TryGetValue("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<Dictionary<string, object>>(argsEl.GetRawText())
                    : new Dictionary<string, object>();

                onEventReceived?.Invoke(new SseEvent { EventType = "thinking", TextContent = "Executing tool: " + toolName });

                ToolResult result;
                if (serverId == "builtin")
                {
                    result = await toolsAdapter.ExecuteToolAsync(toolName, args ?? new Dictionary<string, object>());
                }
                else
                {
                    result = new ToolResult { Success = false, Error = "Unknown serverId: " + serverId };
                }

                if (result.Success)
                {
                    onEventReceived?.Invoke(new SseEvent { EventType = "content", TextContent = result.Output ?? JsonSerializer.Serialize(result.Result) });
                }
                else
                {
                    onEventReceived?.Invoke(new SseEvent { EventType = "error", ErrorMessage = "Tool Error: " + result.Error });
                }
            }
            catch (Exception exTool)
            {
                onEventReceived?.Invoke(new SseEvent { EventType = "error", ErrorMessage = "Tool Handling Error: " + exTool.Message });
            }
        }

        private SseEvent ParseSseEvent(string eventType, string data)
        {
            if (string.IsNullOrEmpty(eventType) && string.IsNullOrEmpty(data)) return null;

            var sseEvent = new SseEvent { EventType = eventType ?? "content" };

            switch (eventType)
            {
                case "thought":
                    sseEvent.Thought = new ThoughtData();
                    try
                    {
                        var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(data);
                        sseEvent.Thought.Text = (obj != null && obj.TryGetValue("text", out var t)) ? t.GetString() : data;
                        sseEvent.Thought.Details = (obj != null && obj.TryGetValue("details", out var d)) ? d.GetString() : null;
                    }
                    catch { sseEvent.Thought.Text = data; }
                    break;

                case "notice":
                    sseEvent.Notice = new NoticeData();
                    try
                    {
                        using var noticeDoc = JsonDocument.Parse(data);
                        var noticeRoot = noticeDoc.RootElement;
                        sseEvent.Notice.Code = noticeRoot.TryGetProperty("code", out var nc) ? nc.GetString() : "";
                        sseEvent.Notice.Text = noticeRoot.TryGetProperty("text", out var nt) ? nt.GetString() : "";
                        if (noticeRoot.TryGetProperty("params", out var np) && np.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in np.EnumerateArray())
                                sseEvent.Notice.Params.Add(item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString());
                        }
                    }
                    catch { sseEvent.Notice.Text = data; }
                    break;

                case "file_change":
                    sseEvent.FileChange = new FileChangeData();
                    try
                    {
                        var obj = JsonSerializer.Deserialize<FileChangeData>(data, _jsonOptions);
                        sseEvent.FileChange = obj ?? new FileChangeData { File = data };
                    }
                    catch { sseEvent.FileChange.File = data; }
                    break;

                case "command":
                    sseEvent.Command = new CommandData();
                    try
                    {
                        var obj = JsonSerializer.Deserialize<CommandData>(data, _jsonOptions);
                        sseEvent.Command = obj ?? new CommandData { Command = data };
                    }
                    catch { sseEvent.Command.Command = data; }
                    break;

                case "status":
                    sseEvent.Status = new StatusData();
                    try
                    {
                        var obj = JsonSerializer.Deserialize<StatusData>(data, _jsonOptions);
                        sseEvent.Status = obj ?? new StatusData { Status = "unknown", Message = data };
                    }
                    catch { sseEvent.Status.Message = data; }
                    break;

                case "context_ref":
                    sseEvent.ContextRef = new ContextRefData();
                    try
                    {
                        var obj = JsonSerializer.Deserialize<ContextRefData>(data, _jsonOptions);
                        sseEvent.ContextRef = obj ?? new ContextRefData { Name = data };
                    }
                    catch { sseEvent.ContextRef.Name = data; }
                    break;

                case "done":
                    break;

                case "error":
                    sseEvent.ErrorMessage = data;
                    break;

                default:
                    sseEvent.TextContent = data;
                    break;
            }

            return sseEvent;
        }
    }

    public class MockAICodeGenerator : IAICodeGenerator
    {
        public string ModelName => "mock-model";
        public string ModelProvider => "openai";

        public async Task<string> GenerateSclAsync(string prompt, string context = "", List<Dictionary<string, string>> history = null, string rid = null, string intent = "")
        {
            // Simulate AI processing delay
            await Task.Delay(1000);

            // Return a simple SCL example based on prompt (mocked)
            return @"FUNCTION_BLOCK ""MotorControl""
                      VAR_INPUT
                          Start : Bool;
                          Stop : Bool;
                      END_VAR
                      VAR_OUTPUT
                          Motor : Bool;
                      END_VAR
                      BEGIN
                          IF #Start THEN
                              #Motor := TRUE;
                          ELSIF #Stop THEN
                              #Motor := FALSE;
                          END_IF;
                      END_FUNCTION_BLOCK";
        }

        [Obsolete("Use ImportBlockCodeAsync with .scl path directly", false)]
        public async Task<string> GenerateXmlAsync(string sclCode, string blockName = "", string blockType = "FB", string rid = null)
        {
            return await new RestAICodeGenerator("", "", "", "auto").GenerateXmlAsync(sclCode, blockName, blockType, rid);
        }

        public async Task GenerateSclStreamingAsync(string prompt, Action<string, string> onChunkReceived, string context = "", List<Dictionary<string, string>> history = null, System.Threading.CancellationToken ct = default, string rid = null)
        {
            onChunkReceived("thinking", "System is thinking...");
            await Task.Delay(500);
            onChunkReceived("content", "FUNCTION_BLOCK MockStream\nBEGIN\n");
            await Task.Delay(300);
            onChunkReceived("content", "  // Incremental output simulation\n");
            await Task.Delay(300);
            onChunkReceived("content", "  #Var := 100;\n");
            await Task.Delay(300);
            onChunkReceived("content", "END_FUNCTION_BLOCK");
        }

        public async Task<string> ConvertXmlAsync(string xmlContent, string instruction = "", string mode = "scl", string rid = null)
        {
            await Task.Delay(1);
            return "";
        }

        public async Task<string> ProbeVisionAsync(string rid = null)
        {
            await Task.Delay(1);
            return "unknown";
        }

        public async Task<ProviderTestResult> TestProviderAsync(string rid = null)
        {
            await Task.Delay(1);
            return new ProviderTestResult
            {
                Ok = true,
                Category = "ok",
                LatencyMs = 1,
                Model = ModelName,
                Reply = "OK",
                Detail = null,
            };
        }

        public async Task GenerateSclStreamingExAsync(string prompt, Action<SseEvent> onEventReceived, string context = "", List<Dictionary<string, string>> history = null, System.Threading.CancellationToken ct = default, string rid = null, string intent = "", string planningKnowledge = "", List<string> images = null, string projectKey = "", bool evolutionEnabled = false)
        {
            onEventReceived(new SseEvent { EventType = "status", Status = new StatusData { Status = "analyzing", Message = "Mock mode: Generating..." } });
            await Task.Delay(500);
            onEventReceived(new SseEvent { EventType = "thought", Thought = new ThoughtData { Text = "Mock: Processing request..." } });
            await Task.Delay(300);
            onEventReceived(new SseEvent { EventType = "content", TextContent = "FUNCTION_BLOCK MockStream\nBEGIN\n" });
            await Task.Delay(200);
            onEventReceived(new SseEvent { EventType = "content", TextContent = "  // Mock SCL output\n" });
            await Task.Delay(200);
            onEventReceived(new SseEvent { EventType = "content", TextContent = "  #Var := 100;\n" });
            await Task.Delay(200);
            onEventReceived(new SseEvent { EventType = "content", TextContent = "END_FUNCTION_BLOCK" });
            await Task.Delay(200);
            onEventReceived(new SseEvent { EventType = "done" });
        }

        public async Task GenerateChatPlanStreamAsync(string prompt, Action<SseEvent> onEventReceived, string context = "", List<Dictionary<string, string>> history = null, System.Threading.CancellationToken ct = default, string rid = null, string intent = "", string planningKnowledge = "", string projectKey = "")
        {
            onEventReceived(new SseEvent { EventType = "thinking", TextContent = "Mock ChatPlan mode: Analyzing..." });
            await Task.Delay(300);
            onEventReceived(new SseEvent { EventType = "content", TextContent = "FUNCTION_BLOCK MockPlan\nBEGIN\n  #Val := 1;\nEND_FUNCTION_BLOCK" });
            await Task.Delay(200);
            onEventReceived(new SseEvent { EventType = "done" });
        }

        public Task<string> PostPlanDecisionAsync(string planId, string phaseId, string decision, string note = null)
        {
            return Task.FromResult<string>(null);
        }

        public Task<string> PostTaskDecisionAsync(string planId, string taskId, string decision)
        {
            return Task.FromResult<string>(null);
        }

        public Task<PlanStartResult> StartPlanAsync(string prompt, string context = "", List<Dictionary<string, string>> history = null, string intent = "", string planningKnowledge = "", string projectKey = "", bool verifyCompileAtPlanEnd = false)
        {
            return Task.FromResult(new PlanStartResult { Started = false });
        }

        public Task<PlanStatusSnapshot> GetPlanStatusAsync(string planId)
        {
            return Task.FromResult<PlanStatusSnapshot>(null);
        }

        public Task StreamPlanAsync(string planId, Action<SseEvent> onEventReceived, System.Threading.CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public void UpdateRuntimeConfig(string apiKey = null, string endpoint = null, string model = null, string provider = null, string reasoningEffort = null)
        {
            // Mock: no-op
        }
    }
}
