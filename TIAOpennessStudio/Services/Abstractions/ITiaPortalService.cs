#if TIA_V18
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Wraps the TIA Portal API (connection, project, device, block, and compile) for TIA V21.
    /// </summary>
    public interface ITiaPortalService
    {
        bool IsConnected { get; }
        bool IsProjectOpen { get; }
        TiaPortal Portal { get; }
        string LastConnectError { get; }

        bool ConnectPortal();
        bool DisconnectPortal();
        void Dispose();

        bool OpenProject(string projectPath);
        bool CreateProject(string directoryPath, string projectName);
        bool SaveProject();
        bool CloseProject();
        string GetProjectTree();

        Device AddDevice(string orderNumber, string version, string deviceName);
        bool ImportBlock(string softwarePath, string groupPath, string importPath);
        PlcBlock ExportBlock(string softwarePath, string blockPath, string exportPath);
        CompilerResult CompileSoftware(string softwarePath, string password = "");
    }
}
#endif