using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using SharedMemory;

namespace SharedMemory.Benchmark;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class LockFreeCircularBufferBenchmark
{
    private LockFreeCircularBuffer _buffer = null!;
    private byte[] _smallMessage = null!;
    private byte[] _mediumMessage = null!;
    private byte[] _largeMessage = null!;
    private byte[] _readBuffer = null!;

    [Params(64, 256, 1024, 4096)]
    public int MessageSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new LockFreeCircularBuffer("Benchmark_Circular", 16 * 1024 * 1024);
        _smallMessage = new byte[64];
        _mediumMessage = new byte[256];
        _largeMessage = new byte[MessageSize];
        _readBuffer = new byte[MessageSize];

        Random.Shared.NextBytes(_smallMessage);
        Random.Shared.NextBytes(_mediumMessage);
        Random.Shared.NextBytes(_largeMessage);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _buffer?.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Clear the buffer before each iteration
        while (_buffer.TryRead(new byte[4096]) > 0)
        { }
    }

    [Benchmark(Description = "TryWrite Single Message")]
    public bool TryWrite()
    {
        return _buffer.TryWrite(_largeMessage);
    }

    [Benchmark(Description = "TryWrite + TryRead Round-trip")]
    public int WriteReadRoundTrip()
    {
        _buffer.TryWrite(_largeMessage);
        return _buffer.TryRead(_readBuffer);
    }

    [Benchmark(Description = "Batch Write x100")]
    public int BatchWrite()
    {
        int written = 0;
        for (int i = 0; i < 100; i++)
        {
            if (_buffer.TryWrite(_largeMessage))
                written++;
        }
        return written;
    }
}

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class LockFreeCircularBufferProducerConsumerBenchmark
{
    private LockFreeCircularBuffer _buffer = null!;
    private byte[] _message = null!;
    private byte[] _readBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new LockFreeCircularBuffer("Benchmark_SPSC", 64 * 1024 * 1024);
        _message = new byte[64];
        _readBuffer = new byte[64];
        Random.Shared.NextBytes(_message);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _buffer?.Dispose();
    }

    [Benchmark(Description = "SPSC 10K messages")]
    public void SPSC_10K_Messages()
    {
        const int messageCount = 10000;
        var producerDone = false;

        var producerTask = Task.Run(() =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                while (!_buffer.TryWrite(_message))
                {
                    Thread.SpinWait(1);
                }
            }
            producerDone = true;
        });

        var consumerTask = Task.Run(() =>
        {
            int consumed = 0;
            while (consumed < messageCount)
            {
                if (_buffer.TryRead(_readBuffer) > 0)
                    consumed++;
                else if (!producerDone)
                    Thread.SpinWait(1);
            }
        });

        Task.WaitAll(producerTask, consumerTask);
    }
}
