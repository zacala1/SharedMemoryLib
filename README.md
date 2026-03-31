# SharedMemory

High-performance shared memory library for .NET 8+.

## Why?

Windows named shared memory is fast, but the raw API is tedious. This library wraps it with SIMD-optimized copy, lock-free queues, and schema-based type safety—without allocations in the hot path.

## Features

- **SIMD Copy** — `Vector<T>` parallel processing (16-32 bytes/op)
- **Lock-free SPSC/MPMC** — Circular buffers with cache-line padding
- **Zero-allocation** — `Span<T>`, `stackalloc`, no GC pressure
- **Schema Versioning** — Type-safe fields with compatibility modes
- **Blob & UTF-8** — Binary data and UTF-8 strings with length prefix
- **Orphan Lock Recovery** — Handles process crashes gracefully
- **CRC32 Checksum** — Hardware-accelerated integrity verification

## Requirements

- .NET 8.0+
- Windows only (uses `MemoryMappedFile`)

## Installation

```bash
dotnet add package SharedMemory.HighPerformance
```

## Quick Start

```csharp
// Simple buffer
using var buffer = new HighPerformanceSharedBuffer("MyBuffer", new() { Capacity = 1024 * 1024 });
buffer.Write(data, offset: 0);
buffer.Read(result, offset: 0);

// Message queue (SPSC)
using var queue = new LockFreeCircularBuffer("Queue", 64 * 1024);
queue.TryWrite(message);
queue.TryRead(buffer);

// Type-safe schema
using var mem = new StrictSharedMemory<SensorSchema>("Sensor", schema);
mem.Write(SensorSchema.Temperature, 25.6);
```

## API Reference

| Class | Use Case |
|-------|----------|
| `HighPerformanceSharedBuffer` | Raw byte buffer with SIMD |
| `LockFreeCircularBuffer` | Single-producer/single-consumer queue |
| `MpmcCircularBuffer` | Multi-producer/multi-consumer queue |
| `StrictSharedMemory<T>` | Schema-based typed fields |
| `SharedArray<T>` | Shared `T[]` with indexer |

## Usage

### HighPerformanceSharedBuffer

```csharp
var options = new SharedMemoryBufferOptions
{
    Capacity = 1024 * 1024,       // Buffer size in bytes
    EnableSimd = true,             // SIMD-optimized copy (default: true)
    EnableOrphanLockDetection = true, // Detect dead lock holders (default: true)
    OrphanLockTimeout = TimeSpan.FromSeconds(30), // Timeout-based fallback
    EnableEvents = false,          // OnDataWritten / OnOrphanLockDetected events
    EnableChecksumVerification = false, // CRC32 integrity checks
    Alignment = 64,                // Cache-line alignment (default: 64)
    FilePath = null                // null = anonymous, or path for persistent file-backed MMF
};

using var buffer = new HighPerformanceSharedBuffer("MyBuffer", options);

byte[] data = [1, 2, 3, 4, 5];
buffer.Write(data, offset: 0);

byte[] result = new byte[5];
buffer.Read(result, offset: 0);

// Manual locking
if (buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)))
{
    try { buffer.Write(data, 0); }
    finally { buffer.ReleaseWriteLock(); }
}

// Checksum
buffer.UpdateChecksum(0, 100);
bool valid = buffer.VerifyIntegrity();
```

### StrictSharedMemory (Schema-based)

```csharp
public struct SensorSchema : IVersionedSchema
{
    public const string Temperature = "Temperature";
    public const string DeviceName = "DeviceName";

    public int Version => 1;
    public bool IsCompatibleWith(int v) => v == 1;

    public IEnumerable<FieldDefinition> GetFields()
    {
        yield return FieldDefinition.Scalar<double>(Temperature);
        yield return FieldDefinition.String(DeviceName, 32);
    }
}

using var memory = new StrictSharedMemory<SensorSchema>("Sensor", new SensorSchema());

memory.Write(SensorSchema.Temperature, 25.6);
memory.WriteString(SensorSchema.DeviceName, "Sensor-001");

double temp = memory.Read<double>(SensorSchema.Temperature);

// RAII locks
using (memory.AcquireWriteLock())
{
    memory.Write(SensorSchema.Temperature, 26.1);
}
```

