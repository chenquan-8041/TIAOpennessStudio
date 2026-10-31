using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Prism.Events;
using Prism.Ioc;
using TIAOpennessStudio.Events;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Standards;
using TIAOpennessStudio.ViewModels;

namespace TIAOpennessStudio.Controls
{
    /// <summary>
    /// Sidebar "Knowledge Base" panel: shows official coding-standard status and manages user private
    /// mount libraries (mount / encrypted create / remove).
    /// Official libraries are decrypted transparently with a built-in default key; user libraries are
    /// encrypted with a user passphrase (optionally remembered via DPAPI).
    /// Supports themes (DynamicResource) and three-language switching (LanguageManager).
    /// </summary>
    public partial class KnowledgeManagementControl : UserControl
    {
        private StandardsRegistry _registry;

        public KnowledgeManagementControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;

            // Three languages: apply initially + refresh on language change, unbind on unload.
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
            ApplyLanguage();
            Unloaded += (_, __) => LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged(AppLanguage lang)
        {
            ApplyLanguage();
            RefreshUi(); // Refresh status text with the current language
        }

        /// <summary>Applies the current language strings to the named controls.</summary>
        private void ApplyLanguage()
        {
            var lm = LanguageManager.Instance;
            if (lblTitle != null) lblTitle.Text = lm.GetString("Kb.Title");
            if (btnMount != null) { btnMount.Content = lm.GetString("Kb.Btn.Mount"); btnMount.ToolTip = lm.GetString("Kb.Btn.MountTip"); }
            if (btnCreateEncrypted != null) { btnCreateEncrypted.Content = lm.GetString("Kb.Btn.Create"); btnCreateEncrypted.ToolTip = lm.GetString("Kb.Btn.CreateTip"); }
            if (btnUnlock != null) { btnUnlock.Content = lm.GetString("Kb.Btn.Unlock"); btnUnlock.ToolTip = lm.GetString("Kb.Btn.UnlockTip"); }
            if (btnUnmount != null) btnUnmount.Content = lm.GetString("Kb.Btn.Unmount");
            // Row-level "Browse" button lives in the list ItemTemplate, so it is localized through a string resource.
            Resources["KbBrowseLabel"] = lm.GetString("Kb.Btn.Browse");
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _registry = App.Services.Resolve<StandardsRegistry>();
            }
            catch
            {
                // If DI is not ready or not registered, the UI can still show but functionality is disabled.
            }
            RefreshUi();
        }

        private void RefreshUi()
        {
            if (_registry == null)
            {
                if (listUserLibs != null) listUserLibs.ItemsSource = null;
                return;
            }

            var lm = LanguageManager.Instance;
            var rows = new List<LibraryRow>();

            // Official libraries: one row per standards* library (shares the same template/selection effect as user libraries).
            foreach (var lib in _registry.OfficialLibraries)
            {
                rows.Add(new LibraryRow
                {
                    Id = lib.Id,               // KbPath as the unique identifier
                    IsOfficial = true,
                    DisplayName = lib.DisplayName,
                    Encryption = "official",
                    Enabled = true,
                    StatusText = lm.GetString("Kb.Official.Loaded")
                });
            }

            rows.AddRange(_registry.Mounts.Select(m => new LibraryRow
            {
                Id = m.Id,
                IsOfficial = false,
                DisplayName = m.DisplayName,
                Encryption = m.Encryption,
                Enabled = m.Enabled,
                StatusText = BuildStatus(m)
            }));

            listUserLibs.ItemsSource = rows;
        }

        private string BuildStatus(MountedLibraryInfo m)
        {
            var lm = LanguageManager.Instance;
            if (!m.Enabled) return lm.GetString("Kb.Status.Disabled");
            if (string.Equals(m.Encryption, "passphrase", StringComparison.OrdinalIgnoreCase))
                return _registry.IsUnlocked(m.Id) ? lm.GetString("Kb.Status.Unlocked") : lm.GetString("Kb.Status.Locked");
            return lm.GetString("Kb.Status.Ready");
        }

        // ---- Open knowledge base → show a browse tab in the central area ----
        // Official and user libraries share the same list-item template and "Browse" button; behavior is dispatched by LibraryRow.IsOfficial.

        /// <summary>"Browse" button: the button Tag carries LibraryRow.Id.</summary>
        private void BtnOpenLib_Click(object sender, RoutedEventArgs e)
        {
            var id = (sender as Button)?.Tag as string;
            OpenRowById(id);
        }

