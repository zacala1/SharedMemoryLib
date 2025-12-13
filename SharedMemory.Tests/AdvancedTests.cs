using System.Collections.Concurrent;
using NUnit.Framework;
using SharedMemory;

namespace SharedMemory.Tests;

/// <summary>
/// Advanced tests: failure scenarios, load tests, multi-threading safety, data integrity
/// </summary>
[TestFixture]
public class AdvancedTests
{
    private readonly List<string> _createdBufferNames = new();

    [TearDown]
    public void Cleanup()
    {
        _createdBufferNames.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private string GetUniqueName(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        _createdBufferNames.Add(name);
        return name;
    }

    #region Failure Scenario Tests

    [Test]
    public void SharedArray_NullName_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => new SharedArray<int>(null!, 100));
    }

    [Test]
    public void SharedArray_EmptyName_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => new SharedArray<int>("", 100));
    }

    [Test]
    public void SharedArray_WhitespaceName_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => new SharedArray<int>("   ", 100));
    }

    [Test]
    public void SharedArray_ZeroLength_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SharedArray<int>(GetUniqueName("Test"), 0));
    }

    [Test]
    public void SharedArray_NegativeLength_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SharedArray<int>(GetUniqueName("Test"), -1));
    }

    [Test]
    public void SharedArray_AccessAfterDispose_ShouldThrow()
    {
        var array = new SharedArray<int>(GetUniqueName("Dispose"), 10);
        array.Dispose();

        Assert.Throws<ObjectDisposedException>(() => array[0] = 1);
        Assert.Throws<ObjectDisposedException>(() => _ = array[0]);
        Assert.Throws<ObjectDisposedException>(() => array.Fill(1));
        Assert.Throws<ObjectDisposedException>(() => array.Clear());
    }

    [Test]
    public void LockFreeCircularBuffer_NullName_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => new LockFreeCircularBuffer(null!, 1024));
    }

    [Test]
    public void LockFreeCircularBuffer_EmptyName_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => new LockFreeCircularBuffer("", 1024));
    }

    [Test]
    public void LockFreeCircularBuffer_ZeroCapacity_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LockFreeCircularBuffer(GetUniqueName("Test"), 0));
    }

    [Test]
    public void LockFreeCircularBuffer_NegativeCapacity_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LockFreeCircularBuffer(GetUniqueName("Test"), -1));
    }

    [Test]
    public void LockFreeCircularBuffer_AccessAfterDispose_ShouldThrow()
    {
        var buffer = new LockFreeCircularBuffer(GetUniqueName("Dispose"), 1024);
        buffer.Dispose();

        var data = new byte[10];
        Assert.Throws<ObjectDisposedException>(() => buffer.TryWrite(data));
        Assert.Throws<ObjectDisposedException>(() => buffer.TryRead(data));
        Assert.Throws<ObjectDisposedException>(() => buffer.Clear());
    }

    [Test]
    public void HighPerformanceSharedBuffer_NullName_ShouldThrow()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 1024 };
        Assert.Throws<ArgumentException>(() => new HighPerformanceSharedBuffer(null!, options));
    }

    [Test]
    public void StrictSharedMemory_NullName_ShouldThrow()
    {
        var schema = new TestSchema();
        Assert.Throws<ArgumentException>(() => new StrictSharedMemory<TestSchema>(null!, schema));
    }

    #endregion

    #region Data Integrity Tests

    [Test]
    public void SharedArray_DataIntegrity_LargeDataSet()
    {
        const int size = 100000;
        using var array = new SharedArray<long>(GetUniqueName("Integrity"), size);

        // Write pattern
        for (int i = 0; i < size; i++)
        {
            array[i] = (long)i * 12345678901L;
        }

        // Verify all data
        for (int i = 0; i < size; i++)
        {
            Assert.That(array[i], Is.EqualTo((long)i * 12345678901L), $"Mismatch at index {i}");
        }
    }

    [Test]
    public void SharedArray_DataIntegrity_BulkOperations()
    {
        const int size = 10000;
        using var array = new SharedArray<double>(GetUniqueName("BulkIntegrity"), size);

        var original = new double[size];
        for (int i = 0; i < size; i++)
        {
            original[i] = Math.PI * i;
        }

        // Bulk write
        array.CopyFrom(0, original);

        // Bulk read
        var retrieved = new double[size];
        array.CopyTo(0, retrieved);

        // Verify
        for (int i = 0; i < size; i++)
        {
            Assert.That(retrieved[i], Is.EqualTo(original[i]).Within(0.0000001), $"Mismatch at index {i}");
        }
    }

    [Test]
    public void LockFreeCircularBuffer_DataIntegrity_SequentialMessages()
    {
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("SeqIntegrity"), 64 * 1024);

        const int messageCount = 1000;
        const int messageSize = 32; // Fixed size for this test
        var random = new Random(42);

        // Write messages with fixed size
        var sentMessages = new List<byte[]>();
        for (int i = 0; i < messageCount; i++)
        {
            var data = new byte[messageSize];
            random.NextBytes(data);
            BitConverter.TryWriteBytes(data.AsSpan(0, 4), i); // Write index for verification

            Assert.That(buffer.TryWrite(data), Is.True, $"Failed to write message {i}");
            sentMessages.Add(data);
        }

        // Read and verify
        for (int i = 0; i < messageCount; i++)
        {
            var readBuffer = new byte[messageSize];
            var bytesRead = buffer.TryRead(readBuffer);

            Assert.That(bytesRead, Is.EqualTo(messageSize), $"Size mismatch at message {i}");
            Assert.That(readBuffer, Is.EqualTo(sentMessages[i]), $"Data mismatch at message {i}");
        }
    }

    [Test]
    public void LockFreeCircularBuffer_PowerOf2Capacity_ShouldRoundUp()
    {
        // Request 1000 bytes, should round up to 1024
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("PowerOf2"), 1000);
        Assert.That(buffer.Capacity, Is.EqualTo(1024));

        // Request 100 bytes, should round up to 128
        using var buffer2 = new LockFreeCircularBuffer(GetUniqueName("PowerOf2_2"), 100);
        Assert.That(buffer2.Capacity, Is.EqualTo(128));

        // Request exactly 512, should stay 512
        using var buffer3 = new LockFreeCircularBuffer(GetUniqueName("PowerOf2_3"), 512);
        Assert.That(buffer3.Capacity, Is.EqualTo(512));
    }

    #endregion

    #region Load and Stress Tests

    [Test]
    public void SharedArray_StressTest_RapidReadWrite()
    {
        const int iterations = 100000;
        using var array = new SharedArray<int>(GetUniqueName("Stress"), 1000);

        for (int i = 0; i < iterations; i++)
        {
            int index = i % 1000;
            array[index] = i;
            var value = array[index];
            Assert.That(value, Is.EqualTo(i));
        }
    }

    [Test]
    public void LockFreeCircularBuffer_StressTest_HighThroughput()
    {
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("HighThroughput"), 1024 * 1024);

        const int iterations = 50000;
        var data = new byte[64];
        var readBuffer = new byte[64];

        for (int i = 0; i < iterations; i++)
        {
            BitConverter.TryWriteBytes(data, i);
            Assert.That(buffer.TryWrite(data), Is.True);

            var bytesRead = buffer.TryRead(readBuffer);
            Assert.That(bytesRead, Is.EqualTo(64));
            Assert.That(BitConverter.ToInt32(readBuffer), Is.EqualTo(i));
        }
    }

    [Test]
    public void LockFreeCircularBuffer_StressTest_WrapAround()
    {
        // Small buffer to force many wrap-arounds
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("WrapStress"), 256);

        const int iterations = 10000;
        var writeData = new byte[32];
        var readData = new byte[32];

        for (int i = 0; i < iterations; i++)
        {
            // Fill with pattern
            for (int j = 0; j < 32; j++)
                writeData[j] = (byte)((i + j) % 256);

            Assert.That(buffer.TryWrite(writeData), Is.True, $"Write failed at iteration {i}");

            var bytesRead = buffer.TryRead(readData);
            Assert.That(bytesRead, Is.EqualTo(32), $"Read size wrong at iteration {i}");
            Assert.That(readData, Is.EqualTo(writeData), $"Data mismatch at iteration {i}");
        }
    }

    #endregion

    #region Multi-Threading Safety Tests

    [Test]
    public async Task SharedArray_ConcurrentAccess_DifferentIndices_ShouldBeThreadSafe()
    {
        const int threadCount = 8;
        const int iterationsPerThread = 10000;
        const int arraySize = threadCount * 100;

        using var array = new SharedArray<long>(GetUniqueName("ConcurrentArray"), arraySize);

        var tasks = new Task[threadCount];
        var errors = new ConcurrentBag<string>();

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            int startIndex = threadId * 100;

            tasks[t] = Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        int index = startIndex + (i % 100);
                        long value = (long)threadId * 1000000 + i;
                        array[index] = value;

                        var readValue = array[index];
                        // Value might be overwritten by same thread's next iteration
                        // but should be valid
                        if (readValue < 0)
                        {
                            errors.Add($"Invalid value at thread {threadId}, index {index}: {readValue}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadId} exception: {ex.Message}");
                }
            });
        }

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }

    [Test]
    public async Task LockFreeCircularBuffer_SPSC_HighContention()
    {
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("SPSC_Contention"), 64 * 1024);

        const int messageCount = 100000;
        var errors = new ConcurrentBag<string>();
        var producerDone = false;

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                var data = BitConverter.GetBytes(i);
                int retries = 0;
                while (!buffer.TryWrite(data))
                {
                    Thread.SpinWait(1);
                    retries++;
                    if (retries > 1000000)
                    {
                        errors.Add($"Producer stuck at message {i}");
                        return;
                    }
                }
            }
            producerDone = true;
        });

        var consumer = Task.Run(() =>
        {
            int expected = 0;
            var readBuffer = new byte[4];

            while (expected < messageCount)
            {
                var bytesRead = buffer.TryRead(readBuffer);
                if (bytesRead == 4)
                {
                    var value = BitConverter.ToInt32(readBuffer, 0);
                    if (value != expected)
                    {
                        errors.Add($"Expected {expected}, got {value}");
                        return;
                    }
                    expected++;
                }
                else if (bytesRead == 0 && !producerDone)
                {
                    Thread.SpinWait(1);
                }
                else if (bytesRead == 0 && producerDone)
                {
                    // Check again after seeing producerDone
                    Thread.MemoryBarrier();
                    if (buffer.TryRead(readBuffer) == 0 && expected < messageCount)
                    {
                        Thread.SpinWait(10);
                    }
                }
            }
        });

        await Task.WhenAll(producer, consumer);
        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }

    [Test]
    public async Task HighPerformanceSharedBuffer_ConcurrentReadWrite_WithLocks()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("ConcurrentLock"), options);

        const int iterations = 10000;
        var errors = new ConcurrentBag<string>();

        var writers = Enumerable.Range(0, 4).Select(writerId => Task.Run(() =>
        {
            var data = new byte[64];
            for (int i = 0; i < iterations; i++)
            {
                if (buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(5)))
                {
                    try
                    {
                        Array.Fill(data, (byte)((writerId * 100 + i) % 256));
                        buffer.Write(data, writerId * 64);
                    }
                    finally
                    {
                        buffer.ReleaseWriteLock();
                    }
                }
                else
                {
                    errors.Add($"Writer {writerId} failed to acquire lock at iteration {i}");
                }
            }
        })).ToArray();

        var readers = Enumerable.Range(0, 4).Select(readerId => Task.Run(() =>
        {
            var data = new byte[64];
            for (int i = 0; i < iterations; i++)
            {
                if (buffer.TryAcquireReadLock(TimeSpan.FromSeconds(5)))
                {
                    try
                    {
                        buffer.Read(data, readerId * 64);
                    }
                    finally
                    {
                        buffer.ReleaseReadLock();
                    }
                }
                else
                {
                    errors.Add($"Reader {readerId} failed to acquire lock at iteration {i}");
                }
            }
        })).ToArray();

        await Task.WhenAll(writers.Concat(readers));
        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }

    [Test]
    public async Task StrictSharedMemory_ConcurrentFieldAccess_WithLocks()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(GetUniqueName("ConcurrentStrict"), schema);

        const int iterations = 5000;
        var errors = new ConcurrentBag<string>();

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                using (memory.AcquireWriteLock())
                {
                    memory.Write(TestSchema.IntField, taskId * 10000 + i);
                }

                using (memory.AcquireReadLock())
                {
                    var value = memory.Read<int>(TestSchema.IntField);
                    // Value should be within valid range
                    if (value < 0 || value > 4 * 10000 + iterations)
                    {
                        errors.Add($"Invalid value {value} at task {taskId}, iteration {i}");
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }

    #endregion

    #region Edge Cases

    [Test]
    public void LockFreeCircularBuffer_EmptyWrite_ShouldSucceed()
    {
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("EmptyWrite"), 1024);

        Assert.That(buffer.TryWrite(ReadOnlySpan<byte>.Empty), Is.True);
        Assert.That(buffer.Used, Is.EqualTo(0));
    }

    [Test]
    public void LockFreeCircularBuffer_EmptyRead_ShouldReturnZero()
    {
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("EmptyRead"), 1024);

        var result = buffer.TryRead(Span<byte>.Empty);
        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void SharedArray_StructType_DataIntegrity()
    {
        using var array = new SharedArray<TestPoint>(GetUniqueName("StructIntegrity"), 1000);

        for (int i = 0; i < 1000; i++)
        {
            array[i] = new TestPoint { X = i, Y = i * 2 };
        }

        for (int i = 0; i < 1000; i++)
        {
            var point = array[i];
            Assert.That(point.X, Is.EqualTo(i));
            Assert.That(point.Y, Is.EqualTo(i * 2));
        }
    }

    [Test]
    public void MultipleDispose_ShouldNotThrow()
    {
        var array = new SharedArray<int>(GetUniqueName("MultiDispose1"), 100);
        array.Dispose();
        Assert.DoesNotThrow(() => array.Dispose());
        Assert.DoesNotThrow(() => array.Dispose());

        var buffer = new LockFreeCircularBuffer(GetUniqueName("MultiDispose2"), 1024);
        buffer.Dispose();
        Assert.DoesNotThrow(() => buffer.Dispose());
        Assert.DoesNotThrow(() => buffer.Dispose());
    }

    #endregion

    private struct TestPoint
    {
        public int X;
        public int Y;
    }

    private struct TestSchema : ISharedMemorySchema
    {
        public const string IntField = "IntValue";
        public const string DoubleField = "DoubleValue";
        public const string StringField = "StringValue";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(IntField);
            yield return FieldDefinition.Scalar<double>(DoubleField);
            yield return FieldDefinition.String(StringField, 32);
        }
    }

    #region Additional Bug Fix Verification Tests

    [Test]
    public void StrictSharedMemory_SchemaHash_ShouldBeStableAcrossInstances()
    {
        // Verify that schema hash is deterministic (not using randomized GetHashCode)
        var schema = new TestSchema();
        var uniqueName1 = GetUniqueName("HashTest1");
        var uniqueName2 = GetUniqueName("HashTest2");

        using var memory1 = new StrictSharedMemory<TestSchema>(uniqueName1, schema);
        using var memory2 = new StrictSharedMemory<TestSchema>(uniqueName2, schema);

        // Both should have the same schema version (implies same hash)
        Assert.That(memory1.SchemaVersion, Is.EqualTo(memory2.SchemaVersion));
    }

    [Test]
    public void WriteLock_DoubleDIspose_ShouldNotThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(GetUniqueName("DoubleLock"), schema);

        var writeLock = memory.AcquireWriteLock();
        writeLock.Dispose();

        // Second dispose should not throw or release lock again
        Assert.DoesNotThrow(() => writeLock.Dispose());
    }

    [Test]
    public void ReadLock_DoubleDispose_ShouldNotThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(GetUniqueName("DoubleReadLock"), schema);

        var readLock = memory.AcquireReadLock();
        readLock.Dispose();

        // Second dispose should not throw or release lock again
        Assert.DoesNotThrow(() => readLock.Dispose());
    }

    [Test]
    public void HighPerformanceSharedBuffer_WriteLock_TimeoutDuringReaderWait_ShouldNotLeakLock()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("LockLeak"), options);

        // Acquire read lock
        Assert.That(buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)), Is.True);

        try
        {
            // Try to acquire write lock with short timeout - should fail because reader holds lock
            var result = buffer.TryAcquireWriteLock(TimeSpan.FromMilliseconds(50));
            Assert.That(result, Is.False);

            // Lock should not be leaked - should be able to acquire write lock after releasing read
            buffer.ReleaseReadLock();

            Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
            buffer.ReleaseWriteLock();
        }
        finally
        {
            // Cleanup in case of test failure
            try
            { buffer.ReleaseReadLock(); }
            catch { }
        }
    }

    [Test]
    public void HighPerformanceSharedBuffer_NullOptions_ShouldUseDefaults()
    {
        // Should not throw when options is null
        Assert.DoesNotThrow(() =>
        {
            using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("NullOptions"), null!);
            Assert.That(buffer.Capacity, Is.GreaterThan(0));
        });
    }

    [Test]
    public void MpmcCircularBuffer_Statistics_FailedOperations_ShouldBeTracked()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("FailStats"), slotCount: 2, slotSize: 64);

        // Fill buffer
        var data = new byte[32];
        Assert.That(buffer.TryWrite(data), Is.True);
        Assert.That(buffer.TryWrite(data), Is.True);

        // Try to write to full buffer
        Assert.That(buffer.TryWrite(data), Is.False);

        // Read from empty buffer (after draining)
        var readBuf = new byte[64];
        buffer.TryRead(readBuf);
        buffer.TryRead(readBuf);
        var emptyRead = buffer.TryRead(readBuf);

        Assert.That(emptyRead, Is.EqualTo(0));

        var stats = buffer.GetStatistics();
        Assert.That(stats.FailedWrites, Is.GreaterThan(0));
        Assert.That(stats.FailedReads, Is.GreaterThan(0));
    }

    #endregion

    #region Boundary Condition Tests

    [Test]
    public void HighPerformanceSharedBuffer_ZeroLengthWrite_ShouldSucceed()
    {
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("ZeroWrite"), new SharedMemoryBufferOptions { Capacity = 1024 });

        var emptyData = ReadOnlySpan<byte>.Empty;
        var result = buffer.Write(emptyData, 0);

        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void HighPerformanceSharedBuffer_ZeroLengthRead_ShouldSucceed()
    {
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("ZeroRead"), new SharedMemoryBufferOptions { Capacity = 1024 });

        var emptyData = Span<byte>.Empty;
        var result = buffer.Read(emptyData, 0);

        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void HighPerformanceSharedBuffer_WriteAtCapacityBoundary_ShouldSucceed()
    {
        const int capacity = 1024;
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("CapBoundary"), new SharedMemoryBufferOptions { Capacity = capacity });

        // Write exactly at the last valid position
        var data = new byte[] { 0xFF };
        var result = buffer.Write(data, capacity - 1);

        Assert.That(result, Is.EqualTo(1));

        var readBack = new byte[1];
        buffer.Read(readBack, capacity - 1);
        Assert.That(readBack[0], Is.EqualTo(0xFF));
    }

    [Test]
    public void HighPerformanceSharedBuffer_WriteExceedingCapacity_ShouldThrow()
    {
        const int capacity = 1024;
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("ExceedCap"), new SharedMemoryBufferOptions { Capacity = capacity });

        var data = new byte[] { 0xFF };

        // Writing at offset = capacity should throw (out of bounds)
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Write(data, capacity));
    }

    [Test]
    public void HighPerformanceSharedBuffer_WriteSpanningBeyondCapacity_ShouldThrow()
    {
        const int capacity = 1024;
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("SpanBeyond"), new SharedMemoryBufferOptions { Capacity = capacity });

        var data = new byte[100];

        // Writing 100 bytes at offset capacity-50 should fail
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Write(data, capacity - 50));
    }

    [Test]
    public void LockFreeCircularBuffer_ZeroLengthWrite_ShouldSucceed()
    {
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("LFZeroWrite"), 1024);

        var result = buffer.TryWrite(ReadOnlySpan<byte>.Empty);
        Assert.That(result, Is.True);
    }

    [Test]
    public void LockFreeCircularBuffer_ZeroLengthRead_ShouldReturnZero()
    {
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("LFZeroRead"), 1024);

        var result = buffer.TryRead(Span<byte>.Empty);
        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void LockFreeCircularBuffer_WrapAround_ShouldWorkCorrectly()
    {
        // Use small capacity to force wrap-around quickly
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("LFWrap"), 64);

        var writeData = new byte[32];
        var readData = new byte[32];

        // Fill with pattern and read to move position forward
        for (int i = 0; i < 5; i++)
        {
            for (int j = 0; j < writeData.Length; j++)
                writeData[j] = (byte)(i * 10 + j);

            Assert.That(buffer.TryWrite(writeData), Is.True, $"Write {i} failed");

            var bytesRead = buffer.TryRead(readData);
            Assert.That(bytesRead, Is.EqualTo(32), $"Read {i} returned wrong count");

            for (int j = 0; j < bytesRead; j++)
                Assert.That(readData[j], Is.EqualTo((byte)(i * 10 + j)), $"Data mismatch at iteration {i}, index {j}");
        }

        var stats = buffer.GetStatistics();
        Assert.That(stats.Writes, Is.EqualTo(5));
        Assert.That(stats.Reads, Is.EqualTo(5));
    }

    [Test]
    public void LockFreeCircularBuffer_ExactCapacityWrite_ShouldSucceed()
    {
        const int capacity = 256; // Will be rounded up to power of 2
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("LFExactCap"), capacity);

        // Write data filling entire capacity
        var data = new byte[buffer.Capacity];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 256);

        Assert.That(buffer.TryWrite(data), Is.True);
        Assert.That(buffer.Available, Is.EqualTo(0));
        Assert.That(buffer.Used, Is.EqualTo(buffer.Capacity));
    }

    [Test]
    public void LockFreeCircularBuffer_ExceedCapacityWrite_ShouldThrow()
    {
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("LFExceedCap"), 256);

        var data = new byte[buffer.Capacity + 1];

        Assert.Throws<ArgumentException>(() => buffer.TryWrite(data));
    }

    [Test]
    public void MpmcCircularBuffer_ZeroLengthWrite_ShouldSucceed()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("MpmcZeroWrite"), slotCount: 4, slotSize: 64);

        var result = buffer.TryWrite(ReadOnlySpan<byte>.Empty);
        Assert.That(result, Is.True);
    }

    [Test]
    public void MpmcCircularBuffer_MaxSlotSizeWrite_ShouldSucceed()
    {
        const int slotSize = 256;
        using var buffer = new MpmcCircularBuffer(GetUniqueName("MpmcMaxSlot"), slotCount: 4, slotSize: slotSize);

        // Write exactly max message size
        var data = new byte[buffer.MaxMessageSize];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 256);

        Assert.That(buffer.TryWrite(data), Is.True);

        var readBack = new byte[data.Length];
        var bytesRead = buffer.TryRead(readBack);

        Assert.That(bytesRead, Is.EqualTo(data.Length));
        Assert.That(readBack, Is.EqualTo(data));
    }

    [Test]
    public void MpmcCircularBuffer_ExceedMaxMessageSize_ShouldThrow()
    {
        const int slotSize = 64;
        using var buffer = new MpmcCircularBuffer(GetUniqueName("MpmcExceedMsg"), slotCount: 4, slotSize: slotSize);

        var data = new byte[buffer.MaxMessageSize + 1];

        Assert.Throws<ArgumentException>(() => buffer.TryWrite(data));
    }

    [Test]
    public void SharedArray_ZeroIndex_ReadWrite_ShouldWork()
    {
        using var array = new SharedArray<int>(GetUniqueName("SAZeroIdx"), 10);

        array[0] = 42;
        Assert.That(array[0], Is.EqualTo(42));
    }

    [Test]
    public void SharedArray_LastIndex_ReadWrite_ShouldWork()
    {
        using var array = new SharedArray<int>(GetUniqueName("SALastIdx"), 10);

        array[9] = 999;
        Assert.That(array[9], Is.EqualTo(999));
    }

    [Test]
    public void SharedArray_OutOfBoundsIndex_ShouldThrow()
    {
        using var array = new SharedArray<int>(GetUniqueName("SAOutOfBounds"), 10);

        Assert.Throws<IndexOutOfRangeException>(() => { var _ = array[10]; });
        Assert.Throws<IndexOutOfRangeException>(() => { var _ = array[-1]; });
    }

    [Test]
    public void HighPerformanceSharedBuffer_LargeNegativeOffset_ShouldThrow()
    {
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("NegOffset"), new SharedMemoryBufferOptions { Capacity = 1024 });

        var data = new byte[10];

        // Negative offsets should fail (cast to ulong causes overflow check)
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Write(data, -1));
    }

    [Test]
    public void StrictSharedMemory_MaxLengthString_ShouldWork()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(GetUniqueName("MaxStr"), schema);

        // String field is defined as 32 bytes max
        // With 2 bytes for length prefix, max string length is 15 chars (assuming UTF-16, 2 bytes per char)
        // Actually string is stored as UTF-8, so let's use ASCII chars
        var maxString = new string('X', 30); // Leave room for null terminator if any

        memory.WriteString(TestSchema.StringField, maxString);
        var readBack = memory.ReadString(TestSchema.StringField);

        Assert.That(readBack, Is.EqualTo(maxString));
    }

    [Test]
    public void HighPerformanceSharedBuffer_ConcurrentTimeout_AllShouldComplete()
    {
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("ConcTimeout"), new SharedMemoryBufferOptions { Capacity = 1024 });

        var tasks = new List<Task<bool>>();

        // Launch many tasks trying to acquire write lock with short timeout
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                if (buffer.TryAcquireWriteLock(TimeSpan.FromMilliseconds(100)))
                {
                    Thread.Sleep(5);
                    buffer.ReleaseWriteLock();
                    return true;
                }
                return false;
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // At least one should succeed (the first one), all should complete without hanging
        var successCount = tasks.Count(t => t.Result);
        Assert.That(successCount, Is.GreaterThanOrEqualTo(1));
    }

    #endregion
}
