using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace TIAOpennessStudio.Controls
{
    public partial class McpServerDialog : Window
    {
        public string ServerName => txtName.Text.Trim();
        public string ServerUrl => txtUrl.Text.Trim();
        public int TimeoutSeconds { get; private set; } = 30;
        public Dictionary<string, string> Headers { get; private set; } = new Dictionary<string, string>();

        /// <summary>
        /// Create mode
        /// </summary>
        public McpServerDialog()
        {
            InitializeComponent();
            Title = "Add MCP Server";
            txtTimeout.Text = "30";
            Loaded += (s, e) => txtName.Focus();
        }

        /// <summary>
        /// Edit mode
        /// </summary>
        public McpServerDialog(string name, string url, int timeoutSeconds, Dictionary<string, string> headers)
        {
            InitializeComponent();
            Title = "Edit MCP Server";
            txtName.Text = name ?? "";
            txtUrl.Text = url ?? "";
            txtTimeout.Text = timeoutSeconds > 0 ? timeoutSeconds.ToString() : "30";
            if (headers != null && headers.Count > 0)
            {
                txtHeaders.Text = string.Join(Environment.NewLine,
                    headers.Select(kv => $"{kv.Key}={kv.Value}"));
            }
            Loaded += (s, e) => txtName.Focus();
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            // Validate required fields
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageBox.Show("Server name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtName.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(txtUrl.Text))
            {
                MessageBox.Show("URL is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtUrl.Focus();
                return;
            }

            // Validate the URL format
            if (!Uri.TryCreate(txtUrl.Text.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                MessageBox.Show("URL must be a valid http:// or https:// address.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtUrl.Focus();
                return;
            }

            // Validate the timeout
            if (!int.TryParse(txtTimeout.Text.Trim(), out var timeout) || timeout < 1 || timeout > 300)
            {
                MessageBox.Show("Timeout must be a number between 1 and 300 seconds.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtTimeout.Focus();
                return;
            }
            TimeoutSeconds = timeout;

            // Parse Headers
            Headers = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(txtHeaders.Text))
            {
                foreach (var line in txtHeaders.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    var eqIdx = trimmed.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var key = trimmed.Substring(0, eqIdx).Trim();
                        var value = trimmed.Substring(eqIdx + 1).Trim();
                        if (!string.IsNullOrEmpty(key))
                            Headers[key] = value;
                    }
                }
            }

            DialogResult = true;
        }
    }
}
