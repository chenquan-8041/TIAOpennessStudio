using System;
using System.Windows;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Styles
{
    public partial class ChatResourceDictionary : ResourceDictionary
    {
        public ChatResourceDictionary()
        {
            // Resource dictionaries don't strictly need InitializeComponent() unless they have logic
        }

        private void CopyCode_Click(object sender, RoutedEventArgs e)
        {
            // Note: In MVVM, this is handled by the Button's Command binding to MainViewModel.CopyCodeCommand
            // The event handler here is kept as a fallback or for UI-specific animations if needed.
        }

        private void PreviewCode_Click(object sender, RoutedEventArgs e)
        {
            // Note: Handled by Button's Command binding to MainViewModel.PreviewCodeCommand
        }

        private T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = System.Windows.Media.VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            T parent = parentObject as T;
            if (parent != null) return parent;
            else return FindParent<T>(parentObject);
        }
    }
}
