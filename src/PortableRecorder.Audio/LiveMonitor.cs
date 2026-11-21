using System.Diagnostics;

namespace PortableRecorder.Audio;

public sealed record MonitorOutputFormat(int SampleRate, int Channels);

public sealed record LiveMonitorStatus(bool Enabled, bool Active, string? Error = null);

public interface ILiveMonitorOutput : IAsyncDisposable
{
    void Write(ReadOnlySpan<float> interleavedSamples, int sourceSampleRate, int sourceChannels, double linearGain);
    event Action<Exception>? Faulted;
}

public interface ILiveMonitorFactory
{
    Task<ILiveMonitorOutput> OpenDefaultAsync(CancellationToken cancellationToken = default);
    IDisposable WatchDefaultDeviceChanged(Action changed);
}

internal delegate void LiveMonitorFeed(ReadOnlySpan<float> samples, AudioFormat format);

internal sealed class LiveMonitorBuffer
{
    private readonly float[] _ring;
    private readonly int _capacityFrames;
    private readonly int _mask;
    private readonly int _maxQueuedFrames;
    private readonly int _slewFrames;
    private readonly float[] _prevFrame;
    private readonly float[] _curFrame;
    private readonly float[] _nextFrame;

    private long _readFrame;
    private long _writeFrame;

    private int _lastSourceRate;
    private int _lastSourceChannels;
    private bool _hasPrevFrame;
    private double _phase;

    private bool _gainInitialized;
    private double _currentGain = 1.0;
    private double _targetGain = 1.0;
    private double _gainStep;
    private int _gainRampRemaining;

    public MonitorOutputFormat OutputFormat { get; }
    public int CapacityFrames => _capacityFrames;
    public int MaxQueuedFrames => _maxQueuedFrames;
    public long QueuedFrames => Math.Max(0, Volatile.Read(ref _writeFrame) - Volatile.Read(ref _readFrame));

