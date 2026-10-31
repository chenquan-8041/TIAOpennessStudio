using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Intent classifier, ordered by precedence:
    /// 1. Fast keyword match
    /// 2. Local Ollama classification
    /// 3. Cloud LLM classification
    /// 4. Default to Chat
    /// </summary>
    public class IntentClassifier
    {
        private readonly ILocalLlmService _localLlmService;
        private readonly IAIEngineService _aiService;

        public IntentClassifier(ILocalLlmService localLlmService = null, IAIEngineService aiService = null)
        {
            _localLlmService = localLlmService;
            _aiService = aiService;
        }

        public async Task<AgentIntent> ClassifyAsync(string prompt, List<Dictionary<string, string>> history = null)
        {
            var keywordIntent = ClassifyByKeywords(prompt);
            if (keywordIntent.HasValue)
            {
                AppLogger.Log($"[Intent] keyword-classified -> {keywordIntent.Value}");
                return keywordIntent.Value;
            }

            if (_localLlmService != null && await IsLocalLlmAvailableQuick(800))
            {
                try
                {
                    AppLogger.Log("[Intent] Calling Ollama ClassifyIntentAsync...");
                    var classifyTask = _localLlmService.ClassifyIntentAsync(prompt);
                    var timeoutTask = Task.Delay(5000);
                    var completedTask = await Task.WhenAny(classifyTask, timeoutTask);

                    if (completedTask == classifyTask)
                    {
                        var intentStr = await classifyTask;
                        if (Enum.TryParse<AgentIntent>(intentStr, true, out var intent))
                        {
                            AppLogger.Log($"[Intent] local Ollama -> {intent}");
                            return intent;
                        }

                        AppLogger.Log($"[Intent] local Ollama returned unparseable: '{intentStr}', defaulting to Chat");
                    }
                    else
                    {
                        AppLogger.Log("[Intent] local Ollama timeout after 5s, defaulting to Chat");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[Intent] local Ollama failed: {ex.Message}, defaulting to Chat");
                }
            }
            else
            {
                AppLogger.Log("[Intent] Ollama not available or probe timeout, skipping");
            }

            if (_aiService != null)
            {
                try
                {
                    AppLogger.Log("[Intent] Trying cloud LLM classification...");
                    var cloudTask = ClassifyByCloudLlmAsync(prompt);
                    var timeoutTask = Task.Delay(3000);
                    var completedTask = await Task.WhenAny(cloudTask, timeoutTask);

                    if (completedTask == cloudTask)
                    {
                        var intent = await cloudTask;
                        if (intent.HasValue)
                        {
                            AppLogger.Log($"[Intent] cloud LLM -> {intent.Value}");
                            return intent.Value;
                        }
                    }
                    else
                    {
                        AppLogger.Log("[Intent] cloud LLM timeout after 3s");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[Intent] cloud LLM failed: {ex.Message}");
                }
            }

            AppLogger.Log("[Intent] unable to classify, returning Unknown (caller routes to full-capability, not read-only Chat)");
            return AgentIntent.Unknown;
        }

        private async Task<AgentIntent?> ClassifyByCloudLlmAsync(string prompt)
        {
            var classificationPrompt =
                "Classify the user request into exactly ONE category. Reply with ONLY a JSON object " +
                "in the form {\"intent\": \"<category>\", \"confidence\": <0..1>} and nothing else.\n\n" +
                "Categories:\n" +
                "- GenerateCode: create/generate new code, function block, or SCL (including write/import into the project)\n" +
                "- ModifyCode: modify, fix, optimize, or refactor existing code\n" +
                "- ExplainCode: understand, explain, or analyze code\n" +
                "- Compile: compile or verify code\n" +
                "- Diagnose: diagnose or health-check the project\n" +
                "- TraceVariable: trace, find, or locate a variable/block\n" +
                "- Chat: general conversation, questions, or anything else\n\n" +
                $"User request: \"{prompt}\"\n\nJSON:";

            try
            {
                var result = await _aiService.GenerateSclAsync(classificationPrompt, "", null, null, "Chat");
                if (string.IsNullOrWhiteSpace(result)) return null;

                // Preferred: structured JSON {intent, confidence}.
                var jsonObj = FirstJsonObject(result);
                if (jsonObj != null)
                {
                    using var doc = JsonDocument.Parse(jsonObj);
                    if (doc.RootElement.TryGetProperty("intent", out var intentEl))
                    {
                        var name = intentEl.GetString()?.Trim();
                        if (Enum.TryParse<AgentIntent>(name, true, out var intent) && intent != AgentIntent.Unknown)
                            return intent;
                    }
                }

                // Fallback: free-text containing a single category name.
                var cleaned = result.Trim()
                    .Replace("\"", "").Replace("'", "").Replace(".", "")
                    .Replace("\n", " ").Replace("\r", " ");
                foreach (AgentIntent val in Enum.GetValues(typeof(AgentIntent)))
                {
                    if (val == AgentIntent.Unknown) continue;
                    if (cleaned.IndexOf(val.ToString(), StringComparison.OrdinalIgnoreCase) >= 0)
                        return val;
                }

                AppLogger.Log($"[Intent] cloud LLM returned unparseable: '{result.Substring(0, Math.Min(120, result.Length))}'");
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string FirstJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int start = text.IndexOf('{');
            if (start < 0) return null;
            int depth = 0;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        private async Task<bool> IsLocalLlmAvailableQuick(int timeoutMs)
        {
            if (_localLlmService == null) return false;

            try
            {
                var probeTask = _localLlmService.IsAvailableAsync();
                var completed = await Task.WhenAny(probeTask, Task.Delay(timeoutMs));
                if (completed != probeTask) return false;
                return await probeTask;
            }
            catch
            {
                return false;
            }
        }

        private static AgentIntent? ClassifyByKeywords(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return AgentIntent.Chat;

            var lower = prompt.ToLowerInvariant();

            if (ContainsAny(lower, "编译", "验证", "compile", "verify", "build"))
                return AgentIntent.Compile;

            if (ContainsAny(lower, "诊断", "健康检查", "体检", "diagnose", "health check"))
                return AgentIntent.Diagnose;

            if (ContainsAny(lower, "引用", "哪里用到", "追踪", "查找变量", "trace", "usage of", "where is", "find variable"))
                return AgentIntent.TraceVariable;

            if (ContainsAny(
                lower,
                "生成", "创建", "新建", "写一个", "写个", "帮我写", "做一个", "实现一个",
                "写入", "写到", "写进", "导入到",
                "generate", "create", "write a", "write an", "implement", "make a", "build a",
                "write to", "import into",
                "new function", "new fb", "new fc", "new ob", "new block"))
            {
                return AgentIntent.GenerateCode;
            }

            if (ContainsAny(
                lower,
                "修改", "优化", "修复", "调整", "改一个", "改成", "替换", "重构",
                "modify", "fix", "optimize", "refactor", "change", "update", "rename", "replace"))
            {
                // Same as ChatRequestRouter: no analysis-cue guessing. Whether writes are allowed is
                // decided by the input-box access-mode switch, enforced at the write gate.
                return AgentIntent.ModifyCode;
            }

            if (ContainsAny(lower, "解释", "说明", "分析", "作用", "含义", "explain", "describe", "walk me through", "understand"))
                return AgentIntent.ExplainCode;

            if (ContainsAny(lower, "是什么", "做什么", "怎么工作", "如何工作", "what is", "what does", "how does") &&
                LooksLikeCodeQuestion(lower))
            {
                return AgentIntent.ExplainCode;
            }

            return null;
        }

        private static bool ContainsAny(string text, params string[] patterns)
        {
            foreach (var pattern in patterns)
            {
                if (!string.IsNullOrEmpty(pattern) && text.Contains(pattern))
                    return true;
            }

            return false;
        }

        private static bool LooksLikeCodeQuestion(string lower)
        {
            return ContainsAny(
                lower,
                "代码", "逻辑", "块", "变量",
                "scl", "lad", "fbd", "awl",
                "block", "function", "program",
                "fb", "fc", "db", "ob", "udt", "tag");
        }
    }
}
