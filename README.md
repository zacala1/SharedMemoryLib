# SharedMemory

High-performance shared memory library for .NET 8+.

## Why?

Windows named shared memory is fast, but the raw API is tedious. This library wraps it with SIMD-optimized copy, lock-free queues, and schema-based type safety—without allocations in the hot path.

## Features

- **SIMD Copy** — `Vector<T>` parallel processing (16-32 bytes/op)
- **Lock-free SPSC/MPMC** — Circular buffers with cache-line padding
- **Zero-allocation** — `Span<T>`, `stackalloc`, no GC pressure
- **Schema Versioning** — Type-safe fields with compatibility modes
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
    Capacity = 1024 * 1024,
    EnableSimd = true,
    EnableOrphanLockDetection = true
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

Types larger than 8 bytes (`Guid`, `DateTimeOffset`, `decimal`, large structs, arrays, strings) can't be written atomically on x64. `StrictSharedMemory` automatically acquires locks for these types to prevent torn reads/writes. The lock is reentrant, so calling from within an explicit lock won't deadlock.

## Supported Types

**Primitives:** `bool`, `byte`, `sbyte`, `char`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`

**Extended:** `Guid`, `DateTime`, `TimeSpan`, `DateTimeOffset`, custom `unmanaged` structs, enums

```csharp
// Custom struct
public struct Vector3 { public float X, Y, Z; }
yield return FieldDefinition.Struct<Vector3>("Position");
yield return FieldDefinition.StructArray<Vector3>("Waypoints", 10);

// Enum (stored as underlying type)
public enum Status : int { Active = 1, Paused = 2 }
yield return FieldDefinition.Scalar<Status>("Status");
```

## Testing

```bash
dotnet test
```

266 tests: unit, concurrency, stress, boundary conditions, schema compatibility, IPC, and extreme load scenarios.

## Project Structure

```text
SharedMemory/
├── HighPerformanceSharedBuffer.cs
├── LockFreeCircularBuffer.cs
├── MpmcCircularBuffer.cs
├── StrictSharedMemory.cs
├── SharedArray.cs
└── SharedMemoryBufferOptions.cs

SharedMemory.Tests/
└── *Tests.cs

SharedMemory.Benchmark/
└── *.cs
```

## License

MIT
