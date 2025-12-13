using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using SharedMemory;

namespace SharedMemory.Benchmark;

/// <summary>
/// SPSC vs MPMC 순환 버퍼 비교
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class SpscVsMpmcBenchmark
{
    private LockFreeCircularBuffer _spsc = null!;
    private MpmcCircularBuffer _mpmc = null!;
    private byte[] _message = null!;
    private byte[] _readBuffer = null!;

    [Params(64, 256, 1024)]
    public int MessageSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _spsc = new LockFreeCircularBuffer("Bench_SPSC", 16 * 1024 * 1024);
        _mpmc = new MpmcCircularBuffer("Bench_MPMC", slotCount: 1024, slotSize: 1024 + 16);
        _message = new byte[MessageSize];
        _readBuffer = new byte[1024];
        Random.Shared.NextBytes(_message);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _spsc?.Dispose();
        _mpmc?.Dispose();
    }

    [Benchmark(Description = "SPSC Write", Baseline = true)]
    public bool SPSC_Write()
    {
        return _spsc.TryWrite(_message);
    }

    [Benchmark(Description = "MPMC Write")]
    public bool MPMC_Write()
    {
        return _mpmc.TryWrite(_message);
    }

    [Benchmark(Description = "SPSC Write+Read")]
    public int SPSC_WriteRead()
    {
        _spsc.TryWrite(_message);
        return _spsc.TryRead(_readBuffer);
    }

    [Benchmark(Description = "MPMC Write+Read")]
    public int MPMC_WriteRead()
    {
        _mpmc.TryWrite(_message);
        return _mpmc.TryRead(_readBuffer);
    }
}

/// <summary>
/// Lock 오버헤드 측정
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class LockOverheadBenchmark
{
    private HighPerformanceSharedBuffer _buffer = null!;
    private byte[] _data = null!;
    private byte[] _readBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 1024 * 1024,
            EnableSimd = true
        };
        _buffer = new HighPerformanceSharedBuffer("Bench_Lock", options);
        _data = new byte[1024];
        _readBuffer = new byte[1024];
        Random.Shared.NextBytes(_data);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _buffer?.Dispose();
    }

    [Benchmark(Description = "Write (No Lock)", Baseline = true)]
    public int Write_NoLock()
    {
        return _buffer.Write(_data, 0);
    }

    [Benchmark(Description = "Write (With Lock)")]
    public int Write_WithLock()
    {
        if (_buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)))
        {
            try
            {
                return _buffer.Write(_data, 0);
            }
            finally
            {
                _buffer.ReleaseWriteLock();
            }
        }
        return 0;
    }

    [Benchmark(Description = "Read (No Lock)")]
    public int Read_NoLock()
    {
        return _buffer.Read(_readBuffer, 0);
    }

    [Benchmark(Description = "Read (With Lock)")]
    public int Read_WithLock()
    {
        if (_buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)))
        {
            try
            {
                return _buffer.Read(_readBuffer, 0);
            }
            finally
            {
                _buffer.ReleaseReadLock();
            }
        }
        return 0;
    }
}

/// <summary>
/// 데이터 크기별 처리량 (GB/s)
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class ThroughputByDataSizeBenchmark
{
    private HighPerformanceSharedBuffer _buffer = null!;
    private byte[] _data = null!;
    private byte[] _readBuffer = null!;

    [Params(64, 256, 1024, 4096, 16384, 65536, 262144, 1048576)]
    public int DataSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 64 * 1024 * 1024,
            EnableSimd = true
        };
        _buffer = new HighPerformanceSharedBuffer("Bench_Throughput", options);
        _data = new byte[DataSize];
        _readBuffer = new byte[DataSize];
        Random.Shared.NextBytes(_data);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _buffer?.Dispose();
    }

    [Benchmark(Description = "Write")]
    public int Write()
    {
        return _buffer.Write(_data, 0);
    }

    [Benchmark(Description = "Read")]
    public int Read()
    {
        return _buffer.Read(_readBuffer, 0);
    }
}

