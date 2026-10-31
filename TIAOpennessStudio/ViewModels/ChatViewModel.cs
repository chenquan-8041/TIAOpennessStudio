using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TIAOpennessStudio.AICore;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using Prism.Mvvm;

namespace TIAOpennessStudio.ViewModels
{
    /// <summary>
    /// ViewModel for Chat module - handles conversation lifecycle, message management, AI generation,
    /// reference management, and chat history. UI interaction (scrolling, button visibility, dialogs)
    /// is handled via events/callbacks to maintain MVVM separation.
    /// </summary>
    public class ChatViewModel : BindableBase
    {
        private readonly IChatRequestRouter _chatRequestRouter;
        private readonly IAgentOrchestrator _agentOrchestrator;
        private readonly ILocalChatService _localChatService;
        private readonly ISimpleAnalyzeService _simpleAnalyzeService;
        private readonly IAIEngineService _aiService;
        private readonly ITiaOpennessService _tiaService;
        private readonly AIConfig _aiConfig;
        private readonly IAgentTargetGroupProvider _targetGroupProvider;
        private readonly IUserInteractionService _interaction;   // optional: drives user decision cards (approval / questions)

        private readonly ObservableCollection<ChatMessage> _chatMessages = new ObservableCollection<ChatMessage>();
        private readonly List<AiReferenceItem> _aiReferences = new List<AiReferenceItem>();
        private ChatHistoryManager _chatHistoryManager;
        private ChatSession _currentSession;
        private CancellationTokenSource _chatCts;

        // Progress-watchdog state (shared by the staleness watchdog and the model-loop detector).
        private long _lastActivityTicks;   // UTC ticks of the most recent progress event
        private int _autoStopKind;         // 0 = user/none, 1 = stalled (no progress), 2 = model output loop

        private string _promptText = "";
        private string _previewText = "";
        private string _statusText = "Ready";
        private bool _isGenerating;
        private bool _isStopButtonVisible;
        private int _referenceCount;
        private string _targetBlockGroupPath = "";
        private string _targetTypeGroupPath = "";
        private TokenUsageData _tokenUsage;

        #region Properties

        public ObservableCollection<ChatMessage> ChatMessages => _chatMessages;

        /// <summary>
        /// Token usage stats for the current session, returned by AgentOrchestrator.
        /// </summary>
        public TokenUsageData TokenUsage
        {
            get => _tokenUsage;
            set => SetProperty(ref _tokenUsage, value);
        }

        public string PromptText
        {
            get => _promptText;
            set => SetProperty(ref _promptText, value);
        }

