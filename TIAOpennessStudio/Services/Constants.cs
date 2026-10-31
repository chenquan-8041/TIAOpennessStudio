namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// PLC block type constants.
    /// </summary>
    public static class PlcBlockTypes
    {
        public const string FunctionBlock = "FB";
        public const string Function = "FC";
        public const string DataBlock = "DB";
        public const string OrganizationBlock = "OB";
        public const string Udt = "UDT";

        public const string FunctionBlockFull = "FUNCTION_BLOCK";
        public const string FunctionFull = "FUNCTION";
        public const string DataBlockFull = "DATA_BLOCK";
        public const string OrganizationBlockFull = "ORGANIZATION_BLOCK";
        public const string UdtFull = "TYPE";
    }

    /// <summary>
    /// API endpoint constants.
    /// </summary>
    public static class ApiEndpoints
    {
        public const string DefaultBackendUrl = "http://127.0.0.1:8000";
        public const string DefaultTiaContextUrl = "http://127.0.0.1:8001";
        public const string DefaultOllamaUrl = "http://localhost:11434";

        // Python Backend endpoints
        public const string Generate = "/generate";
        public const string GenerateStream = "/generate_stream";
        public const string ChatPlan = "/chat_plan";
        public const string ConvertXml = "/convert_xml";
        public const string ResolveXmlFragment = "/resolve_xml_fragment";
        public const string Validate = "/validate";
        public const string ProbeVision = "/probe_vision";
        public const string ProviderTest = "/provider/test";
        public const string Providers = "/providers";
        public const string Health = "/health";
        public const string Metrics = "/metrics";

        // TIA Context Server endpoints
        public const string BlockCode = "/api/block_code";
        public const string VariableType = "/api/variable_type";
        public const string ProjectBlocks = "/api/project_blocks";
        public const string TagTables = "/api/tag_tables";
        public const string DbStructure = "/api/db_structure";
        public const string UdtTypes = "/api/udt_types";
    }

    /// <summary>
    /// AI provider constants.
    /// </summary>
    public static class AiProviders
    {
        public const string OpenAI = "OpenAI";
        public const string Custom = "Custom";
        public const string Ollama = "Ollama";
        // Backend provider mapping
        public const string OpenAiCompat = "openai_compat";
        public const string Auto = "auto";
    }

    /// <summary>
    /// Agent step type constants.
    /// </summary>
    public static class AgentStepTypes
    {
        public const string AI = "AI";
        public const string Read = "Read";
        public const string Write = "Write";
        public const string Import = "Import";
        public const string Compile = "Compile";
        public const string Analyze = "Analyze";
        public const string Search = "Search";
    }

    /// <summary>
    /// SSE event type constants.
    /// </summary>
    public static class SseEventTypes
    {
        public const string Content = "content";
        public const string Delta = "delta";
        public const string Thinking = "thinking";
        public const string Error = "error";
        public const string Done = "done";
        public const string ToolRequest = "tool_request";
        public const string ToolCall = "tool_call";
        public const string TokenUsage = "token_usage";
        public const string PlanProgress = "plan_progress";
        public const string Milestone = "milestone";
        public const string Status = "status";
    }

    /// <summary>
    /// Default value constants.
    /// </summary>
    public static class DefaultValues
    {
        public const string BlockName = "AI_Generated_Block";
        public const string Model = "gpt-4o-mini";
        public const string Endpoint = "https://api.openai.com/v1/chat/completions";
        public const string Provider = AiProviders.OpenAI;
        public const string OllamaModel = "qwen2.5:0.5b";
        public const int MaxChatHistory = 12;
        /// <summary>
        /// Character budget for a single attached reference / exported IR. Raised from
        /// 50000 (pretty-printed JSON era) now that IR serialization is compact: cloud
        /// models have far larger context windows, and 50000 was still truncating dense
        /// LAD blocks to a handful of networks. <see cref="MaxTotalReferenceLength"/>
        /// remains the combined cap across all references.
        /// </summary>
        public const int MaxReferenceLength = 200000;
        public const int MaxXmlReferenceLength = 40000;
        public const int MaxTextReferenceLength = 20000;
        /// <summary>
        /// Total character budget across ALL user-attached references in the cloud
        /// analysis context. Each reference is already compacted to
        /// <see cref="MaxReferenceLength"/>; this caps their combined size so a large
        /// multi-block selection does not blow the model context. Replaces the old
        /// hard 5-block count cap.
        /// </summary>
        public const int MaxTotalReferenceLength = 500000;
    }
}
