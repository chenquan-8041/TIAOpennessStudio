#if TIA_V21
using Siemens.Collaboration.Net;
using System.Threading.Tasks;

namespace TIAOpennessStudio.Services.Portal
{
    /// <summary>Bootstraps the Siemens.Collaboration.Net Openness API for a given TIA Portal version.</summary>
    public static class Openness
    {
        public static int TiaMajorVersion { get; private set; }

        public static void Initialize(int? tiaMajorVersion = 21)
        {
            // with nuget packages:
            // 2.1 nuget package: Siemens.Collaboration.Net.TiaPortal.Openness.Resolver
            //     & User Environment Variable: TiaPortalLocation=C:\Program Files\Siemens\Automation\Portal V20
            // 2.2 nuget package: Siemens.Collaboration.Net.TiaPortal.Packages.Openness
            // 2.3 Api.Global.Openness().Initialize(tiaMajorVersion: 20); // fixed version 20

            TiaMajorVersion = tiaMajorVersion ?? 21; // Default to TIA Portal V21 if not specified

            // Initialize the Openness API with the specified TIA Portal major version
            Api.Global.Openness().Initialize(tiaMajorVersion: tiaMajorVersion);
        }

        public static async Task<bool> IsUserInGroup()
        {
            if (Api.Global.Openness().IsUserInGroup())
            {
                // user is in group
                return true;
            }
            else
            {
                return await Api.Global.Openness().AddUserToGroupAsync();
            }
        }
    }
}

#endif