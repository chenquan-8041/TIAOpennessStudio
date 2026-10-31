using System;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Service for reporting global progress to the StatusBar UI.
    /// Any background operation (rebuild, import, compile) can use this
    /// to show a non-blocking progress indicator.
    /// </summary>
    public interface IGlobalProgressService
    {
        /// <summary>Show indeterminate progress with a message.</summary>
        void Show(string message);

        /// <summary>Show determinate progress with percentage.</summary>
        void Show(string message, double percent);

        /// <summary>Show progress with phase label (for multi-phase operations).</summary>
        void Show(string message, double percent, string phase);

        /// <summary>Hide the progress indicator.</summary>
        void Hide();

        /// <summary>
        /// Creates an Action&lt;string&gt; callback suitable for passing to services
        /// that accept onProgress delegates (like RebuildAsync).
        /// The callback will forward messages to the global progress indicator.
        /// </summary>
        Action<string> CreateProgressCallback(string phase = null);

        /// <summary>
        /// Creates an Action&lt;string&gt; callback that also computes percentage
        /// based on expected total work items.
        /// </summary>
        Action<string> CreateProgressCallback(int expectedTotal, string phase = null);
    }
}
