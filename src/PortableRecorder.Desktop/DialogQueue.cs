namespace PortableRecorder.Desktop;

/// <summary>One modal presenter per window, including error and RAW-consent dialogs.</summary>
internal sealed class DialogQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private int _pending, _isClosed;

    public bool IsBusy => Volatile.Read(ref _pending) != 0;

    public async Task<T> ShowAsync<T>(Func<CancellationToken, Task<T>> show)
    {
        Interlocked.Increment(ref _pending);
        bool entered = false;
        try
        {
            await _gate.WaitAsync(_closed.Token);
            entered = true;
            _closed.Token.ThrowIfCancellationRequested();
            return await show(_closed.Token);
        }
        finally
        {
            if (entered) _gate.Release();
            Interlocked.Decrement(ref _pending);
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _isClosed, 1) == 0) _closed.Cancel();
    }
}
