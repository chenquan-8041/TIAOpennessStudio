using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.Controls
{
    public partial class TraeMessageList : UserControl, INotifyPropertyChanged
    {
        public static readonly DependencyProperty MessagesProperty =
            DependencyProperty.Register(nameof(Messages), typeof(System.Collections.IEnumerable), typeof(TraeMessageList),
                new PropertyMetadata(null, OnMessagesChanged));

        public System.Collections.IEnumerable Messages
        {
            get => (System.Collections.IEnumerable)GetValue(MessagesProperty);
            set => SetValue(MessagesProperty, value);
        }

        // Localized labels used by the inline message/plan templates (bound via RelativeSource).
        public string AgentRoleLabel => LanguageManager.Instance.GetString("Chat.Role.Agent");
        public string UserRoleLabel => LanguageManager.Instance.GetString("Chat.Role.You");
        public string DevTraceTip => LanguageManager.Instance.GetString("Chat.DevTraceTip");
        public string ExecutionPlanLabel => LanguageManager.Instance.GetString("Chat.ExecutionPlan");
        public string ConfirmExecutionLabel => LanguageManager.Instance.GetString("Chat.ConfirmExecution");
        public string CancelLabel => LanguageManager.Instance.GetString("Kb.Common.Cancel");
        public string ThinkingLabel => LanguageManager.Instance.GetString("Chat.Thinking");
        public string ThoughtLabel => LanguageManager.Instance.GetString("Chat.Thought");

        public event EventHandler ScrollToBottomRequested;
        public event EventHandler<AgentActionStep> ConfirmStepRequested;
        public event EventHandler<AgentActionStep> CancelStepRequested;
        public event EventHandler<DevTraceData> DevTraceRequested;
        public event PropertyChangedEventHandler PropertyChanged;

        private double _lastScrollOffset;
        private bool _isAutoScrollEnabled = true;
        
        // Smooth scrolling: throttling (merged during batch adds) + frame-by-frame driving
        // (WPF ScrollViewer.VerticalOffset is marked IsAnimationProhibited, so BeginAnimation cannot be used;
        // only a timer can call ScrollToVerticalOffset frame by frame to simulate animation)
        private System.Windows.Threading.DispatcherTimer _scrollThrottleTimer;
        private System.Windows.Threading.DispatcherTimer _scrollEaseTimer;
        private bool _pendingScroll = false;
        private const int ScrollThrottleMs = 50;       // 50ms throttle
        private const int ScrollEaseFrameMs = 16;      // ~60fps
        private const double ScrollEaseFactor = 0.35;  // Push 35% toward the target each frame
        private const double ScrollEaseEpsilon = 1.0;  // Distance < 1px counts as reached
        private double _targetScrollOffset = -1;

        public TraeMessageList()
        {
            InitializeComponent();
            DataContext = this;

            // Three languages: apply initially + refresh on language change, unbind on unload.
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
            ApplyLanguage();

            MainScrollViewer.ScrollChanged += MainScrollViewer_ScrollChanged;
            
            // Initialize the scroll throttle timer
            _scrollThrottleTimer = new System.Windows.Threading.DispatcherTimer();
            _scrollThrottleTimer.Interval = TimeSpan.FromMilliseconds(ScrollThrottleMs);
            _scrollThrottleTimer.Tick += ScrollThrottleTimer_Tick;

            // Initialize the smooth-scroll timer (frame-by-frame driving instead of BeginAnimation)
            _scrollEaseTimer = new System.Windows.Threading.DispatcherTimer();
            _scrollEaseTimer.Interval = TimeSpan.FromMilliseconds(ScrollEaseFrameMs);
            _scrollEaseTimer.Tick += ScrollEaseTimer_Tick;
            
            // Clean up resources when the control is unloaded
            Unloaded += TraeMessageList_Unloaded;
        }

        private void OnLanguageChanged(AppLanguage lang)
        {
            ApplyLanguage();
        }

        /// <summary>Applies the current language strings to the empty state and template bindings.</summary>
        private void ApplyLanguage()
        {
            var lm = LanguageManager.Instance;
            if (EmptyStateTitleText != null) EmptyStateTitleText.Text = lm.GetString("Chat.EmptyTitle");
            if (EmptyStateSubtitleText != null) EmptyStateSubtitleText.Text = lm.GetString("Chat.EmptySubtitle");

            // Template labels are plain properties: notify so existing bindings re-evaluate.
            OnPropertyChanged(nameof(AgentRoleLabel));
            OnPropertyChanged(nameof(UserRoleLabel));
            OnPropertyChanged(nameof(DevTraceTip));
            OnPropertyChanged(nameof(ExecutionPlanLabel));
            OnPropertyChanged(nameof(ConfirmExecutionLabel));
            OnPropertyChanged(nameof(CancelLabel));
            OnPropertyChanged(nameof(ThinkingLabel));
            OnPropertyChanged(nameof(ThoughtLabel));
        }

        private static void OnMessagesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TraeMessageList control)
            {
                control.UpdateEmptyState();
                // The earlier implementation cast with 'as ObservableCollection<object>', but ChatViewModel.ChatMessages
                // is ObservableCollection<ChatMessage>; generics are not covariant, so 'as' always returned null,
                // meaning CollectionChanged was never subscribed → the empty-state overlay never updated.
                control.SubscribeToCollectionChanged(e.OldValue as System.Collections.Specialized.INotifyCollectionChanged);
                control.SubscribeToCollectionChanged(e.NewValue as System.Collections.Specialized.INotifyCollectionChanged);

                if (e.NewValue != null && control._isAutoScrollEnabled)
                {
                    control.ScrollToBottom();
                }
            }
        }

        private System.Collections.Specialized.NotifyCollectionChangedEventHandler _collectionChangedHandler;

        private void SubscribeToCollectionChanged(System.Collections.Specialized.INotifyCollectionChanged collection)
        {
            if (collection == null) return;

            if (_collectionChangedHandler == null)
            {
                _collectionChangedHandler = (s, e) =>
                {
                    UpdateEmptyState();

                    if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && _isAutoScrollEnabled)
                    {
                        Dispatcher.BeginInvoke(new Action(ScrollToBottom));
                    }
                };
            }

            // Unsubscribe first to avoid duplicate subscriptions to the same collection causing multiple event firings
            collection.CollectionChanged -= _collectionChangedHandler;
            collection.CollectionChanged += _collectionChangedHandler;
        }

        private void UpdateEmptyState()
        {
            if (Messages == null)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                return;
            }
            
            bool isEmpty = true;
            foreach (var item in Messages)
            {
                isEmpty = false;
                break;
            }

            if (isEmpty)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Collapsed;
            }
        }

        private void MainScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // If the change is from manual user scrolling (mouse wheel, dragging the scrollbar) and scrolling up, stop the auto-scroll animation
            if (e.VerticalChange < 0 && Math.Abs(e.VerticalChange) > 1 && _scrollEaseTimer.IsEnabled)
            {
                _scrollEaseTimer.Stop();
            }

            bool isNearBottom = e.VerticalOffset >= (e.ExtentHeight - e.ViewportHeight - 50);

            if (isNearBottom)
            {
                _isAutoScrollEnabled = true;
                ScrollToBottomButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                // If the user scrolls up, disable auto-scroll
                if (e.VerticalChange < 0)
                {
                    _isAutoScrollEnabled = false;
                }

                bool hasMessages = false;
                if (Messages != null)
                {
                    foreach (var m in Messages)
                    {
                        hasMessages = true;
                        break;
                    }
                }

                if (!_isAutoScrollEnabled && hasMessages)
                {
                    ScrollToBottomButton.Visibility = Visibility.Visible;
                }
            }

            _lastScrollOffset = e.VerticalOffset;
        }

        public void ScrollToBottom()
        {
            if (!_isAutoScrollEnabled) return; // If auto-scroll is already canceled, do not trigger again

            // Use a throttling mechanism to avoid frequent scrolling
            _pendingScroll = true;
            _targetScrollOffset = MainScrollViewer.ExtentHeight;
            
            if (!_scrollThrottleTimer.IsEnabled)
            {
                _scrollThrottleTimer.Start();
            }
        }
        
        private void ScrollThrottleTimer_Tick(object sender, EventArgs e)
        {
            _scrollThrottleTimer.Stop();

            if (_pendingScroll && MainScrollViewer.ExtentHeight > MainScrollViewer.ViewportHeight)
            {
                _pendingScroll = false;

                var currentOffset = MainScrollViewer.VerticalOffset;
                var targetOffset = _targetScrollOffset;

                if (Math.Abs(targetOffset - currentOffset) > 10)
                {
                    // Large distance: start frame-by-frame driving to simulate animation
                    if (!_scrollEaseTimer.IsEnabled) _scrollEaseTimer.Start();
                }
                else
                {
                    // Very close: jump in one step
                    MainScrollViewer.ScrollToVerticalOffset(targetOffset);
                }
            }
        }

        private void ScrollEaseTimer_Tick(object sender, EventArgs e)
        {
            // Push VerticalOffset 35% toward _targetScrollOffset each frame until the difference is less than 1px.
            // If a new ScrollToBottom updates _targetScrollOffset in the meantime, the easing follows the new target automatically.
            if (MainScrollViewer == null)
            {
                _scrollEaseTimer.Stop();
                return;
            }

            var current = MainScrollViewer.VerticalOffset;
            var target = _targetScrollOffset > 0 ? _targetScrollOffset : MainScrollViewer.ExtentHeight;
            
            // If the target exceeds the actual maximum scrollable range, correct the target
            if (target > MainScrollViewer.ExtentHeight - MainScrollViewer.ViewportHeight)
            {
                target = MainScrollViewer.ExtentHeight - MainScrollViewer.ViewportHeight;
            }

            var delta = target - current;

            // Distance less than 1px, or negative (current position already passed the target, meaning the user scrolled down more manually): stop the animation
            if (Math.Abs(delta) < ScrollEaseEpsilon || delta <= 0)
            {
                if (delta > 0)
                {
                    MainScrollViewer.ScrollToVerticalOffset(target);
                }
                _scrollEaseTimer.Stop();
                return;
            }

            MainScrollViewer.ScrollToVerticalOffset(current + delta * ScrollEaseFactor);
        }

        private void ScrollToBottomButton_Click(object sender, RoutedEventArgs e)
        {
            _isAutoScrollEnabled = true;
            _targetScrollOffset = MainScrollViewer.ExtentHeight;
            
            // Jump directly to the bottom without smooth animation, to avoid blocking the user's subsequent actions during the animation
            if (_scrollEaseTimer.IsEnabled) _scrollEaseTimer.Stop();
            MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.ExtentHeight);
            
            ScrollToBottomButton.Visibility = Visibility.Collapsed;
            ScrollToBottomRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is AgentActionStep step)
            {
                ConfirmStepRequested?.Invoke(this, step);
            }
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is AgentActionStep step)
            {
                CancelStepRequested?.Invoke(this, step);
            }
        }

        public void SetAutoScroll(bool enabled)
        {
            _isAutoScrollEnabled = enabled;
            if (enabled)
            {
                ScrollToBottom();
            }
        }
        
        private void TraeMessageList_Unloaded(object sender, RoutedEventArgs e)
        {
            LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;

            // Clean up timer resources
            if (_scrollThrottleTimer != null)
            {
                _scrollThrottleTimer.Stop();
                _scrollThrottleTimer.Tick -= ScrollThrottleTimer_Tick;
                _scrollThrottleTimer = null;
            }
            if (_scrollEaseTimer != null)
            {
                _scrollEaseTimer.Stop();
                _scrollEaseTimer.Tick -= ScrollEaseTimer_Tick;
                _scrollEaseTimer = null;
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #region Dev Mode Trace

        private void DevTraceBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ChatMessage msg && msg.DevTrace != null)
            {
                DevTraceRequested?.Invoke(this, msg.DevTrace);
            }
        }

        #endregion
    }
}
