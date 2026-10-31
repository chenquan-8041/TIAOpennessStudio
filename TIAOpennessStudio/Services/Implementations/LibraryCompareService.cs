using System.Collections.Generic;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>Wraps LibraryCompareEngine as ILibraryCompareService, relaying progress updates.</summary>
    public sealed class LibraryCompareService : ILibraryCompareService
    {
        private readonly IOpenness _openness;

        public event ProgressUpdate ProgressUpdated;

        public LibraryCompareService()
        {
            _openness = new LibraryCompareEngine();
            _openness.OnProgressUpdate += HandleProgressUpdate;
        }

        public CompareResults Compare(string leftPath, string rightPath) => _openness.Compare(leftPath, rightPath);

        public void DetailCompare(
            string leftPath,
            string rightPath,
            IEnumerable<ILibraryTypeModel> leftModels,
            IEnumerable<ILibraryTypeModel> rightModels)
        {
            _openness.DetailCompare(leftPath, rightPath, leftModels, rightModels);
        }

        private void HandleProgressUpdate(int percent, object userStatus)
        {
            ProgressUpdated?.Invoke(percent, userStatus);
        }
    }
}
