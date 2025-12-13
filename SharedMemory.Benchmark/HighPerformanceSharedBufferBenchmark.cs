using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using SharedMemory;

namespace SharedMemory.Benchmark;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class HighPerformanceSharedBufferBenchmark
{
    private HighPerformanceSharedBuffer _buffer = null!;
    private byte[] _smallData = null!;
    private byte[] _mediumData = null!;
    private byte[] _largeData = null!;
    private byte[] _readBuffer = null!;

    [Params(64, 1024, 64 * 1024, 1024 * 1024)]
    public int DataSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 16 * 1024 * 1024, // 16MB
            EnableSimd = true
        };
        _buffer = new HighPerformanceSharedBuffer("Benchmark_Buffer", options);

        _smallData = new byte[64];
        _mediumData = new byte[1024];
        _largeData = new byte[DataSize];
        _readBuffer = new byte[DataSize];

        Random.Shared.NextBytes(_smallData);
        Random.Shared.NextBytes(_mediumData);
        Random.Shared.NextBytes(_largeData);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _buffer?.Dispose();
    }

    [Benchmark(Description = "Write")]
    public void Write()
    {
        _buffer.Write(_largeData, 0);
    }

    [Benchmark(Description = "Read")]
    public void Read()
    {
        _buffer.Read(_readBuffer, 0);
    }

    [Benchmark(Description = "Write+Read Round-trip")]
    public void WriteReadRoundTrip()
    {
        _buffer.Write(_largeData, 0);
        _buffer.Read(_readBuffer, 0);
    }

    [Benchmark(Description = "Write with Lock")]
    public void WriteWithLock()
    {
        if (_buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)))
        {
            try
            {
                _buffer.Write(_largeData, 0);
            }
            finally
            {
                _buffer.ReleaseWriteLock();
            }
        }
    }

    [Benchmark(Description = "Read with Lock")]
    public void ReadWithLock()
    {
        if (_buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)))
        {
            try
            {
                _buffer.Read(_readBuffer, 0);
            }
            finally
            {
                _buffer.ReleaseReadLock();
            }
        }
    }
}

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class HighPerformanceSharedBufferThroughputBenchmark
{
    private HighPerformanceSharedBuffer _buffer = null!;
    private byte[] _data = null!;
    private byte[] _readBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 64 * 1024 * 1024, // 64MB
            EnableSimd = true
        };
        _buffer = new HighPerformanceSharedBuffer("Benchmark_Throughput", options);
        _data = new byte[1024 * 1024]; // 1MB
        _readBuffer = new byte[1024 * 1024];
        Random.Shared.NextBytes(_data);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _buffer?.Dispose();
    }

    [Benchmark(Description = "1MB Write x100 (Throughput)")]
    public void Throughput_Write_1MB_x100()
    {
        for (int i = 0; i < 100; i++)
        {
            _buffer.Write(_data, 0);
        }
    }

    [Benchmark(Description = "1MB Read x100 (Throughput)")]
    public void Throughput_Read_1MB_x100()
    {
        for (int i = 0; i < 100; i++)
        {
            _buffer.Read(_readBuffer, 0);
        }
    }
}
