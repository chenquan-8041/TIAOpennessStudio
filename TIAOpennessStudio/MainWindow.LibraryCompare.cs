using System;
using System.Windows;
using Prism.Ioc;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Controls;

namespace TIAOpennessStudio
{
    public partial class MainWindow
    {
        private void OpenLibraryCompare_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (tabLibraryCompare != null)
                {
                    // Lazy-initialize the control on first open
                    if (tabLibraryCompare.Content == null)
                    {
                        var compareService = App.Services.Resolve<ILibraryCompareService>();
                        var backupService = App.Services.Resolve<IBackupService>();
                        tabLibraryCompare.Content = new LibraryCompareControl(compareService, backupService);
                    }

                    tabLibraryCompare.Visibility = Visibility.Visible;
                    tabsCode.Visibility = Visibility.Visible;
                    tabsCode.SelectedItem = tabLibraryCompare;
                    lblNoCode.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to open Library Versioning: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseLibraryCompare_Click(object sender, RoutedEventArgs e)
        {
            if (tabLibraryCompare != null) tabLibraryCompare.Visibility = Visibility.Collapsed;
            UpdateNoCodeVisibility();
        }
    }
}
