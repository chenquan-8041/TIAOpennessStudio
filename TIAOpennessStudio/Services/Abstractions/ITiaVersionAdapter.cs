using System.Collections.Generic;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Adapters feature detection for the installed TIA Portal version.
    /// </summary>
    public interface ITiaVersionAdapter
    {
        int DetectedMajorVersion { get; }
        string InstallPath { get; }
        bool SupportsMultiuser { get; }
        bool SupportsSafety { get; }
        bool SupportsOnlineConfigurations { get; }
        bool SupportsDownloadConfigurations { get; }
        bool SupportsAlarm { get; }
        bool SupportsOpcUa { get; }
        bool SupportsHmiUnified { get; }
        bool SupportsHmi { get; }
        bool UsesNuGetPackages { get; }

        void DetectAndInitialize();
        List<string> GetAvailableFeatures();
        bool IsFeatureAvailable(string featureName);
    }
}