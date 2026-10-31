#if TIA_V18
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Portal;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Registers the Portal class's methods into the ToolRegistry in-process so
    /// the agent can call every TIA capability directly with no HTTP overhead.
    /// Which tools are registered depends on the detected TIA version.
    /// </summary>
    public sealed class InProcessTiaToolProvider
    {
        private readonly Portal _portal;
        private readonly ToolRegistry _toolRegistry;
        private readonly ITiaVersionAdapter _versionAdapter;
        private readonly List<string> _registeredToolNames = new List<string>();
        private bool _isRegistered;

        public InProcessTiaToolProvider(
            Portal portal,
            ToolRegistry toolRegistry,
            ITiaVersionAdapter versionAdapter)
        {
            _portal = portal ?? throw new ArgumentNullException(nameof(portal));
            _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
            _versionAdapter = versionAdapter ?? throw new ArgumentNullException(nameof(versionAdapter));
        }

        /// <summary>Number of registered TIA in-process tools</summary>
        public int RegisteredToolCount => _registeredToolNames.Count;

        /// <summary>Names of the registered tools (read-only copy)</summary>
        public IReadOnlyList<string> RegisteredToolNames => _registeredToolNames.AsReadOnly();

        /// <summary>Whether tools have been registered</summary>
        public bool IsRegistered => _isRegistered;

        /// <summary>
        /// Registers all in-process tools supported by the detected TIA version.
        /// Idempotent: a repeated call unregisters first, then re-registers.
        /// </summary>
        public void RegisterAvailableTools()
        {
            if (_isRegistered)
                UnregisterAll();

            int majorVersion = _versionAdapter.DetectedMajorVersion;
            if (majorVersion <= 0)
            {
                AppLogger.Log("[InProcessTiaToolProvider] TIA version not detected, skipping tool registration.");
                return;
            }

            var staMarshal = BuildStaMarshal();
            var definitions = TiaToolDefinitionRegistry.GetAvailable(majorVersion);
            int count = 0;

            foreach (var def in definitions)
            {
                try
                {
                    var tool = new TiaInProcessTool(def, _portal, staMarshal);
                    _toolRegistry.Register(tool);
                    _registeredToolNames.Add(tool.Name);
                    count++;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[InProcessTiaToolProvider] Failed to register tool '{def.Name}': {ex.Message}");
                }
            }

            _isRegistered = true;
            AppLogger.Log($"[InProcessTiaToolProvider] Registered {count} TIA in-process tools (TIA V{majorVersion}).");
        }

        /// <summary>
        /// Removes all registered in-process TIA tools.
        /// </summary>
        public void UnregisterAll()
        {
            foreach (var name in _registeredToolNames)
            {
                try { _toolRegistry.Unregister(name); }
                catch { /* best effort */ }
            }
            _registeredToolNames.Clear();
            _isRegistered = false;
            AppLogger.Log("[InProcessTiaToolProvider] All TIA in-process tools unregistered.");
        }

        /// <summary>
        /// Refreshes by unregistering and re-registering (used when TIA
        /// reconnects or the version changes).
        /// </summary>
        public void Refresh()
        {
            UnregisterAll();
            RegisterAvailableTools();
        }

        /// <summary>
        /// Synchronizes the Portal connection state, called after the main app
        /// connects to TIA. The Portal class needs the current TiaPortal and
        /// Project references to operate.
        /// </summary>
        public void SyncPortalState(object tiaPortal, object project)
        {
            // Portal's ConnectPortal() already ran in the main app; here we only
            // need its project reference to be in sync. Portal tracks state in its
            // _portal / _project fields (usually set by ConnectPortal / OpenProject
            // / AttachToOpenProject). We trust Portal's own connection management.
            AppLogger.Log($"[InProcessTiaToolProvider] SyncPortalState called. Portal.ProjectIsValid={_portal.ProjectIsValid}");
        }

        /// <summary>
        /// Builds the STA marshal function, routing work through TiaStaHost so all
        /// TIA COM calls run on the dedicated Openness STA thread instead of the WPF
        /// UI thread.
        /// </summary>
        private static Func<Func<object>, Task<object>> BuildStaMarshal()
        {
            return (work) => TiaStaHost.Instance.RunAsync(work);
        }
    }
}
#endif
