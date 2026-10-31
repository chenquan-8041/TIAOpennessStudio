using System;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Late-bound presenter. The DI container builds <see cref="UserInteractionService"/> before any window
    /// exists, so the service gets this host, and the main window attaches the real drawer once it is created.
    ///
    /// Until a drawer is attached every request resolves as Unavailable. That is deliberate: with no UI there
    /// is nobody to ask, so the answer is "no" (fail closed), never "yes".
    /// </summary>
    public sealed class InteractionPresenterHost : IInteractionPresenter
    {
        private volatile IInteractionPresenter _inner;

        public bool IsAttached => _inner != null;

        public void Attach(IInteractionPresenter presenter)
        {
            _inner = presenter ?? throw new ArgumentNullException(nameof(presenter));
        }

        public Task<InteractionResult> PresentAsync(InteractionRequest request, int queuePosition, int queueLength, CancellationToken ct)
        {
            var inner = _inner;
            if (inner == null)
            {
                return Task.FromResult(new InteractionResult
                {
                    Status = InteractionStatus.Unavailable,
                    Reason = "The confirmation UI is not available."
                });
            }
            return inner.PresentAsync(request, queuePosition, queueLength, ct);
        }

        public void Dismiss(string requestId)
        {
            _inner?.Dismiss(requestId);
        }

        public void Update(InteractionRequest request)
        {
            _inner?.Update(request);
        }
    }
}
