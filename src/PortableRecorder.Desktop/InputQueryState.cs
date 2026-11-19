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

    public void Finish(Request request, Task completion)
    {
        _active = null;
        _ = request.CompleteAfterAsync(completion);
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

    private Request _current;
    public int Version => _current.Version;
    public string? DeviceId => _current.DeviceId;
    public bool Loading { get; private set; }
    public InputFormatOptions? Options { get; private set; }
    public string? Error { get; private set; }

    public Request Begin(string? deviceId)
    {
        _current = new(Version + 1, deviceId);
        Loading = deviceId != null;
        Options = null;
        Error = null;
        return _current;
    }

    public void Invalidate() => Begin(null);

    public bool TryComplete(Request request, string? selectedDevice, bool recordingContext,
        InputFormatOptions? options, string? error)
    {
        if (!recordingContext || !Loading || request != _current || request.DeviceId != selectedDevice) return false;
        Options = options;
        Error = error;
        Loading = false;
        return true;
    }
}
