using System;
using System.Collections.Generic;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Builds and applies code patches and manifests with rollback, conflict detection,
    /// and risk assessment.
    /// </summary>
    public interface IPatchService
    {
        CodePatch GeneratePatch(string original, string modified, string fileName = "");
        FilePatch GenerateFilePatch(string filePath, string blockName, string blockType, string original, string modified);
        ChangeManifest CreateManifest(string rid, string purpose, List<FilePatch> files, List<string> constraints = null);
        string GenerateUnifiedDiff(string original, string modified, string fileName, int contextLines = 3);
        PatchApplicationResult ApplyManifest(ChangeManifest manifest, ITiaOpennessService tiaService, Action<string> onProgress = null);
        PatchApplicationResult RollbackManifest(ChangeManifest manifest, ITiaOpennessService tiaService);
        List<ConflictInfo> DetectConflicts(ChangeManifest manifest, ITiaOpennessService tiaService);
        PatchRisk EvaluateRisk(FilePatch patch);
    }
}
