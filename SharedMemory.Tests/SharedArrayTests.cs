using NUnit.Framework;
using SharedMemory;

namespace SharedMemory.Tests;

[TestFixture]
public class SharedArrayTests
{
    private const string TestBufferName = "TestBuffer_Array";

    [TearDown]
    public void Cleanup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Test]
    public void Create_WithValidLength_ShouldSucceed()
    {
        using var array = new SharedArray<int>(TestBufferName + "_Create", 100);

        Assert.That(array.Length, Is.EqualTo(100));
    }

    [Test]
    public void Indexer_SetGet_ShouldRoundTrip()
    {
        using var array = new SharedArray<int>(TestBufferName + "_Indexer", 10);

        array[0] = 123;
        array[5] = 456;
        array[9] = 789;

        Assert.That(array[0], Is.EqualTo(123));
        Assert.That(array[5], Is.EqualTo(456));
        Assert.That(array[9], Is.EqualTo(789));
    }

    [Test]
    public void Indexer_NegativeIndex_ShouldThrow()
    {
        using var array = new SharedArray<int>(TestBufferName + "_NegativeIndex", 10);

        // Note: uint cast causes -1 to become a large positive number, which triggers out of range
        Assert.Throws<IndexOutOfRangeException>(() => array[-1] = 100);
        Assert.Throws<IndexOutOfRangeException>(() => { _ = array[-1]; });
    }

    [Test]
    public void Indexer_IndexOutOfRange_ShouldThrow()
    {
        using var array = new SharedArray<int>(TestBufferName + "_OutOfRange", 10);

        Assert.Throws<IndexOutOfRangeException>(() => array[10] = 100);
        Assert.Throws<IndexOutOfRangeException>(() => { _ = array[10]; });
    }

    [Test]
    public void DoubleArray_ShouldWork()
    {
        using var array = new SharedArray<double>(TestBufferName + "_Double", 5);

        array[0] = 1.1;
        array[1] = 2.2;
        array[2] = 3.3;
        array[3] = 4.4;
        array[4] = 5.5;

        Assert.That(array[0], Is.EqualTo(1.1).Within(0.001));
        Assert.That(array[1], Is.EqualTo(2.2).Within(0.001));
        Assert.That(array[2], Is.EqualTo(3.3).Within(0.001));
        Assert.That(array[3], Is.EqualTo(4.4).Within(0.001));
        Assert.That(array[4], Is.EqualTo(5.5).Within(0.001));
    }

    [Test]
    public void LongArray_ShouldWork()
    {
        using var array = new SharedArray<long>(TestBufferName + "_Long", 5);

        array[0] = long.MaxValue;
        array[1] = long.MinValue;
        array[2] = 0;
        array[3] = 123456789012345L;
        array[4] = -987654321098765L;

        Assert.That(array[0], Is.EqualTo(long.MaxValue));
        Assert.That(array[1], Is.EqualTo(long.MinValue));
        Assert.That(array[2], Is.EqualTo(0));
        Assert.That(array[3], Is.EqualTo(123456789012345L));
        Assert.That(array[4], Is.EqualTo(-987654321098765L));
    }

    [Test]
    public void StructArray_ShouldWork()
    {
        using var array = new SharedArray<TestPoint>(TestBufferName + "_Struct", 3);

        array[0] = new TestPoint { X = 10, Y = 20 };
        array[1] = new TestPoint { X = 30, Y = 40 };
        array[2] = new TestPoint { X = 50, Y = 60 };

        Assert.That(array[0].X, Is.EqualTo(10));
        Assert.That(array[0].Y, Is.EqualTo(20));
        Assert.That(array[1].X, Is.EqualTo(30));
        Assert.That(array[1].Y, Is.EqualTo(40));
        Assert.That(array[2].X, Is.EqualTo(50));
        Assert.That(array[2].Y, Is.EqualTo(60));
    }

    [Test]
    public void CopyFrom_ShouldPopulateArray()
    {
        using var array = new SharedArray<int>(TestBufferName + "_Fill", 10);

        var data = new int[] { 1, 2, 3, 4, 5 };
        array.CopyFrom(0, data);

        for (int i = 0; i < data.Length; i++)
        {
            Assert.That(array[i], Is.EqualTo(data[i]));
        }
    }

    [Test]
    public void CopyFrom_PartialData_ShouldWork()
    {
        using var array = new SharedArray<int>(TestBufferName + "_FillPartial", 10);

        // Initialize all to -1
        array.Fill(-1);

        var data = new int[] { 100, 200, 300 };
        array.CopyFrom(0, data);

        Assert.That(array[0], Is.EqualTo(100));
        Assert.That(array[1], Is.EqualTo(200));
        Assert.That(array[2], Is.EqualTo(300));
        Assert.That(array[3], Is.EqualTo(-1)); // Unchanged
    }

    [Test]
    public void Fill_SingleValue_ShouldPopulateArray()
    {
        using var array = new SharedArray<int>(TestBufferName + "_FillValue", 10);

        array.Fill(42);

        for (int i = 0; i < 10; i++)
        {
            Assert.That(array[i], Is.EqualTo(42));
        }
    }

    [Test]
    public void CopyTo_ShouldExtractData()
    {
        using var array = new SharedArray<int>(TestBufferName + "_CopyTo", 10);

        for (int i = 0; i < 10; i++)
        {
            array[i] = i * 10;
        }

        var buffer = new int[10];
        array.CopyTo(0, buffer);

        for (int i = 0; i < 10; i++)
        {
            Assert.That(buffer[i], Is.EqualTo(i * 10));
        }
    }

    [Test]
    public void CopyTo_WithOffset_ShouldWork()
    {
        using var array = new SharedArray<int>(TestBufferName + "_CopyToOffset", 10);

        for (int i = 0; i < 10; i++)
        {
            array[i] = i * 10;
        }

        var buffer = new int[5];
        array.CopyTo(5, buffer);

        for (int i = 0; i < 5; i++)
        {
            Assert.That(buffer[i], Is.EqualTo((i + 5) * 10));
        }
    }

    [Test]
    public void Clear_ShouldZeroArray()
    {
        using var array = new SharedArray<int>(TestBufferName + "_Clear", 10);

        for (int i = 0; i < 10; i++)
        {
            array[i] = i + 100;
        }

        array.Clear();

        for (int i = 0; i < 10; i++)
        {
            Assert.That(array[i], Is.EqualTo(0));
        }
    }

    [Test]
    public void LargeArray_ShouldWork()
    {
        using var array = new SharedArray<long>(TestBufferName + "_Large", 10000);

        array[0] = long.MaxValue;
        array[5000] = long.MinValue;
        array[9999] = 0;

        Assert.That(array[0], Is.EqualTo(long.MaxValue));
        Assert.That(array[5000], Is.EqualTo(long.MinValue));
        Assert.That(array[9999], Is.EqualTo(0));
    }

    [Test]
    public void AllElements_ShouldBeAccessible()
    {
        using var array = new SharedArray<int>(TestBufferName + "_AllElements", 1000);

        // Write pattern
        for (int i = 0; i < 1000; i++)
        {
            array[i] = i * 3 + 7;
        }

        // Verify
        for (int i = 0; i < 1000; i++)
        {
            Assert.That(array[i], Is.EqualTo(i * 3 + 7));
        }
    }

    [Test]
    public void ByteArray_ShouldWork()
    {
        using var array = new SharedArray<byte>(TestBufferName + "_Byte", 256);

        for (int i = 0; i < 256; i++)
        {
            array[i] = (byte)i;
        }

        for (int i = 0; i < 256; i++)
        {
            Assert.That(array[i], Is.EqualTo((byte)i));
        }
    }

    [Test]
    public void Dispose_ShouldCleanupResources()
    {
        var array = new SharedArray<int>(TestBufferName + "_Dispose", 100);
        array.Dispose();

        // Should not throw on second dispose
        Assert.DoesNotThrow(() => array.Dispose());
    }

    private struct TestPoint
    {
        public int X;
        public int Y;
    }
}
