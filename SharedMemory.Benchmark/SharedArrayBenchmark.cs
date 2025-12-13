using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using SharedMemory;

namespace SharedMemory.Benchmark;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class SharedArrayBenchmark
{
    private SharedArray<int> _intArray = null!;
    private SharedArray<double> _doubleArray = null!;
    private SharedArray<long> _longArray = null!;
    private int[] _sourceInt = null!;
    private int[] _destInt = null!;

    [Params(100, 1000, 10000)]
    public int ArraySize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _intArray = new SharedArray<int>("Benchmark_IntArray", ArraySize);
        _doubleArray = new SharedArray<double>("Benchmark_DoubleArray", ArraySize);
        _longArray = new SharedArray<long>("Benchmark_LongArray", ArraySize);

        _sourceInt = new int[ArraySize];
        _destInt = new int[ArraySize];
        for (int i = 0; i < ArraySize; i++)
            _sourceInt[i] = i;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _intArray?.Dispose();
        _doubleArray?.Dispose();
        _longArray?.Dispose();
    }

    [Benchmark(Description = "Indexer Set (int)")]
    public void IndexerSet_Int()
    {
        for (int i = 0; i < ArraySize; i++)
            _intArray[i] = i;
    }

    [Benchmark(Description = "Indexer Get (int)")]
    public int IndexerGet_Int()
    {
        int sum = 0;
        for (int i = 0; i < ArraySize; i++)
            sum += _intArray[i];
        return sum;
    }

    [Benchmark(Description = "Indexer Set (double)")]
    public void IndexerSet_Double()
    {
        for (int i = 0; i < ArraySize; i++)
            _doubleArray[i] = i * 0.5;
    }

    [Benchmark(Description = "Indexer Get (double)")]
    public double IndexerGet_Double()
    {
        double sum = 0;
        for (int i = 0; i < ArraySize; i++)
            sum += _doubleArray[i];
        return sum;
    }

    [Benchmark(Description = "Indexer Set (long)")]
    public void IndexerSet_Long()
    {
        for (int i = 0; i < ArraySize; i++)
            _longArray[i] = i * 100L;
    }

    [Benchmark(Description = "Indexer Get (long)")]
    public long IndexerGet_Long()
    {
        long sum = 0;
        for (int i = 0; i < ArraySize; i++)
            sum += _longArray[i];
        return sum;
    }

    [Benchmark(Description = "CopyFrom (bulk write)")]
    public void CopyFrom()
    {
        _intArray.CopyFrom(0, _sourceInt);
    }

    [Benchmark(Description = "Fill (single value)")]
    public void Fill()
    {
        _intArray.Fill(42);
    }

    [Benchmark(Description = "CopyTo (bulk read)")]
    public void CopyTo()
    {
        _intArray.CopyTo(0, _destInt);
    }

    [Benchmark(Description = "Clear")]
    public void Clear()
    {
        _intArray.Clear();
    }
}

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class SharedArrayVsNativeArrayBenchmark
{
    private SharedArray<int> _sharedArray = null!;
    private int[] _nativeArray = null!;
    private const int Size = 10000;

    [GlobalSetup]
    public void Setup()
    {
        _sharedArray = new SharedArray<int>("Benchmark_Comparison", Size);
        _nativeArray = new int[Size];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sharedArray?.Dispose();
    }

    [Benchmark(Description = "SharedArray Sequential Write", Baseline = true)]
    public void SharedArray_Write()
    {
        for (int i = 0; i < Size; i++)
            _sharedArray[i] = i;
    }

    [Benchmark(Description = "Native Array Sequential Write")]
    public void NativeArray_Write()
    {
        for (int i = 0; i < Size; i++)
            _nativeArray[i] = i;
    }

    [Benchmark(Description = "SharedArray Sequential Read")]
    public int SharedArray_Read()
    {
        int sum = 0;
        for (int i = 0; i < Size; i++)
            sum += _sharedArray[i];
        return sum;
    }

    [Benchmark(Description = "Native Array Sequential Read")]
    public int NativeArray_Read()
    {
        int sum = 0;
        for (int i = 0; i < Size; i++)
            sum += _nativeArray[i];
        return sum;
    }
}
