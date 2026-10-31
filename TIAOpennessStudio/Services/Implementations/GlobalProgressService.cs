using System;
using System.Threading;
using Prism.Events;
using TIAOpennessStudio.Events;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Singleton service that bridges background operations to the UI
    /// global progress indicator via Prism EventAggregator.
    /// Thread-safe: can be called from any thread.
    /// </summary>
    public sealed class GlobalProgressService : IGlobalProgressService
    {
        private readonly IEventAggregator _eventAggregator;
        private int _activeOperations;

        public GlobalProgressService(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        }

        public void Show(string message)
        {
            Interlocked.Increment(ref _activeOperations);
            Publish(GlobalProgressPayload.Show(message));
        }

        public void Show(string message, double percent)
        {
            Interlocked.CompareExchange(ref _activeOperations, 1, 0);
            Publish(GlobalProgressPayload.Show(message, percent));
        }

        public void Show(string message, double percent, string phase)
        {
            Interlocked.CompareExchange(ref _activeOperations, 1, 0);
            Publish(GlobalProgressPayload.Show(message, percent, phase));
        }

        public void Hide()
        {
            var remaining = Interlocked.Decrement(ref _activeOperations);
            if (remaining <= 0)
            {
                Interlocked.Exchange(ref _activeOperations, 0);
                Publish(GlobalProgressPayload.Hide());
            }
        }

        public Action<string> CreateProgressCallback(string phase = null)
        {
            bool started = false;
            return message =>
            {
                if (!started)
                {
                    started = true;
                    Interlocked.Increment(ref _activeOperations);
                }
                Publish(GlobalProgressPayload.Show(message, -1, phase));
            };
        }

        public Action<string> CreateProgressCallback(int expectedTotal, string phase = null)
        {
            bool started = false;
            int current = 0;
            return message =>
            {
                if (!started)
                {
                    started = true;
                    Interlocked.Increment(ref _activeOperations);
                }
                var c = Interlocked.Increment(ref current);
                var percent = expectedTotal > 0
                    ? Math.Min(100.0, (double)c / expectedTotal * 100.0)
                    : -1;
                Publish(GlobalProgressPayload.Show(message, percent, phase));
            };
        }

        private void Publish(GlobalProgressPayload payload)
        {
            _eventAggregator.GetEvent<GlobalProgressEvent>().Publish(payload);
        }
    }
}
