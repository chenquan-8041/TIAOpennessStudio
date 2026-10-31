using System.Collections.Generic;
using TIAOpennessStudio.Services.Implementations;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Compares two library archives and reports type-level differences, with progress
    /// notifications for long-running comparisons.
    /// </summary>
    public interface ILibraryCompareService
    {
        event ProgressUpdate ProgressUpdated;

        CompareResults Compare(string leftPath, string rightPath);

        void DetailCompare(
            string leftPath,
            string rightPath,
            IEnumerable<ILibraryTypeModel> leftModels,
            IEnumerable<ILibraryTypeModel> rightModels);
    }
}
