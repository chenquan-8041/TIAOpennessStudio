using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace TIAOpennessStudio.Services
{
    public class TiaOpennessWhitelistService
    {
        /// <summary>
        /// Checks whether the current process is running with Administrator privileges.
        /// </summary>
        public static bool IsRunningAsAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        /// <summary>
        /// Parses a registry sub-key name as a TIA Openness version. Accepts both plain numeric
        /// keys ("15.1", "18.0") and V-prefixed keys ("V15.1") emitted by the Siemens TIA V-series
        /// installers. Returns false for non-version keys such as "AllowList" or "All".
        /// </summary>
        private static bool TryParseTiaVersion(string key, out double versionNum)
        {
            versionNum = 0;
            if (string.IsNullOrWhiteSpace(key)) return false;

            string normalized = key;
            if (normalized.Length > 1 && (normalized[0] == 'V' || normalized[0] == 'v'))
                normalized = normalized.Substring(1);

            return double.TryParse(normalized, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out versionNum);
        }

        /// <summary>
        /// Returns the installed TIA Portal Openness versions found in the registry (e.g. "17.0", "18.0", "21.0").
        /// </summary>
        public static System.Collections.Generic.List<string> GetInstalledTiaVersions()
        {
            var versions = new System.Collections.Generic.List<string>();
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var opennessKey = baseKey.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness"))
                {
                    if (opennessKey != null)
                    {
                        foreach (string version in opennessKey.GetSubKeyNames())
                        {
                            if (TryParseTiaVersion(version, out _))
                            {
                                versions.Add(version);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Return whatever was detected so far
            }
            return versions;
        }

        /// <summary>
        /// Automatically registers the current executable for all installed TIA Portal Openness versions.
        /// </summary>
        public static void AutoRegisterIfAdmin()
        {
            if (!IsRunningAsAdmin()) return;

            try
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var opennessKey = baseKey.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness"))
                {
                    if (opennessKey != null)
                    {
                        foreach (string version in opennessKey.GetSubKeyNames())
                        {
                            if (TryParseTiaVersion(version, out _))
                            {
                                WhitelistTiaOpennessApp(exePath, version);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Silently ignore if auto-register fails
            }
        }

        /// <summary>
        /// Adds an external executable to the Siemens TIA Portal Openness whitelist by writing
        /// the application path, last-modified date, and SHA-256 file hash to the Windows registry.
        /// </summary>
        /// <param name="executablePath">The absolute path to the .exe file to whitelist.</param>
        /// <param name="tiaVersion">
        /// The TIA Portal version number (e.g., "18.0", "21.0").
        /// Versions 21.0 and newer use a version-agnostic registry path under AllowList;
        /// older versions use a versioned path under Whitelist.
        /// </param>
        /// <returns>
        /// A string describing the result: a success message, or an error message if the file was
        /// not found, access was denied (requires Administrator), or another exception occurred.
        /// </returns>
        public static string WhitelistTiaOpennessApp(string executablePath, string tiaVersion)
        {
            if (!File.Exists(executablePath)) return $"Error: File '{executablePath}' not found.";

            try
            {
                var fileInfo = new FileInfo(executablePath);
                string dateModified = fileInfo.LastWriteTimeUtc.ToString("yyyy/MM/dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                
                string fileHash;
                using (var stream = File.OpenRead(executablePath))
                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(stream);
                    fileHash = Convert.ToBase64String(hashBytes);
                }

                string appName = fileInfo.Name;
                
                string registryPath;
                if (TryParseTiaVersion(tiaVersion, out double versionNum) && versionNum >= 21.0)
                {
                    registryPath = $@"SOFTWARE\Siemens\Automation\Openness\AllowList\{appName}\Entry";
                }
                else
                {
                    registryPath = $@"SOFTWARE\Siemens\Automation\Openness\{tiaVersion}\Whitelist\{appName}\Entry";
                }
                
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = baseKey.CreateSubKey(registryPath))
                {
                    if (key == null)
                        throw new UnauthorizedAccessException("Failed to open registry key.");

                    key.SetValue("Path", fileInfo.FullName, RegistryValueKind.String);
                    key.SetValue("DateModified", dateModified, RegistryValueKind.String);
                    key.SetValue("FileHash", fileHash, RegistryValueKind.String);
                }

                return $"Success: '{appName}' added to the TIA Portal Openness whitelist (Version {tiaVersion}).";
            }
            catch (UnauthorizedAccessException)
            {
                return "Error: Access Denied. You must run this application as an Administrator to modify the whitelist.";
            }
            catch (Exception ex)
            {
                return $"Fatal Error: {ex.Message}";
            }
        }
    }
}
