using System.Threading.Tasks;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Background daemon that warms the project knowledge cache.
    /// </summary>
    public interface IProjectKnowledgeCacheDaemonService
    {
        void Start();
        Task TriggerWarmupAsync(string reason = null);
    }
}
