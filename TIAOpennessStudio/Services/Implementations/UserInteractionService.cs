using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Queue, timeout, run scoping, batch merging and "remember for this run" for agent-to-user interactions.
    ///
    /// Threading: callers are HttpListener / thread-pool threads and may block (await) for minutes.
    /// The presenter is responsible for marshalling to the UI thread. This class never touches WPF.
    ///
    /// Rules that matter:
    ///   * One card is shown at a time (FIFO); others wait and show "n/m".
    ///   * Approvals of the same run that are still queued are merged into one batch card, so a burst of
    ///     writes asks once instead of flashing several cards. High-risk requests never merge.
    ///   * Two-stage timeout: TimeoutSeconds marks the card "expired but still approvable";
    ///     HardTimeoutSeconds finally resolves it as TimedOut (fail closed).
    ///   * Disposing a run cancels everything it still has pending and forgets its remembered approvals.
    ///   * High risk requests never offer or honour "remember".
    /// </summary>
    public sealed class UserInteractionService : IUserInteractionService
    {
        /// <summary>Default soft deadline: the card is marked expired after this, but can still be approved.</summary>
        public const int DefaultTimeoutSeconds = 180;

        /// <summary>Default hard deadline: after this the request resolves as TimedOut and the write is refused.</summary>
        public const int DefaultHardTimeoutSeconds = 600;

        /// <summary>Hard ceiling so a bad caller cannot park a thread (or an HTTP connection) forever.</summary>
        public const int MaxTimeoutSeconds = 600;

        /// <summary>Upper bound on one merged card, so the drawer stays readable.</summary>
        private const int MaxBatchItems = 8;

        private readonly IInteractionPresenter _presenter;
        private readonly int _defaultTimeoutSeconds;
        private readonly int _defaultHardTimeoutSeconds;
        private readonly object _gate = new object();

        // Active runs, newest last. Interactions are attributed to the newest run.
        private readonly List<RunScope> _runs = new List<RunScope>();

        // FIFO of pending cards waiting for the presenter, plus the one being shown.
        private readonly LinkedList<Pending> _queue = new LinkedList<Pending>();
        private Pending _current;
        private bool _pumping;

        public event Action<InteractionAuditRecord> Audited;

        public UserInteractionService(
            IInteractionPresenter presenter, int defaultTimeoutSeconds = 0, int hardTimeoutSeconds = 0)
        {
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            _defaultTimeoutSeconds = Clamp(
                defaultTimeoutSeconds > 0 ? defaultTimeoutSeconds : DefaultTimeoutSeconds, 1, MaxTimeoutSeconds);
            _defaultHardTimeoutSeconds = Clamp(
                hardTimeoutSeconds > 0 ? hardTimeoutSeconds : DefaultHardTimeoutSeconds,
                _defaultTimeoutSeconds, MaxTimeoutSeconds);
        }

        public string ActiveRunId
        {
            get
            {
                lock (_gate)
                {
                    return _runs.Count == 0 ? null : _runs[_runs.Count - 1].RunId;
                }
            }
        }

        public bool HasPending
        {
            get
            {
                lock (_gate)
                {
                    return _current != null || _queue.Count > 0;
                }
            }
        }

        public IDisposable BeginRun(string runId, CancellationToken runToken)
        {
            if (string.IsNullOrWhiteSpace(runId)) runId = Guid.NewGuid().ToString("N");
            var scope = new RunScope(this, runId, runToken);
            lock (_gate) { _runs.Add(scope); }
            // If the run is cancelled (stop button) we must release every waiter at once.
            scope.Registration = runToken.Register(() => scope.Cancel("run cancelled"));
            return scope;
        }

        public async Task<InteractionResult> RequestAsync(
            InteractionRequest request, CancellationToken ct = default, bool allowWithoutRun = false)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Id)) request.Id = Guid.NewGuid().ToString("N");

            var sw = Stopwatch.StartNew();
            RunScope run;
            lock (_gate) { run = _runs.Count == 0 ? null : _runs[_runs.Count - 1]; }

            // No run means we cannot scope "remember" and cannot tie a cancel to anything.
            if (run == null && !allowWithoutRun)
            {
                return Finish(request, null, sw, new InteractionResult
                {
                    Status = InteractionStatus.Unavailable,
                    Reason = "No active agent run to attach the request to."
                });
            }

            // "Remember for this run": an earlier approval of the same capability covers this one.
            if (run != null && request.CanRemember && run.IsRemembered(request.RememberKey))
            {
                return Finish(request, run, sw, new InteractionResult
                {
                    Status = InteractionStatus.AutoApproved,
                    Reason = "Approved earlier in this run."
                });
            }

            request.TimeoutSeconds = Clamp(
                request.TimeoutSeconds > 0 ? request.TimeoutSeconds : _defaultTimeoutSeconds, 1, MaxTimeoutSeconds);
            request.HardTimeoutSeconds = Clamp(
                request.HardTimeoutSeconds > 0 ? request.HardTimeoutSeconds : _defaultHardTimeoutSeconds,
                request.TimeoutSeconds, MaxTimeoutSeconds);

            Pending pending;
            bool merged;
            bool refreshCurrent = false;
            lock (_gate)
            {
                pending = FindMergeTarget(request, run);
                merged = pending != null;
                if (merged)
                {
                    pending.Items.Add(request);
                    RebuildCard(pending);
                    // The card is already on screen: re-render it so the user sees the extra write.
                    refreshCurrent = ReferenceEquals(pending, _current);
                }
                else
                {
                    pending = new Pending { Run = run };
                    pending.Items.Add(request);
                    pending.Card = request;
                    pending.Node = _queue.AddLast(pending);
                }
            }

            if (!merged)
            {
                // Bound the wait: the hard deadline resolves the whole card as TimedOut.
                pending.Cts = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, run != null ? run.Token : CancellationToken.None);
                pending.TimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(request.HardTimeoutSeconds));
                pending.TimeoutReg = pending.TimeoutCts.Token.Register(() => pending.Complete(new InteractionResult
                {
                    Status = InteractionStatus.TimedOut,
                    Reason = "No answer within " + request.HardTimeoutSeconds + " s."
                }));
                pending.CancelReg = pending.Cts.Token.Register(() => pending.Complete(new InteractionResult
                {
                    Status = InteractionStatus.Cancelled,
                    Reason = "The run was cancelled while waiting for the user."
                }));
                if (run != null) run.Track(pending);
                PumpQueue();
            }
            else if (refreshCurrent)
            {
                try { _presenter.Update(pending.Card); } catch { /* presenter is best effort here */ }
            }

            InteractionResult batchResult;
            try
            {
                batchResult = await pending.Task.ConfigureAwait(false);
            }
            finally
            {
                CleanupPending(pending);
            }

            var own = Materialize(request, batchResult);

            // Only now do we know the user ticked the box, and only an actual approval may be remembered.
            if (run != null && own.Status == InteractionStatus.Answered
                && own.Remember && request.CanRemember)
            {
                run.Remember(request.RememberKey);
            }

            return Finish(request, run, sw, own);
        }

        /// <summary>
        /// A card (already queued or currently on screen) of the same run can absorb another approval,
        /// so a burst of writes shows one card. High-risk, non-approval and already-resolved cards never merge.
        /// </summary>
        private Pending FindMergeTarget(InteractionRequest incoming, RunScope run)
        {
            if (incoming.Kind != InteractionKind.Approve) return null;
            if (incoming.Risk == InteractionRisk.High) return null;

            if (IsMergeable(incoming, _current, run)) return _current;

            foreach (var candidate in _queue)
            {
                if (IsMergeable(incoming, candidate, run)) return candidate;
            }
            return null;
        }

        private static bool IsMergeable(InteractionRequest incoming, Pending card, RunScope run)
        {
            if (card == null) return false;
            if (card.Task.IsCompleted) return false;          // the user already answered: never absorb silently
            if (card.Items.Count == 0 || card.Items.Count >= MaxBatchItems) return false;
            if (!ReferenceEquals(card.Run, run)) return false;
            var first = card.Items[0];
            return first.Kind == InteractionKind.Approve && first.Risk != InteractionRisk.High;
        }

        /// <summary>Rebuild the shown card after a merge: one title, one list, one allow/deny for the batch.</summary>
        private void RebuildCard(Pending p)
        {
            if (p.Items.Count <= 1)
            {
                p.Card = p.Items[0];
                return;
            }

            var lm = LanguageManager.Instance;
            var first = p.Items[0];
            bool sameRememberKey = p.Items.All(i =>
                string.Equals(i.RememberKey, first.RememberKey, StringComparison.OrdinalIgnoreCase));
            bool anyHigh = p.Items.Any(i => i.Risk == InteractionRisk.High);

            p.Card = new InteractionRequest
            {
                Id = first.Id,
                Kind = InteractionKind.Approve,
                Title = string.Format(lm.GetString("Interact.BatchTitle"), p.Items.Count),
                Summary = lm.GetString("Interact.BatchSummary"),
                Risk = anyHigh ? InteractionRisk.High : p.Items.Max(i => i.Risk),
                Source = first.Source,
                Rid = first.Rid,
                TimeoutSeconds = p.Items.Min(i => i.TimeoutSeconds),
                HardTimeoutSeconds = p.Items.Min(i => i.HardTimeoutSeconds),
                RememberKey = sameRememberKey ? first.RememberKey : null,
                AllowLabel = lm.GetString("Interact.AllowAll"),
                DenyLabel = lm.GetString("Interact.DenyAll"),
                Batch = p.Items.Select(i => new InteractionBatchItem
                {
                    RequestId = i.Id,
                    Title = i.Title,
                    Summary = i.Summary,
                    Risk = i.Risk
                }).ToList()
            };
        }

        /// <summary>Per-caller view of the batch outcome (remember only applies to requests that allow it).</summary>
        private static InteractionResult Materialize(InteractionRequest request, InteractionResult batch)
        {
            if (batch == null)
                return new InteractionResult { Status = InteractionStatus.Unavailable, Reason = "No decision." };
            if (batch.Status == InteractionStatus.Answered || batch.Status == InteractionStatus.AutoApproved)
                return new InteractionResult
                {
                    Status = batch.Status,
                    Remember = batch.Remember && request.CanRemember
                };
            return new InteractionResult { Status = batch.Status, Reason = batch.Reason };
        }

        private void CleanupPending(Pending p)
        {
            if (Interlocked.Exchange(ref p.Cleaned, 1) == 1) return;
            try { p.TimeoutReg.Dispose(); } catch { }
            try { p.CancelReg.Dispose(); } catch { }
            try { p.Cts?.Dispose(); } catch { }
            try { p.TimeoutCts?.Dispose(); } catch { }
            if (p.Run != null) p.Run.Untrack(p);
            RemoveFromQueue(p);
            try { if (p.Card != null) _presenter.Dismiss(p.Card.Id); } catch { /* presenter is best effort here */ }
            PumpQueue();
        }

        // ── queue pump ────────────────────────────────────────────────────

        private void PumpQueue()
        {
            Pending next;
            int position, length;
            lock (_gate)
            {
                if (_pumping || _current != null) return;
                // Skip anything that already finished while waiting its turn.
                while (_queue.First != null && _queue.First.Value.Task.IsCompleted)
                    _queue.RemoveFirst();
                if (_queue.First == null) return;

                next = _queue.First.Value;
                _queue.RemoveFirst();
                next.Node = null;
                _current = next;
                _pumping = true;
                position = 1;
                length = 1 + _queue.Count;
            }

            // Fire and forget: the presenter task completes `next` through its own result.
            _ = ShowAsync(next, position, length);
        }

        private async Task ShowAsync(Pending p, int position, int length)
        {
            try
            {
                var presented = _presenter.PresentAsync(p.Card, position, length, p.Cts.Token);
                var done = await Task.WhenAny(presented, p.Task).ConfigureAwait(false);
                if (done == presented)
                {
                    InteractionResult r;
                    try { r = await presented.ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        r = new InteractionResult { Status = InteractionStatus.Cancelled, Reason = "Cancelled." };
                    }
                    p.Complete(r ?? new InteractionResult { Status = InteractionStatus.Denied, Reason = "No decision." });
                }
            }
            catch (Exception ex)
            {
                // A broken presenter must not leave the agent hanging: refuse.
                AppLogger.Log("[Interaction] presenter failed: " + ex.Message);
                p.Complete(new InteractionResult
                {
                    Status = InteractionStatus.Unavailable,
                    Reason = "The confirmation UI failed: " + ex.Message
                });
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_current, p)) _current = null;
                    _pumping = false;
                }
                PumpQueue();
            }
        }

        private void RemoveFromQueue(Pending p)
        {
            lock (_gate)
            {
                if (p.Node != null && p.Node.List != null) _queue.Remove(p.Node);
                p.Node = null;
            }
        }

        // ── audit ─────────────────────────────────────────────────────────

        private InteractionResult Finish(InteractionRequest req, RunScope run, Stopwatch sw, InteractionResult result)
        {
            try
            {
                var rec = new InteractionAuditRecord
                {
                    TimestampUtc = DateTime.UtcNow,
                    RequestId = req.Id,
                    RunId = run != null ? run.RunId : null,
                    Rid = req.Rid,
                    Kind = req.Kind,
                    Source = req.Source,
                    Title = req.Title,
                    Risk = req.Risk,
                    Status = result.Status,
                    // Do not put free text the user typed into logs for Input requests.
                    Value = req.Kind == InteractionKind.Input ? (result.Value == null ? null : "(" + result.Value.Length + " chars)") : result.Value,
                    Remembered = result.Remember && req.CanRemember,
                    WaitedMs = sw.ElapsedMilliseconds
                };
                AppLogger.Log(string.Format(
                    "[Interaction] {0} kind={1} source={2} risk={3} run={4} rid={5} waited={6}ms title=\"{7}\"",
                    rec.Status, rec.Kind, rec.Source, rec.Risk, rec.RunId ?? "-", rec.Rid ?? "-", rec.WaitedMs, rec.Title));
                Audited?.Invoke(rec);
            }
            catch { /* audit must never break the agent */ }
            return result;
        }

        // ── helper types ──────────────────────────────────────────────────

        private static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        /// <summary>One card. It can carry several queued approval requests (a merged batch).</summary>
        private sealed class Pending
        {
            private readonly TaskCompletionSource<InteractionResult> _batch =
                new TaskCompletionSource<InteractionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            public List<InteractionRequest> Items { get; } = new List<InteractionRequest>();
            public InteractionRequest Card { get; set; }
            public RunScope Run { get; set; }
            public LinkedListNode<Pending> Node { get; set; }
            public CancellationTokenSource Cts { get; set; }
            public CancellationTokenSource TimeoutCts { get; set; }
            public CancellationTokenRegistration TimeoutReg { get; set; }
            public CancellationTokenRegistration CancelReg { get; set; }
            public int Cleaned;
            public Task<InteractionResult> Task => _batch.Task;

            /// <summary>First completion wins; later ones are ignored.</summary>
            public void Complete(InteractionResult r) { _batch.TrySetResult(r); }
        }

        private sealed class RunScope : IDisposable
        {
            private readonly UserInteractionService _owner;
            private readonly CancellationTokenSource _cts;
            private readonly HashSet<string> _remembered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly List<Pending> _pending = new List<Pending>();
            private readonly object _lock = new object();
            private int _disposed;

            public RunScope(UserInteractionService owner, string runId, CancellationToken parent)
            {
                _owner = owner;
                RunId = runId;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            }

            public string RunId { get; }
            public CancellationToken Token => _cts.Token;
            public CancellationTokenRegistration Registration { get; set; }

            public bool IsRemembered(string key) { lock (_lock) { return _remembered.Contains(key); } }
            public void Remember(string key) { lock (_lock) { _remembered.Add(key); } }
            public void Track(Pending p) { lock (_lock) { _pending.Add(p); } }
            public void Untrack(Pending p) { lock (_lock) { _pending.Remove(p); } }

            public void Cancel(string reason)
            {
                Pending[] snapshot;
                lock (_lock) { snapshot = _pending.ToArray(); }
                foreach (var p in snapshot)
                {
                    p.Complete(new InteractionResult { Status = InteractionStatus.Cancelled, Reason = reason });
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                lock (_owner._gate) { _owner._runs.Remove(this); }
                Cancel("run ended");
                lock (_lock) { _remembered.Clear(); }
                try { Registration.Dispose(); } catch { }
                try { _cts.Cancel(); } catch { }
                try { _cts.Dispose(); } catch { }
            }
        }
    }
}
