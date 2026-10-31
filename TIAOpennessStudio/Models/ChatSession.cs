using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Models
{
    public class ChatSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "新会话";
        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        // ── Session-scoped model selection (v1.16) ──
        // Each conversation window owns its model choice so the user can switch
        // models per-session without touching the global default. api_key is
        // intentionally NOT persisted here (secret); it is resolved at runtime
        // from the global AI config for the recorded provider.
        /// <summary>Model id/name used by this conversation (empty = follow global default).</summary>
        public string ModelName { get; set; } = "";
        /// <summary>Provider for this conversation's model (e.g. "OpenAI", "Ollama", "Custom").</summary>
        public string Provider { get; set; } = "";
        /// <summary>Upstream endpoint for this conversation's model.</summary>
        public string Endpoint { get; set; } = "";
    }
}