        /// <summary>Double-click a list item: convenience entry point.</summary>
        private void ListUserLibs_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(listUserLibs.SelectedItem is LibraryRow row)) return;
            OpenRowById(row.Id);
        }

        /// <summary>Opens the corresponding knowledge base (official or user) by row Id.</summary>
        private void OpenRowById(string id)
        {
            if (_registry == null || string.IsNullOrEmpty(id)) return;
            var lm = LanguageManager.Instance;

            // Official library: id is that library's KbPath; if matched, browse that library alone.
            var official = _registry.OfficialLibraries.FirstOrDefault(l => l.Id == id);
            if (official.Id != null)
            {
                OpenKnowledgeTab(official.DisplayName, () => _registry.GetOfficialRulesById(id));
                return;
            }

            var info = _registry.Mounts?.FirstOrDefault(m => m.Id == id);
            if (info == null) return;

            var kind = (info.Encryption ?? "passphrase").ToLowerInvariant();
            if (kind == "passphrase" && !_registry.IsUnlocked(info.Id))
            {
                MessageBox.Show(lm.GetString("Kb.Status.Locked"), lm.GetString("Kb.Common.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenKnowledgeTab(info.DisplayName, () => _registry.GetMountRules(info.Id));
        }

        /// <summary>Loads rules and opens a knowledge base browse tab in the central area via an event (reusing ShowCodeViewerEvent).</summary>
        private void OpenKnowledgeTab(string kbName, Func<IReadOnlyList<StandardRule>> ruleLoader)
        {
            var lm = LanguageManager.Instance;
            try
            {
                var rules = ruleLoader() ?? new List<StandardRule>();

                var view = new KnowledgeViewControl();
                view.Load(kbName, rules);

                var aggregator = App.Services?.Resolve<IEventAggregator>();
                aggregator?.GetEvent<ShowCodeViewerEvent>()
                    .Publish(new CodeViewerEventArgs(kbName, "Knowledge", view));
            }
            catch (Exception ex)
            {
                AppLogger.LogError("KnowledgeBase open failed", ex);
                MessageBox.Show(ex.Message, lm.GetString("Kb.Common.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>List row view model (including unlock state).</summary>
        public sealed class LibraryRow
        {
            public string Id { get; set; }
            public bool IsOfficial { get; set; }
            public string DisplayName { get; set; }
            public string Encryption { get; set; }
            public bool Enabled { get; set; }
            public string StatusText { get; set; }
        }

        private void BtnMount_Click(object sender, RoutedEventArgs e)
        {
            if (_registry == null) return;
            var lm = LanguageManager.Instance;
            var dlg = new OpenFileDialog
            {
                Title = lm.GetString("Kb.Btn.Mount"),
                Filter = "*.kb|*.kb|*.*|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            var enc = AskEncryptionKind();
            if (enc == null) return;

            string passphrase = null;
            bool remember = false;
            bool bindToMachine = false;
            if (enc == "passphrase")
            {
                passphrase = PromptText(lm.GetString("Kb.Dlg.EnterPass"), isPassword: true);
                if (passphrase == null) return;
                remember = AskRemember();
                // When mounting an existing library, also specify whether it is a machine-locked library; the decryption mode must match the one used when packaging.
                bindToMachine = AskBindMachine();
            }

            try
            {
                var name = Path.GetFileNameWithoutExtension(dlg.FileName);
                _registry.Mount(dlg.FileName, name, enc, passphrase, remember, bindToMachine);
                RefreshUi();
                MessageBox.Show(lm.GetString("Kb.Msg.MountOk"), lm.GetString("Kb.Common.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(lm.GetString("Kb.Msg.MountFail") + ex.Message, lm.GetString("Kb.Common.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCreateEncrypted_Click(object sender, RoutedEventArgs e)
        {
            if (_registry == null) return;
            var lm = LanguageManager.Instance;

            var src = new OpenFileDialog
            {
                Title = lm.GetString("Kb.Btn.Create"),
                Filter = "Markdown (*.md)|*.md|*.*|*.*"
            };
            if (src.ShowDialog() != true) return;

            var save = new SaveFileDialog
            {
                Title = lm.GetString("Kb.Btn.Create"),
                Filter = "*.kb|*.kb",
                FileName = Path.GetFileNameWithoutExtension(src.FileName) + ".kb"
            };
            if (save.ShowDialog() != true) return;

            var passphrase = PromptText(lm.GetString("Kb.Dlg.SetPass"), isPassword: true);
            if (string.IsNullOrEmpty(passphrase)) return;

            // The user chooses during entry: whether to lock this library to the current machine.
            var bindToMachine = AskBindMachine();

            try
            {
                var rules = StandardsMarkdownImporter.ParseFile(src.FileName);
                if (rules.Count == 0)
                {
                    MessageBox.Show(lm.GetString("Kb.Msg.NoRules"), lm.GetString("Kb.Common.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // Packaging and decryption must use the same bindToMachine, otherwise decryption fails on another machine (or on this machine in locked mode).
                StandardsPackager.Build(rules, save.FileName, new PassphraseSecretProvider(passphrase), bindToMachine);

                // Auto-mount after creation
                var remember = AskRemember();
                var name = Path.GetFileNameWithoutExtension(save.FileName);
                _registry.Mount(save.FileName, name, "passphrase", passphrase, remember, bindToMachine);
                RefreshUi();
                MessageBox.Show(lm.GetString("Kb.Msg.CreateOk"), lm.GetString("Kb.Common.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(lm.GetString("Kb.Msg.CreateFail") + ex.Message, lm.GetString("Kb.Common.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (_registry == null) return;
            var lm = LanguageManager.Instance;
            if (!(listUserLibs.SelectedItem is LibraryRow info)) return;
            if (info.IsOfficial) return; // Official libraries need no unlock
            if (!string.Equals(info.Encryption, "passphrase", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(lm.GetString("Kb.Msg.NoPass"), lm.GetString("Kb.Common.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_registry.IsUnlocked(info.Id))
            {
                MessageBox.Show(lm.GetString("Kb.Msg.AlreadyUnlocked"), lm.GetString("Kb.Common.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var pp = PromptText(lm.GetString("Kb.Dlg.EnterPass"), isPassword: true);
            if (pp == null) return;
            var remember = AskRemember();
            try
            {
                _registry.UnlockPassphraseLibrary(info.Id, pp, remember);
                RefreshUi();
                MessageBox.Show(lm.GetString("Kb.Msg.UnlockOk"), lm.GetString("Kb.Common.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(lm.GetString("Kb.Msg.UnlockFail") + ex.Message, lm.GetString("Kb.Common.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Asks whether to remember the passphrase via DPAPI (auto-unlock on restart).</summary>
        private static bool AskRemember()
        {
            var lm = LanguageManager.Instance;
            return MessageBox.Show(
                lm.GetString("Kb.Dlg.Remember"),
                lm.GetString("Kb.Dlg.RememberTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        /// <summary>Asks whether to lock this library to the current machine (key mixed into the machine fingerprint; even a correct passphrase cannot decrypt it on another machine).</summary>
        private static bool AskBindMachine()
        {
            var lm = LanguageManager.Instance;
            return MessageBox.Show(
                lm.GetString("Kb.Dlg.BindMachine"),
                lm.GetString("Kb.Dlg.BindMachineTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private void BtnUnmount_Click(object sender, RoutedEventArgs e)
        {
            if (_registry == null) return;
            var lm = LanguageManager.Instance;
            if (!(listUserLibs.SelectedItem is LibraryRow info)) return;
            if (info.IsOfficial) return; // Official libraries are read-only shipped content and cannot be removed
            var confirm = string.Format(lm.GetString("Kb.Msg.ConfirmRemove"), info.DisplayName);
            if (MessageBox.Show(confirm, lm.GetString("Kb.Common.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            _registry.Unmount(info.Id);
            RefreshUi();
        }

        /// <summary>Returns "passphrase" | "none" | null (canceled).</summary>
        private static string AskEncryptionKind()
        {
            var lm = LanguageManager.Instance;
            var r = MessageBox.Show(
                lm.GetString("Kb.Dlg.EncQuestion"),
                lm.GetString("Kb.Dlg.EncTitle"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return null;
            return r == MessageBoxResult.Yes ? "passphrase" : "none";
        }

        /// <summary>Minimal input box using the app theme brushes and button style (theme-aware). Returns null on cancel.</summary>
        private static string PromptText(string message, bool isPassword)
        {
            var lm = LanguageManager.Instance;
            var win = new Window
            {
                Title = lm.GetString("Kb.Common.Title"),
                Width = 380,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = ThemeBrush("BackgroundBrush", "SidebarBrush")
            };
            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = message,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("TextBrush", null)
            };
            Grid.SetRow(lbl, 0);

            Control input = isPassword ? (Control)new PasswordBox() : new TextBox();
            input.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(input, 1);

            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okStyle = Application.Current?.TryFindResource("ModernButtonStyle") as Style;
            var ok = new Button { Content = lm.GetString("Kb.Common.Ok"), Width = 72, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, Style = okStyle };
            var cancel = new Button { Content = lm.GetString("Kb.Common.Cancel"), Width = 72, IsCancel = true, Style = okStyle };
            panel.Children.Add(ok);
            panel.Children.Add(cancel);
            Grid.SetRow(panel, 2);

            grid.Children.Add(lbl);
            grid.Children.Add(input);
            grid.Children.Add(panel);
            win.Content = grid;

            string result = null;
            ok.Click += (_, __) =>
            {
                result = isPassword ? ((PasswordBox)input).Password : ((TextBox)input).Text;
                win.DialogResult = true;
            };
            return win.ShowDialog() == true ? result : null;
        }

        /// <summary>Gets a theme brush; falls back to the fallback key, then to transparent/default.</summary>
        private static System.Windows.Media.Brush ThemeBrush(string key, string fallbackKey)
        {
            var app = Application.Current;
            if (app == null) return System.Windows.Media.Brushes.Transparent;
            if (app.TryFindResource(key) is System.Windows.Media.Brush b) return b;
            if (fallbackKey != null && app.TryFindResource(fallbackKey) is System.Windows.Media.Brush fb) return fb;
            return System.Windows.Media.Brushes.Transparent;
        }
    }
}