    public LiveMonitorBuffer(MonitorOutputFormat outputFormat)
    {
        if (outputFormat.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputFormat));
        if (outputFormat.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(outputFormat));
        OutputFormat = outputFormat;
        _capacityFrames = NextPowerOfTwo(Math.Max(outputFormat.SampleRate * 80 / 1000, 1024));
        _mask = _capacityFrames - 1;
        _maxQueuedFrames = Math.Clamp(outputFormat.SampleRate * 40 / 1000, 256, _capacityFrames / 2);
        _slewFrames = Math.Max(1, outputFormat.SampleRate * 5 / 1000);
        _ring = new float[_capacityFrames * outputFormat.Channels];
        _prevFrame = new float[outputFormat.Channels];
        _curFrame = new float[outputFormat.Channels];
        _nextFrame = new float[outputFormat.Channels];
    }

    public void Write(ReadOnlySpan<float> interleavedSamples, int sourceSampleRate, int sourceChannels, double linearGain)
    {
        if (interleavedSamples.IsEmpty || sourceSampleRate <= 0 || sourceChannels <= 0) return;
        int srcFrames = interleavedSamples.Length / sourceChannels;
        if (srcFrames == 0) return;

        double target = double.IsFinite(linearGain) && linearGain >= 0 ? linearGain : 0;
        if (!_gainInitialized)
        {
            _currentGain = target;
            _targetGain = target;
            _gainRampRemaining = 0;
            _gainInitialized = true;
        }
        else if (target != _targetGain)
        {
            _targetGain = target;
            _gainStep = (target - _currentGain) / _slewFrames;
            _gainRampRemaining = _slewFrames;
        }

        if (sourceSampleRate != _lastSourceRate || sourceChannels != _lastSourceChannels)
        {
            _lastSourceRate = sourceSampleRate;
            _lastSourceChannels = sourceChannels;
            _hasPrevFrame = false;
            _phase = 0;
            Array.Clear(_prevFrame);
        }

        int dstChannels = OutputFormat.Channels;
        if (sourceSampleRate == OutputFormat.SampleRate)
        {
            WriteDirect(interleavedSamples, srcFrames, sourceChannels, dstChannels);
        }
        else
        {
            WriteResampled(interleavedSamples, srcFrames, sourceSampleRate, sourceChannels, dstChannels);
        }
    }

    public int Read(Span<float> destination)
    {
        int dstChannels = OutputFormat.Channels;
        int requestedFrames = destination.Length / dstChannels;
        if (requestedFrames == 0)
        {
            destination.Clear();
            return 0;
        }

        while (true)
        {
            long origRead = Volatile.Read(ref _readFrame);
            long write = Volatile.Read(ref _writeFrame);
            long available = write - origRead;
            if (available <= 0)
            {
                destination.Clear();
                return 0;
            }

            long startRead = available > _maxQueuedFrames ? write - _maxQueuedFrames : origRead;
            int toRead = (int)Math.Min(requestedFrames, write - startRead);
            CopyFromRing(startRead, destination.Slice(0, toRead * dstChannels), toRead, dstChannels);

            if (TryCommitReadIfNotOverrun(origRead, startRead + toRead))
            {
                if (toRead * dstChannels < destination.Length)
                    destination.Slice(toRead * dstChannels).Clear();
                return toRead;
            }
        }
    }

    private bool TryCommitReadIfNotOverrun(long expectedRead, long nextRead)
        => Interlocked.CompareExchange(ref _readFrame, nextRead, expectedRead) == expectedRead;

    private void WriteDirect(ReadOnlySpan<float> input, int srcFrames, int srcChannels, int dstChannels)
    {
        int startFrame = 0;
        if (srcFrames > _maxQueuedFrames)
        {
            int skipped = srcFrames - _maxQueuedFrames;
            AdvanceGain(skipped);
            startFrame = skipped;
        }

        int count = srcFrames - startFrame;
        long write = _writeFrame;
        EnsureHeadroom(write, count);

        for (int i = 0; i < count; i++)
        {
            float gain = NextGain();
            MapFrame(input.Slice((startFrame + i) * srcChannels, srcChannels), srcChannels, _curFrame, dstChannels);
            WriteRingFrame(write + i, _curFrame, dstChannels, gain);
        }

        Volatile.Write(ref _writeFrame, write + count);
    }

    private void WriteResampled(ReadOnlySpan<float> input, int srcFrames, int srcRate, int srcChannels, int dstChannels)
    {
        double step = (double)srcRate / OutputFormat.SampleRate;
        if (!_hasPrevFrame) _phase = 0;

        long write = _writeFrame;
        while (_phase <= srcFrames - 1)
        {
            float gain = NextGain();
            if (_phase < 0)
            {
                float frac = (float)(_phase + 1.0);
                MapFrame(input.Slice(0, srcChannels), srcChannels, _curFrame, dstChannels);
                for (int c = 0; c < dstChannels; c++)
                    _nextFrame[c] = _prevFrame[c] + (_curFrame[c] - _prevFrame[c]) * frac;
            }
            else
            {
                int i0 = (int)_phase;
                float frac = (float)(_phase - i0);
                MapFrame(input.Slice(i0 * srcChannels, srcChannels), srcChannels, _curFrame, dstChannels);
                if (i0 >= srcFrames - 1 || frac == 0f)
                {
                    for (int c = 0; c < dstChannels; c++) _nextFrame[c] = _curFrame[c];
                }
                else
                {
                    MapFrame(input.Slice((i0 + 1) * srcChannels, srcChannels), srcChannels, _nextFrame, dstChannels);
                    for (int c = 0; c < dstChannels; c++)
                        _nextFrame[c] = _curFrame[c] + (_nextFrame[c] - _curFrame[c]) * frac;
                }
            }

            EnsureHeadroom(write, 1);
            WriteRingFrame(write, _nextFrame, dstChannels, gain);
            write++;
            _phase += step;
        }

        if (write != _writeFrame) Volatile.Write(ref _writeFrame, write);
        MapFrame(input.Slice((srcFrames - 1) * srcChannels, srcChannels), srcChannels, _prevFrame, dstChannels);
        _hasPrevFrame = true;
        _phase -= srcFrames;
    }

    private void EnsureHeadroom(long currentWrite, int framesToWrite)
    {
        long targetRead = currentWrite + framesToWrite - _maxQueuedFrames;
        while (true)
        {
            long read = Volatile.Read(ref _readFrame);
            if (read >= targetRead) return;
            if (Interlocked.CompareExchange(ref _readFrame, targetRead, read) == read) return;
        }
    }

    private void WriteRingFrame(long frameIndex, ReadOnlySpan<float> mappedFrame, int dstChannels, float gain)
    {
        int offset = (int)(frameIndex & _mask) * dstChannels;
        for (int c = 0; c < dstChannels; c++)
        {
            float scaled = mappedFrame[c] * gain;
            _ring[offset + c] = float.IsFinite(scaled) ? Math.Clamp(scaled, -1f, 1f) : 0f;
        }
    }

    private void CopyFromRing(long startFrame, Span<float> destination, int frameCount, int dstChannels)
    {
        int startSlot = (int)(startFrame & _mask);
        int firstFrames = Math.Min(frameCount, _capacityFrames - startSlot);
        int firstSamples = firstFrames * dstChannels;
        _ring.AsSpan(startSlot * dstChannels, firstSamples).CopyTo(destination);

        int remainingFrames = frameCount - firstFrames;
        if (remainingFrames > 0)
            _ring.AsSpan(0, remainingFrames * dstChannels).CopyTo(destination.Slice(firstSamples));
    }

    private static void MapFrame(ReadOnlySpan<float> src, int srcChannels, Span<float> dst, int dstChannels)
    {
        if (srcChannels == 1 && dstChannels >= 2)
        {
            float mono = Sanitize(src[0]);
            dst[0] = mono;
            dst[1] = mono;
            for (int c = 2; c < dstChannels; c++) dst[c] = 0f;
        }
        else if (srcChannels >= 2 && dstChannels == 1)
        {
            dst[0] = (Sanitize(src[0]) + Sanitize(src[1])) * 0.5f;
        }
        else
        {
            int copy = Math.Min(srcChannels, dstChannels);
            for (int c = 0; c < copy; c++) dst[c] = Sanitize(src[c]);
            for (int c = copy; c < dstChannels; c++) dst[c] = 0f;
        }
    }

    private static float Sanitize(float value) => float.IsFinite(value) ? value : 0f;

    private float NextGain()
    {
        if (_gainRampRemaining > 0)
        {
            _currentGain += _gainStep;
            if (--_gainRampRemaining == 0) _currentGain = _targetGain;
        }
        return (float)_currentGain;
    }

    private void AdvanceGain(int frames)
    {
        if (_gainRampRemaining <= 0) return;
        if (frames >= _gainRampRemaining)
        {
            _currentGain = _targetGain;
            _gainRampRemaining = 0;
        }
        else
        {
            _currentGain += _gainStep * frames;
            _gainRampRemaining -= frames;
        }
    }

    private static int NextPowerOfTwo(int value)
    {
        int n = 1;
        while (n < value) n <<= 1;
        return n;
    }
}

