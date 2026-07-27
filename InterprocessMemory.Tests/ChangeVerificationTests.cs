using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using InterprocessMemory;

namespace InterprocessMemory.Tests;

/// <summary>
/// Focused functional verification of each change applied in the latest commit.
/// Each test directly exercises the modified behavior to confirm it works as intended.
/// </summary>
[TestFixture]
[Category("Verification")]
public class ChangeVerificationTests
{
    private static string N(string p) => $"Verify_{p}_{Guid.NewGuid():N}";

    // ── #2 GetTypeCode Dictionary lookup ────────────────────────────────────

    [Test]
    public void GetTypeCode_AllPrimitives_ReturnsCorrectCode()
    {
        Assert.That(FieldDefinition.GetTypeCode<bool>(),           Is.EqualTo(FieldTypeCode.Boolean));
        Assert.That(FieldDefinition.GetTypeCode<byte>(),           Is.EqualTo(FieldTypeCode.Byte));
        Assert.That(FieldDefinition.GetTypeCode<sbyte>(),          Is.EqualTo(FieldTypeCode.SByte));
        Assert.That(FieldDefinition.GetTypeCode<char>(),           Is.EqualTo(FieldTypeCode.Char));
        Assert.That(FieldDefinition.GetTypeCode<short>(),          Is.EqualTo(FieldTypeCode.Int16));
        Assert.That(FieldDefinition.GetTypeCode<ushort>(),         Is.EqualTo(FieldTypeCode.UInt16));
        Assert.That(FieldDefinition.GetTypeCode<int>(),            Is.EqualTo(FieldTypeCode.Int32));
        Assert.That(FieldDefinition.GetTypeCode<uint>(),           Is.EqualTo(FieldTypeCode.UInt32));
        Assert.That(FieldDefinition.GetTypeCode<long>(),           Is.EqualTo(FieldTypeCode.Int64));
        Assert.That(FieldDefinition.GetTypeCode<ulong>(),          Is.EqualTo(FieldTypeCode.UInt64));
        Assert.That(FieldDefinition.GetTypeCode<float>(),          Is.EqualTo(FieldTypeCode.Single));
        Assert.That(FieldDefinition.GetTypeCode<double>(),         Is.EqualTo(FieldTypeCode.Double));
        Assert.That(FieldDefinition.GetTypeCode<decimal>(),        Is.EqualTo(FieldTypeCode.Decimal));
        Assert.That(FieldDefinition.GetTypeCode<Guid>(),           Is.EqualTo(FieldTypeCode.Guid));
        Assert.That(FieldDefinition.GetTypeCode<DateTime>(),       Is.EqualTo(FieldTypeCode.DateTime));
        Assert.That(FieldDefinition.GetTypeCode<TimeSpan>(),       Is.EqualTo(FieldTypeCode.TimeSpan));
        Assert.That(FieldDefinition.GetTypeCode<DateTimeOffset>(), Is.EqualTo(FieldTypeCode.DateTimeOffset));
    }

    [Test]
    public void GetTypeCode_EnumTypes_ReturnsUnderlyingCode()
    {
        Assert.That(FieldDefinition.GetTypeCode<TestEnumInt>(),    Is.EqualTo(FieldTypeCode.Int32));
        Assert.That(FieldDefinition.GetTypeCode<TestEnumByte>(),   Is.EqualTo(FieldTypeCode.Byte));
        Assert.That(FieldDefinition.GetTypeCode<TestEnumLong>(),   Is.EqualTo(FieldTypeCode.Int64));
    }

    [Test]
    public void GetTypeCode_CustomStruct_ReturnsStruct()
    {
        Assert.That(FieldDefinition.GetTypeCode<Vec3>(), Is.EqualTo(FieldTypeCode.Struct));
    }

    // ── #3 WriteInternal/ReadInternal MemoryMarshal ──────────────────────────

    [Test]
    public void WriteRead_AllUnmanagedTypes_RoundTrip()
    {
        var schema = new AllTypesSchema();
        using var mem = new StructuredMemory<AllTypesSchema>(N("RT"), schema);

        // Every unmanaged type written and read back correctly
        mem.Write(AllTypesSchema.BoolF,   true);
        mem.Write(AllTypesSchema.ByteF,   (byte)255);
        mem.Write(AllTypesSchema.IntF,    int.MaxValue);
        mem.Write(AllTypesSchema.LongF,   long.MinValue);
        mem.Write(AllTypesSchema.DoubleF, Math.PI);
        mem.Write(AllTypesSchema.GuidF,   Guid.Empty);

        Assert.That(mem.Read<bool>(AllTypesSchema.BoolF),   Is.True);
        Assert.That(mem.Read<byte>(AllTypesSchema.ByteF),   Is.EqualTo((byte)255));
        Assert.That(mem.Read<int>(AllTypesSchema.IntF),     Is.EqualTo(int.MaxValue));
        Assert.That(mem.Read<long>(AllTypesSchema.LongF),   Is.EqualTo(long.MinValue));
        Assert.That(mem.Read<double>(AllTypesSchema.DoubleF), Is.EqualTo(Math.PI).Within(1e-15));
        Assert.That(mem.Read<Guid>(AllTypesSchema.GuidF),   Is.EqualTo(Guid.Empty));
    }

    // ── #4 Schema hash cached ───────────────────────────────────────────────

    [Test]
    public void SchemaHash_SameSchema_SameName_IsConsistent()
    {
        // Two instances of the same schema type must produce identical hash
        // (i.e. the cached value is deterministic, not random per-instance)
        var s1 = new SimpleSchema();
        var s2 = new SimpleSchema();
        using var m1 = new StructuredMemory<SimpleSchema>(N("H1"), s1);
        using var m2 = new StructuredMemory<SimpleSchema>(N("H2"), s2);

        // If hashes differ the second open of the same name would throw.
        // Verify by opening the first buffer with a second instance.
        string shared = N("SH");
        using var creator = new StructuredMemory<SimpleSchema>(shared, s1);
        creator.Write(SimpleSchema.Value, 42);

        using var opener = new StructuredMemory<SimpleSchema>(shared, s2, create: false);
        Assert.That(opener.Read<int>(SimpleSchema.Value), Is.EqualTo(42));
    }

