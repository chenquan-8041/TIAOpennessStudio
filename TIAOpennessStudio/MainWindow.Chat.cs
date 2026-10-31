using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Prism.Events;
using Prism.Ioc;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TIAOpennessStudio.AICore;
using TIAOpennessStudio.Events;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.ViewModels;

namespace TIAOpennessStudio
{
    // Chat partial: prompt input, conversation lifecycle, streaming updates, and chat history.
    public partial class MainWindow
    {
        private ChatViewModel _chatViewModel;
        private AIConfig _aiConfig;
        
        private void InitChatModule()
        {
            _chatViewModel = _viewModel.ChatViewModel;
            
            // Bind ChatViewModel properties to the UI
            ChatPanel.MessageListControl.Messages = _chatViewModel.ChatMessages;

            // Subscribe to BlockWritten so we can attach the undo snapshot to the
            // most-recent assistant message after every successful write-back.
            _viewModel.TiaService.BlockWritten += OnBlockWritten;

            // Subscribe to UndoMessageRequested so the Undo button triggers the diff flow.
            _viewModel.UndoMessageRequested += OnUndoMessageRequested;

            // Attach the interaction drawer (above the input box) as the presenter for agent approvals / questions.
            try
            {
                App.Services.Resolve<Services.Implementations.InteractionPresenterHost>()
                    .Attach(ChatPanel.InteractionDrawerControl);
            }
            catch (Exception ex)
            {
                AppLogger.Log("[Interaction] could not attach the drawer: " + ex.Message);
            }

            // Access-mode switch (read-only / allow writes): the input-box button only reflects the shared
            // IAgentAccessMode; clicking it flips Current. A run keeps the mode it started with (the
            // orchestrator binds it at run start), so flipping mid-run only affects the next task.
            try
            {
                var accessMode = App.Services.Resolve<IAgentAccessMode>();
                void RefreshAccessModeSwitch() =>
                    ChatPanel.InputBoxControl.SetAccessMode(accessMode.Current == AgentAccessMode.AllowEdit);
                RefreshAccessModeSwitch();
                ChatPanel.InputBoxControl.AccessModeToggleRequested += (s, e) =>
                    accessMode.Current = accessMode.Current == AgentAccessMode.AllowEdit
                        ? AgentAccessMode.ReadOnly
                        : AgentAccessMode.AllowEdit;
                accessMode.Changed += mode =>
                    Dispatcher.BeginInvoke(new Action(() =>
                        ChatPanel.InputBoxControl.SetAccessMode(mode == AgentAccessMode.AllowEdit)));
                LanguageManager.Instance.LanguageChanged += lang =>
                    Dispatcher.BeginInvoke(new Action(RefreshAccessModeSwitch));
            }
            catch (Exception ex)
            {
                AppLogger.Log("[AccessMode] could not wire the input-box switch: " + ex.Message);
            }

            // Code-block header actions: insert into the prompt box / save into the selected Explorer folder.
            try
            {
                var codeBlockAggregator = App.Services.Resolve<IEventAggregator>();
                codeBlockAggregator.GetEvent<InsertCodeToInputEvent>()
                    .Subscribe(text => ChatPanel.InputBoxControl.InsertTextAtCaret(text), ThreadOption.UIThread);
                codeBlockAggregator.GetEvent<SaveCodeBlockEvent>()
                    .Subscribe(SaveCodeBlockToExplorerFolder, ThreadOption.UIThread);
            }
            catch (Exception ex)
            {
                AppLogger.Log("[CodeBlock] could not wire the header actions: " + ex.Message);
            }
            
            // Wire the @-reference search to ProjectIndexService
            ChatPanel.InputBoxControl.SearchMentionItemsAsync = (filter) =>
            {
                if (!_viewModel.HasIndexService)
                    return Task.FromResult(new List<Controls.MentionItem>());

                var projectKey = _viewModel.GetProjectCacheKey();
                if (string.IsNullOrWhiteSpace(projectKey))
                    return Task.FromResult(new List<Controls.MentionItem>());

                var hits = _viewModel.SearchIndex(projectKey, filter, 10);
                return Task.FromResult(hits?.Select(h => new Controls.MentionItem
                {
                    Name = h.BlockName,
                    RefType = h.BlockType ?? "Block",
                    Path = h.Summary ?? ""
                }).ToList() ?? new List<Controls.MentionItem>());
            };

            // After an @ mention is selected, automatically add the block content to the AI references
            ChatPanel.InputBoxControl.MentionSelected += async (s, item) =>
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Name)) return;
                // Find the corresponding block in the TIA project tree
                var block = FindBlockInTree(item.Name);
                if (block != null)
                {
                    // Same feedback as the tree context menu: pending chip + wait cursor while the
                    // export runs, so a slow STA export never looks like a missed click.
                    await AddReferenceToAiWithFeedbackAsync("Mention", item.Name,
                        () => BuildAiReferenceFromTreeTagAsync(block));
                }
            };
            
            InitializeModelSelector();
            
            // Bind ChatViewModel.IsGenerating to AgentInputBox.IsGenerating
            // → during streaming the send button automatically switches to a red stop button (white square in the middle); clicking it triggers StopRequested to interrupt
            var isGeneratingBinding = new System.Windows.Data.Binding(nameof(ChatViewModel.IsGenerating))
            {
                Source = _chatViewModel,
                Mode = System.Windows.Data.BindingMode.OneWay
            };
            ChatPanel.InputBoxControl.SetBinding(Controls.AgentInputBox.IsGeneratingProperty, isGeneratingBinding);

            // Listen for ChatViewModel target group path changes → refresh the status bar chip display
            _chatViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ChatViewModel.TargetBlockGroupPath) ||
                    e.PropertyName == nameof(ChatViewModel.TargetTypeGroupPath))
                {
                    RefreshStatusBarTargetChips();
                }
                else if (e.PropertyName == nameof(ChatViewModel.TokenUsage))
                {
                    ChatPanel.InputBoxControl.UpdateTokenUsage(_chatViewModel.TokenUsage);
                }
                else if (e.PropertyName == nameof(ChatViewModel.IsGenerating))
                {
                    // Propagate to all ChatMessage.IsGenerating so CanUndo disables during streaming
                    OnChatIsGeneratingChanged(_chatViewModel.IsGenerating);
                }
            };
            RefreshStatusBarTargetChips();
            
            // Subscribe to ChatViewModel events
            _chatViewModel.ScrollToBottomRequested += ScrollChatToBottom;
            _chatViewModel.ShowCodeViewerRequested += (name, type, content) => _viewModel.ShowCodeViewer(name, type, content);
            _chatViewModel.CopyToClipboardRequested += (code) => 
            {
                Clipboard.SetText(code);
                _viewModel.StatusText = "Code copied to clipboard!";
            };
            _chatViewModel.ImportRequested += async (code, name, type, path) =>
                await _projectViewModel.ImportCodeAsync(code, name, type, path);
            _chatViewModel.ReferencesChanged += RefreshReferenceChips;
            // Non-blocking informational messages (e.g. the tool-call soft limit) render as a
            // toast over the chat area instead of a modal dialog, so the agent keeps running.
            _chatViewModel.ToastRequested += (text) => ChatPanel.ShowToast(text);

            // Listen for Pending Write (the Agent finished generating but the user has not chosen a chip):
            // show a three-way choice dialog, then take the corresponding branch based on the choice
            _chatViewModel.PendingWriteAwaitingChoice += OnPendingWriteAwaitingChoice;
            _chatViewModel.OpenBlockGroupPickerRequested += OnOpenBlockGroupPickerRequested;
            _chatViewModel.SavePendingWriteToLocalRequested += OnSavePendingWriteToLocalRequested;
        }

        private void CopyCode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ChatMessage msg)
            {
                _chatViewModel.CopyCodeFromMessage(msg);
            }
        }

        private void PreviewCode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ChatMessage msg)
            {
                _chatViewModel.PreviewCodeFromMessage(msg);
            }
        }

        private void BindChatPanelEvents()
        {
            ChatPanel.NewChatClick += NewChat_Click;
            ChatPanel.ChatHistoryClick += ChatHistory_Click;
            ChatPanel.GenerateRequested += Generate_Click;
            ChatPanel.GenerateWithImagesRequested += GenerateWithImages_Click;
            ChatPanel.StopClick += Stop_Click;
            ChatPanel.ClosePreviewClick += ClosePreview_Click;
            ChatPanel.CopyCodeClick += (s, msg) => CopyCode_Click(msg, null);
            ChatPanel.PreviewCodeClick += (s, msg) => PreviewCode_Click(msg, null);
            ChatPanel.ConfirmStepClick += (s, step) => _viewModel.ConfirmStepCommand.Execute(step);
            ChatPanel.CancelStepClick += (s, step) => _viewModel.CancelStepCommand.Execute(step);
            ChatPanel.QuickCommandInvoked += QuickCommand_Click;
            ChatPanel.MessageListControl.DevTraceRequested += DevTraceRequested_Click;
            
            // Wire up input events for the prompt textbox inside AgentInputBox
            ChatPanel.InputBoxControl.PromptTextBox.PreviewKeyDown += txtPrompt_PreviewKeyDown;
            ChatPanel.InputBoxControl.PromptTextBox.PreviewDragOver += txtPrompt_PreviewDragOver;
            ChatPanel.InputBoxControl.PromptTextBox.Drop += txtPrompt_Drop;

            // Manually compress chat history
            ChatPanel.InputBoxControl.CompressRequested += async (s, e) =>
            {
                await _chatViewModel.CompressHistoryAsync();
            };
        }

        private void txtPrompt_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void txtPrompt_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    var addedNames = new List<string>();
                    foreach (var file in files)
                    {
                        var packed = _chatViewModel.BuildReferenceFromFile(file);
                        if (string.IsNullOrWhiteSpace(packed)) continue;
                        AddReferenceToAi("FileDrop", packed, Path.GetFileName(file));
                        addedNames.Add(Path.GetFileName(file));
                    }
                    if (addedNames.Count > 0)
                    {
                        _viewModel.StatusText = $"Status: Added file references ({_chatViewModel.ReferenceCount})";
                    }
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                string text = e.Data.GetData(DataFormats.Text) as string;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    string title = text.Split('\n').FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(title)) title = "Tree/Code Reference";
                    if (title.Length > 48) title = title.Substring(0, 48);
                    AddReferenceToAi("TreeDrag", text, title);
                }
            }
            e.Handled = true;
        }

        private void txtPrompt_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Shift)
                {
                    return;
                }
                else if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
                {
                    e.Handled = true;
                    // AgentInputBox's internal PreviewKeyDown normally already handles Enter;
                    // reaching here is the fallback branch, so just read the current input box text.
                    Generate_Click(this, ChatPanel.InputBoxControl?.PromptTextBox?.Text ?? "");
                }
            }
        }



        private void NewChat_Click(object sender, RoutedEventArgs e)
        {
            _chatViewModel.StartNewSession();
        }

        private void ChatHistory_Click(object sender, RoutedEventArgs e)
        {
            var sessions = _chatViewModel.GetAllSessions();

            if (sessions.Count == 0)
            {
                MessageBox.Show(
                    LanguageManager.Instance.GetString("Chat.History.NoHistory"),
                    LanguageManager.Instance.GetString("Chat.History.TabTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Refresh button labels for current language
            lblChatHistoryTabTitle.Text = LanguageManager.Instance.GetString("Chat.History.TabTitle");
            btnManageChatHistory.Content = LanguageManager.Instance.GetString("Chat.History.Manage");
            btnSelectAllChatHistory.Content = LanguageManager.Instance.GetString("Chat.History.SelectAll");
            btnInvertSelectionChatHistory.Content = LanguageManager.Instance.GetString("Chat.History.Invert");
            btnDeleteSelectedChatHistory.Content = LanguageManager.Instance.GetString("Chat.History.DeleteSelected");
            btnFinishManageChatHistory.Content = LanguageManager.Instance.GetString("Chat.History.Finish");

            PopulateChatHistoryList(sessions);

            if (tabChatHistory != null)
            {
                tabChatHistory.Visibility = Visibility.Visible;
                tabsCode.Visibility = Visibility.Visible;
                tabsCode.SelectedItem = tabChatHistory;
                lblNoCode.Visibility = Visibility.Collapsed;
            }
        }

        private bool _isChatHistoryManageMode;
        private ObservableCollection<ChatSessionItemViewModel> _chatHistoryItems;

        private void PopulateChatHistoryList(List<ChatSession> sessions)
        {
            _chatHistoryItems = new ObservableCollection<ChatSessionItemViewModel>(
                sessions.Select(s => new ChatSessionItemViewModel { Session = s, IsManageMode = _isChatHistoryManageMode }));
            lstChatHistory.ItemsSource = _chatHistoryItems;
        }

        private void ChatHistoryItem_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isChatHistoryManageMode) return;
            if (lstChatHistory.SelectedItem is ChatSessionItemViewModel selectedItem && selectedItem.Session != null)
            {
                _chatViewModel.LoadSession(selectedItem.Session);
                CloseChatHistory_Click(sender, e);
            }
        }

        private void ManageChatHistory_Click(object sender, RoutedEventArgs e)
        {
            _isChatHistoryManageMode = true;
            btnManageChatHistory.Visibility = Visibility.Collapsed;
            btnSelectAllChatHistory.Visibility = Visibility.Visible;
            btnInvertSelectionChatHistory.Visibility = Visibility.Visible;
            btnDeleteSelectedChatHistory.Visibility = Visibility.Visible;
            btnFinishManageChatHistory.Visibility = Visibility.Visible;

            if (_chatHistoryItems != null)
            {
                foreach (var item in _chatHistoryItems)
                {
                    item.IsManageMode = true;
                }
            }
        }

        private void FinishManageChatHistory_Click(object sender, RoutedEventArgs e)
        {
            _isChatHistoryManageMode = false;
            btnManageChatHistory.Visibility = Visibility.Visible;
            btnSelectAllChatHistory.Visibility = Visibility.Collapsed;
            btnInvertSelectionChatHistory.Visibility = Visibility.Collapsed;
            btnDeleteSelectedChatHistory.Visibility = Visibility.Collapsed;
            btnFinishManageChatHistory.Visibility = Visibility.Collapsed;

            if (_chatHistoryItems != null)
            {
                foreach (var item in _chatHistoryItems)
                {
                    item.IsManageMode = false;
                    item.IsSelected = false;
                }
            }
        }

        private void SelectAllChatHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_chatHistoryItems == null) return;
            foreach (var item in _chatHistoryItems)
            {
                item.IsSelected = true;
            }
        }

        private void InvertSelectionChatHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_chatHistoryItems == null) return;
            foreach (var item in _chatHistoryItems)
            {
                item.IsSelected = !item.IsSelected;
            }
        }

        private void DeleteSelectedChatHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_chatHistoryItems == null) return;
            var selectedItems = _chatHistoryItems.Where(i => i.IsSelected).ToList();
            if (selectedItems.Count == 0)
            {
                MessageBox.Show(
                    LanguageManager.Instance.GetString("Chat.History.NoSelection"),
                    LanguageManager.Instance.GetString("Chat.History.TabTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                string.Format(LanguageManager.Instance.GetString("Chat.History.DeleteConfirm"), selectedItems.Count),
                LanguageManager.Instance.GetString("Chat.History.DeleteTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            foreach (var item in selectedItems)
            {
                _chatViewModel.DeleteSession(item.Session.Id);
            }

            var sessions = _chatViewModel.GetAllSessions();
            if (sessions.Count == 0)
            {
                CloseChatHistory_Click(sender, e);
            }
            else
            {
                PopulateChatHistoryList(sessions);
            }
        }

        private void CloseChatHistory_Click(object sender, RoutedEventArgs e)
        {
            if (tabChatHistory != null) tabChatHistory.Visibility = Visibility.Collapsed;
            UpdateNoCodeVisibility();
        }

        private void LoadChatSession(ChatSession session)
        {
            _chatViewModel.LoadSession(session);
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _chatViewModel.StopGeneration();
        }

        private ScrollViewer GetScrollViewer(DependencyObject obj)
        {
            if (obj is ScrollViewer) return (ScrollViewer)obj;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var result = GetScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(obj, i));
                if (result != null) return result;
            }
            return null;
        }

        private void ScrollChatToBottom()
        {
            // _chatViewModel.ScrollToBottomRequested is already handled in ChatPanelControl
            // which listens to TraeMessageList.ScrollToBottomRequested.
            // Or we can manually scroll it if needed, but TraeMessageList has auto-scroll.
        }

        private async void Generate_Click(object sender, string promptFromEvent)
        {
            // Prefer the prompt carried by the event (the input text at the moment SendRequested fired),
            // to avoid getting an empty string because the InputBox has already been cleared.
            string prompt = promptFromEvent;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                // Fallback: if the caller passed nothing (e.g. a legacy path), read from the InputBox instead
                prompt = ChatPanel.InputBoxControl?.PromptTextBox?.Text;
            }
            AppLogger.Log($"[CHAT] Generate_Click prompt='{(prompt ?? "<null>")}', length={(prompt?.Length ?? 0)}");
            if (string.IsNullOrWhiteSpace(prompt))
            {
                AppLogger.Log("[CHAT] Generate_Click early-return: empty prompt");
                return;
            }

            var aiReferences = _chatViewModel.GetReferencesForGeneration();
            await _chatViewModel.GenerateAsync(prompt, aiReferences);
        }

        private async void GenerateWithImages_Click(object sender, (string text, List<byte[]> images) args)
        {
            string prompt = args.text;
            var images = args.images;
            AppLogger.Log($"[CHAT] GenerateWithImages_Click prompt='{(prompt ?? "<null>")}', length={(prompt?.Length ?? 0)}, images={images?.Count ?? 0}");
            if (string.IsNullOrWhiteSpace(prompt) && (images == null || images.Count == 0))
            {
                AppLogger.Log("[CHAT] GenerateWithImages_Click early-return: empty prompt and no images");
                return;
            }

            var aiReferences = _chatViewModel.GetReferencesForGeneration();
            await _chatViewModel.GenerateWithImagesAsync(prompt, images, aiReferences);
        }

        private void ClosePreview_Click(object sender, RoutedEventArgs e)
        {
            ChatPanel.PreviewPopup.IsOpen = false;
        }

        private void QuickCommand_Click(object sender, string intent)
        {
            // Set a status hint based on the quick command
            var lm = LanguageManager.Instance;
            var intentDescriptions = new Dictionary<string, string>
            {
                ["GenerateAndFix"] = lm.GetString("Cmd.Mode.Generate"),
                ["ModifyCode"] = lm.GetString("Cmd.Mode.Modify"),
                ["ExplainCode"] = lm.GetString("Cmd.Mode.Explain"),
                ["Diagnose"] = lm.GetString("Cmd.Mode.Diagnose"),
                ["Compile"] = lm.GetString("Cmd.Mode.Compile"),
                ["TraceVariable"] = lm.GetString("Cmd.Mode.Trace")
            };

            if (intentDescriptions.TryGetValue(intent, out var desc))
            {
                _viewModel.StatusText = desc;
            }
        }

        private void InitializeModelSelector()
        {
            // Load the AI configuration
            _aiConfig = AIConfig.Load();

            RefreshAvailableModelList();

            // Subscribe to the model-changed event (subscribe only once)
            ChatPanel.ModelChanged -= OnModelChanged;
            ChatPanel.ModelChanged += OnModelChanged;

            // Session-level model: when loading a session, if the model recorded by that session differs from the current one,
            // switch the model selector to that session's model (which goes through ModelChanged → OnModelChanged
            // and the full runtime hot-update chain).
            if (_chatViewModel != null)
            {
                _chatViewModel.SessionModelRestoreRequested -= OnSessionModelRestoreRequested;
                _chatViewModel.SessionModelRestoreRequested += OnSessionModelRestoreRequested;
            }

            // Refresh the input box dropdown after the Settings page is saved
            _eventAggregator.GetEvent<ConfigurationSavedEvent>().Subscribe(config =>
            {
                _aiConfig = config;
                RefreshAvailableModelList();
            }, ThreadOption.UIThread);
        }

        /// <summary>
        /// Rebuild the input box dropdown model list (called again after Model Config is saved).
        /// Sources: Ollama dynamic fetch + current Provider built-ins + user custom (AIConfig.CustomModels)
        /// </summary>
        private async void RefreshAvailableModelList()
        {
            if (_aiConfig == null) return;

            var models = new List<string>();

            // 1. Dynamically fetch installed models from the local Ollama
            try
            {
                var ollamaHost = _viewModel?.OllamaHostService;
                if (ollamaHost != null)
                {
                    var installedModels = await ollamaHost.GetInstalledModelsAsync();
                    foreach (var m in installedModels)
                    {
                        if (!models.Contains(m)) models.Add(m);
                    }
                    AppLogger.Log($"[ModelList] Ollama installed models: {string.Join(", ", installedModels)}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ModelList] Failed to fetch Ollama models: {ex.Message}");
            }

            // If Ollama is unavailable, there are no local models to show
            if (models.Count == 0)
            {
                AppLogger.Log("[ModelList] Ollama unreachable, no local models to show.");
            }

            // 2. Built-in models of the current Provider — only when an API key is configured.
            //    Without a key, these cloud models cannot be called, so showing them would mislead the user.
            if (!string.IsNullOrWhiteSpace(_aiConfig.ApiKey))
            {
                var providerModels = _aiConfig.GetCurrentProviderModels();
                foreach (var m in providerModels)
                {
                    if (!models.Contains(m.Name)) models.Add(m.Name);
                }
            }
            else
            {
                AppLogger.Log($"[ModelList] Provider '{_aiConfig.Provider}' has no API key configured; its built-in models are hidden.");
            }

            // 3. Custom models the user saved in Model Config
            if (_aiConfig.CustomModels != null)
            {
                foreach (var name in _aiConfig.CustomModels)
                {
                    if (!string.IsNullOrWhiteSpace(name) && !models.Contains(name))
                    {
                        models.Add(name);
                    }
                }
            }

            // Item to be selected currently: prefer Config.Model (the effective value), fall back to SelectedModel
            string current = !string.IsNullOrWhiteSpace(_aiConfig.Model) ? _aiConfig.Model : _aiConfig.SelectedModel;

            // If current is not in the list, add it and select it as well — but never
            // re-introduce a cloud model whose provider has no API key (that would bypass
            // the key filter above). Ollama local models and user custom models are always allowed.
            if (!string.IsNullOrWhiteSpace(current) && !models.Contains(current))
            {
                bool isOllama = AICore.AIConfig.IsLocalLlmModel(current);
                bool isCustom = _aiConfig.CustomModels != null &&
                                _aiConfig.CustomModels.Any(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase));
                bool cloudNeedsKey = !isOllama && !isCustom;

                if (!cloudNeedsKey || !string.IsNullOrWhiteSpace(_aiConfig.ApiKey))
                {
                    models.Add(current);
                }
            }

            ChatPanel.InputBoxControl.SetAvailableModels(models, current);
            // Show/hide the image button based on whether the current model supports vision
            ChatPanel.InputBoxControl.VisionSupported = AICore.AIConfig.IsVisionModel(current ?? "");
        }

        /// <summary>
        /// On session restore, switch the selector to the model recorded by that session. Driving the selector's selected item is enough;
        /// its ModelChanged event triggers OnModelChanged to complete the runtime hot-update and re-recording.
        /// If the selector's selected item is already the target model (no ModelChanged fires), do a direct fallback hot-update once.
        /// </summary>
        private void OnSessionModelRestoreRequested(string modelName, string provider, string endpoint)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return;
            try
            {
                // 1) Sync the dropdown selected item (SetAvailableModels suppresses SelectionChanged,
                //    so it only updates the UI and does not fire ModelChanged).
                var models = ChatPanel.InputBoxControl.GetAvailableModels()?.ToList()
                             ?? new List<string>();
                if (!models.Contains(modelName)) models.Add(modelName);
                ChatPanel.InputBoxControl.SetAvailableModels(models, modelName);
                ChatPanel.InputBoxControl.VisionSupported = AICore.AIConfig.IsVisionModel(modelName);

                // 2) Because SetAvailableModels does not fire ModelChanged, explicitly run the
                //    full model switch chain here (compute runtime provider/endpoint/apiKey +
                //    UpdateRuntimeConfig + record back to the current session). OnModelChanged is idempotent.
                OnModelChanged(this, modelName);
                AppLogger.Log($"[Session] Restored model for loaded session: {modelName} ({provider})");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Session] Model restore failed: {ex.Message}");
            }
        }

        private void OnModelChanged(object sender, string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return;

            // Reload from disk to ensure we get the latest values saved from the Settings page
            _aiConfig = AIConfig.Load();
            _aiConfig.SelectedModel = modelName;

            // Check whether it is a built-in model of the current Provider (match by Name, since the dropdown shows Name)
            var providerModels = _aiConfig.GetCurrentProviderModels();
            var matched = providerModels.FirstOrDefault(m => m.Name == modelName);
            if (matched != null)
            {
                _aiConfig.Model = matched.Id;
            }
            else
            {
                // Local LLM or custom model, Name is the Id
                _aiConfig.Model = modelName;
            }

            // Compute the runtime configuration: Ollama models use the local endpoint, other models reuse the user-configured cloud connection
            // Note: do not modify _aiConfig's Provider/Endpoint/ApiKey, to avoid overwriting the user's cloud configuration
            bool isOllamaModel = AIConfig.IsLocalLlmModel(modelName);
            bool isCustomModel = _aiConfig.CustomModels != null &&
                _aiConfig.CustomModels.Any(m => string.Equals(m, modelName, StringComparison.OrdinalIgnoreCase));

            string runtimeProvider, runtimeEndpoint, runtimeApiKey;
            if (isOllamaModel && !isCustomModel)
            {
                runtimeProvider = "Ollama";
                runtimeEndpoint = "http://localhost:11434/v1/chat/completions";
                runtimeApiKey = "";
                AppLogger.Log($"[ModelSwitch] Using Ollama runtime for '{modelName}'");
            }
            else
            {
                runtimeProvider = _aiConfig.Provider;
                runtimeEndpoint = _aiConfig.Endpoint;
                runtimeApiKey = _aiConfig.ApiKey;
            }

            // Persist only the model selection field, do not modify the cloud connection configuration
            _aiConfig.Save();

            // Hot-update the runtime model of the current AI engine instance (otherwise the next send still uses the stale value from startup)
            _viewModel?.AIService?.UpdateRuntimeConfig(
                model: _aiConfig.Model,
                provider: runtimeProvider,
                endpoint: runtimeEndpoint,
                apiKey: runtimeApiKey);

            // Session-level model: record this switch on the current session, so it can be restored when switching back to that session.
            _chatViewModel?.RecordModelForCurrentSession(_aiConfig.Model, runtimeProvider, runtimeEndpoint);

            // Local Ollama model: sync to the ILocalLlmService singleton,
            // otherwise local chat/analysis/ReAct still uses the small model defaulted at startup (e.g. qwen2.5:0.5b),
            // resulting in "you chose 9b but it reasons with 0.5b".
            if (isOllamaModel && !isCustomModel)
            {
                try
                {
                    var localLlm = App.Services?.Resolve<ILocalLlmService>();
                    localLlm?.SetModel(modelName);
                    AppLogger.Log($"[ModelSwitch] Local LLM model synced to '{modelName}'");
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[ModelSwitch] Failed to sync local LLM model: {ex.Message}");
                }
            }

            _viewModel.StatusText = $"Status: Model switched to {modelName}";

            // Update the image button visibility
            ChatPanel.InputBoxControl.VisionSupported = AIConfig.IsVisionModel(modelName);
        }

        private void ImportFromChat_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ChatMessage msg)
            {
                _chatViewModel.ImportCodeFromMessage(msg);
            }
        }

        private string ExtractCodeFromMessage(ChatMessage msg)
        {
            return _chatViewModel.ExtractCodeFromMessage(msg);
        }

        // ---- Pending Write three-way choice dialog handling ----

        private void OnPendingWriteAwaitingChoice(string blockName, string blockType)
        {
            // ChatViewModel raises this on a thread pool thread, so the event may not be on the UI thread
            Dispatcher.Invoke(() =>
            {
                var dlg = new TIAOpennessStudio.Services.ImportTargetChoiceDialog(blockName, blockType)
                {
                    Owner = this
                };
                dlg.ShowDialog();
                switch (dlg.Choice)
                {
                    case TIAOpennessStudio.Services.ImportTargetChoice.TiaRoot:
                        _chatViewModel.ApplyPendingWriteToTiaRoot();
                        break;
                    case TIAOpennessStudio.Services.ImportTargetChoice.PickGroup:
                        _chatViewModel.RequestPickGroupForPendingWrite();
                        break;
                    case TIAOpennessStudio.Services.ImportTargetChoice.LocalFile:
                        _chatViewModel.RequestSavePendingWriteToLocal();
                        break;
                    default:
                        // Cancel: the code is already in the chat, the user can manually Copy/Import later, nothing is written
                        _chatViewModel.ClearPendingWrite();
                        break;
                }
            });
        }

        private void OnOpenBlockGroupPickerRequested()
        {
            // Switch to the Block kind and pop up a picker for the user to select a group. Once chosen, SelectedItemChanged fires
            // → that handler calls _chatViewModel.ConsumePendingPickGroupAndImport()
            Dispatcher.InvokeAsync(async () =>
            {
                await OpenStatusBarTargetPopupAsync(TargetGroupKind.Block, StatusBlockTargetChip);
            });
        }

        private void OnSavePendingWriteToLocalRequested(string suggestedName, string code)
        {
            Dispatcher.Invoke(() =>
            {
                // Default GeneratedCode\<BlockName>.scl, under the workspace root
                string defaultDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeneratedCode");
                try
                {
                    if (!System.IO.Directory.Exists(defaultDir))
                        System.IO.Directory.CreateDirectory(defaultDir);
                }
                catch { /* Creation failure is fine; SaveFileDialog still lets the user pick another location */ }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "SCL files (*.scl)|*.scl|All files (*.*)|*.*",
                    DefaultExt = ".scl",
                    FileName = suggestedName ?? "AI_Generated_Block.scl",
                    InitialDirectory = defaultDir,
                    OverwritePrompt = true
                };
                if (dlg.ShowDialog(this) == true)
                {
                    try
                    {
                        System.IO.File.WriteAllText(dlg.FileName, code ?? "", System.Text.Encoding.UTF8);
                        _viewModel.StatusText = $"Status: Saved to {dlg.FileName}";
                        AppLogger.Log($"[CHAT] Pending write saved to local file: {dlg.FileName}");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to save file:\n{ex.Message}", "Save error", MessageBoxButton.OK, MessageBoxImage.Error);
                        AppLogger.Log($"[CHAT] Save local file failed: {ex.Message}");
                    }
                }
            });
        }

        // ---- Per-message Undo (write-back revert) ----

        /// <summary>
        /// Called by OpennessService.BlockWritten after every successful write-back.
        /// Finds the most recent assistant message and attaches the snapshot so the
        /// ↩ Undo button becomes visible for that message only.
        /// Also propagates IsGenerating = false to all messages so the button is enabled.
        /// </summary>
        private void OnBlockWritten(object sender, BlockWrittenEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var messages = _chatViewModel?.ChatMessages;
                if (messages == null) return;

                // Mark all messages as not-generating so any previously disabled undo buttons re-enable
                foreach (var m in messages)
                    m.IsGenerating = false;

                // Attach snapshot to the most-recent assistant message
                for (int i = messages.Count - 1; i >= 0; i--)
                {
                    if (messages[i].Role == "assistant")
                    {
                        messages[i].UndoBlockName = e.BlockName;
                        messages[i].UndoBlockType = e.BlockType;
                        messages[i].UndoSnapshot  = e.SnapshotXml; // null = brand-new block, CanUndo stays false
                        break;
                    }
                }
            });
        }

        /// <summary>
        /// Called when IsGenerating changes on the ChatViewModel.
        /// Propagates the generating state to every ChatMessage so CanUndo is correct
        /// while a response is streaming.
        /// </summary>
        private void OnChatIsGeneratingChanged(bool isGenerating)
        {
            Dispatcher.Invoke(() =>
            {
                var messages = _chatViewModel?.ChatMessages;
                if (messages == null) return;
                foreach (var m in messages)
                    m.IsGenerating = isGenerating;
            });
        }

        /// <summary>
        /// Handles the UndoMessageCommand raised by MainViewModel when the user clicks
        /// the ↩ button on an assistant message bubble.
        /// Shows the diff (current vs snapshot), then writes back on confirmation.
        /// </summary>
        private async void OnUndoMessageRequested(ChatMessage msg)
        {
            if (msg == null || !msg.CanUndo) return;
            if (!await EnsurePlcSoftwareReadyAsync()) return;

            // Export current TIA content for the left side of the diff
            string currentCode;
            try
            {
                currentCode = await _viewModel.TiaService.ExportBlockCodeAsync(msg.UndoBlockName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(LanguageManager.Instance.GetString("Chat.Undo.NoSnapshot"), msg.UndoBlockName) + $"\n{ex.Message}",
                    "Undo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string diffTitle = string.Format(
                LanguageManager.Instance.GetString("Chat.Undo.DiffTitle"),
                msg.UndoBlockName);

            // Left = current code in TIA, Right = snapshot to restore
            bool confirmed = await ShowDiffAndWaitAsync(currentCode, msg.UndoSnapshot, diffTitle);
            if (!confirmed)
            {
                _chatViewModel.AddAssistantMessage(LanguageManager.Instance.GetString("Chat.Undo.Cancelled"));
                return;
            }

            try
            {
                await _viewModel.TiaService.ImportBlockCodeAsync(
                    msg.UndoBlockName, msg.UndoBlockType, msg.UndoSnapshot);

                // Clear snapshot on this message — it has been consumed. The write-back
                // itself will produce a new BlockWritten event that attaches a fresh snapshot
                // to the newly added "Reverted" assistant message.
                msg.UndoSnapshot  = null;
                msg.UndoBlockName = null;
                msg.UndoBlockType = null;

                _chatViewModel.AddAssistantMessage(
                    string.Format(LanguageManager.Instance.GetString("Chat.Undo.Confirmed"), msg.UndoBlockName ?? "block"));
            }
            catch (Exception ex)
            {
                _chatViewModel.AddAssistantMessage($"❌ {ex.Message}");
            }
        }

        // ---- Status Bar target group chips & popup ----

        private TargetGroupKind _activeStatusPopupKind;

        private void RefreshStatusBarTargetChips()
        {
            if (_chatViewModel == null) return;
            string blockPath = _chatViewModel.TargetBlockGroupPath ?? "";
            string typePath = _chatViewModel.TargetTypeGroupPath ?? "";

            if (StatusBlockTargetText != null)
            {
                StatusBlockTargetText.Text = string.IsNullOrEmpty(blockPath)
                    ? "Blocks: <root>"
                    : $"Blocks: {blockPath}";
            }
            if (StatusTypeTargetText != null)
            {
                StatusTypeTargetText.Text = string.IsNullOrEmpty(typePath)
                    ? "Types: <root>"
                    : $"Types: {typePath}";
            }
            if (StatusBlockTargetClearBtn != null)
            {
                StatusBlockTargetClearBtn.Visibility = string.IsNullOrEmpty(blockPath)
                    ? Visibility.Collapsed : Visibility.Visible;
            }
            if (StatusTypeTargetClearBtn != null)
            {
                StatusTypeTargetClearBtn.Visibility = string.IsNullOrEmpty(typePath)
                    ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private async void StatusBlockTargetChip_Click(object sender, MouseButtonEventArgs e)
        {
            await OpenStatusBarTargetPopupAsync(TargetGroupKind.Block, StatusBlockTargetChip);
        }

        private async void StatusTypeTargetChip_Click(object sender, MouseButtonEventArgs e)
        {
            await OpenStatusBarTargetPopupAsync(TargetGroupKind.Type, StatusTypeTargetChip);
        }

        private void StatusBlockTargetClear_Click(object sender, RoutedEventArgs e)
        {
            if (_chatViewModel != null) _chatViewModel.TargetBlockGroupPath = "";
            e.Handled = true;
        }

        private void StatusTypeTargetClear_Click(object sender, RoutedEventArgs e)
        {
            if (_chatViewModel != null) _chatViewModel.TargetTypeGroupPath = "";
            e.Handled = true;
        }

        private async System.Threading.Tasks.Task OpenStatusBarTargetPopupAsync(TargetGroupKind kind, UIElement anchor)
        {
            _activeStatusPopupKind = kind;

            // Build the group tree on the STA host thread (OpennessService.*GroupTreeAsync) and
            // consume only plain-data DTOs here. The former BuildBlockGroupNodes/BuildTypeGroupNodes
            // walked PlcSoftware.BlockGroup/TypeGroup COM directly on the UI thread, which threw
            // "Cross-thread operation is not valid in Openness within STA".
            IList<TargetGroupNodeDto> rootDtos;
            try
            {
                rootDtos = kind == TargetGroupKind.Block
                    ? await _viewModel.TiaService.GetBlockGroupTreeAsync()
                    : await _viewModel.TiaService.GetTypeGroupTreeAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[TargetGroupTree] Build tree failed: {ex.Message}");
                rootDtos = new List<TargetGroupNodeDto>();
            }

            var roots = rootDtos?.Select(MapTargetGroupDto).ToList();

            var collection = new ObservableCollection<TargetGroupNode>();
            collection.Add(new TargetGroupNode { Name = "<Project root>", Path = "", IsRoot = true });
            if (roots != null)
            {
                foreach (var r in roots) collection.Add(r);
            }

            StatusTargetGroupTreeView.ItemsSource = collection;
            StatusTargetPopupTitle.Text = kind == TargetGroupKind.Block
                ? "Select Blocks group"
                : "Select Types group";
            StatusTargetGroupPopup.PlacementTarget = anchor;
            StatusTargetGroupPopup.IsOpen = true;
        }

        private void StatusTargetGroupTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_chatViewModel == null) return;
            if (!(e.NewValue is TargetGroupNode node)) return;

            string path = node.IsRoot ? "" : node.Path;
            if (_activeStatusPopupKind == TargetGroupKind.Block)
                _chatViewModel.TargetBlockGroupPath = path;
            else
                _chatViewModel.TargetTypeGroupPath = path;

            StatusTargetGroupPopup.IsOpen = false;

            // If a Pending Write was waiting for the user to choose a group, trigger the automatic import here
            _chatViewModel.ConsumePendingPickGroupAndImport();
        }

        // ---- Target Group Tree builders ----

        /// <summary>
        /// Map a plain-data TargetGroupNodeDto (built on the STA host thread) onto the UI-side
        /// TargetGroupNode tree. No COM access — purely data.
        /// </summary>
        private static TargetGroupNode MapTargetGroupDto(TargetGroupNodeDto dto)
        {
            var node = new TargetGroupNode { Name = dto.Name, Path = dto.Path, IsRoot = dto.IsRoot };
            if (dto.Children != null)
            {
                foreach (var child in dto.Children)
                    node.Children.Add(MapTargetGroupDto(child));
            }
            return node;
        }

        #region Dev Trace Tab

        private DevTraceData _currentDevTrace;

        private void DevTraceRequested_Click(object sender, DevTraceData trace)
        {
            if (trace == null) return;
            _currentDevTrace = trace;
            ShowDevTraceInTab();
        }

        private void ShowDevTraceInTab()
        {
            if (_currentDevTrace == null) return;

            DevTraceModelLabel.Text = string.IsNullOrEmpty(_currentDevTrace.ModelName)
                ? "" : $"Model: {_currentDevTrace.ModelName}";
            DevTraceDurationLabel.Text = _currentDevTrace.TotalDuration.TotalSeconds > 0
                ? $"Duration: {_currentDevTrace.TotalDuration.TotalSeconds:F1}s"
                : "";

            // Default to Raw Prompt tab
            DevTraceContent.Text = _currentDevTrace.RawPrompt ?? "(no prompt data)";

            // Reset tab styles
            TabRawPrompt.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
            TabRawPrompt.BorderThickness = new Thickness(0);
            TabRawPrompt.Foreground = System.Windows.Media.Brushes.White;

            TabToolCalls.Background = System.Windows.Media.Brushes.Transparent;
            TabToolCalls.BorderThickness = new Thickness(1);
            TabToolCalls.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");

            TabTiaApi.Background = System.Windows.Media.Brushes.Transparent;
            TabTiaApi.BorderThickness = new Thickness(1);
            TabTiaApi.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");

            tabDevTrace.Visibility = Visibility.Visible;
            tabsCode.Visibility = Visibility.Visible;
            tabsCode.SelectedItem = tabDevTrace;
            if (lblNoCode != null) lblNoCode.Visibility = Visibility.Collapsed;
        }

        private void DevTraceTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || _currentDevTrace == null) return;

            var tag = btn.Tag as string;

            // Reset all tab button styles
            if (btn.Parent is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Button tabBtn)
                    {
                        bool isActive = tabBtn == btn;
                        tabBtn.Background = isActive
                            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                            : System.Windows.Media.Brushes.Transparent;
                        tabBtn.BorderThickness = isActive ? new Thickness(0) : new Thickness(1);
                        tabBtn.Foreground = isActive
                            ? System.Windows.Media.Brushes.White
                            : (System.Windows.Media.Brush)FindResource("TextBrush");
                    }
                }
            }

            DevTraceContent.Text = tag switch
            {
                "RawPrompt" => _currentDevTrace.RawPrompt ?? "(no prompt data)",
                "ToolCalls" => _currentDevTrace.ToolInvocationsJson ?? "(no tool calls)",
                "TiaApi" => _currentDevTrace.TiaApiResults ?? "(no TIA API calls)",
                _ => ""
            };
        }

        private void CloseDevTrace_Click(object sender, RoutedEventArgs e)
        {
            tabDevTrace.Visibility = Visibility.Collapsed;
            _currentDevTrace = null;
            UpdateNoCodeVisibility();
        }

        #endregion
    }
}
