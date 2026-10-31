using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Real token usage of a single Ollama generation, taken from the
    /// prompt_eval_count / eval_count fields of the stream's done chunk
    /// (engine-exact counts, not character estimates).
    /// </summary>
    public sealed class OllamaTokenStats
    {
        /// <summary>Input (prompt) token count from prompt_eval_count.</summary>
        public int PromptTokens { get; set; }

        /// <summary>Output (generation) token count from eval_count.</summary>
        public int CompletionTokens { get; set; }

        /// <summary>Total context tokens consumed by this call (prompt + completion).</summary>
        public int TotalTokens => PromptTokens + CompletionTokens;
    }

    /// <summary>
    /// Ollama task type — determines the endpoint choice and reasoning switch.
    ///
    /// <list type="bullet">
    ///   <item><term><see cref="Chat"/></term>
    ///     <description>
    ///       Conversational/reasoning tasks (user chat, SCL generation, code analysis,
    ///       ReAct reasoning). Routes to /api/chat with think=true: the model reasons
    ///       fully before producing the final answer, for best quality. Streaming can
    ///       receive the reasoning via the onThinking callback.
    ///     </description>
    ///   </item>
    ///   <item><term><see cref="Completion"/></term>
    ///     <description>
    ///       Single-shot structured completion (intent classification, tool-output
    ///       compression, reflection summaries, etc.). Routes to /api/generate with
    ///       think=false: no deep reasoning, faster and cheaper tokens, suited to
    ///       lightweight helper tasks with fixed I/O formats.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    public enum OllamaTaskKind
    {
        /// <summary>Conversation/reasoning → /api/chat with think=true.</summary>
        Chat,

        /// <summary>Single-shot structured completion → /api/generate with think=false.</summary>
        Completion,
    }

    /// <summary>
    /// Local LLM service interface (for Ollama and other local models).
    ///
    /// Prefer the unified <see cref="InvokeAsync"/> entry point, which routes to the
    /// correct endpoint/parameters via <see cref="OllamaTaskKind"/>. The legacy methods
    /// remain for backward compatibility and migrate to InvokeAsync over time.
    /// </summary>
    public interface ILocalLlmService
    {
        // ── Unified entry point (preferred) ─────────────────────────────

        /// <summary>
        /// Unified entry point. Routes automatically based on <paramref name="kind"/>:
        /// <list type="bullet">
        ///   <item><see cref="OllamaTaskKind.Chat"/> →
        ///     /api/chat with think=true (streaming, splits thinking/content)</item>
        ///   <item><see cref="OllamaTaskKind.Completion"/> →
        ///     /api/generate with think=false (streaming or not; only the response is used)</item>
        /// </list>
        /// Callers only declare intent; they don't need to know endpoint details.
        /// </summary>
        /// <param name="prompt">User/system prompt.</param>
        /// <param name="kind">Task kind, deciding the endpoint and reasoning switch.</param>
        /// <param name="onChunk">Incremental callback for the formal reply (content delta). Null = non-streaming (wait for full result).</param>
        /// <param name="onThinking">Incremental callback for the reasoning process. Only effective for <see cref="OllamaTaskKind.Chat"/>; null silently discards thinking content.</param>
        /// <param name="onUsage">Real token-usage callback fired when generation ends. May be null.</param>
        /// <param name="maxTokens">Maximum number of generated tokens.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The complete formal reply text (excluding thinking content).</returns>
        Task<string> InvokeAsync(
            string prompt,
            OllamaTaskKind kind = OllamaTaskKind.Chat,
            Action<string> onChunk = null,
            Action<string> onThinking = null,
            Action<OllamaTokenStats> onUsage = null,
            int maxTokens = 4096,
            CancellationToken ct = default);

        // ── Legacy interface (backward-compatible; migrates to InvokeAsync over time) ──

        /// <inheritdoc cref="InvokeAsync"/>
        [Obsolete("Use InvokeAsync(kind: OllamaTaskKind.Completion) instead.")]
        Task<string> GenerateAsync(string prompt, string model = "qwen2.5:0.5b", int maxTokens = 512);

        /// <inheritdoc cref="InvokeAsync"/>
        [Obsolete("Use InvokeAsync(kind: OllamaTaskKind.Chat, onChunk: ...) instead.")]
        Task<string> GenerateStreamAsync(
            string prompt,
            Action<string> onChunk,
            string model = "qwen2.5:0.5b",
            int maxTokens = 512,
            CancellationToken ct = default);

        /// <summary>
        /// Generates text (streaming) with image inputs (base64-encoded). Vision-only;
        /// no InvokeAsync equivalent yet.
        /// </summary>
        Task<string> GenerateStreamWithImagesAsync(
            string prompt,
            List<string> images,
            Action<string> onChunk,
            string model = "qwen2.5:0.5b",
            int maxTokens = 512,
            CancellationToken ct = default);

        /// <inheritdoc cref="InvokeAsync"/>
        [Obsolete("Use InvokeAsync(kind: OllamaTaskKind.Chat, onChunk: ..., onThinking: ...) instead.")]
        Task<string> GenerateStreamWithThinkingAsync(
            string prompt,
            Action<string> onThinking,
            Action<string> onContent,
            string model = "qwen2.5:0.5b",
            int maxTokens = 512,
            CancellationToken ct = default,
            Action<OllamaTokenStats> onUsage = null);

        /// <summary>Classifies intent (for agent intent recognition). Routes through the Completion path internally.</summary>
        Task<string> ClassifyIntentAsync(string userPrompt);

        /// <summary>Checks whether the service is available.</summary>
        Task<bool> IsAvailableAsync();

        /// <summary>Gets the current model name.</summary>
        string CurrentModel { get; }

        /// <summary>Sets the current model (runtime switch).</summary>
        void SetModel(string model);

        /// <summary>Checks whether the current model supports image/vision input.</summary>
        bool CurrentModelSupportsVision { get; }
    }
}