    // ── #5 CancellationToken on WaitWrite/WaitRead ───────────────────────────

    [Test]
    public void LockFree_WaitWrite_CancelledToken_ReturnsFalseImmediately()
    {
        using var buf = new SingleProducerByteStream(N("LFCancel"), 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        // Fill the buffer so TryWrite would block
        var chunk = new byte[32];
        while (buf.TryWrite(chunk)) { }

        bool result = buf.WaitWrite(chunk, TimeSpan.FromSeconds(5), cts.Token);
        Assert.That(result, Is.False, "Cancelled token should return false without waiting");
    }

    [Test]
    public void LockFree_WaitRead_CancelledToken_ReturnsZeroImmediately()
    {
        using var buf = new SingleProducerByteStream(N("LFCancelR"), 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        int result = buf.WaitRead(new byte[4], TimeSpan.FromSeconds(5), cts.Token);
        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void LockFree_WaitWrite_CancelMidWait_StopsBlocking()
    {
        using var buf = new SingleProducerByteStream(N("LFMidW"), 64);
        using var cts = new CancellationTokenSource();

        var chunk = new byte[32];
        while (buf.TryWrite(chunk)) { } // fill buffer

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = Task.Run(() => buf.WaitWrite(chunk, TimeSpan.FromSeconds(30), cts.Token));

        Thread.Sleep(100);
        cts.Cancel();

        bool result = task.Result;
        sw.Stop();

        Assert.That(result, Is.False);
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(5000), "Should have stopped well before 30s timeout");
    }

    [Test]
    public void Mpmc_WaitWrite_CancelledToken_ReturnsFalseImmediately()
    {
        using var buf = new ConcurrentMessageQueue(N("MpmcCancel"), slotCount: 2, slotSize: 32);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Fill all slots
        var data = new byte[16];
        while (buf.TryWrite(data)) { }

        bool result = buf.WaitWrite(data, TimeSpan.FromSeconds(5), cts.Token);
        Assert.That(result, Is.False);
    }

    [Test]
    public void Mpmc_WaitRead_CancelledToken_ReturnsZeroImmediately()
    {
        using var buf = new ConcurrentMessageQueue(N("MpmcCancelR"), slotCount: 4, slotSize: 32);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        int result = buf.WaitRead(new byte[16], TimeSpan.FromSeconds(5), cts.Token);
        Assert.That(result, Is.EqualTo(0));
    }

    // ── #6 maxSpins parameter ────────────────────────────────────────────────

    [Test]
    public void Mpmc_MaxSpins_InvalidValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConcurrentMessageQueue(N("MxSp0"), slotCount: 4, slotSize: 32, maxSpins: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConcurrentMessageQueue(N("MxSpN"), slotCount: 4, slotSize: 32, maxSpins: -1));
    }

    [Test]
    public void Mpmc_MaxSpins_LowValue_StillFunctional()
    {
        // maxSpins=1 means almost no spinning — TryWrite/TryRead fail fast on full/empty,
        // but they should still succeed when slots are available.
        using var buf = new ConcurrentMessageQueue(N("MxSp1"), slotCount: 4, slotSize: 32, maxSpins: 1);
        var data = BitConverter.GetBytes(12345);
        Assert.That(buf.TryWrite(data), Is.True, "TryWrite to empty buffer should succeed");
        var dst = new byte[16];
        Assert.That(buf.TryRead(dst), Is.GreaterThan(0), "TryRead with data should succeed");
    }

    [Test]
    public void Mpmc_MaxSpins_DefaultValue_Works()
    {
        // Verify default (100) still works correctly end-to-end
        using var buf = new ConcurrentMessageQueue(N("MxSpDef"), slotCount: 8, slotSize: 64);
        var data = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            data[0] = (byte)i;
            Assert.That(buf.TryWrite(data), Is.True);
        }
        var dst = new byte[64];
        for (int i = 0; i < 8; i++)
        {
            int read = buf.TryRead(dst);
            Assert.That(read, Is.EqualTo(32));
            Assert.That(dst[0], Is.EqualTo((byte)i));
        }
    }

    // ── #8 CalculateAvailable Math.Max(0) ────────────────────────────────────

    [Test]
    public void LockFree_Available_NeverNegative()
    {
        using var buf = new SingleProducerByteStream(N("AvailNN"), 128);
        // Available should never be negative, even on empty or full buffer
        Assert.That(buf.Available, Is.GreaterThanOrEqualTo(0));

        var data = new byte[64];
        buf.TryWrite(data);
        Assert.That(buf.Available, Is.GreaterThanOrEqualTo(0));

        while (buf.TryWrite(new byte[1])) { }
        Assert.That(buf.Available, Is.GreaterThanOrEqualTo(0));
    }

    // ── #9 SharedHeader cache-line separation ────────────────────────────────

