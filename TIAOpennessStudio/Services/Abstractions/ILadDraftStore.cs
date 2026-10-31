using System.Collections.Generic;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// G-6: session-level store for incremental LAD drafts. Holds the active draft
    /// per session and persists it to <c>Drafts/{sessionId}.json</c> for restart
    /// recovery. Compose (draft → LAD block XML) lives here so the draft tools stay
    /// thin. STA/import is NOT done here — the commit tool marshals import to STA.
    /// </summary>
    public interface ILadDraftStore
    {
        /// <summary>Create/replace a draft for a session. Returns the new draft.</summary>
        LadDraft Begin(LadDraft draft);

        /// <summary>Get the active draft for a session (null if none).</summary>
        LadDraft Get(string sessionId);

        /// <summary>Append a network to the session's draft. Throws if no draft.</summary>
        LadDraft AppendNetwork(string sessionId, LadDraftNetwork network);

        /// <summary>Discard a session's draft (memory + persisted file).</summary>
        void Discard(string sessionId);

        /// <summary>
        /// Compose the current draft into complete LAD block XML.
        /// Phase 1: only <c>ladfc</c> composes (reuses PlcLadFcBlockXmlComposer).
        /// Returns (ok, xmlOrError). ladfb returns ok=false with a "not yet" note.
        /// </summary>
        (bool Ok, string XmlOrError) ComposeXml(string sessionId);

        /// <summary>All session ids with an active draft (diagnostics).</summary>
        IReadOnlyList<string> ActiveSessions();
    }
}
