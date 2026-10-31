using System;
using System.Threading;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// The id of the agent run that the current call chain belongs to. It flows with async calls (and into
    /// Task.Run), so code far from the chat window, such as the HTTP client that talks to the Python backend,
    /// can send it as the request's <c>rid</c> without every method in between carrying a parameter.
    ///
    /// The same id is used as the user-interaction run id, so one value ties together the C# log, the Python
    /// log (every line carries [rid=...]), the X-Request-Id on tool callbacks, and the approval audit record.
    /// </summary>
    public static class RunContext
    {
        private static readonly AsyncLocal<string> Current = new AsyncLocal<string>();

        /// <summary>The run id of the current call chain, or null outside a run.</summary>
        public static string CurrentRid => Current.Value;

        /// <summary>Make <paramref name="rid"/> the current run id until the returned handle is disposed.</summary>
        public static IDisposable Enter(string rid)
        {
            var previous = Current.Value;
            Current.Value = string.IsNullOrWhiteSpace(rid) ? null : rid.Trim();
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly string _previous;
            private bool _disposed;

            public Scope(string previous) { _previous = previous; }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Current.Value = _previous;
            }
        }
    }
}
