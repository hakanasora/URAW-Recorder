using System.Buffers;
using System.Threading.Channels;

namespace PortableRecorder.Audio;

internal sealed class RecordingWriteQueue(AudioFormat format,string overloadMessage)
{
    private enum AdmissionPhase { Open, Draining, Faulted }
    internal delegate Exception? PrepareBlock(Span<float> samples);
    internal readonly record struct Block(float[] Data,int Count);
    private const int Capacity=512;
    private readonly Channel<Block> _queue=Channel.CreateBounded<Block>(new BoundedChannelOptions(Capacity)
        {SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly object _admission=new();
    private readonly long _budget=Math.Min(64L*1024*1024,format.SampleRate*(long)format.Channels*sizeof(float)*2);
    private AdmissionPhase _phase=AdmissionPhase.Open;
    private long _queuedBytes;
    private Exception? _failure;

    public Exception? Failure=>Volatile.Read(ref _failure);

    public bool TryWrite(ReadOnlySpan<float> samples,PrepareBlock? prepare=null)
    {
        // Completion and every producer share the same admission boundary. Sink I/O runs outside it.
        lock(_admission)
        {
            if(_phase!=AdmissionPhase.Open)return false;
            long bytes=samples.Length*(long)sizeof(float);
            if(Interlocked.Read(ref _queuedBytes)+bytes>_budget || _queue.Reader.Count==Capacity)
            {
                Reject(new IOException(overloadMessage));
                return false;
            }

            var data=ArrayPool<float>.Shared.Rent(samples.Length);
            samples.CopyTo(data);
            if(prepare?.Invoke(data.AsSpan(0,samples.Length)) is Exception error)
            {
                ArrayPool<float>.Shared.Return(data);
                Reject(error);
                return false;
            }
            Interlocked.Add(ref _queuedBytes,bytes);
            // Only the reader can change the available capacity until this lock is released.
            if(_queue.Writer.TryWrite(new(data,samples.Length)))return true;
            Release(new(data,samples.Length));
            Reject(new IOException(overloadMessage));
            return false;
        }
    }

    public void Fault(Exception error)
    {
        lock(_admission)
            Reject(error);
    }

    public void CloseInput()
    {
        lock(_admission)
        {
            if(_phase!=AdmissionPhase.Open)return;
            _phase=AdmissionPhase.Draining;
            _queue.Writer.TryComplete();
        }
    }

    private void Reject(Exception error)
    {
        Volatile.Write(ref _failure,AudioFailure.Combine(_failure,error));
        if(_phase==AdmissionPhase.Faulted)return;
        _phase=AdmissionPhase.Faulted;
        _queue.Writer.TryComplete();
    }

    public IAsyncEnumerable<Block> ReadAllAsync()=>_queue.Reader.ReadAllAsync();

    public void DiscardPending()
    {
        while(_queue.Reader.TryRead(out var block))Release(block);
    }

    public void Release(Block block)
    {
        Interlocked.Add(ref _queuedBytes,-block.Count*(long)sizeof(float));
        ArrayPool<float>.Shared.Return(block.Data);
    }
}
