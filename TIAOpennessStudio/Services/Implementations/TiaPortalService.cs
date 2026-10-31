#if TIA_V18
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Portal;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>Facade over the Siemens Openness Portal for in-process TIA operations.</summary>
    public class TiaPortalService : ITiaPortalService
    {
        private readonly Portal _portal;

        public TiaPortalService()
        {
            _portal = new Portal();
        }

        public bool IsConnected => _portal.ProjectIsValid;
        public bool IsProjectOpen => _portal.ProjectIsValid;
        public TiaPortal Portal => _portal.PortalInstance;
        public string LastConnectError => _portal.LastConnectError;

        public bool ConnectPortal() => _portal.ConnectPortal();
        public bool DisconnectPortal() => _portal.DisconnectPortal();
        public void Dispose() => _portal.Dispose();

        public bool OpenProject(string projectPath) => _portal.OpenProject(projectPath);
        public bool CreateProject(string directoryPath, string projectName) => _portal.CreateProject(directoryPath, projectName);
        public bool SaveProject() => _portal.SaveProject();
        public bool CloseProject() => _portal.CloseProject();
        public string GetProjectTree() => _portal.GetProjectTree();

        public Device AddDevice(string orderNumber, string version, string deviceName) => _portal.AddDevice(orderNumber, version, deviceName);
        public bool ImportBlock(string softwarePath, string groupPath, string importPath) => _portal.ImportBlock(softwarePath, groupPath, importPath);
        public PlcBlock ExportBlock(string softwarePath, string blockPath, string exportPath) => _portal.ExportBlock(softwarePath, blockPath, exportPath);
        public CompilerResult CompileSoftware(string softwarePath, string password = "") => _portal.CompileSoftware(softwarePath, password);
    }
}
#endif