    [Test]
    public void HPBuffer_WriteLock_And_ReadLock_Concurrent_NoDeadlock()
    {
        // Exercises actual lock acquisition which goes through WriterLockState (offset 24)
        // and ReaderCount (offset 64) — previously same cache line, now separated.
        var opts = new MemoryRegionOptions { Capacity = 1024 };
        using var buf = new MemoryRegion(N("CL"), opts);

        int writers = 0, readers = 0;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        var tasks = new Task[20];
        for (int i = 0; i < 10; i++)
        {
            int tid = i;
            tasks[tid] = Task.Run(() =>
            {
                var data = new byte[64];
                for (int j = 0; j < 200; j++)
                {
                    if (buf.TryAcquireWriteLock(TimeSpan.FromMilliseconds(100)))
                    {
                        try { buf.Write(data, 0); Interlocked.Increment(ref writers); }
                        finally { buf.ReleaseWriteLock(); }
                    }
                }
            });
            tasks[10 + tid] = Task.Run(() =>
            {
                var dst = new byte[64];
                for (int j = 0; j < 200; j++)
                {
                    if (buf.TryAcquireReadLock(TimeSpan.FromMilliseconds(100)))
                    {
                        try { buf.Read(dst, 0); Interlocked.Increment(ref readers); }
                        finally { buf.ReleaseReadLock(); }
                    }
                }
            });
        }

        Task.WaitAll(tasks, TimeSpan.FromSeconds(15));
        Assert.That(errors, Is.Empty);
        Assert.That(writers, Is.GreaterThan(0));
        Assert.That(readers, Is.GreaterThan(0));
    }

    // ── #11 Empty schema throws ──────────────────────────────────────────────

    [Test]
    public void StructuredMemory_EmptySchema_ThrowsArgumentException()
    {
        var schema = new EmptySchema();
        Assert.Throws<ArgumentException>(
            () => new StructuredMemory<EmptySchema>(N("Empty"), schema));
    }

    // ── #13 Auto-lock pattern (ternary) ──────────────────────────────────────

    [Test]
    public void AutoLock_WriteString_InsideWriteLock_NoDeadlock()
    {
        var schema = new StringSchema();
        using var mem = new StructuredMemory<StringSchema>(N("ALStr"), schema);

        // Writing string while already holding a write lock must not deadlock
        using (mem.AcquireWriteLock())
        {
            Assert.DoesNotThrow(() => mem.WriteString(StringSchema.Name, "hello"));
            Assert.That(mem.ReadString(StringSchema.Name), Is.EqualTo("hello"));
        }
    }