        public string PreviewText
        {
            get => _previewText;
            set => SetProperty(ref _previewText, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public bool IsGenerating
        {
            get => _isGenerating;
            set => SetProperty(ref _isGenerating, value);
        }

        public bool IsStopButtonVisible
        {
            get => _isStopButtonVisible;
            set => SetProperty(ref _isStopButtonVisible, value);
        }

        public bool IsGenerateButtonVisible => !IsGenerating;

        public ChatSession CurrentSession => _currentSession;

        public int ReferenceCount
        {
            get => _referenceCount;
            set => SetProperty(ref _referenceCount, value);
        }

        public IReadOnlyList<AiReferenceItem> AiReferences => _aiReferences.AsReadOnly();

        // When the Agent finishes generating code but the user hasn't picked a chip, we stage the code here until the user makes a choice in the three-option dialog.
        private string _pendingWriteCode;
        private string _pendingWriteName;
        private string _pendingWriteType;
        private bool _pendingPickGroupAfterSelection;

        /// <summary>
        /// Target group path for new/modified Blocks (FB/FC/DB/OB).
        /// Empty string = project root (PlcSoftware.BlockGroup).
        /// Path uses backslash separator, e.g. "Factory\\Line1\\Robot".
        /// </summary>
        public string TargetBlockGroupPath
        {
            get => _targetBlockGroupPath;
            set
            {
                if (SetProperty(ref _targetBlockGroupPath, value ?? ""))
                {
                    RaisePropertyChanged(nameof(HasTargetBlockGroup));
                    RaisePropertyChanged(nameof(TargetBlockGroupDisplay));
                    RaisePropertyChanged(nameof(HasAnyTargetGroup));
                    // Sync to the shared provider so the agent tool (import_block_code) can read it as a fallback
                    if (_targetGroupProvider != null) _targetGroupProvider.BlockGroupPath = _targetBlockGroupPath;
                }
            }
        }

        /// <summary>
        /// Target group path for new/modified Types (UDT).
        /// Empty string = project root (PlcSoftware.TypeGroup).
        /// </summary>
        public string TargetTypeGroupPath
        {
            get => _targetTypeGroupPath;
            set
            {
                if (SetProperty(ref _targetTypeGroupPath, value ?? ""))
                {
                    RaisePropertyChanged(nameof(HasTargetTypeGroup));
                    RaisePropertyChanged(nameof(TargetTypeGroupDisplay));
                    RaisePropertyChanged(nameof(HasAnyTargetGroup));
                    // Sync to the shared provider so the agent tool (import_block_code) can read it as a fallback
                    if (_targetGroupProvider != null) _targetGroupProvider.TypeGroupPath = _targetTypeGroupPath;
                }
            }
        }

        public bool HasTargetBlockGroup => !string.IsNullOrEmpty(_targetBlockGroupPath);
        public bool HasTargetTypeGroup => !string.IsNullOrEmpty(_targetTypeGroupPath);
        public bool HasAnyTargetGroup => HasTargetBlockGroup || HasTargetTypeGroup;

        public string TargetBlockGroupDisplay =>
            string.IsNullOrEmpty(_targetBlockGroupPath) ? "Blocks: <root>" : $"Blocks: {_targetBlockGroupPath}";

        public string TargetTypeGroupDisplay =>
            string.IsNullOrEmpty(_targetTypeGroupPath) ? "Types: <root>" : $"Types: {_targetTypeGroupPath}";

        #endregion

        #region Events

        /// <summary>Raised when chat messages change and UI should scroll to bottom.</summary>
        public event Action ScrollToBottomRequested;

        /// <summary>Raised when a code block should be shown in the center viewer.</summary>
        public event Action<string, string, string> ShowCodeViewerRequested;

        /// <summary>Raised when a message should be copied to clipboard.</summary>
        public event Action<string> CopyToClipboardRequested;

        /// <summary>
        /// Raised when the import flow should be triggered with code.
        /// Args: (code, blockName, blockType, groupPath). blockName/blockType default sensibly when null.
        /// groupPath comes from <see cref="TargetBlockGroupPath"/> or <see cref="TargetTypeGroupPath"/>.
        /// </summary>
        public event Action<string, string, string, string> ImportRequested;

        /// <summary>
        /// Raised when the assistant generated code but the user hasn't picked a target group.
        /// Host (MainWindow) should show the 3-choice dialog and call back into ChatViewModel via
        /// <see cref="ApplyPendingWriteToTiaRoot"/> / <see cref="OpenPickGroupForPendingWrite"/> /
        /// <see cref="SavePendingWriteToLocalFile"/> based on the user's answer.
        /// Args: (blockName, blockType) for dialog headline; the code itself is held inside the ViewModel.
        /// </summary>
        public event Action<string, string> PendingWriteAwaitingChoice;

        /// <summary>
        /// Raised after the host should open the StatusBar chip popup so the user can pick a target group.
        /// Once the user picks, host sets <see cref="TargetBlockGroupPath"/> and ChatViewModel will
        /// auto-import using the deferred code (handled by <see cref="OnTargetBlockGroupPathChangedDuringPending"/>).
        /// </summary>
        public event Action OpenBlockGroupPickerRequested;

        /// <summary>
        /// Raised to ask the host to save deferred code to a local file via SaveFileDialog.
        /// Args: (suggestedFileName, code).
        /// </summary>
        public event Action<string, string> SavePendingWriteToLocalRequested;

        /// <summary>Raised when references need to be refreshed.</summary>
        public event Action ReferencesChanged;

        /// <summary>
        /// Raised for a non-blocking, purely informational message (shown as a toast by the host).
        /// Used for soft notices such as the tool-call soft limit, which must NOT interrupt the run.
        /// </summary>
        public event Action<string> ToastRequested;



        #endregion

        public ChatViewModel(
            IChatRequestRouter chatRequestRouter,
            IAgentOrchestrator agentOrchestrator,
            ILocalChatService localChatService,
            ISimpleAnalyzeService simpleAnalyzeService,
            IAIEngineService aiService,
            ITiaOpennessService tiaService,
            AIConfig aiConfig,
            IAgentTargetGroupProvider targetGroupProvider = null,
            IUserInteractionService interaction = null)
        {
            _interaction = interaction;
            _chatRequestRouter = chatRequestRouter;
            _agentOrchestrator = agentOrchestrator;
            _localChatService = localChatService;
            _simpleAnalyzeService = simpleAnalyzeService;
            _aiService = aiService;
            _tiaService = tiaService;
            _aiConfig = aiConfig;
            _targetGroupProvider = targetGroupProvider;

            InitChatModule();
        }

        #region Initialization

        private void InitChatModule()
        {
            _chatHistoryManager = new ChatHistoryManager();
            _currentSession = _chatHistoryManager.CreateNewSession();
        }

        #endregion

        #region Session Management

        /// <summary>
        /// Raised when the active session's model differs from the current runtime
        /// model (e.g. after loading a session that used another model). The UI
        /// should sync its model selector and call UpdateRuntimeConfig.
        /// Args: (modelName, provider, endpoint).
        /// </summary>
        public event Action<string, string, string> SessionModelRestoreRequested;

        public void StartNewSession()
        {
            if (_currentSession != null && _chatMessages.Count > 0)
            {
                _currentSession.Messages = new List<ChatMessage>(_chatMessages);
                _chatHistoryManager.SaveSession(_currentSession);
            }

            _currentSession = _chatHistoryManager.CreateNewSession();
            // Stamp the new session with the current global model as its initial choice.
            _currentSession.ModelName = _aiConfig?.Model ?? "";
            _currentSession.Provider = _aiConfig?.Provider ?? "";
            _currentSession.Endpoint = _aiConfig?.Endpoint ?? "";
            _chatMessages.Clear();
            PreviewText = "";
            PromptText = "";
            AppLogger.Log("Started a new AI chat session.");
        }

        /// <summary>
        /// Record the model selection onto the active session and persist it.
        /// Called by the UI when the user switches models inside a conversation.
        /// </summary>
        public void RecordModelForCurrentSession(string modelName, string provider, string endpoint)
        {
            if (_currentSession == null) return;
            _currentSession.ModelName = modelName ?? "";
            _currentSession.Provider = provider ?? "";
            _currentSession.Endpoint = endpoint ?? "";
            // Keep the session's message snapshot current before persisting.
            _currentSession.Messages = new List<ChatMessage>(_chatMessages);
            _chatHistoryManager.SaveSession(_currentSession);
            AppLogger.Log($"[Session] Model recorded for session {_currentSession.Id}: {modelName} ({provider})");
        }

        public List<ChatSession> GetAllSessions()
        {
            return _chatHistoryManager.GetAllSessions();
        }

        public void LoadSession(ChatSession session)
        {
            _currentSession = session;
            _chatMessages.Clear();
            foreach (var msg in session.Messages)
            {
                _chatMessages.Add(msg);
            }
            PromptText = "";
            PreviewText = "";
            AppLogger.Log($"Loaded chat session: {session.Title}");

            // Restore the session's model into the runtime so the next request uses
            // the model this conversation was held with. Only fire when the session
            // recorded a model and it differs from the current global selection.
            if (!string.IsNullOrWhiteSpace(session.ModelName) &&
                !string.Equals(session.ModelName, _aiConfig?.Model, StringComparison.OrdinalIgnoreCase))
            {
                SessionModelRestoreRequested?.Invoke(
                    session.ModelName, session.Provider, session.Endpoint);
            }
        }

        public void DeleteSession(string sessionId)
        {
            _chatHistoryManager.DeleteSession(sessionId);
        }

        /// <summary>
        /// Manually trigger AI compression of the current session's history.
        /// After compression, a system message is inserted into the chat describing the result.
        /// </summary>
        public async Task CompressHistoryAsync()
        {
            try
            {
                StatusText = "Compressing conversation history...";
                var history = BuildChatHistory();

                var compressedSummary = await _agentOrchestrator.CompressHistoryAsync(history);
                if (compressedSummary != null)
                {
                    // Keep the 3 most recent original messages; replace the rest with the summary
                    var recentCount = Math.Min(3, _chatMessages.Count);
                    var recent = _chatMessages.Skip(_chatMessages.Count - recentCount).ToList();
                    var oldCount = _chatMessages.Count - recentCount;

                    _chatMessages.Clear();
                    var summaryMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"📋 **Conversation Compressed**\n\n{compressedSummary["content"]}",
                        Timestamp = DateTime.Now
                    };
                    _chatMessages.Add(summaryMsg);
                    foreach (var msg in recent)
                    {
                        _chatMessages.Add(msg);
                    }

                    StatusText = $"Compressed {oldCount} old messages into summary.";
                    AppLogger.Log($"[Compress] Manual compression: {oldCount} messages → summary + {recentCount} recent");
                }
                else
                {
                    StatusText = "Compression failed. History unchanged.";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Compression error: {ex.Message}";
                AppLogger.Log($"[Compress] Error: {ex.Message}");
            }
        }

        #endregion

        #region Reference Management

        public void AddReference(string source, string content, string displayName = null)
        {
            var normalized = (content ?? "").Trim();
            if (string.IsNullOrWhiteSpace(normalized)) return;

            string packed = $"[Source: {source}]\n{normalized}";
            _aiReferences.Add(new AiReferenceItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Source = source,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? source : displayName,
                Packed = packed
            });

            ReferenceCount = _aiReferences.Count;
            ReferencesChanged?.Invoke();
            StatusText = $"Status: Added reference ({_aiReferences.Count})";
        }

        public void RemoveReference(string id)
        {
            _aiReferences.RemoveAll(x => x.Id == id);
            ReferenceCount = _aiReferences.Count;
            ReferencesChanged?.Invoke();
            StatusText = _aiReferences.Count > 0
                ? $"Status: Added reference ({_aiReferences.Count})"
                : "Status: Ready";
        }

        public void ClearReferences()
        {
            _aiReferences.Clear();
            ReferenceCount = 0;
            ReferencesChanged?.Invoke();
        }

        public List<AiReferenceItem> GetReferencesForGeneration()
        {
            return new List<AiReferenceItem>(_aiReferences);
        }

        public string BuildReferenceFromFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return null;

            string ext = System.IO.Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";
            var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".txt", ".md", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg",
                ".cs", ".scl", ".awl", ".st", ".udt", ".db", ".h", ".hpp", ".c", ".cpp",
                ".py", ".js", ".ts", ".tsx", ".java", ".go", ".rs", ".sql", ".log"
            };

