using System;
using System.Collections.Generic;
using SharedMemory;

/// <summary>
/// Lightweight child-process helper for cross-process integration tests.
/// Usage: SharedMemory.IpcHelper &lt;role&gt; &lt;bufferName&gt;
///
/// Roles:
///   hpbuf_writer   &lt;name&gt;  — open existing buffer, write known byte pattern
///   hpbuf_reader   &lt;name&gt;  — open existing buffer, verify known byte pattern
///   spsc_producer  &lt;name&gt;  — open existing SPSC queue, write 100 int messages
///   spsc_consumer  &lt;name&gt;  — open existing SPSC queue, read and verify 100 int messages
///   strict_writer  &lt;name&gt;  — open existing StrictSharedMemory, write Counter=42, Label="hello"
///   strict_reader  &lt;name&gt;  — open existing StrictSharedMemory, verify Counter=42, Label="hello"
/// </summary>
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: SharedMemory.IpcHelper <role> <bufferName>");
    return 1;
}

string role = args[0];
string bufferName = args[1];

return role switch
{
    "hpbuf_writer"  => HpBufWriter(bufferName),
    "hpbuf_reader"  => HpBufReader(bufferName),
    "spsc_producer" => SpscProducer(bufferName),
    "spsc_consumer" => SpscConsumer(bufferName),
    "strict_writer" => StrictWriter(bufferName),
    "strict_reader" => StrictReader(bufferName),
    _               => Error($"Unknown role: {role}")
};

// ── helpers ─────────────────────────────────────────────────────────────────

static int HpBufWriter(string name)
{
    var opts = new SharedMemoryBufferOptions { Capacity = 256, CreateOrOpen = false };
    using var buf = new HighPerformanceSharedBuffer(name, opts);
    var data = new byte[64];
    for (int i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);
    buf.Write(data, 0);
    Console.WriteLine("written");
    return 0;
}

static int HpBufReader(string name)
{
    var opts = new SharedMemoryBufferOptions { Capacity = 256, CreateOrOpen = false };
    using var buf = new HighPerformanceSharedBuffer(name, opts);
    var result = new byte[64];
    buf.Read(result, 0);
    for (int i = 0; i < result.Length; i++)
    {
        if (result[i] != (byte)(i + 1))
        {
            Console.Error.WriteLine($"Mismatch at index {i}: expected {i + 1}, got {result[i]}");
            return 3;
        }
    }
    Console.WriteLine("verified");
    return 0;
}

static int SpscProducer(string name)
{
    using var buf = new LockFreeCircularBuffer(name, 4096, create: false);
    for (int i = 0; i < 100; i++)
    {
        if (!buf.WaitWrite(BitConverter.GetBytes(i), TimeSpan.FromSeconds(10)))
        {
            Console.Error.WriteLine($"WaitWrite timed out at message {i}");
            return 4;
        }
    }
    Console.WriteLine("produced:100");
    return 0;
}

static int SpscConsumer(string name)
{
    using var buf = new LockFreeCircularBuffer(name, 4096, create: false);
    var dst = new byte[4];
    for (int i = 0; i < 100; i++)
    {
        int read = buf.WaitRead(dst, TimeSpan.FromSeconds(10));
        if (read != 4) { Console.Error.WriteLine($"Short read at {i}: {read}"); return 5; }
        int val = BitConverter.ToInt32(dst);
        if (val != i) { Console.Error.WriteLine($"Value mismatch at {i}: got {val}"); return 6; }
    }
    Console.WriteLine("consumed:100");
    return 0;
}

static int StrictWriter(string name)
{
    var schema = new IpcSchema();
    using var mem = new StrictSharedMemory<IpcSchema>(name, schema, create: false);
    using (mem.AcquireWriteLock())
    {
        mem.Write(IpcSchema.Counter, 42);
        mem.WriteString(IpcSchema.Label, "hello");
    }
    Console.WriteLine("strict_written");
    return 0;
}

static int StrictReader(string name)
{
    var schema = new IpcSchema();
    using var mem = new StrictSharedMemory<IpcSchema>(name, schema, create: false);
    int counter;
    string label;
    using (mem.AcquireReadLock())
    {
        counter = mem.Read<int>(IpcSchema.Counter);
        label = mem.ReadString(IpcSchema.Label);
    }
    if (counter != 42) { Console.Error.WriteLine($"Counter mismatch: {counter}"); return 7; }
    if (label != "hello") { Console.Error.WriteLine($"Label mismatch: {label}"); return 8; }
    Console.WriteLine("strict_verified");
    return 0;
}

static int Error(string msg)
{
    Console.Error.WriteLine(msg);
    return 1;
}

// ── schema ───────────────────────────────────────────────────────────────────

public struct IpcSchema : ISharedMemorySchema
{
    public const string Counter = "Counter";
    public const string Label   = "Label";

    public IEnumerable<FieldDefinition> GetFields()
    {
        yield return FieldDefinition.Scalar<int>(Counter);
        yield return FieldDefinition.String(Label, 32);
    }
}
