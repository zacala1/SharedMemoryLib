using NUnit.Framework;
using SharedMemory;

namespace SharedMemory.Tests;

[TestFixture]
public class StrictSharedMemoryTests
{
    private const string TestBufferName = "TestBuffer_Strict";

    public struct TestSchema : ISharedMemorySchema
    {
        public const string IntField = "IntValue";
        public const string DoubleField = "DoubleValue";
        public const string LongField = "LongValue";
        public const string FloatArrayField = "FloatArray";
        public const string IntArrayField = "IntArray";
        public const string StringField = "StringValue";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(IntField);
            yield return FieldDefinition.Scalar<double>(DoubleField);
            yield return FieldDefinition.Scalar<long>(LongField);
            yield return FieldDefinition.Array<float>(FloatArrayField, 10);
            yield return FieldDefinition.Array<int>(IntArrayField, 10);
            yield return FieldDefinition.String(StringField, 32);
        }
    }

    [TearDown]
    public void Cleanup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Test]
    public void Create_WithSchema_ShouldSucceed()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_Create", schema);

        Assert.That(memory.IsOwner, Is.True);
    }

    [Test]
    public void WriteRead_ScalarInt_ShouldRoundTrip()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_Int", schema);

        memory.Write(TestSchema.IntField, 12345);
        var value = memory.Read<int>(TestSchema.IntField);

        Assert.That(value, Is.EqualTo(12345));
    }

    [Test]
    public void WriteRead_ScalarDouble_ShouldRoundTrip()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_Double", schema);

        memory.Write(TestSchema.DoubleField, 3.14159265359);
        var value = memory.Read<double>(TestSchema.DoubleField);

        Assert.That(value, Is.EqualTo(3.14159265359).Within(0.0000001));
    }

    [Test]
    public void WriteRead_FloatArray_ShouldRoundTrip()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_Array", schema);

        var testArray = new float[] { 1.1f, 2.2f, 3.3f, 4.4f, 5.5f };
        memory.WriteArray<float>(TestSchema.FloatArrayField, testArray);

        var readArray = new float[10];
        memory.ReadArray<float>(TestSchema.FloatArrayField, readArray);

        for (int i = 0; i < testArray.Length; i++)
        {
            Assert.That(readArray[i], Is.EqualTo(testArray[i]).Within(0.001f));
        }
    }

    [Test]
    public void WriteRead_String_ShouldRoundTrip()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_String", schema);

        const string testString = "Hello, World!";
        memory.WriteString(TestSchema.StringField, testString);
        var readString = memory.ReadString(TestSchema.StringField);

        Assert.That(readString, Is.EqualTo(testString));
    }

    [Test]
    public void WriteRead_EmptyString_ShouldRoundTrip()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_EmptyString", schema);

        memory.WriteString(TestSchema.StringField, "");
        var readString = memory.ReadString(TestSchema.StringField);

        Assert.That(readString, Is.EqualTo(""));
    }

    [Test]
    public void WriteLock_ShouldProvideExclusiveAccess()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_WriteLock", schema);

        using (var writeLock = memory.AcquireWriteLock())
        {
            memory.Write(TestSchema.IntField, 999);
        }

        var value = memory.Read<int>(TestSchema.IntField);
        Assert.That(value, Is.EqualTo(999));
    }

    [Test]
    public void ReadLock_ShouldProvideReadAccess()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_ReadLock", schema);

        memory.Write(TestSchema.IntField, 777);

        using (var readLock = memory.AcquireReadLock())
        {
            var value = memory.Read<int>(TestSchema.IntField);
            Assert.That(value, Is.EqualTo(777));
        }
    }

    [Test]
    public void Write_InvalidFieldName_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_InvalidField", schema);

        Assert.Throws<ArgumentException>(() => memory.Write("NonExistentField", 123));
    }

    [Test]
    public void Write_WrongType_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_WrongType", schema);

        Assert.Throws<InvalidOperationException>(() => memory.Write(TestSchema.IntField, 3.14));
    }

    [Test]
    public void WriteString_TooLong_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_LongString", schema);

        var longString = new string('X', 100);
        Assert.Throws<ArgumentException>(() => memory.WriteString(TestSchema.StringField, longString));
    }

    [Test]
    public void MultipleFields_ShouldNotInterfere()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_MultiField", schema);

        memory.Write(TestSchema.IntField, 42);
        memory.Write(TestSchema.DoubleField, 2.71828);
        memory.WriteString(TestSchema.StringField, "Test");

        Assert.That(memory.Read<int>(TestSchema.IntField), Is.EqualTo(42));
        Assert.That(memory.Read<double>(TestSchema.DoubleField), Is.EqualTo(2.71828).Within(0.00001));
        Assert.That(memory.ReadString(TestSchema.StringField), Is.EqualTo("Test"));
    }

    [Test]
    public void Schema_FieldsAreOrdered()
    {
        var schema = new TestSchema();
        var fields = schema.GetFields().ToList();

        Assert.That(fields.Count, Is.EqualTo(6));
        Assert.That(fields[0].Name, Is.EqualTo(TestSchema.IntField));
        Assert.That(fields[1].Name, Is.EqualTo(TestSchema.DoubleField));
        Assert.That(fields[2].Name, Is.EqualTo(TestSchema.LongField));
        Assert.That(fields[3].Name, Is.EqualTo(TestSchema.FloatArrayField));
        Assert.That(fields[4].Name, Is.EqualTo(TestSchema.IntArrayField));
        Assert.That(fields[5].Name, Is.EqualTo(TestSchema.StringField));
    }

    #region Schema Versioning Tests

    public struct VersionedSchemaV1 : IVersionedSchema
    {
        public const string IntField = "IntValue";
        public const string DoubleField = "DoubleValue";

        public int Version => 1;

        public bool IsCompatibleWith(int otherVersion)
        {
            return otherVersion == 1;
        }

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(IntField);
            yield return FieldDefinition.Scalar<double>(DoubleField);
        }
    }

    public struct VersionedSchemaV2 : IVersionedSchema
    {
        public const string IntField = "IntValue";
        public const string DoubleField = "DoubleValue";
        public const string StringField = "StringValue";

        public int Version => 2;

        public bool IsCompatibleWith(int otherVersion)
        {
            // V2 is backward compatible with V1
            return otherVersion == 1 || otherVersion == 2;
        }

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(IntField);
            yield return FieldDefinition.Scalar<double>(DoubleField);
            yield return FieldDefinition.String(StringField, 32);
        }
    }

    [Test]
    public void VersionedSchema_ShouldStoreVersion()
    {
        var schema = new VersionedSchemaV1();
        using var memory = new StrictSharedMemory<VersionedSchemaV1>(TestBufferName + "_Versioned", schema);

        Assert.That(memory.SchemaVersion, Is.EqualTo(1));
    }

    [Test]
    public void VersionedSchema_SameVersion_ShouldWork()
    {
        var uniqueName = TestBufferName + "_SameVer_" + Guid.NewGuid().ToString("N");

        // Create with V1 and keep it alive (IPC scenario)
        var schemaV1 = new VersionedSchemaV1();
        using var memory1 = new StrictSharedMemory<VersionedSchemaV1>(uniqueName, schemaV1, create: true);
        memory1.Write(VersionedSchemaV1.IntField, 42);

        // Open with same V1 while first is still active
        using var memory2 = new StrictSharedMemory<VersionedSchemaV1>(uniqueName, schemaV1, create: false);
        var value = memory2.Read<int>(VersionedSchemaV1.IntField);
        Assert.That(value, Is.EqualTo(42));
    }

    [Test]
    public void VersionedSchema_BackwardCompatible_SameStructure_ShouldWork()
    {
        var uniqueName = TestBufferName + "_BackCompat_" + Guid.NewGuid().ToString("N");

        // Create with V1 and keep alive (IPC scenario)
        var schemaV1 = new VersionedSchemaV1();
        using var memory1 = new StrictSharedMemory<VersionedSchemaV1>(uniqueName, schemaV1, create: true);
        memory1.Write(VersionedSchemaV1.IntField, 100);
        memory1.Write(VersionedSchemaV1.DoubleField, 3.14);

        // Open with same V1 schema in backward compatibility mode
        // This demonstrates that backward mode allows reading data from same version
        using var memory2 = new StrictSharedMemory<VersionedSchemaV1>(
            uniqueName, schemaV1, create: false, compatibility: SchemaCompatibility.Backward);

        // Should be able to read V1 fields
        var intValue = memory2.Read<int>(VersionedSchemaV1.IntField);
        var doubleValue = memory2.Read<double>(VersionedSchemaV1.DoubleField);
        Assert.That(intValue, Is.EqualTo(100));
        Assert.That(doubleValue, Is.EqualTo(3.14).Within(0.001));
    }

    [Test]
    public void VersionedSchema_FullCompatibility_ShouldWorkForSameVersion()
    {
        var uniqueName = TestBufferName + "_FullCompat_" + Guid.NewGuid().ToString("N");

        // Create with V2 and keep alive
        var schemaV2 = new VersionedSchemaV2();
        using var memory1 = new StrictSharedMemory<VersionedSchemaV2>(uniqueName, schemaV2, create: true);
        memory1.Write(VersionedSchemaV2.IntField, 200);

        // Open with same schema in full compatibility mode
        using var memory2 = new StrictSharedMemory<VersionedSchemaV2>(
            uniqueName, schemaV2, create: false, compatibility: SchemaCompatibility.Full);

        var intValue = memory2.Read<int>(VersionedSchemaV2.IntField);
        Assert.That(intValue, Is.EqualTo(200));
    }

    [Test]
    public void VersionedSchema_StrictMode_DifferentVersion_ShouldThrow()
    {
        var uniqueName = TestBufferName + "_StrictMode_" + Guid.NewGuid().ToString("N");

        // Create with V1 and keep alive
        var schemaV1 = new VersionedSchemaV1();
        using var memory1 = new StrictSharedMemory<VersionedSchemaV1>(uniqueName, schemaV1, create: true);
        memory1.Write(VersionedSchemaV1.IntField, 42);

        // Try to open with V2 in strict mode - should throw
        var schemaV2 = new VersionedSchemaV2();
        Assert.Throws<InvalidOperationException>(() =>
            new StrictSharedMemory<VersionedSchemaV2>(
                uniqueName, schemaV2, create: false, compatibility: SchemaCompatibility.Strict));
    }

    [Test]
    public void IVersionedSchema_IsCompatibleWith_ShouldWork()
    {
        var v1 = new VersionedSchemaV1();
        var v2 = new VersionedSchemaV2();

        Assert.That(v1.IsCompatibleWith(1), Is.True);
        Assert.That(v1.IsCompatibleWith(2), Is.False);

        Assert.That(v2.IsCompatibleWith(1), Is.True);  // V2 is backward compatible
        Assert.That(v2.IsCompatibleWith(2), Is.True);
    }

    #endregion

    #region Extended Type Tests

    public struct ExtendedTypeSchema : ISharedMemorySchema
    {
        public const string GuidField = "GuidValue";
        public const string DateTimeField = "DateTimeValue";
        public const string TimeSpanField = "TimeSpanValue";
        public const string DateTimeOffsetField = "DateTimeOffsetValue";
        public const string DecimalField = "DecimalValue";
        public const string GuidArrayField = "GuidArray";
        public const string DateTimeArrayField = "DateTimeArray";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<Guid>(GuidField);
            yield return FieldDefinition.Scalar<DateTime>(DateTimeField);
            yield return FieldDefinition.Scalar<TimeSpan>(TimeSpanField);
            yield return FieldDefinition.Scalar<DateTimeOffset>(DateTimeOffsetField);
            yield return FieldDefinition.Scalar<decimal>(DecimalField);
            yield return FieldDefinition.Array<Guid>(GuidArrayField, 5);
            yield return FieldDefinition.Array<DateTime>(DateTimeArrayField, 3);
        }
    }

    [Test]
    public void WriteRead_Guid_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_Guid", schema);

        var testGuid = Guid.NewGuid();
        memory.Write(ExtendedTypeSchema.GuidField, testGuid);
        var value = memory.Read<Guid>(ExtendedTypeSchema.GuidField);

        Assert.That(value, Is.EqualTo(testGuid));
    }

    [Test]
    public void WriteRead_Guid_EmptyGuid_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_GuidEmpty", schema);

        memory.Write(ExtendedTypeSchema.GuidField, Guid.Empty);
        var value = memory.Read<Guid>(ExtendedTypeSchema.GuidField);

        Assert.That(value, Is.EqualTo(Guid.Empty));
    }

    [Test]
    public void WriteRead_GuidArray_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_GuidArr", schema);

        var guids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        memory.WriteArray<Guid>(ExtendedTypeSchema.GuidArrayField, guids);

        var readArray = new Guid[5];
        memory.ReadArray<Guid>(ExtendedTypeSchema.GuidArrayField, readArray);

        for (int i = 0; i < guids.Length; i++)
            Assert.That(readArray[i], Is.EqualTo(guids[i]));
    }

    [Test]
    public void WriteRead_DateTime_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_DateTime", schema);

        var testDateTime = new DateTime(2024, 12, 25, 10, 30, 45, DateTimeKind.Utc);
        memory.Write(ExtendedTypeSchema.DateTimeField, testDateTime);
        var value = memory.Read<DateTime>(ExtendedTypeSchema.DateTimeField);

        Assert.That(value, Is.EqualTo(testDateTime));
    }

    [Test]
    public void WriteRead_DateTime_MinMax_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_DTMinMax", schema);

        memory.Write(ExtendedTypeSchema.DateTimeField, DateTime.MinValue);
        Assert.That(memory.Read<DateTime>(ExtendedTypeSchema.DateTimeField), Is.EqualTo(DateTime.MinValue));

        memory.Write(ExtendedTypeSchema.DateTimeField, DateTime.MaxValue);
        Assert.That(memory.Read<DateTime>(ExtendedTypeSchema.DateTimeField), Is.EqualTo(DateTime.MaxValue));
    }

    [Test]
    public void WriteRead_DateTimeArray_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_DTArr", schema);

        var dates = new[] { DateTime.Now, DateTime.UtcNow, DateTime.Today };
        memory.WriteArray<DateTime>(ExtendedTypeSchema.DateTimeArrayField, dates);

        var readArray = new DateTime[3];
        memory.ReadArray<DateTime>(ExtendedTypeSchema.DateTimeArrayField, readArray);

        for (int i = 0; i < dates.Length; i++)
            Assert.That(readArray[i], Is.EqualTo(dates[i]));
    }

    [Test]
    public void WriteRead_TimeSpan_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_TimeSpan", schema);

        var testTimeSpan = TimeSpan.FromHours(2.5);
        memory.Write(ExtendedTypeSchema.TimeSpanField, testTimeSpan);
        var value = memory.Read<TimeSpan>(ExtendedTypeSchema.TimeSpanField);

        Assert.That(value, Is.EqualTo(testTimeSpan));
    }

    [Test]
    public void WriteRead_TimeSpan_MinMax_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_TSMinMax", schema);

        memory.Write(ExtendedTypeSchema.TimeSpanField, TimeSpan.MinValue);
        Assert.That(memory.Read<TimeSpan>(ExtendedTypeSchema.TimeSpanField), Is.EqualTo(TimeSpan.MinValue));

        memory.Write(ExtendedTypeSchema.TimeSpanField, TimeSpan.MaxValue);
        Assert.That(memory.Read<TimeSpan>(ExtendedTypeSchema.TimeSpanField), Is.EqualTo(TimeSpan.MaxValue));

        memory.Write(ExtendedTypeSchema.TimeSpanField, TimeSpan.Zero);
        Assert.That(memory.Read<TimeSpan>(ExtendedTypeSchema.TimeSpanField), Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void WriteRead_TimeSpan_Negative_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_TSNeg", schema);

        var negativeSpan = TimeSpan.FromDays(-5);
        memory.Write(ExtendedTypeSchema.TimeSpanField, negativeSpan);
        Assert.That(memory.Read<TimeSpan>(ExtendedTypeSchema.TimeSpanField), Is.EqualTo(negativeSpan));
    }

    [Test]
    public void WriteRead_DateTimeOffset_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_DateTimeOffset", schema);

        var testDateTimeOffset = new DateTimeOffset(2024, 12, 25, 10, 30, 45, TimeSpan.FromHours(9));
        memory.Write(ExtendedTypeSchema.DateTimeOffsetField, testDateTimeOffset);
        var value = memory.Read<DateTimeOffset>(ExtendedTypeSchema.DateTimeOffsetField);

        Assert.That(value, Is.EqualTo(testDateTimeOffset));
    }

    [Test]
    public void WriteRead_DateTimeOffset_DifferentOffsets_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_DTOOff", schema);

        var utcOffset = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        memory.Write(ExtendedTypeSchema.DateTimeOffsetField, utcOffset);
        Assert.That(memory.Read<DateTimeOffset>(ExtendedTypeSchema.DateTimeOffsetField), Is.EqualTo(utcOffset));

        var negativeOffset = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(-5));
        memory.Write(ExtendedTypeSchema.DateTimeOffsetField, negativeOffset);
        Assert.That(memory.Read<DateTimeOffset>(ExtendedTypeSchema.DateTimeOffsetField), Is.EqualTo(negativeOffset));
    }

    [Test]
    public void WriteRead_Decimal_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_Decimal", schema);

        var testDecimal = 12345.6789m;
        memory.Write(ExtendedTypeSchema.DecimalField, testDecimal);
        var value = memory.Read<decimal>(ExtendedTypeSchema.DecimalField);

        Assert.That(value, Is.EqualTo(testDecimal));
    }

    [Test]
    public void WriteRead_Decimal_MinMax_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_DecMinMax", schema);

        memory.Write(ExtendedTypeSchema.DecimalField, decimal.MinValue);
        Assert.That(memory.Read<decimal>(ExtendedTypeSchema.DecimalField), Is.EqualTo(decimal.MinValue));

        memory.Write(ExtendedTypeSchema.DecimalField, decimal.MaxValue);
        Assert.That(memory.Read<decimal>(ExtendedTypeSchema.DecimalField), Is.EqualTo(decimal.MaxValue));

        memory.Write(ExtendedTypeSchema.DecimalField, decimal.Zero);
        Assert.That(memory.Read<decimal>(ExtendedTypeSchema.DecimalField), Is.EqualTo(decimal.Zero));
    }

    [Test]
    public void WriteRead_Decimal_NegativeAndPrecision_ShouldRoundTrip()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_DecPrec", schema);

        memory.Write(ExtendedTypeSchema.DecimalField, -99999.99999m);
        Assert.That(memory.Read<decimal>(ExtendedTypeSchema.DecimalField), Is.EqualTo(-99999.99999m));

        memory.Write(ExtendedTypeSchema.DecimalField, 0.0000000001m);
        Assert.That(memory.Read<decimal>(ExtendedTypeSchema.DecimalField), Is.EqualTo(0.0000000001m));
    }

    #endregion

    #region Custom Struct Tests

    public struct Vector3
    {
        public float X;
        public float Y;
        public float Z;

        public Vector3(float x, float y, float z) => (X, Y, Z) = (x, y, z);
    }

    public struct ComplexStruct
    {
        public int Id;
        public double Value;
        public long Timestamp;
        public byte Flags;
    }

    public struct CustomStructSchema : ISharedMemorySchema
    {
        public const string PositionField = "Position";
        public const string WaypointsField = "Waypoints";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Struct<Vector3>(PositionField);
            yield return FieldDefinition.StructArray<Vector3>(WaypointsField, 5);
        }
    }

    public struct ComplexStructSchema : ISharedMemorySchema
    {
        public const string DataField = "Data";
        public const string DataArrayField = "DataArray";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Struct<ComplexStruct>(DataField);
            yield return FieldDefinition.StructArray<ComplexStruct>(DataArrayField, 10);
        }
    }

    [Test]
    public void WriteRead_CustomStruct_ShouldRoundTrip()
    {
        var schema = new CustomStructSchema();
        using var memory = new StrictSharedMemory<CustomStructSchema>(TestBufferName + "_CustomStruct", schema);

        var testVector = new Vector3(1.5f, 2.5f, 3.5f);
        memory.Write(CustomStructSchema.PositionField, testVector);
        var value = memory.Read<Vector3>(CustomStructSchema.PositionField);

        Assert.That(value.X, Is.EqualTo(1.5f).Within(0.001f));
        Assert.That(value.Y, Is.EqualTo(2.5f).Within(0.001f));
        Assert.That(value.Z, Is.EqualTo(3.5f).Within(0.001f));
    }

    [Test]
    public void WriteRead_CustomStruct_ZeroValues_ShouldRoundTrip()
    {
        var schema = new CustomStructSchema();
        using var memory = new StrictSharedMemory<CustomStructSchema>(TestBufferName + "_StructZero", schema);

        var zeroVector = new Vector3(0, 0, 0);
        memory.Write(CustomStructSchema.PositionField, zeroVector);
        var value = memory.Read<Vector3>(CustomStructSchema.PositionField);

        Assert.That(value.X, Is.EqualTo(0f));
        Assert.That(value.Y, Is.EqualTo(0f));
        Assert.That(value.Z, Is.EqualTo(0f));
    }

    [Test]
    public void WriteRead_CustomStruct_NegativeValues_ShouldRoundTrip()
    {
        var schema = new CustomStructSchema();
        using var memory = new StrictSharedMemory<CustomStructSchema>(TestBufferName + "_StructNeg", schema);

        var negVector = new Vector3(-100.5f, -200.25f, -0.001f);
        memory.Write(CustomStructSchema.PositionField, negVector);
        var value = memory.Read<Vector3>(CustomStructSchema.PositionField);

        Assert.That(value.X, Is.EqualTo(-100.5f).Within(0.001f));
        Assert.That(value.Y, Is.EqualTo(-200.25f).Within(0.001f));
        Assert.That(value.Z, Is.EqualTo(-0.001f).Within(0.0001f));
    }

    [Test]
    public void WriteRead_CustomStructArray_ShouldRoundTrip()
    {
        var schema = new CustomStructSchema();
        using var memory = new StrictSharedMemory<CustomStructSchema>(TestBufferName + "_StructArray", schema);

        var waypoints = new Vector3[]
        {
            new(0, 0, 0),
            new(1, 2, 3),
            new(4, 5, 6)
        };

        memory.WriteArray<Vector3>(CustomStructSchema.WaypointsField, waypoints);

        var readArray = new Vector3[5];
        memory.ReadArray<Vector3>(CustomStructSchema.WaypointsField, readArray);

        for (int i = 0; i < waypoints.Length; i++)
        {
            Assert.That(readArray[i].X, Is.EqualTo(waypoints[i].X).Within(0.001f));
            Assert.That(readArray[i].Y, Is.EqualTo(waypoints[i].Y).Within(0.001f));
            Assert.That(readArray[i].Z, Is.EqualTo(waypoints[i].Z).Within(0.001f));
        }
    }

    [Test]
    public void WriteRead_CustomStructArray_FullCapacity_ShouldRoundTrip()
    {
        var schema = new CustomStructSchema();
        using var memory = new StrictSharedMemory<CustomStructSchema>(TestBufferName + "_StructFull", schema);

        var waypoints = new Vector3[5];
        for (int i = 0; i < 5; i++)
            waypoints[i] = new Vector3(i * 10f, i * 20f, i * 30f);

        memory.WriteArray<Vector3>(CustomStructSchema.WaypointsField, waypoints);

        var readArray = new Vector3[5];
        memory.ReadArray<Vector3>(CustomStructSchema.WaypointsField, readArray);

        for (int i = 0; i < waypoints.Length; i++)
        {
            Assert.That(readArray[i].X, Is.EqualTo(waypoints[i].X).Within(0.001f));
            Assert.That(readArray[i].Y, Is.EqualTo(waypoints[i].Y).Within(0.001f));
            Assert.That(readArray[i].Z, Is.EqualTo(waypoints[i].Z).Within(0.001f));
        }
    }

    [Test]
    public void WriteRead_ComplexStruct_ShouldRoundTrip()
    {
        var schema = new ComplexStructSchema();
        using var memory = new StrictSharedMemory<ComplexStructSchema>(TestBufferName + "_Complex", schema);

        var data = new ComplexStruct
        {
            Id = 12345,
            Value = 3.14159265359,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Flags = 0xFF
        };

        memory.Write(ComplexStructSchema.DataField, data);
        var value = memory.Read<ComplexStruct>(ComplexStructSchema.DataField);

        Assert.That(value.Id, Is.EqualTo(data.Id));
        Assert.That(value.Value, Is.EqualTo(data.Value).Within(0.0000001));
        Assert.That(value.Timestamp, Is.EqualTo(data.Timestamp));
        Assert.That(value.Flags, Is.EqualTo(data.Flags));
    }

    [Test]
    public void WriteRead_ComplexStructArray_ShouldRoundTrip()
    {
        var schema = new ComplexStructSchema();
        using var memory = new StrictSharedMemory<ComplexStructSchema>(TestBufferName + "_ComplexArr", schema);

        var dataArray = new ComplexStruct[3];
        for (int i = 0; i < 3; i++)
        {
            dataArray[i] = new ComplexStruct
            {
                Id = i + 1,
                Value = i * 1.5,
                Timestamp = 1000 + i,
                Flags = (byte)(i * 10)
            };
        }

        memory.WriteArray<ComplexStruct>(ComplexStructSchema.DataArrayField, dataArray);

        var readArray = new ComplexStruct[10];
        memory.ReadArray<ComplexStruct>(ComplexStructSchema.DataArrayField, readArray);

        for (int i = 0; i < dataArray.Length; i++)
        {
            Assert.That(readArray[i].Id, Is.EqualTo(dataArray[i].Id));
            Assert.That(readArray[i].Value, Is.EqualTo(dataArray[i].Value).Within(0.001));
            Assert.That(readArray[i].Timestamp, Is.EqualTo(dataArray[i].Timestamp));
            Assert.That(readArray[i].Flags, Is.EqualTo(dataArray[i].Flags));
        }
    }

    #endregion

    #region Enum Tests

    public enum Status : int
    {
        None = 0,
        Active = 1,
        Paused = 2,
        Stopped = 3
    }

    public enum Priority : byte
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    public enum LongEnum : long
    {
        Small = 1L,
        Large = 1_000_000_000_000L,
        Negative = -9_999_999_999L
    }

    public enum ShortEnum : short
    {
        Min = short.MinValue,
        Zero = 0,
        Max = short.MaxValue
    }

    public enum UShortEnum : ushort
    {
        Min = ushort.MinValue,
        Mid = 32000,
        Max = ushort.MaxValue
    }

    public struct EnumSchema : ISharedMemorySchema
    {
        public const string StatusField = "Status";
        public const string PriorityField = "Priority";
        public const string LongEnumField = "LongEnum";
        public const string ShortEnumField = "ShortEnum";
        public const string UShortEnumField = "UShortEnum";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<Status>(StatusField);
            yield return FieldDefinition.Scalar<Priority>(PriorityField);
            yield return FieldDefinition.Scalar<LongEnum>(LongEnumField);
            yield return FieldDefinition.Scalar<ShortEnum>(ShortEnumField);
            yield return FieldDefinition.Scalar<UShortEnum>(UShortEnumField);
        }
    }

    [Test]
    public void WriteRead_IntEnum_ShouldRoundTrip()
    {
        var schema = new EnumSchema();
        using var memory = new StrictSharedMemory<EnumSchema>(TestBufferName + "_IntEnum", schema);

        memory.Write(EnumSchema.StatusField, Status.Active);
        var value = memory.Read<Status>(EnumSchema.StatusField);

        Assert.That(value, Is.EqualTo(Status.Active));
    }

    [Test]
    public void WriteRead_IntEnum_AllValues_ShouldRoundTrip()
    {
        var schema = new EnumSchema();
        using var memory = new StrictSharedMemory<EnumSchema>(TestBufferName + "_IntEnumAll", schema);

        foreach (Status status in Enum.GetValues<Status>())
        {
            memory.Write(EnumSchema.StatusField, status);
            Assert.That(memory.Read<Status>(EnumSchema.StatusField), Is.EqualTo(status));
        }
    }

    [Test]
    public void WriteRead_ByteEnum_ShouldRoundTrip()
    {
        var schema = new EnumSchema();
        using var memory = new StrictSharedMemory<EnumSchema>(TestBufferName + "_ByteEnum", schema);

        memory.Write(EnumSchema.PriorityField, Priority.High);
        var value = memory.Read<Priority>(EnumSchema.PriorityField);

        Assert.That(value, Is.EqualTo(Priority.High));
    }

    [Test]
    public void WriteRead_ByteEnum_AllValues_ShouldRoundTrip()
    {
        var schema = new EnumSchema();
        using var memory = new StrictSharedMemory<EnumSchema>(TestBufferName + "_ByteEnumAll", schema);

        foreach (Priority priority in Enum.GetValues<Priority>())
        {
            memory.Write(EnumSchema.PriorityField, priority);
            Assert.That(memory.Read<Priority>(EnumSchema.PriorityField), Is.EqualTo(priority));
        }
    }

    [Test]
    public void WriteRead_LongEnum_ShouldRoundTrip()
    {
        var schema = new EnumSchema();
        using var memory = new StrictSharedMemory<EnumSchema>(TestBufferName + "_LongEnum", schema);

        memory.Write(EnumSchema.LongEnumField, LongEnum.Large);
        Assert.That(memory.Read<LongEnum>(EnumSchema.LongEnumField), Is.EqualTo(LongEnum.Large));

        memory.Write(EnumSchema.LongEnumField, LongEnum.Negative);
        Assert.That(memory.Read<LongEnum>(EnumSchema.LongEnumField), Is.EqualTo(LongEnum.Negative));
    }

    [Test]
    public void WriteRead_ShortEnum_ShouldRoundTrip()
    {
        var schema = new EnumSchema();
        using var memory = new StrictSharedMemory<EnumSchema>(TestBufferName + "_ShortEnum", schema);

        foreach (ShortEnum val in Enum.GetValues<ShortEnum>())
        {
            memory.Write(EnumSchema.ShortEnumField, val);
            Assert.That(memory.Read<ShortEnum>(EnumSchema.ShortEnumField), Is.EqualTo(val));
        }
    }

    [Test]
    public void WriteRead_UShortEnum_ShouldRoundTrip()
    {
        var schema = new EnumSchema();
        using var memory = new StrictSharedMemory<EnumSchema>(TestBufferName + "_UShortEnum", schema);

        foreach (UShortEnum val in Enum.GetValues<UShortEnum>())
        {
            memory.Write(EnumSchema.UShortEnumField, val);
            Assert.That(memory.Read<UShortEnum>(EnumSchema.UShortEnumField), Is.EqualTo(val));
        }
    }

    #endregion

    #region Multiple Field Interaction Tests

    [Test]
    public void ExtendedTypes_MultipleFields_ShouldNotInterfere()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_ExtMulti", schema);

        var guid = Guid.NewGuid();
        var dateTime = DateTime.UtcNow;
        var timeSpan = TimeSpan.FromMinutes(30);
        var dateTimeOffset = DateTimeOffset.Now;
        var decimalVal = 123.456m;

        memory.Write(ExtendedTypeSchema.GuidField, guid);
        memory.Write(ExtendedTypeSchema.DateTimeField, dateTime);
        memory.Write(ExtendedTypeSchema.TimeSpanField, timeSpan);
        memory.Write(ExtendedTypeSchema.DateTimeOffsetField, dateTimeOffset);
        memory.Write(ExtendedTypeSchema.DecimalField, decimalVal);

        Assert.That(memory.Read<Guid>(ExtendedTypeSchema.GuidField), Is.EqualTo(guid));
        Assert.That(memory.Read<DateTime>(ExtendedTypeSchema.DateTimeField), Is.EqualTo(dateTime));
        Assert.That(memory.Read<TimeSpan>(ExtendedTypeSchema.TimeSpanField), Is.EqualTo(timeSpan));
        Assert.That(memory.Read<DateTimeOffset>(ExtendedTypeSchema.DateTimeOffsetField), Is.EqualTo(dateTimeOffset));
        Assert.That(memory.Read<decimal>(ExtendedTypeSchema.DecimalField), Is.EqualTo(decimalVal));
    }

    [Test]
    public void ExtendedTypes_RepeatedWriteRead_ShouldWork()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_ExtRepeat", schema);

        for (int i = 0; i < 100; i++)
        {
            var guid = Guid.NewGuid();
            memory.Write(ExtendedTypeSchema.GuidField, guid);
            Assert.That(memory.Read<Guid>(ExtendedTypeSchema.GuidField), Is.EqualTo(guid));
        }
    }

    #endregion

    #region Extended Type Concurrent Tests

    [Test]
    public async Task ExtendedTypes_ConcurrentGuid_WithLocks_ShouldBeThreadSafe()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_ConcGuid", schema);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 1000;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    var guid = Guid.NewGuid();
                    using (var _ = memory.AcquireWriteLock())
                    {
                        memory.Write(ExtendedTypeSchema.GuidField, guid);
                    }

                    Guid readValue;
                    using (var _ = memory.AcquireReadLock())
                    {
                        readValue = memory.Read<Guid>(ExtendedTypeSchema.GuidField);
                    }
                    // Note: value may have changed between write and read by other threads
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public async Task ExtendedTypes_ConcurrentDateTime_WithLocks_ShouldBeThreadSafe()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_ConcDT", schema);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 1000;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    var dt = DateTime.UtcNow.AddTicks(i);
                    using (var _ = memory.AcquireWriteLock())
                    {
                        memory.Write(ExtendedTypeSchema.DateTimeField, dt);
                    }

                    using (memory.AcquireReadLock())
                    {
                        memory.Read<DateTime>(ExtendedTypeSchema.DateTimeField);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public async Task ExtendedTypes_ConcurrentDecimal_WithLocks_ShouldBeThreadSafe()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_ConcDec", schema);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 1000;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    var value = taskId * 1000m + i + 0.123456789m;
                    using (var _ = memory.AcquireWriteLock())
                    {
                        memory.Write(ExtendedTypeSchema.DecimalField, value);
                    }

                    using (memory.AcquireReadLock())
                    {
                        memory.Read<decimal>(ExtendedTypeSchema.DecimalField);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public async Task ExtendedTypes_ConcurrentStruct_WithLocks_ShouldBeThreadSafe()
    {
        var schema = new CustomStructSchema();
        using var memory = new StrictSharedMemory<CustomStructSchema>(TestBufferName + "_ConcStruct", schema);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 1000;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    var vec = new Vector3(taskId * 100f + i, i * 2f, i * 3f);
                    using (var _ = memory.AcquireWriteLock())
                    {
                        memory.Write(CustomStructSchema.PositionField, vec);
                    }

                    using (memory.AcquireReadLock())
                    {
                        memory.Read<Vector3>(CustomStructSchema.PositionField);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public async Task ExtendedTypes_ConcurrentEnum_WithLocks_ShouldBeThreadSafe()
    {
        var schema = new EnumSchema();
        using var memory = new StrictSharedMemory<EnumSchema>(TestBufferName + "_ConcEnum", schema);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 1000;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                var statuses = Enum.GetValues<Status>();
                for (int i = 0; i < iterations; i++)
                {
                    var status = statuses[i % statuses.Length];
                    using (var _ = memory.AcquireWriteLock())
                    {
                        memory.Write(EnumSchema.StatusField, status);
                    }

                    using (memory.AcquireReadLock())
                    {
                        memory.Read<Status>(EnumSchema.StatusField);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public void TypeMismatch_SameSize_ShouldThrow()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_TypeMismatch", schema);

        // Guid is 16 bytes, DateTimeOffset is also 16 bytes
        // Writing Guid should not allow reading as DateTimeOffset
        var guid = Guid.NewGuid();
        memory.Write(ExtendedTypeSchema.GuidField, guid);

        // This should throw because we're trying to read a Guid field as DateTimeOffset
        Assert.Throws<InvalidOperationException>(() =>
            memory.Read<DateTimeOffset>(ExtendedTypeSchema.GuidField));
    }

    [Test]
    public void TypeMismatch_DateTime_TimeSpan_ShouldThrow()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_TypeMismatch2", schema);

        // DateTime is 8 bytes, TimeSpan is also 8 bytes
        var dt = DateTime.UtcNow;
        memory.Write(ExtendedTypeSchema.DateTimeField, dt);

        // This should throw because we're trying to read a DateTime field as TimeSpan
        Assert.Throws<InvalidOperationException>(() =>
            memory.Read<TimeSpan>(ExtendedTypeSchema.DateTimeField));
    }

    #endregion

    #region Auto-Lock Tests

    [Test]
    public async Task AutoLock_ConcurrentGuid_WithoutExplicitLock_ShouldBeThreadSafe()
    {
        // This test verifies that 16-byte types are automatically locked
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_AutoGuid", schema);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 1000;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    var guid = Guid.NewGuid();
                    // No explicit lock - relying on auto-lock
                    memory.Write(ExtendedTypeSchema.GuidField, guid);
                    memory.Read<Guid>(ExtendedTypeSchema.GuidField);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public async Task AutoLock_ConcurrentDecimal_WithoutExplicitLock_ShouldBeThreadSafe()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_AutoDec", schema);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 1000;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    var value = taskId * 1000m + i + 0.123456789m;
                    // No explicit lock - relying on auto-lock
                    memory.Write(ExtendedTypeSchema.DecimalField, value);
                    memory.Read<decimal>(ExtendedTypeSchema.DecimalField);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public async Task AutoLock_ConcurrentDateTimeOffset_WithoutExplicitLock_ShouldBeThreadSafe()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_AutoDTO", schema);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 1000;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    var value = DateTimeOffset.UtcNow.AddTicks(taskId * 1000 + i);
                    // No explicit lock - relying on auto-lock
                    memory.Write(ExtendedTypeSchema.DateTimeOffsetField, value);
                    memory.Read<DateTimeOffset>(ExtendedTypeSchema.DateTimeOffsetField);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public async Task AutoLock_ConcurrentLargeStruct_WithoutExplicitLock_ShouldBeThreadSafe()
    {
        var schema = new CustomStructSchema();
        using var memory = new StrictSharedMemory<CustomStructSchema>(TestBufferName + "_AutoStruct", schema);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 1000;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    var vec = new Vector3(taskId * 100f + i, i * 2f, i * 3f);
                    // No explicit lock - relying on auto-lock (Vector3 is 12 bytes)
                    memory.Write(CustomStructSchema.PositionField, vec);
                    memory.Read<Vector3>(CustomStructSchema.PositionField);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public async Task AutoLock_ConcurrentString_WithoutExplicitLock_ShouldBeThreadSafe()
    {
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_AutoString", new TestSchema());

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 500;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    var str = $"Task{taskId}-{i}";
                    // No explicit lock - relying on auto-lock
                    memory.WriteString(TestSchema.StringField, str);
                    memory.ReadString(TestSchema.StringField);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public async Task AutoLock_ConcurrentArray_WithoutExplicitLock_ShouldBeThreadSafe()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_AutoArr", schema);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 500;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    var guids = new[] { Guid.NewGuid(), Guid.NewGuid() };
                    // No explicit lock - relying on auto-lock
                    memory.WriteArray<Guid>(ExtendedTypeSchema.GuidArrayField, guids);

                    var readArray = new Guid[5];
                    memory.ReadArray<Guid>(ExtendedTypeSchema.GuidArrayField, readArray);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public void AutoLock_ReentrantLock_ShouldNotDeadlock()
    {
        var schema = new ExtendedTypeSchema();
        using var memory = new StrictSharedMemory<ExtendedTypeSchema>(TestBufferName + "_Reentrant", schema);

        // Acquire explicit lock, then call Write (which would try to auto-lock)
        // This should NOT deadlock because of reentrant lock detection
        using (var _ = memory.AcquireWriteLock())
        {
            // This would try to auto-lock (Guid is 16 bytes), but should detect existing lock
            memory.Write(ExtendedTypeSchema.GuidField, Guid.NewGuid());
            memory.Write(ExtendedTypeSchema.DecimalField, 123.456m);
            memory.Write(ExtendedTypeSchema.DateTimeOffsetField, DateTimeOffset.UtcNow);
        }

        // Verify values can be read
        Assert.DoesNotThrow(() => memory.Read<Guid>(ExtendedTypeSchema.GuidField));
    }

    #endregion

    #region Dispose and Lifecycle Tests

    [Test]
    public void Dispose_ThenWrite_ShouldThrowObjectDisposedException()
    {
        var schema = new TestSchema();
        var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_DisposeWrite", schema);
        memory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => memory.Write(TestSchema.IntField, 42));
    }

    [Test]
    public void Dispose_ThenRead_ShouldThrowObjectDisposedException()
    {
        var schema = new TestSchema();
        var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_DisposeRead", schema);
        memory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => memory.Read<int>(TestSchema.IntField));
    }

    [Test]
    public void Dispose_ThenWriteString_ShouldThrowObjectDisposedException()
    {
        var schema = new TestSchema();
        var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_DisposeStr", schema);
        memory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => memory.WriteString(TestSchema.StringField, "test"));
    }

    [Test]
    public void Dispose_ThenReadString_ShouldThrowObjectDisposedException()
    {
        var schema = new TestSchema();
        var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_DisposeReadStr", schema);
        memory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => memory.ReadString(TestSchema.StringField));
    }

    [Test]
    public void Dispose_ThenAcquireWriteLock_ShouldThrowObjectDisposedException()
    {
        var schema = new TestSchema();
        var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_DisposeLock", schema);
        memory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => memory.AcquireWriteLock());
    }

    [Test]
    public void Dispose_ThenAcquireReadLock_ShouldThrowObjectDisposedException()
    {
        var schema = new TestSchema();
        var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_DisposeRLock", schema);
        memory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => memory.AcquireReadLock());
    }

    [Test]
    public void DoubleDispose_ShouldNotThrow()
    {
        var schema = new TestSchema();
        var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_DoubleDispose", schema);

        Assert.DoesNotThrow(() =>
        {
            memory.Dispose();
            memory.Dispose();
            memory.Dispose();
        });
    }

    [Test]
    public void WriteLock_DoubleDispose_ShouldNotThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_LockDoubleDisp", schema);

        var lockObj = memory.AcquireWriteLock();
        Assert.DoesNotThrow(() =>
        {
            lockObj.Dispose();
            lockObj.Dispose();
        });
    }

    [Test]
    public void ReadLock_DoubleDispose_ShouldNotThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_RLockDoubleDisp", schema);

        var lockObj = memory.AcquireReadLock();
        Assert.DoesNotThrow(() =>
        {
            lockObj.Dispose();
            lockObj.Dispose();
        });
    }

    #endregion

    #region IPC Multi-Instance Tests

    [Test]
    public void IPC_TwoInstances_SameMemory_ShouldShareData()
    {
        var schema = new TestSchema();
        const string sharedName = "IPC_SharedData_Test";

        using var writer = new StrictSharedMemory<TestSchema>(sharedName, schema, create: true);
        using var reader = new StrictSharedMemory<TestSchema>(sharedName, schema, create: false);

        writer.Write(TestSchema.IntField, 12345);
        writer.Write(TestSchema.DoubleField, 3.14159);
        writer.WriteString(TestSchema.StringField, "SharedTest");

        Assert.That(reader.Read<int>(TestSchema.IntField), Is.EqualTo(12345));
        Assert.That(reader.Read<double>(TestSchema.DoubleField), Is.EqualTo(3.14159).Within(0.00001));
        Assert.That(reader.ReadString(TestSchema.StringField), Is.EqualTo("SharedTest"));
    }

    [Test]
    public void IPC_TwoInstances_ExtendedTypes_ShouldShareData()
    {
        var schema = new ExtendedTypeSchema();
        const string sharedName = "IPC_ExtendedTypes_Test";

        using var writer = new StrictSharedMemory<ExtendedTypeSchema>(sharedName, schema, create: true);
        using var reader = new StrictSharedMemory<ExtendedTypeSchema>(sharedName, schema, create: false);

        var guid = Guid.NewGuid();
        var dto = DateTimeOffset.UtcNow;
        var dec = 99999.12345m;

        writer.Write(ExtendedTypeSchema.GuidField, guid);
        writer.Write(ExtendedTypeSchema.DateTimeOffsetField, dto);
        writer.Write(ExtendedTypeSchema.DecimalField, dec);

        Assert.That(reader.Read<Guid>(ExtendedTypeSchema.GuidField), Is.EqualTo(guid));
        Assert.That(reader.Read<DateTimeOffset>(ExtendedTypeSchema.DateTimeOffsetField), Is.EqualTo(dto));
        Assert.That(reader.Read<decimal>(ExtendedTypeSchema.DecimalField), Is.EqualTo(dec));
    }

    [Test]
    public async Task IPC_ConcurrentAccess_WithLocks_ShouldWork()
    {
        var schema = new TestSchema();
        const string sharedName = "IPC_Concurrent_Test";

        using var instance1 = new StrictSharedMemory<TestSchema>(sharedName, schema, create: true);
        using var instance2 = new StrictSharedMemory<TestSchema>(sharedName, schema, create: false);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 500;

        var task1 = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    using (instance1.AcquireWriteLock())
                    {
                        instance1.Write(TestSchema.IntField, i);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Instance1: {ex.Message}");
            }
        });

        var task2 = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    using (instance2.AcquireReadLock())
                    {
                        instance2.Read<int>(TestSchema.IntField);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Instance2: {ex.Message}");
            }
        });

        await Task.WhenAll(task1, task2);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public void IPC_ReaderFirst_ShouldFail()
    {
        var schema = new TestSchema();
        var sharedName = "IPC_ReaderFirst_" + Guid.NewGuid().ToString("N");

        // Reader tries to open non-existent memory - should throw some exception
        // FileNotFoundException if memory doesn't exist, or InvalidOperationException if stale memory exists
        Assert.That(() => new StrictSharedMemory<TestSchema>(sharedName, schema, create: false),
            Throws.InstanceOf<Exception>());
    }

    #endregion

    #region Boundary Condition Tests

    [Test]
    public void AtomicBoundary_Long_ShouldNotAutoLock()
    {
        // long is exactly 8 bytes - should NOT auto-lock (threshold is > 8)
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_AtomicLong", schema);

        // This should work without any locking overhead
        memory.Write(TestSchema.LongField, long.MaxValue);
        Assert.That(memory.Read<long>(TestSchema.LongField), Is.EqualTo(long.MaxValue));

        memory.Write(TestSchema.LongField, long.MinValue);
        Assert.That(memory.Read<long>(TestSchema.LongField), Is.EqualTo(long.MinValue));
    }

    [Test]
    public void AtomicBoundary_Double_ShouldNotAutoLock()
    {
        // double is exactly 8 bytes - should NOT auto-lock
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_AtomicDouble", schema);

        memory.Write(TestSchema.DoubleField, double.MaxValue);
        Assert.That(memory.Read<double>(TestSchema.DoubleField), Is.EqualTo(double.MaxValue));

        memory.Write(TestSchema.DoubleField, double.MinValue);
        Assert.That(memory.Read<double>(TestSchema.DoubleField), Is.EqualTo(double.MinValue));
    }

    [Test]
    public async Task AtomicBoundary_Long_ConcurrentWithoutLock_ShouldWork()
    {
        // long (8 bytes) is atomic on x64, so concurrent access without lock should be safe
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_ConcLong", schema);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 5000;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    memory.Write(TestSchema.LongField, taskId * 1000000L + i);
                    memory.Read<long>(TestSchema.LongField);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public void SmallTypes_ShouldNotAutoLock()
    {
        // Types <= 8 bytes should not auto-lock
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_SmallTypes", schema);

        // int (4 bytes)
        memory.Write(TestSchema.IntField, int.MaxValue);
        Assert.That(memory.Read<int>(TestSchema.IntField), Is.EqualTo(int.MaxValue));

        // double (8 bytes)
        memory.Write(TestSchema.DoubleField, Math.PI);
        Assert.That(memory.Read<double>(TestSchema.DoubleField), Is.EqualTo(Math.PI));
    }

    [Test]
    public void EmptyArray_WriteRead_ShouldWork()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_EmptyArr", schema);

        // Write empty array (0 elements)
        memory.WriteArray<int>(TestSchema.IntArrayField, ReadOnlySpan<int>.Empty);

        // Read should still work
        var readArray = new int[10];
        Assert.DoesNotThrow(() => memory.ReadArray<int>(TestSchema.IntArrayField, readArray));
    }

    [Test]
    public void SingleElementArray_WriteRead_ShouldWork()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_SingleArr", schema);

        var single = new int[] { 42 };
        memory.WriteArray<int>(TestSchema.IntArrayField, single);

        var readArray = new int[10];
        memory.ReadArray<int>(TestSchema.IntArrayField, readArray);

        Assert.That(readArray[0], Is.EqualTo(42));
    }

    [Test]
    public void FullCapacityArray_WriteRead_ShouldWork()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_FullArr", schema);

        var fullArray = Enumerable.Range(0, 10).ToArray();
        memory.WriteArray<int>(TestSchema.IntArrayField, fullArray);

        var readArray = new int[10];
        memory.ReadArray<int>(TestSchema.IntArrayField, readArray);

        Assert.That(readArray, Is.EqualTo(fullArray));
    }

    [Test]
    public void ArrayExceedsCapacity_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_OverArr", schema);

        var oversizedArray = new int[20]; // Capacity is 10
        Assert.Throws<ArgumentException>(() =>
            memory.WriteArray<int>(TestSchema.IntArrayField, oversizedArray));
    }

    #endregion

    #region Error Handling Tests

    [Test]
    public void InvalidFieldName_Write_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_InvalidField", schema);

        Assert.Throws<ArgumentException>(() => memory.Write("NonExistentField", 42));
    }

    [Test]
    public void InvalidFieldName_Read_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_InvalidFieldR", schema);

        Assert.Throws<ArgumentException>(() => memory.Read<int>("NonExistentField"));
    }

    [Test]
    public void InvalidFieldName_WriteString_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_InvalidFieldS", schema);

        Assert.Throws<ArgumentException>(() => memory.WriteString("NonExistentField", "test"));
    }

    [Test]
    public void InvalidFieldName_ReadString_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_InvalidFieldRS", schema);

        Assert.Throws<ArgumentException>(() => memory.ReadString("NonExistentField"));
    }

    [Test]
    public void NullString_Write_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_NullStr", schema);

        Assert.Throws<ArgumentNullException>(() => memory.WriteString(TestSchema.StringField, null!));
    }

    [Test]
    public void StringExceedsCapacity_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_LongStr", schema);

        // StringField has maxLength 32, so 31 chars + null terminator
        var longString = new string('A', 50);
        Assert.Throws<ArgumentException>(() => memory.WriteString(TestSchema.StringField, longString));
    }

    [Test]
    public void StringAtMaxCapacity_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_MaxStr", schema);

        // StringField has maxLength 32, including null terminator, so max is 31 chars
        var maxString = new string('B', 32); // This exceeds capacity
        Assert.Throws<ArgumentException>(() => memory.WriteString(TestSchema.StringField, maxString));
    }

    [Test]
    public void StringAtMaxMinusOne_ShouldWork()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_MaxStr1", schema);

        // maxLength 32 means 31 chars + null terminator
        var validString = new string('C', 31);
        Assert.DoesNotThrow(() => memory.WriteString(TestSchema.StringField, validString));
        Assert.That(memory.ReadString(TestSchema.StringField), Is.EqualTo(validString));
    }

    [Test]
    public void WriteToArrayFieldAsScalar_ShouldWork()
    {
        // Writing to array field with scalar should write to first element's position
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_ArrScalar", schema);

        // This should work but only writes one element
        memory.Write(TestSchema.IntArrayField, 42);
        Assert.That(memory.Read<int>(TestSchema.IntArrayField), Is.EqualTo(42));
    }

    [Test]
    public void WriteToStringFieldAsNonString_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_StrNonStr", schema);

        // String field is char array, writing int should fail type check
        Assert.Throws<InvalidOperationException>(() =>
            memory.Write(TestSchema.StringField, 42));
    }

    [Test]
    public void ReadStringFromNonStringField_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_NonStrRead", schema);

        Assert.Throws<InvalidOperationException>(() =>
            memory.ReadString(TestSchema.IntField));
    }

    [Test]
    public void WriteStringToNonStringField_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_NonStrWrite", schema);

        Assert.Throws<InvalidOperationException>(() =>
            memory.WriteString(TestSchema.IntField, "test"));
    }

    #endregion

    #region Schema Validation Tests

    [Test]
    public void EmptyName_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
            new StrictSharedMemory<TestSchema>("", new TestSchema()));
    }

    [Test]
    public void WhitespaceName_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
            new StrictSharedMemory<TestSchema>("   ", new TestSchema()));
    }

    public struct DuplicateFieldSchema : ISharedMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("SameName");
            yield return FieldDefinition.Scalar<int>("SameName"); // Duplicate
        }
    }

    [Test]
    public void DuplicateFieldName_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
            new StrictSharedMemory<DuplicateFieldSchema>(TestBufferName + "_DupField", new DuplicateFieldSchema()));
    }

    public struct EmptyFieldNameSchema : ISharedMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(""); // Empty name
        }
    }

    [Test]
    public void EmptyFieldName_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
            new StrictSharedMemory<EmptyFieldNameSchema>(TestBufferName + "_EmptyField", new EmptyFieldNameSchema()));
    }

    #endregion

    #region HasField and GetFieldNames Tests

    [Test]
    public void HasField_ExistingField_ShouldReturnTrue()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_HasField", schema);

        Assert.That(memory.HasField(TestSchema.IntField), Is.True);
        Assert.That(memory.HasField(TestSchema.DoubleField), Is.True);
        Assert.That(memory.HasField(TestSchema.StringField), Is.True);
    }

    [Test]
    public void HasField_NonExistingField_ShouldReturnFalse()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_NoField", schema);

        Assert.That(memory.HasField("NonExistent"), Is.False);
        Assert.That(memory.HasField(""), Is.False);
    }

    [Test]
    public void GetFieldNames_ShouldReturnAllFields()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_FieldNames", schema);

        var fieldNames = memory.GetFieldNames().ToList();

        Assert.That(fieldNames, Contains.Item(TestSchema.IntField));
        Assert.That(fieldNames, Contains.Item(TestSchema.DoubleField));
        Assert.That(fieldNames, Contains.Item(TestSchema.StringField));
        Assert.That(fieldNames, Contains.Item(TestSchema.LongField));
        Assert.That(fieldNames, Contains.Item(TestSchema.IntArrayField));
    }

    #endregion

    #region Lock Timeout Tests

    [Test]
    public void WriteLock_Timeout_ShouldThrow()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_LockTimeout", schema);

        using (memory.AcquireWriteLock())
        {
            // Try to acquire another write lock from different thread with short timeout
            var task = Task.Run(() =>
            {
                Assert.Throws<TimeoutException>(() =>
                    memory.AcquireWriteLock(TimeSpan.FromMilliseconds(50)));
            });
            task.Wait();
        }
    }

    #endregion

    #region Struct Size Edge Cases

    public struct Exactly8ByteStruct
    {
        public int A;
        public int B;
    }

    public struct Exactly9ByteStruct
    {
        public long A;
        public byte B;
    }

    public struct Size8Schema : ISharedMemorySchema
    {
        public const string Field = "Field";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Struct<Exactly8ByteStruct>(Field);
        }
    }

    public struct Size9Schema : ISharedMemorySchema
    {
        public const string Field = "Field";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Struct<Exactly9ByteStruct>(Field);
        }
    }

    [Test]
    public void Struct_Exactly8Bytes_ShouldNotAutoLock()
    {
        var schema = new Size8Schema();
        using var memory = new StrictSharedMemory<Size8Schema>(TestBufferName + "_8Byte", schema);

        var value = new Exactly8ByteStruct { A = 100, B = 200 };
        memory.Write(Size8Schema.Field, value);

        var read = memory.Read<Exactly8ByteStruct>(Size8Schema.Field);
        Assert.That(read.A, Is.EqualTo(100));
        Assert.That(read.B, Is.EqualTo(200));
    }

    [Test]
    public async Task Struct_Over8Bytes_ConcurrentWithoutExplicitLock_ShouldAutoLock()
    {
        var schema = new Size9Schema();
        using var memory = new StrictSharedMemory<Size9Schema>(TestBufferName + "_9Byte", schema);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int iterations = 1000;

        var tasks = Enumerable.Range(0, 4).Select(taskId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    var value = new Exactly9ByteStruct { A = taskId * 1000 + i, B = (byte)(i % 256) };
                    // No explicit lock - relies on auto-lock since struct > 8 bytes
                    memory.Write(Size9Schema.Field, value);
                    memory.Read<Exactly9ByteStruct>(Size9Schema.Field);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Task {taskId}: {ex.Message}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    #endregion

    #region Special Value Tests

    [Test]
    public void SpecialFloatValues_ShouldRoundTrip()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_SpecialFloat", schema);

        // NaN
        memory.Write(TestSchema.DoubleField, double.NaN);
        Assert.That(double.IsNaN(memory.Read<double>(TestSchema.DoubleField)), Is.True);

        // Positive Infinity
        memory.Write(TestSchema.DoubleField, double.PositiveInfinity);
        Assert.That(memory.Read<double>(TestSchema.DoubleField), Is.EqualTo(double.PositiveInfinity));

        // Negative Infinity
        memory.Write(TestSchema.DoubleField, double.NegativeInfinity);
        Assert.That(memory.Read<double>(TestSchema.DoubleField), Is.EqualTo(double.NegativeInfinity));

        // Epsilon
        memory.Write(TestSchema.DoubleField, double.Epsilon);
        Assert.That(memory.Read<double>(TestSchema.DoubleField), Is.EqualTo(double.Epsilon));
    }

    [Test]
    public void EmptyString_ShouldRoundTrip()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_EmptyStr", schema);

        memory.WriteString(TestSchema.StringField, "");
        Assert.That(memory.ReadString(TestSchema.StringField), Is.EqualTo(""));
    }

    [Test]
    public void UnicodeString_ShouldRoundTrip()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_Unicode", schema);

        var unicode = "한글테스트日本語🎉";
        memory.WriteString(TestSchema.StringField, unicode);
        Assert.That(memory.ReadString(TestSchema.StringField), Is.EqualTo(unicode));
    }

    [Test]
    public void StringWithNullChar_ShouldTruncate()
    {
        var schema = new TestSchema();
        using var memory = new StrictSharedMemory<TestSchema>(TestBufferName + "_NullChar", schema);

        // String with embedded null - should read only up to null
        memory.WriteString(TestSchema.StringField, "Hello");
        var read = memory.ReadString(TestSchema.StringField);
        Assert.That(read, Is.EqualTo("Hello"));
    }

    #endregion
}
