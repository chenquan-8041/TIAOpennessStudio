using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Implementations;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Agent persistence service — unifies the persistence and recovery of Agent
    /// execution state, session memory, and tool call history.
    ///
    /// Persisted content:
    ///   1. AgentActionPlan execution snapshots (supporting crash recovery)
    ///   2. Cross-session Agent memory (project context, common patterns)
    ///   3. Tool call history (for audit and debugging)
    ///   4. Session-level ReAct iteration state
    /// </summary>
    public class AgentPersistenceService
    {
        private static readonly string BaseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AgentData");
        private static readonly string PlansDir = Path.Combine(BaseDir, "Plans");
        private static readonly string MemoryDir = Path.Combine(BaseDir, "Memory");
        private static readonly string TracesDir = Path.Combine(BaseDir, "Traces");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public AgentPersistenceService()
        {
            EnsureDirectories();
        }

        #region Directory initialization

        private static void EnsureDirectories()
        {
            Directory.CreateDirectory(PlansDir);
            Directory.CreateDirectory(MemoryDir);
            Directory.CreateDirectory(TracesDir);
        }

        #endregion

        #region Plan persistence (AgentActionPlan snapshots)

        /// <summary>
        /// Saves an AgentActionPlan snapshot. Called whenever the plan state changes.
        /// File name: Plans/{plan.Id}.json
        /// </summary>
        public void SavePlanSnapshot(AgentActionPlan plan)
        {
            if (plan == null) return;
            try
            {
                var snapshot = PlanSnapshot.FromPlan(plan);
                var path = GetPlanPath(plan.Id);
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentPersistence] SavePlanSnapshot failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads an AgentActionPlan snapshot.
        /// </summary>
        public PlanSnapshot LoadPlanSnapshot(string planId)
        {
            try
            {
                var path = GetPlanPath(planId);
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<PlanSnapshot>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentPersistence] LoadPlanSnapshot failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets all incomplete plans (for crash recovery).
        /// </summary>
        public List<PlanSnapshot> GetIncompletePlans()
        {
            var result = new List<PlanSnapshot>();
            try
            {
                foreach (var file in Directory.GetFiles(PlansDir, "*.json"))
                {
                    var json = File.ReadAllText(file);
                    var snapshot = JsonSerializer.Deserialize<PlanSnapshot>(json, JsonOptions);
                    if (snapshot != null && !snapshot.IsCompleted)
                        result.Add(snapshot);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentPersistence] GetIncompletePlans failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Deletes a plan snapshot.
        /// </summary>
        public void DeletePlanSnapshot(string planId)
        {
            try
            {
                var path = GetPlanPath(planId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentPersistence] DeletePlanSnapshot failed: {ex.Message}");
            }
        }

        private static string GetPlanPath(string planId) =>
            Path.Combine(PlansDir, $"{SanitizeFileName(planId)}.json");

        #endregion

        #region Agent memory persistence (cross-session)

        /// <summary>
        /// Saves an Agent memory entry. Memories are keyed and support overwrite.
        /// File: Memory/{key}.json
        /// </summary>
        public void SaveMemory(string key, AgentMemoryEntry entry)
        {
            if (string.IsNullOrWhiteSpace(key) || entry == null) return;
            try
            {
                entry.UpdatedAt = DateTime.UtcNow;
                var path = GetMemoryPath(key);
                var json = JsonSerializer.Serialize(entry, JsonOptions);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentPersistence] SaveMemory failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads an Agent memory entry.
        /// </summary>
        public AgentMemoryEntry LoadMemory(string key)
        {
            try
            {
                var path = GetMemoryPath(key);
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AgentMemoryEntry>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentPersistence] LoadMemory failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets all memory entries.
        /// </summary>
        public List<AgentMemoryEntry> GetAllMemories()
        {
            var result = new List<AgentMemoryEntry>();
            try
            {
                foreach (var file in Directory.GetFiles(MemoryDir, "*.json"))
                {
                    var json = File.ReadAllText(file);
                    var entry = JsonSerializer.Deserialize<AgentMemoryEntry>(json, JsonOptions);
                    if (entry != null) result.Add(entry);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentPersistence] GetAllMemories failed: {ex.Message}");
            }
            return result.OrderByDescending(m => m.UpdatedAt).ToList();
        }

        /// <summary>
        /// Saves project context memory (auto-deduped and merged).
        /// </summary>
        public void SaveProjectContextMemory(string projectName, string contextSummary)
        {
            var key = $"project_ctx_{SanitizeFileName(projectName ?? "default")}";
            var existing = LoadMemory(key) ?? new AgentMemoryEntry
            {
                Key = key,
                Category = "project_context",
                CreatedAt = DateTime.UtcNow
            };

            // Merge contexts (keep the most recent 5).
            var contexts = existing.Tags ?? new List<string>();
            contexts.Insert(0, contextSummary);
            existing.Tags = contexts.Take(5).ToList();
            existing.Value = contextSummary;

            SaveMemory(key, existing);
        }

        /// <summary>
        /// Saves a common code pattern memory.
        /// </summary>
        public void SaveCodePatternMemory(string patternName, string sclCode, string description)
        {
            var key = $"pattern_{SanitizeFileName(patternName)}";
            var entry = new AgentMemoryEntry
            {
                Key = key,
                Category = "code_pattern",
                Value = sclCode,
                Description = description,
                CreatedAt = DateTime.UtcNow
            };
            SaveMemory(key, entry);
        }

        /// <summary>
        /// Gets all code patterns.
        /// </summary>
        public List<AgentMemoryEntry> GetCodePatterns()
        {
            return GetAllMemories().Where(m => m.Category == "code_pattern").ToList();
        }

        private static string GetMemoryPath(string key) =>
            Path.Combine(MemoryDir, $"{SanitizeFileName(key)}.json");

        #endregion

        #region Tool call history

        /// <summary>
        /// Appends a tool call record to the session trace file.
        /// File: Traces/{sessionId}.jsonl
        /// </summary>
        public void AppendToolTrace(string sessionId, ToolCallRecord record)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || record == null) return;
            try
            {
                var path = GetTracePath(sessionId);
                var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = false });
                File.AppendAllText(path, json + Environment.NewLine);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentPersistence] AppendToolTrace failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads a session's tool call history.
        /// </summary>
        public List<ToolCallRecord> LoadToolTraces(string sessionId)
        {
            var result = new List<ToolCallRecord>();
            try
            {
                var path = GetTracePath(sessionId);
                if (!File.Exists(path)) return result;

                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var record = JsonSerializer.Deserialize<ToolCallRecord>(line, JsonOptions);
                        if (record != null) result.Add(record);
                    }
                    catch { /* skip malformed lines */ }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentPersistence] LoadToolTraces failed: {ex.Message}");
            }
            return result;
        }

        private static string GetTracePath(string sessionId) =>
            Path.Combine(TracesDir, $"{SanitizeFileName(sessionId)}.jsonl");

        #endregion

        #region Session recovery

        /// <summary>
        /// Attempts to recover incomplete Agent executions. Returns the list of
        /// recoverable PlanSnapshots; the caller decides whether to continue.
        /// </summary>
        public List<PlanSnapshot> GetRecoverablePlans()
        {
            return GetIncompletePlans()
                .Where(p => (DateTime.UtcNow - p.LastUpdated).TotalHours < 24)
                .ToList();
        }

        /// <summary>
        /// Cleans up expired data (completed plans older than 7 days, traces older
        /// than 30 days).
        /// </summary>
        public void CleanupExpiredData()
        {
            try
            {
                var cutoffPlans = DateTime.UtcNow.AddDays(-7);
                foreach (var file in Directory.GetFiles(PlansDir, "*.json"))
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTimeUtc < cutoffPlans)
                    {
                        // Only clean up completed ones.
                        var json = File.ReadAllText(file);
                        var snapshot = JsonSerializer.Deserialize<PlanSnapshot>(json, JsonOptions);
                        if (snapshot?.IsCompleted == true)
                            File.Delete(file);
                    }
                }

                var cutoffTraces = DateTime.UtcNow.AddDays(-30);
                foreach (var file in Directory.GetFiles(TracesDir, "*.jsonl"))
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTimeUtc < cutoffTraces)
                        File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentPersistence] CleanupExpiredData failed: {ex.Message}");
            }
        }

        #endregion

        #region Helper methods

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unnamed";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '_';
            }
            return new string(chars);
        }

        #endregion
    }

    #region Persistence data models

    /// <summary>
    /// A serializable snapshot of an AgentActionPlan.
    /// </summary>
    public class PlanSnapshot
    {
        public string PlanId { get; set; }
        public string Rid { get; set; }
        public string Intent { get; set; }
        public string Prompt { get; set; }
        public string TargetBlockName { get; set; }
        public string TargetBlockType { get; set; }
        public string State { get; set; }
        public string Phase { get; set; }
        public List<StepSnapshot> Steps { get; set; } = new();
        public Dictionary<string, string> SharedData { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public int RetryCount { get; set; }
        public int FixCycleCount { get; set; }
        public bool IsCompleted => State == "Done" || State == "Cancelled" || State == "Failed";

        public static PlanSnapshot FromPlan(AgentActionPlan plan)
        {
            var snapshot = new PlanSnapshot
            {
                PlanId = plan.Id,
                Rid = plan.Rid,
                Intent = plan.Intent.ToString(),
                Prompt = plan.Prompt,
                TargetBlockName = plan.TargetBlockName,
                TargetBlockType = plan.TargetBlockType,
                State = plan.State.ToString(),
                Phase = plan.Phase.ToString(),
                CreatedAt = plan.CreatedAt,
                LastUpdated = DateTime.UtcNow,
                RetryCount = plan.RetryCount,
                FixCycleCount = plan.FixCycleCount,
                Steps = plan.Steps.Select(s => new StepSnapshot
                {
                    StepId = s.Id,
                    Description = s.Description,
                    ActionType = s.ActionType,
                    State = s.State.ToString(),
                    Output = s.Output,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime
                }).ToList()
            };

            // Only serialize string-typed SharedData.
            foreach (var kv in plan.SharedData)
            {
                if (kv.Value is string s)
                    snapshot.SharedData[kv.Key] = s;
                else if (kv.Value != null)
                    snapshot.SharedData[kv.Key] = kv.Value.ToString();
            }

            return snapshot;
        }
    }

    /// <summary>
    /// A serializable snapshot of an AgentActionStep.
    /// </summary>
    public class StepSnapshot
    {
        public string StepId { get; set; }
        public string Description { get; set; }
        public string ActionType { get; set; }
        public string State { get; set; }
        public string Output { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }

    /// <summary>
    /// Agent memory entry — a knowledge fragment persisted across sessions.
    /// </summary>
    public class AgentMemoryEntry
    {
        /// <summary>Unique key (e.g. "project_ctx_MyProject", "pattern_MotorControl").</summary>
        public string Key { get; set; }

        /// <summary>Category: project_context / code_pattern / user_preference / block_template / learned_rule.</summary>
        public string Category { get; set; }

        /// <summary>Primary value (code, context summary, etc.).</summary>
        public string Value { get; set; }

        /// <summary>Description / note.</summary>
        public string Description { get; set; }

        /// <summary>List of tags.</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>Usage count (used for priority ordering).</summary>
        public int UseCount { get; set; }

        /// <summary>Creation time.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Last update time.</summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    #endregion
}
