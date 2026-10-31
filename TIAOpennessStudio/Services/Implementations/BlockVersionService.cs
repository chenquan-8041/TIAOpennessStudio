using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Implementation of IBlockVersionService for block version management and git integration.
    /// </summary>
    public class BlockVersionService : IBlockVersionService
    {
        private sealed class BlockVersionInfo
        {
            public string Version { get; set; }
            public DateTime LastSeen { get; set; }
            public string LastExportFile { get; set; }
        }

        private readonly Dictionary<string, BlockVersionInfo> _blockVersionCache = 
            new Dictionary<string, BlockVersionInfo>(StringComparer.OrdinalIgnoreCase);

        public string ComputeContentVersion(string content)
        {
            var text = content ?? "";
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 12);
            }
        }

        public string UpdateBlockVersion(string cacheKey, string xmlContent, string exportFile, out bool changed, out string previousVersion)
        {
            var version = ComputeContentVersion(xmlContent);
            previousVersion = "";
            changed = true;
            if (_blockVersionCache.TryGetValue(cacheKey, out var existing))
            {
                previousVersion = existing.Version ?? "";
                changed = !string.Equals(previousVersion, version, StringComparison.OrdinalIgnoreCase);
                existing.Version = version;
                existing.LastSeen = DateTime.Now;
                existing.LastExportFile = exportFile;
            }
            else
            {
                _blockVersionCache[cacheKey] = new BlockVersionInfo
                {
                    Version = version,
                    LastSeen = DateTime.Now,
                    LastExportFile = exportFile
                };
            }
            return version;
        }

        public string GetProjectCacheKey(string projectPath = null)
        {
            if (!string.IsNullOrWhiteSpace(projectPath))
                return projectPath;
            
            return "default-project";
        }

        public bool TryGetGitSnapshot(out string branch, out string commit)
        {
            branch = "";
            commit = "";
            try
            {
                string workDir = FindGitWorkingDirectory();
                if (string.IsNullOrWhiteSpace(workDir)) return false;
                branch = RunGit(workDir, "rev-parse --abbrev-ref HEAD");
                commit = RunGit(workDir, "rev-parse --short HEAD");
                return !string.IsNullOrWhiteSpace(commit);
            }
            catch
            {
                return false;
            }
        }

        public string ApplyContextBudget(string context, int maxChars = 22000)
        {
            var text = context ?? "";
            if (text.Length <= maxChars) return text;
            return text.Substring(0, maxChars) + "\n...[context trimmed]";
        }

        private string FindGitWorkingDirectory()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var gitDir = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(gitDir)) return dir.FullName;
                dir = dir.Parent;
            }
            return "";
        }

        private static string RunGit(string workDir, string args)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                if (p == null) return "";
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(1200);
                if (p.ExitCode != 0) return "";
                return (output ?? "").Trim();
            }
        }
    }
}