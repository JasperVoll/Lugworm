using System;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper.Kernel;

namespace Lugworm.Ui
{
    /// <summary>Component-side background file-work coordinator (AUD-PERF-001).
    ///
    /// One instance per role per component (a table read, a report write, …).
    /// The component captures an immutable input snapshot on the solver thread,
    /// hands a pure worker function to <see cref="Start"/>, and keeps emitting its
    /// last good result while the worker runs. Exactly one completion callback is
    /// marshalled through <c>RhinoApp.InvokeOnUiThread</c>; it stores the outcome,
    /// rejects stale completions (a newer request supersedes), and expires the
    /// owning component once so the next solve publishes the fresh result.
    ///
    /// Workers must not touch IGH_DataAccess, Rhino geometry, unsynchronized
    /// component fields or a Grasshopper document - only Rhino-free Core models,
    /// strings and byte arrays cross the thread boundary.
    ///
    /// All coordinator state is read and written on the UI/solver thread only;
    /// the worker communicates exclusively through the marshalled completion.</summary>
    public sealed class BackgroundFileWork<T> where T : class
    {
        readonly string _role;
        GH_Component? _owner;
        CancellationTokenSource? _cts;
        string? _pendingKey;
        string? _resultKey;
        T? _result;
        string? _errorKey;
        string? _error;
        Task _tail = Task.CompletedTask;

        public BackgroundFileWork(string role) { _role = role; }

        /// <summary>True while a request is running; the component shows a
        /// "loading/writing" remark and keeps its last good outputs.</summary>
        public bool IsPending => _pendingKey != null;

        /// <summary>The completed result for exactly this request key, or null when
        /// the key has not completed (yet). A failed request never replaces the
        /// last good result of a previous key.</summary>
        public T? ResultFor(string key) => _resultKey == key ? _result : null;

        /// <summary>The failure message for exactly this request key, or null.</summary>
        public string? ErrorFor(string key) => _errorKey == key ? _error : null;

        /// <summary>Starts (or keeps) the background request for <paramref name="key"/>.
        /// Re-entrant per solve: the same key never starts twice, a new key cancels
        /// and supersedes the pending one. Call from the solver thread only.</summary>
        public void Start(GH_Component owner, string key, Func<CancellationToken, T> work)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (_pendingKey == key) return;                    // already on its way
            if (_resultKey == key || _errorKey == key) return; // already answered

            try { _cts?.Cancel(); }
            // best effort: superseding the previous request. A source the completing
            // worker already disposed has, by definition, nothing left to cancel.
            catch (ObjectDisposedException) { }
            var requestCts = new CancellationTokenSource();
            _cts = requestCts;
            _owner = owner;
            _pendingKey = key;
            var ct = requestCts.Token;
            // Serialize work owned by this coordinator. File APIs on net48 are
            // synchronous and cannot be interrupted mid-write; allowing replacement
            // writes to overlap could let the cancelled, older content finish last.
            // The replacement waits, checks its token, then becomes the final writer.
            Task predecessor = _tail;
            _tail = Task.Run(async () =>
            {
                bool completionQueued = false;
                T? outcome = null;
                string? error = null;
                try
                {
                    try { await predecessor.ConfigureAwait(false); }
                    // best effort: this await only orders the queue. Every worker captures
                    // its own error into _errorKey, so the predecessor's fault is already
                    // reported; rethrowing it here would fail THIS request as well.
                    catch (Exception) { }
                    if (ct.IsCancellationRequested) return;
                    try { outcome = work(ct); }
                    catch (OperationCanceledException) { return; }   // superseded - drop silently
                    catch (Exception ex) { error = ex.Message; }
                    if (ct.IsCancellationRequested) return;
                    Rhino.RhinoApp.InvokeOnUiThread(
                        (Action)(() => Complete(key, outcome, error, requestCts)));
                    completionQueued = true;
                }
                finally
                {
                    // A queued completion owns disposal on the UI thread. Cancelled,
                    // detached or marshal-failed work has no callback, so clean it here.
                    if (!completionQueued) requestCts.Dispose();
                }
            });
        }

        void Complete(string key, T? outcome, string? error, CancellationTokenSource requestCts)
        {
            try
            {
                if (_pendingKey != key) return;   // a newer request superseded this one
                _pendingKey = null;
                if (ReferenceEquals(_cts, requestCts)) _cts = null;
                if (error == null)
                {
                    _resultKey = key; _result = outcome;
                    if (_errorKey == key) { _errorKey = null; _error = null; }
                }
                else { _errorKey = key; _error = $"{_role} failed: {error}"; }

                var owner = _owner;
                if (owner != null && owner.OnPingDocument() != null && !owner.Locked)
                {
                    if (owner is IamComponentBase iam) iam.OnBackgroundWorkCompleted();
                    else owner.ExpireSolution(true);
                }
            }
            finally { requestCts.Dispose(); }
        }

        /// <summary>Cancels the pending request and detaches from the component -
        /// call on component removal and document close.</summary>
        public void CancelAndDetach()
        {
            try { _cts?.Cancel(); }
            // best effort: as in Start - the component is being removed either way, and
            // the fields below detach it whether or not the source was still live.
            catch (ObjectDisposedException) { }
            _cts = null;
            _pendingKey = null;
            _owner = null;
        }

        /// <summary>Forgets a completed error so the same key can be retried
        /// (e.g. a rising write edge after the user fixed the folder).</summary>
        public void ForgetError()
        {
            _errorKey = null;
            _error = null;
        }
    }
}
