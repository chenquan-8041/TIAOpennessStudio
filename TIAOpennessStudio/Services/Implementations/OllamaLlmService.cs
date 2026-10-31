using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio;
using TIAOpennessStudio.AICore;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Ollama local LLM service implementation.
    /// </summary>
    public class OllamaLlmService : ILocalLlmService
    {
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private string _currentModel;

        public string CurrentModel => _currentModel;

        public OllamaLlmService(string baseUrl = "http://localhost:11434", string defaultModel = "qwen2.5:0.5b")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _currentModel = defaultModel;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)  // Lowered to 5 seconds to avoid long waits
            };
        }

        // ── Unified invocation entry ────────────────────────────────────────────

        /// <inheritdoc/>
        public async Task<string> InvokeAsync(
            string prompt,
            OllamaTaskKind kind = OllamaTaskKind.Chat,
            Action<string> onChunk = null,
            Action<string> onThinking = null,
            Action<OllamaTokenStats> onUsage = null,
            int maxTokens = 4096,
            CancellationToken ct = default)
        {
            var model = string.IsNullOrWhiteSpace(_currentModel) ? "qwen2.5:0.5b" : _currentModel;

            switch (kind)
            {
                case OllamaTaskKind.Chat:
                    // /api/chat + think=true: full reasoning, separating thinking/content.
                    // Shares the same underlying logic as GenerateStreamWithThinkingAsync.
                    return await GenerateStreamWithThinkingAsync(
                        prompt,
                        onThinking: onThinking,
                        onContent: onChunk,
                        model: model,
                        maxTokens: maxTokens,
                        ct: ct,
                        onUsage: onUsage);

                case OllamaTaskKind.Completion:
                default:
                    // /api/generate + think=false: fast completion, no reasoning.
                    // Degrades to non-streaming (waits for the full result) when onChunk == null.
                    if (onChunk != null)
                    {
                        return await GenerateStreamAsync(
                            prompt,
                            onChunk,
                            model: model,
                            maxTokens: maxTokens,
                            ct: ct);
                    }
                    else
                    {
                        return await GenerateAsync(prompt, model, maxTokens);
                    }
            }
        }

        // ── Legacy interface implementation (backward compatible) ────────────────
        public async Task<string> GenerateAsync(string prompt, string model = "qwen2.5:0.5b", int maxTokens = 512)
        {
            try
            {
                model = string.IsNullOrWhiteSpace(model) ? _currentModel : model;
                AppLogger.Log($"[Ollama] GenerateAsync called, model={model}, prompt_len={prompt.Length}, maxTokens={maxTokens}");
                var startTime = DateTime.Now;
                
                var requestBody = new
                {
                    model = model,
                    prompt = prompt,
                    stream = false,
                    // Disable thinking so reasoning models don't return only thinking and leave response empty.
                    think = false,
                    options = new
                    {
                        num_predict = maxTokens,
                        temperature = 0.3f, // Lower the temperature for more deterministic output
                        top_p = 0.9f
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                AppLogger.Log($"[Ollama] Sending request to {_baseUrl}/api/generate");
                var response = await _httpClient.PostAsync($"{_baseUrl}/api/generate", content);
                
                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                AppLogger.Log($"[Ollama] Response received in {elapsed}ms, status={response.StatusCode}");
                
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<OllamaResponse>(responseJson, _jsonOptions);

                AppLogger.Log($"[Ollama] Generated response length={result?.Response?.Length ?? 0}");
                return result?.Response ?? string.Empty;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Ollama] GenerateAsync error: {ex.GetType().Name}: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Streams text generation, invoking onChunk for each received token.
        /// </summary>
        public async Task<string> GenerateStreamAsync(
            string prompt,
            Action<string> onChunk,
            string model = "qwen2.5:0.5b",
            int maxTokens = 512,
            CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            model = string.IsNullOrWhiteSpace(model) ? _currentModel : model;

            try
            {
                AppLogger.Log($"[Ollama] GenerateStreamAsync called, model={model}, prompt_len={prompt.Length}");

                var requestBody = new
                {
                    model = model,
                    prompt = prompt,
                    stream = true,
                    // Disable thinking: reasoning models such as qwen3.5 put content into the
                    // thinking field by default, leaving response empty. Explicit think=false
                    // makes the model emit the body directly.
                    think = false,
                    options = new
                    {
                        num_predict = maxTokens,
                        temperature = 0.3f,
                        top_p = 0.9f
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var streamClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/generate")
                {
                    Content = content
                };

                using var response = await streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var chunk = JsonSerializer.Deserialize<OllamaStreamChunk>(line, _jsonOptions);
                        if (chunk != null && !string.IsNullOrEmpty(chunk.Response))
                        {
                            sb.Append(chunk.Response);
                            onChunk?.Invoke(chunk.Response);
                        }
                        if (chunk != null && chunk.Done)
                            break;
                    }
                    catch (JsonException)
                    {
                        // Skip lines that fail to parse
                    }
                }

                AppLogger.Log($"[Ollama] GenerateStreamAsync done, total_len={sb.Length}");
                return sb.ToString();
            }
            catch (OperationCanceledException)
            {
                AppLogger.Log($"[Ollama] GenerateStreamAsync cancelled, partial_len={sb.Length}");
                return sb.Length > 0 ? sb.ToString() : string.Empty;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Ollama] GenerateStreamAsync error: {ex.GetType().Name}: {ex.Message}");
                // If streaming fails, fall back to non-streaming
                if (sb.Length == 0)
                {
                    AppLogger.Log("[Ollama] Stream failed, falling back to non-streaming");
                    return await GenerateAsync(prompt, model, maxTokens);
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Streams text generation with image input (base64 encoded).
        /// The Ollama /api/generate endpoint supports the images field.
        /// </summary>
        public async Task<string> GenerateStreamWithImagesAsync(
            string prompt,
            List<string> images,
            Action<string> onChunk,
            string model = "qwen2.5:0.5b",
            int maxTokens = 512,
            CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            model = string.IsNullOrWhiteSpace(model) ? _currentModel : model;

            try
            {
                AppLogger.Log($"[Ollama] GenerateStreamWithImagesAsync called, model={model}, prompt_len={prompt.Length}, images={images?.Count ?? 0}");

                var requestBody = new
                {
                    model = model,
                    prompt = prompt,
                    images = images ?? new List<string>(),
                    stream = true,
                    // Disable thinking so reasoning models don't return only thinking and leave response empty.
                    think = false,
                    options = new
                    {
                        num_predict = maxTokens,
                        temperature = 0.3f,
                        top_p = 0.9f
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var streamClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/generate")
                {
                    Content = content
                };

                using var response = await streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var chunk = JsonSerializer.Deserialize<OllamaStreamChunk>(line, _jsonOptions);
                        if (chunk != null && !string.IsNullOrEmpty(chunk.Response))
                        {
                            sb.Append(chunk.Response);
                            onChunk?.Invoke(chunk.Response);
                        }
                        if (chunk != null && chunk.Done)
                            break;
                    }
                    catch (JsonException)
                    {
                    }
                }

                AppLogger.Log($"[Ollama] GenerateStreamWithImagesAsync done, total_len={sb.Length}");
                return sb.ToString();
            }
            catch (OperationCanceledException)
            {
                AppLogger.Log($"[Ollama] GenerateStreamWithImagesAsync cancelled, partial_len={sb.Length}");
                return sb.Length > 0 ? sb.ToString() : string.Empty;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Ollama] GenerateStreamWithImagesAsync error: {ex.GetType().Name}: {ex.Message}");
                if (sb.Length == 0)
                {
                    AppLogger.Log("[Ollama] Image stream failed, falling back to non-streaming without images");
                    return await GenerateAsync(prompt, model, maxTokens);
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Streams text generation, distinguishing the thought process from the
        /// final reply. Uses /api/chat + think=true to separate message.thinking
        /// from message.content, with fallback parsing of &lt;think&gt; tags.
        /// </summary>
        public async Task<string> GenerateStreamWithThinkingAsync(
            string prompt,
            Action<string> onThinking,
            Action<string> onContent,
            string model = "qwen2.5:0.5b",
            int maxTokens = 512,
            CancellationToken ct = default,
            Action<OllamaTokenStats> onUsage = null)
        {
            var contentSb = new StringBuilder();
            model = string.IsNullOrWhiteSpace(model) ? _currentModel : model;

            // <thinking> tag parsing state (for models that write reasoning into content).
            // Design notes (so the body
            // is never swallowed into the thinking):
            //   - Text inside <thinking> accumulates into pendingThink and becomes thinking
            //     output only when a real </thinking> is encountered.
            //   - If the stream ends before closing, pendingThink is flushed as [body].
            bool inThinkTag = false;
            string carry = string.Empty;        // Buffer for a possibly truncated tag prefix
            string pendingThink = string.Empty; // Pending thinking that has entered <thinking> but not yet seen </thinking>

            // Process a content delta: separate text inside / outside the <thinking>...</thinking> tags.
            void EmitContentDelta(string delta)
            {
                if (string.IsNullOrEmpty(delta)) return;
                carry += delta;
                while (carry.Length > 0)
                {
                    if (!inThinkTag)
                    {
                        int open = carry.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                        if (open < 0)
                        {
                            // Keep a trailing partial tag so we don't cut off "<thi"
                            int safe = SafeFlushLen(carry, "<think>");
                            if (safe > 0)
                            {
                                var text = carry.Substring(0, safe);
                                contentSb.Append(text);
                                onContent?.Invoke(text);
                                carry = carry.Substring(safe);
                            }
                            break;
                        }
                        if (open > 0)
                        {
                            var text = carry.Substring(0, open);
                            contentSb.Append(text);
                            onContent?.Invoke(text);
                        }
                        carry = carry.Substring(open + "<think>".Length);
                        inThinkTag = true;
                    }
                    else
                    {
                        int close = carry.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                        if (close < 0)
                        {
                            // No closing tag yet: accumulate the safely flushable part into pendingThink
                            // (don't emit yet), keeping a possible trailing partial </thinking> for the next chunk.
                            int safe = SafeFlushLen(carry, "</think>");
                            if (safe > 0)
                            {
                                pendingThink += carry.Substring(0, safe);
                                carry = carry.Substring(safe);
                            }
                            break;
                        }
                        // Closing tag found: emit pendingThink plus this segment as thinking.
                        pendingThink += carry.Substring(0, close);
                        if (pendingThink.Length > 0)
                        {
                            onThinking?.Invoke(pendingThink);
                            pendingThink = string.Empty;
                        }
                        carry = carry.Substring(close + "</think>".Length);
                        inThinkTag = false;
                    }
                }
            }

            try
            {
                AppLogger.Log($"[Ollama] GenerateStreamWithThinkingAsync called, model={model}, prompt_len={prompt.Length}");

                var requestBody = new
                {
                    model = model,
                    messages = new[] { new { role = "user", content = prompt } },
                    stream = true,
                    think = true,
                    options = new
                    {
                        num_predict = maxTokens,
                        temperature = 0.3f,
                        top_p = 0.9f
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var streamClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
                {
                    Content = content
                };

                using var response = await streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var chunk = JsonSerializer.Deserialize<OllamaChatStreamChunk>(line, _jsonOptions);
                        var msg = chunk?.Message;
                        if (msg != null)
                        {
                            // 1) Native thinking field (qwen3 / deepseek-r1, with think=true)
                            if (!string.IsNullOrEmpty(msg.Thinking))
                                onThinking?.Invoke(msg.Thinking);
                            // 2) content field (may embed <thinking> tags)
                            if (!string.IsNullOrEmpty(msg.Content))
                                EmitContentDelta(msg.Content);
                        }
                        if (chunk != null && chunk.Done)
                        {
                            // The done chunk carries the engine's real token counts, so report usage.
                            if (onUsage != null && (chunk.PromptEvalCount.HasValue || chunk.EvalCount.HasValue))
                            {
                                onUsage(new OllamaTokenStats
                                {
                                    PromptTokens = chunk.PromptEvalCount ?? 0,
                                    CompletionTokens = chunk.EvalCount ?? 0,
                                });
                            }
                            break;
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip lines that fail to parse
                    }
                }

                // Flush leftovers:
                // When <thinking> is unclosed (inThinkTag still true, e.g. the stream was
                // cut off), what accumulated in pendingThink is unconfirmed content — emit
                // it as [body], never swallow it into thinking.
                // This runs only in the C# local loop; the Python backend never serves Ollama.
                var leftover = pendingThink + carry;
                if (leftover.Length > 0)
                {
                    contentSb.Append(leftover);
                    onContent?.Invoke(leftover);
                }

                AppLogger.Log($"[Ollama] GenerateStreamWithThinkingAsync done, content_len={contentSb.Length}");
                return contentSb.ToString();
            }
            catch (OperationCanceledException)
            {
                AppLogger.Log($"[Ollama] GenerateStreamWithThinkingAsync cancelled, partial_len={contentSb.Length}");
                return contentSb.Length > 0 ? contentSb.ToString() : string.Empty;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Ollama] GenerateStreamWithThinkingAsync error: {ex.GetType().Name}: {ex.Message}" +
                              (ex.InnerException != null ? $" | inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : ""));
                // Fall back to plain streaming (/api/generate) when /api/chat or think is
                // unsupported. Only fall back if nothing has been emitted yet, to avoid duplicates.
                if (contentSb.Length == 0)
                {
                    AppLogger.Log("[Ollama] chat/think stream failed, falling back to GenerateStreamAsync");
                    // Reset <thinking> parsing state so the fallback stream starts clean,
                    // unaffected by leftover inThinkTag/carry/pendingThink from the interrupted run.
                    inThinkTag = false;
                    carry = string.Empty;
                    pendingThink = string.Empty;
                    await GenerateStreamAsync(prompt, d => EmitContentDelta(d), model, maxTokens, ct);
                    // After the fallback stream, flush leftovers (any unclosed <thinking> is treated as body).
                    var fbLeftover = pendingThink + carry;
                    if (fbLeftover.Length > 0)
                    {
                        contentSb.Append(fbLeftover);
                        onContent?.Invoke(fbLeftover);
                    }
                }
                return contentSb.ToString();
            }
        }

        /// <summary>
        /// Computes how many characters can be safely flushed, avoiding cutting
        /// off a possible tag prefix. E.g. if the buffer ends with "&lt;thi",
        /// keep it to wait for the next chunk to complete the tag.
        /// </summary>
        private static int SafeFlushLen(string buffer, string tag)
        {
            int maxKeep = Math.Min(buffer.Length, tag.Length - 1);
            for (int keep = maxKeep; keep > 0; keep--)
            {
                if (tag.StartsWith(buffer.Substring(buffer.Length - keep), StringComparison.OrdinalIgnoreCase))
                    return buffer.Length - keep;
            }
            return buffer.Length;
        }

        /// <summary>
        /// Classifies intent (used for agent intent recognition).
        /// </summary>
        public async Task<string> ClassifyIntentAsync(string userPrompt)
        {
            // The category dictionary must match the AgentIntent enum (otherwise the
            // caller's Enum.TryParse always fails).
            // Reference: TIAOpennessStudio.Models.AgentIntent
            var systemPrompt = @"你是一个 PLC 工程师助手的意图分类器。请将用户输入分类为以下类别之一,只返回类别名称(不要任何其他字、标点、引号):

                - ExplainCode: 用户想理解某段代码或逻辑(""解释"" / ""分析"" / ""是什么意思"")
                - ModifyCode: 用户想修改、修复、优化已有代码(""改"" / ""修改"" / ""优化"" / ""修复"")
                - Compile: 用户想编译或验证代码(""编译"" / ""验证"" / ""检查错误"")
                - TraceVariable: 用户想找变量在哪里使用(""哪里用到"" / ""引用"" / ""追踪"")
                - GenerateCode: 用户想生成新的功能块/函数/数据块(""生成"" / ""创建"" / ""写一个"" / ""新建"")
                - Diagnose: 用户想做整体诊断或健康检查(""诊断"" / ""体检"" / ""检查项目"")
                - Chat: 闲聊、打招呼、不属于以上的开放问题(""你好"" / ""谢谢"" / ""你能做什么"")";

            var fullPrompt = $"{systemPrompt}\n\n用户输入: {userPrompt}\n\n类别:";

            var result = await GenerateAsync(fullPrompt, _currentModel, 20);

            if (string.IsNullOrWhiteSpace(result))
                return "Chat";

            // Take the first token of the first line, stripping common punctuation
            result = result.Trim()
                           .Split(new[] { '\n', '\r', ' ', '\t', '.', ',', '、', '。', '，', '`', '"', '\'' },
                                  StringSplitOptions.RemoveEmptyEntries)
                           .FirstOrDefault() ?? "Chat";

            // Validate that the returned category is valid (strictly aligned with the AgentIntent enum)
            var validIntents = new[]
            {
                "ExplainCode", "ModifyCode", "Compile",
                "TraceVariable", "GenerateCode", "Diagnose", "Chat"
            };
            foreach (var intent in validIntents)
            {
                if (result.Equals(intent, StringComparison.OrdinalIgnoreCase))
                    return intent;
            }

            // Model returned junk -> safely fall back to Chat
            AppLogger.Log($"[Ollama] ClassifyIntent unknown response: '{result}', defaulting to Chat");
            return "Chat";
        }

        /// <summary>
        /// Checks whether the service is available.
        /// </summary>
        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/tags");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Sets the current model.
        /// </summary>
        public void SetModel(string model)
        {
            _currentModel = model;
        }

        /// <summary>
        /// Checks whether the current model supports image/Vision input.
        /// </summary>
        public bool CurrentModelSupportsVision => AICore.AIConfig.IsVisionModel(_currentModel);

        private class OllamaResponse
        {
            [JsonPropertyName("response")]
            public string Response { get; set; }

            [JsonPropertyName("model")]
            public string Model { get; set; }

            [JsonPropertyName("done")]
            public bool Done { get; set; }
        }

        private class OllamaStreamChunk
        {
            [JsonPropertyName("response")]
            public string Response { get; set; }

            // Some models (e.g. the qwen3.5 family) put their reasoning into the thinking
            // field, leaving response empty. Keep the field for potential fallback use.
            [JsonPropertyName("thinking")]
            public string Thinking { get; set; }

            [JsonPropertyName("done")]
            public bool Done { get; set; }
        }

        private class OllamaChatStreamChunk
        {
            [JsonPropertyName("message")]
            public OllamaChatMessage Message { get; set; }

            [JsonPropertyName("done")]
            public bool Done { get; set; }

            // Real token usage: appears only in the final done chunk.
            [JsonPropertyName("prompt_eval_count")]
            public int? PromptEvalCount { get; set; }

            [JsonPropertyName("eval_count")]
            public int? EvalCount { get; set; }
        }

        private class OllamaChatMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; }

            [JsonPropertyName("content")]
            public string Content { get; set; }

            [JsonPropertyName("thinking")]
            public string Thinking { get; set; }
        }
    }
}
