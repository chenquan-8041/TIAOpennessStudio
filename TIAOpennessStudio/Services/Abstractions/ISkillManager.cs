using System.Collections.Generic;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Manages the lifecycle of agent skills: registration, versioning, search,
    /// installation, and execution.
    /// </summary>
    public interface ISkillManager
    {
        Skill RegisterSkill(Skill skill);
        Skill UpdateSkill(Skill skill);
        void DeleteSkill(string skillName);
        Skill GetSkill(string skillName);
        List<Skill> GetAllSkills();
        List<Skill> GetSkillsByCategory(string category);
        List<Skill> SearchSkills(string keyword);
        List<SkillVersion> GetVersionHistory(string skillName);
        Skill RollbackToVersion(string skillName, string version);
        void CheckPermission(string skillName, string action);
        void SetCurrentUserRoles(List<string> roles);
        bool CanExecute(string skillName);
        List<SkillMarketItem> GetSkillMarket();
        void InstallSkill(SkillMarketItem marketItem);
        void UninstallSkill(string skillName);
        SkillExecutionResult ExecuteSkill(string skillName, Dictionary<string, object> parameters);

        /// <summary>
        /// G-3: rank enabled skills by relevance to a task query (name/description/
        /// category/tags keyword overlap). Used by the agent loop to inject only the
        /// most relevant skill descriptions on demand, instead of dumping all skills.
        /// Returns at most <paramref name="limit"/> skills, most relevant first; empty
        /// when nothing matches.
        /// </summary>
        List<Skill> GetRelevantSkills(string query, int limit = 3);

        /// <summary>
        /// G-3: render a compact, prompt-ready description of the given skills for
        /// on-demand injection into the agent's system prompt. Returns empty string
        /// when the list is empty.
        /// </summary>
        string BuildSkillInjection(List<Skill> skills);
    }
}
