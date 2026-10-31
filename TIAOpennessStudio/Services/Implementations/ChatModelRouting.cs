using System;
using TIAOpennessStudio.AICore;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Determines whether the currently selected chat model is a local Ollama
    /// model or a cloud one. Shared by LocalChatService / SimpleAnalyzeService
    /// so the decision stays consistent across both call sites.
    ///
    /// Decision rules (by reliability):
    ///   1. Provider is explicitly "Ollama" -> local.
    ///   2. The selected model name (Model / SelectedModel, first non-empty value)
    ///      contains a tag (":") -> local. Ollama model names are always shaped
    ///      like "qwen3.5:9b", whereas cloud model names usually have no colon.
    ///   3. When neither of the above is decisive, fall back to the local
    ///      service's CurrentModel, but only if Provider is not an explicit
    ///      cloud provider ("OpenAI"/"Custom"/...) -- otherwise a user who
    ///      selected a cloud model could be misclassified as local just because
    ///      the local service still holds a stale Ollama model.
    /// </summary>
    public static class ChatModelRouting
    {
        public static bool IsLocalModelSelected(AIConfig cfg, string localCurrentModel)
        {
            if (cfg == null)
                return false;

            // 1) Provider is explicitly Ollama
            var provider = cfg.ModelProvider ?? cfg.Provider;
            if (!string.IsNullOrWhiteSpace(provider) &&
                provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 2) Selected model name (first non-empty value, so an empty string does not defeat the ?? fallback)
            var selected = FirstNonEmpty(cfg.Model, cfg.SelectedModel);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                // A decisive selected name wins: colon -> local, no colon -> cloud (an explicit cloud model is trusted as-is)
                return AIConfig.IsLocalLlmModel(selected);
            }

            // 3) Selected name is empty and Provider is not an explicit cloud: fall back to the local service's current model
            bool providerIsCloud = !string.IsNullOrWhiteSpace(provider) &&
                (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
                 provider.Equals("Custom", StringComparison.OrdinalIgnoreCase) ||
                 provider.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase));
            if (!providerIsCloud && AIConfig.IsLocalLlmModel(localCurrentModel))
                return true;

            return false;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return null;
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            }
            return null;
        }
    }
}
