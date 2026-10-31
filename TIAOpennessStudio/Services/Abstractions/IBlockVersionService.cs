using System;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Abstraction for block version management and git integration.
    /// </summary>
    public interface IBlockVersionService
    {
        /// <summary>
        /// Computes a content version hash for the given content.
        /// </summary>
        /// <param name="content">The content to hash.</param>
        /// <returns>A 12-character hex string representing the content hash.</returns>
        string ComputeContentVersion(string content);

        /// <summary>
        /// Updates the block version cache and returns version information.
        /// </summary>
        /// <param name="cacheKey">The cache key for the block.</param>
        /// <param name="xmlContent">The XML content of the block.</param>
        /// <param name="exportFile">The export file path.</param>
        /// <param name="changed">Output: whether the content changed.</param>
        /// <param name="previousVersion">Output: the previous version hash.</param>
        /// <returns>The current version hash.</returns>
        string UpdateBlockVersion(string cacheKey, string xmlContent, string exportFile, out bool changed, out string previousVersion);

        /// <summary>
        /// Gets the project cache key for indexing.
        /// </summary>
        /// <param name="projectPath">Optional project path to use.</param>
        /// <returns>A string identifier for the project.</returns>
        string GetProjectCacheKey(string projectPath = null);

        /// <summary>
        /// Tries to get git snapshot information (branch and commit).
        /// </summary>
        /// <param name="branch">Output: the current git branch.</param>
        /// <param name="commit">Output: the current git commit hash.</param>
        /// <returns>True if git information was successfully retrieved.</returns>
        bool TryGetGitSnapshot(out string branch, out string commit);

        /// <summary>
        /// Applies context budget trimming to a string.
        /// </summary>
        /// <param name="context">The context to trim.</param>
        /// <param name="maxChars">Maximum number of characters.</param>
        /// <returns>The trimmed context.</returns>
        string ApplyContextBudget(string context, int maxChars = 22000);
    }
}