internal sealed class LiveMonitorController : IAsyncDisposable
{
    private abstract record MonitorPhase
    {
        private MonitorPhase() { }
        public sealed record Disabled(string? Error = null) : MonitorPhase;
        public sealed record Starting : MonitorPhase;
        public sealed record Running(ILiveMonitorOutput Output, IDisposable Watcher, long Generation) : MonitorPhase;
        public sealed record Disposed : MonitorPhase;
    }

    private readonly ILiveMonitorFactory _factory;
    private readonly Action<string>? _diagnostic;
    private readonly TimeSpan _rebindDebounce;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _rebindLock = new();
    private MonitorPhase _phase = new MonitorPhase.Disabled();
    private ILiveMonitorOutput? _activeOutput;
    private double _linearGain = 1.0;
    private long _rebindGeneration;
    private bool _rebindPending;
    private bool _rebindDeviceChanged;
    private Exception? _rebindFault;
    private bool _rebindWorkerRunning;
    private long _lastFaultTicks;
    private int _consecutiveFaultRebinds;

    public event Action<string>? Faulted;

    public LiveMonitorController(ILiveMonitorFactory factory, Action<string>? diagnostic = null, TimeSpan? rebindDebounce = null)
    {
        _factory = factory;
        _diagnostic = diagnostic;
        _rebindDebounce = rebindDebounce ?? TimeSpan.FromMilliseconds(40);
    }

    public LiveMonitorStatus Status => Volatile.Read(ref _phase) switch
    {
        MonitorPhase.Running => new(true, true),
        MonitorPhase.Starting => new(true, false),
        MonitorPhase.Disabled d => new(false, false, d.Error),
        _ => new(false, false)
    };

