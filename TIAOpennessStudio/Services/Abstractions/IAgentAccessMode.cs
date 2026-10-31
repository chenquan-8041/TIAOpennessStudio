using System;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// What the user allows the agent to do with the TIA project during a run.
    /// Chosen explicitly with the switch in the input box; nothing is guessed from the wording of the prompt.
    /// </summary>
    public enum AgentAccessMode
    {
        /// <summary>
        /// Analyse only. The agent may read and explain, but every write is held back and the user is asked
        /// whether to override. This is the default, so a new or restarted app never writes without being asked.
        /// </summary>
        ReadOnly,

        /// <summary>The agent may plan and write. Each write still goes through the normal approval card.</summary>
        AllowEdit
    }

    /// <summary>
    /// The access mode the user has selected, plus the per-run copy that the write gate reads.
    /// </summary>
    public interface IAgentAccessMode
    {
        /// <summary>The mode currently selected in the input box. New runs start with this value.</summary>
        AgentAccessMode Current { get; set; }

        /// <summary>Raised on the thread that changed <see cref="Current"/>.</summary>
        event Action<AgentAccessMode> Changed;

        /// <summary>
        /// Run id to mode, fixed when the run starts. A run keeps the mode it started with even if the user flips the
        /// switch while it is going, so a half-finished task never changes rules under the agent.
        /// </summary>
        void BindRun(string runId, AgentAccessMode mode);

        /// <summary>Forget a finished run.</summary>
        void ReleaseRun(string runId);

        /// <summary>
        /// The mode a write request is judged by. <paramref name="rid"/> is the X-Request-Id the Python backend sent;
        /// it equals the run id (see <c>RunContext</c>). Unknown rid (a caller that is not one of our runs, such as an
        /// external MCP client) falls back to <see cref="AgentAccessMode.ReadOnly"/>: fail closed.
        /// </summary>
        AgentAccessMode ModeFor(string rid);

        /// <summary>
        /// Raise one run to <see cref="AgentAccessMode.AllowEdit"/> after the user approved an override. The change
        /// covers that run only; it never touches <see cref="Current"/>.
        /// </summary>
        void ElevateRun(string runId);
    }
}