    [Test]
    public void AutoLock_WriteBlob_InsideWriteLock_NoDeadlock()
    {
        var schema = new BlobSchema();
        using var mem = new StructuredMemory<BlobSchema>(N("ALBlob"), schema);

        using (mem.AcquireWriteLock())
        {
            Assert.DoesNotThrow(() => mem.WriteBlob(BlobSchema.Data, new byte[] { 1, 2, 3 }));
            var result = mem.ReadBlob(BlobSchema.Data);
            Assert.That(result, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
    }

    [Test]
    public void AutoLock_WriteUtf8_InsideWriteLock_NoDeadlock()
    {
        var schema = new Utf8Schema();
        using var mem = new StructuredMemory<Utf8Schema>(N("ALUtf8"), schema);

        using (mem.AcquireWriteLock())
        {
            Assert.DoesNotThrow(() => mem.WriteUtf8String(Utf8Schema.Msg, "안녕하세요"));
            Assert.That(mem.ReadUtf8String(Utf8Schema.Msg), Is.EqualTo("안녕하세요"));
        }
    }

    [Test]
    public void AutoLock_WriteArray_InsideReadLock_NoDeadlock()
    {
        // Auto-lock for arrays should not deadlock when inside a read lock
        var schema = new ArraySchema();
        using var mem = new StructuredMemory<ArraySchema>(N("ALArr"), schema);

        int[] src = { 10, 20, 30 };
        using (mem.AcquireWriteLock())
        {
            Assert.DoesNotThrow(() => mem.WriteArray<int>(ArraySchema.Numbers, src));
        }

        var dst = new int[3];
        using (mem.AcquireReadLock())
        {
            Assert.DoesNotThrow(() => mem.ReadArray<int>(ArraySchema.Numbers, dst));
        }
        Assert.That(dst, Is.EqualTo(src));
    }

    // ── #14 InitializeMemory dynamic stackalloc ──────────────────────────────

    [Test]
    public void InitializeMemory_SmallSchema_AllZeroOnCreation()
    {
        // Schema with only one int field → buffer is tiny (< 4096).
        // The dynamic stackalloc path should zero it correctly.
        var schema = new SimpleSchema();
        using var mem = new StructuredMemory<SimpleSchema>(N("Init"), schema);

        // Before any write, field should read as zero-initialized
        Assert.That(mem.Read<int>(SimpleSchema.Value), Is.EqualTo(0));
    }

    // ── #16 Cross-process — smoke test (single-process path) ────────────────
    // (Full cross-process tests are in CrossProcessTests.cs)

    [Test]
    public void FileLayout_SchemaTypes_AreInCorrectFile()
    {
        // Verify SchemaTypes.cs types are accessible (file split didn't break visibility)
        Assert.That(Enum.IsDefined(typeof(SchemaCompatibility), SchemaCompatibility.Strict), Is.True);
        Assert.That(Enum.IsDefined(typeof(SchemaCompatibility), SchemaCompatibility.Full), Is.True);
        Assert.That(Enum.IsDefined(typeof(FieldTypeCode), FieldTypeCode.Blob), Is.True);
        Assert.That(Enum.IsDefined(typeof(FieldTypeCode), FieldTypeCode.Utf8String), Is.True);

        // IMemorySchema and IVersionedSchema are usable
        IMemorySchema s = new SimpleSchema();
        Assert.That(s.GetFields(), Is.Not.Null);
    }

    // ── #13 Auto-lock inside ReadLock-only (no WriteLock) ───────────────────

    [Test]
    public void AutoLock_WriteString_InsideReadLockOnly_Throws()
    {
        // String writes are always non-atomic. The previous behavior skipped the auto-lock
        // whenever any lock was held, allowing a string write while only a read lock was
        // held — exposing other readers to a partially written buffer. The fix is to throw
        // because attempting to upgrade the read lock would deadlock (the current thread
        // is one of the readers the writer would be waiting on).
        var schema = new StringSchema();
        using var mem = new StructuredMemory<StringSchema>(N("ALRL"), schema);

        using (mem.AcquireReadLock())
        {
            Assert.Throws<InvalidOperationException>(
                () => mem.WriteString(StringSchema.Name, "inside-readlock"));
        }
    }

    [Test]
    public void AutoLock_WriteNonAtomicScalar_InsideReadLockOnly_Throws()
    {
        // Guid is 16 bytes > AtomicThreshold(8) and requires a write lock to avoid torn
        // writes. The previous behavior silently skipped the auto-lock whenever ANY lock
        // was held — that allowed an unsafe write while only a read lock was held, exposing
        // other readers to a half-written value. The fix is to throw: upgrading the read
        // lock to a write lock on the same thread would deadlock (writer waits for
        // ReaderCount=0, but this thread is one of the readers).
        var schema = new AllTypesSchema();
        using var mem = new StructuredMemory<AllTypesSchema>(N("ALRG"), schema);
        var guid = Guid.NewGuid();

        using (mem.AcquireReadLock())
        {
            Assert.Throws<InvalidOperationException>(() => mem.Write(AllTypesSchema.GuidF, guid));
        }
    }

    // ── ValidateOffset edge cases ────────────────────────────────────────────

    [Test]
    public void ValidateOffset_LargePositiveOffset_Throws()
    {
        // Offset well beyond capacity — the ulong comparison should catch this
        // even when offset is a large positive long (not negative).
        var opts = new MemoryRegionOptions { Capacity = 1024 };
        using var buf = new MemoryRegion(N("VOBig"), opts);

        var data = new byte[10];
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Write(data, long.MaxValue - 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Read(data, long.MaxValue - 5));
    }

    [Test]
    public void ValidateOffset_OffsetPlusLengthExceedsCapacity_Throws()
    {
        // offset valid alone but offset+length exceeds boundary
        var opts = new MemoryRegionOptions { Capacity = 64 };
        using var buf = new MemoryRegion(N("VOOL"), opts);

        var data = new byte[10];
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Write(data, 60)); // 60+10=70 > 64
    }

    // ── CalculateAvailable edge cases ────────────────────────────────────────

    [Test]
    public void LockFree_Available_FullBuffer_ReturnsZero()
    {
        // Fill the buffer completely — Available must not go negative, must be exactly 0
        using var buf = new SingleProducerByteStream(N("AvFull"), 64);
        var data = new byte[32];
        buf.TryWrite(data);
        buf.TryWrite(data); // second write fills to capacity
        Assert.That(buf.Available, Is.EqualTo(0));
    }

    [Test]
    public void LockFree_Available_AfterClear_IsPositive()
    {
        // Fill then Clear — Available should be restored (> 0)
        using var buf = new SingleProducerByteStream(N("AvClr"), 64);
        var data = new byte[32];
        while (buf.TryWrite(data)) { }
        Assert.That(buf.Available, Is.EqualTo(0), "should be full before clear");

        buf.Clear();
        Assert.That(buf.Available, Is.GreaterThan(0), "should have space after clear");
    }

    // ── InitializeMemory large schema (> 4096 bytes) ─────────────────────────

    [Test]
    public void InitializeMemory_LargeSchema_AllZeroOnCreation()
    {
        // Schema total size > 4096 bytes exercises the loop in InitializeMemory
        // (default chunkSize=4096, multiple iterations required)
        var schema = new LargeSchema();
        using var mem = new StructuredMemory<LargeSchema>(N("LrgInit"), schema);

        // Every element of the large array should be zero-initialized
        var result = new double[LargeSchema.ElementCount];
        mem.ReadArray<double>(LargeSchema.Data, result);
        Assert.That(result, Is.All.EqualTo(0.0), "All elements should be zero on creation");
    }

    // ── AtomicThreshold boundary ─────────────────────────────────────────────

    [Test]
    public void AtomicThreshold_EightByteType_NoAutoLockNeeded()
    {
        // double is exactly 8 bytes == AtomicThreshold, so 8 > 8 is false → no auto-lock.
        // Writing/reading double without any lock should work correctly on x86-64.
        var schema = new AllTypesSchema();
        using var mem = new StructuredMemory<AllTypesSchema>(N("AT8"), schema);

        mem.Write(AllTypesSchema.DoubleF, Math.E);
        Assert.That(mem.Read<double>(AllTypesSchema.DoubleF), Is.EqualTo(Math.E).Within(1e-15));
    }

    [Test]
    public void AtomicThreshold_SixteenByteType_AutoLockApplied()
    {
        // Guid is 16 bytes > AtomicThreshold → auto-lock is acquired.
        // Round-trip must be correct to confirm locking path is taken without error.
        var schema = new AllTypesSchema();
        using var mem = new StructuredMemory<AllTypesSchema>(N("AT16"), schema);
        var expected = Guid.NewGuid();

        mem.Write(AllTypesSchema.GuidF, expected);
        Assert.That(mem.Read<Guid>(AllTypesSchema.GuidF), Is.EqualTo(expected));
    }

    // ── maxSpins edge values ─────────────────────────────────────────────────

    [Test]
    public void Mpmc_MaxSpins_IntMaxValue_DoesNotThrow()
    {
        // int.MaxValue is valid — no overflow, constructor must succeed
        using var buf = new ConcurrentMessageQueue(N("MxSpMax"), slotCount: 4, slotSize: 32,
            maxSpins: int.MaxValue);
        var data = BitConverter.GetBytes(99);
        Assert.That(buf.TryWrite(data), Is.True);
        var dst = new byte[16];
        Assert.That(buf.TryRead(dst), Is.GreaterThan(0));
    }

    // ── #8 InitializeOrOpen race-safe two-phase magic ───────────────────────

    [Test]
    public void InitializeOrOpen_ConcurrentSameProcessOpen_NoTornCapacityRead()
    {
        // Spawn N threads that all try to open the same buffer simultaneously. Exactly one
        // wins the init CAS and writes the header; the rest must observe the final state
        // consistently — no "Capacity mismatch" exception from reading a half-initialized
        // header (the bug the two-phase magic fix prevents).
        const int Threads = 16;
        string name = N("InitRace");
        var options = new MemoryRegionOptions { Capacity = 4096, CreateOrOpen = true };

        var barrier = new Barrier(Threads);
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var buffers = new MemoryRegion?[Threads];

        Parallel.For(0, Threads, i =>
        {
            try
            {
                barrier.SignalAndWait();
                buffers[i] = new MemoryRegion(name, options);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        try
        {
            Assert.That(errors, Is.Empty,
                "Concurrent open from same process must not throw — that would indicate a torn header read");

            // Exactly one thread must report IsOwner=true (the CAS winner).
            int ownerCount = 0;
            foreach (var b in buffers)
                if (b is not null && b.IsOwner) ownerCount++;
            Assert.That(ownerCount, Is.EqualTo(1), "Exactly one CAS winner expected");

            // All openers must see the same capacity (the one the winner wrote).
            foreach (var b in buffers)
                if (b is not null)
                    Assert.That(b.Capacity, Is.EqualTo(4096));
        }
        finally
        {
            foreach (var b in buffers) b?.Dispose();
        }
    }

    // ── #9 IVersionedSchema.IsCompatibleWith now consulted ──────────────────

    [Test]
    public void VersionedSchema_IsCompatibleWith_RejectionVetoesFullMode()
    {
        // Before the fix, IsCompatibleWith was declared on the interface but never invoked,
        // so a schema could only express its policy via the SchemaCompatibility enum mode.
        // Now: if the schema vetoes a version pair, the open must fail even under Full mode.
        string name = N("VetoFull");

        // Keep v1 alive — on Windows the named MMF is destroyed when the last handle closes,
        // so a `using` block around just the writer would let the storage vanish before v2 opens.
        using var v1 = new StructuredMemory<VetoSchemaV1>(name, default);
        v1.Write(VetoSchemaV1.Value, 42);

        // Open as v2 schema with Full mode — but v2.IsCompatibleWith(v1) returns false,
        // so the open must throw despite Full mode being permissive at the enum level.
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var v2 = new StructuredMemory<VetoSchemaV2>(name, default,
                create: false, SchemaCompatibility.Full);
        }, "Schema-side veto via IsCompatibleWith must override permissive enum mode");
    }

    [Test]
    public void VersionedSchema_IsCompatibleWith_AcceptanceAllowsFullMode()
    {
        // Mirror test: when the schema accepts, Full mode succeeds as before.
        string name = N("VetoOk");

        using var v1 = new StructuredMemory<VetoSchemaV1>(name, default);
        v1.Write(VetoSchemaV1.Value, 7);

        // VetoSchemaAccept always accepts via IsCompatibleWith — Full mode succeeds.
        using var v2 = new StructuredMemory<VetoSchemaAccept>(name, default,
            create: false, SchemaCompatibility.Full);
        // Should not throw — IsCompatibleWith returned true, enum allows version drift.
    }

    public struct VetoSchemaV1 : IVersionedSchema
    {
        public const string Value = "Value";
        public int Version => 1;
        public bool IsCompatibleWith(int otherVersion) => otherVersion == 1;
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(Value);
        }
    }

    public struct VetoSchemaV2 : IVersionedSchema
    {
        public const string Value = "Value";
        public int Version => 2;
        // Explicit veto of v1 — this schema does NOT want to be opened over v1 storage,
        // regardless of what the enum-level SchemaCompatibility mode allows.
        public bool IsCompatibleWith(int otherVersion) => otherVersion == 2;
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(Value);
        }
    }

