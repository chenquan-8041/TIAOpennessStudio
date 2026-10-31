using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Detects the installed TIA Portal version and installation path, and
    /// exposes feature-availability flags derived from the detected version.
    /// </summary>
    public class TiaVersionAdapter : ITiaVersionAdapter
    {
        private int _majorVersion;
        private string _installPath = "";

        public int DetectedMajorVersion => _majorVersion;

        // Static mirror for the V21 Portal bridge (composed without an injected ITiaVersionAdapter).
        public static int CurrentMajorVersion { get; set; }

        // Full version string resolved at startup (e.g. "15.1"); mirrors Program.Version for callers that only touch the adapter.
        public static string CurrentVersion { get; set; }

        // Engineering version header for generated XML ("V15.1", "V16", ...): single source of truth for the
        // block/DB/UDT/tag-table builders, replacing the previously scattered hard-coded "V21".
        public static string CurrentEngineeringVersion
        {
            get
            {
                var v = CurrentVersion;
                if (string.IsNullOrWhiteSpace(v)) return "V18";
                if (!v.StartsWith("V", StringComparison.OrdinalIgnoreCase)) v = "V" + v;
                // Majors render without the trailing ".0" ("V18", not "V18.0").
                if (v.EndsWith(".0")) v = v.Substring(0, v.Length - 2);
                return v;
            }
        }

        public string InstallPath => _installPath;

        public bool SupportsMultiuser => _majorVersion >= 16;
        public bool SupportsSafety => _majorVersion >= 16;
        public bool SupportsOnlineConfigurations => _majorVersion >= 17;
        public bool SupportsDownloadConfigurations => _majorVersion >= 17;
        public bool SupportsAlarm => _majorVersion >= 18;
        public bool SupportsOpcUa => _majorVersion >= 18;
        public bool SupportsHmiUnified => _majorVersion >= 18;
        public bool SupportsHmi => _majorVersion >= 18;
        public bool UsesNuGetPackages => _majorVersion >= 20;

        public void DetectAndInitialize()
        {
            // Single source of installed version + path: TiaApiPathResolver (string keys,
            // correctly distinguishes V15 vs V15.1). Replaces the old int-based detection.
            var resolution = new TiaApiPathResolver().Resolve(AppDomain.CurrentDomain.BaseDirectory);
            _majorVersion = ToMajorVersion(resolution.Version);
            _installPath = resolution.ApiPath ?? "";
            CurrentMajorVersion = _majorVersion;
            CurrentVersion = resolution.Version ?? "";

            // Warm up the Openness capability probe in the background (pure reflection:
            // non-blocking and no STA involvement). Results are cached for use by
            // IsFeatureAvailable and the built-in MCP probe tools.
            try { _ = OpennessCapabilityProbe.GetCapabilitiesAsync(); }
            catch { /* Probe warm-up failure must not affect version detection */ }
        }

        private static int ToMajorVersion(string version)
        {
            if (!string.IsNullOrWhiteSpace(version) && int.TryParse(version.Split('.')[0], out var m))
                return m;
            return 0;
        }

        /// <summary>Maps a feature name to the type-name fragment used for real probing (probe first, fall back to version threshold).</summary>
        private static string MapFeatureToTypeHint(string featureName)
        {
            switch (featureName)
            {
                case "Multiuser": return "Siemens.Engineering.Multiuser";
                case "Safety": return "Siemens.Engineering.Safety";
                case "OnlineConfigurations": return "Siemens.Engineering.Online.Configurations";
                case "DownloadConfigurations": return "Siemens.Engineering.Download.Configurations";
                case "Alarm": return "Siemens.Engineering.SW.Alarm";
                case "OpcUa": return "Siemens.Engineering.SW.OpcUa";
                case "HmiUnified": return "Siemens.Engineering.HmiUnified";
                case "Hmi": return "Siemens.Engineering.Hmi";
                default: return null;
            }
        }

        public List<string> GetAvailableFeatures()
        {
            var features = new List<string> { "BasicPLC", "BasicHMI" };
            if (SupportsMultiuser) features.Add("Multiuser");
            if (SupportsSafety) features.Add("Safety");
            if (SupportsOnlineConfigurations) features.Add("OnlineConfigurations");
            if (SupportsDownloadConfigurations) features.Add("DownloadConfigurations");
            if (SupportsAlarm) features.Add("Alarm");
            if (SupportsOpcUa) features.Add("OpcUa");
            if (SupportsHmiUnified) features.Add("HmiUnified");
            if (SupportsHmi) features.Add("Hmi");
            if (UsesNuGetPackages) features.Add("NuGetPackages");
            return features;
        }

        public bool IsFeatureAvailable(string featureName)
        {
            // Prefer the real reflection probe result when it can give a verdict.
            var typeHint = MapFeatureToTypeHint(featureName);
            if (typeHint != null)
            {
                var probed = OpennessCapabilityProbe.IsTypeAvailable(typeHint);
                if (probed.HasValue) return probed.Value;
                // Probe not ready (null) -> fall back to the version threshold.
            }

            switch (featureName)
            {
                case "Multiuser": return SupportsMultiuser;
                case "Safety": return SupportsSafety;
                case "OnlineConfigurations": return SupportsOnlineConfigurations;
                case "DownloadConfigurations": return SupportsDownloadConfigurations;
                case "Alarm": return SupportsAlarm;
                case "OpcUa": return SupportsOpcUa;
                case "HmiUnified": return SupportsHmiUnified;
                case "Hmi": return SupportsHmi;
                case "NuGetPackages": return UsesNuGetPackages;
                default: return true;
            }
        }
    }
}
