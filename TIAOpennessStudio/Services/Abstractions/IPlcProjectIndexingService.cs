using System;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Rebuilds the PLC project index.
    /// </summary>
    public interface IPlcProjectIndexingService
    {
        Task<PlcProjectIndexBuildResult> RebuildAsync(bool force = false, Action<string> onProgress = null, CancellationToken ct = default);
    }
}