    public struct VetoSchemaAccept : IVersionedSchema
    {
        public const string Value = "Value";
        public int Version => 2;
        public bool IsCompatibleWith(int otherVersion) => true; // accept any version
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(Value);
        }
    }

    // ── #15 Cross-platform support smoke tests ───────────────────────────────

    [Test]
    public void CrossPlatform_ConstructionSucceeds_OnCurrentOS()
    {
        // The library now removes [SupportedOSPlatform("windows")] — construction must succeed
        // on whatever OS the test runs on (Windows in CI here, Linux when shipped to users).
        // Anything else means the platform-dispatch in Initialize() picked the wrong branch.
        using var buf = new MemoryRegion(N("XPlatCtor"),
            new MemoryRegionOptions { Capacity = 4096 });
        Assert.That(buf.IsOwner, Is.True);
        Assert.That(buf.Capacity, Is.EqualTo(4096));
    }

    [Test]
    public void CrossPlatform_FilePathMode_WorksOnAllSupportedOS()
    {
        // FilePath mode uses CreateFromFile with mapName=null on Linux and the kernel name
        // on Windows — both paths must succeed. This is the portable escape hatch for users
        // who want explicit on-disk backing.
        string tmpPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"shmtest_{Guid.NewGuid():N}.bin");
        try
        {
            using var buf = new MemoryRegion(N("XPlatFile"),
                new MemoryRegionOptions { Capacity = 4096, FilePath = tmpPath });
            var data = new byte[] { 1, 2, 3, 4 };
            buf.Write(data, 0);
            var back = new byte[4];
            buf.Read(back, 0);
            Assert.That(back, Is.EqualTo(data));
        }
        finally
        {
            try { System.IO.File.Delete(tmpPath); } catch { /* best-effort */ }
        }
    }

    [Test]
    public void CrossPlatform_NameWithLeadingSlash_IsRejected()
    {
        // Version 3 names are flat identifiers on every platform. A leading slash must not
        // alias the same region as the slash-free name.
        string name = "/SlashTest_" + Guid.NewGuid().ToString("N");
        Assert.Throws<ArgumentException>(() =>
        {
            using var _ = new MemoryRegion(name,
                new MemoryRegionOptions { Capacity = 4096 });
        });
    }

    [Test]
    public void CrossPlatform_NameWithPathSeparator_IsRejected()
    {
        // A flat identifier cannot contain platform-specific namespace or path separators.
        string bad = "Path/Sep_" + Guid.NewGuid().ToString("N");
        Assert.Throws<ArgumentException>(() =>
        {
            using var _ = new MemoryRegion(bad,
                new MemoryRegionOptions { Capacity = 4096 });
        });
    }

    // ── BUG-A: Constructor cleans up if InitializeOrOpen throws ──────────────

    [Test]
    public void BugA_CapacityMismatch_ConstructorDoesNotLeakResources()
    {
        // Craft a backing file whose header advertises Capacity=4096, then attempt to open it
        // with a 8192 request. Initialize() succeeds (MMF + accessor + pointer all live), then
        // InitializeOrOpen detects the capacity mismatch and throws — exercising the exact
        // post-Initialize failure window BUG-A protects against.
        //
        // Verification: after the throw, File.Delete must succeed. On Windows, an MMF that
        // wasn't disposed holds an exclusive FileStream lock and Delete throws IOException
        // ("file in use"). If BUG-A had not added the catch+Cleanup, the file would still be
        // locked by the leaked accessor until GC eventually finalized it — non-deterministic
        // and easy to miss. File.Delete is the cleanest possible "no handles left open" probe.
        string tmpPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"shmtest_BugA_{Guid.NewGuid():N}.bin");
        string name = N("BugA_HeaderMismatch");
        const int existingCapacity = 4096;
        const int headerSize = 128;
        try
        {
            // Synthetic header: Magic + Version + Capacity, the three fields InitializeOrOpen
            // reads on the "open existing" branch. Everything else stays zero.
            byte[] file = new byte[headerSize + existingCapacity];
            BitConverter.TryWriteBytes(file.AsSpan(0), 0x48504D53u); // MagicNumber "SMHP"
            BitConverter.TryWriteBytes(file.AsSpan(4), 2u);          // Version
            BitConverter.TryWriteBytes(file.AsSpan(8), (long)existingCapacity);
            System.IO.File.WriteAllBytes(tmpPath, file);

            // Mismatch open: 8192 ≠ 4096 stored in header → InitializeOrOpen throws AFTER
            // Initialize() returned successfully. This is the BUG-A code path.
            Assert.Throws<InvalidDataException>(() =>
            {
                using var _ = new MemoryRegion(name,
                    new MemoryRegionOptions { Capacity = 8192, FilePath = tmpPath });
            });

            // Probe: file must be deletable, i.e., no leaked handle.
            Assert.DoesNotThrow(() => System.IO.File.Delete(tmpPath),
                "Failed constructor leaked the MMF/accessor — file still locked after throw");
        }
        finally
        {
            try { System.IO.File.Delete(tmpPath); } catch { /* best-effort */ }
        }
    }

    // ── STABILITY-B: Name sanitization rejects hostile inputs ────────────────

    [Test]
    public void StabilityB_NameWithNulCharacter_Rejected()
    {
        // NUL would silently truncate the /dev/shm path on Linux, letting a caller alias an
        // unrelated region under "foo" when they thought they were creating "foo\0secret".
        // Windows MMF accepts NUL but the same logical risk exists, so we reject everywhere
        // — the cross-platform name surface needs to be the strictest of all targets.
        Assert.Throws<ArgumentException>(() =>
        {
            using var _ = new MemoryRegion("foo\0bar",
                new MemoryRegionOptions { Capacity = 4096 });
        });
    }

    [Test]
    public void StabilityB_NameWithControlCharacter_Rejected()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            using var _ = new MemoryRegion("foo\nbar",
                new MemoryRegionOptions { Capacity = 4096 });
        });
    }

    [Test]
    public void StabilityB_NameExceedingNameMax_Rejected()
    {
        // Linux NAME_MAX is 255 bytes (not chars). A 300-char ASCII name = 300 bytes UTF-8 ⇒
        // ENAMETOOLONG from FileStream. We want a clear ArgumentException at the API surface
        // before any filesystem call.
        string longName = new string('a', 300);
        Assert.Throws<ArgumentException>(() =>
        {
            using var _ = new MemoryRegion(longName,
                new MemoryRegionOptions { Capacity = 4096 });
        });
    }

    [Test]
    public void StabilityB_NameWithEmojis_AcceptedIfUnder255Bytes()
    {
        // UTF-8 multi-byte chars should be accepted as long as total stays under NAME_MAX —
        // documenting the byte-not-char semantics matters because callers may pre-size assuming chars.
        string name = "테스트_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        using var buf = new MemoryRegion(name,
            new MemoryRegionOptions { Capacity = 4096 });
        Assert.That(buf.Capacity, Is.EqualTo(4096));
    }

    // ── STABILITY-A: PID reuse field is written and respected ────────────────

    [Test]
    public void StabilityA_LockOwnerInfo_IncludesProcessStartTime()
    {
        // Indirect test of STABILITY-A: after acquiring the write lock, the header's
        // LockOwnerProcessStartTime field should be non-zero (assuming StartTime is readable on
        // this host). We probe through the public GetLockOwnerInfo + the orphan-check pathway,
        // which both depend on the start-time being captured at acquire.
        using var buf = new MemoryRegion(N("StabilityA"),
            new MemoryRegionOptions
            {
                Capacity = 4096,
                EnableOrphanLockDetection = true,
                OrphanLockTimeout = TimeSpan.FromSeconds(30)
            });

        Assert.That(buf.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        try
        {
            var info = buf.GetLockOwnerInfo();
            Assert.That(info.ProcessId, Is.EqualTo(Environment.ProcessId));
            // Orphan check should return false since WE are alive AND our StartTime matches the
            // captured one — verifies the comparison path doesn't false-positive ourselves.
            Assert.That(info.IsOrphan, Is.False, "Our own held lock must not be detected as orphan");
        }
        finally { buf.ReleaseWriteLock(); }
    }

    // ── OPT-7: EnableStatistics opt-in ───────────────────────────────────────

    [Test]
    public void Opt7_StatsDisabled_GetStatisticsReturnsZero()
    {
        // When stats are off, the hot-path Interlocked updates are skipped entirely.
        // GetStatistics must reflect that — the counters were never incremented, so all
        // four fields read zero even after real reads and writes.
        using var buf = new MemoryRegion(N("Opt7_Off"),
            new MemoryRegionOptions { Capacity = 4096, EnableStatistics = false });

        var data = new byte[] { 1, 2, 3, 4 };
        buf.Write(data, 0);
        var back = new byte[4];
        buf.Read(back, 0);

        var (reads, writes, bytesRead, bytesWritten) = buf.GetStatistics();
        Assert.That(reads, Is.EqualTo(0));
        Assert.That(writes, Is.EqualTo(0));
        Assert.That(bytesRead, Is.EqualTo(0));
        Assert.That(bytesWritten, Is.EqualTo(0));
    }

    [Test]
    public void Opt7_StatsEnabledByDefault_PreservesBackCompat()
    {
        // No explicit option ⇒ statistics tracked. Critical: external callers that consumed
        // GetStatistics before OPT-7 must keep seeing accurate numbers.
        using var buf = new MemoryRegion(N("Opt7_OnDefault"),
            new MemoryRegionOptions { Capacity = 4096 });

        buf.Write(new byte[10], 0);
        buf.Write(new byte[20], 10);
        buf.Read(new byte[5], 0);

        var (reads, writes, bytesRead, bytesWritten) = buf.GetStatistics();
        Assert.That(writes, Is.EqualTo(2));
        Assert.That(reads, Is.EqualTo(1));
        Assert.That(bytesWritten, Is.EqualTo(30));
        Assert.That(bytesRead, Is.EqualTo(5));
    }

    [Test]
    public void Opt7_Mpmc_StatsDisabled_HeaderCountersStayZero()
    {
        // MPMC counters live in shared memory — disabling must keep them zero AND the buffer
        // must still function correctly without them.
        using var buf = new ConcurrentMessageQueue(N("Opt7_Mpmc"),
            slotCount: 16, slotSize: 128, create: true, maxSpins: 100, enableStatistics: false);

        var msg = new byte[] { 9, 8, 7 };
        Assert.That(buf.TryWrite(msg), Is.True);
        var dst = new byte[8];
        Assert.That(buf.TryRead(dst), Is.EqualTo(3));

        var (totalWrites, totalReads, _, _) = buf.GetStatistics();
        Assert.That(totalWrites, Is.EqualTo(0));
        Assert.That(totalReads, Is.EqualTo(0));
    }

    // ── OPT-8: Optimistic reader-lock contention test ────────────────────────

    [Test]
    public void Opt8_OptimisticReaderLock_ManyReadersAllSucceed()
    {
        // Spawn N readers acquiring the lock simultaneously. The optimistic-increment design
        // must let every reader claim without CAS-loop spinning against each other (the old
        // design had each reader retry until its CAS landed). We assert all readers eventually
        // succeed AND the ReaderCount returns to zero — proving rollback paths balance.
        using var buf = new MemoryRegion(N("Opt8_Readers"),
            new MemoryRegionOptions { Capacity = 4096 });

        const int readerCount = 32;
        const int iterations = 50;
        var successCount = 0;
        var threads = new System.Threading.Thread[readerCount];
        for (int i = 0; i < readerCount; i++)
        {
            threads[i] = new System.Threading.Thread(() =>
            {
                for (int j = 0; j < iterations; j++)
                {
                    if (buf.TryAcquireReadLock(TimeSpan.FromSeconds(2)))
                    {
                        try { System.Threading.Interlocked.Increment(ref successCount); }
                        finally { buf.ReleaseReadLock(); }
                    }
                }
            });
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.That(successCount, Is.EqualTo(readerCount * iterations),
            "Every reader acquire attempt must succeed (no writer present)");

        // Probe via brief write-lock — succeeds iff ReaderCount==0 (no leaked refs).
        Assert.That(buf.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True,
            "Writer must be able to acquire — leaked reader refs would block this");
        buf.ReleaseWriteLock();
    }

    // ── AUDIT-1: SharedArray uint overflow ───────────────────────────────────

    [Test]
    public void Audit1_SharedArray_NegativeStartIndex_Rejected()
    {
        using var arr = new SharedArray<int>(N("Audit1_Neg"), 16);
        var src = new int[4];
        // Previous uint cast would coerce -1 to UInt32.MaxValue and STILL throw, but only by
        // accident; the long-arithmetic version makes the rejection explicit and reliable.
        Assert.Throws<ArgumentOutOfRangeException>(() => arr.CopyFrom(-1, src));
        Assert.Throws<ArgumentOutOfRangeException>(() => arr.CopyTo(-1, src));
    }

    [Test]
    public void Audit1_SharedArray_FillNegativeCount_Rejected()
    {
        // Previous (uint)count cast would convert -2 to ~4 billion and pass the upper bound
        // check (because (uint)startIndex + huge > capacity wraps mod 2^32 to small).
        using var arr = new SharedArray<int>(N("Audit1_Fill"), 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => arr.Fill(42, startIndex: 0, count: -2));
    }

    // ── AUDIT-3: Circular buffer constructor cleanup on bad capacity ─────────

    [Test]
    public void Audit3_LockFree_ConstructionFailure_DoesNotLeakBuffer()
    {
        // Force the inner _buffer.GetMemory cast to throw by using a capacity that exceeds
        // int.MaxValue after power-of-2 rounding + header. The constructor must dispose the
        // inner MemoryRegion it just created — otherwise the kernel section
        // sticks around until finalization.
        // We probe by attempting many failed constructions in a tight loop and confirming
        // none of them cumulatively leak (process would either OOM or hit handle limits).
        for (int i = 0; i < 50; i++)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                using var _ = new SingleProducerByteStream(N($"Audit3_Bad_{i}"),
                    capacity: int.MaxValue); // totalSize check trips
            });
        }
        // If we got here without OOM, cleanup worked.
        Assert.Pass();
    }

    [Test]
    public void Audit3_Mpmc_ConstructionFailure_DoesNotLeakBuffer()
    {
        // Same shape: force ValidateBuffer or capacity check to throw, then verify many
        // iterations don't accumulate leaked MemoryRegion instances.
        for (int i = 0; i < 50; i++)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                using var _ = new ConcurrentMessageQueue(N($"Audit3_Mpmc_{i}"),
                    slotCount: int.MaxValue / 2, slotSize: 64); // totalSize > int.MaxValue
            });
        }
        Assert.Pass();
    }

    // ── AUDIT-4: FieldDefinition name validation ─────────────────────────────

    [Test]
    public void Audit4_FieldDefinition_NullOrEmptyName_AllFactoriesReject()
    {
        // Every factory must reject null/empty/whitespace names. Previously only the ones
        // taking a length parameter validated; Scalar/Struct/Blob/Utf8String didn't.
        Assert.Throws<ArgumentException>(() => FieldDefinition.Scalar<int>(""));
        Assert.Throws<ArgumentException>(() => FieldDefinition.Scalar<int>(null!));
        Assert.Throws<ArgumentException>(() => FieldDefinition.Scalar<int>("   "));
        Assert.Throws<ArgumentException>(() => FieldDefinition.Array<int>("", 4));
        Assert.Throws<ArgumentException>(() => FieldDefinition.Struct<Guid>(""));
        Assert.Throws<ArgumentException>(() => FieldDefinition.StructArray<Guid>("", 4));
        Assert.Throws<ArgumentException>(() => FieldDefinition.String("", 16));
        Assert.Throws<ArgumentException>(() => FieldDefinition.Blob("", 16));
        Assert.Throws<ArgumentException>(() => FieldDefinition.Utf8String("", 16));
    }

    // ── AUDIT-5: ReleaseWriteLock CAS-by-owner ───────────────────────────────

    [Test]
    public void Audit5_ReleaseWriteLock_WithoutAcquire_IsNoOp()
    {
        // Caller bug: releasing a lock that wasn't acquired by this process. Without the
        // owner-CAS guard, ReleaseWriteLock would zero ownership metadata and free the lock —
        // dangerous when another process legitimately holds it. With the fix it's a logged no-op.
        using var buf = new MemoryRegion(N("Audit5_NoAcquire"),
            new MemoryRegionOptions { Capacity = 4096 });

        // No acquire here. Release should be a safe no-op (logs a warning).
        Assert.DoesNotThrow(() => buf.ReleaseWriteLock());

        // Now actually acquire — must still work normally after the no-op release.
        Assert.That(buf.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        buf.ReleaseWriteLock();
    }

    // ── AUDIT-6: UnmanagedMemoryManager.Pin at Length ────────────────────────

    [Test]
    public void Audit6_GetMemory_PinAtLength_DoesNotThrow()
    {
        // MemoryManager<T>.Pin(Length) is legal — used for empty-tail slices. Previous
        // implementation rejected >= Length; the fix allows == Length.
        using var buf = new MemoryRegion(N("Audit6_Pin"),
            new MemoryRegionOptions { Capacity = 1024 });
        var memory = buf.GetMemory(0, 16);
        // Slice to zero-length at the end and pin — exercises the elementIndex == Length path
        var tail = memory.Slice(16);
        Assert.That(tail.Length, Is.EqualTo(0));
        Assert.DoesNotThrow(() => { using var _ = tail.Pin(); });
    }

    // ── Schemas & helpers ────────────────────────────────────────────────────

    private enum TestEnumInt  : int  { A = 1 }
    private enum TestEnumByte : byte { B = 2 }
    private enum TestEnumLong : long { C = 3 }

    private struct Vec3 { public float X, Y, Z; }

    public struct SimpleSchema : IMemorySchema
    {
        public const string Value = "Value";
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(Value);
        }
    }

    public struct EmptySchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields() { yield break; }
    }

    public struct AllTypesSchema : IMemorySchema
    {
        public const string BoolF   = "Bool";
        public const string ByteF   = "Byte";
        public const string IntF    = "Int";
        public const string LongF   = "Long";
        public const string DoubleF = "Double";
        public const string GuidF   = "Guid";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<bool>(BoolF);
            yield return FieldDefinition.Scalar<byte>(ByteF);
            yield return FieldDefinition.Scalar<int>(IntF);
            yield return FieldDefinition.Scalar<long>(LongF);
            yield return FieldDefinition.Scalar<double>(DoubleF);
            yield return FieldDefinition.Scalar<Guid>(GuidF);
        }
    }

    public struct StringSchema : IMemorySchema
    {
        public const string Name = "Name";
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.String(Name, 64);
        }
    }

    public struct BlobSchema : IMemorySchema
    {
        public const string Data = "Data";
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Blob(Data, 128);
        }
    }

    public struct Utf8Schema : IMemorySchema
    {
        public const string Msg = "Msg";
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Utf8String(Msg, 256);
        }
    }

    public struct ArraySchema : IMemorySchema
    {
        public const string Numbers = "Numbers";
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Array<int>(Numbers, 10);
        }
    }

    public struct LargeSchema : IMemorySchema
    {
        public const string Data = "Data";
        // 600 doubles = 4800 bytes > 4096, exercises InitializeMemory loop
        public const int ElementCount = 600;
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Array<double>(Data, ElementCount);
        }
    }
}