    public void SetGain(double db)
    {
        if (!double.IsFinite(db)) throw new ArgumentOutOfRangeException(nameof(db), "监听增益必须为有限数值。");
        Interlocked.Exchange(ref _linearGain, Math.Pow(10, Math.Clamp(db, -60, 60) / 20));
    }

    public void Feed(ReadOnlySpan<float> samples, AudioFormat format)
    {
        var output = Volatile.Read(ref _activeOutput);
        if (output == null || samples.IsEmpty) return;
        double gain = Volatile.Read(ref _linearGain);
        try
        {
            output.Write(samples, format.SampleRate, format.Channels, gain);
        }
        catch (Exception error)
        {
            HandleOutputFault(output, error);
        }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var phase = Volatile.Read(ref _phase);
            if (phase is MonitorPhase.Disposed)
            {
                if (enabled) throw new ObjectDisposedException(nameof(LiveMonitorController));
                return;
            }

            if (enabled)
            {
                if (phase is MonitorPhase.Running) return;
                Volatile.Write(ref _phase, new MonitorPhase.Starting());
                long generation;
                lock (_rebindLock)
                {
                    generation = ++_rebindGeneration;
                    _rebindPending = false;
                    _rebindDeviceChanged = false;
                    _rebindFault = null;
                }
                _consecutiveFaultRebinds = 0;
                _lastFaultTicks = 0;
                try
                {
                    await OpenAndAttachLockedAsync(generation).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    await TeardownLockedAsync(new MonitorPhase.Disabled(AudioEngine.Friendly(error))).ConfigureAwait(false);
                    throw;
                }
            }
            else
            {
                await TeardownLockedAsync(new MonitorPhase.Disabled()).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OpenAndAttachLockedAsync(long generation, IDisposable? existingWatcher = null)
    {
        ILiveMonitorOutput? output = null;
        IDisposable? ownedWatcher = null;
        try
        {
            output = await _factory.OpenDefaultAsync().ConfigureAwait(false);
            var opened = output;
            Exception? earlyFault = null;
            int attached = 0;
            opened.Faulted += error =>
            {
                if (Volatile.Read(ref attached) == 0)
                {
                    Interlocked.CompareExchange(ref earlyFault, error, null);
                    if (Volatile.Read(ref attached) == 0) return;
                    if (Interlocked.Exchange(ref earlyFault, null) is Exception captured)
                        HandleOutputFault(opened, captured);
                    return;
                }
                HandleOutputFault(opened, error);
            };
            var watcher = existingWatcher ?? (ownedWatcher = _factory.WatchDefaultDeviceChanged(() => RequestRebind(generation, deviceChanged: true, fault: null)));
            Volatile.Write(ref _phase, new MonitorPhase.Running(opened, watcher, generation));
            Volatile.Write(ref _activeOutput, opened);
            Volatile.Write(ref attached, 1);
            if (Interlocked.Exchange(ref earlyFault, null) is Exception pendingEarlyFault)
                HandleOutputFault(opened, pendingEarlyFault);
        }
        catch
        {
            SafeDisposeWatcher(ownedWatcher);
            await SafeDisposeOutputAsync(output).ConfigureAwait(false);
            throw;
        }
    }

    private async Task TeardownLockedAsync(MonitorPhase nextPhase)
    {
        var phase = Volatile.Read(ref _phase);
        Volatile.Write(ref _activeOutput, null);
        lock (_rebindLock)
        {
            _rebindGeneration++;
            _rebindPending = false;
            _rebindDeviceChanged = false;
            _rebindFault = null;
        }
        Volatile.Write(ref _phase, nextPhase);
        if (phase is MonitorPhase.Running running)
        {
            SafeDisposeWatcher(running.Watcher);
            await SafeDisposeOutputAsync(running.Output).ConfigureAwait(false);
        }
    }

    private void HandleOutputFault(ILiveMonitorOutput source, Exception error)
    {
        if (Interlocked.CompareExchange(ref _activeOutput, null, source) != source) return;
        var phase = Volatile.Read(ref _phase);
        if (phase is not MonitorPhase.Running running || !ReferenceEquals(running.Output, source)) return;
        RequestRebind(running.Generation, deviceChanged: false, fault: error);
    }

    private void RequestRebind(long generation, bool deviceChanged, Exception? fault)
    {
        lock (_rebindLock)
        {
            if (_rebindGeneration != generation) return;
            if (deviceChanged) _rebindDeviceChanged = true;
            _rebindFault ??= fault;
            _rebindPending = true;
            if (_rebindWorkerRunning) return;
            _rebindWorkerRunning = true;
        }
        _ = Task.Run(RunRebindLoopAsync);
    }

    private async Task RunRebindLoopAsync()
    {
        try
        {
            while (true)
            {
                long batchGeneration;
                bool deviceChanged;
                Exception? initialFault;
                lock (_rebindLock)
                {
                    if (!_rebindPending) break;
                    _rebindPending = false;
                    batchGeneration = _rebindGeneration;
                    deviceChanged = _rebindDeviceChanged;
                    _rebindDeviceChanged = false;
                    initialFault = _rebindFault;
                    _rebindFault = null;
                }

                if (deviceChanged && initialFault == null && _rebindDebounce > TimeSpan.Zero)
                    await Task.Delay(_rebindDebounce).ConfigureAwait(false);

                string? faultNotification = null;
                bool stopLoop = false;
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    (stopLoop, faultNotification) = await RebindOnceLockedAsync(batchGeneration, deviceChanged, initialFault).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }

                if (faultNotification != null)
                {
                    NotifyFaulted(faultNotification);
                    return;
                }
                if (stopLoop) break;
            }
        }
        catch (Exception unexpected)
        {
            Trace("Live monitor rebind loop failed: " + unexpected);
        }
        finally
        {
            bool restart = false;
            lock (_rebindLock)
            {
                if (_rebindPending) restart = true;
                else _rebindWorkerRunning = false;
            }
            if (restart) _ = Task.Run(RunRebindLoopAsync);
        }
    }

    private async Task<(bool StopLoop, string? FaultNotification)> RebindOnceLockedAsync(long batchGeneration, bool deviceChanged, Exception? initialFault)
    {
        if (Volatile.Read(ref _phase) is not MonitorPhase.Running running)
            return (true, null);
        if (running.Generation != batchGeneration)
            return (false, null);

        bool wasDeviceChange = deviceChanged;
        Exception? fault = initialFault;
        lock (_rebindLock)
        {
            if (_rebindGeneration == batchGeneration)
            {
                _rebindPending = false;
                if (_rebindDeviceChanged)
                {
                    wasDeviceChange = true;
                    _rebindDeviceChanged = false;
                }
                fault ??= _rebindFault;
                _rebindFault = null;
            }
        }

        if (wasDeviceChange)
        {
            _consecutiveFaultRebinds = 0;
        }
        else if (fault != null)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastFaultTicks != 0 && Stopwatch.GetElapsedTime(_lastFaultTicks, now) > TimeSpan.FromSeconds(2))
                _consecutiveFaultRebinds = 0;
            _lastFaultTicks = now;
            if (++_consecutiveFaultRebinds > 1)
            {
                string reason = AudioEngine.Friendly(fault);
                await TeardownLockedAsync(new MonitorPhase.Disabled(reason)).ConfigureAwait(false);
                return (true, reason);
            }
        }

