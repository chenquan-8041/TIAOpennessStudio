using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Phase 6: Compresses long tool outputs to prevent token explosion.
    ///
    /// After 10+ ReAct iterations, raw tool outputs can consume thousands of tokens.
    /// The compressor reduces long outputs to structured summaries, keeping only
    /// the essential information the LLM needs for the next decision.
    ///
    /// Strategy:
    ///   1. Rule-based compression (fast, free): truncate + extract key info
    ///   2. LLM summarization (fallback): when rule-based isn't enough
    /// </summary>
    public interface IObservationCompressor
    {
        /// <summary>
        /// Compress a tool output if it exceeds the threshold.
        /// Returns the original output if under threshold or if compression isn't needed.
        /// </summary>
        /// <param name="output">Raw tool output.</param>
        /// <param name="toolName">The tool that produced the output.</param>
        /// <param name="threshold">Max characters before compression triggers (default 500).</param>
        /// <param name="ct">Cancellation token.</param>
        Task<string> CompressAsync(string output, string toolName, int threshold = 500, CancellationToken ct = default);

        /// <summary>
        /// Compress a batch of parallel tool results into a single merged summary.
        /// Each individual result is first compressed if over threshold, then all
        /// results are merged. If total exceeds 2000 chars, an LLM summary is generated.
        /// </summary>
        /// <param name="results">List of (toolName, rawOutput) tuples from parallel execution.</param>
        /// <param name="threshold">Per-result compression threshold (default 500).</param>
        /// <param name="ct">Cancellation token.</param>
        Task<string> CompressBatchAsync(List<(string toolName, string output)> results, int threshold = 500, CancellationToken ct = default);
    }
}
