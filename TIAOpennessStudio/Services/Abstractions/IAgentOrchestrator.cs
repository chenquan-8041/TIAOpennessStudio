using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Orchestrates the agent execution loop (planning, tool execution, import, and
    /// compile) for chat and internal callers.
    /// </summary>
    public interface IAgentOrchestrator
    {
        /// <summary>
        /// Minimal entry point - prompt only, no target block, no references, no history.
        /// Suitable for internal callers (smoke tests, tools) that don't need full chat context.
        /// </summary>
        Task RunAgentActionAsync(
            string prompt,
            Action<AgentActionPlan> onPlanUpdated,
            Action<string> onContentDelta,
            CancellationToken ct = default);

        /// <summary>
        /// Full entry point used by the chat UI. Carries everything the assistant needs:
        ///   - target block info (when user picked one in the tree),
        ///   - user-attached references (file drops, tree drags, context menu adds),
        ///   - chat history for multi-turn memory,
        ///   - target group paths for write-back (Blocks group / Types group).
        /// Pass null/empty for parts that don't apply.
        /// </summary>
        Task RunAgentActionAsync(
            string prompt,
            string targetBlockName,
            string targetBlockType,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            string targetBlockGroupPath,
            string targetTypeGroupPath,
            Action<AgentActionPlan> onPlanUpdated,
            Action<string> onContentDelta,
            CancellationToken ct = default);

        /// <summary>
        /// Same as the full entry point above but with a typed event callback so the chat UI can
        /// route different kinds of stream content (text / thinking / tool-call / error) into
        /// different message blocks instead of mashing everything into one string.
        /// </summary>
        Task RunAgentActionAsync(
            string prompt,
            string targetBlockName,
            string targetBlockType,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            string targetBlockGroupPath,
            string targetTypeGroupPath,
            Action<AgentActionPlan> onPlanUpdated,
            Action<AgentStreamEvent> onAgentEvent,
            CancellationToken ct = default);

        /// <summary>
        /// Same as above but with base64-encoded images for vision-capable models.
        /// </summary>
        Task RunAgentActionAsync(
            string prompt,
            string targetBlockName,
            string targetBlockType,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            string targetBlockGroupPath,
            string targetTypeGroupPath,
            List<string> images,
            Action<AgentActionPlan> onPlanUpdated,
            Action<AgentStreamEvent> onAgentEvent,
            CancellationToken ct = default);

        /// <summary>
        /// Explicitly recognize user intent from prompt.
        /// </summary>
        Task<AgentIntent> RecognizeIntentAsync(string prompt, List<Dictionary<string, string>> history = null);

        /// <summary>
        /// Confirms a step that is in NeedConfirm state.
        /// </summary>
        void ConfirmStep(string planId, string stepId, bool confirmed = true);

        /// <summary>
        /// Gets the currently active plan, used to read SharedData (e.g. ReadCode,
        /// PendingWriteCode) while confirming a step.
        /// </summary>
        AgentActionPlan GetPlan(string planId);

        /// <summary>
        /// Uses AI to compress the conversation history into a short summary, returning
        /// a single compressed system message. Returns null on failure; callers should
        /// fall back to truncation.
        /// </summary>
        Task<Dictionary<string, string>> CompressHistoryAsync(
            List<Dictionary<string, string>> history,
            CancellationToken ct = default);
    }
}
