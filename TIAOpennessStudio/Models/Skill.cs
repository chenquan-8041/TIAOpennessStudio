using System;
using System.Collections.Generic;

namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// Definition of a skill parameter.
    /// </summary>
    public class SkillParameter
    {
        public string Name { get; set; }
        public string Type { get; set; }  // string, int, bool, object, array
        public string Description { get; set; }
        public bool Required { get; set; }
        public object DefaultValue { get; set; }
    }

    /// <summary>
    /// Executable definition of a skill.
    /// </summary>
    public class SkillDefinition
    {
        public List<SkillParameter> Parameters { get; set; } = new List<SkillParameter>();
        public string ExecutionLogic { get; set; }  // Execution logic (Python / Lambda / JSON)
        public List<string> Dependencies { get; set; } = new List<string>();
        public string ReturnType { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Skill permission and rate-limit settings.
    /// </summary>
    public class SkillPermissions
    {
        public List<string> AllowedRoles { get; set; } = new List<string> { "Admin", "User" };
        public bool RequireApproval { get; set; }
        public int RateLimit { get; set; } = 60;  // Calls per minute
        public bool IsPublic { get; set; } = true;
    }

    /// <summary>
    /// Version record of a skill.
    /// </summary>
    public class SkillVersion
    {
        public string Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Changelog { get; set; }
        public bool IsDeprecated { get; set; }
    }

    /// <summary>
    /// A skill.
    /// </summary>
    public class Skill
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Description { get; set; }
        public string Version { get; set; } = "1.0.0";
        public string Category { get; set; }  // e.g. "data processing", "file operations", "API calls"
        public string Author { get; set; }
        public string Icon { get; set; }  // Icon URL or Base64 data
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public SkillDefinition Definition { get; set; } = new SkillDefinition();
        public SkillPermissions Permissions { get; set; } = new SkillPermissions();
        public List<SkillVersion> VersionHistory { get; set; } = new List<SkillVersion>();
        public int UsageCount { get; set; }
        public double Rating { get; set; }
        public bool IsEnabled { get; set; } = true;
        public List<string> Tags { get; set; } = new List<string>();
    }

    /// <summary>
    /// A skill listed in the marketplace.
    /// </summary>
    public class SkillMarketItem
    {
        public Skill Skill { get; set; }
        public int InstallCount { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public string RepositoryUrl { get; set; }
        public string License { get; set; }
    }

    /// <summary>
    /// Result of a skill execution.
    /// </summary>
    public class SkillExecutionResult
    {
        public bool Success { get; set; }
        public object Result { get; set; }
        public string ErrorMessage { get; set; }
        public long ExecutionTimeMs { get; set; }
        public DateTime ExecutedAt { get; set; } = DateTime.Now;
    }
}