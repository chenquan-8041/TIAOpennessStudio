using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Implementations;

namespace TIAOpennessStudio.Controls
{
    /// <summary>
    /// The drawer that slides up above the chat input box when an agent needs the user
    /// (approve / choose / input). Implements <see cref="IInteractionPresenter"/>, so
    /// <see cref="UserInteractionService"/> drives it and owns queueing and timeouts.
    ///
    /// Design choices:
    ///  * Safe defaults: keyboard focus starts on "Deny", Enter never approves, Esc denies.
    ///  * Everything shown is plain text. Approval text is built by trusted C# code upstream.
    ///  * The drawer only ever shows one request; the service serialises the rest.
    ///  * It never decides policy (remember/risk) beyond what the request already says.
    /// </summary>
    public partial class InteractionDrawer : UserControl, IInteractionPresenter
    {
        private sealed class State
        {
            public InteractionRequest Request;
            public TaskCompletionSource<InteractionResult> Tcs;
            /// <summary>After this the card is marked expired, but it stays on screen and stays approvable.</summary>
            public DateTime SoftDeadlineUtc;
            /// <summary>The service resolves the request as TimedOut at this point; the drawer must not outlive it.</summary>
            public DateTime HardDeadlineUtc;
        }

        private readonly DispatcherTimer _timer;
        private State _state;

        public InteractionDrawer()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;
            InputBox.TextChanged += (s, e) => UpdateAllowEnabled();
            Unloaded += (s, e) =>
            {
                _timer.Stop();
                var st = _state;
                if (st != null) Complete(st, Result(InteractionStatus.Cancelled, "The window was closed."));
            };
        }

        /// <summary>True while a request is on screen.</summary>
        public bool IsShowing => _state != null;

        // ── IInteractionPresenter ─────────────────────────────────────────

        public Task<InteractionResult> PresentAsync(InteractionRequest request, int queuePosition, int queueLength, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            int soft = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : UserInteractionService.DefaultTimeoutSeconds;
            if (soft > UserInteractionService.MaxTimeoutSeconds) soft = UserInteractionService.MaxTimeoutSeconds;
            int hard = request.HardTimeoutSeconds > 0 ? request.HardTimeoutSeconds : UserInteractionService.DefaultHardTimeoutSeconds;
            if (hard < soft) hard = soft;
            if (hard > UserInteractionService.MaxTimeoutSeconds) hard = UserInteractionService.MaxTimeoutSeconds;

            var state = new State
            {
                Request = request,
                Tcs = new TaskCompletionSource<InteractionResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                SoftDeadlineUtc = DateTime.UtcNow.AddSeconds(soft),
                HardDeadlineUtc = DateTime.UtcNow.AddSeconds(hard)
            };

            CancellationTokenRegistration reg = default;
            if (ct.CanBeCanceled)
            {
                reg = ct.Register(() => OnUi(() => Complete(state, Result(InteractionStatus.Cancelled, "Cancelled."))));
            }
            state.Tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);

