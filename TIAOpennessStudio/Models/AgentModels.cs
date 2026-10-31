using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace TIAOpennessStudio.Models
{
    public enum AgentIntent
    {
        Unknown,
        Chat,           // Pure conversation
        ExplainCode,    // Explain SCL/LAD logic
        ModifyCode,     // Generate fix or modify logic
        Compile,        // Compile and verify
        TraceVariable,  // Find cross-references
        GenerateCode,   // Create new block from scratch
        Diagnose,       // Full diagnosis (Read-only)
        GenerateAndFix  // Generate + compile + auto-fix loop
    }

    public enum AgentExecutionState
    {
        Pending,
        Planning,
        Running,
        Tooling,
        Editing,
        Verifying,
        WaitingConfirmation,
        Failed,
        Done,
        Cancelled
    }

    public enum ExecutionPhase
    {
        None,
        Planning,
        ContextBuilding,
        ToolExecution,
        CodeGeneration,
        CodeModification,
        Verification,
        Confirmation,
        Finalization,
        Compiling,      
        Fixing,        
        AwaitingRetry    
    }

    public enum ErrorCode
    {
        None = 0,
        NetworkError,
        AuthError,
        QuotaError,
        RateLimitError,
        ServiceUnavailable,
        ValidationError,
        ToolExecutionError,
        AgentLogicError,
        SystemError
    }

    public enum FileChangeAction
    {
        Modified,
        Added,
        Deleted
    }

    public enum CommandStatus
    {
        Running,
        Success,
        Failed
    }

    public enum ContextRefType
    {
        File,
        Variable,
        Function,
        Udt
    }

    // ========== Trae-style message block models ==========

    public interface IMessageBlock
    {
        string BlockType { get; }
        DateTime Timestamp { get; }
    }

    public class MessageTextBlock : IMessageBlock, INotifyPropertyChanged
    {
        public string BlockType => "text";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        private string _content;
        public string Content
        {
            get => _content;
            set
            {
                if (_content != value)
                {
                    _content = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
                }
            }
        }

        private bool _isStreamingCode = false;
        public bool IsStreamingCode
        {
            get => _isStreamingCode;
            set
            {
                if (_isStreamingCode != value)
                {
                    _isStreamingCode = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStreamingCode)));
                }
            }
        }

        private bool _isStreaming = false;
        public bool IsStreaming
        {
            get => _isStreaming;
            set
            {
                if (_isStreaming != value)
                {
                    _isStreaming = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStreaming)));
                }
            }
        }

        public bool IsMarkdown { get; set; } = true;

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public enum ThoughtKind
    {
        General,
        Analysis,
        Creative,
        Warning,
        Error
    }

    public class ThoughtBlock : IMessageBlock, INotifyPropertyChanged
    {
        public string BlockType => "thought";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        private ThoughtKind _kind = ThoughtKind.General;
        public ThoughtKind Kind
        {
            get => _kind;
            set { if (_kind != value) { _kind = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Kind))); } }
        }

        private string _content;
        public string Content
        {
            get => _content;
            set
            {
                if (_content != value)
                {
                    _content = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CharCount)));
                }
            }
        }

        private bool _isExpanded = false;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded != value) { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); } }
        }

        public string ContentPreview
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Content))
                    return "";
                var lines = Content.Split('\n');
                var preview = string.Join(" ", lines.Take(2)).Trim();
                return preview.Length > 60 ? preview.Substring(0, 60) + "..." : preview;
            }
        }

        public int CharCount => Content?.Length ?? 0;

        public string Details { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class FileChangeBlock : IMessageBlock
    {
        public string BlockType => "file_change";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string FileName { get; set; }
        public string RelativePath { get; set; }
        public FileChangeAction Action { get; set; } = FileChangeAction.Modified;
        public int LinesAdded { get; set; }
        public int LinesRemoved { get; set; }
        public string Summary { get; set; }
    }

    public class CommandExecutionBlock : IMessageBlock, INotifyPropertyChanged
    {
        public string BlockType => "command";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        private string _commandText;
        public string CommandText
        {
            get => _commandText;
            set { if (_commandText != value) { _commandText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CommandText))); } }
        }

        private CommandStatus _status = CommandStatus.Running;
        public CommandStatus Status
        {
            get => _status;
            set { if (_status != value) { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
        }

        private string _output;
        public string Output
        {
            get => _output;
            set { if (_output != value) { _output = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Output))); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class ContextRefBlock : IMessageBlock
    {
        public string BlockType => "context_ref";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public ContextRefType RefType { get; set; } = ContextRefType.File;
        public string Name { get; set; }
        public string Path { get; set; }
        public int Line { get; set; }
    }

    public class AgentStatusBlock : IMessageBlock
    {
        public string BlockType => "status";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Status { get; set; }  // analyzing, completed, failed, waiting
        public string Message { get; set; }
        public string Details { get; set; }
    }

    public class CodeBlock : IMessageBlock
    {
        public string BlockType => "code";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Language { get; set; } = "scl";
        public string Code { get; set; }
        public string FileName { get; set; }
    }

    public enum DeliveryItemStatus
    {
        Written,
        PreparedNotWritten,
        Failed
    }

    public class DeliveryItem
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Language { get; set; }
        public int? Number { get; set; }
        public bool? Compiled { get; set; }
        public DeliveryItemStatus Status { get; set; }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case DeliveryItemStatus.Written:
                        return Compiled == true ? "已写入 · 编译通过" : "已写入";
                    case DeliveryItemStatus.Failed:
                        return "失败";
                    default:
                        return "未写入";
                }
            }
        }
    }

    public class DeliveryBlock : IMessageBlock
    {
        public string BlockType => "delivery";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public ObservableCollection<DeliveryItem> Items { get; } = new ObservableCollection<DeliveryItem>();
        public string Summary { get; set; }
    }

    /// <summary>Project-plan execution progress bar block (long-task job progress), rendered as a ProgressBar.</summary>
    public class PlanProgressBlock : IMessageBlock, INotifyPropertyChanged
    {
        public string BlockType => "plan_progress";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        private int _total;
        public int Total { get => _total; set { if (_total != value) { _total = value; Notify(nameof(Total)); Notify(nameof(Progress)); Notify(nameof(DisplayText)); } } }

        private int _done;
        public int Done { get => _done; set { if (_done != value) { _done = value; Notify(nameof(Done)); Notify(nameof(Progress)); Notify(nameof(DisplayText)); } } }

        private int _failed;
        public int Failed { get => _failed; set { if (_failed != value) { _failed = value; Notify(nameof(Failed)); Notify(nameof(DisplayText)); } } }

        private string _current;
        public string Current { get => _current; set { if (_current != value) { _current = value; Notify(nameof(Current)); Notify(nameof(DisplayText)); } } }

        /// <summary>0..1 progress, bound to ProgressBar.Value.</summary>
        public double Progress => _total <= 0 ? 0.0 : Math.Min(1.0, Math.Max(0.0, (double)_done / _total));

        public string DisplayText
        {
            get
            {
                var sb = new System.Text.StringBuilder($"进度 {_done}/{_total}");
                if (_failed > 0) sb.Append($"（失败 {_failed}）");
                if (!string.IsNullOrEmpty(_current)) sb.Append($" · 当前 {_current}");
                return sb.ToString();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ========== Original models ==========

    public class AgentActionStep : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string PlanId { get; set; }
        public string Description { get; set; }
        
        private AgentExecutionState _state = AgentExecutionState.Pending;
        public AgentExecutionState State 
        { 
            get => _state; 
            set { _state = value; OnPropertyChanged(); } 
        }

        public string ActionType { get; set; } // e.g., "Search", "Generate", "Write", "Compile"
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        
        private string _output;
        public string Output 
        { 
            get => _output; 
            set { _output = value; OnPropertyChanged(); } 
        }

        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class AgentActionPlan : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Rid { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public AgentIntent Intent { get; set; }
        public string Prompt { get; set; }
        public string Context { get; set; }
        public string TargetBlockName { get; set; }
        public string TargetBlockType { get; set; }
        public Dictionary<string, object> SharedData { get; set; } = new Dictionary<string, object>();
        public ObservableCollection<AgentActionStep> Steps { get; set; } = new ObservableCollection<AgentActionStep>();

        /// <summary>
        /// Multi-turn chat history fed to the LLM together with this plan's prompt.
        /// Each entry is {"role": "user"|"assistant", "content": "..."}.
        /// Set by AgentOrchestrator when the user has prior messages in the session.
        /// </summary>
        public List<Dictionary<string, string>> History { get; set; } = new List<Dictionary<string, string>>();

        private AgentExecutionState _state = AgentExecutionState.Pending;
        public AgentExecutionState State 
        { 
            get => _state; 
            set { _state = value; OnPropertyChanged(); } 
        }

        private ExecutionPhase _phase = ExecutionPhase.None;
        public ExecutionPhase Phase
        {
            get => _phase;
            set { _phase = value; OnPropertyChanged(); }
        }

        public string Summary { get; set; }
        public ToolCallTrace ToolTrace { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public int MaxRetries { get; set; } = 3;
        public int RetryCount { get; set; } = 0;
        public int MaxFixCycles { get; set; } = 3;
        public int FixCycleCount { get; set; } = 0;
        public string LastCompileErrors { get; set; }
        public bool RequiresConfirmation { get; set; }
        public bool SkipConfirmation { get; set; }
        public bool IsInFixCycle { get; set; } = false;

        /// <summary>
        /// Dev Mode debug trace. Populated automatically while ExecutePlanAsync runs; the
        /// caller (ChatViewModel) reads it after the agent completes and forwards it to ChatMessage.DevTrace.
        /// </summary>
        public DevTraceData DevTrace { get; set; }
        
        public bool CanRetry => RetryCount < MaxRetries && FixCycleCount < MaxFixCycles;
        
        public void RecordFixAttempt(string compileErrors)
        {
            FixCycleCount++;
            RetryCount++;
            LastCompileErrors = compileErrors;
            IsInFixCycle = true;
        }
        
        public void ResetFixCycle()
        {
            IsInFixCycle = false;
            LastCompileErrors = null;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Dev Mode debug trace: the full call chain behind one agent message — raw prompt,
    /// tool-call history, TIA API return codes, etc. — expanded for developer inspection.
    /// </summary>
    public class DevTraceData
    {
        /// <summary>The full System Prompt + User Prompt sent to the LLM.</summary>
        public string RawPrompt { get; set; }

        /// <summary>Tool-call history (JSON array string).</summary>
        public string ToolInvocationsJson { get; set; }

        /// <summary>TIA Portal API return codes / result summary.</summary>
        public string TiaApiResults { get; set; }

        /// <summary>Name of the LLM model.</summary>
        public string ModelName { get; set; }

        /// <summary>Total elapsed time.</summary>
        public TimeSpan TotalDuration { get; set; }

        /// <summary>Whether there is any data worth displaying.</summary>
        public bool HasData =>
            !string.IsNullOrWhiteSpace(RawPrompt) ||
            !string.IsNullOrWhiteSpace(ToolInvocationsJson) ||
            !string.IsNullOrWhiteSpace(TiaApiResults);
    }
}
