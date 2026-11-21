using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PortableRecorder.Audio;

public sealed class WasapiLiveMonitorFactory : ILiveMonitorFactory
{
    public async Task<ILiveMonitorOutput> OpenDefaultAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = new WasapiLiveMonitorOutput();
        try
        {
            await output.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            return output;
        }
        catch (Exception openingError)
        {
            var described = openingError is OperationCanceledException
                ? openingError
                : WasapiLiveMonitorOutput.DescribeRenderError(openingError);
            try { await output.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                throw AudioFailure.Combine(described, WasapiLiveMonitorOutput.DescribeRenderError(cleanupError));
            }
            if (ReferenceEquals(described, openingError)) throw;
            throw described;
        }
    }

    public IDisposable WatchDefaultDeviceChanged(Action changed)
        => new DefaultRenderDeviceWatcher(changed);

    // Native callbacks only enqueue rebind work; never enumerate or dispose streams on the COM thread.
    private sealed class DefaultRenderDeviceWatcher : IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly MMDeviceNotificationClient _notifications;

        public DefaultRenderDeviceWatcher(Action changed)
        {
            try { _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false); }
            catch { _enumerator.Dispose(); throw; }
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia) changed();
            };
        }

        public void Dispose()
        {
            _notifications.Dispose();
            _enumerator.Dispose();
        }
    }
}

internal sealed class WasapiLiveMonitorOutput : ILiveMonitorOutput
{
    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    private const int AudclntEDeviceInUse = unchecked((int)0x8889000A);
    private const int ErrorNotFound = unchecked((int)0x80070490);
    private const int EAccessDenied = unchecked((int)0x80070005);

    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<MonitorOutputFormat> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _disposeGate = new();
    private readonly object _faultLock = new();
    private Action<Exception>? _faulted;
    private Exception? _storedFault;
    private Task? _disposeTask;
    private LiveMonitorBuffer? _buffer;

    public Task<MonitorOutputFormat> Ready => _ready.Task;

    public event Action<Exception>? Faulted
    {
        add
        {
            if (value == null) return;
            Exception? stored;
            lock (_faultLock)
            {
                _faulted += value;
                stored = _storedFault;
            }
            if (stored != null) _ = Task.Run(() => value(stored));
        }
        remove
        {
            if (value == null) return;
            lock (_faultLock) _faulted -= value;
        }
    }

    public WasapiLiveMonitorOutput()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Portable Recorder Monitor Out" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public void Write(ReadOnlySpan<float> interleavedSamples, int sourceSampleRate, int sourceChannels, double linearGain)
    {
        Volatile.Read(ref _buffer)?.Write(interleavedSamples, sourceSampleRate, sourceChannels, linearGain);
    }

