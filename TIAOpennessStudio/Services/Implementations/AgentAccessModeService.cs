using System;
using System.Collections.Generic;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <inheritdoc />
    public sealed class AgentAccessModeService : IAgentAccessMode
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, AgentAccessMode> _runs =
            new Dictionary<string, AgentAccessMode>(StringComparer.OrdinalIgnoreCase);
        private AgentAccessMode _current = AgentAccessMode.ReadOnly;

        public event Action<AgentAccessMode> Changed;

        public AgentAccessMode Current
        {
            get { lock (_gate) { return _current; } }
            set
            {
                bool changed;
                lock (_gate)
                {
                    changed = _current != value;
                    _current = value;
                }
                if (changed) Changed?.Invoke(value);
            }
        }

        public void BindRun(string runId, AgentAccessMode mode)
        {
            if (string.IsNullOrWhiteSpace(runId)) return;
            lock (_gate) { _runs[runId.Trim()] = mode; }
        }

        public void ReleaseRun(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return;
            lock (_gate) { _runs.Remove(runId.Trim()); }
        }

        public AgentAccessMode ModeFor(string rid)
        {
            if (string.IsNullOrWhiteSpace(rid)) return AgentAccessMode.ReadOnly;
            lock (_gate)
            {
                return _runs.TryGetValue(rid.Trim(), out var mode) ? mode : AgentAccessMode.ReadOnly;
            }
        }

        public void ElevateRun(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return;
            lock (_gate)
            {
                // Only a run we know about can be raised; an unknown id stays read-only.
                if (_runs.ContainsKey(runId.Trim())) _runs[runId.Trim()] = AgentAccessMode.AllowEdit;
            }
        }
    }
}
