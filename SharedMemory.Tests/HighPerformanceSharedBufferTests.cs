using NUnit.Framework;
using SharedMemory;

namespace SharedMemory.Tests;

[TestFixture]
public class HighPerformanceSharedBufferTests
{
    private const string TestBufferName = "TestBuffer_HighPerf";

    [TearDown]
    public void Cleanup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Test]
    public void Create_WithValidOptions_ShouldSucceed()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            CreateOrOpen = true
        };

        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_Create", options);

        Assert.That(buffer.Name, Is.EqualTo(TestBufferName + "_Create"));
        Assert.That(buffer.Capacity, Is.GreaterThanOrEqualTo(4096));
        Assert.That(buffer.IsOwner, Is.True);
    }

    [Test]
    public void Write_Read_SmallData_ShouldRoundTrip()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_SmallData", options);

        var testData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        buffer.Write(testData, 0);

        var readBuffer = new byte[testData.Length];
        buffer.Read(readBuffer, 0);

        Assert.That(readBuffer, Is.EqualTo(testData));
    }

    [Test]
    public void Write_Read_LargeData_ShouldUseSimd()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 1024 * 1024,
            EnableSimd = true
        };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_LargeData", options);

        // 64KB - large enough to trigger SIMD path
        var testData = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i % 256)).ToArray();

        buffer.Write(testData, 0);

        var readBuffer = new byte[testData.Length];
        buffer.Read(readBuffer, 0);

        Assert.That(readBuffer, Is.EqualTo(testData));
    }

    [Test]
    public async Task WriteAsync_ReadAsync_ShouldRoundTrip()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_Async", options);

        var testData = new byte[] { 11, 22, 33, 44, 55 };

        await buffer.WriteAsync(testData, 100);

        var readBuffer = new byte[testData.Length];
        await buffer.ReadAsync(readBuffer, 100);

        Assert.That(readBuffer, Is.EqualTo(testData));
    }

    [Test]
    public void WriteLock_ShouldPreventConcurrentWrites()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_WriteLock", options);

        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromMilliseconds(100)), Is.False);

        buffer.ReleaseWriteLock();
        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        buffer.ReleaseWriteLock();
    }

    [Test]
    public void ReadLock_ShouldAllowMultipleReaders()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_ReadLock", options);

        Assert.That(buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)), Is.True);

        buffer.ReleaseReadLock();
        buffer.ReleaseReadLock();
    }

    [Test]
    public void GetMemory_ShouldReturnValidSpan()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_GetMemory", options);

        var memory = buffer.GetMemory(0, 100);
        Assert.That(memory.Length, Is.EqualTo(100));

        var testData = new byte[] { 99, 88, 77, 66, 55 };
        testData.CopyTo(memory);

        var readBuffer = new byte[testData.Length];
        buffer.Read(readBuffer, 0);

        Assert.That(readBuffer, Is.EqualTo(testData));
    }

    [Test]
    public void Write_AtDifferentOffsets_ShouldNotOverlap()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_Offsets", options);

        var data1 = new byte[] { 1, 1, 1, 1, 1 };
        var data2 = new byte[] { 2, 2, 2, 2, 2 };
        var data3 = new byte[] { 3, 3, 3, 3, 3 };

        buffer.Write(data1, 0);
        buffer.Write(data2, 100);
        buffer.Write(data3, 200);

        var read1 = new byte[5];
        var read2 = new byte[5];
        var read3 = new byte[5];

        buffer.Read(read1, 0);
        buffer.Read(read2, 100);
        buffer.Read(read3, 200);

        Assert.That(read1, Is.EqualTo(data1));
        Assert.That(read2, Is.EqualTo(data2));
        Assert.That(read3, Is.EqualTo(data3));
    }

    [Test]
    public void Write_OutOfBounds_ShouldThrow()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 100 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_OutOfBounds", options);

        var testData = new byte[200];
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Write(testData, 0));
    }

    [Test]
    public void Read_OutOfBounds_ShouldThrow()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 100 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_ReadOutOfBounds", options);

        var readBuffer = new byte[200];
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Read(readBuffer, 0));
    }

    [Test]
    public void ConcurrentReadWrite_ShouldBeThreadSafe()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 1024 * 1024 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_Concurrent", options);

        var writeCount = 0;
        var readCount = 0;
        var errors = new List<Exception>();

        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                try
                {
                    if (buffer.TryAcquireWriteLock(TimeSpan.FromMilliseconds(10)))
                    {
                        try
                        {
                            var data = BitConverter.GetBytes(i);
                            buffer.Write(data, 0);
                            Interlocked.Increment(ref writeCount);
                        }
                        finally
                        {
                            buffer.ReleaseWriteLock();
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (errors)
                        errors.Add(ex);
                }
            }
        });

        var readerTask = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                try
                {
                    if (buffer.TryAcquireReadLock(TimeSpan.FromMilliseconds(10)))
                    {
                        try
                        {
                            var data = new byte[4];
                            buffer.Read(data, 0);
                            Interlocked.Increment(ref readCount);
                        }
                        finally
                        {
                            buffer.ReleaseReadLock();
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (errors)
                        errors.Add(ex);
                }
            }
        });

        Task.WaitAll(writerTask, readerTask);

        Assert.That(errors, Is.Empty, $"Errors: {string.Join(", ", errors.Select(e => e.Message))}");
        Assert.That(writeCount, Is.GreaterThan(0));
        Assert.That(readCount, Is.GreaterThan(0));
    }

    #region Checksum Tests

    [Test]
    public void CalculateChecksum_ShouldReturnConsistentValue()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_Checksum", options);

        var testData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        buffer.Write(testData, 0);

        var checksum1 = buffer.CalculateChecksum(0, testData.Length);
        var checksum2 = buffer.CalculateChecksum(0, testData.Length);

        Assert.That(checksum1, Is.EqualTo(checksum2));
    }

    [Test]
    public void CalculateChecksum_DifferentData_ShouldReturnDifferentValues()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_ChecksumDiff", options);

        var data1 = new byte[] { 1, 2, 3, 4, 5 };
        var data2 = new byte[] { 5, 4, 3, 2, 1 };

        buffer.Write(data1, 0);
        var checksum1 = buffer.CalculateChecksum(0, data1.Length);

        buffer.Write(data2, 0);
        var checksum2 = buffer.CalculateChecksum(0, data2.Length);

        Assert.That(checksum1, Is.Not.EqualTo(checksum2));
    }

    [Test]
    public void VerifyIntegrity_WhenDataUnchanged_ShouldReturnTrue()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            EnableChecksumVerification = true
        };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_Integrity", options);

        var testData = new byte[] { 10, 20, 30, 40, 50 };
        buffer.Write(testData, 0);

        Assert.That(buffer.VerifyIntegrity(), Is.True);
    }

    #endregion

    #region Lock Owner Info Tests

    [Test]
    public void GetLockOwnerInfo_WhenLockHeld_ShouldReturnOwnerInfo()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_LockOwner", options);

        buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1));
        try
        {
            var info = buffer.GetLockOwnerInfo();
            Assert.That(info.ProcessId, Is.GreaterThan(0));
            Assert.That(info.ThreadId, Is.GreaterThan(0));
            Assert.That(info.AcquiredTimestamp, Is.GreaterThan(0));
            Assert.That(info.IsOrphan, Is.False);
        }
        finally
        {
            buffer.ReleaseWriteLock();
        }
    }

    [Test]
    public void IsWriteLockOrphaned_WhenLockHeldByCurrentProcess_ShouldReturnFalse()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            EnableOrphanLockDetection = true
        };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_NotOrphan", options);

        buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1));
        try
        {
            Assert.That(buffer.IsWriteLockOrphaned(), Is.False);
        }
        finally
        {
            buffer.ReleaseWriteLock();
        }
    }

    [Test]
    public void TryForceReleaseWriteLock_WhenNotOrphaned_ShouldReturnFalse()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_ForceRelease", options);

        buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1));
        try
        {
            // Should not force release since we're the owner
            var released = buffer.TryForceReleaseWriteLock();
            Assert.That(released, Is.False);
        }
        finally
        {
            buffer.ReleaseWriteLock();
        }
    }

    #endregion

    #region Event Tests

    [Test]
    public void OnDataWritten_WhenEventsEnabled_ShouldFire()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            EnableEvents = true
        };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_WriteEvent", options);

        BufferEventArgs? receivedArgs = null;
        buffer.OnDataWritten += (sender, args) => receivedArgs = args;

        var testData = new byte[] { 1, 2, 3, 4, 5 };
        buffer.Write(testData, 100);

        Assert.That(receivedArgs, Is.Not.Null);
        Assert.That(receivedArgs!.EventType, Is.EqualTo(BufferEventType.DataWritten));
        Assert.That(receivedArgs.BytesAffected, Is.EqualTo(5));
        Assert.That(receivedArgs.Offset, Is.EqualTo(100));
    }

    [Test]
    public void OnDataWritten_WhenEventsDisabled_ShouldNotFire()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            EnableEvents = false
        };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_NoEvent", options);

        BufferEventArgs? receivedArgs = null;
        buffer.OnDataWritten += (sender, args) => receivedArgs = args;

        var testData = new byte[] { 1, 2, 3, 4, 5 };
        buffer.Write(testData, 0);

        Assert.That(receivedArgs, Is.Null);
    }

    #endregion

    #region Checksum Corruption Detection Tests

    [Test]
    public void VerifyIntegrity_WhenDataCorrupted_ShouldReturnFalse()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            EnableChecksumVerification = true
        };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_Corrupt", options);

        // Write original data and update checksum
        var originalData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        buffer.Write(originalData, 0);
        buffer.UpdateChecksum(0, originalData.Length);

        // Verify integrity is true before corruption
        Assert.That(buffer.VerifyIntegrity(), Is.True);

        // Corrupt the data by writing different bytes
        var corruptedData = new byte[] { 99, 99, 99, 99, 99, 99, 99, 99, 99, 99 };
        buffer.Write(corruptedData, 0);

        // Verify integrity should now return false (checksum mismatch)
        Assert.That(buffer.VerifyIntegrity(), Is.False);
    }

    [Test]
    public void UpdateChecksum_ThenVerify_ShouldSucceed()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            EnableChecksumVerification = true
        };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_UpdateChecksum", options);

        var data = new byte[100];
        Random.Shared.NextBytes(data);
        buffer.Write(data, 0);

        // Update checksum for the written data
        buffer.UpdateChecksum(0, data.Length);

        // Verify should pass
        Assert.That(buffer.VerifyIntegrity(), Is.True);
    }

    #endregion

    #region Edge Case Tests

    [Test]
    public void Write_ZeroLengthData_ShouldNotThrow()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_ZeroWrite", options);

        var emptyData = Array.Empty<byte>();
        Assert.DoesNotThrow(() => buffer.Write(emptyData, 0));
    }

    [Test]
    public void Read_ZeroLengthBuffer_ShouldNotThrow()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_ZeroRead", options);

        var emptyBuffer = Array.Empty<byte>();
        Assert.DoesNotThrow(() => buffer.Read(emptyBuffer, 0));
    }

    [Test]
    public void Write_AtCapacityBoundary_ShouldSucceed()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 1024 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_Boundary", options);

        // Write data that fills exactly to capacity (minus header)
        var dataSize = (int)buffer.Capacity - 256; // Leave room for header
        var data = new byte[dataSize];
        Random.Shared.NextBytes(data);

        Assert.DoesNotThrow(() => buffer.Write(data, 0));

        var readBuffer = new byte[dataSize];
        buffer.Read(readBuffer, 0);
        Assert.That(readBuffer, Is.EqualTo(data));
    }

    [Test]
    public void ConcurrentDispose_ShouldNotThrow()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_ConcurrentDispose", options);

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            try
            {
                buffer.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Expected for concurrent dispose calls
            }
        })).ToArray();

        Assert.DoesNotThrow(() => Task.WaitAll(tasks));
    }

    [Test]
    public void OperationsAfterDispose_ShouldThrowObjectDisposedException()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_AfterDispose", options);
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer.Write(new byte[10], 0));
        Assert.Throws<ObjectDisposedException>(() => buffer.Read(new byte[10], 0));
        Assert.Throws<ObjectDisposedException>(() => buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)));
        Assert.Throws<ObjectDisposedException>(() => buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void LockTimeout_ShouldReturnFalse()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer(TestBufferName + "_Timeout", options);

        // Acquire write lock
        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);

        // Try to acquire another write lock from same thread with short timeout
        // This should fail because write lock is already held
        var task = Task.Run(() => buffer.TryAcquireWriteLock(TimeSpan.FromMilliseconds(50)));
        Assert.That(task.Result, Is.False);

        buffer.ReleaseWriteLock();
    }

    #endregion
}
