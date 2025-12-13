using NUnit.Framework;
using SharedMemory;

namespace SharedMemory.Tests;

[TestFixture]
public class LockFreeCircularBufferTests
{
    private const string TestBufferName = "TestBuffer_Circular";

    [TearDown]
    public void Cleanup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Test]
    public void Create_WithValidCapacity_ShouldSucceed()
    {
        using var buffer = new LockFreeCircularBuffer(TestBufferName + "_Create", 4096);

        Assert.That(buffer.Capacity, Is.GreaterThanOrEqualTo(4096));
    }

    [Test]
    public void TryWrite_TryRead_SingleMessage_ShouldRoundTrip()
    {
        using var buffer = new LockFreeCircularBuffer(TestBufferName + "_Single", 4096);

        var testData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var success = buffer.TryWrite(testData);

        Assert.That(success, Is.True);

        var readBuffer = new byte[testData.Length];
        var bytesRead = buffer.TryRead(readBuffer);

        Assert.That(bytesRead, Is.EqualTo(testData.Length));
        Assert.That(readBuffer, Is.EqualTo(testData));
    }

    [Test]
    public void TryWrite_ExceedsCapacity_ShouldThrow()
    {
        using var buffer = new LockFreeCircularBuffer(TestBufferName + "_Full", 128);

        var largeData = new byte[200];
        Assert.Throws<ArgumentException>(() => buffer.TryWrite(largeData));
    }

    [Test]
    public void TryWrite_WhenFull_ShouldReturnFalse()
    {
        using var buffer = new LockFreeCircularBuffer(TestBufferName + "_BufferFull", 256);

        // Fill the buffer
        var chunk = new byte[64];
        while (buffer.TryWrite(chunk))
        { }

        // Now writing should fail
        var success = buffer.TryWrite(chunk);
        Assert.That(success, Is.False);
    }

    [Test]
    public void TryRead_WhenEmpty_ShouldReturnZero()
    {
        using var buffer = new LockFreeCircularBuffer(TestBufferName + "_Empty", 4096);

        var readBuffer = new byte[10];
        var bytesRead = buffer.TryRead(readBuffer);

        Assert.That(bytesRead, Is.EqualTo(0));
    }

    [Test]
    public void MultipleWrites_ShouldMaintainFIFOOrder()
    {
        using var buffer = new LockFreeCircularBuffer(TestBufferName + "_FIFO", 4096);

        for (int i = 0; i < 10; i++)
        {
            var data = new byte[] { (byte)i };
            Assert.That(buffer.TryWrite(data), Is.True);
        }

        for (int i = 0; i < 10; i++)
        {
            var readBuffer = new byte[1];
            var bytesRead = buffer.TryRead(readBuffer);
            Assert.That(bytesRead, Is.EqualTo(1));
            Assert.That(readBuffer[0], Is.EqualTo((byte)i));
        }
    }

    [Test]
    public void Available_ShouldTrackFreeSpace()
    {
        using var buffer = new LockFreeCircularBuffer(TestBufferName + "_Available", 4096);

        // Available means free space to write (not data to read)
        var initialAvailable = buffer.Available;
        Assert.That(initialAvailable, Is.EqualTo(4096));

        var testData = new byte[100];
        buffer.TryWrite(testData);

        // After writing 100 bytes, available space decreases
        Assert.That(buffer.Available, Is.EqualTo(4096 - 100));
    }

    [Test]
    public void Used_ShouldTrackWrittenData()
    {
        using var buffer = new LockFreeCircularBuffer(TestBufferName + "_Used", 4096);

        Assert.That(buffer.Used, Is.EqualTo(0));

        var testData = new byte[100];
        buffer.TryWrite(testData);

        Assert.That(buffer.Used, Is.GreaterThanOrEqualTo(100));
    }

    [Test]
    public void GetStatistics_ShouldReturnValidData()
    {
        using var buffer = new LockFreeCircularBuffer(TestBufferName + "_Stats", 4096);

        var testData = new byte[50];
        buffer.TryWrite(testData);
        buffer.TryWrite(testData);

        var readBuffer = new byte[50];
        buffer.TryRead(readBuffer);

        var stats = buffer.GetStatistics();
        Assert.That(stats.Writes, Is.EqualTo(2));
        Assert.That(stats.Reads, Is.EqualTo(1));
        Assert.That(stats.BytesWritten, Is.EqualTo(100));
        Assert.That(stats.BytesRead, Is.EqualTo(50));
    }

    [Test]
    public void WrapAround_ShouldHandleCorrectly()
    {
        using var buffer = new LockFreeCircularBuffer(TestBufferName + "_Wrap", 512);

        // Write and read multiple times to cause wrap-around
        for (int iteration = 0; iteration < 10; iteration++)
        {
            for (int i = 0; i < 5; i++)
            {
                var writeData = new byte[50];
                for (int j = 0; j < 50; j++)
                    writeData[j] = (byte)((iteration * 5 + i) % 256);

                Assert.That(buffer.TryWrite(writeData), Is.True, $"Failed to write at iteration {iteration}, i={i}");
            }

            for (int i = 0; i < 5; i++)
            {
                var readBuffer = new byte[50];
                var bytesRead = buffer.TryRead(readBuffer);
                Assert.That(bytesRead, Is.EqualTo(50), $"Failed to read at iteration {iteration}, i={i}");
            }
        }
    }

    [Test]
    public async Task ConcurrentProducerConsumer_SPSC_ShouldWork()
    {
        using var buffer = new LockFreeCircularBuffer(TestBufferName + "_SPSC", 64 * 1024);

        const int messageCount = 10000;
        var producerDone = false;
        var consumerErrors = new List<string>();

        var producerTask = Task.Run(() =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                var data = BitConverter.GetBytes(i);
                while (!buffer.TryWrite(data))
                {
                    Thread.SpinWait(10);
                }
            }
            producerDone = true;
        });

        var consumerTask = Task.Run(() =>
        {
            int expectedValue = 0;
            while (expectedValue < messageCount)
            {
                var readBuffer = new byte[4];
                var bytesRead = buffer.TryRead(readBuffer);

                if (bytesRead == 4)
                {
                    var value = BitConverter.ToInt32(readBuffer, 0);
                    if (value != expectedValue)
                    {
                        consumerErrors.Add($"Expected {expectedValue}, got {value}");
                    }
                    expectedValue++;
                }
                else if (bytesRead == 0 && producerDone)
                {
                    Thread.SpinWait(10);
                }
            }
        });

        await Task.WhenAll(producerTask, consumerTask);

        Assert.That(consumerErrors, Is.Empty, string.Join(", ", consumerErrors));
    }

    [Test]
    public void VariableSizeMessages_ShouldWork()
    {
        using var buffer = new LockFreeCircularBuffer(TestBufferName + "_Variable", 4096);

        var messages = new[]
        {
            new byte[] { 1 },
            new byte[] { 2, 2 },
            new byte[] { 3, 3, 3 },
            new byte[] { 4, 4, 4, 4 },
            new byte[] { 5, 5, 5, 5, 5 }
        };

        foreach (var msg in messages)
        {
            Assert.That(buffer.TryWrite(msg), Is.True);
        }

        foreach (var expected in messages)
        {
            var readBuffer = new byte[expected.Length];
            var bytesRead = buffer.TryRead(readBuffer);
            Assert.That(bytesRead, Is.EqualTo(expected.Length));
            Assert.That(readBuffer, Is.EqualTo(expected));
        }
    }

    [Test]
    public void Dispose_ShouldCleanupResources()
    {
        var buffer = new LockFreeCircularBuffer(TestBufferName + "_Dispose", 4096);
        buffer.Dispose();

        // Should not throw on second dispose
        Assert.DoesNotThrow(() => buffer.Dispose());
    }
}