/// <summary>
/// SIMD 활성화 vs 비활성화
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class SimdComparisonBenchmark
{
    private HighPerformanceSharedBuffer _simdEnabled = null!;
    private HighPerformanceSharedBuffer _simdDisabled = null!;
    private byte[] _data = null!;
    private byte[] _readBuffer = null!;

    [Params(1024, 65536, 1048576)]
    public int DataSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var simdOptions = new SharedMemoryBufferOptions
        {
            Capacity = 64 * 1024 * 1024,
            EnableSimd = true
        };
        var noSimdOptions = new SharedMemoryBufferOptions
        {
            Capacity = 64 * 1024 * 1024,
            EnableSimd = false
        };

        _simdEnabled = new HighPerformanceSharedBuffer("Bench_SIMD", simdOptions);
        _simdDisabled = new HighPerformanceSharedBuffer("Bench_NoSIMD", noSimdOptions);
        _data = new byte[DataSize];
        _readBuffer = new byte[DataSize];
        Random.Shared.NextBytes(_data);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _simdEnabled?.Dispose();
        _simdDisabled?.Dispose();
    }

    [Benchmark(Description = "SIMD Enabled Write", Baseline = true)]
    public int Simd_Write()
    {
        return _simdEnabled.Write(_data, 0);
    }

    [Benchmark(Description = "SIMD Disabled Write")]
    public int NoSimd_Write()
    {
        return _simdDisabled.Write(_data, 0);
    }

    [Benchmark(Description = "SIMD Enabled Read")]
    public int Simd_Read()
    {
        return _simdEnabled.Read(_readBuffer, 0);
    }

    [Benchmark(Description = "SIMD Disabled Read")]
    public int NoSimd_Read()
    {
        return _simdDisabled.Read(_readBuffer, 0);
    }
}

/// <summary>
/// CRC32 체크섬 오버헤드
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class ChecksumOverheadBenchmark
{
    private HighPerformanceSharedBuffer _buffer = null!;
    private byte[] _data = null!;

    [Params(1024, 65536, 1048576)]
    public int DataSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 64 * 1024 * 1024,
            EnableSimd = true
        };
        _buffer = new HighPerformanceSharedBuffer("Bench_Checksum", options);
        _data = new byte[DataSize];
        Random.Shared.NextBytes(_data);
        _buffer.Write(_data, 0);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _buffer?.Dispose();
    }

    [Benchmark(Description = "Write Only", Baseline = true)]
    public int Write()
    {
        return _buffer.Write(_data, 0);
    }

    [Benchmark(Description = "Write + UpdateChecksum")]
    public void Write_WithChecksum()
    {
        _buffer.Write(_data, 0);
        _buffer.UpdateChecksum(0, DataSize);
    }

    [Benchmark(Description = "CalculateChecksum")]
    public uint CalculateChecksum()
    {
        return _buffer.CalculateChecksum(0, DataSize);
    }

    [Benchmark(Description = "VerifyIntegrity")]
    public bool VerifyIntegrity()
    {
        return _buffer.VerifyIntegrity();
    }
}

/// <summary>
/// 멀티스레드 경쟁 상황 (Contention)
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class ContentionBenchmark
{
    private MpmcCircularBuffer _buffer = null!;
    private byte[] _message = null!;

    [Params(1, 2, 4, 8)]
    public int ThreadCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new MpmcCircularBuffer("Bench_Contention", slotCount: 4096, slotSize: 128);
        _message = new byte[64];
        Random.Shared.NextBytes(_message);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _buffer?.Dispose();
    }

    [Benchmark(Description = "Parallel Write 10K")]
    public void ParallelWrite()
    {
        const int totalMessages = 10000;
        int messagesPerThread = totalMessages / ThreadCount;

        Parallel.For(0, ThreadCount, _ =>
        {
            for (int i = 0; i < messagesPerThread; i++)
            {
                while (!_buffer.TryWrite(_message))
                    Thread.SpinWait(1);
            }
        });

        // Drain
        var buf = new byte[128];
        while (_buffer.TryRead(buf) > 0)
        { }
    }

    [Benchmark(Description = "Parallel Read 10K")]
    public void ParallelRead()
    {
        const int totalMessages = 10000;

        // Fill
        for (int i = 0; i < totalMessages; i++)
            _buffer.TryWrite(_message);

        int messagesPerThread = totalMessages / ThreadCount;
        Parallel.For(0, ThreadCount, _ =>
        {
            var buf = new byte[128];
            for (int i = 0; i < messagesPerThread; i++)
            {
                while (_buffer.TryRead(buf) == 0)
                    Thread.SpinWait(1);
            }
        });
    }
}
