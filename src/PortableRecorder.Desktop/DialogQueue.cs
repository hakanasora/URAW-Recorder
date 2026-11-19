namespace PortableRecorder.Desktop;

/// <summary>One modal presenter per window, including error and RAW-consent dialogs.</summary>
internal sealed class DialogQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private readonly CancellationTokenSource _normalClosed = new();
    private int _pending, _isClosed, _cleanupStarted, _cleanupErrorShown;

    public bool IsBusy => Volatile.Read(ref _pending) != 0;

    public async Task<T> ShowAsync<T>(Func<CancellationToken, Task<T>> show, bool cleanupError = false)
    {
        if (cleanupError && (Volatile.Read(ref _cleanupStarted) == 0 ||
            Interlocked.Exchange(ref _cleanupErrorShown, 1) != 0))
            throw new OperationCanceledException();

        var token = cleanupError ? _closed.Token : _normalClosed.Token;
        Interlocked.Increment(ref _pending);
        bool entered = false;
        try
        {
            await _gate.WaitAsync(token);
            entered = true;
            token.ThrowIfCancellationRequested();
            return await show(token);
        }
        finally
        {
            if (entered) _gate.Release();
            Interlocked.Decrement(ref _pending);
        }
    }

    public void BeginCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) == 0) _normalClosed.Cancel();
    }

    public async Task<bool> WaitForPresenterAsync(TimeSpan timeout)
    {
        if (!await _gate.WaitAsync(timeout)) return false;
        _gate.Release();
        return true;
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _isClosed, 1) != 0) return;
        BeginCleanup();
        _closed.Cancel();
    }
}
