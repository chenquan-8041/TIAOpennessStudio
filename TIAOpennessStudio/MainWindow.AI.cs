using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Prism.Ioc;
using TIAOpennessStudio.AICore;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Models;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;

namespace TIAOpennessStudio
{
    // AI partial: AI configuration, backend startup, and language switching.
    public partial class MainWindow
    {
        // ExistsTargetInProject, ValidateXmlTargetConstraint, BuildLineDiffPreview
        // have been moved to Services.BlockValidationHelper

        /// <summary>
        /// Show the code diff in the Code Viewer's Diff Tab and wait for the user to Apply/Reject.
        /// Returns true if the user confirms, false if cancelled.
        /// </summary>
        private Task<bool> ShowDiffAndWaitAsync(string oldCode, string newCode, string title)
        {
            var tcs = new TaskCompletionSource<bool>();

            Dispatcher.Invoke(() =>
            {
                ShowDiffTab(oldCode, newCode, title);

                EventHandler onApplied = null;
                EventHandler onRejected = null;

                onApplied = (s, e) =>
                {
                    diffPreviewControl.Applied -= onApplied;
                    diffPreviewControl.Rejected -= onRejected;
                    tcs.TrySetResult(true);
                };

                onRejected = (s, e) =>
                {
                    diffPreviewControl.Applied -= onApplied;
                    diffPreviewControl.Rejected -= onRejected;
                    tcs.TrySetResult(false);
                };

                diffPreviewControl.Applied += onApplied;
                diffPreviewControl.Rejected += onRejected;
            });

            return tcs.Task;
        }

        private void Config_Click(object sender, RoutedEventArgs e)
        {
            // Show Settings tab in Code Viewer instead of a separate dialog
            if (tabSettings != null)
            {
                tabSettings.Visibility = Visibility.Visible;
                tabsCode.Visibility = Visibility.Visible;
                tabsCode.SelectedItem = tabSettings;
                lblNoCode.Visibility = Visibility.Collapsed;
            }
        }

        private void MnuPreferences_Click(object sender, RoutedEventArgs e)
        {
            // Show Preferences tab in Code Viewer (same pattern as the Settings tab)
            if (tabPreferences != null)
            {
                tabPreferences.Visibility = Visibility.Visible;
                tabsCode.Visibility = Visibility.Visible;
                tabsCode.SelectedItem = tabPreferences;
                lblNoCode.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowAIConfigDialog()
        {
            // Load configuration via the ViewModel
            _viewModel.AIConfigViewModel.LoadConfig();
            var config = _viewModel.AIConfigViewModel.Config;
            Brush GetThemeBrush(string key, Brush fallback)
            {
                return TryFindResource(key) as Brush ?? fallback;
            }

            var backgroundBrush = GetThemeBrush("BackgroundBrush", Brushes.Black);
            var panelBrush = GetThemeBrush("PanelBrush", Brushes.DimGray);
            var borderBrush = GetThemeBrush("BorderBrush", Brushes.Gray);
            var textBrush = GetThemeBrush("TextBrush", Brushes.White);
            var mutedTextBrush = GetThemeBrush("MutedTextBrush", Brushes.Gray);
            var buttonBrush = GetThemeBrush("ButtonBackground", Brushes.DimGray);
            var accentBrush = GetThemeBrush("AccentBrush", Brushes.DodgerBlue);

            var checkBoxStyle = new Style(typeof(CheckBox));
            checkBoxStyle.Setters.Add(new Setter(Control.ForegroundProperty, textBrush));
            checkBoxStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            checkBoxStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 0, 0, 8)));

