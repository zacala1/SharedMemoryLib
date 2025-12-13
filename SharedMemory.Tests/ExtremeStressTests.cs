using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using NUnit.Framework;
using SharedMemory;

namespace SharedMemory.Tests;

/// <summary>
/// Extreme stress tests for maximum reliability validation.
/// These tests push the library to its limits.
/// </summary>
[TestFixture]
[Category("Extreme")]
public class ExtremeStressTests
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
        var name = $"Extreme_{prefix}_{Guid.NewGuid():N}";
        _createdBufferNames.Add(name);
        return name;
    }

    #region MPMC Extreme Stress Tests

    [Test]
    [Timeout(120000)]
    [Explicit("Long-running stress test")]
    public async Task MPMC_16Producers_16Consumers_1MillionMessages()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("MPMC_16x16"), slotCount: 4096, slotSize: 128);

        const int producerCount = 16;
        const int consumerCount = 16;
        const int messagesPerProducer = 62500; // Total 1M messages
        var totalExpected = producerCount * messagesPerProducer;

        var received = new ConcurrentDictionary<long, int>();
        var producersDone = 0;
        var errors = new ConcurrentBag<string>();
        var sw = Stopwatch.StartNew();

        // Producers
        var producers = Enumerable.Range(0, producerCount).Select(producerId => Task.Run(() =>
        {
            try
            {
                var data = new byte[64];
                for (int i = 0; i < messagesPerProducer; i++)
                {
                    long value = (long)producerId * 100000000L + i;
                    BitConverter.TryWriteBytes(data, value);

                    int retries = 0;
                    while (!buffer.TryWrite(data))
                    {
                        if (retries++ > 1000000)
                        {
                            errors.Add($"Producer {producerId} stuck at {i}");
                            return;
                        }
                        Thread.SpinWait(1 << Math.Min(retries, 8));
                    }
                }
            }
            finally
            {
                Interlocked.Increment(ref producersDone);
            }
        })).ToArray();

        // Consumers
        var consumers = Enumerable.Range(0, consumerCount).Select(consumerId => Task.Run(() =>
        {
            var readBuffer = new byte[128];
            int localCount = 0;
            int emptyReads = 0;

            while (localCount < totalExpected / consumerCount + 10000 && emptyReads < 50000)
            {
                var bytesRead = buffer.TryRead(readBuffer);
                if (bytesRead >= 8)
                {
                    long value = BitConverter.ToInt64(readBuffer, 0);
                    received.AddOrUpdate(value, 1, (k, v) => v + 1);
                    localCount++;
                    emptyReads = 0;
                }
                else
                {
                    emptyReads++;
                    if (producersDone == producerCount)
                    {
                        if (buffer.ApproximateCount == 0)
                            break;
                    }
                    Thread.SpinWait(1);
                }
            }
        })).ToArray();

        await Task.WhenAll(producers.Concat(consumers));

        sw.Stop();
        var stats = buffer.GetStatistics();

        Assert.That(errors, Is.Empty, $"Errors: {string.Join("; ", errors.Take(10))}");
        Assert.That(received.Count, Is.EqualTo(totalExpected),
            $"Expected {totalExpected} unique, got {received.Count}. Time: {sw.ElapsedMilliseconds}ms");

        // Check for duplicates
        var duplicates = received.Where(kv => kv.Value > 1).ToList();
        Assert.That(duplicates, Is.Empty,
            $"Found {duplicates.Count} duplicates: {string.Join(", ", duplicates.Take(5).Select(d => $"{d.Key}(x{d.Value})"))}");

        TestContext.Out.WriteLine($"Throughput: {totalExpected / (sw.ElapsedMilliseconds / 1000.0):N0} msg/sec");
        TestContext.Out.WriteLine($"Stats: Writes={stats.TotalWrites}, Reads={stats.TotalReads}, FailedWrites={stats.FailedWrites}");
    }

    [Test]
    [Timeout(60000)]
    public async Task MPMC_BurstTraffic_ShouldHandleSpikes()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("MPMC_Burst"), slotCount: 1024, slotSize: 64);

        const int burstCount = 10;
        const int messagesPerBurst = 10000;
        var errors = new ConcurrentBag<string>();
        var totalReceived = 0;

        for (int burst = 0; burst < burstCount; burst++)
        {
            var received = new ConcurrentBag<int>();
            var producerDone = false;

            // Burst producer
            var producer = Task.Run(() =>
            {
                var data = new byte[32];
                for (int i = 0; i < messagesPerBurst; i++)
                {
                    BitConverter.TryWriteBytes(data, burst * messagesPerBurst + i);
                    while (!buffer.TryWrite(data))
                        Thread.SpinWait(1);
                }
                producerDone = true;
            });

            // Consumer
            var consumer = Task.Run(() =>
            {
                var readBuf = new byte[64];
                int emptyCount = 0;
                while (received.Count < messagesPerBurst && emptyCount < 10000)
                {
                    var bytesRead = buffer.TryRead(readBuf);
                    if (bytesRead > 0)
                    {
                        received.Add(BitConverter.ToInt32(readBuf, 0));
                        emptyCount = 0;
                    }
                    else
                    {
                        emptyCount++;
                        if (producerDone && buffer.ApproximateCount == 0)
                            break;
                    }
                }
            });

            await Task.WhenAll(producer, consumer);
            totalReceived += received.Count;

            if (received.Count != messagesPerBurst)
                errors.Add($"Burst {burst}: expected {messagesPerBurst}, got {received.Count}");
        }

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
        Assert.That(totalReceived, Is.EqualTo(burstCount * messagesPerBurst));
    }

    #endregion

    #region SPSC Extreme Stress Tests

    [Test]
    [Timeout(60000)]
    public async Task SPSC_5MillionMessages_DataIntegrity()
    {
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("SPSC_5M"), 1024 * 1024);

        const int messageCount = 5000000;
        var errors = new ConcurrentBag<string>();
        var producerDone = false;
        var sw = Stopwatch.StartNew();

        var producer = Task.Run(() =>
        {
            var data = new byte[16];
            for (int i = 0; i < messageCount; i++)
            {
                BitConverter.TryWriteBytes(data.AsSpan(0, 4), i);
                BitConverter.TryWriteBytes(data.AsSpan(4, 4), ~i); // Checksum
                BitConverter.TryWriteBytes(data.AsSpan(8, 8), (long)i * 0x12345678L);

                while (!buffer.TryWrite(data))
                    Thread.SpinWait(1);
            }
            producerDone = true;
        });

        var consumer = Task.Run(() =>
        {
            var readBuf = new byte[16];
            int expected = 0;

            while (expected < messageCount)
            {
                var bytesRead = buffer.TryRead(readBuf);
                if (bytesRead == 16)
                {
                    int value = BitConverter.ToInt32(readBuf, 0);
                    int checksum = BitConverter.ToInt32(readBuf, 4);
                    long extended = BitConverter.ToInt64(readBuf, 8);

                    if (value != expected)
                    {
                        errors.Add($"Order mismatch: expected {expected}, got {value}");
                        return;
                    }
                    if (checksum != ~expected)
                    {
                        errors.Add($"Checksum mismatch at {expected}");
                        return;
                    }
                    if (extended != (long)expected * 0x12345678L)
                    {
                        errors.Add($"Extended data mismatch at {expected}");
                        return;
                    }
                    expected++;
                }
                else if (bytesRead == 0 && producerDone)
                {
                    Thread.Sleep(1);
                }
            }
        });

        await Task.WhenAll(producer, consumer);
        sw.Stop();

        Assert.That(errors, Is.Empty, string.Join("\n", errors.Take(10)));
        TestContext.Out.WriteLine($"Throughput: {messageCount / (sw.ElapsedMilliseconds / 1000.0):N0} msg/sec");
    }

    [Test]
    [Timeout(30000)]
    public void SPSC_VariableMessageSizes_Integrity()
    {
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("SPSC_VarSize"), 64 * 1024);

        const int messageCount = 10000;
        var random = new Random(42);
        var readBuf = new byte[256];

        for (int i = 0; i < messageCount; i++)
        {
            int size = random.Next(16, 128);
            var data = new byte[size];
            BitConverter.TryWriteBytes(data.AsSpan(0, 4), i);
            BitConverter.TryWriteBytes(data.AsSpan(4, 4), size);
            random.NextBytes(data.AsSpan(8));

            Assert.That(buffer.TryWrite(data), Is.True, $"Write {i} failed");

            var bytesRead = buffer.TryRead(readBuf);
            Assert.That(bytesRead, Is.EqualTo(size), $"Size mismatch at {i}");

            int readIdx = BitConverter.ToInt32(readBuf, 0);
            int readSize = BitConverter.ToInt32(readBuf, 4);
            Assert.That(readIdx, Is.EqualTo(i), $"Index mismatch at {i}");
            Assert.That(readSize, Is.EqualTo(size), $"Stored size mismatch at {i}");
        }
    }

    #endregion

    #region HighPerformanceSharedBuffer Extreme Tests

    [Test]
    [Timeout(30000)]
    public async Task HPBuffer_32Threads_ConcurrentLocking()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 64 * 1024 };
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("HP_32Thread"), options);

        const int threadCount = 32;
        const int iterationsPerThread = 5000;
        var errors = new ConcurrentBag<string>();
        var totalOps = 0L;

        var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(() =>
        {
            var data = new byte[256];
            var readBuf = new byte[256];
            var localRandom = new Random(threadId);
            int offset = (threadId % 16) * 256; // Partition space

            for (int i = 0; i < iterationsPerThread; i++)
            {
                bool isWrite = localRandom.Next(2) == 0;

                if (isWrite)
                {
                    if (buffer.TryAcquireWriteLock(TimeSpan.FromMilliseconds(100)))
                    {
                        try
                        {
                            localRandom.NextBytes(data);
                            buffer.Write(data, offset);
                            Interlocked.Increment(ref totalOps);
                        }
                        finally
                        {
                            buffer.ReleaseWriteLock();
                        }
                    }
                }
                else
                {
                    if (buffer.TryAcquireReadLock(TimeSpan.FromMilliseconds(100)))
                    {
                        try
                        {
                            buffer.Read(readBuf, offset);
                            Interlocked.Increment(ref totalOps);
                        }
                        finally
                        {
                            buffer.ReleaseReadLock();
                        }
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
        TestContext.Out.WriteLine($"Total operations completed: {totalOps:N0}");
        Assert.That(totalOps, Is.GreaterThan(threadCount * iterationsPerThread / 2)); // At least half should succeed
    }

    [Test]
    [Timeout(30000)]
    public void HPBuffer_LargeDataTransfer_1GB()
    {
        const int bufferSize = 16 * 1024 * 1024; // 16MB buffer
        const int chunkSize = 1024 * 1024; // 1MB chunks
        const long totalBytes = 1024L * 1024 * 1024; // 1GB total

        var options = new SharedMemoryBufferOptions
        {
            Capacity = bufferSize,
            EnableSimd = true
        };
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("HP_1GB"), options);

        var writeData = new byte[chunkSize];
        var readData = new byte[chunkSize];
        var random = new Random(42);

        var sw = Stopwatch.StartNew();
        long bytesTransferred = 0;

        while (bytesTransferred < totalBytes)
        {
            // Write
            random.NextBytes(writeData);
            int offset = (int)((bytesTransferred / chunkSize) % (bufferSize / chunkSize)) * chunkSize;
            if (offset + chunkSize > bufferSize)
                offset = 0;

            buffer.Write(writeData, offset);

            // Read back and verify
            buffer.Read(readData, offset);

            for (int i = 0; i < chunkSize; i += 1024)
            {
                if (writeData[i] != readData[i])
                {
                    Assert.Fail($"Data mismatch at offset {offset + i}");
                }
            }

            bytesTransferred += chunkSize;
        }

        sw.Stop();
        double throughputGBps = (totalBytes / (1024.0 * 1024 * 1024)) / (sw.ElapsedMilliseconds / 1000.0);
        TestContext.Out.WriteLine($"Throughput: {throughputGBps:F2} GB/s");
        Assert.That(throughputGBps, Is.GreaterThan(0.1)); // At least 0.1 GB/s (conservative for CI)
    }

    [Test]
    [Timeout(30000)]
    public void HPBuffer_ChecksumIntegrity_UnderLoad()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 1024 * 1024,
            EnableChecksumVerification = true
        };
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("HP_Checksum"), options);

        const int iterations = 10000;
        var random = new Random(42);
        var data = new byte[1024];

        for (int i = 0; i < iterations; i++)
        {
            random.NextBytes(data);
            buffer.Write(data, 0);
            buffer.UpdateChecksum(0, data.Length);

            Assert.That(buffer.VerifyIntegrity(), Is.True, $"Integrity check failed at iteration {i}");
        }
    }

    #endregion

    #region StrictSharedMemory Extreme Tests

    [Test]
    [Timeout(30000)]
    public async Task Strict_16Threads_MixedFieldAccess()
    {
        var schema = new ExtendedTestSchema();
        using var memory = new StrictSharedMemory<ExtendedTestSchema>(GetUniqueName("Strict_16T"), schema);

        const int threadCount = 16;
        const int iterations = 5000;
        var errors = new ConcurrentBag<string>();

        var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(() =>
        {
            var localRandom = new Random(threadId);

            for (int i = 0; i < iterations; i++)
            {
                try
                {
                    int fieldChoice = localRandom.Next(4);

                    switch (fieldChoice)
                    {
                        case 0:
                            using (memory.AcquireWriteLock())
                            {
                                memory.Write(ExtendedTestSchema.IntField, threadId * 10000 + i);
                            }
                            using (memory.AcquireReadLock())
                            {
                                var v = memory.Read<int>(ExtendedTestSchema.IntField);
                                if (v < 0)
                                    errors.Add($"Invalid int at {threadId}:{i}");
                            }
                            break;

                        case 1:
                            using (memory.AcquireWriteLock())
                            {
                                memory.Write(ExtendedTestSchema.DoubleField, Math.PI * i);
                            }
                            using (memory.AcquireReadLock())
                            {
                                var v = memory.Read<double>(ExtendedTestSchema.DoubleField);
                                if (double.IsNaN(v))
                                    errors.Add($"NaN double at {threadId}:{i}");
                            }
                            break;

                        case 2:
                            using (memory.AcquireWriteLock())
                            {
                                memory.Write(ExtendedTestSchema.GuidField, Guid.NewGuid());
                            }
                            using (memory.AcquireReadLock())
                            {
                                var v = memory.Read<Guid>(ExtendedTestSchema.GuidField);
                                // Just ensure no crash
                            }
                            break;

                        case 3:
                            using (memory.AcquireWriteLock())
                            {
                                memory.WriteString(ExtendedTestSchema.StringField, $"Thread{threadId}_Iter{i}");
                            }
                            using (memory.AcquireReadLock())
                            {
                                var v = memory.ReadString(ExtendedTestSchema.StringField);
                                if (string.IsNullOrEmpty(v))
                                    errors.Add($"Empty string at {threadId}:{i}");
                            }
                            break;
                    }
                }
                catch (TimeoutException)
                {
                    // Expected under high contention
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.That(errors, Is.Empty, $"Errors: {string.Join("; ", errors.Take(10))}");
    }

    [Test]
    [Timeout(60000)]
    public void Strict_ArrayField_LargeData()
    {
        var schema = new LargeArraySchema();
        using var memory = new StrictSharedMemory<LargeArraySchema>(GetUniqueName("Strict_LargeArr"), schema);

        const int iterations = 100;
        var sourceArray = new float[10000];
        var destArray = new float[10000];
        var random = new Random(42);

        for (int iter = 0; iter < iterations; iter++)
        {
            // Fill with random data
            for (int i = 0; i < sourceArray.Length; i++)
                sourceArray[i] = (float)random.NextDouble() * 1000;

            // Write
            using (memory.AcquireWriteLock())
            {
                memory.WriteArray<float>(LargeArraySchema.FloatArrayField, sourceArray);
            }

            // Read
            using (memory.AcquireReadLock())
            {
                memory.ReadArray<float>(LargeArraySchema.FloatArrayField, destArray);
            }

            // Verify
            for (int i = 0; i < sourceArray.Length; i++)
            {
                Assert.That(destArray[i], Is.EqualTo(sourceArray[i]).Within(0.0001f),
                    $"Mismatch at iter {iter}, index {i}");
            }
        }
    }

    #endregion

    #region Chaos Tests

    [Test]
    [Timeout(60000)]
    public async Task Chaos_RandomOperations_NoExceptions()
    {
        using var mpmcBuffer = new MpmcCircularBuffer(GetUniqueName("Chaos_MPMC"), slotCount: 256, slotSize: 128);
        using var spscBuffer = new LockFreeCircularBuffer(GetUniqueName("Chaos_SPSC"), 64 * 1024);
        var options = new SharedMemoryBufferOptions { Capacity = 16 * 1024 };
        using var hpBuffer = new HighPerformanceSharedBuffer(GetUniqueName("Chaos_HP"), options);

        const int threadCount = 8;
        const int operationsPerThread = 10000;
        var errors = new ConcurrentBag<string>();
        var completedOps = 0L;

        var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(() =>
        {
            var localRandom = new Random(threadId);
            var data = new byte[64];
            var readBuf = new byte[128];

            for (int i = 0; i < operationsPerThread; i++)
            {
                try
                {
                    int op = localRandom.Next(10);
                    localRandom.NextBytes(data);

                    switch (op)
                    {
                        case 0:
                        case 1:
                        case 2:
                            mpmcBuffer.TryWrite(data.AsSpan(0, localRandom.Next(1, 64)));
                            break;
                        case 3:
                        case 4:
                        case 5:
                            mpmcBuffer.TryRead(readBuf);
                            break;
                        case 6:
                            if (hpBuffer.TryAcquireWriteLock(TimeSpan.FromMilliseconds(10)))
                            {
                                try
                                { hpBuffer.Write(data, localRandom.Next(0, 1000)); }
                                finally { hpBuffer.ReleaseWriteLock(); }
                            }
                            break;
                        case 7:
                            if (hpBuffer.TryAcquireReadLock(TimeSpan.FromMilliseconds(10)))
                            {
                                try
                                { hpBuffer.Read(readBuf, localRandom.Next(0, 1000)); }
                                finally { hpBuffer.ReleaseReadLock(); }
                            }
                            break;
                        case 8:
                            _ = mpmcBuffer.ApproximateCount;
                            _ = mpmcBuffer.GetStatistics();
                            break;
                        case 9:
                            // Random delay to create timing variations
                            Thread.SpinWait(localRandom.Next(1, 100));
                            break;
                    }

                    Interlocked.Increment(ref completedOps);
                }
                catch (ObjectDisposedException)
                {
                    // Can happen in chaos test
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadId} op {i}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.That(errors, Is.Empty, $"Errors: {string.Join("; ", errors.Take(10))}");
        TestContext.Out.WriteLine($"Completed operations: {completedOps:N0}");
    }

    [Test]
    [Timeout(30000)]
    public async Task Chaos_RapidCreateDestroy()
    {
        const int cycles = 100;
        const int operationsPerCycle = 1000;
        var errors = new ConcurrentBag<string>();

        await Task.Run(() =>
        {
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                try
                {
                    using var buffer = new MpmcCircularBuffer(
                        GetUniqueName($"Chaos_CD_{cycle}"),
                        slotCount: 32, slotSize: 64);

                    var data = new byte[32];
                    var readBuf = new byte[64];

                    for (int op = 0; op < operationsPerCycle; op++)
                    {
                        buffer.TryWrite(data);
                        buffer.TryRead(readBuf);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Cycle {cycle}: {ex.Message}");
                }
            }
        });

        Assert.That(errors, Is.Empty, string.Join("\n", errors.Take(10)));
    }

    #endregion

    #region Long-Running Stability Tests

    [Test]
    [Timeout(120000)] // 2 minutes
    [Explicit("Long-running test")]
    public async Task Stability_MPMC_2Minutes_Continuous()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("Stability_2min"), slotCount: 2048, slotSize: 128);

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var errors = new ConcurrentBag<string>();
        var totalWrites = 0L;
        var totalReads = 0L;

        var producers = Enumerable.Range(0, 4).Select(id => Task.Run(() =>
        {
            var data = new byte[64];
            while (!cts.Token.IsCancellationRequested)
            {
                if (buffer.TryWrite(data))
                    Interlocked.Increment(ref totalWrites);
                Thread.SpinWait(1);
            }
        })).ToArray();

        var consumers = Enumerable.Range(0, 4).Select(id => Task.Run(() =>
        {
            var readBuf = new byte[128];
            while (!cts.Token.IsCancellationRequested)
            {
                if (buffer.TryRead(readBuf) > 0)
                    Interlocked.Increment(ref totalReads);
                Thread.SpinWait(1);
            }
        })).ToArray();

        await Task.WhenAll(producers.Concat(consumers));

        Assert.That(errors, Is.Empty);
        TestContext.Out.WriteLine($"Total writes: {totalWrites:N0}, reads: {totalReads:N0}");
        Assert.That(totalWrites, Is.GreaterThan(100000)); // Should complete many operations
    }

    #endregion

    #region Memory Pressure Tests

    [Test]
    [Timeout(30000)]
    public void MemoryPressure_LargeBufferCreation()
    {
        var buffers = new List<MpmcCircularBuffer>();

        try
        {
            // Create multiple large buffers
            for (int i = 0; i < 10; i++)
            {
                var buffer = new MpmcCircularBuffer(
                    GetUniqueName($"MemPressure_{i}"),
                    slotCount: 1024,
                    slotSize: 4096);
                buffers.Add(buffer);

                // Use them
                var data = new byte[2048];
                for (int j = 0; j < 100; j++)
                {
                    buffer.TryWrite(data);
                    buffer.TryRead(new byte[4096]);
                }
            }

            Assert.That(buffers.Count, Is.EqualTo(10));
        }
        finally
        {
            foreach (var buf in buffers)
                buf.Dispose();
        }
    }

    [Test]
    [Timeout(30000)]
    public async Task MemoryPressure_ConcurrentBufferAccess()
    {
        const int bufferCount = 20;
        var buffers = new List<MpmcCircularBuffer>();
        var errors = new ConcurrentBag<string>();

        try
        {
            for (int i = 0; i < bufferCount; i++)
            {
                buffers.Add(new MpmcCircularBuffer(
                    GetUniqueName($"ConcMem_{i}"),
                    slotCount: 256, slotSize: 256));
            }

            var tasks = buffers.Select((buffer, idx) => Task.Run(() =>
            {
                var data = new byte[128];
                var readBuf = new byte[256];

                for (int i = 0; i < 5000; i++)
                {
                    buffer.TryWrite(data);
                    buffer.TryRead(readBuf);
                }
            })).ToArray();

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }
        finally
        {
            foreach (var buf in buffers)
                buf.Dispose();
        }

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }

    #endregion

    #region Race Condition Detection Tests

    [Test]
    [Timeout(30000)]
    public async Task RaceCondition_ConcurrentDisposeWhileOperating()
    {
        const int iterations = 100;
        var errors = new ConcurrentBag<string>();

        for (int iter = 0; iter < iterations; iter++)
        {
            var buffer = new MpmcCircularBuffer(
                GetUniqueName($"RaceDispose_{iter}"),
                slotCount: 64, slotSize: 64);

            var operationTask = Task.Run(() =>
            {
                var data = new byte[32];
                var readBuf = new byte[64];

                for (int i = 0; i < 1000; i++)
                {
                    try
                    {
                        buffer.TryWrite(data);
                        buffer.TryRead(readBuf);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Expected - buffer was disposed
                        break;
                    }
                }
            });

            // Dispose while operations are ongoing
            await Task.Delay(1);
            buffer.Dispose();

            await operationTask;
        }

        Assert.That(errors, Is.Empty);
    }

    [Test]
    [Timeout(30000)]
    public async Task RaceCondition_WriterReaderContention()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("RaceWR"), slotCount: 8, slotSize: 64);

        const int iterations = 100000;
        var errors = new ConcurrentBag<string>();
        var writeSuccess = 0L;
        var readSuccess = 0L;

        var writer = Task.Run(() =>
        {
            var data = new byte[32];
            for (int i = 0; i < iterations; i++)
            {
                BitConverter.TryWriteBytes(data, i);
                if (buffer.TryWrite(data))
                    Interlocked.Increment(ref writeSuccess);
            }
        });

        var reader = Task.Run(() =>
        {
            var readBuf = new byte[64];
            int lastRead = -1;

            for (int i = 0; i < iterations; i++)
            {
                var bytesRead = buffer.TryRead(readBuf);
                if (bytesRead >= 4)
                {
                    int value = BitConverter.ToInt32(readBuf, 0);
                    if (value < lastRead)
                    {
                        errors.Add($"Out of order: got {value} after {lastRead}");
                    }
                    lastRead = value;
                    Interlocked.Increment(ref readSuccess);
                }
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.That(errors, Is.Empty, string.Join("\n", errors.Take(10)));
        TestContext.Out.WriteLine($"Writes: {writeSuccess}, Reads: {readSuccess}");
    }

    #endregion

    #region Helper Schemas

    private struct ExtendedTestSchema : ISharedMemorySchema
    {
        public const string IntField = "IntValue";
        public const string DoubleField = "DoubleValue";
        public const string GuidField = "GuidValue";
        public const string StringField = "StringValue";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(IntField);
            yield return FieldDefinition.Scalar<double>(DoubleField);
            yield return FieldDefinition.Scalar<Guid>(GuidField);
            yield return FieldDefinition.String(StringField, 128);
        }
    }

    private struct LargeArraySchema : ISharedMemorySchema
    {
        public const string FloatArrayField = "FloatArray";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Array<float>(FloatArrayField, 10000);
        }
    }

    #endregion
}

/// <summary>
/// Data integrity tests under extreme conditions
/// </summary>
[TestFixture]
[Category("Extreme")]
public class DataIntegrityExtremeTests
{
    private string GetUniqueName(string prefix) => $"Integrity_{prefix}_{Guid.NewGuid():N}";

    [Test]
    [Timeout(30000)]
    public async Task TornRead_Detection_16ByteTypes()
    {
        var schema = new GuidOnlySchema();
        using var memory = new StrictSharedMemory<GuidOnlySchema>(GetUniqueName("TornRead"), schema);

        const int iterations = 50000;
        var errors = new ConcurrentBag<string>();

        // Writer writes specific GUID pattern
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var guid = new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                memory.Write(GuidOnlySchema.GuidField, guid);
            }
        });

        // Reader verifies GUIDs are not torn
        var reader = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var guid = memory.Read<Guid>(GuidOnlySchema.GuidField);
                // A torn read would produce a GUID where parts don't match
                // Valid GUIDs should have the counter in the first 4 bytes and zeros elsewhere
                var bytes = guid.ToByteArray();
                // Check that bytes 4-15 are zero (if not torn)
                bool valid = true;
                for (int j = 4; j < 16; j++)
                {
                    if (bytes[j] != 0)
                    {
                        valid = false;
                        break;
                    }
                }
                if (!valid)
                {
                    errors.Add($"Possible torn read detected: {guid}");
                }
            }
        });

        await Task.WhenAll(writer, reader);

        // Note: With auto-lock for 16-byte types, we should NOT see torn reads
        Assert.That(errors, Is.Empty, $"Torn reads detected: {errors.Count}");
    }

    [Test]
    [Timeout(30000)]
    public void CRC32_IntegrityAfterHeavyLoad()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 1024 * 1024,
            EnableChecksumVerification = true
        };
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("CRC32Load"), options);

        const int iterations = 10000;
        var random = new Random(42);
        var data = new byte[4096];

        for (int i = 0; i < iterations; i++)
        {
            random.NextBytes(data);
            int offset = random.Next(0, 1024 * 1024 - 4096);

            buffer.Write(data, offset);
            buffer.UpdateChecksum(offset, data.Length);

            // Verify immediately
            Assert.That(buffer.VerifyIntegrity(), Is.True, $"Integrity failed at iteration {i}");

            // Read back and compare
            var readBack = new byte[4096];
            buffer.Read(readBack, offset);

            for (int j = 0; j < data.Length; j++)
            {
                if (data[j] != readBack[j])
                {
                    Assert.Fail($"Data corruption at iteration {i}, offset {j}");
                }
            }
        }
    }

    [Test]
    [Timeout(30000)]
    public async Task PatternVerification_UnderConcurrentLoad()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("PatternVerify"), slotCount: 1024, slotSize: 256);

        const int messageCount = 50000;
        var errors = new ConcurrentBag<string>();
        var producerDone = false;

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                var data = new byte[128];
                // Create verifiable pattern: [4-byte index][4-byte checksum][120 bytes pattern]
                BitConverter.TryWriteBytes(data.AsSpan(0, 4), i);
                BitConverter.TryWriteBytes(data.AsSpan(4, 4), i ^ unchecked((int)0xDEADBEEF));
                for (int j = 8; j < 128; j++)
                    data[j] = (byte)((i + j) % 256);

                while (!buffer.TryWrite(data))
                    Thread.SpinWait(1);
            }
            producerDone = true;
        });

        var consumer = Task.Run(() =>
        {
            var readBuf = new byte[256];
            int received = 0;
            int emptyReads = 0;

            while (received < messageCount && emptyReads < 100000)
            {
                var bytesRead = buffer.TryRead(readBuf);
                if (bytesRead >= 128)
                {
                    emptyReads = 0;
                    int idx = BitConverter.ToInt32(readBuf, 0);
                    int checksum = BitConverter.ToInt32(readBuf, 4);
                    int expectedChecksum = idx ^ unchecked((int)0xDEADBEEF);

                    if (checksum != expectedChecksum)
                    {
                        errors.Add($"Checksum mismatch at msg {received}: idx={idx}, expected={expectedChecksum:X8}, got={checksum:X8}");
                    }

                    // Verify pattern for first few bytes only to save time
                    for (int j = 8; j < Math.Min(16, 128); j++)
                    {
                        byte expected = (byte)((idx + j) % 256);
                        if (readBuf[j] != expected)
                        {
                            errors.Add($"Pattern mismatch at msg {received}, byte {j}: expected {expected}, got {readBuf[j]}");
                            break;
                        }
                    }

                    received++;
                }
                else
                {
                    emptyReads++;
                    if (producerDone && buffer.ApproximateCount == 0)
                    {
                        Thread.Sleep(1);
                    }
                }
            }
        });

        await Task.WhenAll(producer, consumer);

        Assert.That(errors, Is.Empty, $"Errors ({errors.Count}): {string.Join("; ", errors.Take(10))}");
    }

    private struct GuidOnlySchema : ISharedMemorySchema
    {
        public const string GuidField = "GuidValue";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<Guid>(GuidField);
        }
    }
}

