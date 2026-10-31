using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// Persists chat sessions to a local JSON file and provides CRUD access to them.
    /// </summary>
    public class ChatHistoryManager
    {
        private static readonly string HistoryFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chat_history.json");
        private List<ChatSession> _sessions = new List<ChatSession>();

        public ChatHistoryManager()
        {
            LoadHistory();
        }

        /// <summary>
        /// Loads all persisted sessions from disk.
        /// </summary>
        public List<ChatSession> LoadHistory()
        {
            try
            {
                if (File.Exists(HistoryFilePath))
                {
                    string json = File.ReadAllText(HistoryFilePath);
                    _sessions = JsonSerializer.Deserialize<List<ChatSession>>(json) ?? new List<ChatSession>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error loading chat history: " + ex.Message);
                _sessions = new List<ChatSession>();
            }
            return _sessions;
        }

        /// <summary>
        /// Serializes all sessions to the backing JSON file.
        /// </summary>
        public void SaveHistory()
        {
            try
            {
                string json = JsonSerializer.Serialize(_sessions);
                File.WriteAllText(HistoryFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error saving chat history: " + ex.Message);
            }
        }

        /// <summary>
        /// Inserts the session if new, otherwise updates the existing one, then persists.
        /// </summary>
        public void SaveSession(ChatSession session)
        {
            var existing = GetSession(session.Id);
            if (existing != null)
            {
                existing.Messages = session.Messages;
                existing.UpdatedAt = DateTime.Now;
                // Persist session-scoped model selection on update.
                existing.ModelName = session.ModelName;
                existing.Provider = session.Provider;
                existing.Endpoint = session.Endpoint;
                if (session.Messages.Count > 0)
                {
                    var firstUserMsg = session.Messages.Find(m => m.Role == "user");
                    if (firstUserMsg != null && !string.IsNullOrWhiteSpace(firstUserMsg.Content))
                    {
                        existing.Title = firstUserMsg.Content.Length > 30 
                            ? firstUserMsg.Content.Substring(0, 30) + "..." 
                            : firstUserMsg.Content;
                    }
                }
            }
            else
            {
                // Derive a title from the first user message.
                if (session.Messages.Count > 0)
                {
                    var firstUserMsg = session.Messages.Find(m => m.Role == "user");
                    if (firstUserMsg != null && !string.IsNullOrWhiteSpace(firstUserMsg.Content))
                    {
                        session.Title = firstUserMsg.Content.Length > 30 
                            ? firstUserMsg.Content.Substring(0, 30) + "..." 
                            : firstUserMsg.Content;
                    }
                }
                _sessions.Insert(0, session);
            }
            SaveHistory();
        }

        /// <summary>
        /// Returns the session with the specified id, or null if not found.
        /// </summary>
        public ChatSession GetSession(string sessionId)
        {
            return _sessions.Find(s => s.Id == sessionId);
        }

        /// <summary>
        /// Removes the session with the specified id and persists the change.
        /// </summary>
        public void DeleteSession(string sessionId)
        {
            _sessions.RemoveAll(s => s.Id == sessionId);
            SaveHistory();
        }

        /// <summary>
        /// Returns all sessions sorted by update time (most recent first).
        /// </summary>
        public List<ChatSession> GetAllSessions()
        {
            _sessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            return _sessions;
        }

        /// <summary>
        /// Creates a new session with a fresh id and default title (not yet persisted).
        /// </summary>
        public ChatSession CreateNewSession()
        {
            return new ChatSession
            {
                Id = Guid.NewGuid().ToString(),
                Title = "新会话",
                Messages = new List<ChatMessage>(),
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
        }
    }
}