            var dialog = new Window
            {
                Title = "Model Configuration",
                Width = 500,
                Height = 550,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = backgroundBrush,
                Foreground = textBrush,
                ResizeMode = ResizeMode.NoResize
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var providerLabel = new TextBlock { Text = "AI Provider:", Foreground = textBrush, Margin = new Thickness(0, 0, 0, 5) };
            var providerCombo = new ComboBox
            {
                Margin = new Thickness(0, 0, 0, 10),
                IsEditable = true,
                IsReadOnly = false
            };
            // Provider list is driven by the local model table (all supported
            // platforms), which mirrors the backend's provider catalog. The
            // backend remains the source of truth for what it can actually serve;
            // the connection test surfaces any adapter/dependency gap at use time.
            foreach (var p in AIConfig.GetAvailableModels().Keys)
            {
                providerCombo.Items.Add(p);
            }
            providerCombo.SelectedItem = config.Provider;

            var providerPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            providerPanel.Children.Add(providerLabel);
            providerPanel.Children.Add(providerCombo);
            Grid.SetRow(providerPanel, 0);
            grid.Children.Add(providerPanel);

            var modelLabel = new TextBlock { Text = "Model:", Foreground = textBrush, Margin = new Thickness(0, 0, 0, 5) };
            var modelCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 10), IsEditable = true };

