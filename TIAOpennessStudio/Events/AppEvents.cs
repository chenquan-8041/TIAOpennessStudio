using System;
using Prism.Events;
using Siemens.Engineering.SW;
using TIAOpennessStudio.AICore;
using TIAOpennessStudio.ViewModels;

namespace TIAOpennessStudio.Events
{
    // UI interaction events
    public class ShowMessageEvent : PubSubEvent<string> { }
    public class ShowOverlayEvent : PubSubEvent<string> { }
    public class HideOverlayEvent : PubSubEvent { }
    public class ShowCodeViewerEvent : PubSubEvent<CodeViewerEventArgs> { }
    public class ShowChoiceDialogEvent : PubSubEvent<ChoiceDialogEventArgs> { }

    /// <summary>Requests opening an OPC UA live monitor page in the central area.</summary>
    public class ShowOpcUaMonitorEvent : PubSubEvent<OpcUaMonitorEventArgs> { }

    // Chat events
    public class ScrollToBottomEvent : PubSubEvent { }
    public class CopyToClipboardEvent : PubSubEvent<string> { }
    public class ImportCodeEvent : PubSubEvent<string> { }
    public class ReferencesChangedEvent : PubSubEvent { }

    /// <summary>Chat code block: insert its text at the caret of the chat prompt box.</summary>
    public class InsertCodeToInputEvent : PubSubEvent<string> { }

    /// <summary>Chat code block: save its text as a file into the selected Explorer folder.</summary>
    public class SaveCodeBlockEvent : PubSubEvent<SaveCodeBlockPayload> { }

    public class SaveCodeBlockPayload
    {
        public string Text { get; set; }
        public string Language { get; set; }
        public string FileName { get; set; }

        /// <summary>Reports success and the written path (or the failure reason) back to the button.</summary>
        public Action<bool, string> Completed { get; set; }
    }

    // Project events
    public class ProjectStatusChangedEvent : PubSubEvent { }
    public class RefreshTreeEvent : PubSubEvent { }
    public class ImportSuccessfulEvent : PubSubEvent { }

    // Tree view events
    public class UpdateProjectStatusEvent : PubSubEvent { }
    public class RefreshBlockCountsEvent : PubSubEvent { }
    public class UpdateMenuEnableStatesEvent : PubSubEvent { }
    // Parameterless: the tree is rebuilt from service-side DTOs for the currently selected PLC
    // (set via SelectPlcByHandleAsync). No COM object is carried on the event.
    public class LoadTreeEvent : PubSubEvent { }

    // Graph events
    public class SaveFileDialogEvent : PubSubEvent<string> { }
    public class SaveFileCompletedEvent : PubSubEvent<FileSaveEventArgs> { }

    // AI config events
    public class ConfigurationSavedEvent : PubSubEvent<AIConfig> { }
    public class BackendStatusChangedEvent : PubSubEvent<string> { }

    // Global progress events
    public class GlobalProgressEvent : PubSubEvent<GlobalProgressPayload> { }

    /// <summary>
    /// Payload for the global progress bar in the StatusBar.
    /// </summary>
    public class GlobalProgressPayload
    {
        /// <summary>Whether the progress bar should be visible.</summary>
        public bool IsVisible { get; set; }

        /// <summary>Short message shown next to the progress bar.</summary>
        public string Message { get; set; }

        /// <summary>
        /// Progress percentage (0-100). Use -1 for indeterminate mode.
        /// </summary>
        public double Percent { get; set; } = -1;

        /// <summary>Optional phase identifier for multi-phase operations.</summary>
        public string Phase { get; set; }

        public static GlobalProgressPayload Show(string message, double percent = -1, string phase = null)
            => new GlobalProgressPayload { IsVisible = true, Message = message, Percent = percent, Phase = phase };

        public static GlobalProgressPayload Hide()
            => new GlobalProgressPayload { IsVisible = false };
    }

    /// <summary>
    /// Payload for an OPC UA live monitor page request: title (taken from node attributes) + a pre-built monitor control.
    /// </summary>
    public class OpcUaMonitorEventArgs
    {
        public string Title { get; }
        public System.Windows.UIElement Content { get; }

        public OpcUaMonitorEventArgs(string title, System.Windows.UIElement content)
        {
            Title = title;
            Content = content;
        }
    }
}