        try
        {
            await OpenAndAttachLockedAsync(running.Generation, running.Watcher).ConfigureAwait(false);
        }
        catch (Exception rebindError)
        {
            string reason = AudioEngine.Friendly(rebindError);
            await TeardownLockedAsync(new MonitorPhase.Disabled(reason)).ConfigureAwait(false);
            return (true, reason);
        }

        await SafeDisposeOutputAsync(running.Output).ConfigureAwait(false);
        return (false, null);
    }

    private void NotifyFaulted(string reason)
    {
        _ = Task.Run(() =>
        {
            try { Faulted?.Invoke(reason); }
            catch (Exception error) { Trace("Live monitor fault notification failed: " + error); }
        });
    }

    private void SafeDisposeWatcher(IDisposable? watcher)
    {
        if (watcher == null) return;
        try { watcher.Dispose(); }
        catch (Exception error) { Trace("Live monitor watcher dispose failed: " + error); }
    }

    private async Task SafeDisposeOutputAsync(ILiveMonitorOutput? output)
    {
        if (output == null) return;
        try { await output.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { Trace("Live monitor output dispose failed: " + error); }
    }

    private void Trace(string message)
    {
        try { _diagnostic?.Invoke(message); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _phase) is MonitorPhase.Disposed) return;
            await TeardownLockedAsync(new MonitorPhase.Disposed()).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
