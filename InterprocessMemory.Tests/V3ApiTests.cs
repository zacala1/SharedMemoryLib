using System.Reflection;
using NUnit.Framework;

namespace InterprocessMemory.Tests;

[TestFixture]
public class V3ApiTests
{
    private static string Name(string suffix) => $"V3_{suffix}_{Guid.NewGuid():N}";

    [Test]
    public void MemoryRegion_OpenExisting_DiscoversCapacity()
    {
        string name = Name("Region");
        using var owner = MemoryRegion.CreateOrOpen(name, 1234);
        using var opener = MemoryRegion.OpenExisting(name);

        Assert.Multiple(() =>
        {
            Assert.That(owner.Capacity, Is.EqualTo(1234));
            Assert.That(opener.Capacity, Is.EqualTo(1234));
            Assert.That(owner.IsOwner, Is.True);
            Assert.That(opener.IsOwner, Is.False);
        });
    }

    [Test]
    public void PublicFactories_ReplacePublicConstructors()
    {
        Type[] factoryOnlyTypes =
        {
            typeof(MemoryRegion),
            typeof(StructuredMemory<SimpleSchema>),
            typeof(SharedArray<int>),
            typeof(SingleProducerQueue<int>),
            typeof(InterprocessMemory.ConcurrentQueue<int>),
            typeof(SingleProducerByteStream),
            typeof(ConcurrentMessageQueue)
        };

        foreach (Type type in factoryOnlyTypes)
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, type.FullName);
    }

    [Test]
    public void ExportedApi_UsesOnlyVersion3NamesAndNamespace()
    {
        Assembly assembly = typeof(MemoryRegion).Assembly;
        Type[] exportedTypes = assembly.GetExportedTypes();
        string[] removedTypeNames =
        {
            "HighPerformanceSharedBuffer",
            "ISharedMemoryBuffer",
            "SharedMemoryBufferOptions",
            "ISharedMemorySchema",
            "SharedTypeCode",
            "StrictSharedMemory`1",
            "LockFreeCircularBuffer",
            "MpmcCircularBuffer"
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                exportedTypes.Select(type => type.Namespace),
                Is.All.EqualTo("InterprocessMemory"));
            Assert.That(
                exportedTypes.Select(type => type.Name),
                Has.None.Matches<string>(name => removedTypeNames.Contains(name)));
        });
    }

    [Test]
    public void SingleProducerQueue_RoundsCapacity_AndPreservesOrderAcrossWraparound()
    {
        string name = Name("TypedSpsc");
        using var queue = SingleProducerQueue<int>.CreateOrOpen(name, 3);
        Assert.That(queue.Capacity, Is.EqualTo(4));

        for (int i = 0; i < 4; i++)
            Assert.That(queue.TryEnqueue(i), Is.True);
        Assert.That(queue.TryEnqueue(99), Is.False);

        for (int expected = 0; expected < 2; expected++)
        {
            Assert.That(queue.TryDequeue(out int value), Is.True);
            Assert.That(value, Is.EqualTo(expected));
        }

        Assert.That(queue.TryEnqueue(4), Is.True);
        Assert.That(queue.TryEnqueue(5), Is.True);

        for (int expected = 2; expected < 6; expected++)
        {
            Assert.That(queue.TryDequeue(out int value), Is.True);
            Assert.That(value, Is.EqualTo(expected));
        }
    }

    [Test]
    public void TypedQueue_DifferentSameSizedType_IsRejected()
    {
        string name = Name("Fingerprint");
        using var owner = SingleProducerQueue<PayloadA>.CreateOrOpen(name, 8);
        Assert.Throws<InvalidDataException>(
            () => SingleProducerQueue<PayloadB>.OpenExisting(name));
    }

    [Test]
    public void TypedQueue_TimeoutAndCancellation_ReturnFalse()
    {
        string name = Name("Timeout");
        using var queue = SingleProducerQueue<int>.CreateOrOpen(name, 2);
        Assert.That(queue.TryEnqueue(1), Is.True);
        Assert.That(queue.TryEnqueue(2), Is.True);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.That(
            queue.TryEnqueue(3, TimeSpan.FromSeconds(1), canceled.Token),
            Is.False);

        Assert.That(queue.TryDequeue(out _), Is.True);
        Assert.That(queue.TryDequeue(out _), Is.True);
        Assert.That(
            queue.TryDequeue(out _, TimeSpan.FromMilliseconds(10)),
            Is.False);
    }

    [Test]
    public void ConcurrentQueue_MultipleThreads_DeliversEveryItemExactlyOnce()
    {
        string name = Name("TypedMpmc");
        using var queue = InterprocessMemory.ConcurrentQueue<int>.CreateOrOpen(name, 1024);
        const int producers = 4;
        const int perProducer = 2000;
        var seen = new int[producers * perProducer];

        Task[] producerTasks = Enumerable.Range(0, producers)
            .Select(producerId => Task.Run(() =>
            {
                for (int i = 0; i < perProducer; i++)
                {
                    int value = producerId * perProducer + i;
                    Assert.That(queue.TryEnqueue(value, TimeSpan.FromSeconds(10)), Is.True);
                }
            }))
            .ToArray();

        var consumer = Task.Run(() =>
        {
            int received = 0;
            while (received < seen.Length)
            {
                if (!queue.TryDequeue(out int value))
                {
                    Thread.Yield();
                    continue;
                }
                Interlocked.Increment(ref seen[value]);
                received++;
            }
        });

        Assert.That(
            Task.WaitAll(producerTasks.Append(consumer).ToArray(), TimeSpan.FromSeconds(30)),
            Is.True);
        Assert.That(seen, Is.All.EqualTo(1));
    }

    [Test]
    public void ByteStream_PartialRead_DoesNotPreserveWriteBoundary()
    {
        string name = Name("Stream");
        using var stream = SingleProducerByteStream.CreateOrOpen(name, 16);
        Assert.That(stream.TryWrite(new byte[] { 1, 2, 3, 4, 5, 6 }), Is.True);

        Span<byte> first = stackalloc byte[2];
        Span<byte> second = stackalloc byte[4];
        Assert.That(stream.TryRead(first), Is.EqualTo(2));
        Assert.That(stream.TryRead(second), Is.EqualTo(4));
        Assert.That(first.ToArray(), Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(second.ToArray(), Is.EqualTo(new byte[] { 3, 4, 5, 6 }));
    }

    [Test]
    public void OversizedQueueAndStreamDimensions_AreRejected()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SingleProducerByteStream.CreateOrOpen(Name("HugeStream"), long.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ConcurrentMessageQueue.CreateOrOpen(
                    Name("HugeCapacity"),
                    int.MaxValue,
                    maxMessageSize: 1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ConcurrentMessageQueue.CreateOrOpen(
                    Name("HugeMessage"),
                    capacity: 1,
                    maxMessageSize: int.MaxValue));
        });
    }

    [Test]
    public void MessageQueue_PreservesBoundaries_AndDoesNotConsumeOnSmallDestination()
    {
        string name = Name("Messages");
        using var owner = ConcurrentMessageQueue.CreateOrOpen(name, 3, 32);
        using var opener = ConcurrentMessageQueue.OpenExisting(name);
        Assert.That(owner.Capacity, Is.EqualTo(4));
        Assert.That(opener.MaxMessageSize, Is.EqualTo(32));

        Assert.That(owner.TryEnqueue(new byte[] { 1, 2, 3, 4 }), Is.True);
        Assert.Throws<ArgumentException>(() =>
        {
            Span<byte> tooSmall = stackalloc byte[2];
            opener.TryDequeue(tooSmall, out _);
        });

        Span<byte> destination = stackalloc byte[32];
        Assert.That(opener.TryDequeue(destination, out int length), Is.True);
        Assert.That(destination[..length].ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void MessageQueue_HeaderSizeBeyondRegion_IsRejected()
    {
        string name = Name("MessageHeader");
        const int actualRegionCapacity = 384 + 2 * 24;
        using var region = MemoryRegion.CreateOrOpen(
            name,
            actualRegionCapacity,
            options: null,
            RegionKind.ConcurrentMessageQueue);

        Span<byte> header = stackalloc byte[384];
        header.Clear();
        BitConverter.TryWriteBytes(header.Slice(128), 2);
        BitConverter.TryWriteBytes(header.Slice(132), 32);
        BitConverter.TryWriteBytes(header.Slice(136), 16);
        BitConverter.TryWriteBytes(header.Slice(140), 3);
        BitConverter.TryWriteBytes(header.Slice(144), 0x514D434D504953L);
        region.Write(header, 0);

        var error = Assert.Throws<InvalidDataException>(() =>
        {
            using var queue = ConcurrentMessageQueue.OpenExisting(name);
        });
        Assert.That(error!.Message, Does.Contain("capacity does not match"));
    }

    [Test]
    public void WrongRegionKind_IsRejected()
    {
        string name = Name("Kind");
        using var array = SharedArray<int>.CreateOrOpen(name, 4);
        Assert.Throws<InvalidDataException>(() => MemoryRegion.OpenExisting(name));
    }

    [Test]
    public void Version2Header_IsRejectedWithoutModification()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ipm_v2_{Guid.NewGuid():N}.bin");
        try
        {
            byte[] bytes = new byte[256];
            BitConverter.TryWriteBytes(bytes.AsSpan(0), 0x48504D53u);
            BitConverter.TryWriteBytes(bytes.AsSpan(4), 2u);
            BitConverter.TryWriteBytes(bytes.AsSpan(8), 128L);
            File.WriteAllBytes(path, bytes);

            var error = Assert.Throws<InvalidDataException>(() =>
                MemoryRegion.OpenExisting(
                    Name("V2"),
                    new MemoryRegionOptions { FilePath = path }));

            Assert.That(error!.Message, Does.Contain("2.x"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Test]
    public void OpenExisting_HeaderCapacityBeyondMapping_IsRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ipm_corrupt_{Guid.NewGuid():N}.bin");
        try
        {
            byte[] bytes = new byte[256];
            BitConverter.TryWriteBytes(bytes.AsSpan(0), 0x524D5049u);
            BitConverter.TryWriteBytes(bytes.AsSpan(4), 3u);
            BitConverter.TryWriteBytes(bytes.AsSpan(8), 1024L);
            BitConverter.TryWriteBytes(bytes.AsSpan(84), 1);
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(() =>
                MemoryRegion.OpenExisting(
                    Name("Corrupt"),
                    new MemoryRegionOptions { FilePath = path }));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    public struct SimpleSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Value");
        }
    }

    private readonly struct PayloadA
    {
        public PayloadA(int value) => Value = value;

        public readonly int Value;
    }

    private readonly struct PayloadB
    {
        public PayloadB(int value) => Value = value;

        public readonly int Value;
    }
}
