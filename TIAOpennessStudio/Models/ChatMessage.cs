using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Models
{
    public class ChatMessage : INotifyPropertyChanged
    {
        private string _role;
        private string _content = "";
        private string _thinking = "";
        private bool _isThinking;
        private bool _isStreaming;
        private AgentActionPlan _plan;
        private DevTraceData _devTrace;
        private DateTime _timestamp = DateTime.Now;

        public System.Collections.ObjectModel.ObservableCollection<IMessageBlock> MessageBlocks { get; } = new System.Collections.ObjectModel.ObservableCollection<IMessageBlock>();

        /// <summary>
        /// References that were attached to this message when it was sent.
        /// Mainly informational - lets the UI render a strip of attached files / blocks
        /// so the user can recall what context the assistant saw.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<AttachedReferenceInfo> AttachedReferences { get; } = new System.Collections.ObjectModel.ObservableCollection<AttachedReferenceInfo>();

        public bool HasAttachedReferences => AttachedReferences != null && AttachedReferences.Count > 0;

        /// <summary>
        /// Base64-encoded images attached to this user message.
        /// Only relevant for user messages. Each entry is a base64 string (without data URI prefix).
        /// </summary>
        public System.Collections.Generic.List<string> Images { get; set; }

        public bool HasImages => Images != null && Images.Count > 0;

        public string Role
        {
            get => _role;
            set { _role = value; OnPropertyChanged(); OnPropertyChanged(nameof(RoleIconPath)); }
        }

        private static readonly Random _iconRandom = new Random();
        private string _cachedRoleIconPath;
        private int _cachedThemeVersion;

        /// <summary>
        /// Incremented on theme changes to invalidate cached icon paths.
        /// </summary>
        private static int s_themeVersion;
        private static ThemeMode s_currentTheme = ThemeMode.Dark;
        private static bool s_themeInitialized;

        /// <summary>
        /// Subscribes to IThemeService.ThemeChanged once so icon paths follow theme switches.
        /// </summary>
        public static void InitializeThemeTracking(IThemeService themeService)
        {
            if (s_themeInitialized) return;
            s_themeInitialized = true;
            s_currentTheme = themeService.CurrentTheme;
            themeService.ThemeChanged += (_, mode) =>
            {
                s_currentTheme = mode;
                s_themeVersion++;
            };
        }

        /// <summary>
        /// Icon path for the message role: the agent uses robot-light.png / robot-dark.png
        /// by theme; the user picks one of human1/2/3.png at random.
        /// </summary>
        public string RoleIconPath
        {
            get
            {
                if (_cachedRoleIconPath != null && _cachedThemeVersion == s_themeVersion)
                    return _cachedRoleIconPath;

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (_role == "user")
                {
                    int idx = _iconRandom.Next(1, 4); // 1, 2, 3
                    _cachedRoleIconPath = System.IO.Path.Combine(baseDir, "Ressources", $"human{idx}.png");
                }
                else
                {
                    string iconName = s_currentTheme == ThemeMode.Light ? "robot-light.png" : "robot-dark.png";
                    _cachedRoleIconPath = System.IO.Path.Combine(baseDir, "Ressources", iconName);
                }
                _cachedThemeVersion = s_themeVersion;
                return _cachedRoleIconPath;
            }
        }

        public AgentActionPlan Plan
        {
            get => _plan;
            set { _plan = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Dev Mode debug trace. Present only on assistant messages; holds the raw
        /// prompt, tool-call JSON, and TIA API return codes.
        /// </summary>
        public DevTraceData DevTrace
        {
            get => _devTrace;
            set { _devTrace = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDevTrace)); }
        }

        public bool HasDevTrace => _devTrace != null && _devTrace.HasData;

        public DateTime Timestamp
        {
            get => _timestamp;
            set { _timestamp = value; OnPropertyChanged(); }
        }

        public bool IsStreaming
        {
            get => _isStreaming;
            set { _isStreaming = value; OnPropertyChanged(); }
        }

        public string Content
        {
            get => _content;
            set 
            { 
                if (_content != value)
                {
                    _content = value; 
                    // ParseContent builds the text/code blocks (including the fenced code split).
                    // Do not also push the raw text into the trailing block here — that used to
                    // append the unsplit markdown (fences included) after the parsed blocks.
                    ParseContent();
                    OnPropertyChanged(); 
                }
            }
        }

        public string Thinking
        {
            get => _thinking;
            set { _thinking = value; OnPropertyChanged(); }
        }

        public bool IsThinking
        {
            get => _isThinking;
            set { _isThinking = value; OnPropertyChanged(); }
        }

        private string _displayContent = "";
        public string DisplayContent
        {
            get => _displayContent;
            set { _displayContent = value; OnPropertyChanged(); }
        }

        private string _displayCode = "";
        public string DisplayCode
        {
            get => _displayCode;
            set { _displayCode = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCode)); }
        }

        public bool HasCode => !string.IsNullOrWhiteSpace(_displayCode);

        // ---- Write-back undo snapshot ----

        /// <summary>
        /// The block name that was written back when this assistant message was generated.
        /// Null when this message did not trigger a write-back.
        /// </summary>
        public string UndoBlockName  { get; set; }

        /// <summary>
        /// The block type (FB / FC / DB / UDT) of the written-back block.
        /// </summary>
        public string UndoBlockType  { get; set; }

        /// <summary>
        /// TIA XML export captured immediately before the write-back.
        /// Null = no write-back was performed, or the block was brand-new (no prior content).
        /// </summary>
        public string UndoSnapshot   { get; set; }

        private bool _isGenerating;
        /// <summary>
        /// True while the session is actively streaming. Bound to the Undo button's
        /// IsEnabled so it cannot be clicked mid-generation.
        /// </summary>
        public bool IsGenerating
        {
            get => _isGenerating;
            set { _isGenerating = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanUndo)); }
        }

        /// <summary>
        /// True when this message has a valid undo snapshot AND no generation is in progress.
        /// Drives the Undo button's Visibility and IsEnabled in the chat template.
        /// </summary>
        public bool CanUndo => UndoSnapshot != null && !_isGenerating;

        public void AppendContentDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            _content += delta;
            DisplayContent += delta;

            // Keep the trailing text block in MessageBlocks updated so TraeMessageList
            // (bound to MessageBlocks) still shows text on the plain streaming path, where
            // only content/thinking deltas are sent without structured blocks.
            SyncTrailingTextBlock(DisplayContent);

            OnPropertyChanged(nameof(Content));
        }

        public void FinalizeContent()
        {
            ParseContent();
            OnPropertyChanged(nameof(Content));
        }

        /// <summary>
        /// Updates the trailing text block in MessageBlocks, creating one if absent.
        /// </summary>
        private void SyncTrailingTextBlock(string text)
        {
            for (int i = MessageBlocks.Count - 1; i >= 0; i--)
            {
                if (MessageBlocks[i] is MessageTextBlock tb)
                {
                    tb.Content = text ?? "";
                    tb.IsStreaming = this.IsStreaming;
                    
                    // Detect code being streamed (text starts with a code fence).
                    if (tb.Content.TrimStart().StartsWith("```"))
                    {
                        tb.IsStreamingCode = true;
                    }
                    else
                    {
                        tb.IsStreamingCode = false;
                    }
                    
                    return;
                }
            }
            
            var newBlock = new MessageTextBlock { Content = text ?? "", IsMarkdown = true, IsStreaming = this.IsStreaming };
            if (newBlock.Content.TrimStart().StartsWith("```"))
            {
                newBlock.IsStreamingCode = true;
            }
            MessageBlocks.Add(newBlock);
        }

        // Kept for backward compatibility; currently uncalled but retained in case of
        // potential external references.
        private MessageTextBlock EnsureTrailingTextBlock()
        {
            for (int i = MessageBlocks.Count - 1; i >= 0; i--)
            {
                if (MessageBlocks[i] is MessageTextBlock tb) return tb;
            }
            var newBlock = new MessageTextBlock { Content = "", IsMarkdown = true };
            MessageBlocks.Add(newBlock);
            return newBlock;
        }

        // ---- Structured stream event entry points (AgentStreamEvent -> MessageBlocks) ----

        /// <summary>
        /// Appends a thinking fragment. Reuses the trailing ThoughtBlock when it is still
        /// the last block, otherwise starts a new one.
        /// </summary>
        public void AppendThinking(string text, ThoughtKind kind = ThoughtKind.General)
        {
            if (string.IsNullOrEmpty(text)) return;
            // Reuse the trailing ThoughtBlock; if the tail is a text/tool block, start a
            // fresh one for visual clarity.
            ThoughtBlock tail = null;
            for (int i = MessageBlocks.Count - 1; i >= 0; i--)
            {
                if (MessageBlocks[i] is ThoughtBlock tb) { tail = tb; break; }
                if (MessageBlocks[i] is MessageTextBlock) break; // body text has begun; start a new thought
                if (MessageBlocks[i] is CommandExecutionBlock) break;
            }

            // Thinking streams token by token: append verbatim to preserve the model's own
            // spacing/newlines. Never trim the delta or force a line break — doing so would
            // split every token onto its own line and swallow inter-token spaces.
            if (tail == null)
            {
                Thinking = (Thinking ?? "") + text;
                tail = new ThoughtBlock { Content = text, IsExpanded = false, Kind = kind };
                MessageBlocks.Add(tail);
            }
            else
            {
                Thinking = (Thinking ?? "") + text;
                tail.Content = (tail.Content ?? "") + text;
                if (kind != ThoughtKind.General && tail.Kind == ThoughtKind.General)
                {
                    tail.Kind = kind;
                }
            }
        }

        /// <summary>
        /// Updates (or creates) the single PlanProgressBlock on this message with the latest job progress.
        /// </summary>
        public void UpdatePlanProgress(int total, int done, int failed, string current)
        {
            var block = MessageBlocks.OfType<PlanProgressBlock>().FirstOrDefault();
            if (block == null)
            {
                block = new PlanProgressBlock();
                MessageBlocks.Add(block);
            }
            block.Total = total;
            block.Done = done;
            block.Failed = failed;
            block.Current = current;
        }

        /// <summary>
        /// Tool call started: creates a CommandExecutionBlock (Status=Running). The caller
        /// marks the returned block done via <see cref="FinishToolCall"/> on ToolEnd.
        /// </summary>
        public CommandExecutionBlock BeginToolCall(string toolName, string input = null)
        {
            var block = new CommandExecutionBlock
            {
                CommandText = string.IsNullOrEmpty(input) ? toolName : $"{toolName}\n{input}",
                Status = CommandStatus.Running,
                Output = ""
            };
            MessageBlocks.Add(block);
            return block;
        }

        /// <summary>
        /// Tool call ended: marks the matching Running CommandExecutionBlock complete.
        /// The cloud agent can run several tools in parallel and they finish in any order, so
        /// pair by tool name first (earliest Running block of that name); only when no name
        /// matches fall back to the last Running block. No-op if nothing is Running.
        /// </summary>
        public void FinishToolCall(string toolName, string output = null, bool success = true)
        {
            CommandExecutionBlock target = null;
            CommandExecutionBlock lastRunning = null;

            for (int i = 0; i < MessageBlocks.Count; i++)
            {
                if (!(MessageBlocks[i] is CommandExecutionBlock cmd) || cmd.Status != CommandStatus.Running)
                    continue;

                lastRunning = cmd;
                if (target == null && !string.IsNullOrEmpty(toolName) && IsCallOfTool(cmd.CommandText, toolName))
                    target = cmd;
            }

            target = target ?? lastRunning;
            if (target == null) return;

            target.Status = success ? CommandStatus.Success : CommandStatus.Failed;
            if (!string.IsNullOrEmpty(output))
            {
                target.Output = output;
            }
        }

        // BeginToolCall stores CommandText as "toolName" or "toolName\n<input>".
        private static bool IsCallOfTool(string commandText, string toolName)
        {
            if (string.IsNullOrEmpty(commandText)) return false;
            return commandText.Equals(toolName, StringComparison.Ordinal)
                || commandText.StartsWith(toolName + "\n", StringComparison.Ordinal);
        }

        /// <summary>
        /// Error event: appends a red failure status block.
        /// </summary>
        public void AppendError(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            MessageBlocks.Add(new AgentStatusBlock { Message = text, Status = "failed" });
        }

        /// <summary>
        /// Collapses all thought blocks. Typically called when a task completes.
        /// </summary>
        public void CollapseThoughts()
        {
            foreach (var block in MessageBlocks)
            {
                if (block is ThoughtBlock tb)
                {
                    tb.IsExpanded = false;
                }
            }
        }

        private void ParseContent()
        {
            if (string.IsNullOrEmpty(_content)) 
            {
                 DisplayContent = "";
                 DisplayCode = "";
                 // Never drop non-text/code structured blocks (e.g. ThoughtBlock).
                 var keepBlocks = MessageBlocks.Where(b => !(b is MessageTextBlock || b is CodeBlock)).ToList();
                 MessageBlocks.Clear();
                 foreach (var b in keepBlocks) MessageBlocks.Add(b);
                 return;
            }

            // Keep blocks that are not plain text/code (e.g. ThoughtBlock, CommandExecutionBlock, AgentStatusBlock).
            var preservedBlocks = MessageBlocks.Where(b => !(b is MessageTextBlock || b is CodeBlock)).ToList();
            MessageBlocks.Clear();
            
            // Put preserved structured blocks back first (thoughts usually lead).
            foreach (var b in preservedBlocks)
            {
                MessageBlocks.Add(b);
            }

            DisplayCode = "";

            // Line-based fence scanning: only a fence that starts its own line opens or
            // closes a code block. The previous IndexOf("```") approach also cut at
            // backticks embedded in code/prose and ignored info strings longer than ~17
            // characters, which made the split look arbitrary.
            var textPart = new StringBuilder();
            var codePart = new StringBuilder();
            bool inFence = false;
            char fenceChar = '`';
            int fenceLength = 3;
            string fenceLanguage = null;

            void FlushText()
            {
                var value = textPart.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    MessageBlocks.Add(new MessageTextBlock { Content = value, IsMarkdown = true });
                }
                textPart.Clear();
            }

            void FlushCode()
            {
                // Trim only line breaks: indentation inside the block must survive.
                var value = codePart.ToString().Trim('\r', '\n');
                if (!string.IsNullOrWhiteSpace(value))
                {
                    MessageBlocks.Add(new CodeBlock
                    {
                        Code = value,
                        Language = string.IsNullOrEmpty(fenceLanguage) ? "scl" : fenceLanguage
                    });
                    if (string.IsNullOrEmpty(DisplayCode)) DisplayCode = value;
                }
                codePart.Clear();
            }

            int lineStart = 0;
            while (lineStart <= _content.Length)
            {
                int lineEnd = _content.IndexOf('\n', lineStart);
                string line = lineEnd < 0
                    ? _content.Substring(lineStart)
                    : _content.Substring(lineStart, lineEnd - lineStart);
                if (line.Length > 0 && line[line.Length - 1] == '\r')
                    line = line.Substring(0, line.Length - 1);

                if (!inFence)
                {
                    if (TryReadFence(line, out char ch, out int len, out string info))
                    {
                        FlushText();
                        inFence = true;
                        fenceChar = ch;
                        fenceLength = len;
                        fenceLanguage = NormalizeFenceLanguage(info);
                    }
                    else
                    {
                        textPart.Append(line).Append('\n');
                    }
                }
                else if (IsFenceCloser(line, fenceChar, fenceLength))
                {
                    FlushCode();
                    inFence = false;
                    fenceLanguage = null;
                }
                else
                {
                    codePart.Append(line).Append('\n');
                }

                if (lineEnd < 0) break;
                lineStart = lineEnd + 1;
            }

            // Unterminated fence: keep what we have as code (streaming can end mid-block).
            if (inFence) FlushCode();
            FlushText();

            // For backward compatibility, set DisplayContent to the full text.
            DisplayContent = _content;
        }

        /// <summary>
        /// Recognizes an opening fence: at least three backticks or tildes at the start of
        /// a line, optionally followed by an info string (language, filename, ...).
        /// </summary>
        private static bool TryReadFence(string line, out char fenceChar, out int length, out string info)
        {
            fenceChar = '`';
            length = 0;
            info = null;
            if (line == null) return false;

            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i >= line.Length || (line[i] != '`' && line[i] != '~')) return false;

            char ch = line[i];
            int run = i;
            while (run < line.Length && line[run] == ch) run++;
            if (run - i < 3) return false;

            fenceChar = ch;
            length = run - i;
            info = line.Substring(run).Trim();
            return true;
        }

        private static bool IsFenceCloser(string line, char fenceChar, int openLength)
        {
            if (line == null) return false;

            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i >= line.Length || line[i] != fenceChar) return false;

            int run = i;
            while (run < line.Length && line[run] == fenceChar) run++;
            if (run - i < openLength) return false;

            // A closing fence carries no info string.
            return line.Substring(run).Trim().Length == 0;
        }

        /// <summary>
        /// Maps common fence info strings to the ids CodeBlockControl understands.
        /// Returns null for a bare fence so the caller can keep the SCL default.
        /// </summary>
        private static string NormalizeFenceLanguage(string info)
        {
            if (string.IsNullOrWhiteSpace(info)) return null;

            // Only the first token matters: ```scl title="FB1" -> scl
            var token = info.Trim()
                .Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries)[0]
                .Trim(':')
                .ToLowerInvariant();

            switch (token)
            {
                case "cs":
                case "c#":
                    return "csharp";
                case "js":
                case "node":
                    return "javascript";
                case "ts":
                    return "typescript";
                case "py":
                    return "python";
                case "sh":
                case "shell":
                    return "bash";
                case "aml":
                    return "xml";
                case "awl":
                    return "stl";
                case "txt":
                case "plain":
                case "plaintext":
                    return "text";
                default:
                    return token;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Lightweight info about a reference attached to a chat message.
    /// Stored separately from <see cref="AiReferenceItem"/> so it survives serialization
    /// without dragging the full packed payload into chat history.
    /// </summary>
    public class AttachedReferenceInfo
    {
        public string DisplayName { get; set; }
        public string Source { get; set; }
        public int CharCount { get; set; }
    }
}