            if (!textExtensions.Contains(ext))
            {
                return $"File: {System.IO.Path.GetFileName(filePath)}\nPath: {filePath}\nNote: Binary or unsupported extension ({ext}).";
            }

            string text;
            try
            {
                text = System.IO.File.ReadAllText(filePath, Encoding.UTF8);
            }
            catch
            {
                text = System.IO.File.ReadAllText(filePath, Encoding.Default);
            }

            // Sniff the content instead of trusting the extension: TIA exports use .xml, .udt, .db
            // and .scl names for the same XML payload. Anything that looks like an Openness export
            // goes through the same snapshot projection as the tree context menu.
            var trimmed = text.TrimStart();
            var looksLikeOpennessXml = trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                                       || trimmed.StartsWith("<Document", StringComparison.OrdinalIgnoreCase);
            if (looksLikeOpennessXml &&
                TiaXmlAgentReferenceBuilder.TryBuildReference(text, System.IO.Path.GetFileName(filePath), out var referenceText))
            {
                return $"File: {System.IO.Path.GetFileName(filePath)}\nPath: {filePath}\n{referenceText}";
            }

            if (text.Length > 20000)
            {
                text = text.Substring(0, 20000) + "\n...<truncated>";
            }

            return $"File: {System.IO.Path.GetFileName(filePath)}\nPath: {filePath}\nContent:\n{text}";
        }

        #endregion

        #region History Helpers

        private List<Dictionary<string, string>> BuildChatHistory(int maxMessages = 12)
        {
            var historyList = new List<Dictionary<string, string>>();
            int endExclusive = Math.Max(0, _chatMessages.Count - 2);
            int startIndex = Math.Max(0, endExclusive - maxMessages);
            for (int i = startIndex; i < endExclusive; i++)
            {
                var msg = _chatMessages[i];
                if (msg == null) continue;
                var role = (msg.Role ?? "").Trim().ToLowerInvariant();
                if (role != "user" && role != "assistant") continue;
                var contentText = (msg.Content ?? "").Trim();
                if (string.IsNullOrWhiteSpace(contentText)) continue;
                historyList.Add(new Dictionary<string, string>
                {
                    { "role", role },
                    { "content", contentText }
                });
            }
            return historyList;
        }