### LockFreeCircularBuffer (SPSC)

Single-producer/single-consumer only. For multiple producers or consumers, use `MpmcCircularBuffer`.

```csharp
using var buffer = new LockFreeCircularBuffer("Queue", 64 * 1024);

// Producer
buffer.TryWrite(BitConverter.GetBytes(12345));

// Consumer
byte[] data = new byte[4];
if (buffer.TryRead(data) > 0)
    Console.WriteLine(BitConverter.ToInt32(data));

// Blocking
buffer.WaitWrite(data, TimeSpan.FromMilliseconds(100));
buffer.WaitRead(data, TimeSpan.FromMilliseconds(100));
```

### MpmcCircularBuffer

```csharp
using var buffer = new MpmcCircularBuffer("MpmcQueue", slotCount: 16, slotSize: 256);

Parallel.For(0, 10, i => buffer.TryWrite(BitConverter.GetBytes(i)));

var stats = buffer.GetStatistics();
Console.WriteLine($"Writes: {stats.TotalWrites}, Failed: {stats.FailedWrites}");
```

### SharedArray

```csharp
using var arr = new SharedArray<int>("IntArray", 1000);

arr[0] = 100;
arr[999] = 200;

arr.CopyFrom(0, new int[] { 1, 2, 3, 4, 5 });
arr.Fill(42);
arr.Clear();
```

### Orphan Lock Recovery

When a process holding a write lock crashes, other processes would be blocked forever. The library detects this automatically:

1. **Process death detection** — Checks if the lock owner PID is still alive via `Process.GetProcessById()`
2. **Timeout fallback** — If a lock is held longer than `OrphanLockTimeout` (default: 30s), it's considered orphaned
3. **Safe CAS release** — Uses compare-and-swap on the owner PID to avoid releasing a valid lock that was acquired by a new process between the check and release

```csharp
// Automatic: TryAcquireWriteLock checks for orphans on first CAS failure
buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(5)); // auto-recovers if orphaned

// Manual check
if (buffer.IsWriteLockOrphaned())
    buffer.TryForceReleaseWriteLock();

// Event notification
buffer.OnOrphanLockDetected += (s, e) => Console.WriteLine("Orphan lock recovered");
```

## IPC Example

**Process A (Producer):**

```csharp
using var mem = new StrictSharedMemory<SensorSchema>("Sensor", schema);

while (true)
{
    using (mem.AcquireWriteLock())
        mem.Write(SensorSchema.Temperature, ReadSensor());
    Thread.Sleep(100);
}
```

**Process B (Consumer):**

```csharp
using var mem = new StrictSharedMemory<SensorSchema>("Sensor", schema, create: false);

while (true)
{
    using (mem.AcquireReadLock())
        Console.WriteLine(mem.Read<double>(SensorSchema.Temperature));
    Thread.Sleep(100);
}
```

## Performance

Measured on i7-12700K, DDR5-4800, .NET 8.

### Throughput

| Size | Write | Read | Throughput |
|------|-------|------|------------|
| 64B | 8.1 ns | 8.3 ns | 7.9 GB/s |
| 256B | 9.3 ns | 10.0 ns | 27.5 GB/s |
| 1KB | 13.7 ns | 15.1 ns | 74.8 GB/s |
| 64KB | 996 ns | 1,017 ns | 65.8 GB/s |
| 1MB | ~19.8 µs | ~21.4 µs | 51 GB/s |

### Queue Latency

| Type | Write Latency | Notes |
|------|---------------|-------|
| SPSC | 0.95 ns | Use when possible |
| MPMC | 4.49 ns | ~4.7x slower due to CAS |

### Lock Overhead

| Operation | Without Lock | With Lock |
|-----------|--------------|-----------|
| Write | 13.3 ns | 60.7 ns |
| Read | 13.3 ns | 46.5 ns |

## Thread Safety

Types larger than 8 bytes (`Guid`, `DateTimeOffset`, `decimal`, large structs, arrays, strings) can't be written atomically on x64. `StrictSharedMemory` automatically acquires locks for these types to prevent torn reads/writes.

Locks are **reentrant**: nested `AcquireWriteLock()` / `AcquireReadLock()` calls from the same thread increment a depth counter instead of deadlocking. A read lock acquired inside a write lock is also safe.

