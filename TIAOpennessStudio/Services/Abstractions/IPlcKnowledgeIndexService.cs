using System.Collections.Generic;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Searches the PLC knowledge index and builds context packs and variable references.
    /// </summary>
    public interface IPlcKnowledgeIndexService
    {
        List<PlcKnowledgeSearchHit> Search(string projectKey, string query, int limit = 5);
        ContextPack BuildContextPack(string projectKey, string query, string rid = null, int topK = 5);
        PlcVariableReferenceResult QueryVariableReferences(string projectKey, string variablePath, string direction = "both", int topK = 20, bool includeDefinitions = true);
    }
}
