using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Controls
{
    public partial class ChatPanelControl : UserControl
    {
        public ChatPanelControl()
        {
            InitializeComponent();

            AgentInputBox.SendRequested += AgentInputBox_SendRequested;
            AgentInputBox.SendWithImagesRequested += AgentInputBox_SendWithImagesRequested;
            AgentInputBox.StopRequested += AgentInputBox_StopRequested;
            AgentInputBox.ModelChanged += AgentInputBox_ModelChanged;
            AgentInputBox.QuickCommandInvoked += AgentInputBox_QuickCommandInvoked;

            TraeMessageList.ScrollToBottomRequested += TraeMessageList_ScrollToBottomRequested;
            TraeMessageList.ConfirmStepRequested += TraeMessageList_ConfirmStepRequested;
            TraeMessageList.CancelStepRequested += TraeMessageList_CancelStepRequested;
        }

        private void TraeMessageList_ConfirmStepRequested(object sender, AgentActionStep e)
        {
            ConfirmStepClick?.Invoke(this, e);
        }

        private void TraeMessageList_CancelStepRequested(object sender, AgentActionStep e)
        {
            CancelStepClick?.Invoke(this, e);
        }

        private void AgentInputBox_SendRequested(object sender, string text)
        {
            if (GenerateRequested != null)
            {
                GenerateRequested.Invoke(this, text);
            }
            else
            {
                GenerateClick?.Invoke(this, new RoutedEventArgs());
            }
        }

        private void AgentInputBox_SendWithImagesRequested(object sender, (string text, List<byte[]> images) args)
        {
            GenerateWithImagesRequested?.Invoke(this, args);
        }

        private void AgentInputBox_StopRequested(object sender, EventArgs e)
        {
            StopClick?.Invoke(this, new RoutedEventArgs());
        }

        private void AgentInputBox_ModelChanged(object sender, string model)
        {
            // The event args is string, but ModelChanged event uses string
            ModelChanged?.Invoke(this, model);
        }

        private void AgentInputBox_QuickCommandInvoked(object sender, string intent)
        {
            QuickCommandInvoked?.Invoke(this, intent);
        }

        private void TraeMessageList_ScrollToBottomRequested(object sender, EventArgs e)
        {
            ScrollToBottomRequested?.Invoke(this, e);
        }

        public TraeMessageList MessageListControl => TraeMessageList;
        public AgentInputBox InputBoxControl => AgentInputBox;

        private DispatcherTimer _toastTimer;

        /// <summary>
        /// Show a non-modal toast over the message list. Used for soft notices (e.g. the
        /// tool-call soft limit) that must inform the user WITHOUT blocking the running agent.
        /// Auto-hides after <paramref name="autoHideMs"/>; repeated calls reuse the same toast.
        /// </summary>
        public void ShowToast(string text, int autoHideMs = 6000)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowToast(text, autoHideMs)));
                return;
            }

            txtToast.Text = text;
            borderToast.Visibility = Visibility.Visible;
            borderToast.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(autoHideMs) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
                fadeOut.Completed += (_, __) => borderToast.Visibility = Visibility.Collapsed;
                borderToast.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };
            _toastTimer.Start();
        }

        /// <summary>The drawer that shows agent approval / choice / input requests above the input box.</summary>
        public InteractionDrawer InteractionDrawerControl => AgentInteractionDrawer;

        public TextBlock TitleLabel => lblAiTitle;
        public Button NewChatButton => btnNewChat;
        public Button ChatHistoryButton => btnChatHistory;
        public TextBox PreviewTextBox => txtPreview;
        public System.Windows.Controls.Primitives.Popup PreviewPopup => popCodePreview;
        public WrapPanel ReferencesPanel => wrapReferences;
        public Border ReferencesBorder => borderReferences;

        public event RoutedEventHandler NewChatClick;
        public event RoutedEventHandler ChatHistoryClick;
        public event RoutedEventHandler GenerateClick;
        /// <summary>Send event with prompt text; more reliable than GenerateClick (no need for the host to read InputBox text again)</summary>
        public event EventHandler<string> GenerateRequested;
        /// <summary>Send event with images</summary>
        public event EventHandler<(string text, List<byte[]> images)> GenerateWithImagesRequested;
        public event RoutedEventHandler StopClick;
        public event RoutedEventHandler ClosePreviewClick;
        public event EventHandler ScrollToBottomRequested;
        public event EventHandler<string> ModelChanged;
        public event EventHandler<string> QuickCommandInvoked;
        public event EventHandler<ChatMessage> CopyCodeClick;
        public event EventHandler<ChatMessage> PreviewCodeClick;
        public event EventHandler<AgentActionStep> ConfirmStepClick;
        public event EventHandler<AgentActionStep> CancelStepClick;

        private void NewChat_Click(object sender, RoutedEventArgs e) => NewChatClick?.Invoke(this, e);
        private void ChatHistory_Click(object sender, RoutedEventArgs e) => ChatHistoryClick?.Invoke(this, e);
        private void btnClosePreview_Click(object sender, RoutedEventArgs e)
        {
            popCodePreview.IsOpen = false;
            ClosePreviewClick?.Invoke(this, e);
        }

        private void CopyCode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ChatMessage msg)
            {
                CopyCodeClick?.Invoke(this, msg);
            }
        }

        private void PreviewCode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ChatMessage msg)
            {
                PreviewCodeClick?.Invoke(this, msg);
            }
        }
    }
}
