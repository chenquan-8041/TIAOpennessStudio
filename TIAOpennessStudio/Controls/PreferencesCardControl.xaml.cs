using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TIAOpennessStudio.Controls
{
    /// <summary>
    /// Preferences page hosted in the code-viewer tab area. New preference sections are
    /// added as cards in the XAML, keeping the same layout as the AI settings page.
    /// </summary>
    public partial class PreferencesCardControl : UserControl
    {
        public PreferencesCardControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitWhitelistCard();
        }

        // ── TIA Openness Whitelist ─────────────────────────────────

        private void InitWhitelistCard()
        {
            var versions = Services.TiaOpennessWhitelistService.GetInstalledTiaVersions();
            cmbTiaVersion.ItemsSource = versions;
            if (versions.Count > 0)
            {
                cmbTiaVersion.SelectedIndex = versions.Count - 1;
            }
            else
            {
                txtNoTiaDetected.Visibility = Visibility.Visible;
            }

            if (!Services.TiaOpennessWhitelistService.IsRunningAsAdmin())
            {
                btnRegisterWhitelist.IsEnabled = false;
                txtWhitelistAdminWarning.Visibility = Visibility.Visible;
            }
        }

        private void OnRegisterWhitelist(object sender, RoutedEventArgs e)
        {
            string version = (cmbTiaVersion.SelectedItem as string) ?? cmbTiaVersion.Text;
            version = version?.Trim();
            if (string.IsNullOrEmpty(version)) return;

            txtWhitelistResult.Visibility = Visibility.Visible;
            try
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                string result = Services.TiaOpennessWhitelistService.WhitelistTiaOpennessApp(exePath, version);

                bool success = result.StartsWith("Success");
                txtWhitelistResult.Text = result;
                txtWhitelistResult.Foreground = success
                    ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
                    : new SolidColorBrush(Color.FromRgb(244, 67, 54));
            }
            catch (Exception ex)
            {
                txtWhitelistResult.Text = "Failed to register whitelist: " + ex.Message;
                txtWhitelistResult.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            }
        }
    }
}
