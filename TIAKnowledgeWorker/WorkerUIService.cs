using System;
using TIAOpennessStudio;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAKnowledgeWorker
{
    /// <summary>
    /// No-op IUIService for the Worker process.
    /// All notifications are logged instead of shown in UI.
    /// </summary>
    internal sealed class WorkerUIService : IUIService
    {
        public void ShowOverlay(string message)
        {
            AppLogger.Log("[WorkerUI] overlay: " + message);
        }

        public void HideOverlay()
        {
        }

        public void ShowNotification(string title, string message, NotificationType type)
        {
            AppLogger.Log($"[WorkerUI] notification [{type}] {title}: {message}");
        }

        public void SetStatus(string status)
        {
            AppLogger.Log("[WorkerUI] status: " + status);
        }

        public void OutputLog(string message)
        {
            AppLogger.Log("[WorkerUI] " + message);
        }
    }
}
