using System.Collections.Generic;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Validates code and patches against style and quality constraints.
    /// </summary>
    public interface IQualityGateService
    {
        QualityGateResult ValidateCode(string code, string fileName = "", List<QualityConstraint> customConstraints = null);
        QualityGateResult ValidatePatch(FilePatch patch, List<QualityConstraint> customConstraints = null);
        QualityGateResult ValidateManifest(ChangeManifest manifest, List<QualityConstraint> customConstraints = null);
        string EnforceStyle(string code, string fileName = "");
        List<QualityConstraint> GetDefaultConstraints();
        List<StylePattern> GetStylePatterns();
    }
}