            var models = AIConfig.GetAvailableModels();
            var currentModels = config.GetCurrentProviderModels();
            bool modelFound = false;
            foreach (var m in currentModels)
            {
                modelCombo.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id });
                if (m.Id == config.Model || m.Id == config.SelectedModel)
                {
                    modelCombo.SelectedIndex = modelCombo.Items.Count - 1;
                    modelFound = true;
                }
            }

            if (!modelFound && !string.IsNullOrEmpty(config.Model))
            {
                modelCombo.Text = config.Model;
            }

            providerCombo.SelectionChanged += (s, args) =>
            {
                modelCombo.Items.Clear();
                var selectedProvider = providerCombo.SelectedItem?.ToString();
                if (selectedProvider != null && models.ContainsKey(selectedProvider))
                {
                    foreach (var m in models[selectedProvider])
                    {
                        modelCombo.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id });
                    }
                    if (modelCombo.Items.Count > 0)
                    {
                        modelCombo.SelectedIndex = 0;
                    }
                }
                // Update the ViewModel's ModelProvider property
                _viewModel.AIConfigViewModel.ModelProvider = selectedProvider;
            };

            var modelPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            modelPanel.Children.Add(modelLabel);
            modelPanel.Children.Add(modelCombo);
            Grid.SetRow(modelPanel, 1);
            grid.Children.Add(modelPanel);

            var apiKeyLabel = new TextBlock { Text = "API Key:", Foreground = textBrush, Margin = new Thickness(0, 0, 0, 5) };
            var apiKeyBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(5), Background = panelBrush, Foreground = textBrush, BorderBrush = borderBrush };
            apiKeyBox.Password = config.ApiKey;

            var apiKeyPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            apiKeyPanel.Children.Add(apiKeyLabel);
            apiKeyPanel.Children.Add(apiKeyBox);
            Grid.SetRow(apiKeyPanel, 2);
            grid.Children.Add(apiKeyPanel);

            var endpointLabel = new TextBlock { Text = "API Endpoint:", Foreground = textBrush, Margin = new Thickness(0, 0, 0, 5) };
            var endpointBox = new TextBox { Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(5), Text = config.Endpoint, Background = panelBrush, Foreground = textBrush, BorderBrush = borderBrush };

            providerCombo.SelectionChanged += (s, args) =>
            {
                var selectedProvider = providerCombo.SelectedItem?.ToString();
                if (selectedProvider == "OpenAI")
                {
                    endpointBox.Text = "https://api.openai.com/v1/chat/completions";
                }
                // Update the ViewModel's ModelProvider property
                _viewModel.AIConfigViewModel.ModelProvider = selectedProvider;
            };

            var endpointPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            endpointPanel.Children.Add(endpointLabel);
            endpointPanel.Children.Add(endpointBox);
            Grid.SetRow(endpointPanel, 3);
            grid.Children.Add(endpointPanel);

            var useLocalBackendCheck = new CheckBox { Content = "Use Local Backend (Python Agent)", Style = checkBoxStyle, IsChecked = config.UseLocalBackend };
            var backendHint = new TextBlock { Text = "Checked: Use local Python backend agent\nUnchecked: Direct API call to provider", Foreground = mutedTextBrush, FontSize = 11, Margin = new Thickness(20, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
            var backendPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            backendPanel.Children.Add(useLocalBackendCheck);
            backendPanel.Children.Add(backendHint);
            Grid.SetRow(backendPanel, 4);
            grid.Children.Add(backendPanel);

            var localBackendLabel = new TextBlock { Text = "Local Backend URL:", Foreground = textBrush, Margin = new Thickness(0, 10, 0, 5) };
            var localBackendBox = new TextBox { Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(5), Text = config.LocalBackendUrl ?? "http://127.0.0.1:8000", Background = panelBrush, Foreground = textBrush, BorderBrush = borderBrush };
            backendPanel.Children.Add(localBackendLabel);
            backendPanel.Children.Add(localBackendBox);

            var contextCheck = new CheckBox { Content = "Include Project Context", Style = checkBoxStyle, Margin = new Thickness(0, 0, 0, 4) };
            contextCheck.IsChecked = config.IncludeContext;
            var contextHint = new TextBlock
            {
                Text = "Checked: Send PLC project facts (block list, UDTs, tag tables) as context for project-aware code generation\nUnchecked: Only chat prompt + history (saves tokens, off-topic chatting)",
                Foreground = mutedTextBrush,
                FontSize = 11,
                Margin = new Thickness(20, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            var contextPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            contextPanel.Children.Add(contextCheck);
            contextPanel.Children.Add(contextHint);
            Grid.SetRow(contextPanel, 6);
            grid.Children.Add(contextPanel);

            // ---- Saved custom models manager ----
            var customModelsHeader = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            customModelsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            customModelsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var customModelsLabel = new TextBlock
            {
                Text = "Saved Custom Models:",
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(customModelsLabel, 0);
            customModelsHeader.Children.Add(customModelsLabel);

            var addCustomBtn = new Button
            {
                Content = "+ Add",
                Padding = new Thickness(10, 2, 10, 2),
                Background = Brushes.Transparent,
                Foreground = textBrush,
                BorderBrush = borderBrush,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Add the model name from the Model field above to the saved custom models list"
            };
            Grid.SetColumn(addCustomBtn, 1);
            customModelsHeader.Children.Add(addCustomBtn);

            var customModelsList = new ItemsControl
            {
                Margin = new Thickness(0, 0, 0, 10),
                MaxHeight = 110,
                Background = panelBrush
            };
            // Wrap in a ScrollViewer to prevent a long list from stretching the dialog
            var customModelsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = panelBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                MaxHeight = 110,
                Margin = new Thickness(0, 0, 0, 15),
                Content = customModelsList
            };
            var customHint = new TextBlock
            {
                Text = "Built-in local LLMs are read-only. Type a custom model name in the Model field above and click + Add (or Save) to register it here. Models in this list appear in the chat input box dropdown.",
                Foreground = (Brush)(TryFindResource("MutedTextBrush") ?? Brushes.Gray),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };

            // Render the list (the same helper is called after + Add / Delete / Save to reflect the latest state)
            Action renderCustomModels = null;
            renderCustomModels = () =>
            {
                customModelsList.Items.Clear();

                // 1) Built-in local LLMs (read-only, grayed out)
                foreach (var name in AIConfig.BuiltInLocalLlmModels)
                {
                    customModelsList.Items.Add(BuildModelRow(name, panelBrush, textBrush, borderBrush,
                        deletable: false, onDelete: null));
                }

                // 2) User custom models (deletable)
                if (config.CustomModels != null)
                {
                    foreach (var name in new List<string>(config.CustomModels))
                    {
                        var captured = name;
                        customModelsList.Items.Add(BuildModelRow(captured, panelBrush, textBrush, borderBrush,
                            deletable: true, onDelete: () =>
                            {
                                if (config.RemoveCustomModel(captured))
                                {
                                    config.Save();
                                    renderCustomModels();
                                }
                            }));
                    }
                }
            };
            renderCustomModels();

            // + Add button: read from the Model field, add to CustomModels, refresh the list
            addCustomBtn.Click += (s, args) =>
            {
                var name = (modelCombo.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    MessageBox.Show(
                        "Please type a model name in the Model field first.",
                        "Add Custom Model",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (config.AddCustomModel(name))
                {
                    config.Save();
                    renderCustomModels();
                }
                else
                {
                    MessageBox.Show(
                        $"'{name}' is already in the list (or it duplicates a built-in / provider model).",
                        "Add Custom Model",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            };

            var customModelsPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
            customModelsPanel.Children.Add(customModelsHeader);
            customModelsPanel.Children.Add(customModelsScroll);
            customModelsPanel.Children.Add(customHint);
            Grid.SetRow(customModelsPanel, 5);
            grid.Children.Add(customModelsPanel);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };

            var testBtn = new Button { Content = "Test API", Padding = new Thickness(20, 8, 20, 8), Margin = new Thickness(0, 0, 10, 0), Background = buttonBrush, Foreground = textBrush, BorderBrush = borderBrush };
            testBtn.Click += async (s, args) =>
            {
                testBtn.IsEnabled = false;
                testBtn.Content = "Testing...";
                try
                {
                // Update the ViewModel configuration for the test
                _viewModel.AIConfigViewModel.ExecutionMode = useLocalBackendCheck.IsChecked == true ? "LocalGateway" : "Direct";
                _viewModel.AIConfigViewModel.ModelProvider = providerCombo.SelectedItem?.ToString() ?? "OpenAI";
                    
                    string currentText = modelCombo.Text;
                    string selectedModel = currentText;
                    foreach (ComboBoxItem item in modelCombo.Items)
                    {
                        if (item.Content?.ToString() == currentText || item.Tag?.ToString() == currentText)
                        {
                            selectedModel = item.Tag?.ToString() ?? currentText;
                            break;
                        }
                    }
                    
                    _viewModel.AIConfigViewModel.Model = selectedModel;
                    _viewModel.AIConfigViewModel.ApiKey = apiKeyBox.Password;
                    _viewModel.AIConfigViewModel.Endpoint = endpointBox.Text;
                    _viewModel.AIConfigViewModel.LocalBackendUrl = localBackendBox.Text;
                    
                    bool useLocal = useLocalBackendCheck.IsChecked ?? true;
                    if (!useLocal && string.IsNullOrEmpty(apiKeyBox.Password))
                    {
                        MessageBox.Show("Please enter an API Key.", "Test Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Test the API via the ViewModel
                    string testResult = await _viewModel.AIConfigViewModel.TestApiAsync();
                    
                    if (testResult.Contains("测试通过"))
                    {
                        MessageBox.Show($"Connection successful!\n{testResult}", "Test Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show($"Connection failed: \n{testResult}", "Test Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Connection failed:\n{ex.Message}", "Test Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    testBtn.IsEnabled = true;
                    testBtn.Content = "Test API";
                }
            };

            var saveBtn = new Button { Content = "Save", Padding = new Thickness(20, 8, 20, 8), Margin = new Thickness(0, 0, 10, 0), Background = accentBrush, Foreground = textBrush, BorderBrush = borderBrush };
            saveBtn.Click += (s, args) =>
            {
                // Update the ViewModel configuration
                _viewModel.AIConfigViewModel.ExecutionMode = useLocalBackendCheck.IsChecked == true ? "LocalGateway" : "Direct";
                _viewModel.AIConfigViewModel.ModelProvider = providerCombo.SelectedItem?.ToString() ?? "OpenAI";

                string currentText = modelCombo.Text;
                string selectedModel = currentText;
                foreach (ComboBoxItem item in modelCombo.Items)
                {
                    if (item.Content?.ToString() == currentText || item.Tag?.ToString() == currentText)
                    {
                        selectedModel = item.Tag?.ToString() ?? currentText;
                        break;
                    }
                }

                _viewModel.AIConfigViewModel.Model = selectedModel;
                _viewModel.AIConfigViewModel.ApiKey = apiKeyBox.Password;
                _viewModel.AIConfigViewModel.Endpoint = endpointBox.Text;
                _viewModel.AIConfigViewModel.LocalBackendUrl = localBackendBox.Text;
                _viewModel.AIConfigViewModel.IncludeContext = contextCheck.IsChecked ?? false;

                // If the model name the user typed is new (not a hard-coded local LLM and not a built-in of the current Provider),
                // record it in CustomModels so it appears and can be selected in the input box dropdown
                var configForCustom = _viewModel.AIConfigViewModel.Config;
                bool added = false;
                if (configForCustom != null && !string.IsNullOrWhiteSpace(selectedModel))
                {
                    added = configForCustom.AddCustomModel(selectedModel);
                }


                // Save the configuration via the ViewModel
                _viewModel.AIConfigViewModel.SaveConfig();

                // Refresh the Saved Custom Models list inside the dialog (in case the user edited the Model field and clicked Save directly)
                if (added) renderCustomModels();

                // Update the AI generator
                var savedConfig = _viewModel.AIConfigViewModel.Config;
                if (savedConfig.UseRealAI)
                    aiGenerator = new RestAICodeGenerator(savedConfig.ApiKey, savedConfig.Endpoint, savedConfig.Model, savedConfig.Provider, savedConfig.UseLocalBackend, savedConfig.LocalBackendUrl);
                else
                    aiGenerator = new MockAICodeGenerator();

                // Also hot-update the AIService instance in the DI container (the reference held by AgentOrchestrator/ChatViewModel)
                _viewModel?.AIService?.UpdateRuntimeConfig(
                    apiKey: savedConfig.ApiKey,
                    endpoint: savedConfig.Endpoint,
                    model: savedConfig.Model,
                    provider: savedConfig.Provider);

                // Refresh the input box model dropdown (so newly added custom models are immediately visible)
                _aiConfig = savedConfig;
                RefreshAvailableModelList();

                MessageBox.Show("Configuration saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                dialog.Close();
            };

            var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(20, 8, 20, 8), Background = buttonBrush, Foreground = textBrush, BorderBrush = borderBrush };
            cancelBtn.Click += (s, args) => dialog.Close();

            buttonPanel.Children.Add(testBtn);
            buttonPanel.Children.Add(saveBtn);
            buttonPanel.Children.Add(cancelBtn);
            Grid.SetRow(buttonPanel, 7);
            grid.Children.Add(buttonPanel);

            dialog.Content = grid;
            dialog.ShowDialog();
        }

        /// <summary>
        /// Render one row in the Saved Custom Models list: [model name] ... [Delete] / [Built-in lock tag]
        /// </summary>
        private static UIElement BuildModelRow(
            string modelName,
            Brush rowBg,
            Brush textBrush,
            Brush borderBrush,
            bool deletable,
            Action onDelete)
        {
            var row = new Grid
            {
                Margin = new Thickness(2),
                Background = rowBg
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = modelName,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 4, 8, 4)
            };
            Grid.SetColumn(nameText, 0);
            row.Children.Add(nameText);

            if (deletable)
            {
                var del = new Button
                {
                    Content = "Delete",
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 2, 4, 2),
                    Background = Brushes.Transparent,
                    Foreground = textBrush,
                    BorderBrush = borderBrush,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                del.Click += (s, e) => onDelete?.Invoke();
                Grid.SetColumn(del, 1);
                row.Children.Add(del);
            }
            else
            {
                var lockTag = new TextBlock
                {
                    Text = "Built-in",
                    Foreground = (Brush)(Application.Current.TryFindResource("MutedTextBrush") ?? Brushes.Gray),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                Grid.SetColumn(lockTag, 1);
                row.Children.Add(lockTag);
            }

            return row;
        }

        private void StartAi_Click(object sender, RoutedEventArgs e)
        {
            Config_Click(sender, e);
        }

        private async void StartBackend_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var backendHost = App.Services.Resolve<IBackendHostService>();
                bool ok = await backendHost.EnsureStartedAsync();
                if (ok)
                {
                    AppLogger.Log("[System] Python Backend is ready.");
                    MessageBox.Show("Python Backend is ready.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Failed to start Python backend. Please check backend logs.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Failed to start backend", ex);
                MessageBox.Show($"Failed to start backend: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Language_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item && item.Tag is string langCode)
            {
                if (langCode == "DE") LanguageManager.Instance.SetLanguage(AppLanguage.German);
                else if (langCode == "CN") LanguageManager.Instance.SetLanguage(AppLanguage.Chinese);
                else LanguageManager.Instance.SetLanguage(AppLanguage.English);
            }
        }

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is MenuItem item) || !(item.Tag is string themeTag)) return;

            var themeService = App.Services.Resolve<IThemeService>();
            if (string.Equals(themeTag, "Light", StringComparison.OrdinalIgnoreCase))
            {
                themeService.SetTheme(ThemeMode.Light);
            }
            else
            {
                themeService.SetTheme(ThemeMode.Dark);
            }
        }
    }
}
