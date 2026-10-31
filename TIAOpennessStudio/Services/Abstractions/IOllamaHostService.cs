using System.Collections.Generic;
using System.Threading.Tasks;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Ollama local model host service interface.
    /// </summary>
    public interface IOllamaHostService
    {
        /// <summary>Checks whether the Ollama service is healthy.</summary>
        Task<bool> IsHealthyAsync();

        /// <summary>Ensures the Ollama service is started.</summary>
        Task<bool> EnsureStartedAsync();

        /// <summary>Gets the list of installed models.</summary>
        Task<List<string>> GetInstalledModelsAsync();

        /// <summary>Checks whether the specified model is downloaded.</summary>
        Task<bool> IsModelAvailableAsync(string model);

        /// <summary>Downloads the specified model.</summary>
        Task<bool> PullModelAsync(string model);

        /// <summary>Stops the owned Ollama process.</summary>
        void StopOwnedProcess();

        /// <summary>Gets the model's disk size (bytes).</summary>
        Task<long> GetModelSizeAsync(string model);

        /// <summary>Auto-selects the best installed model for the current available memory.</summary>
        Task<string> PickBestModelAsync(long availableMemoryBytes);

        /// <summary>Deletes an installed model.</summary>
        Task<bool> DeleteModelAsync(string model);

        /// <summary>Gets detailed model info (parameter count, quantization, etc.).</summary>
        Task<string> GetModelDetailsAsync(string model);
    }
}
