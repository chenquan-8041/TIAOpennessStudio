namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Shared, session-scoped source of truth for the target group paths the user
    /// picked in the status-bar chips (StatusBlockTargetChip / StatusTypeTargetChip).
    ///
    /// This bridges the gap between the UI selection (held in ChatViewModel) and the
    /// agent tool execution layer (ImportBlockCodeTool), which otherwise only sees the
    /// <c>group_path</c> parameter the LLM chooses to emit — and the LLM almost never
    /// emits it, so blocks silently land in the project root.
    ///
    /// Paths use backslash separators, e.g. "Factory\\Line1\\Robot". Empty string = root.
    /// </summary>
    public interface IAgentTargetGroupProvider
    {
        /// <summary>Target group for Blocks (FB/FC/DB/OB). Empty = PlcSoftware.BlockGroup root.</summary>
        string BlockGroupPath { get; set; }

        /// <summary>Target group for Types (UDT). Empty = PlcSoftware.TypeGroup root.</summary>
        string TypeGroupPath { get; set; }
    }

    /// <summary>
    /// Default thread-safe singleton implementation. Values are plain strings updated
    /// from the UI thread and read from the STA tool-execution path.
    /// </summary>
    public sealed class AgentTargetGroupProvider : IAgentTargetGroupProvider
    {
        private volatile string _blockGroupPath = "";
        private volatile string _typeGroupPath = "";

        public string BlockGroupPath
        {
            get => _blockGroupPath;
            set => _blockGroupPath = value ?? "";
        }

        public string TypeGroupPath
        {
            get => _typeGroupPath;
            set => _typeGroupPath = value ?? "";
        }
    }
}
