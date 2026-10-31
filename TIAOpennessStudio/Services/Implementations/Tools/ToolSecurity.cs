using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TIAOpennessStudio.Services.Implementations.Tools
{
    /// <summary>
    /// Central safety-policy hub for high-risk tools such as file_write and
    /// execute_command: applies a path sandbox and a command blacklist. Keeping
    /// the checks in one place makes them easy to audit and tighten uniformly.
    /// </summary>
    public static class ToolSecurity
    {
        /// <summary>
        /// Whitelist of roots (absolute, normalized) that may be written to.
        /// Seeded with the application working directory and the temp directory,
        /// and extensible at runtime via AddAllowedRoot (e.g. the open project dir).
        /// </summary>
        private static readonly HashSet<string> _allowedWriteRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static ToolSecurity()
        {
            try { AddAllowedRoot(AppDomain.CurrentDomain.BaseDirectory); } catch { }
            try { AddAllowedRoot(Path.GetTempPath()); } catch { }
            try { AddAllowedRoot(Environment.CurrentDirectory); } catch { }
        }

        /// <summary>Adds a root directory that may be written to (e.g. the open project root or a generated-code output directory).</summary>
        public static void AddAllowedRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            try
            {
                var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrEmpty(full)) _allowedWriteRoots.Add(full);
            }
            catch { /* Ignore invalid paths */ }
        }

        /// <summary>
        /// Validates a write path. Returns true when it is allowed; otherwise
        /// <paramref name="reason"/> explains the rejection. The path must be
        /// normalizable and fall inside a whitelisted root (blocking traversal
        /// and writes to system directories).
        /// </summary>
        public static bool IsWritePathAllowed(string path, out string fullPath, out string reason)
        {
            fullPath = null;
            reason = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                reason = "path is empty";
                return false;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path); // Normalize, resolving ".." traversal
            }
            catch (Exception ex)
            {
                reason = "invalid path: " + ex.Message;
                return false;
            }
            fullPath = full;

            // Reject writes into obviously sensitive system locations even if they happen to sit under a broad root.
            if (IsSensitiveSystemPath(full))
            {
                reason = "target is a protected system location";
                return false;
            }

            foreach (var root in _allowedWriteRoots)
            {
                if (IsUnderRoot(full, root)) return true;
            }

            reason = "path is outside the allowed working directories. Allowed roots: "
                     + string.Join("; ", _allowedWriteRoots);
            return false;
        }

        private static bool IsUnderRoot(string fullPath, string root)
        {
            // Compare with a trailing separator so C:\app never matches C:\app-evil.
            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? root
                : root + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSensitiveSystemPath(string fullPath)
        {
            string[] sensitive =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (var s in sensitive)
            {
                if (string.IsNullOrEmpty(s)) continue;
                if (IsUnderRoot(fullPath, s)) return true;
            }
            return false;
        }

        // ── Command blacklist ─────────────────────────────────────────

        /// <summary>Destructive / high-risk command keywords (case-insensitive, matched by token).</summary>
        private static readonly string[] _blockedCommandTokens =
        {
            "format", "del", "erase", "rd", "rmdir", "rm",
            "shutdown", "reg", "diskpart", "cipher", "takeown",
            "icacls", "attrib", "sc", "net", "bcdedit", "vssadmin",
            "mkfs", "fdisk", "dd"
        };

        /// <summary>
        /// Validates a shell command. Returns true when it is allowed; otherwise
        /// <paramref name="reason"/> explains why. Conservative policy: reject if
        /// the command's first token (the executable name) is blacklisted, and also
        /// scan for blacklisted commands anywhere in a chain/pipeline so tricks like
        /// "echo x &amp; del y" cannot slip through.
        /// </summary>
        public static bool IsCommandAllowed(string command, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(command))
            {
                reason = "command is empty";
                return false;
            }

            // Split into sub-commands on common separators and inspect each first token.
            var separators = new[] { "&&", "&", "|", ";", "\n", "\r" };
            var segments = command.Split(separators, StringSplitOptions.RemoveEmptyEntries);

            foreach (var seg in segments)
            {
                var trimmed = seg.Trim();
                if (trimmed.Length == 0) continue;

                var firstToken = trimmed
                    .Split(new[] { ' ', '\t', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";

                // Strip the executable extension (del.exe -> del)
                var exe = firstToken;
                var dot = exe.IndexOf('.');
                if (dot > 0) exe = exe.Substring(0, dot);

                if (_blockedCommandTokens.Any(b => string.Equals(b, exe, StringComparison.OrdinalIgnoreCase)))
                {
                    reason = $"command '{exe}' is blocked by safety policy (destructive/system-altering).";
                    return false;
                }
            }

            return true;
        }
    }
}
