using System.Collections.Concurrent;
using NUnit.Framework;
using SharedMemory;

namespace SharedMemory.Tests;

/// <summary>
/// Tests for the Multi-Producer/Multi-Consumer circular buffer
/// </summary>
[TestFixture]
public class MpmcCircularBufferTests
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
        var name = $"MPMC_{prefix}_{Guid.NewGuid():N}";
        _createdBufferNames.Add(name);
        return name;
    }

    #region Basic Functionality Tests

    [Test]
    public void Create_WithValidParameters_ShouldSucceed()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("Create"), slotCount: 16, slotSize: 64);

        Assert.That(buffer.SlotCount, Is.EqualTo(16));
        Assert.That(buffer.MaxMessageSize, Is.EqualTo(48)); // 64 - 16 (slot header)
    }

    [Test]
    public void Create_SlotCountRoundsUpToPowerOf2()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("PowerOf2"), slotCount: 10, slotSize: 64);

        Assert.That(buffer.SlotCount, Is.EqualTo(16)); // 10 rounds up to 16
    }

    [Test]
    public void TryWrite_TryRead_SingleMessage_ShouldRoundTrip()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("Single"), slotCount: 16, slotSize: 64);

        var testData = new byte[] { 1, 2, 3, 4, 5 };
        var success = buffer.TryWrite(testData);

        Assert.That(success, Is.True);

        var readBuffer = new byte[64];
        var bytesRead = buffer.TryRead(readBuffer);

        Assert.That(bytesRead, Is.EqualTo(testData.Length));
        Assert.That(readBuffer.Take(bytesRead).ToArray(), Is.EqualTo(testData));
    }

    [Test]
    public void TryWrite_ExceedsMaxSize_ShouldThrow()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("ExceedsMax"), slotCount: 16, slotSize: 32);

        var largeData = new byte[100]; // Exceeds MaxMessageSize (32 - 16 = 16)
        Assert.Throws<ArgumentException>(() => buffer.TryWrite(largeData));
    }

    [Test]
    public void TryRead_WhenEmpty_ShouldReturnZero()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("Empty"), slotCount: 16, slotSize: 64);

        var readBuffer = new byte[64];
        var bytesRead = buffer.TryRead(readBuffer);

        Assert.That(bytesRead, Is.EqualTo(0));
    }

    [Test]
    public void TryWrite_WhenFull_ShouldReturnFalse()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("Full"), slotCount: 4, slotSize: 64);

        // Fill all slots
        var data = new byte[32];
        for (int i = 0; i < 4; i++)
        {
            Assert.That(buffer.TryWrite(data), Is.True, $"Write {i} should succeed");
        }

        // Next write should fail
        Assert.That(buffer.TryWrite(data), Is.False);
    }

    #endregion

    #region FIFO Order Tests

    [Test]
    public void MultipleWrites_ShouldMaintainFIFOOrder()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("FIFO"), slotCount: 32, slotSize: 64);

        // Write sequential values
        for (int i = 0; i < 20; i++)
        {
            var data = BitConverter.GetBytes(i);
            Assert.That(buffer.TryWrite(data), Is.True);
        }

        // Read and verify order
        for (int i = 0; i < 20; i++)
        {
            var readBuffer = new byte[64];
            var bytesRead = buffer.TryRead(readBuffer);
            Assert.That(bytesRead, Is.EqualTo(4));
            Assert.That(BitConverter.ToInt32(readBuffer, 0), Is.EqualTo(i));
        }
    }

    #endregion

    #region Statistics Tests

    [Test]
    public void GetStatistics_ShouldReturnCorrectCounts()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("Stats"), slotCount: 16, slotSize: 64);

        var data = new byte[32];
        var readBuffer = new byte[64];

        // Write 5 times
        for (int i = 0; i < 5; i++)
            buffer.TryWrite(data);

        // Read 3 times
        for (int i = 0; i < 3; i++)
            buffer.TryRead(readBuffer);

        var stats = buffer.GetStatistics();
        Assert.That(stats.TotalWrites, Is.EqualTo(5));
        Assert.That(stats.TotalReads, Is.EqualTo(3));
    }

    [Test]
    public void ApproximateCount_ShouldTrackUnreadItems()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("Count"), slotCount: 16, slotSize: 64);

        Assert.That(buffer.ApproximateCount, Is.EqualTo(0));

        var data = new byte[32];
        for (int i = 0; i < 5; i++)
            buffer.TryWrite(data);

        Assert.That(buffer.ApproximateCount, Is.EqualTo(5));

        var readBuffer = new byte[64];
        buffer.TryRead(readBuffer);
        buffer.TryRead(readBuffer);

        Assert.That(buffer.ApproximateCount, Is.EqualTo(3));
    }

    [Test]
    public void ApproximateAvailable_ShouldTrackFreeSlots()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("Available"), slotCount: 8, slotSize: 64);

        Assert.That(buffer.ApproximateAvailable, Is.EqualTo(8));

        var data = new byte[32];
        for (int i = 0; i < 3; i++)
            buffer.TryWrite(data);

        Assert.That(buffer.ApproximateAvailable, Is.EqualTo(5));
    }

    #endregion

    #region Wait Methods Tests

    [Test]
    public void WaitWrite_WhenSpaceAvailable_ShouldSucceed()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("WaitWrite"), slotCount: 16, slotSize: 64);

        var data = new byte[32];
        var success = buffer.WaitWrite(data, TimeSpan.FromSeconds(1));

        Assert.That(success, Is.True);
    }

    [Test]
    public void WaitRead_WhenDataAvailable_ShouldSucceed()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("WaitRead"), slotCount: 16, slotSize: 64);

        var data = new byte[] { 1, 2, 3, 4 };
        buffer.TryWrite(data);

        var readBuffer = new byte[64];
        var bytesRead = buffer.WaitRead(readBuffer, TimeSpan.FromSeconds(1));

        Assert.That(bytesRead, Is.EqualTo(4));
    }

    [Test]
    public void WaitRead_WhenEmpty_ShouldTimeout()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("WaitTimeout"), slotCount: 16, slotSize: 64);

        var readBuffer = new byte[64];
        var bytesRead = buffer.WaitRead(readBuffer, TimeSpan.FromMilliseconds(50));

        Assert.That(bytesRead, Is.EqualTo(0));
    }

    #endregion

    #region Multi-Producer Tests

    [Test]
    public async Task MultipleProducers_SingleConsumer_ShouldNotLoseMessages()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("MPSC"), slotCount: 1024, slotSize: 64);

        const int producerCount = 4;
        const int messagesPerProducer = 1000;
        var totalExpected = producerCount * messagesPerProducer;

        var received = new ConcurrentBag<int>();
        var producerDone = 0;

        // Start producers
        var producers = Enumerable.Range(0, producerCount).Select(producerId => Task.Run(() =>
        {
            for (int i = 0; i < messagesPerProducer; i++)
            {
                int value = producerId * 100000 + i;
                var data = BitConverter.GetBytes(value);

                while (!buffer.TryWrite(data))
                {
                    Thread.SpinWait(10);
                }
            }
            Interlocked.Increment(ref producerDone);
        })).ToArray();

        // Consumer
        var consumer = Task.Run(() =>
        {
            var readBuffer = new byte[64];
            while (received.Count < totalExpected)
            {
                var bytesRead = buffer.TryRead(readBuffer);
                if (bytesRead >= 4)
                {
                    received.Add(BitConverter.ToInt32(readBuffer, 0));
                }
                else if (producerDone == producerCount)
                {
                    Thread.SpinWait(100);
                    if (buffer.ApproximateCount == 0 && received.Count < totalExpected)
                    {
                        // Check one more time
                        Thread.Sleep(10);
                        if (buffer.ApproximateCount == 0)
                            break;
                    }
                }
            }
        });

        await Task.WhenAll(producers.Append(consumer));

        Assert.That(received.Count, Is.EqualTo(totalExpected), "Should receive all messages");
    }

    #endregion

    #region Multi-Consumer Tests

    [Test]
    public async Task SingleProducer_MultipleConsumers_ShouldNotDuplicateMessages()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("SPMC"), slotCount: 1024, slotSize: 64);

        const int consumerCount = 4;
        const int totalMessages = 4000;

        var received = new ConcurrentBag<int>();
        var producerDone = false;

        // Producer
        var producer = Task.Run(() =>
        {
            for (int i = 0; i < totalMessages; i++)
            {
                var data = BitConverter.GetBytes(i);
                while (!buffer.TryWrite(data))
                {
                    Thread.SpinWait(10);
                }
            }
            producerDone = true;
        });

        // Consumers
        var consumers = Enumerable.Range(0, consumerCount).Select(_ => Task.Run(() =>
        {
            var readBuffer = new byte[64];
            while (true)
            {
                var bytesRead = buffer.TryRead(readBuffer);
                if (bytesRead >= 4)
                {
                    received.Add(BitConverter.ToInt32(readBuffer, 0));
                }
                else if (producerDone)
                {
                    Thread.MemoryBarrier();
                    if (buffer.ApproximateCount == 0)
                    {
                        Thread.Sleep(5);
                        if (buffer.ApproximateCount == 0)
                            break;
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(consumers.Prepend(producer));

        // All messages should be received exactly once
        Assert.That(received.Count, Is.EqualTo(totalMessages), "Should receive all messages");

        var sorted = received.OrderBy(x => x).ToList();
        for (int i = 0; i < totalMessages; i++)
        {
            Assert.That(sorted[i], Is.EqualTo(i), $"Message {i} should appear exactly once");
        }
    }

    #endregion

    #region MPMC Stress Test

    [Test]
    public async Task MPMC_HighContention_ShouldMaintainIntegrity()
    {
        using var buffer = new MpmcCircularBuffer(GetUniqueName("MPMC_Stress"), slotCount: 256, slotSize: 64);

        const int producerCount = 4;
        const int consumerCount = 4;
        const int messagesPerProducer = 2500;
        var totalExpected = producerCount * messagesPerProducer;

        var received = new ConcurrentBag<int>();
        var producersDone = 0;
        var errors = new ConcurrentBag<string>();

        // Producers
        var producers = Enumerable.Range(0, producerCount).Select(producerId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < messagesPerProducer; i++)
                {
                    int value = producerId * 1000000 + i;
                    var data = BitConverter.GetBytes(value);

                    int retries = 0;
                    while (!buffer.TryWrite(data))
                    {
                        Thread.SpinWait(1 << Math.Min(retries, 10));
                        retries++;
                        if (retries > 100000)
                        {
                            errors.Add($"Producer {producerId} stuck at message {i}");
                            return;
                        }
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
            var readBuffer = new byte[64];
            int emptyReads = 0;

            while (received.Count < totalExpected && emptyReads < 1000)
            {
                var bytesRead = buffer.TryRead(readBuffer);
                if (bytesRead >= 4)
                {
                    received.Add(BitConverter.ToInt32(readBuffer, 0));
                    emptyReads = 0;
                }
                else
                {
                    emptyReads++;
                    if (producersDone == producerCount && buffer.ApproximateCount == 0)
                    {
                        Thread.Sleep(1);
                    }
                    else
                    {
                        Thread.SpinWait(10);
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(producers.Concat(consumers));

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
        Assert.That(received.Count, Is.EqualTo(totalExpected),
            $"Expected {totalExpected} messages, received {received.Count}");

        // Verify no duplicates
        var grouped = received.GroupBy(x => x).Where(g => g.Count() > 1).ToList();
        Assert.That(grouped, Is.Empty,
            $"Found duplicate messages: {string.Join(", ", grouped.Select(g => $"{g.Key}(x{g.Count()})"))}");
    }

    #endregion

    #region Dispose Tests

    [Test]
    public void Dispose_ShouldCleanupResources()
    {
        var buffer = new MpmcCircularBuffer(GetUniqueName("Dispose"), slotCount: 16, slotSize: 64);
        buffer.Dispose();

        // Should not throw on second dispose
        Assert.DoesNotThrow(() => buffer.Dispose());
    }

    [Test]
    public void AccessAfterDispose_ShouldThrow()
    {
        var buffer = new MpmcCircularBuffer(GetUniqueName("DisposedAccess"), slotCount: 16, slotSize: 64);
        buffer.Dispose();

        var data = new byte[10];
        Assert.Throws<ObjectDisposedException>(() => buffer.TryWrite(data));
        Assert.Throws<ObjectDisposedException>(() => buffer.TryRead(data));
        Assert.Throws<ObjectDisposedException>(() => _ = buffer.ApproximateCount);
    }

    #endregion

    #region Constructor Validation Tests

    [Test]
    public void Constructor_NullName_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => new MpmcCircularBuffer(null!, slotCount: 16, slotSize: 64));
    }

    [Test]
    public void Constructor_EmptyName_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => new MpmcCircularBuffer("", slotCount: 16, slotSize: 64));
    }

    [Test]
    public void Constructor_ZeroSlotCount_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MpmcCircularBuffer(GetUniqueName("Zero"), slotCount: 0, slotSize: 64));
    }

    [Test]
    public void Constructor_NegativeSlotCount_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MpmcCircularBuffer(GetUniqueName("Negative"), slotCount: -1, slotSize: 64));
    }

    [Test]
    public void Constructor_SlotSizeTooSmall_ShouldThrow()
    {
        // Slot size must be > 16 (SlotHeaderSize)
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MpmcCircularBuffer(GetUniqueName("TooSmall"), slotCount: 16, slotSize: 16));
    }

    #endregion
}
