using System;

namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// G-4: a single cross-session user memory item — a user-level preference or habit
    /// (naming style, favored block kinds, language, frequently used standard libraries).
    ///
    /// Orthogonal to <c>project_key</c>: memory is keyed by <c>user_key</c> and follows
    /// the user across projects/sessions. Like planning knowledge, memory is advisory
    /// CONTEXT (labelled by source), never treated as verified project fact.
    /// </summary>
    public sealed class UserMemoryEntry
    {
        /// <summary>User identity (machine id / login / configured id). Isolation key.</summary>
        public string UserKey { get; set; }

        /// <summary>Stable dedup/identity key within a user (derived from category+key).</summary>
        public string MemoryKey { get; set; }

        /// <summary>Category, e.g. "naming" | "block_kind" | "language" | "standard_lib" | "misc".</summary>
        public string Category { get; set; }

        /// <summary>Short key/name of the preference, e.g. "prefix_style".</summary>
        public string Key { get; set; }

        /// <summary>The remembered value / statement.</summary>
        public string Value { get; set; }

        /// <summary>
        /// Provenance of this memory: "explicit" (user stated) | "derived" (extracted
        /// from history) | "summarized" (periodic rollup). Drives trust/labelling.
        /// </summary>
        public string Source { get; set; } = "explicit";

        /// <summary>Confidence 0..1 (explicit=1.0; derived/summarized lower).</summary>
        public double Confidence { get; set; } = 1.0;

        /// <summary>Reinforcement counter — how many times this memory was confirmed.</summary>
        public int HitCount { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.MinValue;
    }
}
