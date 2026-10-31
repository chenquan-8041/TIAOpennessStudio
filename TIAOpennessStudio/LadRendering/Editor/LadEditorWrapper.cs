using System;
using System.Windows;
using System.Windows.Controls;
using Prism.Ioc;
using TIAOpennessStudio;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.LadRendering.Rendering;

namespace TIAOpennessStudio.LadRendering.Editor
{
    /// <summary>
    /// LAD editor wrapper (read-only rendering).
    /// </summary>
    public class LadEditorWrapper
    {
        private readonly GraphSnapshot _snapshot;
        private readonly ITiaOpennessService _tiaService;
        private readonly string _blockName;
        private readonly string _blockType;
        private readonly int _networkIndex;

        private UIElement _renderedView;

        public LadEditorWrapper(
            GraphSnapshot snapshot,
            ITiaOpennessService tiaService,
            string blockName,
            string blockType,
            int networkIndex)
        {
            _snapshot = snapshot;
            _tiaService = tiaService;
            _blockName = blockName;
            _blockType = blockType;
            _networkIndex = networkIndex;
        }

        /// <summary>
        /// Renders the LAD view (read-only mode)
        /// </summary>
        public UIElement Render()
        {
            LadWpfRenderer.CrossReferenceService = _tiaService;
            LadWpfRenderer.CurrentBlockName = _blockName;

            // Set the Worker host service (phase-3 cross-reference TIA supplement runs in the child process, without blocking the UI thread)
            try
            {
                LadWpfRenderer.WorkerHostService =
                    Prism.Ioc.ContainerLocator.Container?.Resolve<IProjectKnowledgeWorkerHostService>();
            }
            catch { /* Fall back to an in-process query when the Worker service is unavailable */ }

            // Set the offline index service (phase-2 cross-reference query)
            try
            {
                var indexStore = Prism.Ioc.ContainerLocator.Container?.Resolve<ProjectIndexService>();
                if (indexStore != null)
                {
                    LadWpfRenderer.OfflineIndexStore = indexStore;
                    // Get the current project key from the TIA service
                    var projectPath = _tiaService?.Project?.Path?.FullName;
                    if (!string.IsNullOrWhiteSpace(projectPath))
                    {
                        var versionSvc = Prism.Ioc.ContainerLocator.Container?.Resolve<IBlockVersionService>();
                        LadWpfRenderer.CurrentProjectKey = versionSvc?.GetProjectCacheKey(projectPath) ?? projectPath;
                    }
                }
            }
            catch { /* Ignore when DI is unavailable; phase 2 will be skipped */ }

            _renderedView = LadWpfRenderer.Render(_snapshot);
            return _renderedView;
        }
    }
}