using System;

namespace TIAOpennessStudio.Services.Abstractions
{
    public enum NotificationType { Info, Warning, Error, Success }

    /// <summary>
    /// UI facade for overlays, notifications, status, and log output.
    /// </summary>
    public interface IUIService
    {
        void ShowOverlay(string message);
        void HideOverlay();
        void ShowNotification(string title, string message, NotificationType type = NotificationType.Info);
        void SetStatus(string message);
        void OutputLog(string message);
    }
}