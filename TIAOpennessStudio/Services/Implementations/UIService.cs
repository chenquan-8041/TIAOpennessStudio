using System;
using System.Windows;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>WPF UI service that forwards overlay/status requests to injected UI callbacks.</summary>
    public class UIService : IUIService
    {
        public Action<string> ShowOverlayAction { get; set; }
        public Action HideOverlayAction { get; set; }
        public Action<string> SetStatusAction { get; set; }

        public void ShowOverlay(string message) => Application.Current.Dispatcher.Invoke(() => ShowOverlayAction?.Invoke(message));
        public void HideOverlay() => Application.Current.Dispatcher.Invoke(() => HideOverlayAction?.Invoke());
        public void SetStatus(string message) => Application.Current.Dispatcher.Invoke(() => SetStatusAction?.Invoke(message));

        public void ShowNotification(string title, string message, NotificationType type = NotificationType.Info)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBoxImage icon = MessageBoxImage.Information;
                switch (type)
                {
                    case NotificationType.Warning: icon = MessageBoxImage.Warning; break;
                    case NotificationType.Error: icon = MessageBoxImage.Error; break;
                    case NotificationType.Success: icon = MessageBoxImage.Information; break;
                }
                MessageBox.Show(message, title, MessageBoxButton.OK, icon);
            });
        }

        public void OutputLog(string message)
        {
            // Implementation for logging...
            AppLogger.Log(message);
        }
    }
}