            OnUi(() => Show(state, queuePosition, queueLength));
            return state.Tcs.Task;
        }

        public void Dismiss(string requestId)
        {
            OnUi(() =>
            {
                var st = _state;
                if (st != null && string.Equals(st.Request.Id, requestId, StringComparison.Ordinal))
                    Complete(st, Result(InteractionStatus.Cancelled, "Dismissed."));
            });
        }

        /// <summary>
        /// The service merged another queued approval into the card on screen: re-render it in place,
        /// keeping the deadline and the current focus. Never steals focus from the user.
        /// </summary>
        public void Update(InteractionRequest request)
        {
            if (request == null) return;
            OnUi(() =>
            {
                var st = _state;
                if (st == null || !string.Equals(st.Request.Id, request.Id, StringComparison.Ordinal)) return;

                st.Request = request;
                var lm = LanguageManager.Instance;
                TitleText.Text = !string.IsNullOrWhiteSpace(request.Title)
                    ? request.Title
                    : lm.GetString(request.Kind == InteractionKind.Approve ? "Interact.NeedsApproval" : "Interact.AgentAsks");
                SummaryText.Text = request.Summary ?? "";
                SummaryText.Visibility = string.IsNullOrWhiteSpace(request.Summary) ? Visibility.Collapsed : Visibility.Visible;

                var batch = request.Batch;
                bool hasBatch = batch != null && batch.Count > 1;
                BatchList.ItemsSource = hasBatch ? batch : null;
                BatchList.Visibility = hasBatch ? Visibility.Visible : Visibility.Collapsed;

                if (!string.IsNullOrWhiteSpace(request.AllowLabel)) AllowButton.Content = request.AllowLabel;
                if (!string.IsNullOrWhiteSpace(request.DenyLabel)) DenyButton.Content = request.DenyLabel;

                if (request.CanRemember && RememberCheck.Visibility != Visibility.Visible)
                {
                    RememberCheck.Content = lm.GetString("Interact.Remember");
                    RememberCheck.Visibility = Visibility.Visible;
                }
            });
        }

        // ── showing / hiding ──────────────────────────────────────────────

        private void Show(State state, int pos, int len)
        {
            // Already finished (cancelled or timed out while waiting its turn): never flash it on screen.
            if (state.Tcs.Task.IsCompleted) return;

            // The service serialises requests, but never leave an orphan if that ever breaks.
            var previous = _state;
            if (previous != null && !ReferenceEquals(previous, state))
                Complete(previous, Result(InteractionStatus.Cancelled, "Superseded by a newer request."));

            _state = state;
            var req = state.Request;
            var lm = LanguageManager.Instance;

            // Header
            string title = !string.IsNullOrWhiteSpace(req.Title)
                ? req.Title
                : lm.GetString(req.Kind == InteractionKind.Approve ? "Interact.NeedsApproval" : "Interact.AgentAsks");
            TitleText.Text = title;
            SummaryText.Text = req.Summary ?? "";
            SummaryText.Visibility = string.IsNullOrWhiteSpace(req.Summary) ? Visibility.Collapsed : Visibility.Visible;

            // Source line. High risk is called out in words, not colour alone.
            var src = new List<string>();
            if (req.Risk == InteractionRisk.High) src.Add("⚠ " + lm.GetString("Interact.HighRisk"));
            if (!string.IsNullOrWhiteSpace(req.Source)) src.Add(string.Format(lm.GetString("Interact.From"), req.Source));
            SourceText.Text = string.Join("  ·  ", src);
            SourceText.Visibility = src.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            // Risk stripe
            string brushKey = req.Risk == InteractionRisk.High ? "ThoughtErrorBrush"
                            : req.Risk == InteractionRisk.Medium ? "ThoughtWarningBrush"
                            : "AccentBrush";
            RiskStripe.Background = (TryFindResource(brushKey) as Brush) ?? (TryFindResource("AccentBrush") as Brush) ?? Brushes.SteelBlue;

            // Queue indicator
            if (len > 1)
            {
                QueueText.Text = string.Format(lm.GetString("Interact.Queue"), pos, len);
                QueueText.Visibility = Visibility.Visible;
            }
            else QueueText.Visibility = Visibility.Collapsed;

            // Detail (collapsed until asked for)
            bool hasDetail = !string.IsNullOrWhiteSpace(req.Detail);
            DetailBox.Text = hasDetail ? req.Detail : "";
            DetailBox.Visibility = Visibility.Collapsed;
            DetailToggle.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
            DetailToggle.Content = lm.GetString("Interact.ShowDetail");

            // Merged batch: one row per queued write; the summary above is the aggregate.
            var batch = req.Batch;
            bool hasBatch = batch != null && batch.Count > 1;
            BatchList.ItemsSource = hasBatch ? batch : null;
            BatchList.Visibility = hasBatch ? Visibility.Visible : Visibility.Collapsed;

            // Countdown returns to its normal colour for every new request.
            CountdownText.Foreground = (TryFindResource("InputHintBrush") as Brush) ?? CountdownText.Foreground;

            // Body by kind
            OptionsList.Visibility = Visibility.Collapsed;
            InputBox.Visibility = Visibility.Collapsed;
            RememberCheck.Visibility = Visibility.Collapsed;
            RememberCheck.IsChecked = false;      // never pre-ticked
            AllowButton.Visibility = Visibility.Visible;
            DenyButton.Content = !string.IsNullOrWhiteSpace(req.DenyLabel) ? req.DenyLabel : lm.GetString("Interact.Deny");

            switch (req.Kind)
            {
                case InteractionKind.Choose:
                    OptionsList.ItemsSource = req.Options;
                    OptionsList.Visibility = Visibility.Visible;
                    AllowButton.Visibility = Visibility.Collapsed;   // picking an option is the answer
                    break;

                case InteractionKind.Input:
                    InputBox.Text = req.DefaultInput ?? "";
                    InputBox.ToolTip = string.IsNullOrWhiteSpace(req.InputHint) ? null : req.InputHint;
                    InputBox.Visibility = Visibility.Visible;
                    AllowButton.Content = !string.IsNullOrWhiteSpace(req.AllowLabel) ? req.AllowLabel : lm.GetString("Interact.Submit");
                    break;

                default: // Approve
                    AllowButton.Content = !string.IsNullOrWhiteSpace(req.AllowLabel) ? req.AllowLabel : lm.GetString("Interact.Allow");
                    if (req.CanRemember)
                    {
                        RememberCheck.Content = lm.GetString("Interact.Remember");
                        RememberCheck.Visibility = Visibility.Visible;
                    }
                    break;
            }
            UpdateAllowEnabled();

            // Countdown
            UpdateCountdown();
            _timer.Start();

            // Slide up.
            DrawerRoot.Visibility = Visibility.Visible;
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            SlideTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            DrawerRoot.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

            // The user may be working in TIA Portal: tell them something is waiting.
            FlashIfInactive();

            // Focus: Deny by default, the text box for input. Enter never approves.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_state != state) return;
                if (req.Kind == InteractionKind.Input) { InputBox.Focus(); InputBox.SelectAll(); }
                else DenyButton.Focus();
            }), DispatcherPriority.Input);
        }

        private void Hide()
        {
            _timer.Stop();
            DrawerRoot.BeginAnimation(OpacityProperty, null);
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            DrawerRoot.Visibility = Visibility.Collapsed;
            OptionsList.ItemsSource = null;
            BatchList.ItemsSource = null;
            DetailBox.Text = "";
            InputBox.Text = "";   // do not leave typed answers lying around
        }

        private void Complete(State state, InteractionResult result)
        {
            if (ReferenceEquals(_state, state))
            {
                _state = null;
                Hide();
            }
            state.Tcs.TrySetResult(result);
        }

        // ── user actions ──────────────────────────────────────────────────

        private void Allow_Click(object sender, RoutedEventArgs e)
        {
            var st = _state;
            if (st == null) return;

            if (st.Request.Kind == InteractionKind.Input)
            {
                var text = InputBox.Text ?? "";
                if (string.IsNullOrWhiteSpace(text)) return;
                Complete(st, new InteractionResult { Status = InteractionStatus.Answered, Value = text });
                return;
            }

            // Approve. Remember only counts if the request allowed it AND the box is ticked.
            bool remember = st.Request.CanRemember && RememberCheck.IsChecked == true;
            Complete(st, new InteractionResult { Status = InteractionStatus.Answered, Remember = remember });
        }

        private void Deny_Click(object sender, RoutedEventArgs e) => DenyCurrent();

        private void DenyCurrent()
        {
            var st = _state;
            if (st == null) return;
            Complete(st, Result(InteractionStatus.Denied, "The user declined this request."));
        }

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            var st = _state;
            if (st == null) return;
            var id = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(id)) return;
            Complete(st, new InteractionResult { Status = InteractionStatus.Answered, Value = id });
        }

        private void DetailToggle_Click(object sender, RoutedEventArgs e)
        {
            var lm = LanguageManager.Instance;
            bool show = DetailBox.Visibility != Visibility.Visible;
            DetailBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            DetailToggle.Content = lm.GetString(show ? "Interact.HideDetail" : "Interact.ShowDetail");
        }

        private void Drawer_KeyDown(object sender, KeyEventArgs e)
        {
            if (_state == null) return;

            if (e.Key == Key.Escape)
            {
                DenyCurrent();
                e.Handled = true;
                return;
            }

            // Free text: Ctrl+Enter submits. Plain Enter is a new line (the box accepts returns).
            if (_state.Request.Kind == InteractionKind.Input
                && e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                Allow_Click(sender, e);
                e.Handled = true;
            }
        }

        // ── small helpers ─────────────────────────────────────────────────

        private void UpdateAllowEnabled()
        {
            var st = _state;
            if (st == null) return;
            AllowButton.IsEnabled = st.Request.Kind != InteractionKind.Input
                                    || !string.IsNullOrWhiteSpace(InputBox.Text);
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            var st = _state;
            if (st == null) { _timer.Stop(); return; }
            UpdateCountdown();

            // The service owns the real (hard) timeout. If it somehow did not fire, do not sit here forever.
            if ((DateTime.UtcNow - st.HardDeadlineUtc).TotalSeconds > 3)
                Complete(st, Result(InteractionStatus.TimedOut, "No answer in time."));
        }

        private void UpdateCountdown()
        {
            var st = _state;
            if (st == null) return;
            var lm = LanguageManager.Instance;

            if (DateTime.UtcNow < st.SoftDeadlineUtc)
            {
                int left = (int)Math.Ceiling((st.SoftDeadlineUtc - DateTime.UtcNow).TotalSeconds);
                if (left < 0) left = 0;
                CountdownText.Text = string.Format(lm.GetString("Interact.Countdown"), left);
                return;
            }

            // Soft deadline passed: the card stays approvable until the hard deadline.
            int hardLeft = (int)Math.Ceiling((st.HardDeadlineUtc - DateTime.UtcNow).TotalSeconds);
            if (hardLeft < 0) hardLeft = 0;
            CountdownText.Text = string.Format(lm.GetString("Interact.Expired"), hardLeft);
            CountdownText.Foreground = (TryFindResource("ThoughtWarningBrush") as Brush) ?? CountdownText.Foreground;
        }

        private void OnUi(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        }

        private static InteractionResult Result(InteractionStatus status, string reason)
            => new InteractionResult { Status = status, Reason = reason };

        // ── taskbar flash ─────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        private const uint FLASHW_TRAY = 0x2;
        private const uint FLASHW_TIMERNOFG = 0xC;

        private void FlashIfInactive()
        {
            try
            {
                var window = Window.GetWindow(this);
                if (window == null || window.IsActive) return;
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                var info = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                    hwnd = handle,
                    dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,   // flash until the window comes to the foreground
                    uCount = uint.MaxValue,
                    dwTimeout = 0
                };
                FlashWindowEx(ref info);
            }
            catch { /* cosmetic only */ }
        }
    }
}
