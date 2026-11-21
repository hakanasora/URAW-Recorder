using PortableRecorder.Audio;

namespace PortableRecorder.Desktop;

// All transitions happen on the dispatcher thread. Invalidation keeps the active slot
// reserved until its enumeration returns, even if a new recording workspace has opened.
internal sealed class DeviceRefreshQueue
{
    internal sealed class Request
    {
        private TaskCompletionSource? _resume;
        private TaskCompletionSource? _completion;
        internal bool Manual { get; set; }
        internal bool ModeInitialization { get; set; }
        internal bool Invalidated { get; private set; }

        internal Task WaitForCompletionAsync() =>
            (_completion ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        internal async Task CompleteAfterAsync(Task completion)
        {
            try
            {
                await completion;
                _completion?.TrySetResult();
            }
            catch (Exception error) { _completion?.TrySetException(error); }
        }

        internal Task WaitUntilReadyAsync() =>
            (_resume ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        internal void Resume()
        {
            var resume = _resume;
            _resume = null;
            resume?.TrySetResult();
        }

        internal void Invalidate()
        {
            Invalidated = true;
            _completion?.TrySetResult();
            Resume();
        }
    }

    private Request? _active, _pending;
    private bool _topologyReady;

    public void RestartDebounce() => _topologyReady = false;
    public void DebounceElapsed() => _topologyReady = true;

    public Task Enqueue(bool manual, bool modeInitialization)
    {
        var request = _pending ??= new();
        request.Manual |= manual;
        request.ModeInitialization |= modeInitialization;
        return request.WaitForCompletionAsync();
    }

    public Request? TryBegin(bool allowRegular, bool allowModeInitialization)
    {
        if (_active is { } active)
        {
            if (active.ModeInitialization ? allowModeInitialization : allowRegular) active.Resume();
            return null;
        }

        bool allowed = _pending is { ModeInitialization: true } ? allowModeInitialization : allowRegular;
        if (!allowed || (_pending == null && !_topologyReady)) return null;

        _active = _pending ?? new();
        _pending = null;
        _topologyReady = false;
        return _active;
    }

    public void Finish(Request request, Task completion, Action? onFinished = null)
    {
        _active = null;
        _ = ObserveAsync(request, completion, onFinished);
    }

    private static async Task ObserveAsync(Request request, Task completion, Action? onFinished)
    {
        try { await request.CompleteAfterAsync(completion); }
        finally { onFinished?.Invoke(); }
    }

    public void Invalidate()
    {
        _topologyReady = false;
        _pending?.Invalidate();
        _pending = null;
        _active?.Invalidate();
    }
}

internal sealed class InputFormatQuery
{
    public readonly record struct Request(int Version, string? DeviceId);

    public abstract record QueryPhase
    {
        private QueryPhase() { }
        public sealed record Idle(int Version) : QueryPhase;
        public sealed record Loading(int Version, string DeviceId) : QueryPhase;
        public sealed record Ready(int Version, string DeviceId, InputFormatOptions Options) : QueryPhase;
        public sealed record Failed(int Version, string DeviceId, string Error) : QueryPhase;
    }

    public QueryPhase Phase { get; private set; } = new QueryPhase.Idle(0);

    public int Version => Phase switch
    {
        QueryPhase.Idle i => i.Version,
        QueryPhase.Loading l => l.Version,
        QueryPhase.Ready r => r.Version,
        QueryPhase.Failed f => f.Version,
        _ => 0
    };

    public string? DeviceId => Phase switch
    {
        QueryPhase.Loading l => l.DeviceId,
        QueryPhase.Ready r => r.DeviceId,
        QueryPhase.Failed f => f.DeviceId,
        _ => null
    };

    public bool Loading => Phase is QueryPhase.Loading;
    public InputFormatOptions? Options => (Phase as QueryPhase.Ready)?.Options;
    public string? Error => (Phase as QueryPhase.Failed)?.Error;

    public Request Begin(string? deviceId)
    {
        int nextVersion = Version + 1;
        Phase = deviceId == null ? new QueryPhase.Idle(nextVersion) : new QueryPhase.Loading(nextVersion, deviceId);
        return new(nextVersion, deviceId);
    }

    public void Invalidate() => Begin(null);

    public bool TryComplete(Request request, string? selectedDevice, bool recordingContext,
        InputFormatOptions? options, string? error)
    {
        if (!recordingContext || Phase is not QueryPhase.Loading loading ||
            request.Version != loading.Version || request.DeviceId != loading.DeviceId ||
            request.DeviceId != selectedDevice)
            return false;

        Phase = error != null
            ? new QueryPhase.Failed(loading.Version, loading.DeviceId, error)
            : options != null
                ? new QueryPhase.Ready(loading.Version, loading.DeviceId, options)
                : new QueryPhase.Idle(loading.Version);
        return true;
    }
}
