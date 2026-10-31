using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Hosts a dedicated STA thread (with its own WPF Dispatcher message pump) that
    /// exclusively owns all TIA Openness COM objects.
    ///
    /// Background: TIA Openness is apartment-affine — its objects (TiaPortal / Project /
    /// PlcSoftware / DeviceItem / PlcBlock ...) must be created AND used on the same STA
    /// thread, otherwise Openness throws "Cross-thread operation is not valid in Openness
    /// within STA". Today that STA thread is the WPF UI thread, so long Openness operations
    /// (compile, import/export) freeze the UI.
    ///
    /// Marshaling every Openness access through this host moves all of that work off the
    /// UI thread onto a dedicated background STA thread, keeping the UI responsive. Callers
    /// only ever exchange plain data (strings / DTOs) with this thread, never raw RCWs.
    /// </summary>
    public sealed class TiaStaHost : IDisposable
    {
        public static TiaStaHost Instance { get; } = new TiaStaHost();

        private readonly Thread _staThread;
        private readonly Dispatcher _dispatcher;
        private long _pendingCount;

        public Dispatcher Dispatcher => _dispatcher;

        /// <summary>
        /// Number of operations currently queued or running on the STA thread.
        /// Used by background maintenance (e.g. the worker's change scanner) to yield
        /// to real work so it never queues long enumeration slices ahead of user/agent
        /// requests on the single COM-owning thread.
        /// </summary>
        public long PendingTaskCount => Interlocked.Read(ref _pendingCount);

        private TiaStaHost()
        {
            Dispatcher dispatcher = null;
            using (var ready = new ManualResetEventSlim(false))
            {
                _staThread = new Thread(() =>
                {
                    dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    Name = "TiaOpenness-STA",
                    IsBackground = true,
                };
                _staThread.SetApartmentState(ApartmentState.STA);
                _staThread.Start();
                ready.Wait(TimeSpan.FromSeconds(10));
            }

            _dispatcher = dispatcher
                ?? throw new InvalidOperationException("Failed to initialize the TIA Openness STA thread.");
        }

        /// <summary>True when the calling thread already is the STA host thread.</summary>
        public bool CheckAccess() => _dispatcher.CheckAccess();

        /// <summary>Runs a synchronous function on the STA thread.</summary>
        public Task<T> RunAsync<T>(Func<T> func)
        {
            Interlocked.Increment(ref _pendingCount);
            return _dispatcher.InvokeAsync(() =>
            {
                try { return func(); }
                finally { Interlocked.Decrement(ref _pendingCount); }
            }, DispatcherPriority.Normal, CancellationToken.None).Task;
        }

        /// <summary>Runs a synchronous action on the STA thread.</summary>
        public Task RunAsync(Action action)
        {
            Interlocked.Increment(ref _pendingCount);
            return _dispatcher.InvokeAsync(() =>
            {
                try { action(); }
                finally { Interlocked.Decrement(ref _pendingCount); }
            }, DispatcherPriority.Normal, CancellationToken.None).Task;
        }

        /// <summary>Runs an async function on the STA thread.</summary>
        public Task<T> RunAsync<T>(Func<Task<T>> func)
        {
            Interlocked.Increment(ref _pendingCount);
            return _dispatcher.InvokeAsync(func, DispatcherPriority.Normal, CancellationToken.None)
                .Task.Unwrap()
                .ContinueWith(t =>
                {
                    Interlocked.Decrement(ref _pendingCount);
                    return t.GetAwaiter().GetResult();
                }, TaskScheduler.Default);
        }

        /// <summary>Runs an async action on the STA thread.</summary>
        public Task RunAsync(Func<Task> func)
        {
            Interlocked.Increment(ref _pendingCount);
            return _dispatcher.InvokeAsync(func, DispatcherPriority.Normal, CancellationToken.None)
                .Task.Unwrap()
                .ContinueWith(t =>
                {
                    Interlocked.Decrement(ref _pendingCount);
                    t.GetAwaiter().GetResult();
                }, TaskScheduler.Default);
        }

        public void Dispose()
        {
            try { _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal); } catch { }
        }
    }
}