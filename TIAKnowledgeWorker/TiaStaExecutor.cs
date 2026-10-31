using System;
using System.Threading;
using System.Threading.Tasks;

namespace TIAKnowledgeWorker
{
    /// <summary>
    /// Runs TIA Openness operations on a dedicated STA thread.
    /// All TIA API calls must be dispatched through this executor.
    /// </summary>
    public sealed class TiaStaExecutor : IDisposable
    {
        private readonly Thread _staThread;
        private readonly TaskScheduler _staScheduler;
        private readonly TaskFactory _staFactory;
        private bool _disposed;
        private long _pendingCount;

        /// <summary>
        /// Number of operations currently queued or running on the STA thread.
        /// Used by the background change scanner to yield to real work.
        /// </summary>
        public long PendingTaskCount => Interlocked.Read(ref _pendingCount);

        public TiaStaExecutor()
        {
            TaskScheduler scheduler = null;
            using (var ready = new ManualResetEventSlim(false))
            {
                _staThread = new Thread(() =>
                {
                    // Create a dummy control to initialize STA message pump,
                    // then grab the SynchronizationContext's TaskScheduler.
                    var ctx = new System.Windows.Forms.WindowsFormsSynchronizationContext();
                    System.Windows.Forms.Application.Idle += (s, e) =>
                    {
                        // Keep the message pump alive without a visible form.
                    };
                    SynchronizationContext.SetSynchronizationContext(ctx);
                    scheduler = TaskScheduler.FromCurrentSynchronizationContext();
                    ready.Set();
                    System.Windows.Forms.Application.Run();
                })
                {
                    Name = "TIAKnowledgeWorker-STA",
                    IsBackground = true,
                };
                _staThread.SetApartmentState(ApartmentState.STA);
                _staThread.Start();
                ready.Wait(TimeSpan.FromSeconds(10));
            }

            _staScheduler = scheduler ?? throw new InvalidOperationException("Failed to initialize STA scheduler.");
            _staFactory = new TaskFactory(_staScheduler);
        }

        /// <summary>
        /// Runs an action on the STA thread and returns the result.
        /// </summary>
        public Task<T> RunAsync<T>(Func<T> func)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TiaStaExecutor));
            Interlocked.Increment(ref _pendingCount);
            return _staFactory.StartNew(() =>
            {
                try { return func(); }
                finally { Interlocked.Decrement(ref _pendingCount); }
            });
        }

        /// <summary>
        /// Runs an action on the STA thread.
        /// </summary>
        public Task RunAsync(Action action)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TiaStaExecutor));
            Interlocked.Increment(ref _pendingCount);
            return _staFactory.StartNew(() =>
            {
                try { action(); }
                finally { Interlocked.Decrement(ref _pendingCount); }
            });
        }

        /// <summary>
        /// Runs an async operation on the STA thread.
        /// </summary>
        public async Task<T> RunAsync<T>(Func<Task<T>> func)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TiaStaExecutor));
            Interlocked.Increment(ref _pendingCount);
            try
            {
                return await _staFactory.StartNew(func).Unwrap().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingCount);
            }
        }

        /// <summary>
        /// Runs an async operation on the STA thread.
        /// </summary>
        public async Task RunAsync(Func<Task> func)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TiaStaExecutor));
            Interlocked.Increment(ref _pendingCount);
            try
            {
                await _staFactory.StartNew(func).Unwrap().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingCount);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _staFactory.StartNew(() => System.Windows.Forms.Application.ExitThread()); }
            catch { }
        }
    }
}