```csharp
using (mem.AcquireWriteLock())
{
    // Inner lock is reentrant — no deadlock
    using (mem.AcquireWriteLock())
    {
        mem.Write("Field", value);
    }
}
```

## Schema Compatibility

When opening existing shared memory with a different schema version, use `SchemaCompatibility` to control behavior:

| Mode | Behavior |
|------|----------|
| `Strict` | Exact version match required (default) |
| `Forward` | Allow opening memory written by a **newer** version |
| `Backward` | Allow opening memory written by an **older** version |
| `Full` | Allow any version difference |

```csharp
// Consumer opens v1 memory with v2 schema in backward-compatible mode
using var mem = new StrictSharedMemory<SchemaV2>(
    "Sensor", schemaV2, create: false, SchemaCompatibility.Backward);
```

## Supported Types

**Primitives:** `bool`, `byte`, `sbyte`, `char`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`

**Extended:** `Guid`, `DateTime`, `TimeSpan`, `DateTimeOffset`, custom `unmanaged` structs, enums

**Strings:** UTF-16 (`FieldDefinition.String`) and UTF-8 (`FieldDefinition.Utf8String`)

**Binary:** Fixed-size blob (`FieldDefinition.Blob`) with length prefix

```csharp
// Custom struct
public struct Vector3 { public float X, Y, Z; }
yield return FieldDefinition.Struct<Vector3>("Position");
yield return FieldDefinition.StructArray<Vector3>("Waypoints", 10);

// Enum (stored as underlying type)
public enum Status : int { Active = 1, Paused = 2 }
yield return FieldDefinition.Scalar<Status>("Status");

// UTF-8 string (more compact for ASCII/Latin text)
yield return FieldDefinition.Utf8String("DeviceName", maxByteLength: 128);

// Binary blob (images, serialized data, etc.)
yield return FieldDefinition.Blob("Thumbnail", maxSize: 4096);
```

### Blob and UTF-8 String Fields

Blob and UTF-8 fields use a 4-byte length prefix so actual data size is tracked:

```csharp
// Schema definition
public struct DataSchema : ISharedMemorySchema
{
    public IEnumerable<FieldDefinition> GetFields()
    {
        yield return FieldDefinition.Blob("Payload", maxSize: 1024);      // up to 1KB binary
        yield return FieldDefinition.Utf8String("Message", maxByteLength: 256); // up to 256 bytes UTF-8
    }
}

// Usage
using var mem = new StrictSharedMemory<DataSchema>("Data", new DataSchema());

// Blob: write/read raw bytes
mem.WriteBlob("Payload", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
byte[] payload = mem.ReadBlob("Payload"); // returns exactly [0xDE, 0xAD, 0xBE, 0xEF]

// UTF-8: efficient for ASCII, full Unicode support
mem.WriteUtf8String("Message", "Hello 한국어 🚀");
string msg = mem.ReadUtf8String("Message");
```

## Testing

```bash
dotnet test
```

360 tests: unit, concurrency, stress, boundary conditions, schema compatibility, reentrant locks, blob/UTF-8 fields, IPC, and extreme load scenarios.

## Project Structure

```text
SharedMemory/                          # Core library
├── ISharedMemoryBuffer.cs             # Core interface + event types
├── SharedMemoryBufferOptions.cs       # Configuration options
├── HighPerformanceSharedBuffer.cs     # Raw byte buffer with SIMD & orphan lock detection
├── LockFreeCircularBuffer.cs          # SPSC queue
├── MpmcCircularBuffer.cs             # MPMC queue
├── StrictSharedMemory.cs             # Schema-based typed memory access
└── SharedArray.cs                     # Generic shared T[] with indexer

SharedMemory.Tests/                    # 339 tests (NUnit)
├── HighPerformanceSharedBufferTests.cs
├── LockFreeCircularBufferTests.cs
├── MpmcCircularBufferTests.cs
├── StrictSharedMemoryTests.cs
├── SharedArrayTests.cs
├── AdvancedTests.cs                   # Concurrency & edge cases
├── ExtremeStressTests.cs              # Extreme load scenarios
└── CoverageBoostTests.cs             # Validation, reentrant locks, rare paths

SharedMemory.Benchmark/                # BenchmarkDotNet
└── *.cs
```

## License

MIT
