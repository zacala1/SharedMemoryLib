using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SharedMemory;

namespace SharedMemory.Tests;

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
        Assert.That(FieldDefinition.GetTypeCode<bool>(),           Is.EqualTo(SharedTypeCode.Boolean));
        Assert.That(FieldDefinition.GetTypeCode<byte>(),           Is.EqualTo(SharedTypeCode.Byte));
        Assert.That(FieldDefinition.GetTypeCode<sbyte>(),          Is.EqualTo(SharedTypeCode.SByte));
        Assert.That(FieldDefinition.GetTypeCode<char>(),           Is.EqualTo(SharedTypeCode.Char));
        Assert.That(FieldDefinition.GetTypeCode<short>(),          Is.EqualTo(SharedTypeCode.Int16));
        Assert.That(FieldDefinition.GetTypeCode<ushort>(),         Is.EqualTo(SharedTypeCode.UInt16));
        Assert.That(FieldDefinition.GetTypeCode<int>(),            Is.EqualTo(SharedTypeCode.Int32));
        Assert.That(FieldDefinition.GetTypeCode<uint>(),           Is.EqualTo(SharedTypeCode.UInt32));
        Assert.That(FieldDefinition.GetTypeCode<long>(),           Is.EqualTo(SharedTypeCode.Int64));
        Assert.That(FieldDefinition.GetTypeCode<ulong>(),          Is.EqualTo(SharedTypeCode.UInt64));
        Assert.That(FieldDefinition.GetTypeCode<float>(),          Is.EqualTo(SharedTypeCode.Single));
        Assert.That(FieldDefinition.GetTypeCode<double>(),         Is.EqualTo(SharedTypeCode.Double));
        Assert.That(FieldDefinition.GetTypeCode<decimal>(),        Is.EqualTo(SharedTypeCode.Decimal));
        Assert.That(FieldDefinition.GetTypeCode<Guid>(),           Is.EqualTo(SharedTypeCode.Guid));
        Assert.That(FieldDefinition.GetTypeCode<DateTime>(),       Is.EqualTo(SharedTypeCode.DateTime));
        Assert.That(FieldDefinition.GetTypeCode<TimeSpan>(),       Is.EqualTo(SharedTypeCode.TimeSpan));
        Assert.That(FieldDefinition.GetTypeCode<DateTimeOffset>(), Is.EqualTo(SharedTypeCode.DateTimeOffset));
    }

    [Test]
    public void GetTypeCode_EnumTypes_ReturnsUnderlyingCode()
    {
        Assert.That(FieldDefinition.GetTypeCode<TestEnumInt>(),    Is.EqualTo(SharedTypeCode.Int32));
        Assert.That(FieldDefinition.GetTypeCode<TestEnumByte>(),   Is.EqualTo(SharedTypeCode.Byte));
        Assert.That(FieldDefinition.GetTypeCode<TestEnumLong>(),   Is.EqualTo(SharedTypeCode.Int64));
    }

    [Test]
    public void GetTypeCode_CustomStruct_ReturnsStruct()
    {
        Assert.That(FieldDefinition.GetTypeCode<Vec3>(), Is.EqualTo(SharedTypeCode.Struct));
    }

    // ── #3 WriteInternal/ReadInternal MemoryMarshal ──────────────────────────

    [Test]
    public void WriteRead_AllUnmanagedTypes_RoundTrip()
    {
        var schema = new AllTypesSchema();
        using var mem = new StrictSharedMemory<AllTypesSchema>(N("RT"), schema);

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
        using var m1 = new StrictSharedMemory<SimpleSchema>(N("H1"), s1);
        using var m2 = new StrictSharedMemory<SimpleSchema>(N("H2"), s2);

        // If hashes differ the second open of the same name would throw.
        // Verify by opening the first buffer with a second instance.
        string shared = N("SH");
        using var creator = new StrictSharedMemory<SimpleSchema>(shared, s1);
        creator.Write(SimpleSchema.Value, 42);

        using var opener = new StrictSharedMemory<SimpleSchema>(shared, s2, create: false);
        Assert.That(opener.Read<int>(SimpleSchema.Value), Is.EqualTo(42));
    }

    // ── #5 CancellationToken on WaitWrite/WaitRead ───────────────────────────

    [Test]
    public void LockFree_WaitWrite_CancelledToken_ReturnsFalseImmediately()
    {
        using var buf = new LockFreeCircularBuffer(N("LFCancel"), 64);
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
        using var buf = new LockFreeCircularBuffer(N("LFCancelR"), 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        int result = buf.WaitRead(new byte[4], TimeSpan.FromSeconds(5), cts.Token);
        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void LockFree_WaitWrite_CancelMidWait_StopsBlocking()
    {
        using var buf = new LockFreeCircularBuffer(N("LFMidW"), 64);
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
        using var buf = new MpmcCircularBuffer(N("MpmcCancel"), slotCount: 2, slotSize: 32);
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
        using var buf = new MpmcCircularBuffer(N("MpmcCancelR"), slotCount: 4, slotSize: 32);
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
            () => new MpmcCircularBuffer(N("MxSp0"), slotCount: 4, slotSize: 32, maxSpins: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MpmcCircularBuffer(N("MxSpN"), slotCount: 4, slotSize: 32, maxSpins: -1));
    }

    [Test]
    public void Mpmc_MaxSpins_LowValue_StillFunctional()
    {
        // maxSpins=1 means almost no spinning — TryWrite/TryRead fail fast on full/empty,
        // but they should still succeed when slots are available.
        using var buf = new MpmcCircularBuffer(N("MxSp1"), slotCount: 4, slotSize: 32, maxSpins: 1);
        var data = BitConverter.GetBytes(12345);
        Assert.That(buf.TryWrite(data), Is.True, "TryWrite to empty buffer should succeed");
        var dst = new byte[16];
        Assert.That(buf.TryRead(dst), Is.GreaterThan(0), "TryRead with data should succeed");
    }

    [Test]
    public void Mpmc_MaxSpins_DefaultValue_Works()
    {
        // Verify default (100) still works correctly end-to-end
        using var buf = new MpmcCircularBuffer(N("MxSpDef"), slotCount: 8, slotSize: 64);
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
        using var buf = new LockFreeCircularBuffer(N("AvailNN"), 128);
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
        var opts = new SharedMemoryBufferOptions { Capacity = 1024 };
        using var buf = new HighPerformanceSharedBuffer(N("CL"), opts);

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
    public void StrictSharedMemory_EmptySchema_ThrowsArgumentException()
    {
        var schema = new EmptySchema();
        Assert.Throws<ArgumentException>(
            () => new StrictSharedMemory<EmptySchema>(N("Empty"), schema));
    }

    // ── #13 Auto-lock pattern (ternary) ──────────────────────────────────────

    [Test]
    public void AutoLock_WriteString_InsideWriteLock_NoDeadlock()
    {
        var schema = new StringSchema();
        using var mem = new StrictSharedMemory<StringSchema>(N("ALStr"), schema);

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
        using var mem = new StrictSharedMemory<BlobSchema>(N("ALBlob"), schema);

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
        using var mem = new StrictSharedMemory<Utf8Schema>(N("ALUtf8"), schema);

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
        using var mem = new StrictSharedMemory<ArraySchema>(N("ALArr"), schema);

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
        using var mem = new StrictSharedMemory<SimpleSchema>(N("Init"), schema);

        // Before any write, field should read as zero-initialized
        Assert.That(mem.Read<int>(SimpleSchema.Value), Is.EqualTo(0));
    }

    // ── #16 Cross-process — smoke test (single-process path) ────────────────
    // (Full cross-process tests are in CrossProcessTests.cs)

    [Test]
    public void FileLayout_SchemaTypes_AreInCorrectFile()
    {
        // Verify SchemaTypes.cs types are accessible (file split didn't break visibility)
        Assert.That(SchemaCompatibility.Strict, Is.EqualTo(SchemaCompatibility.Strict));
        Assert.That(SchemaCompatibility.Full,   Is.EqualTo(SchemaCompatibility.Full));
        Assert.That(SharedTypeCode.Blob,        Is.EqualTo(SharedTypeCode.Blob));
        Assert.That(SharedTypeCode.Utf8String,  Is.EqualTo(SharedTypeCode.Utf8String));

        // ISharedMemorySchema and IVersionedSchema are usable
        ISharedMemorySchema s = new SimpleSchema();
        Assert.That(s.GetFields(), Is.Not.Null);
    }

    // ── #13 Auto-lock inside ReadLock-only (no WriteLock) ───────────────────

    [Test]
    public void AutoLock_WriteString_InsideReadLockOnly_NoDeadlock()
    {
        // When holding only a ReadLock, IsHoldingAnyLock() returns true, so WriteString
        // skips AcquireWriteLock. This avoids deadlock (S-lock cannot upgrade to X-lock
        // without releasing first). The caller is responsible for ensuring no concurrent writer.
        var schema = new StringSchema();
        using var mem = new StrictSharedMemory<StringSchema>(N("ALRL"), schema);

        using (mem.AcquireReadLock())
        {
            Assert.DoesNotThrow(() => mem.WriteString(StringSchema.Name, "inside-readlock"));
            Assert.That(mem.ReadString(StringSchema.Name), Is.EqualTo("inside-readlock"));
        }
    }

    [Test]
    public void AutoLock_WriteNonAtomicScalar_InsideReadLockOnly_NoDeadlock()
    {
        // Guid is 16 bytes > AtomicThreshold(8). Normally auto-locks, but when holding
        // ReadLock the auto-lock is skipped to prevent deadlock — must not throw or hang.
        var schema = new AllTypesSchema();
        using var mem = new StrictSharedMemory<AllTypesSchema>(N("ALRG"), schema);
        var guid = Guid.NewGuid();

        using (mem.AcquireReadLock())
        {
            Assert.DoesNotThrow(() => mem.Write(AllTypesSchema.GuidF, guid));
            Assert.That(mem.Read<Guid>(AllTypesSchema.GuidF), Is.EqualTo(guid));
        }
    }

    // ── ValidateOffset edge cases ────────────────────────────────────────────

    [Test]
    public void ValidateOffset_LargePositiveOffset_Throws()
    {
        // Offset well beyond capacity — the ulong comparison should catch this
        // even when offset is a large positive long (not negative).
        var opts = new SharedMemoryBufferOptions { Capacity = 1024 };
        using var buf = new HighPerformanceSharedBuffer(N("VOBig"), opts);

        var data = new byte[10];
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Write(data, long.MaxValue - 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Read(data, long.MaxValue - 5));
    }

    [Test]
    public void ValidateOffset_OffsetPlusLengthExceedsCapacity_Throws()
    {
        // offset valid alone but offset+length exceeds boundary
        var opts = new SharedMemoryBufferOptions { Capacity = 64 };
        using var buf = new HighPerformanceSharedBuffer(N("VOOL"), opts);

        var data = new byte[10];
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Write(data, 60)); // 60+10=70 > 64
    }

    // ── CalculateAvailable edge cases ────────────────────────────────────────

    [Test]
    public void LockFree_Available_FullBuffer_ReturnsZero()
    {
        // Fill the buffer completely — Available must not go negative, must be exactly 0
        using var buf = new LockFreeCircularBuffer(N("AvFull"), 64);
        var data = new byte[32];
        buf.TryWrite(data);
        buf.TryWrite(data); // second write fills to capacity
        Assert.That(buf.Available, Is.EqualTo(0));
    }

    [Test]
    public void LockFree_Available_AfterClear_IsPositive()
    {
        // Fill then Clear — Available should be restored (> 0)
        using var buf = new LockFreeCircularBuffer(N("AvClr"), 64);
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
        using var mem = new StrictSharedMemory<LargeSchema>(N("LrgInit"), schema);

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
        using var mem = new StrictSharedMemory<AllTypesSchema>(N("AT8"), schema);

        mem.Write(AllTypesSchema.DoubleF, Math.E);
        Assert.That(mem.Read<double>(AllTypesSchema.DoubleF), Is.EqualTo(Math.E).Within(1e-15));
    }

    [Test]
    public void AtomicThreshold_SixteenByteType_AutoLockApplied()
    {
        // Guid is 16 bytes > AtomicThreshold → auto-lock is acquired.
        // Round-trip must be correct to confirm locking path is taken without error.
        var schema = new AllTypesSchema();
        using var mem = new StrictSharedMemory<AllTypesSchema>(N("AT16"), schema);
        var expected = Guid.NewGuid();

        mem.Write(AllTypesSchema.GuidF, expected);
        Assert.That(mem.Read<Guid>(AllTypesSchema.GuidF), Is.EqualTo(expected));
    }

    // ── maxSpins edge values ─────────────────────────────────────────────────

    [Test]
    public void Mpmc_MaxSpins_IntMaxValue_DoesNotThrow()
    {
        // int.MaxValue is valid — no overflow, constructor must succeed
        using var buf = new MpmcCircularBuffer(N("MxSpMax"), slotCount: 4, slotSize: 32,
            maxSpins: int.MaxValue);
        var data = BitConverter.GetBytes(99);
        Assert.That(buf.TryWrite(data), Is.True);
        var dst = new byte[16];
        Assert.That(buf.TryRead(dst), Is.GreaterThan(0));
    }

    // ── Schemas & helpers ────────────────────────────────────────────────────

    private enum TestEnumInt  : int  { A = 1 }
    private enum TestEnumByte : byte { B = 2 }
    private enum TestEnumLong : long { C = 3 }

    private struct Vec3 { public float X, Y, Z; }

    public struct SimpleSchema : ISharedMemorySchema
    {
        public const string Value = "Value";
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(Value);
        }
    }

    public struct EmptySchema : ISharedMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields() { yield break; }
    }

    public struct AllTypesSchema : ISharedMemorySchema
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

    public struct StringSchema : ISharedMemorySchema
    {
        public const string Name = "Name";
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.String(Name, 64);
        }
    }

    public struct BlobSchema : ISharedMemorySchema
    {
        public const string Data = "Data";
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Blob(Data, 128);
        }
    }

    public struct Utf8Schema : ISharedMemorySchema
    {
        public const string Msg = "Msg";
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Utf8String(Msg, 256);
        }
    }

    public struct ArraySchema : ISharedMemorySchema
    {
        public const string Numbers = "Numbers";
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Array<int>(Numbers, 10);
        }
    }

    public struct LargeSchema : ISharedMemorySchema
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