        private DevTraceData BuildRequestTrace(
            string prompt,
            ChatRouteResult routeResult,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Request Trace ===");
            sb.AppendLine($"RouteKind: {routeResult?.RouteKind}");
            sb.AppendLine($"Intent: {routeResult?.Intent}");
            sb.AppendLine($"Confidence: {routeResult?.Confidence:0.00}");
            sb.AppendLine($"Reason: {routeResult?.Reason}");
            sb.AppendLine($"RequestedModel: {ResolveTraceModelName()}");
            sb.AppendLine($"HistoryCount: {history?.Count ?? 0}");
            sb.AppendLine($"ReferenceCount: {references?.Count ?? 0}");

            if (!string.IsNullOrWhiteSpace(TargetBlockGroupPath))
                sb.AppendLine($"TargetBlockGroupPath: {TargetBlockGroupPath}");
            if (!string.IsNullOrWhiteSpace(TargetTypeGroupPath))
                sb.AppendLine($"TargetTypeGroupPath: {TargetTypeGroupPath}");

            if (history != null && history.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("=== History Snapshot ===");
                foreach (var item in history)
                {
                    if (item == null) continue;
                    var role = item.TryGetValue("role", out var roleVal) ? roleVal : "user";
                    var content = item.TryGetValue("content", out var contentVal) ? contentVal : string.Empty;
                    sb.AppendLine($"[{role}] {content}");
                }
            }

            if (references != null && references.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("=== References ===");
                foreach (var reference in references)
                {
                    if (reference == null) continue;
                    sb.AppendLine($"- {reference.DisplayName ?? reference.Source ?? "(reference)"}");
                    if (!string.IsNullOrWhiteSpace(reference.Packed))
                    {
                        var packed = reference.Packed.Length > 1200
                            ? reference.Packed.Substring(0, 1200) + "\n...<truncated>"
                            : reference.Packed;
                        sb.AppendLine(packed);
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== User Prompt ===");
            sb.AppendLine(prompt ?? string.Empty);

            return new DevTraceData
            {
                RawPrompt = sb.ToString(),
                ModelName = ResolveTraceModelName()
            };
        }

        private string ResolveTraceModelName()
        {
            return _aiConfig?.Model
                ?? _aiConfig?.SelectedModel
                ?? _aiConfig?.DefaultOllamaModel
                ?? "unknown";
        }

        #endregion

        #region Message Operations

        public void AddAssistantMessage(string content)
        {
            var msg = new ChatMessage
            {
                Role = "assistant",
                Content = content ?? ""
            };
            _chatMessages.Add(msg);
            ScrollToBottomRequested?.Invoke();
            SaveCurrentSession();
        }

        public string ExtractCodeFromMessage(ChatMessage msg)
        {
            if (msg == null) return string.Empty;

            if (!string.IsNullOrWhiteSpace(msg.DisplayCode))
                return msg.DisplayCode.Trim();

            string content = msg.Content ?? string.Empty;
            string code = content;
            if (content.IndexOf("```scl", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int start = content.IndexOf("```scl", StringComparison.OrdinalIgnoreCase) + 6;
                int end = content.IndexOf("```", start, StringComparison.OrdinalIgnoreCase);
                if (end > start) code = content.Substring(start, end - start).Trim();
            }
            else if (content.Contains("```"))
            {
                int start = content.IndexOf("```", StringComparison.OrdinalIgnoreCase) + 3;
                int end = content.IndexOf("```", start, StringComparison.OrdinalIgnoreCase);
                if (end > start)
                {
                    code = content.Substring(start, end - start).Trim();
                    if (code.StartsWith("scl", StringComparison.OrdinalIgnoreCase))
                        code = code.Substring(3).Trim();
                }
            }
            return code.Trim();
        }

        public void CopyCodeFromMessage(ChatMessage msg)
        {
            string code = ExtractCodeFromMessage(msg);
            CopyToClipboardRequested?.Invoke(code);
            StatusText = "Code copied to clipboard!";
        }

        public void PreviewCodeFromMessage(ChatMessage msg)
        {
            string code = ExtractCodeFromMessage(msg);
            if (string.IsNullOrWhiteSpace(code)) return;

            PreviewText = code;
            string tabName = $"AI Generated Code {DateTime.Now:HHmmss}";
            ShowCodeViewerRequested?.Invoke(tabName, "SCL", code);
            StatusText = "Status: Code expanded in center viewer";
        }

        public void ImportCodeFromMessage(ChatMessage msg)
        {
            string code = ExtractCodeFromMessage(msg);
            if (string.IsNullOrWhiteSpace(code)) return;

            PreviewText = code;

            // Default to BlockGroup; if the code looks like a UDT, use the TypeGroup chip path instead
            bool looksLikeUdt = code.IndexOf("TYPE ", StringComparison.OrdinalIgnoreCase) >= 0
                                && code.IndexOf("END_TYPE", StringComparison.OrdinalIgnoreCase) >= 0;
            string blockType = looksLikeUdt ? "UDT" : "FB";
            string groupPath = looksLikeUdt ? TargetTypeGroupPath : TargetBlockGroupPath;

            // Guard: if the user hasn't picked a target chip, prompt them to choose one instead of silently writing to the root
            if (string.IsNullOrEmpty(groupPath))
            {
                string chipName = looksLikeUdt ? "Types" : "Blocks";
                var result = MessageBox.Show(
                    $"还没有选择 {chipName} 目标分组.\n\n" +
                    $"是否写入项目根目录? (推荐先在状态栏点 {chipName} chip 选一个具体分组)",
                    "Confirm import target",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    StatusText = "Status: Import cancelled, please pick a target group.";
                    return;
                }
                groupPath = ""; // User confirmed → explicitly fall back to root
            }

            ImportRequested?.Invoke(code, "AI_Generated_Block", blockType, groupPath);
        }

        #endregion

        #region Generation

        public async Task GenerateAsync(string prompt, List<AiReferenceItem> aiReferences = null)
        {
            await GenerateInternalAsync(prompt, aiReferences, null);
        }

        /// <summary>
        /// Generation entry point with images. Images are base64-encoded, stored on the user message, and passed through to the AI service.
        /// </summary>
        public async Task GenerateWithImagesAsync(string prompt, List<byte[]> images, List<AiReferenceItem> aiReferences = null)
        {
            await GenerateInternalAsync(prompt, aiReferences, images);
        }

        /// <summary>
        /// Probe the backend for a model's vision support and cache a definitive
        /// verdict into AIConfig. Runs fire-and-forget; "unknown" results are not
        /// cached so the model is re-probed next time. Never throws.
        /// </summary>
        private async Task ProbeAndCacheVisionAsync(string modelName)
        {
            try
            {
                if (_aiService == null || _aiConfig == null || string.IsNullOrWhiteSpace(modelName))
                    return;
                var result = await _aiService.ProbeVisionAsync();
                if (string.Equals(result, "supported", StringComparison.OrdinalIgnoreCase))
                    _aiConfig.SetVisionSupport(modelName, true);
                else if (string.Equals(result, "unsupported", StringComparison.OrdinalIgnoreCase))
                    _aiConfig.SetVisionSupport(modelName, false);
                // "unknown" → do not cache; will re-probe next time.
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[CHAT] ProbeAndCacheVisionAsync failed for {modelName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Localize a backend notice. The code maps to a LanguageManager key
        /// (English / German / Chinese); unknown codes fall back to the English text.
        /// </summary>
        private static string LocalizeNotice(NoticeData notice)
        {
            if (notice == null) return string.Empty;
            string fallback = notice.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(notice.Code)) return fallback;
            var args = (notice.Params ?? new List<string>()).Cast<object>().ToArray();
            return LanguageManager.Instance.Format("Agent." + notice.Code, fallback, args);
        }

        /// <summary>Milestone reached during a project plan; carried from the streaming callback to the approval loop.</summary>
        private sealed class PendingMilestone
        {
            public string PlanId;
            public string PhaseId;
            public string Name;
        }

        /// <summary>Failed task during a project plan; carried to the continue / skip / terminate decision card.</summary>
        private sealed class PendingTaskFailure
        {
            public string PlanId;
            public string TaskId;
            public string BlockName;
            public string Error;
            public ManualActionData ManualAction;
        }

        /// <summary>
        /// Show the milestone approval card (approve / adjust / reject) via the interaction service and return the
        /// chosen decision plus an optional adjust note. Returns (null, null) when the user declines / times out /
        /// no UI is available — the plan stays paused and can be resumed later by re-sending the request.
        /// </summary>
        private async Task<(string decision, string note)> RequestMilestoneDecisionAsync(PendingMilestone m, CancellationToken ct)
        {
            if (_interaction == null)
            {
                AppLogger.Log("[CHAT] No interaction service; auto-approving milestone.");
                return ("approve", null);
            }

            var req = new InteractionRequest
            {
                Kind = InteractionKind.Choose,
                Title = $"里程碑审批：{m.Name}",
                Summary = $"阶段「{m.Name}」已生成完毕并通过编译。是否继续下一阶段？",
                Risk = InteractionRisk.Medium,
                Source = "Planner",
                Options = new List<InteractionOption>
                {
                    new InteractionOption { Id = "approve", Label = "通过并继续", Description = "接受当前阶段产物，继续执行后续任务" },
                    new InteractionOption { Id = "adjust", Label = "调整后继续", Description = "重新规划尚未完成的部分后继续" },
                    new InteractionOption { Id = "reject", Label = "终止", Description = "终止本次项目生成（计划保留，可下次继续）" },
                }
            };

            var result = await _interaction.RequestAsync(req, ct, allowWithoutRun: true);
            if (result == null || !result.IsPositive || string.IsNullOrWhiteSpace(result.Value))
                return (null, null);

            var decision = result.Value;
            string note = null;
            if (decision == "adjust")
            {
                // Add a free-text card to collect the user's specific adjustment note for backend replanning.
                var inputResult = await _interaction.RequestAsync(new InteractionRequest
                {
                    Kind = InteractionKind.Input,
                    Title = $"调整说明：{m.Name}",
                    Summary = "请描述需要调整/补充的内容（例如：新增一个堵转保护块、修改共享 DB 字段）。",
                    Risk = InteractionRisk.Medium,
                    Source = "Planner",
                    InputHint = "输入调整要求（可留空，仅重跑未完成部分）",
                }, ct, allowWithoutRun: true);
                if (inputResult != null && inputResult.IsPositive && !string.IsNullOrWhiteSpace(inputResult.Value))
                    note = inputResult.Value;
            }
            return (decision, note);
        }

        /// <summary>Show the failed-task decision card (continue / skip / terminate) and return the choice, or null.</summary>
        private async Task<string> RequestTaskDecisionAsync(PendingTaskFailure f, CancellationToken ct)
        {
            if (_interaction == null)
            {
                AppLogger.Log("[CHAT] No interaction service; auto-continuing failed task.");
                return "continue";
            }

            var req = new InteractionRequest
            {
                Kind = InteractionKind.Choose,
                Title = $"任务失败：{f.BlockName}",
                Summary = $"块「{f.BlockName}」生成/编译失败，已暂停。如何继续？",
                Detail = f.Error,
                Risk = InteractionRisk.High,
                Source = "Executor",
                Options = new List<InteractionOption>
                {
                    new InteractionOption { Id = "continue", Label = "重试该块", Description = "重置该块并重新生成" },
                    new InteractionOption { Id = "skip", Label = "跳过该块", Description = "跳过该块及其下游，继续其余任务" },
                    new InteractionOption { Id = "terminate", Label = "终止", Description = "终止本次项目生成（计划保留，可下次继续）" },
                }
            };

            var result = await _interaction.RequestAsync(req, ct, allowWithoutRun: true);
            if (result == null || !result.IsPositive || string.IsNullOrWhiteSpace(result.Value))
                return null;
            return result.Value;
        }

        /// <summary>
        /// Show a manual-action card for a failure the agent cannot fix itself (e.g. safety-program
        /// password). Approve = the user says "I've done it" → retry; anything else stays paused.
        /// </summary>
        private async Task<string> RequestManualActionAsync(PendingTaskFailure f, CancellationToken ct)
        {
            var action = f.ManualAction;
            if (_interaction == null)
            {
                AppLogger.Log("[CHAT] No interaction service; auto-continuing manual-action task.");
                return "continue";
            }

            var req = new InteractionRequest
            {
                Kind = InteractionKind.Approve,
                Title = LanguageManager.Instance.GetString("Agent.manual.title"),
                Summary = LanguageManager.Instance.Format("Agent." + (action?.Code ?? "write.manual"), action?.Text ?? ""),
                Detail = f.Error,
                Risk = InteractionRisk.Medium,
                Source = "Executor",
            };

            var result = await _interaction.RequestAsync(req, ct, allowWithoutRun: true);
            if (result == null || !result.IsPositive)
                return null;
            return "continue";
        }

        private async Task GenerateInternalAsync(string prompt, List<AiReferenceItem> aiReferences, List<byte[]> images)
        {
            AppLogger.Log($"[CHAT] GenerateInternalAsync entered, prompt='{(prompt ?? "<null>")}', length={(prompt?.Length ?? 0)}, refs={aiReferences?.Count ?? 0}, images={images?.Count ?? 0}");
            if (string.IsNullOrWhiteSpace(prompt) && (images == null || images.Count == 0))
            {
                MessageBox.Show("Please enter a description or attach an image.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Convert images to a base64 list
            var imageBase64List = images?.Select(img => Convert.ToBase64String(img)).ToList();

            // Vision capability gate: only evaluated when the user actually attached images.
            //   supported(true)  → send normally with images
            //   unsupported(false) → notify and strip images, continue as text-only (friendlier than blocking outright)
            //   unknown(null)     → probe once asynchronously in the background and cache; this turn is not blocked (result applies next time)
            if (imageBase64List != null && imageBase64List.Count > 0)
            {
                var modelName = ResolveTraceModelName();
                var visionSupport = _aiConfig?.ResolveVisionSupport(modelName);
                if (visionSupport == false)
                {
                    MessageBox.Show(
                        $"当前模型 \"{modelName}\" 不支持图片输入，图片将被忽略，仅按文字处理。\n" +
                        "如需图片理解能力，请切换到支持多模态的模型（如 GPT-4o）。",
                        "模型不支持图片", MessageBoxButton.OK, MessageBoxImage.Information);
                    imageBase64List = null;
                }
                else if (visionSupport == null)
                {
                    // Fire-and-forget probe so future turns get a definitive verdict.
                    _ = ProbeAndCacheVisionAsync(modelName);
                }
            }

            _chatCts = new CancellationTokenSource();
            IsGenerating = true;
            IsStopButtonVisible = true;
            StatusText = aiReferences != null && aiReferences.Count > 0
                ? $"Status: Generating with {aiReferences.Count} reference(s)..."
                : "Status: Generating...";

            // Add User Message - record reference names and images on the message so the user can see what context was attached
            var userMsg = new ChatMessage { Role = "user", Content = prompt };
            if (aiReferences != null && aiReferences.Count > 0)
            {
                foreach (var r in aiReferences)
                {
                    if (r == null) continue;
                    userMsg.AttachedReferences.Add(new AttachedReferenceInfo
                    {
                        DisplayName = r.DisplayName ?? r.Source ?? "(reference)",
                        Source = r.Source ?? "",
                        CharCount = r.Packed?.Length ?? 0
                    });
                }
            }
            if (imageBase64List != null && imageBase64List.Count > 0)
            {
                userMsg.Images = imageBase64List;
            }
            _chatMessages.Add(userMsg);
            AppLogger.Log($"[CHAT] user msg added; chatMessages.Count={_chatMessages.Count}");
            PromptText = "";

            // Force the UI to update the user message immediately
            ScrollToBottomRequested?.Invoke();
            System.Windows.Application.Current?.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            // Add Assistant Message with Thinking state
            var assistantMsg = new ChatMessage { Role = "assistant", Content = "", Thinking = "", IsThinking = true };
            _chatMessages.Add(assistantMsg);
            AppLogger.Log($"[CHAT] assistant placeholder added; chatMessages.Count={_chatMessages.Count}");
            ScrollToBottomRequested?.Invoke();

            // Snapshot the references so later additions during sending don't mutate the collection
            var refsSnapshot = aiReferences != null ? new List<AiReferenceItem>(aiReferences) : null;

            // Snapshot the history *after* the user message is added but before calling the LLM.
            // BuildChatHistory automatically skips the last two entries (the just-added user + assistant placeholder),
            // so the model sees what was actually discussed before.
            var historySnapshot = BuildChatHistory();
            AppLogger.Log($"[CHAT] history size for LLM = {historySnapshot.Count}");
            var requestStopwatch = Stopwatch.StartNew();

            CancellationTokenSource watchdogCts = null;
            Task watchdogTask = null;

            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                var routeResult = await _chatRequestRouter.RouteAsync(prompt, refsSnapshot, historySnapshot, _chatCts.Token);
                AppLogger.Log($"[CHAT] route kind={routeResult.RouteKind}, intent={routeResult.Intent}, confidence={routeResult.Confidence:0.00}, reason={routeResult.Reason}");

                // Progress watchdog instead of a fixed wall-clock timeout: a run is auto-stopped only
                // when it demonstrably stalls (no events) or loops (repeated output). Slow-but-steady
                // local generations stream events continuously and are left alone.
                Interlocked.Exchange(ref _autoStopKind, 0);
                Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(_chatCts.Token);
                watchdogTask = Task.Run(() => RunWatchdogAsync(watchdogCts.Token, _interaction));
                var modelLoop = new TextLoopDetector();

                // Routing info is debug data: record it only in DevTrace, not in the user-visible thinking stream.
                assistantMsg.DevTrace = BuildRequestTrace(prompt, routeResult, refsSnapshot, historySnapshot);

                // ── Project-plan resume loop ──
                // A milestone pauses the Python plan (status → "paused", the stream ends). We surface the
                // approve/adjust/reject card, POST /plan/decide, then re-send /chat_plan with the SAME prompt +
                // project_key so the supervisor resumes from the breakpoint. Each resumed round gets a fresh
                // assistant message so FinalizeContent is never called twice on one message.
                PendingMilestone pendingMilestone = null;
                PendingTaskFailure pendingTaskFailure = null;
                var currentAssistantMsg = assistantMsg;

                while (true)
                {
                    pendingMilestone = null;
                    pendingTaskFailure = null;

                    // Critical fix: run the whole agent call on the thread pool via Task.Run.
                    // Otherwise the SSE read loop keeps running on the UI thread (WPF's default SynchronizationContext),
                    // the UI thread is saturated by network IO, and although streamed chunks arrive the UI can't repaint →
                    // it appears to "freeze and then show everything at once".
                    var agentTask = Task.Run(async () =>
                    {
                        Action<AgentStreamEvent> emit = ev =>
                        {
                            // Any event means the pipeline is alive: refresh the staleness watchdog.
                            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

                            // Detect a model looping on its own output (repeated content) and auto-stop.
                            if (ev.Kind == AgentStreamKind.Content && !string.IsNullOrEmpty(ev.Text) && modelLoop.Append(ev.Text))
                            {
                                if (Interlocked.CompareExchange(ref _autoStopKind, 2, 0) == 0)
                                    _chatCts?.Cancel();
                            }

                            dispatcher?.BeginInvoke(new Action(() =>
                            {
                                if (currentAssistantMsg.IsThinking) currentAssistantMsg.IsThinking = false;
                                if (!currentAssistantMsg.IsStreaming) currentAssistantMsg.IsStreaming = true;

                                switch (ev.Kind)
                                {
                                    case AgentStreamKind.Content:
                                        currentAssistantMsg.AppendContentDelta(ev.Text);
                                        break;
                                    case AgentStreamKind.Thinking:
                                        currentAssistantMsg.AppendThinking(ev.Text);
                                        break;
                                    case AgentStreamKind.ToolStart:
                                        currentAssistantMsg.BeginToolCall(ev.ToolName, ev.ToolInput);
                                        break;
                                    case AgentStreamKind.ToolEnd:
                                        currentAssistantMsg.FinishToolCall(ev.ToolName, ev.ToolOutput, success: true);
                                        break;
                                    case AgentStreamKind.Error:
                                        currentAssistantMsg.AppendError(ev.Text);
                                        break;
                                    case AgentStreamKind.Status:
                                        break;
                                    case AgentStreamKind.TokenUsage:
                                        TokenUsage = ev.TokenUsage;
                                        break;
                                    case AgentStreamKind.Delivery:
                                        if (ev.Delivery != null)
                                            currentAssistantMsg.MessageBlocks.Add(ev.Delivery);
                                        break;
                                    case AgentStreamKind.Milestone:
                                        pendingMilestone = new PendingMilestone { PlanId = ev.PlanId, PhaseId = ev.PhaseId, Name = ev.PhaseName };
                                        currentAssistantMsg.AppendThinking(LanguageManager.Instance.Format(
                                            "Agent.plan.milestone.waiting",
                                            $"⏸ Milestone \"{ev.PhaseName}\" completed; awaiting approval",
                                            ev.PhaseName ?? ""));
                                        break;
                                    case AgentStreamKind.PlanProgress:
                                        if (ev.PlanProgress != null)
                                            currentAssistantMsg.UpdatePlanProgress(ev.PlanProgress.Total, ev.PlanProgress.Done, ev.PlanProgress.Failed, ev.PlanProgress.Current);
                                        break;
                                    case AgentStreamKind.TaskFailed:
                                        if (ev.TaskFailed != null)
                                            pendingTaskFailure = new PendingTaskFailure { PlanId = ev.TaskFailed.PlanId, TaskId = ev.TaskFailed.TaskId, BlockName = ev.TaskFailed.BlockName, Error = ev.TaskFailed.Error, ManualAction = ev.TaskFailed.ManualAction };
                                        currentAssistantMsg.AppendThinking(LanguageManager.Instance.Format(
                                            "Agent.plan.task.failed",
                                            $"❌ Block {ev.TaskFailed?.BlockName} generation failed; awaiting decision (continue/skip/terminate)",
                                            ev.TaskFailed?.BlockName ?? ""));
                                        break;
                                    case AgentStreamKind.Notice:
                                        if (ev.Notice != null)
                                        {
                                            var noticeText = LocalizeNotice(ev.Notice);
                                            currentAssistantMsg.AppendThinking(noticeText);
                                            // Soft tool-call threshold: the run is NOT aborted — it continues
                                            // under token-budget/compaction control. Inform the user with a
                                            // non-blocking toast (never a modal dialog: it would stall the stream).
                                            if (string.Equals(ev.Notice.Code, "tool.soft_limit", StringComparison.OrdinalIgnoreCase))
                                                ToastRequested?.Invoke(noticeText);
                                        }
                                        break;
                                }
                                ScrollToBottomRequested?.Invoke();
                            }), System.Windows.Threading.DispatcherPriority.Background);
                        };

                        switch (routeResult.RouteKind)
                        {
                            case ChatRouteKind.Chat:
                                await _localChatService.ChatAsync(prompt, refsSnapshot, historySnapshot, imageBase64List, emit, _chatCts.Token);
                                break;
                            case ChatRouteKind.SimpleAnalyze:
                                await _simpleAnalyzeService.AnalyzeAsync(prompt, refsSnapshot, historySnapshot, imageBase64List, emit, _chatCts.Token);
                                break;
                            default:
                                await _agentOrchestrator.RunAgentActionAsync(
                                    prompt,
                                    null,
                                    null,
                                    refsSnapshot,
                                    historySnapshot,
                                    TargetBlockGroupPath,
                                    TargetTypeGroupPath,
                                    imageBase64List,
                                    (plan) =>
                                    {
                                        dispatcher?.BeginInvoke(new Action(() =>
                                        {
                                            currentAssistantMsg.Plan = plan;
                                            currentAssistantMsg.IsThinking = plan.State == AgentExecutionState.Running && !currentAssistantMsg.IsStreaming;
                                            ScrollToBottomRequested?.Invoke();
                                        }), System.Windows.Threading.DispatcherPriority.Background);
                                    },
                                    emit,
                                    _chatCts.Token);
                                break;
                        }
                    }, _chatCts.Token);

                await agentTask; // Ensure exceptions are propagated

                // Critical fix: ensure FinalizeContent runs only after all streamed BeginInvoke calls (Background priority) have been processed.
                // Otherwise, if agentTask completes too quickly, FinalizeContent clears the existing blocks and converts them to a CodeBlock before streaming finishes,
                // and the leftover AppendContentDelta events then re-insert the full text as a MessageTextBlock, causing content to "generate twice".
                if (dispatcher != null)
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        currentAssistantMsg.IsThinking = false;
                        currentAssistantMsg.IsStreaming = false;
                        currentAssistantMsg.FinalizeContent();

                        // Dev Mode: copy the Agent execution trace data to the ChatMessage
                        if (currentAssistantMsg.Plan?.DevTrace != null)
                        {
                            currentAssistantMsg.Plan.DevTrace.ModelName = _aiConfig?.Model ?? _aiConfig?.SelectedModel ?? "unknown";
                            currentAssistantMsg.DevTrace = currentAssistantMsg.Plan.DevTrace;
                        }
                        else if (currentAssistantMsg.DevTrace != null)
                        {
                            currentAssistantMsg.DevTrace.TotalDuration = requestStopwatch.Elapsed;
                            currentAssistantMsg.DevTrace.ModelName = ResolveTraceModelName();
                        }

                        DetectAndSurfacePendingWrite(currentAssistantMsg.Plan);
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
                else
                {
                    currentAssistantMsg.IsThinking = false;
                    currentAssistantMsg.IsStreaming = false;
                    currentAssistantMsg.FinalizeContent();

                    if (currentAssistantMsg.Plan?.DevTrace != null)
                    {
                        currentAssistantMsg.Plan.DevTrace.ModelName = _aiConfig?.Model ?? _aiConfig?.SelectedModel ?? "unknown";
                        currentAssistantMsg.DevTrace = currentAssistantMsg.Plan.DevTrace;
                    }
                    else if (currentAssistantMsg.DevTrace != null)
                    {
                        currentAssistantMsg.DevTrace.TotalDuration = requestStopwatch.Elapsed;
                        currentAssistantMsg.DevTrace.ModelName = ResolveTraceModelName();
                    }

                    DetectAndSurfacePendingWrite(currentAssistantMsg.Plan);
                }

                // No milestone and no task failure -> this round ended normally (single block or plan finished).
                if (pendingMilestone == null && pendingTaskFailure == null)
                    break;

                if (pendingMilestone != null)
                {
                    // ── Milestone approval card ──
                    var (decision, note) = await RequestMilestoneDecisionAsync(pendingMilestone, _chatCts.Token);
                    if (decision == null)
                    {
                        currentAssistantMsg.AppendThinking(LanguageManager.Instance.GetString("Agent.plan.milestone.unanswered"));
                        break;
                    }

                    await _aiService.PostPlanDecisionAsync(pendingMilestone.PlanId, pendingMilestone.PhaseId, decision, note);

                    if (decision == "reject")
                    {
                        currentAssistantMsg.AppendThinking(LanguageManager.Instance.GetString("Agent.plan.milestone.rejected"));
                        break;
                    }

                    currentAssistantMsg.AppendThinking(decision == "approve"
                        ? LanguageManager.Instance.GetString("Agent.plan.milestone.approved")
                        : LanguageManager.Instance.GetString("Agent.plan.milestone.adjust"));
                }
                else if (pendingTaskFailure != null && pendingTaskFailure.ManualAction != null)
                {
                    // ── Manual-action card: known blocker the agent can't fix; ask the user, then retry ──
                    var taskDecision = await RequestManualActionAsync(pendingTaskFailure, _chatCts.Token);
                    if (taskDecision == null)
                    {
                        currentAssistantMsg.AppendThinking(LanguageManager.Instance.GetString("Agent.plan.task.unhandled"));
                        break;
                    }

                    await _aiService.PostTaskDecisionAsync(pendingTaskFailure.PlanId, pendingTaskFailure.TaskId, taskDecision);
                    currentAssistantMsg.AppendThinking(LanguageManager.Instance.Format(
                        "Agent.plan.task.continue",
                        $"🔁 Retrying block \"{pendingTaskFailure.BlockName}\"; resuming from the checkpoint...",
                        pendingTaskFailure.BlockName ?? ""));
                }
                else
                {
                    // ── Task failure decision card (continue / skip / terminate) ──
                    var taskDecision = await RequestTaskDecisionAsync(pendingTaskFailure, _chatCts.Token);
                    if (taskDecision == null)
                    {
                        currentAssistantMsg.AppendThinking(LanguageManager.Instance.GetString("Agent.plan.task.unhandled"));
                        break;
                    }

                    await _aiService.PostTaskDecisionAsync(pendingTaskFailure.PlanId, pendingTaskFailure.TaskId, taskDecision);

                    if (taskDecision == "terminate")
                    {
                        currentAssistantMsg.AppendThinking(LanguageManager.Instance.GetString("Agent.plan.terminated"));
                        break;
                    }

                    currentAssistantMsg.AppendThinking(taskDecision == "continue"
                        ? LanguageManager.Instance.Format(
                            "Agent.plan.task.continue",
                            $"🔁 Retrying block \"{pendingTaskFailure.BlockName}\"; resuming from the checkpoint...",
                            pendingTaskFailure.BlockName ?? "")
                        : LanguageManager.Instance.Format(
                            "Agent.plan.task.skip",
                            $"⏭ Skipped block \"{pendingTaskFailure.BlockName}\"; continuing with the remaining tasks...",
                            pendingTaskFailure.BlockName ?? ""));
                }

                // Start a fresh assistant message for the resumed round.
                currentAssistantMsg = new ChatMessage { Role = "assistant", Content = "", Thinking = "", IsThinking = true };
                _chatMessages.Add(currentAssistantMsg);
                ScrollToBottomRequested?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                assistantMsg.IsThinking = false;
                assistantMsg.IsStreaming = false;
                if (_autoStopKind == 1)
                {
                    var minutes = (int)WatchdogPolicy.StaleAfter.TotalMinutes;
                    assistantMsg.Content += $"\n\n---\n⚠️ **Auto-stopped: no response for {minutes} minutes (model appears stuck)**";
                    StatusText = "Status: Auto-stopped (no response)";
                }
                else if (_autoStopKind == 2)
                {
                    assistantMsg.Content += "\n\n---\n⚠️ **Auto-stopped: model output is looping (repeated text detected)**";
                    StatusText = "Status: Auto-stopped (loop)";
                }
                else
                {
                    assistantMsg.Content += "\n\n---\n❌ **操作已取消**";
                    StatusText = "Status: Cancelled";
                }
            }
            catch (Exception ex)
            {
                assistantMsg.IsThinking = false;
                assistantMsg.IsStreaming = false;
                var errorMsg = ex.Message;
                
                // Provide a friendly error message
                if (errorMsg.Contains("401") || errorMsg.Contains("Unauthorized") || errorMsg.Contains("API key"))
                {
                    assistantMsg.Content += "\n\n---\n🔑 **API 认证失败**\n\n" +
                        $"错误详情: {errorMsg}\n\n" +
                        "解决方案:\n" +
                        "1. 检查 AI Config 中的 API Key 是否正确\n" +
                        "2. 确认 API Key 未过期\n" +
                        "3. 验证 Base URL 配置正确";
                }
                else if (errorMsg.Contains("429") || errorMsg.Contains("Rate limit") || errorMsg.Contains("配额"))
                {
                    assistantMsg.Content += "\n\n---\n⚠️ **API 配额已用尽**\n\n" +
                        $"错误详情: {errorMsg}\n\n" +
                        "解决方案:\n" +
                        "1. 等待配额重置\n" +
                        "2. 切换到其他模型\n" +
                        "3. 升级 API 套餐";
                }
                else if (errorMsg.Contains("403") || errorMsg.Contains("Forbidden"))
                {
                    assistantMsg.Content += "\n\n---\n🚫 **访问被拒绝**\n\n" +
                        $"错误详情: {errorMsg}\n\n" +
                        "解决方案:\n" +
                        "1. 检查 API Key 权限\n" +
                        "2. 确认模型可用性\n" +
                        "3. 联系 API 提供商";
                }
                else if (errorMsg.Contains("timeout") || errorMsg.Contains("超时"))
                {
                    assistantMsg.Content += "\n\n---\n⏱️ **连接超时**\n\n" +
                        $"错误详情: {errorMsg}\n\n" +
                        "解决方案:\n" +
                        "1. 检查网络连接\n" +
                        "2. 尝试使用本地模型\n" +
                        "3. 稍后重试";
                }
                else
                {
                    assistantMsg.Content += $"\n\n---\n❌ **Agent 执行失败**\n\n错误详情: {errorMsg}\n\n" +
                        "建议:\n" +
                        "1. 检查模型配置\n" +
                        "2. 查看后端日志\n" +
                        "3. 尝试重新发送";
                }
                
                StatusText = "Status: Error";
                AppLogger.Log($"[CHAT] GenerateAsync error: {ex}");
            }
            finally
            {
                try { watchdogCts?.Cancel(); } catch { /* already disposed */ }
                if (watchdogTask != null)
                {
                    // Do NOT ConfigureAwait(false) here: the cleanup below removes reference
                    // chips, raises ReferencesChanged → MainWindow.RefreshReferenceChips, and
                    // that handler touches the WPF visual tree. Resuming on the captured UI
                    // context keeps the event on the UI thread; the await itself is
                    // asynchronous and does not block the UI (the watchdog is already
                    // cancelled and exits at its next tick).
                    try { await watchdogTask; } catch { /* cancelled */ }
                }
                watchdogCts?.Dispose();

                IsGenerating = false;
                IsStopButtonVisible = false;
                _chatCts = null;

                // Clear references that were already sent from the input header so they aren't reused by default next time,
                // consistent with the product UX: chips are "context for this send" and disappear after sending.
                // If the user wants to keep them, they can drop them again before the next send.
                if (refsSnapshot != null && refsSnapshot.Count > 0)
                {
                    foreach (var r in refsSnapshot)
                    {
                        if (r != null && !string.IsNullOrEmpty(r.Id))
                        {
                            _aiReferences.RemoveAll(x => x.Id == r.Id);
                        }
                    }
                    ReferenceCount = _aiReferences.Count;
                    ReferencesChanged?.Invoke();
                }

                SaveCurrentSession();
            }
        }

        public void StopGeneration()
        {
            _chatCts?.Cancel();
            IsStopButtonVisible = false;
            IsGenerating = false;
            StatusText = "Status: Cancelled";
        }

        /// <summary>
        /// Staleness watchdog: auto-stops a run that produced no progress event for
        /// <see cref="WatchdogPolicy.StaleAfter"/>, unless the run is paused on a user decision
        /// (a slow human is not a hung model). Slow-but-steady generations keep refreshing
        /// <see cref="_lastActivityTicks"/> and never trip this.
        /// </summary>
        private async Task RunWatchdogAsync(CancellationToken ct, IUserInteractionService interaction)
        {
            var staleAfter = WatchdogPolicy.StaleAfter;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

                    if (interaction != null && interaction.HasPending)
                    {
                        // Refresh the reference so time spent deciding never counts as staleness.
                        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                        continue;
                    }

                    var last = new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
                    if (DateTime.UtcNow - last >= staleAfter)
                    {
                        // First writer wins (the model-loop detector may have already claimed the stop).
                        if (Interlocked.CompareExchange(ref _autoStopKind, 1, 0) == 0)
                            _chatCts?.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Run ended (normal finish, user cancel, or a detection fired) — exit quietly.
            }
        }

        // ---- Pending write (no target chip selected) handling ----

        /// <summary>
        /// Look at the just-completed plan: if any Write/Import step is in WaitingConfirmation state
        /// (Orchestrator deferred it because target group wasn't selected), pull the code out of
        /// SharedData and notify the host to show the 3-choice dialog.
        /// </summary>
        private void DetectAndSurfacePendingWrite(AgentActionPlan plan)
        {
            if (plan == null || plan.Steps == null) return;
            var pending = plan.Steps.FirstOrDefault(s =>
                (s.ActionType == "Write" || s.ActionType == "Import") &&
                s.State == AgentExecutionState.WaitingConfirmation);
            if (pending == null) return;

            string code = plan.SharedData.TryGetValue("PendingWriteCode", out var c) ? c as string : null;
            string name = plan.SharedData.TryGetValue("PendingWriteName", out var n) ? n as string : null;
            string type = plan.SharedData.TryGetValue("PendingWriteType", out var t) ? t as string : null;
            if (string.IsNullOrWhiteSpace(code))
            {
                AppLogger.Log("[CHAT] Pending write detected but PendingWriteCode is empty, skipping dialog.");
                return;
            }

            _pendingWriteCode = code;
            _pendingWriteName = string.IsNullOrWhiteSpace(name) ? "AI_Generated_Block" : name;
            _pendingWriteType = string.IsNullOrWhiteSpace(type) ? "FB" : type;
            AppLogger.Log($"[CHAT] Pending write surfaced for '{_pendingWriteName}' ({_pendingWriteType}), {code.Length} chars.");
            PendingWriteAwaitingChoice?.Invoke(_pendingWriteName, _pendingWriteType);
        }

        /// <summary>
        /// User picked "TIA Root" → import deferred code into PlcSoftware.BlockGroup root.
        /// </summary>
        public void ApplyPendingWriteToTiaRoot()
        {
            if (string.IsNullOrWhiteSpace(_pendingWriteCode)) return;
            var (code, name, type) = ConsumePendingWrite();
            ImportRequested?.Invoke(code, name, type, "");
        }

        /// <summary>
        /// User picked "Pick group" → host opens StatusBar chip popup. We arm a one-shot flag so that
        /// when the user finally picks a group (TargetBlockGroupPath / TargetTypeGroupPath changes from
        /// empty to non-empty), we auto-fire the import. This is wired by the host calling
        /// <see cref="ConsumePendingPickGroupAndImport"/> after the property changes.
        /// </summary>
        public void RequestPickGroupForPendingWrite()
        {
            if (string.IsNullOrWhiteSpace(_pendingWriteCode)) return;
            _pendingPickGroupAfterSelection = true;
            OpenBlockGroupPickerRequested?.Invoke();
        }

        /// <summary>
        /// Called by the host after user picked a group while pending. Reads the now-non-empty target
        /// group path, imports the deferred code, and clears the pending state.
        /// </summary>
        public void ConsumePendingPickGroupAndImport()
        {
            if (!_pendingPickGroupAfterSelection) return;
            if (string.IsNullOrWhiteSpace(_pendingWriteCode)) { _pendingPickGroupAfterSelection = false; return; }

            bool isUdt = string.Equals(_pendingWriteType, "UDT", StringComparison.OrdinalIgnoreCase);
            string groupPath = isUdt ? TargetTypeGroupPath : TargetBlockGroupPath;
            if (string.IsNullOrEmpty(groupPath))
            {
                // User cleared the chip and triggered again → don't write; wait for the next change
                return;
            }

            var (code, name, type) = ConsumePendingWrite();
            _pendingPickGroupAfterSelection = false;
            ImportRequested?.Invoke(code, name, type, groupPath);
        }

        /// <summary>
        /// User picked "Save to local file" → host pops SaveFileDialog. We just hand the code over.
        /// </summary>
        public void RequestSavePendingWriteToLocal()
        {
            if (string.IsNullOrWhiteSpace(_pendingWriteCode)) return;
            string suggestedName = (_pendingWriteName ?? "AI_Generated_Block") + ".scl";
            var (code, _, _) = ConsumePendingWrite();
            SavePendingWriteToLocalRequested?.Invoke(suggestedName, code);
        }

        /// <summary>
        /// Cancel: just clear the pending state (the SCL is still visible in the chat for copy/preview).
        /// </summary>
        public void ClearPendingWrite()
        {
            _pendingWriteCode = null;
            _pendingWriteName = null;
            _pendingWriteType = null;
            _pendingPickGroupAfterSelection = false;
        }

        private (string code, string name, string type) ConsumePendingWrite()
        {
            var c = _pendingWriteCode;
            var n = _pendingWriteName;
            var t = _pendingWriteType;
            _pendingWriteCode = null;
            _pendingWriteName = null;
            _pendingWriteType = null;
            return (c, n, t);
        }

        #endregion

        #region Private Helpers

        private void SaveCurrentSession()
        {
            if (_currentSession != null && _chatMessages.Count > 0)
            {
                _currentSession.Messages = new List<ChatMessage>(_chatMessages);
                _chatHistoryManager.SaveSession(_currentSession);
            }
        }

        #endregion
    }

}
