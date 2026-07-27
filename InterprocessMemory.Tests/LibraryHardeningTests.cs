using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using InterprocessMemory;

namespace InterprocessMemory.Tests;

[TestFixture]
public class LibraryHardeningTests
{
    private static string N(string prefix) => $"Hardening_{prefix}_{Guid.NewGuid():N}";

    [Test]
    public void CreateFalse_MissingRegions_ShouldThrow()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new MemoryRegion(N("MissingRaw"),
                new MemoryRegionOptions { Capacity = 256, CreateOrOpen = false }));

        Assert.Throws<FileNotFoundException>(() =>
            new SharedArray<int>(N("MissingArray"), 4, create: false));

        Assert.Throws<FileNotFoundException>(() =>
            new SingleProducerByteStream(N("MissingSpsc"), 1024, create: false));

        Assert.Throws<FileNotFoundException>(() =>
            new ConcurrentMessageQueue(N("MissingMpmc"), 4, 64, create: false));

        Assert.Throws<FileNotFoundException>(() =>
            new StructuredMemory<SimpleSchema>(N("MissingStrict"), new SimpleSchema(), create: false));
    }

    [Test]
    public void SingleProducerByteStream_DefaultSecondOpener_DoesNotResetExistingQueue()
    {
        string name = N("SpscNoReset");
        using var writer = new SingleProducerByteStream(name, 1024);
        Assert.That(writer.TryWrite(new byte[] { 1, 2, 3, 4 }), Is.True);

        using var secondOpener = new SingleProducerByteStream(name, 1024);

        Span<byte> read = stackalloc byte[4];
        Assert.That(writer.TryRead(read), Is.EqualTo(4));
        Assert.That(read.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void InfiniteWriteLockTimeout_WaitsUntilReaderReleases()
    {
        using var buffer = new MemoryRegion(
            N("InfiniteLock"),
            new MemoryRegionOptions { Capacity = 256 });

        Assert.That(buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)), Is.True);
        using var writerStarted = new ManualResetEventSlim(false);

        var writer = Task.Run(() =>
        {
            writerStarted.Set();
            bool acquired = buffer.TryAcquireWriteLock(Timeout.InfiniteTimeSpan);
            if (acquired)
                buffer.ReleaseWriteLock();
            return acquired;
        });

        Assert.That(writerStarted.Wait(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(writer.Wait(TimeSpan.FromMilliseconds(100)), Is.False);

        buffer.ReleaseReadLock();
        Assert.That(writer.Wait(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(writer.Result, Is.True);
    }

    [Test]
    public void WriteLock_CannotBeReleasedByDifferentThread()
    {
        using var buffer = new MemoryRegion(
            N("ThreadOwner"),
            new MemoryRegionOptions { Capacity = 256 });

        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);

        var invalidRelease = new Thread(buffer.ReleaseWriteLock);
        invalidRelease.Start();
        invalidRelease.Join();

        var contender = Task.Run(() => buffer.TryAcquireWriteLock(TimeSpan.FromMilliseconds(50)));
        Assert.That(contender.Result, Is.False);

        buffer.ReleaseWriteLock();
        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        buffer.ReleaseWriteLock();
    }

    [Test]
    public void ReadLock_DoubleRelease_DoesNotBreakWriterExclusion()
    {
        using var buffer = new MemoryRegion(
            N("ReadUnderflow"),
            new MemoryRegionOptions { Capacity = 256 });

        Assert.That(buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)), Is.True);
        buffer.ReleaseReadLock();
        buffer.ReleaseReadLock();

        Assert.That(buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)), Is.True);
        try
        {
            var writer = Task.Run(() => buffer.TryAcquireWriteLock(TimeSpan.FromMilliseconds(50)));
            Assert.That(writer.Result, Is.False);
        }
        finally
        {
            buffer.ReleaseReadLock();
        }
    }

    [Test]
    public void StructuredMemory_ReorderedSameVersionSchema_ShouldThrow()
    {
        string name = N("SchemaOrder");
        using var owner = new StructuredMemory<OrderedSchema>(name, new OrderedSchema());

        Assert.Throws<InvalidOperationException>(() =>
            new StructuredMemory<ReorderedSchema>(name, new ReorderedSchema(), create: false));
    }

    [Test]
    public void StructuredMemory_InvalidFieldAlignment_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
            new StructuredMemory<InvalidAlignmentSchema>(N("BadAlignment"), new InvalidAlignmentSchema()));
    }

    [Test]
    public void StructuredMemory_CreatorStoredSchemaVersion_ShouldMatchCurrentVersion()
    {
        using var memory = new StructuredMemory<VersionedSimpleSchema>(
            N("StoredVersion"),
            new VersionedSimpleSchema());

        Assert.That(memory.StoredSchemaVersion, Is.EqualTo(memory.SchemaVersion));
    }

    [Test]
    public void StructuredMemory_CorruptBlobLength_ShouldThrowInvalidData()
    {
        string name = N("BlobLength");
        using var memory = new StructuredMemory<BlobSchema>(name, new BlobSchema());
        memory.WriteBlob(BlobSchema.Data, new byte[] { 1, 2, 3 });

        using var raw = MemoryRegion.OpenExisting(
            name,
            options: null,
            RegionKind.StructuredMemory);
        raw.Write(BitConverter.GetBytes(999), 64);

        Assert.Throws<InvalidDataException>(() => memory.ReadBlob(BlobSchema.Data));
    }

    [Test]
    public void OrphanEvent_RespectsEnableEvents()
    {
        using var disabled = new MemoryRegion(
            N("OrphanEventOff"),
            new MemoryRegionOptions
            {
                Capacity = 256,
                EnableEvents = false,
                OrphanLockTimeout = TimeSpan.FromMilliseconds(1)
            });

        bool disabledRaised = false;
        disabled.OnOrphanLockDetected += (_, _) => disabledRaised = true;
        Assert.That(disabled.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(SpinWait.SpinUntil(
            () => disabled.IsWriteLockOrphaned(),
            TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(disabled.TryForceReleaseWriteLock(), Is.True);
        Assert.That(disabledRaised, Is.False);

        using var enabled = new MemoryRegion(
            N("OrphanEventOn"),
            new MemoryRegionOptions
            {
                Capacity = 256,
                EnableEvents = true,
                OrphanLockTimeout = TimeSpan.FromMilliseconds(1)
            });

        bool enabledRaised = false;
        enabled.OnOrphanLockDetected += (_, _) => enabledRaised = true;
        Assert.That(enabled.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(SpinWait.SpinUntil(
            () => enabled.IsWriteLockOrphaned(),
            TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(enabled.TryForceReleaseWriteLock(), Is.True);
        Assert.That(enabledRaised, Is.True);
    }

    [Test]
    public void ConcurrentMessageQueue_ZeroLengthMessage_ShouldThrow()
    {
        using var buffer = new ConcurrentMessageQueue(N("MpmcZero"), 4, 64);

        Assert.Throws<ArgumentException>(() => buffer.TryWrite(ReadOnlySpan<byte>.Empty));
    }

    public struct SimpleSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Value");
        }
    }

    public struct OrderedSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("A");
            yield return FieldDefinition.Scalar<double>("B");
        }
    }

    public struct ReorderedSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<double>("B");
            yield return FieldDefinition.Scalar<int>("A");
        }
    }

    public struct InvalidAlignmentSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return new FieldDefinition
            {
                Name = "Bad",
                TypeCode = FieldTypeCode.Int32,
                ElementSize = 4,
                ArrayLength = 1,
                Alignment = 3
            };
        }
    }

    public struct VersionedSimpleSchema : IVersionedSchema
    {
        public int Version => 7;
        public bool IsCompatibleWith(int otherVersion) => otherVersion == Version;

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Value");
        }
    }

    public struct BlobSchema : IMemorySchema
    {
        public const string Data = "Data";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Blob(Data, 8);
        }
    }
}
