using System.Threading;
using System.Threading.Tasks;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Content type enum — determines the compression strategy.
    /// </summary>
    public enum ContentType
    {
        /// <summary>SCL code block (export_block output).</summary>
        SclCode,
        /// <summary>TIA XML block structure (export_block output in XML format).</summary>
        XmlBlock,
        /// <summary>Compilation output (compile tool output).</summary>
        CompileOutput,
        /// <summary>Search results (search_project / get_project_tree output).</summary>
        SearchResults,
        /// <summary>Conversation history (list of user/assistant role messages).</summary>
        ChatHistory,
        /// <summary>Project context (ProjectIndex summary).</summary>
        ProjectContext,
        /// <summary>Variable references (query_variable_references output).</summary>
        VariableReferences,
        /// <summary>Unknown type — falls back to hard truncation.</summary>
        Unknown
    }

    /// <summary>
    /// Compression mode — affects the code-body retention strategy.
    /// </summary>
    public enum CompressMode
    {
        /// <summary>Analysis mode: keep declaration section + keyword-matching lines + first/last 3 body lines.</summary>
        Analysis,
        /// <summary>Generation mode: keep declaration section + first/last 5 body lines (structural reference for generation).</summary>
        Generation,
        /// <summary>Modification mode: keep declaration section + keyword-matching lines + first/last 10 body lines.</summary>
        Modification
    }

    /// <summary>
    /// Compression configuration — distinguishes the two code paths.
    /// </summary>
    public class CompressConfig
    {
        /// <summary>Maximum token budget (cloud LLM: 1500, 7B: 400).</summary>
        public int MaxTokens { get; set; } = 500;

        /// <summary>Whether LLM summarization is allowed (cloud LLM: true, 7B: false).</summary>
        public bool AllowLlmSummarize { get; set; } = false;

        /// <summary>Original user query — used for keyword matching to retain relevant code lines.</summary>
        public string UserQuery { get; set; } = string.Empty;

        /// <summary>Compression mode.</summary>
        public CompressMode Mode { get; set; } = CompressMode.Analysis;

        /// <summary>Creates the default configuration for the 7B local path.</summary>
        public static CompressConfig ForLocalModel(string userQuery = "")
            => new CompressConfig
            {
                MaxTokens = 400,
                AllowLlmSummarize = false,
                UserQuery = userQuery ?? string.Empty,
                Mode = CompressMode.Analysis
            };

        /// <summary>Creates the default configuration for the cloud LLM path.</summary>
        public static CompressConfig ForCloudModel(string userQuery = "")
            => new CompressConfig
            {
                MaxTokens = 1500,
                AllowLlmSummarize = true,
                UserQuery = userQuery ?? string.Empty,
                Mode = CompressMode.Analysis
            };
    }

    /// <summary>
    /// Compression request.
    /// </summary>
    public class CompressRequest
    {
        /// <summary>Original content.</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>Content type.</summary>
        public ContentType Type { get; set; } = ContentType.Unknown;

        /// <summary>Source tool name (aids classification).</summary>
        public string ToolName { get; set; } = string.Empty;

        /// <summary>Compression configuration.</summary>
        public CompressConfig Config { get; set; } = new CompressConfig();
    }

    /// <summary>
    /// Semantic-aware compressor — unified entry point that routes to different
    /// strategies by content type. Both paths (cloud LLM and 7B local) share the same
    /// code and differ only through CompressConfig.
    ///
    /// Core principles:
    ///   1. Code content (SCL/XML) uses rule-based compression only, never LLM summaries.
    ///   2. LLM summarization is used only for conversation history compression (old messages → summary).
    ///   3. Always preserves semantic structure; never mechanically truncates.
    /// </summary>
    public interface ISemanticCompressor
    {
        /// <summary>
        /// Compresses content while preserving semantic structure.
        /// </summary>
        /// <param name="request">Compression request.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Compressed content.</returns>
        Task<string> CompressAsync(CompressRequest request, CancellationToken ct = default);

        /// <summary>
        /// Infers the content type (based on tool name and output format).
        /// </summary>
        ContentType InferContentType(string toolName, string output);
    }
}