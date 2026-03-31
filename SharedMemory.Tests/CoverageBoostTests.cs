using NUnit.Framework;
using SharedMemory;

namespace SharedMemory.Tests;

/// <summary>
/// Tests to boost code coverage for uncovered branches and paths.
/// </summary>
[TestFixture]
public class CoverageBoostTests
{
    [TearDown]
    public void Cleanup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    #region SharedMemoryBufferOptions.Validate Coverage

    [Test]
    public void Validate_NegativeCapacity_ShouldThrow()
    {
        var options = new SharedMemoryBufferOptions { Capacity = -1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Test]
    public void Validate_ZeroCapacity_ShouldThrow()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Test]
    public void Validate_NegativeLockTimeout_ShouldThrow()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            LockTimeout = TimeSpan.FromSeconds(-1)
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Test]
    public void Validate_InfiniteLockTimeout_ShouldSucceed()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            LockTimeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        Assert.DoesNotThrow(() => options.Validate());
    }

    [Test]
    public void Validate_NonPowerOf2Alignment_ShouldThrow()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            Alignment = 3 // not power of 2
        };
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Test]
    public void Validate_ZeroAlignment_ShouldThrow()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            Alignment = 0
        };
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Test]
    public void Validate_NegativeOrphanLockTimeout_ShouldThrow()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            OrphanLockTimeout = TimeSpan.FromSeconds(-1)
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Test]
    public void Validate_ValidDefaults_ShouldSucceed()
    {
        var options = new SharedMemoryBufferOptions();
        Assert.DoesNotThrow(() => options.Validate());
    }

    #endregion

    #region Reentrant Lock Coverage (new code)

    [Test]
    public void WriteLock_Reentrant_ShouldNotDeadlock()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_Reentrant_Write", schema);

        using (var outerLock = memory.AcquireWriteLock())
        {
            // This should NOT deadlock — reentrant path
            using (var innerLock = memory.AcquireWriteLock())
            {
                memory.Write(ReentrantTestSchema.IntField, 42);
            }

            // Outer lock should still be valid
            memory.Write(ReentrantTestSchema.IntField, 99);
        }

        Assert.That(memory.Read<int>(ReentrantTestSchema.IntField), Is.EqualTo(99));
    }

    [Test]
    public void ReadLock_Reentrant_ShouldNotDeadlock()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_Reentrant_Read", schema);

        memory.Write(ReentrantTestSchema.IntField, 123);

        using (var outerLock = memory.AcquireReadLock())
        {
            // Reentrant read lock
            using (var innerLock = memory.AcquireReadLock())
            {
                var value = memory.Read<int>(ReentrantTestSchema.IntField);
                Assert.That(value, Is.EqualTo(123));
            }
        }
    }

    [Test]
    public void ReadLock_UnderWriteLock_ShouldNotDeadlock()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_ReadUnderWrite", schema);

        memory.Write(ReentrantTestSchema.IntField, 555);

        using (var writeLock = memory.AcquireWriteLock())
        {
            // Read lock inside write lock — reentrant path
            using (var readLock = memory.AcquireReadLock())
            {
                var value = memory.Read<int>(ReentrantTestSchema.IntField);
                Assert.That(value, Is.EqualTo(555));
            }
        }
    }

    #endregion

    #region Capacity Overflow Coverage (new code)

    [Test]
    public void LockFreeCircularBuffer_HugeCapacity_ShouldThrow()
    {
        // After power-of-2 rounding + header, total exceeds int.MaxValue
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LockFreeCircularBuffer("CovBoost_Overflow_SPSC", int.MaxValue));
    }

    [Test]
    public void MpmcCircularBuffer_HugeSlotCount_ShouldThrow()
    {
        // Large slotCount * slotSize will exceed int.MaxValue
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MpmcCircularBuffer("CovBoost_Overflow_MPMC", int.MaxValue / 2, 64));
    }

    #endregion

    #region Event Handler Exception Safety Coverage

    [Test]
    public void OnDataWritten_HandlerThrows_ShouldNotAffectWrite()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            EnableEvents = true
        };
        using var buffer = new HighPerformanceSharedBuffer("CovBoost_EventException", options);

        // Subscribe a handler that throws
        buffer.OnDataWritten += (sender, args) => throw new InvalidOperationException("Handler error");

        var testData = new byte[] { 1, 2, 3, 4, 5 };

        // Write should succeed despite handler exception
        Assert.DoesNotThrow(() => buffer.Write(testData, 0));

        var readBuffer = new byte[5];
        buffer.Read(readBuffer, 0);
        Assert.That(readBuffer, Is.EqualTo(testData));
    }

    #endregion

    #region WriteAsync/ReadAsync Exception and Cancellation Coverage

    [Test]
    public async Task WriteAsync_Cancelled_ShouldReturnCancelled()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer("CovBoost_AsyncCancel_W", options);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = buffer.WriteAsync(new byte[] { 1, 2, 3 }, 0, cts.Token);
        Assert.That(task.IsCanceled, Is.True);

        try { await task; } catch (OperationCanceledException) { }
    }

    [Test]
    public async Task ReadAsync_Cancelled_ShouldReturnCancelled()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer("CovBoost_AsyncCancel_R", options);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = buffer.ReadAsync(new byte[10], 0, cts.Token);
        Assert.That(task.IsCanceled, Is.True);

        try { await task; } catch (OperationCanceledException) { }
    }

    [Test]
    public void WriteAsync_OutOfBounds_ShouldReturnFaultedTask()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 100 };
        using var buffer = new HighPerformanceSharedBuffer("CovBoost_AsyncExc_W", options);

        var task = buffer.WriteAsync(new byte[200], 0);
        Assert.That(task.IsFaulted, Is.True);
    }

    [Test]
    public void ReadAsync_OutOfBounds_ShouldReturnFaultedTask()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 100 };
        using var buffer = new HighPerformanceSharedBuffer("CovBoost_AsyncExc_R", options);

        var task = buffer.ReadAsync(new byte[200], 0);
        Assert.That(task.IsFaulted, Is.True);
    }

    #endregion

    #region WaitWrite/WaitRead Timeout Coverage

    [Test]
    public void LockFreeCircularBuffer_WaitWrite_Timeout_ShouldReturnFalse()
    {
        using var buffer = new LockFreeCircularBuffer("CovBoost_WaitWrite", 256);

        // Fill the buffer completely
        var chunk = new byte[64];
        while (buffer.TryWrite(chunk)) { }

        // WaitWrite with very short timeout should fail
        var result = buffer.WaitWrite(new byte[64], TimeSpan.FromMilliseconds(50));
        Assert.That(result, Is.False);
    }

    [Test]
    public void LockFreeCircularBuffer_WaitRead_Timeout_ShouldReturnZero()
    {
        using var buffer = new LockFreeCircularBuffer("CovBoost_WaitRead", 256);

        // Buffer is empty, WaitRead should timeout
        var readBuffer = new byte[10];
        var bytesRead = buffer.WaitRead(readBuffer, TimeSpan.FromMilliseconds(50));
        Assert.That(bytesRead, Is.EqualTo(0));
    }

    [Test]
    public void MpmcCircularBuffer_WaitWrite_Timeout_ShouldReturnFalse()
    {
        using var buffer = new MpmcCircularBuffer("CovBoost_MpmcWaitWrite", 4, 64);

        // Fill all slots
        var data = new byte[48]; // MaxMessageSize = 64 - 16 = 48
        while (buffer.TryWrite(data)) { }

        var result = buffer.WaitWrite(data, TimeSpan.FromMilliseconds(50));
        Assert.That(result, Is.False);
    }

    [Test]
    public void MpmcCircularBuffer_WaitRead_Timeout_ShouldReturnZero()
    {
        using var buffer = new MpmcCircularBuffer("CovBoost_MpmcWaitRead", 4, 64);

        var readBuffer = new byte[48];
        var bytesRead = buffer.WaitRead(readBuffer, TimeSpan.FromMilliseconds(50));
        Assert.That(bytesRead, Is.EqualTo(0));
    }

    #endregion

    #region File-backed MMF Coverage

    [Test]
    public void FileBacked_Create_WriteRead_ShouldRoundTrip()
    {
        var filePath = Path.Combine(Path.GetTempPath(), "shm_test_filebacked.dat");
        try
        {
            var options = new SharedMemoryBufferOptions
            {
                Capacity = 4096,
                FilePath = filePath
            };
            using var buffer = new HighPerformanceSharedBuffer("CovBoost_FileBacked", options);

            var testData = new byte[] { 10, 20, 30, 40, 50 };
            buffer.Write(testData, 0);

            var readBuffer = new byte[5];
            buffer.Read(readBuffer, 0);

            Assert.That(readBuffer, Is.EqualTo(testData));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    #endregion

    #region Large String (ArrayPool path) Coverage

    [Test]
    public void WriteRead_LargeString_ShouldUseArrayPoolPath()
    {
        // String field large enough to trigger ArrayPool path (>1024 bytes = 512+ chars)
        var schema = new LargeStringSchema();
        using var memory = new StrictSharedMemory<LargeStringSchema>("CovBoost_LargeString", schema);

        // 600 chars → 1200 bytes > MaxStackAllocBytes (1024)
        var largeString = new string('A', 600);
        memory.WriteString(LargeStringSchema.BigStringField, largeString);
        var readString = memory.ReadString(LargeStringSchema.BigStringField);

        Assert.That(readString, Is.EqualTo(largeString));
    }

    [Test]
    public void WriteRead_LargeStringEmpty_ShouldReturnEmpty()
    {
        var schema = new LargeStringSchema();
        using var memory = new StrictSharedMemory<LargeStringSchema>("CovBoost_LargeStringEmpty", schema);

        memory.WriteString(LargeStringSchema.BigStringField, "");
        var readString = memory.ReadString(LargeStringSchema.BigStringField);

        Assert.That(readString, Is.EqualTo(""));
    }

    #endregion

    #region Schema Compatibility Coverage

    public struct SchemaV1 : IVersionedSchema
    {
        public int Version => 1;
        public bool IsCompatibleWith(int otherVersion) => otherVersion <= 2;

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Value");
        }
    }

    public struct SchemaV2 : IVersionedSchema
    {
        public int Version => 2;
        public bool IsCompatibleWith(int otherVersion) => otherVersion >= 1;

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Value");
        }
    }

    [Test]
    public void Schema_Strict_VersionMismatch_ShouldThrow()
    {
        var v1 = new SchemaV1();
        using var mem1 = new StrictSharedMemory<SchemaV1>(
            "CovBoost_SchemaStrict", v1, true, SchemaCompatibility.Strict);
        mem1.Write("Value", 42);

        // Open with v2 in strict mode → should throw
        var v2 = new SchemaV2();
        Assert.Throws<InvalidOperationException>(() =>
            new StrictSharedMemory<SchemaV2>("CovBoost_SchemaStrict", v2, false, SchemaCompatibility.Strict));
    }

    [Test]
    public void Schema_Forward_NewerVersion_ShouldSucceed()
    {
        var v2 = new SchemaV2();
        using var mem2 = new StrictSharedMemory<SchemaV2>(
            "CovBoost_SchemaFwd", v2, true, SchemaCompatibility.Strict);
        mem2.Write("Value", 100);

        // Open with v1 in Forward mode (stored=2 > current=1) → should succeed
        var v1 = new SchemaV1();
        using var mem1 = new StrictSharedMemory<SchemaV1>(
            "CovBoost_SchemaFwd", v1, false, SchemaCompatibility.Forward);

        Assert.That(mem1.StoredSchemaVersion, Is.EqualTo(2));
    }

    [Test]
    public void Schema_Forward_OlderVersion_ShouldThrow()
    {
        var v1 = new SchemaV1();
        using var mem1 = new StrictSharedMemory<SchemaV1>(
            "CovBoost_SchemaFwdFail", v1, true, SchemaCompatibility.Strict);

        // Open with v2 in Forward mode (stored=1 < current=2) → should throw
        var v2 = new SchemaV2();
        Assert.Throws<InvalidOperationException>(() =>
            new StrictSharedMemory<SchemaV2>("CovBoost_SchemaFwdFail", v2, false, SchemaCompatibility.Forward));
    }

    [Test]
    public void Schema_Backward_OlderVersion_ShouldSucceed()
    {
        var v1 = new SchemaV1();
        using var mem1 = new StrictSharedMemory<SchemaV1>(
            "CovBoost_SchemaBwd", v1, true, SchemaCompatibility.Strict);
        mem1.Write("Value", 200);

        // Open with v2 in Backward mode (stored=1 < current=2) → should succeed
        var v2 = new SchemaV2();
        using var mem2 = new StrictSharedMemory<SchemaV2>(
            "CovBoost_SchemaBwd", v2, false, SchemaCompatibility.Backward);

        Assert.That(mem2.StoredSchemaVersion, Is.EqualTo(1));
    }

    [Test]
    public void Schema_Backward_NewerVersion_ShouldThrow()
    {
        var v2 = new SchemaV2();
        using var mem2 = new StrictSharedMemory<SchemaV2>(
            "CovBoost_SchemaBwdFail", v2, true, SchemaCompatibility.Strict);

        // Open with v1 in Backward mode (stored=2 > current=1) → should throw
        var v1 = new SchemaV1();
        Assert.Throws<InvalidOperationException>(() =>
            new StrictSharedMemory<SchemaV1>("CovBoost_SchemaBwdFail", v1, false, SchemaCompatibility.Backward));
    }

    [Test]
    public void Schema_Full_AnyVersion_ShouldSucceed()
    {
        var v1 = new SchemaV1();
        using var mem1 = new StrictSharedMemory<SchemaV1>(
            "CovBoost_SchemaFull", v1, true, SchemaCompatibility.Strict);
        mem1.Write("Value", 300);

        // Open with v2 in Full mode → should succeed regardless of direction
        var v2 = new SchemaV2();
        using var mem2 = new StrictSharedMemory<SchemaV2>(
            "CovBoost_SchemaFull", v2, false, SchemaCompatibility.Full);

        Assert.That(mem2.StoredSchemaVersion, Is.EqualTo(1));
    }

    #endregion

    #region FieldDefinition Rare Type Coverage

    [Test]
    public void FieldDefinition_SByte_ShouldReturnCorrectTypeCode()
    {
        var field = FieldDefinition.Scalar<sbyte>("test");
        Assert.That(field.TypeCode, Is.EqualTo(SharedTypeCode.SByte));
    }

    [Test]
    public void FieldDefinition_Char_ShouldReturnCorrectTypeCode()
    {
        var field = FieldDefinition.Scalar<char>("test");
        Assert.That(field.TypeCode, Is.EqualTo(SharedTypeCode.Char));
    }

    [Test]
    public void FieldDefinition_UShort_ShouldReturnCorrectTypeCode()
    {
        var field = FieldDefinition.Scalar<ushort>("test");
        Assert.That(field.TypeCode, Is.EqualTo(SharedTypeCode.UInt16));
    }

    [Test]
    public void FieldDefinition_UInt_ShouldReturnCorrectTypeCode()
    {
        var field = FieldDefinition.Scalar<uint>("test");
        Assert.That(field.TypeCode, Is.EqualTo(SharedTypeCode.UInt32));
    }

    [Test]
    public void FieldDefinition_ULong_ShouldReturnCorrectTypeCode()
    {
        var field = FieldDefinition.Scalar<ulong>("test");
        Assert.That(field.TypeCode, Is.EqualTo(SharedTypeCode.UInt64));
    }

    [Test]
    public void FieldDefinition_Decimal_ShouldReturnCorrectTypeCode()
    {
        var field = FieldDefinition.Scalar<decimal>("test");
        Assert.That(field.TypeCode, Is.EqualTo(SharedTypeCode.Decimal));
    }

    [Test]
    public void FieldDefinition_Guid_ShouldReturnCorrectTypeCode()
    {
        var field = FieldDefinition.Scalar<Guid>("test");
        Assert.That(field.TypeCode, Is.EqualTo(SharedTypeCode.Guid));
    }

    [Test]
    public void FieldDefinition_DateTime_ShouldReturnCorrectTypeCode()
    {
        var field = FieldDefinition.Scalar<DateTime>("test");
        Assert.That(field.TypeCode, Is.EqualTo(SharedTypeCode.DateTime));
    }

    [Test]
    public void FieldDefinition_TimeSpan_ShouldReturnCorrectTypeCode()
    {
        var field = FieldDefinition.Scalar<TimeSpan>("test");
        Assert.That(field.TypeCode, Is.EqualTo(SharedTypeCode.TimeSpan));
    }

    [Test]
    public void FieldDefinition_DateTimeOffset_ShouldReturnCorrectTypeCode()
    {
        var field = FieldDefinition.Scalar<DateTimeOffset>("test");
        Assert.That(field.TypeCode, Is.EqualTo(SharedTypeCode.DateTimeOffset));
    }

    [Test]
    public void FieldDefinition_Struct_ShouldReturnStructTypeCode()
    {
        var field = FieldDefinition.Struct<TestPoint>("test");
        Assert.That(field.TypeCode, Is.EqualTo(SharedTypeCode.Struct));
        Assert.That(field.ArrayLength, Is.EqualTo(1));
    }

    [Test]
    public void FieldDefinition_StructArray_ShouldReturnStructTypeCode()
    {
        var field = FieldDefinition.StructArray<TestPoint>("test", 5);
        Assert.That(field.TypeCode, Is.EqualTo(SharedTypeCode.Struct));
        Assert.That(field.ArrayLength, Is.EqualTo(5));
    }

    [Test]
    public void FieldDefinition_StructArray_ZeroLength_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FieldDefinition.StructArray<TestPoint>("test", 0));
    }

    [Test]
    public void FieldDefinition_Array_ZeroLength_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FieldDefinition.Array<int>("test", 0));
    }

    [Test]
    public void FieldDefinition_String_ZeroLength_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FieldDefinition.String("test", 0));
    }

    public enum TestByteEnum : byte { A, B, C }
    public enum TestShortEnum : short { A, B, C }
    public enum TestLongEnum : long { A, B, C }
    public enum TestUIntEnum : uint { A, B, C }
    public enum TestUShortEnum : ushort { A, B, C }
    public enum TestULongEnum : ulong { A, B, C }
    public enum TestSByteEnum : sbyte { A, B, C }

    [Test]
    public void FieldDefinition_EnumTypes_ShouldMapToUnderlyingType()
    {
        Assert.That(FieldDefinition.Scalar<TestByteEnum>("e").TypeCode, Is.EqualTo(SharedTypeCode.Byte));
        Assert.That(FieldDefinition.Scalar<TestShortEnum>("e").TypeCode, Is.EqualTo(SharedTypeCode.Int16));
        Assert.That(FieldDefinition.Scalar<TestLongEnum>("e").TypeCode, Is.EqualTo(SharedTypeCode.Int64));
        Assert.That(FieldDefinition.Scalar<TestUIntEnum>("e").TypeCode, Is.EqualTo(SharedTypeCode.UInt32));
        Assert.That(FieldDefinition.Scalar<TestUShortEnum>("e").TypeCode, Is.EqualTo(SharedTypeCode.UInt16));
        Assert.That(FieldDefinition.Scalar<TestULongEnum>("e").TypeCode, Is.EqualTo(SharedTypeCode.UInt64));
        Assert.That(FieldDefinition.Scalar<TestSByteEnum>("e").TypeCode, Is.EqualTo(SharedTypeCode.SByte));
    }

    #endregion

    #region Orphan Lock Timeout Path Coverage

    [Test]
    public void IsWriteLockOrphaned_WhenNoLockHeld_ShouldReturnFalse()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            EnableOrphanLockDetection = true,
            OrphanLockTimeout = TimeSpan.FromMilliseconds(100)
        };
        using var buffer = new HighPerformanceSharedBuffer("CovBoost_OrphanNoLock", options);

        Assert.That(buffer.IsWriteLockOrphaned(), Is.False);
    }

    [Test]
    public void TryForceRelease_WhenNoLock_ShouldReturnFalse()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            EnableOrphanLockDetection = true
        };
        using var buffer = new HighPerformanceSharedBuffer("CovBoost_ForceNoLock", options);

        Assert.That(buffer.TryForceReleaseWriteLock(), Is.False);
    }

    [Test]
    public void GetLockOwnerInfo_WhenNoLockHeld_ShouldReturnZero()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer("CovBoost_OwnerNoLock", options);

        var info = buffer.GetLockOwnerInfo();
        Assert.That(info.ProcessId, Is.EqualTo(0));
        Assert.That(info.ThreadId, Is.EqualTo(0));
    }

    #endregion

    #region StrictSharedMemory Edge Cases

    [Test]
    public void HasField_ExistingField_ShouldReturnTrue()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_HasField", schema);

        Assert.That(memory.HasField(ReentrantTestSchema.IntField), Is.True);
        Assert.That(memory.HasField("NonExistent"), Is.False);
    }

    [Test]
    public void GetFieldNames_ShouldReturnAllFields()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_FieldNames", schema);

        var names = memory.GetFieldNames().ToList();
        Assert.That(names, Does.Contain(ReentrantTestSchema.IntField));
        Assert.That(names, Does.Contain(ReentrantTestSchema.DoubleField));
    }

    [Test]
    public void Read_InvalidFieldName_ShouldThrow()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_ReadInvalid", schema);

        Assert.Throws<ArgumentException>(() => memory.Read<int>("NonExistent"));
    }

    [Test]
    public void WriteArray_OnNonArrayField_ShouldThrow()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_ArrayOnScalar", schema);

        var data = new int[] { 1, 2, 3 };
        Assert.Throws<InvalidOperationException>(() =>
            memory.WriteArray<int>(ReentrantTestSchema.IntField, data));
    }

    [Test]
    public void ReadArray_OnNonArrayField_ShouldThrow()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_ReadArrayOnScalar", schema);

        var data = new int[3];
        Assert.Throws<InvalidOperationException>(() =>
            memory.ReadArray<int>(ReentrantTestSchema.IntField, data));
    }

    [Test]
    public void WriteString_OnNonStringField_ShouldThrow()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_StringOnScalar", schema);

        Assert.Throws<InvalidOperationException>(() =>
            memory.WriteString(ReentrantTestSchema.IntField, "hello"));
    }

    [Test]
    public void ReadString_OnNonStringField_ShouldThrow()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_ReadStringOnScalar", schema);

        Assert.Throws<InvalidOperationException>(() =>
            memory.ReadString(ReentrantTestSchema.IntField));
    }

    [Test]
    public void WriteString_Null_ShouldThrow()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_NullString", schema);

        Assert.Throws<ArgumentNullException>(() =>
            memory.WriteString(ReentrantTestSchema.StringField, null!));
    }

    [Test]
    public void WriteArray_ExceedsCapacity_ShouldThrow()
    {
        var schema = new ArraySchema();
        using var memory = new StrictSharedMemory<ArraySchema>("CovBoost_ArrayOverflow", schema);

        var data = new int[20]; // ArrayLength is only 10
        Assert.Throws<ArgumentException>(() =>
            memory.WriteArray<int>(ArraySchema.IntArrayField, data));
    }

    [Test]
    public void ReadArray_ExceedsCapacity_ShouldThrow()
    {
        var schema = new ArraySchema();
        using var memory = new StrictSharedMemory<ArraySchema>("CovBoost_ReadArrayOverflow", schema);

        var data = new int[20];
        Assert.Throws<ArgumentException>(() =>
            memory.ReadArray<int>(ArraySchema.IntArrayField, data));
    }

    [Test]
    public void WriteLock_Timeout_ShouldThrow()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_LockTimeout", schema);

        // Hold the underlying buffer write lock from another thread
        var lockAcquired = new ManualResetEventSlim(false);
        var releaseLock = new ManualResetEventSlim(false);

        var task = Task.Run(() =>
        {
            using var lock2 = memory.AcquireWriteLock();
            lockAcquired.Set();
            releaseLock.Wait();
        });

        lockAcquired.Wait();

        // Now try to acquire with a very short timeout — should throw TimeoutException
        Assert.Throws<TimeoutException>(() =>
            memory.AcquireWriteLock(TimeSpan.FromMilliseconds(50)));

        releaseLock.Set();
        task.Wait();
    }

    [Test]
    public void ReadLock_Timeout_ShouldThrow()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_ReadLockTimeout", schema);

        var lockAcquired = new ManualResetEventSlim(false);
        var releaseLock = new ManualResetEventSlim(false);

        var task = Task.Run(() =>
        {
            using var lock2 = memory.AcquireWriteLock();
            lockAcquired.Set();
            releaseLock.Wait();
        });

        lockAcquired.Wait();

        // ReadLock should fail while write lock is held
        Assert.Throws<TimeoutException>(() =>
            memory.AcquireReadLock(TimeSpan.FromMilliseconds(50)));

        releaseLock.Set();
        task.Wait();
    }

    [Test]
    public void OperationsAfterDispose_ShouldThrow()
    {
        var schema = new ReentrantTestSchema();
        var memory = new StrictSharedMemory<ReentrantTestSchema>("CovBoost_Disposed", schema);
        memory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => memory.Write(ReentrantTestSchema.IntField, 1));
        Assert.Throws<ObjectDisposedException>(() => memory.Read<int>(ReentrantTestSchema.IntField));
        Assert.Throws<ObjectDisposedException>(() => memory.AcquireWriteLock());
        Assert.Throws<ObjectDisposedException>(() => memory.AcquireReadLock());
    }

    #endregion

    #region SharedArray Edge Cases

    [Test]
    public void SharedArray_CopyFrom_OutOfBounds_ShouldThrow()
    {
        using var array = new SharedArray<int>("CovBoost_ArrCopyOOB", 10);
        var data = new int[5];
        Assert.Throws<ArgumentOutOfRangeException>(() => array.CopyFrom(8, data));
    }

    [Test]
    public void SharedArray_CopyTo_OutOfBounds_ShouldThrow()
    {
        using var array = new SharedArray<int>("CovBoost_ArrCopyToOOB", 10);
        var data = new int[5];
        Assert.Throws<ArgumentOutOfRangeException>(() => array.CopyTo(8, data));
    }

    [Test]
    public void SharedArray_Fill_WithRange_ShouldWork()
    {
        using var array = new SharedArray<int>("CovBoost_FillRange", 100);

        array.Fill(0);
        array.Fill(99, 5, 10);

        Assert.That(array[4], Is.EqualTo(0));
        Assert.That(array[5], Is.EqualTo(99));
        Assert.That(array[14], Is.EqualTo(99));
        Assert.That(array[15], Is.EqualTo(0));
    }

    [Test]
    public void SharedArray_Fill_OutOfBounds_ShouldThrow()
    {
        using var array = new SharedArray<int>("CovBoost_FillOOB", 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => array.Fill(1, 8, 5));
    }

    [Test]
    public void SharedArray_OperationsAfterDispose_ShouldThrow()
    {
        var array = new SharedArray<int>("CovBoost_ArrDisposed", 10);
        array.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = array[0]; });
        Assert.Throws<ObjectDisposedException>(() => array.Fill(1));
        Assert.Throws<ObjectDisposedException>(() => array.Clear());
    }

    [Test]
    public void SharedArray_Constructor_InvalidArgs_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => new SharedArray<int>("", 10));
        Assert.Throws<ArgumentException>(() => new SharedArray<int>("  ", 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SharedArray<int>("valid", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SharedArray<int>("valid", -1));
    }

    #endregion

    #region Constructor Validation

    [Test]
    public void HighPerformanceSharedBuffer_EmptyName_ShouldThrow()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        Assert.Throws<ArgumentException>(() => new HighPerformanceSharedBuffer("", options));
        Assert.Throws<ArgumentException>(() => new HighPerformanceSharedBuffer("  ", options));
    }

    [Test]
    public void LockFreeCircularBuffer_InvalidArgs_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => new LockFreeCircularBuffer("", 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LockFreeCircularBuffer("valid", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LockFreeCircularBuffer("valid", -1));
    }

    [Test]
    public void MpmcCircularBuffer_InvalidArgs_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => new MpmcCircularBuffer("", 4, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpmcCircularBuffer("valid", 0, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpmcCircularBuffer("valid", 4, 0));
    }

    [Test]
    public void StrictSharedMemory_EmptyName_ShouldThrow()
    {
        var schema = new ReentrantTestSchema();
        Assert.Throws<ArgumentException>(() =>
            new StrictSharedMemory<ReentrantTestSchema>("", schema));
    }

    #endregion

    #region Blob Field Tests

    [Test]
    public void WriteBlob_ReadBlob_ShouldRoundTrip()
    {
        var schema = new BlobSchema();
        using var memory = new StrictSharedMemory<BlobSchema>("CovBoost_Blob", schema);

        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03 };
        memory.WriteBlob(BlobSchema.DataField, data);
        var result = memory.ReadBlob(BlobSchema.DataField);

        Assert.That(result, Is.EqualTo(data));
    }

    [Test]
    public void WriteBlob_EmptyData_ShouldReturnEmpty()
    {
        var schema = new BlobSchema();
        using var memory = new StrictSharedMemory<BlobSchema>("CovBoost_BlobEmpty", schema);

        memory.WriteBlob(BlobSchema.DataField, ReadOnlySpan<byte>.Empty);
        var result = memory.ReadBlob(BlobSchema.DataField);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void WriteBlob_MaxCapacity_ShouldWork()
    {
        var schema = new BlobSchema();
        using var memory = new StrictSharedMemory<BlobSchema>("CovBoost_BlobMax", schema);

        var data = new byte[256]; // BlobSchema maxSize = 256
        Random.Shared.NextBytes(data);
        memory.WriteBlob(BlobSchema.DataField, data);
        var result = memory.ReadBlob(BlobSchema.DataField);

        Assert.That(result, Is.EqualTo(data));
    }

    [Test]
    public void WriteBlob_ExceedsCapacity_ShouldThrow()
    {
        var schema = new BlobSchema();
        using var memory = new StrictSharedMemory<BlobSchema>("CovBoost_BlobOverflow", schema);

        var data = new byte[300]; // > 256
        Assert.Throws<ArgumentException>(() => memory.WriteBlob(BlobSchema.DataField, data));
    }

    [Test]
    public void WriteBlob_OnNonBlobField_ShouldThrow()
    {
        var schema = new BlobSchema();
        using var memory = new StrictSharedMemory<BlobSchema>("CovBoost_BlobWrongField", schema);

        Assert.Throws<InvalidOperationException>(() =>
            memory.WriteBlob(BlobSchema.IntField, new byte[5]));
    }

    [Test]
    public void ReadBlob_OnNonBlobField_ShouldThrow()
    {
        var schema = new BlobSchema();
        using var memory = new StrictSharedMemory<BlobSchema>("CovBoost_ReadBlobWrong", schema);

        Assert.Throws<InvalidOperationException>(() => memory.ReadBlob(BlobSchema.IntField));
    }

    [Test]
    public void WriteBlob_Overwrite_ShouldClearStaleData()
    {
        var schema = new BlobSchema();
        using var memory = new StrictSharedMemory<BlobSchema>("CovBoost_BlobOverwrite", schema);

        // Write large data first
        var large = new byte[200];
        Array.Fill(large, (byte)0xFF);
        memory.WriteBlob(BlobSchema.DataField, large);

        // Overwrite with small data
        var small = new byte[] { 1, 2, 3 };
        memory.WriteBlob(BlobSchema.DataField, small);

        // Should only return the small data
        var result = memory.ReadBlob(BlobSchema.DataField);
        Assert.That(result, Is.EqualTo(small));
    }

    [Test]
    public void ReadBlob_BeforeWrite_ShouldReturnEmpty()
    {
        var schema = new BlobSchema();
        using var memory = new StrictSharedMemory<BlobSchema>("CovBoost_BlobNoWrite", schema);

        var result = memory.ReadBlob(BlobSchema.DataField);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void FieldDefinition_Blob_ZeroSize_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldDefinition.Blob("test", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldDefinition.Blob("test", -1));
    }

    #endregion

    #region UTF-8 String Tests

    [Test]
    public void WriteUtf8_ReadUtf8_ASCII_ShouldRoundTrip()
    {
        var schema = new Utf8Schema();
        using var memory = new StrictSharedMemory<Utf8Schema>("CovBoost_Utf8Ascii", schema);

        memory.WriteUtf8String(Utf8Schema.NameField, "Hello World");
        var result = memory.ReadUtf8String(Utf8Schema.NameField);

        Assert.That(result, Is.EqualTo("Hello World"));
    }

    [Test]
    public void WriteUtf8_ReadUtf8_Unicode_ShouldRoundTrip()
    {
        var schema = new Utf8Schema();
        using var memory = new StrictSharedMemory<Utf8Schema>("CovBoost_Utf8Unicode", schema);

        memory.WriteUtf8String(Utf8Schema.NameField, "한국어 테스트 🚀");
        var result = memory.ReadUtf8String(Utf8Schema.NameField);

        Assert.That(result, Is.EqualTo("한국어 테스트 🚀"));
    }

    [Test]
    public void WriteUtf8_EmptyString_ShouldReturnEmpty()
    {
        var schema = new Utf8Schema();
        using var memory = new StrictSharedMemory<Utf8Schema>("CovBoost_Utf8Empty", schema);

        memory.WriteUtf8String(Utf8Schema.NameField, "");
        var result = memory.ReadUtf8String(Utf8Schema.NameField);

        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public void WriteUtf8_Null_ShouldThrow()
    {
        var schema = new Utf8Schema();
        using var memory = new StrictSharedMemory<Utf8Schema>("CovBoost_Utf8Null", schema);

        Assert.Throws<ArgumentNullException>(() =>
            memory.WriteUtf8String(Utf8Schema.NameField, null!));
    }

    [Test]
    public void WriteUtf8_ExceedsCapacity_ShouldThrow()
    {
        var schema = new Utf8Schema();
        using var memory = new StrictSharedMemory<Utf8Schema>("CovBoost_Utf8Overflow", schema);

        // 256 byte max, each Korean char is 3 bytes in UTF-8
        var longString = new string('가', 100); // 300 bytes > 256
        Assert.Throws<ArgumentException>(() =>
            memory.WriteUtf8String(Utf8Schema.NameField, longString));
    }

    [Test]
    public void WriteUtf8_OnNonUtf8Field_ShouldThrow()
    {
        var schema = new Utf8Schema();
        using var memory = new StrictSharedMemory<Utf8Schema>("CovBoost_Utf8WrongField", schema);

        Assert.Throws<InvalidOperationException>(() =>
            memory.WriteUtf8String(Utf8Schema.IntField, "test"));
    }

    [Test]
    public void ReadUtf8_OnNonUtf8Field_ShouldThrow()
    {
        var schema = new Utf8Schema();
        using var memory = new StrictSharedMemory<Utf8Schema>("CovBoost_ReadUtf8Wrong", schema);

        Assert.Throws<InvalidOperationException>(() =>
            memory.ReadUtf8String(Utf8Schema.IntField));
    }

    [Test]
    public void ReadUtf8_BeforeWrite_ShouldReturnEmpty()
    {
        var schema = new Utf8Schema();
        using var memory = new StrictSharedMemory<Utf8Schema>("CovBoost_Utf8NoWrite", schema);

        var result = memory.ReadUtf8String(Utf8Schema.NameField);
        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public void WriteUtf8_LargeString_ShouldUseArrayPoolPath()
    {
        var schema = new LargeUtf8Schema();
        using var memory = new StrictSharedMemory<LargeUtf8Schema>("CovBoost_Utf8Large", schema);

        // 800 ASCII chars = 800 bytes, field is 2048 bytes max
        var largeString = new string('X', 800);
        memory.WriteUtf8String(LargeUtf8Schema.BigField, largeString);
        var result = memory.ReadUtf8String(LargeUtf8Schema.BigField);

        Assert.That(result, Is.EqualTo(largeString));
    }

    [Test]
    public void WriteUtf8_LargeUnicode_ShouldUseArrayPoolPath()
    {
        var schema = new LargeUtf8Schema();
        using var memory = new StrictSharedMemory<LargeUtf8Schema>("CovBoost_Utf8LargeUni", schema);

        // 400 Korean chars = 1200 bytes > MaxStackAllocBytes (1024), field is 2048 max
        var largeString = new string('가', 400);
        memory.WriteUtf8String(LargeUtf8Schema.BigField, largeString);
        var result = memory.ReadUtf8String(LargeUtf8Schema.BigField);

        Assert.That(result, Is.EqualTo(largeString));
    }

    [Test]
    public void Utf8_MemoryEfficiency_ComparedToUtf16()
    {
        // Demonstrate that UTF-8 uses less memory for ASCII
        var schema = new MixedStringSchema();
        using var memory = new StrictSharedMemory<MixedStringSchema>("CovBoost_Utf8Efficiency", schema);

        string asciiText = "Hello World 123";

        // UTF-16: 15 chars * 2 bytes = 30 bytes
        memory.WriteString(MixedStringSchema.Utf16Field, asciiText);

        // UTF-8: 15 chars * 1 byte = 15 bytes (50% less)
        memory.WriteUtf8String(MixedStringSchema.Utf8Field, asciiText);

        Assert.That(memory.ReadString(MixedStringSchema.Utf16Field), Is.EqualTo(asciiText));
        Assert.That(memory.ReadUtf8String(MixedStringSchema.Utf8Field), Is.EqualTo(asciiText));
    }

    [Test]
    public void FieldDefinition_Utf8String_ZeroSize_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldDefinition.Utf8String("test", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldDefinition.Utf8String("test", -1));
    }

    #endregion

    #region Helper Schemas

    public struct BlobSchema : ISharedMemorySchema
    {
        public const string DataField = "Data";
        public const string IntField = "IntValue";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Blob(DataField, 256);
            yield return FieldDefinition.Scalar<int>(IntField);
        }
    }

    public struct Utf8Schema : ISharedMemorySchema
    {
        public const string NameField = "Name";
        public const string IntField = "IntValue";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Utf8String(NameField, 256);
            yield return FieldDefinition.Scalar<int>(IntField);
        }
    }

    public struct LargeUtf8Schema : ISharedMemorySchema
    {
        public const string BigField = "BigUtf8";

        public IEnumerable<FieldDefinition> GetFields()
        {
            // 2048 bytes max — triggers ArrayPool path for large strings
            yield return FieldDefinition.Utf8String(BigField, 2048);
        }
    }

    public struct MixedStringSchema : ISharedMemorySchema
    {
        public const string Utf16Field = "Utf16Name";
        public const string Utf8Field = "Utf8Name";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.String(Utf16Field, 64);
            yield return FieldDefinition.Utf8String(Utf8Field, 128);
        }
    }

    public struct ReentrantTestSchema : ISharedMemorySchema
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

    public struct LargeStringSchema : ISharedMemorySchema
    {
        public const string BigStringField = "BigString";

        public IEnumerable<FieldDefinition> GetFields()
        {
            // 1024 chars → 2048 bytes, exceeds MaxStackAllocBytes (1024)
            yield return FieldDefinition.String(BigStringField, 1024);
        }
    }

    public struct ArraySchema : ISharedMemorySchema
    {
        public const string IntArrayField = "IntArray";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Array<int>(IntArrayField, 10);
        }
    }

    private struct TestPoint
    {
        public float X;
        public float Y;
    }

    #endregion

    #region Additional Coverage Boost — Round 2

    // --- HighPerformanceSharedBuffer ---

    [Test]
    public void GetStatistics_ShouldTrackReadWriteCounts()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer("CovR2_Stats", options);

        buffer.Write(new byte[] { 1, 2, 3 }, 0);
        buffer.Write(new byte[] { 4, 5 }, 10);
        buffer.Read(new byte[3], 0);

        var stats = buffer.GetStatistics();
        Assert.That(stats.Writes, Is.EqualTo(2));
        Assert.That(stats.Reads, Is.EqualTo(1));
        Assert.That(stats.BytesWritten, Is.EqualTo(5));
        Assert.That(stats.BytesRead, Is.EqualTo(3));
    }

    [Test]
    public void ReadLock_Timeout_DueToWriterContention_ShouldReturnFalse()
    {
        var options = new SharedMemoryBufferOptions { Capacity = 4096 };
        using var buffer = new HighPerformanceSharedBuffer("CovR2_RLockTO", options);

        var lockHeld = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);

        var task = Task.Run(() =>
        {
            buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(5));
            lockHeld.Set();
            release.Wait();
            buffer.ReleaseWriteLock();
        });

        lockHeld.Wait();
        // ReadLock should fail because write lock is held
        var result = buffer.TryAcquireReadLock(TimeSpan.FromMilliseconds(50));
        Assert.That(result, Is.False);

        release.Set();
        task.Wait();
    }

    [Test]
    public void OrphanLock_TimeoutBased_ShouldDetect()
    {
        var options = new SharedMemoryBufferOptions
        {
            Capacity = 4096,
            EnableOrphanLockDetection = true,
            OrphanLockTimeout = TimeSpan.FromMilliseconds(50) // very short
        };
        using var buffer = new HighPerformanceSharedBuffer("CovR2_OrphanTO", options);

        buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1));
        // Don't release — wait for timeout
        Thread.Sleep(100);

        // Timeout-based detection should fire
        Assert.That(buffer.IsWriteLockOrphaned(), Is.True);

        buffer.ReleaseWriteLock();
    }

    // --- MpmcCircularBuffer ---

    [Test]
    public void Mpmc_OpenExisting_ShouldValidate()
    {
        // Create first
        using var buf1 = new MpmcCircularBuffer("CovR2_MpmcOpen", 4, 64, create: true);

        // Open existing with same params
        using var buf2 = new MpmcCircularBuffer("CovR2_MpmcOpen", 4, 64, create: false);

        // Should work without exception
        Assert.That(buf2.SlotCount, Is.EqualTo(buf1.SlotCount));
    }

    [Test]
    public void Mpmc_OpenExisting_SlotCountMismatch_ShouldThrow()
    {
        using var buf1 = new MpmcCircularBuffer("CovR2_MpmcMismatch", 4, 64, create: true);

        Assert.Throws<InvalidOperationException>(() =>
            new MpmcCircularBuffer("CovR2_MpmcMismatch", 8, 64, create: false));
    }

    [Test]
    public void Mpmc_OpenExisting_SlotSizeMismatch_ShouldThrow()
    {
        using var buf1 = new MpmcCircularBuffer("CovR2_MpmcSizeMismatch", 4, 64, create: true);

        Assert.Throws<InvalidOperationException>(() =>
            new MpmcCircularBuffer("CovR2_MpmcSizeMismatch", 4, 128, create: false));
    }

    [Test]
    public void Mpmc_TryRead_SmallDestination_ShouldTruncate()
    {
        using var buffer = new MpmcCircularBuffer("CovR2_MpmcTrunc", 4, 64);

        var data = new byte[40];
        Array.Fill(data, (byte)0xAB);
        buffer.TryWrite(data);

        // Read into smaller buffer — should truncate
        var small = new byte[10];
        int read = buffer.TryRead(small);
        Assert.That(read, Is.EqualTo(10));
        Assert.That(small[0], Is.EqualTo(0xAB));
    }

    [Test]
    public void Mpmc_SpinExhaustion_Write_ShouldReturnFalse()
    {
        // 2 slots, fill both, then TryWrite should spin-exhaust on a 3rd
        using var buffer = new MpmcCircularBuffer("CovR2_MpmcSpinW", 2, 32);

        var data = new byte[16]; // MaxMessageSize = 32 - 16 = 16
        Assert.That(buffer.TryWrite(data), Is.True);
        Assert.That(buffer.TryWrite(data), Is.True);

        // Buffer full — TryWrite will spin then fail
        Assert.That(buffer.TryWrite(data), Is.False);
    }

    [Test]
    public void Mpmc_SpinExhaustion_Read_ShouldReturnZero()
    {
        // Empty buffer — TryRead will spin then fail
        using var buffer = new MpmcCircularBuffer("CovR2_MpmcSpinR", 2, 32);

        var dest = new byte[16];
        Assert.That(buffer.TryRead(dest), Is.EqualTo(0));
    }

    // --- LockFreeCircularBuffer ---

    [Test]
    public void LockFree_Clear_ShouldResetPositions()
    {
        using var buffer = new LockFreeCircularBuffer("CovR2_Clear", 4096);

        buffer.TryWrite(new byte[] { 1, 2, 3, 4 });
        Assert.That(buffer.Used, Is.GreaterThan(0));

        buffer.Clear();
        Assert.That(buffer.Used, Is.EqualTo(0));
        Assert.That(buffer.Available, Is.EqualTo(buffer.Capacity));
    }

    [Test]
    public void LockFree_TryWrite_EmptyData_ShouldSucceed()
    {
        using var buffer = new LockFreeCircularBuffer("CovR2_EmptyWrite", 4096);

        Assert.That(buffer.TryWrite(ReadOnlySpan<byte>.Empty), Is.True);
        Assert.That(buffer.Used, Is.EqualTo(0));
    }

    [Test]
    public void LockFree_TryRead_EmptyDestination_ShouldReturnZero()
    {
        using var buffer = new LockFreeCircularBuffer("CovR2_EmptyRead", 4096);

        buffer.TryWrite(new byte[] { 1, 2, 3 });
        Assert.That(buffer.TryRead(Span<byte>.Empty), Is.EqualTo(0));
    }

    [Test]
    public void LockFree_WaitWrite_ShouldSucceedWhenSpaceAvailable()
    {
        using var buffer = new LockFreeCircularBuffer("CovR2_WaitWriteOK", 4096);

        var data = new byte[] { 1, 2, 3, 4, 5 };
        Assert.That(buffer.WaitWrite(data, TimeSpan.FromSeconds(1)), Is.True);

        var read = new byte[5];
        Assert.That(buffer.WaitRead(read, TimeSpan.FromSeconds(1)), Is.EqualTo(5));
        Assert.That(read, Is.EqualTo(data));
    }

    // --- SharedArray large fill ---

    [Test]
    public void SharedArray_Fill_Large_ShouldUseBatchPath()
    {
        // Use a large struct to trigger batchBytes > 1024 path
        using var array = new SharedArray<long>("CovR2_LargeFill", 5000);

        array.Fill(42L);
        Assert.That(array[0], Is.EqualTo(42L));
        Assert.That(array[2500], Is.EqualTo(42L));
        Assert.That(array[4999], Is.EqualTo(42L));
    }

    // --- StrictSharedMemory additional ---

    [Test]
    public void Schema_Property_ShouldReturnSchema()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovR2_SchemaProp", schema);

        Assert.That(memory.Schema, Is.EqualTo(schema));
        Assert.That(memory.SchemaVersion, Is.EqualTo(1));
    }

    [Test]
    public void WriteArray_InvalidFieldName_ShouldThrow()
    {
        var schema = new ArraySchema();
        using var memory = new StrictSharedMemory<ArraySchema>("CovR2_ArrInvalidName", schema);

        Assert.Throws<ArgumentException>(() =>
            memory.WriteArray<int>("NonExistent", new int[5]));
    }

    [Test]
    public void ReadArray_InvalidFieldName_ShouldThrow()
    {
        var schema = new ArraySchema();
        using var memory = new StrictSharedMemory<ArraySchema>("CovR2_ReadArrInvalidName", schema);

        Assert.Throws<ArgumentException>(() =>
            memory.ReadArray<int>("NonExistent", new int[5]));
    }

    [Test]
    public void WriteString_InvalidFieldName_ShouldThrow()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovR2_StrInvalidName", schema);

        Assert.Throws<ArgumentException>(() =>
            memory.WriteString("NonExistent", "test"));
    }

    [Test]
    public void ReadString_InvalidFieldName_ShouldThrow()
    {
        var schema = new ReentrantTestSchema();
        using var memory = new StrictSharedMemory<ReentrantTestSchema>("CovR2_ReadStrInvalidName", schema);

        Assert.Throws<ArgumentException>(() =>
            memory.ReadString("NonExistent"));
    }

    [Test]
    public void WriteBlob_InvalidFieldName_ShouldThrow()
    {
        var schema = new BlobSchema();
        using var memory = new StrictSharedMemory<BlobSchema>("CovR2_BlobInvalidName", schema);

        Assert.Throws<ArgumentException>(() =>
            memory.WriteBlob("NonExistent", new byte[5]));
    }

    [Test]
    public void ReadBlob_InvalidFieldName_ShouldThrow()
    {
        var schema = new BlobSchema();
        using var memory = new StrictSharedMemory<BlobSchema>("CovR2_ReadBlobInvalidName", schema);

        Assert.Throws<ArgumentException>(() =>
            memory.ReadBlob("NonExistent"));
    }

    [Test]
    public void WriteUtf8_InvalidFieldName_ShouldThrow()
    {
        var schema = new Utf8Schema();
        using var memory = new StrictSharedMemory<Utf8Schema>("CovR2_Utf8InvalidName", schema);

        Assert.Throws<ArgumentException>(() =>
            memory.WriteUtf8String("NonExistent", "test"));
    }

    [Test]
    public void ReadUtf8_InvalidFieldName_ShouldThrow()
    {
        var schema = new Utf8Schema();
        using var memory = new StrictSharedMemory<Utf8Schema>("CovR2_ReadUtf8InvalidName", schema);

        Assert.Throws<ArgumentException>(() =>
            memory.ReadUtf8String("NonExistent"));
    }

    [Test]
    public void WriteArray_WrongElementType_ShouldThrow()
    {
        var schema = new ArraySchema();
        using var memory = new StrictSharedMemory<ArraySchema>("CovR2_ArrWrongType", schema);

        // IntArray expects int (4 bytes), not double (8 bytes)
        Assert.Throws<InvalidOperationException>(() =>
            memory.WriteArray<double>(ArraySchema.IntArrayField, new double[5]));
    }

    [Test]
    public void ReadArray_WrongElementType_ShouldThrow()
    {
        var schema = new ArraySchema();
        using var memory = new StrictSharedMemory<ArraySchema>("CovR2_ReadArrWrongType", schema);

        Assert.Throws<InvalidOperationException>(() =>
            memory.ReadArray<double>(ArraySchema.IntArrayField, new double[5]));
    }

    [Test]
    public void WriteBlob_UnderExplicitLock_ShouldSkipAutoLock()
    {
        var schema = new BlobSchema();
        using var memory = new StrictSharedMemory<BlobSchema>("CovR2_BlobLock", schema);

        using (memory.AcquireWriteLock())
        {
            memory.WriteBlob(BlobSchema.DataField, new byte[] { 1, 2, 3 });
        }

        var result = memory.ReadBlob(BlobSchema.DataField);
        Assert.That(result, Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void ReadBlob_UnderExplicitLock_ShouldSkipAutoLock()
    {
        var schema = new BlobSchema();
        using var memory = new StrictSharedMemory<BlobSchema>("CovR2_ReadBlobLock", schema);

        memory.WriteBlob(BlobSchema.DataField, new byte[] { 10, 20 });

        using (memory.AcquireReadLock())
        {
            var result = memory.ReadBlob(BlobSchema.DataField);
            Assert.That(result, Is.EqualTo(new byte[] { 10, 20 }));
        }
    }

    [Test]
    public void WriteUtf8_UnderExplicitLock_ShouldSkipAutoLock()
    {
        var schema = new Utf8Schema();
        using var memory = new StrictSharedMemory<Utf8Schema>("CovR2_Utf8Lock", schema);

        using (memory.AcquireWriteLock())
        {
            memory.WriteUtf8String(Utf8Schema.NameField, "Locked");
        }

        Assert.That(memory.ReadUtf8String(Utf8Schema.NameField), Is.EqualTo("Locked"));
    }

    [Test]
    public void ReadUtf8_UnderExplicitLock_ShouldSkipAutoLock()
    {
        var schema = new Utf8Schema();
        using var memory = new StrictSharedMemory<Utf8Schema>("CovR2_ReadUtf8Lock", schema);

        memory.WriteUtf8String(Utf8Schema.NameField, "Test");

        using (memory.AcquireReadLock())
        {
            Assert.That(memory.ReadUtf8String(Utf8Schema.NameField), Is.EqualTo("Test"));
        }
    }

    #endregion
}
