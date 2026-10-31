using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Skills management service - implements skill registration, version management, and permission checks.
    /// </summary>
    public class SkillManager : ISkillManager
    {
        private static readonly string SkillsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skills");
        private static readonly string SkillsIndexFile = Path.Combine(SkillsDirectory, "skills_index.json");
        private readonly Dictionary<string, Skill> _skills = new Dictionary<string, Skill>();
        private readonly List<string> _currentUserRoles = new List<string> { "Admin" };  // Default admin role

        #region Initialization

        public SkillManager()
        {
            EnsureDirectoryExists();
            LoadSkillsIndex();
        }

        private void EnsureDirectoryExists()
        {
            if (!Directory.Exists(SkillsDirectory))
            {
                Directory.CreateDirectory(SkillsDirectory);
            }
        }

        #endregion

        #region Skill registration and management

        /// <summary>
        /// Registers a new skill.
        /// </summary>
        public Skill RegisterSkill(Skill skill)
        {
            if (string.IsNullOrEmpty(skill.Name))
            {
                throw new ArgumentException("Skill name cannot be empty");
            }

            // Check whether a skill with the same name already exists
            if (_skills.ContainsKey(skill.Name.ToLower()))
            {
                throw new InvalidOperationException($"Skill '{skill.Name}' already exists. Use UpdateSkill to update.");
            }

            skill.Id = Guid.NewGuid().ToString();
            skill.CreatedAt = DateTime.Now;
            skill.UpdatedAt = DateTime.Now;
            
            // Add version history
            skill.VersionHistory.Add(new SkillVersion
            {
                Version = skill.Version,
                CreatedAt = DateTime.Now,
                Changelog = "Initial version"
            });

            _skills[skill.Name.ToLower()] = skill;
            SaveSkill(skill);
            SaveSkillsIndex();

            return skill;
        }

        /// <summary>
        /// Updates a skill.
        /// </summary>
        public Skill UpdateSkill(Skill skill)
        {
            var existing = GetSkill(skill.Name);
            if (existing == null)
            {
                throw new InvalidOperationException($"Skill '{skill.Name}' not found");
            }

            // Permission check
            CheckPermission(skill.Name, "update");

            // If the version number changed, append to history
            if (skill.Version != existing.Version)
            {
                skill.VersionHistory.Add(new SkillVersion
                {
                    Version = skill.Version,
                    CreatedAt = DateTime.Now,
                    Changelog = $"Updated from {existing.Version}"
                });
            }

            skill.Id = existing.Id;
            skill.CreatedAt = existing.CreatedAt;
            skill.UpdatedAt = DateTime.Now;
            skill.UsageCount = existing.UsageCount;

            _skills[skill.Name.ToLower()] = skill;
            SaveSkill(skill);
            SaveSkillsIndex();

            return skill;
        }

        /// <summary>
        /// Deletes a skill.
        /// </summary>
        public void DeleteSkill(string skillName)
        {
            var skill = GetSkill(skillName);
            if (skill == null)
            {
                throw new InvalidOperationException($"Skill '{skillName}' not found");
            }

            // Permission check - only admins may delete
            CheckPermission(skillName, "delete");

            _skills.Remove(skillName.ToLower());
            
            // Delete the skill file
            string skillFile = GetSkillFilePath(skillName);
            if (File.Exists(skillFile))
            {
                File.Delete(skillFile);
            }

            SaveSkillsIndex();
        }

        /// <summary>
        /// Gets a skill.
        /// </summary>
        public Skill GetSkill(string skillName)
        {
            _skills.TryGetValue(skillName.ToLower(), out var skill);
            return skill;
        }

        /// <summary>
        /// Gets all skills.
        /// </summary>
        public List<Skill> GetAllSkills()
        {
            return _skills.Values.Where(s => s.IsEnabled).ToList();
        }

        /// <summary>
        /// Gets skills by category.
        /// </summary>
        public List<Skill> GetSkillsByCategory(string category)
        {
            return _skills.Values
                .Where(s => s.IsEnabled && s.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Searches skills.
        /// </summary>
        public List<Skill> SearchSkills(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return GetAllSkills();
            }

            keyword = keyword.ToLower();
            return _skills.Values
                .Where(s => s.IsEnabled && (
                    s.Name.ToLower().Contains(keyword) ||
                    s.Description.ToLower().Contains(keyword) ||
                    s.Category.ToLower().Contains(keyword) ||
                    (s.Tags != null && s.Tags.Any(t => t.ToLower().Contains(keyword)))
                ))
                .ToList();
        }

        #endregion

        #region Version management

        /// <summary>
        /// Gets a skill's version history.
        /// </summary>
        public List<SkillVersion> GetVersionHistory(string skillName)
        {
            var skill = GetSkill(skillName);
            if (skill == null)
            {
                throw new InvalidOperationException($"Skill '{skillName}' not found");
            }
            return skill.VersionHistory;
        }

        /// <summary>
        /// Rolls back to the specified version.
        /// </summary>
        public Skill RollbackToVersion(string skillName, string version)
        {
            var skill = GetSkill(skillName);
            if (skill == null)
            {
                throw new InvalidOperationException($"Skill '{skillName}' not found");
            }

            CheckPermission(skillName, "update");

            var targetVersion = skill.VersionHistory.FirstOrDefault(v => v.Version == version);
            if (targetVersion == null)
            {
                throw new InvalidOperationException($"Version '{version}' not found");
            }

            // Reload the skill definition for that version
            string skillFile = GetSkillFilePath(skillName, version);
            if (!File.Exists(skillFile))
            {
                throw new FileNotFoundException($"Skill file for version '{version}' not found");
            }

            var json = File.ReadAllText(skillFile);
            var oldSkill = JsonSerializer.Deserialize<Skill>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            // Update the current version
            skill.Definition = oldSkill.Definition;
            skill.Version = version;
            skill.UpdatedAt = DateTime.Now;

            SaveSkill(skill);
            SaveSkillsIndex();

            return skill;
        }

        #endregion

        #region Permission checks

        /// <summary>
        /// Checks permission.
        /// </summary>
        public void CheckPermission(string skillName, string action)
        {
            var skill = GetSkill(skillName);
            if (skill == null) return;  // Skip the check when the skill does not exist

            var permissions = skill.Permissions;
            
            // Check role permissions
            if (permissions.AllowedRoles != null && permissions.AllowedRoles.Count > 0)
            {
                var hasRole = _currentUserRoles.Any(role => permissions.AllowedRoles.Contains(role));
                if (!hasRole)
                {
                    throw new UnauthorizedAccessException($"User role not authorized to {action} skill '{skillName}'");
                }
            }

            // Check rate limit
            if (action == "execute" && permissions.RateLimit > 0)
            {
                // More sophisticated rate-limiting logic could be added here.
                // Simplified implementation: not handled for now.
            }
        }

        /// <summary>
        /// Sets the current user roles.
        /// </summary>
        public void SetCurrentUserRoles(List<string> roles)
        {
            _currentUserRoles.Clear();
            _currentUserRoles.AddRange(roles);
        }

        /// <summary>
        /// Checks whether a skill can be executed.
        /// </summary>
        public bool CanExecute(string skillName)
        {
            try
            {
                CheckPermission(skillName, "execute");
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Skill market

        /// <summary>
        /// Gets skill market items.
        /// </summary>
        public List<SkillMarketItem> GetSkillMarket()
        {
            return _skills.Values
                .Where(s => s.IsEnabled && s.Permissions.IsPublic)
                .Select(s => new SkillMarketItem
                {
                    Skill = s,
                    InstallCount = s.UsageCount,
                    Tags = s.Category?.Split(',').ToList() ?? new List<string>()
                })
                .OrderByDescending(m => m.InstallCount)
                .ToList();
        }

        /// <summary>
        /// Installs a skill (from the market).
        /// </summary>
        public void InstallSkill(SkillMarketItem marketItem)
        {
            var skill = marketItem.Skill;
            
            // Check whether it is already installed
            if (GetSkill(skill.Name) != null)
            {
                throw new InvalidOperationException($"Skill '{skill.Name}' is already installed");
            }

            // Reassign an ID and register
            skill.Id = Guid.NewGuid().ToString();
            skill.CreatedAt = DateTime.Now;
            skill.UpdatedAt = DateTime.Now;
            
            _skills[skill.Name.ToLower()] = skill;
            SaveSkill(skill);
            SaveSkillsIndex();
        }

        /// <summary>
        /// Uninstalls a skill.
        /// </summary>
        public void UninstallSkill(string skillName)
        {
            DeleteSkill(skillName);
        }

        #endregion

        #region Skill execution

        /// <summary>
        /// Executes a skill.
        /// </summary>
        public SkillExecutionResult ExecuteSkill(string skillName, Dictionary<string, object> parameters)
        {
            var startTime = DateTime.Now;
            var result = new SkillExecutionResult();

            try
            {
                var skill = GetSkill(skillName);
                if (skill == null)
                {
                    throw new InvalidOperationException($"Skill '{skillName}' not found");
                }

                // Permission check
                CheckPermission(skillName, "execute");

                // Parameter validation
                ValidateParameters(skill, parameters);

                // Execution logic (simplified here; a real implementation might call an external interpreter or service)
                var executionResult = ExecuteSkillLogic(skill, parameters);

                result.Success = true;
                result.Result = executionResult;
                result.ExecutedAt = DateTime.Now;

                // Update usage count
                skill.UsageCount++;
                SaveSkillsIndex();
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ExecutedAt = DateTime.Now;
            }
            finally
            {
                result.ExecutionTimeMs = (long)(DateTime.Now - startTime).TotalMilliseconds;
            }

            return result;
        }

        private void ValidateParameters(Skill skill, Dictionary<string, object> parameters)
        {
            if (skill.Definition?.Parameters == null) return;

            foreach (var param in skill.Definition.Parameters)
            {
                if (param.Required && (!parameters.ContainsKey(param.Name) || parameters[param.Name] == null))
                {
                    throw new ArgumentException($"Required parameter '{param.Name}' is missing");
                }
            }
        }

        /// <summary>
        /// Optional handler registry: skills whose <c>ExecutionLogic</c> names a
        /// registered handler run real code. Other skills resolve to a
        /// prompt-injection contract (the agent uses the skill's guidance in-loop
        /// rather than executing a black box). This replaces the old "simplified"
        /// placeholder that returned a fake success for everything.
        /// </summary>
        private readonly Dictionary<string, Func<Skill, Dictionary<string, object>, object>> _executionHandlers
            = new Dictionary<string, Func<Skill, Dictionary<string, object>, object>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Register a real execution handler for a given <c>ExecutionLogic</c> key.
        /// The host wires handlers (e.g. "web_search", "calculator") to actual services.
        /// </summary>
        public void RegisterExecutionHandler(string executionLogic, Func<Skill, Dictionary<string, object>, object> handler)
        {
            if (string.IsNullOrWhiteSpace(executionLogic) || handler == null) return;
            _executionHandlers[executionLogic.Trim()] = handler;
        }

        private object ExecuteSkillLogic(Skill skill, Dictionary<string, object> parameters)
        {
            var logic = (skill.Definition?.ExecutionLogic ?? "").Trim();

            // 1) A registered handler → run real code.
            if (!string.IsNullOrEmpty(logic) && _executionHandlers.TryGetValue(logic, out var handler))
            {
                return handler(skill, parameters ?? new Dictionary<string, object>());
            }

            // 2) No handler: this skill is a PROMPT-INJECTION skill (knowledge/guidance),
            // not an executable black box. Return its injectable description so the caller
            // can fold it into the agent context. This is an explicit, honest contract —
            // not a fake "success".
            var injection = BuildSkillInjection(new List<Skill> { skill });
            if (!string.IsNullOrWhiteSpace(injection))
            {
                return new
                {
                    skillName = skill.Name,
                    kind = "prompt-injection",
                    injection,
                    note = "No executable handler registered; this skill provides guidance to inject into the agent context."
                };
            }

            // 3) Truly empty skill definition — surface it as a failure rather than a fake OK.
            throw new InvalidOperationException(
                $"Skill '{skill.Name}' has no executable handler for ExecutionLogic '{logic}' and no injectable content.");
        }

        // ---- G-3: on-demand relevance + prompt injection ----

        /// <inheritdoc />
        public List<Skill> GetRelevantSkills(string query, int limit = 3)
        {
            limit = Math.Max(1, Math.Min(limit <= 0 ? 3 : limit, 20));
            var enabled = _skills.Values.Where(s => s.IsEnabled).ToList();
            if (enabled.Count == 0) return new List<Skill>();

            var tokens = Tokenize(query);
            if (tokens.Count == 0) return new List<Skill>();

            var scored = new List<(Skill Skill, int Score)>();
            foreach (var s in enabled)
            {
                int score = 0;
                score += MatchScore(s.Name, tokens, 50);
                score += MatchScore(s.Description, tokens, 20);
                score += MatchScore(s.Category, tokens, 15);
                if (s.Tags != null)
                    foreach (var tag in s.Tags) score += MatchScore(tag, tokens, 25);
                if (score > 0) scored.Add((s, score));
            }

            return scored
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Skill.UsageCount)
                .Take(limit)
                .Select(x => x.Skill)
                .ToList();
        }

        /// <inheritdoc />
        public string BuildSkillInjection(List<Skill> skills)
        {
            if (skills == null || skills.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("## Available Skills (on-demand guidance)");
            sb.AppendLine("The following skills are relevant to this task. Apply their guidance when useful; they are advisory, not mandatory.");
            foreach (var s in skills)
            {
                if (s == null) continue;
                sb.Append("- ").Append(s.Name);
                if (!string.IsNullOrWhiteSpace(s.Category)) sb.Append(" [").Append(s.Category).Append(']');
                if (!string.IsNullOrWhiteSpace(s.Description)) sb.Append(": ").Append(s.Description.Trim());
                sb.AppendLine();

                // Include parameter contract briefly so the agent knows how to invoke it.
                var pars = s.Definition?.Parameters;
                if (pars != null && pars.Count > 0)
                {
                    var parts = pars.Select(p =>
                        p.Name + (p.Required ? "*" : "") + (string.IsNullOrWhiteSpace(p.Type) ? "" : ":" + p.Type));
                    sb.Append("    params: ").AppendLine(string.Join(", ", parts));
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static int MatchScore(string text, List<string> tokens, int weight)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            int score = 0;
            foreach (var t in tokens)
                if (text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) score += weight;
            return score;
        }

        private static List<string> Tokenize(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<string>();
            return query
                .Split(new[] { ' ', '\t', '\n', '\r', ',', ';', '.', '，', '；', '。' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
        }

        #endregion

        #region File storage

        private string GetSkillFilePath(string skillName, string version = null)
        {
            var fileName = string.IsNullOrEmpty(version) 
                ? $"{skillName}.json" 
                : $"{skillName}_v{version}.json";
            return Path.Combine(SkillsDirectory, fileName);
        }

        private void SaveSkill(Skill skill)
        {
            var json = JsonSerializer.Serialize(skill, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(GetSkillFilePath(skill.Name), json);
        }

        private void LoadSkillsIndex()
        {
            try
            {
                if (File.Exists(SkillsIndexFile))
                {
                    var json = File.ReadAllText(SkillsIndexFile);
                    var skills = JsonSerializer.Deserialize<List<Skill>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (skills != null)
                    {
                        foreach (var skill in skills)
                        {
                            _skills[skill.Name.ToLower()] = skill;
                        }
                    }
                }
                else
                {
                    CreateSampleSkills();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading skills index: {ex.Message}");
            }
        }

        private void SaveSkillsIndex()
        {
            var json = JsonSerializer.Serialize(_skills.Values.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(SkillsIndexFile, json);
        }

        private void CreateSampleSkills()
        {
            var sampleSkills = new List<Skill>
            {
                new Skill
                {
                    Name = "WebSearch",
                    Description = "Search the web for information",
                    Category = "Tools",
                    Author = "System",
                    Definition = new SkillDefinition
                    {
                        Parameters = new List<SkillParameter>
                        {
                            new SkillParameter { Name = "query", Type = "string", Description = "Search query", Required = true },
                            new SkillParameter { Name = "maxResults", Type = "int", Description = "Maximum results", Required = false, DefaultValue = 10 }
                        },
                        ExecutionLogic = "web_search"
                    }
                },
                new Skill
                {
                    Name = "FileRead",
                    Description = "Read file content",
                    Category = "FileSystem",
                    Author = "System",
                    Definition = new SkillDefinition
                    {
                        Parameters = new List<SkillParameter>
                        {
                            new SkillParameter { Name = "path", Type = "string", Description = "File path", Required = true }
                        },
                        ExecutionLogic = "file_read"
                    }
                },
                new Skill
                {
                    Name = "Calculator",
                    Description = "Perform mathematical calculations",
                    Category = "Tools",
                    Author = "System",
                    Definition = new SkillDefinition
                    {
                        Parameters = new List<SkillParameter>
                        {
                            new SkillParameter { Name = "expression", Type = "string", Description = "Math expression", Required = true }
                        },
                        ExecutionLogic = "calculator"
                    }
                }
            };

            foreach (var skill in sampleSkills)
            {
                RegisterSkill(skill);
            }
        }

        #endregion
    }
}