/// <summary>
/// Boundary and edge case extreme tests
/// </summary>
[TestFixture]
[Category("Extreme")]
public class BoundaryExtremeTests
{
    private string GetUniqueName(string prefix) => $"Boundary_{prefix}_{Guid.NewGuid():N}";

    [Test]
    public void MPMC_MinimalSlots_Stress()
    {
        // Minimum practical slot count is 2 (MPMC algorithm requires >1 for proper full detection)
        using var buffer = new MpmcCircularBuffer(GetUniqueName("MinSlots"), slotCount: 2, slotSize: 64);

        Assert.That(buffer.SlotCount, Is.EqualTo(2));

        var data = new byte[32];
        var readBuf = new byte[64];

        for (int i = 0; i < 10000; i++)
        {
            BitConverter.TryWriteBytes(data, i);

            // Should succeed (buffer has space)
            Assert.That(buffer.TryWrite(data), Is.True, $"Write {i} failed");

            // Read
            var bytesRead = buffer.TryRead(readBuf);
            Assert.That(bytesRead, Is.EqualTo(32));
            Assert.That(BitConverter.ToInt32(readBuf, 0), Is.EqualTo(i));
        }
    }

    [Test]
    public void MPMC_TwoSlots_FullDetection()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("TwoSlotsFull"), slotCount: 2, slotSize: 64);

        var data = new byte[32];
        var readBuf = new byte[64];

        // Fill both slots
        Assert.That(buffer.TryWrite(data), Is.True, "First write should succeed");
        Assert.That(buffer.TryWrite(data), Is.True, "Second write should succeed");

        // Third write should fail (buffer full)
        Assert.That(buffer.TryWrite(data), Is.False, "Third write should fail (buffer full)");

        // Read one
        Assert.That(buffer.TryRead(readBuf), Is.GreaterThan(0));

        // Now write should succeed again
        Assert.That(buffer.TryWrite(data), Is.True, "Write after read should succeed");
    }

    [Test]
    public void SPSC_MinimalCapacity_Stress()
    {
        // Very small capacity
        using var buffer = new LockFreeCircularBuffer(GetUniqueName("MinCap"), 16);

        Assert.That(buffer.Capacity, Is.EqualTo(16));

        var data = new byte[8];
        var readBuf = new byte[8];

        for (int i = 0; i < 10000; i++)
        {
            BitConverter.TryWriteBytes(data, i);
            Assert.That(buffer.TryWrite(data), Is.True);

            var bytesRead = buffer.TryRead(readBuf);
            Assert.That(bytesRead, Is.EqualTo(8));
            Assert.That(BitConverter.ToInt32(readBuf, 0), Is.EqualTo(i));
        }
    }

    [Test]
    public void HPBuffer_ExactBoundaryAccess()
    {
        const int capacity = 1024;
        var options = new SharedMemoryBufferOptions { Capacity = capacity };
        using var buffer = new HighPerformanceSharedBuffer(GetUniqueName("ExactBound"), options);

        // Test exact boundary writes
        var data = new byte[1];

        // Write to every position
        for (int i = 0; i < capacity; i++)
        {
            data[0] = (byte)(i % 256);
            buffer.Write(data, i);
        }

        // Read back and verify
        var readBuf = new byte[1];
        for (int i = 0; i < capacity; i++)
        {
            buffer.Read(readBuf, i);
            Assert.That(readBuf[0], Is.EqualTo((byte)(i % 256)), $"Mismatch at offset {i}");
        }
    }

    [Test]
    public void SharedArray_MaxIndex_Stress()
    {
        const int size = 100000;
        using var array = new SharedArray<long>(GetUniqueName("MaxIdx"), size);

        // Access first and last repeatedly
        for (int i = 0; i < 10000; i++)
        {
            array[0] = i;
            array[size - 1] = i * 2;

            Assert.That(array[0], Is.EqualTo(i));
            Assert.That(array[size - 1], Is.EqualTo(i * 2));
        }
    }

    [Test]
    public void MPMC_MaxMessageSize_Repeated()
    {
        const int slotSize = 1024;
        using var buffer = new MpmcCircularBuffer(GetUniqueName("MaxMsg"), slotCount: 16, slotSize: slotSize);

        int maxSize = buffer.MaxMessageSize;
        var data = new byte[maxSize];
        var readBuf = new byte[maxSize];
        var random = new Random(42);

        for (int i = 0; i < 1000; i++)
        {
            random.NextBytes(data);
            BitConverter.TryWriteBytes(data, i); // First 4 bytes = index

            Assert.That(buffer.TryWrite(data), Is.True, $"Write {i} failed");

            var bytesRead = buffer.TryRead(readBuf);
            Assert.That(bytesRead, Is.EqualTo(maxSize));

            int readIdx = BitConverter.ToInt32(readBuf, 0);
            Assert.That(readIdx, Is.EqualTo(i), $"Index mismatch at {i}");
        }
    }
}
