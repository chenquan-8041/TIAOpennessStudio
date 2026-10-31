using System.Threading.Tasks;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// TIA Portal tool service: wraps the offline builders and TIA import capability for the AI agent.
    /// </summary>
    public interface ITiaToolService
    {
        // ===== Offline building (no TIA connection; returns XML strings) =====

        /// <summary>JSON → UDT XML.</summary>
        string BuildUdtXml(string json);

        /// <summary>JSON → tag table XML.</summary>
        string BuildTagTableXml(string json);

        /// <summary>JSON → global DB XML.</summary>
        string BuildGlobalDbXml(string json);

        /// <summary>JSON → SCL FC block XML.</summary>
        string BuildFcBlockXml(string json);

        /// <summary>JSON → SCL FB block XML.</summary>
        string BuildFbBlockXml(string json);

        /// <summary>JSON → LAD FC block XML (FlgNet networks).</summary>
        string BuildLadFcBlockXml(string json);

        /// <summary>JSON → LAD FB block XML (FlgNet networks + Static section).</summary>
        string BuildLadFbBlockXml(string json);

        /// <summary>JSON → S7-GRAPH FB block XML (steps / transitions / branches / connections).</summary>
        string BuildGraphFbBlockXml(string json);

        /// <summary>JSON → FlgNet call-network XML.</summary>
        string BuildFlgNetCallXml(string json);

        /// <summary>JSON → SCL structured text XML fragment.</summary>
        string BuildStructuredTextXml(string json);

        // ===== Build + import (connects to TIA) =====

        /// <summary>
        /// Builds XML and imports it into TIA Portal.
        /// </summary>
        /// <param name="softwarePath">PLC software path, e.g. "PLC_1".</param>
        /// <param name="kind">Block kind: udt|tagtable|globaldb|fc|fb|ladfc|ladfb|graphfb.</param>
        /// <param name="json">JSON specification string.</param>
        /// <param name="groupPath">Target group path for import; empty string means the root group.</param>
        /// <returns>Import result message.</returns>
        Task<string> BuildAndImportAsync(string softwarePath, string kind, string json, string groupPath = "");

        /// <summary>
        /// Imports ready-made XML/SCL code directly into TIA Portal.
        /// </summary>
        Task<string> ImportBlockCodeAsync(string blockName, string blockType, string code, string groupPath = "");

        // ===== Project scaffolding =====

        /// <summary>
        /// Generates a complete project (UDT + DB + FC + FB + TagTable) from a JSON specification in one pass.
        /// </summary>
        /// <param name="specJson">Project specification JSON.</param>
        /// <param name="dryRun">true = validate only, do not import.</param>
        /// <returns>Execution result.</returns>
        Task<string> ScaffoldProjectAsync(string specJson, bool dryRun = false);
    }
}
