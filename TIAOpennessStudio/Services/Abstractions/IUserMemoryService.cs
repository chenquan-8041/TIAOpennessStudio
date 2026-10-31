using System.Collections.Generic;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// G-4: cross-session user memory. Stores user-level preferences/habits (naming
    /// style, favored block kinds, language, standard libraries) keyed by user_key,
    /// orthogonal to project_key so it follows the user across projects and sessions.
    ///
    /// Memory is advisory context (labelled by source), never verified project fact —
    /// same discipline as planning knowledge.
    /// </summary>
    public interface IUserMemoryService
    {
        /// <summary>
        /// Record (upsert) a user memory item. Re-recording the same category+key
        /// reinforces it (HitCount++) and refreshes value/confidence/timestamp.
        /// </summary>
        void Record(string userKey, string category, string key, string value,
            string source = "explicit", double confidence = 1.0);

        /// <summary>
        /// Recall memory items for a user, optionally filtered by category, most
        /// reinforced/recent first. Query tokens (optional) further rank by keyword
        /// overlap on key/value.
        /// </summary>
        List<UserMemoryEntry> Recall(string userKey, string query = null, string category = null, int limit = 10);

        /// <summary>Remove a specific memory item (category+key) for a user.</summary>
        void Forget(string userKey, string category, string key);

        /// <summary>Clear all memory for a user.</summary>
        void ClearUser(string userKey);

        /// <summary>
        /// Render recalled memory as an advisory context block for prompt injection.
        /// Clearly labelled as user preference (not project fact). Empty when no memory.
        /// </summary>
        string BuildMemoryContext(string userKey, string query = null, int limit = 8);
    }
}