    private void Run()
    {
        AudioClient? client = null;
        AudioRenderClient? renderClient = null;
        EventWaitHandle? signal = null;
        IntPtr mmcss = IntPtr.Zero;
        bool started = false;
        Exception? cleanupFailure = null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice device;
            try { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
            catch (COMException error) when (error.HResult == ErrorNotFound)
            {
                throw new IOException("没有可用的默认播放设备。");
            }

            using (device)
            {
                client = device.CreateAudioClient();
                var mix = client.MixFormat;
                var format = WasapiCaptureSession.Describe(mix);
                var outputFormat = new MonitorOutputFormat(format.SampleRate, format.Channels);
                var buffer = new LiveMonitorBuffer(outputFormat);
                Volatile.Write(ref _buffer, buffer);

                signal = new EventWaitHandle(false, EventResetMode.AutoReset);
                long duration = Math.Max(200_000, client.DefaultDevicePeriod);
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, duration, 0, mix, Guid.Empty);
                client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle());
                renderClient = client.AudioRenderClient;

                uint taskIndex = 0;
                mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
                if (mmcss == IntPtr.Zero)
                {
                    mmcss = AvSetMmThreadCharacteristics("Audio", ref taskIndex);
                    if (mmcss == IntPtr.Zero) Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
                }

                int bufferFrames = client.BufferSize;
                int periodFrames = Math.Clamp((int)Math.Ceiling(format.SampleRate * client.DefaultDevicePeriod / 10_000_000d), 1, bufferFrames);
                _ = renderClient.GetBuffer(periodFrames);
                renderClient.ReleaseBuffer(periodFrames, AudioClientBufferFlags.Silent);

                var floatScratch = new float[checked(bufferFrames * format.Channels)];
                byte[]? pcmScratch = format.Encoding == PcmEncoding.Float ? null : new byte[checked(bufferFrames * format.BlockAlign)];
                var waits = new WaitHandle[] { signal, _stop.Token.WaitHandle };

                client.Start();
                started = true;
                _ready.TrySetResult(outputFormat);

                while (!_stop.IsCancellationRequested)
                {
                    int signaled = WaitHandle.WaitAny(waits, 2000);
                    if (signaled == 1) break;
                    if (signaled == WaitHandle.WaitTimeout) continue;

                    int available = bufferFrames - client.CurrentPadding;
                    if (available <= 0) continue;

                    var span = floatScratch.AsSpan(0, checked(available * format.Channels));
                    buffer.Read(span);

                    IntPtr dest = renderClient.GetBuffer(available);
                    int written = 0;
                    try
                    {
                        WriteToNativeBuffer(span, dest, format, pcmScratch);
                        written = available;
                    }
                    finally
                    {
                        renderClient.ReleaseBuffer(written, AudioClientBufferFlags.None);
                    }
                }
            }
        }
        catch (Exception error)
        {
            var reported = DescribeRenderError(error);
            if (!_ready.TrySetException(reported) && !_stop.IsCancellationRequested)
            {
                Action<Exception>? handler;
                lock (_faultLock)
                {
                    _storedFault ??= reported;
                    handler = _faulted;
                }
                if (handler != null)
                {
                    try { handler.Invoke(reported); }
                    catch (Exception callbackError) { cleanupFailure = callbackError; }
                }
            }
        }
        finally
        {
            if (client != null)
            {
                if (started)
                    try { client.Stop(); } catch (Exception error) { cleanupFailure = AudioFailure.Combine(cleanupFailure, error); }
                if (renderClient != null)
                    try { renderClient.Dispose(); } catch (Exception error) { cleanupFailure = AudioFailure.Combine(cleanupFailure, error); }
                try { client.Dispose(); } catch (Exception error) { cleanupFailure = AudioFailure.Combine(cleanupFailure, error); }
            }
            if (signal != null)
                try { signal.Dispose(); } catch (Exception error) { cleanupFailure = AudioFailure.Combine(cleanupFailure, error); }
            if (mmcss != IntPtr.Zero)
            {
                try
                {
                    if (!AvRevertMmThreadCharacteristics(mmcss))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法释放监听播放线程调度注册。");
                }
                catch (Exception error) { cleanupFailure = AudioFailure.Combine(cleanupFailure, error); }
            }

            if (cleanupFailure == null) _done.TrySetResult();
            else _done.TrySetException(cleanupFailure);
        }
    }

    internal static Exception DescribeRenderError(Exception error) => error.HResult switch
    {
        AudclntEDeviceInvalidated => new IOException("默认播放设备已断开或被系统重新配置。"),
        AudclntEDeviceInUse => new IOException("默认播放设备正被其他程序独占占用。"),
        ErrorNotFound => new IOException("没有可用的默认播放设备。"),
        EAccessDenied => new IOException("无法访问默认播放设备，请检查 Windows 音频播放权限。"),
        _ => error
    };

    private static unsafe void WriteToNativeBuffer(ReadOnlySpan<float> samples, IntPtr destination, AudioFormat format, byte[]? pcmScratch)
    {
        if (format.Encoding == PcmEncoding.Float && format.Bits == 32)
        {
            samples.CopyTo(new Span<float>((void*)destination, samples.Length));
            return;
        }

        int byteCount = checked(samples.Length * (format.Bits / 8));
        var scratch = pcmScratch.AsSpan(0, byteCount);
        EncodePcm(samples, scratch, format);
        scratch.CopyTo(new Span<byte>((void*)destination, byteCount));
    }

    internal static void EncodePcm(ReadOnlySpan<float> samples, Span<byte> destination, AudioFormat format)
    {
        int width = format.Bits / 8;
        if (destination.Length < samples.Length * width)
            throw new ArgumentException("PCM output buffer is too small.", nameof(destination));

        if (format.Encoding == PcmEncoding.Float && format.Bits == 32)
        {
            MemoryMarshal.AsBytes(samples).CopyTo(destination);
            return;
        }

        long scale = 1L << (format.ValidBits - 1);
        int shift = format.Bits - format.ValidBits;
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1f, 1f) : 0f;
            long code = Math.Clamp((long)Math.Round(clamped * scale), -scale, scale - 1) << shift;
            var target = destination.Slice(i * width, width);
            switch (width)
            {
                case 2:
                    BinaryPrimitives.WriteInt16LittleEndian(target, (short)code);
                    break;
                case 3:
                    target[0] = (byte)code;
                    target[1] = (byte)(code >> 8);
                    target[2] = (byte)(code >> 16);
                    break;
                case 4:
                    BinaryPrimitives.WriteInt32LittleEndian(target, (int)code);
                    break;
                default:
                    throw new NotSupportedException("仅支持 PCM 16/24/32 和 float32 监听输出。");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposeTask ??= StopAndReleaseAsync());
    }

    private async Task StopAndReleaseAsync()
    {
        _stop.Cancel();
        try { await _done.Task.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
