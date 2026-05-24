# SharedMemory

High-performance shared memory library for .NET 8+. **Cross-platform: Windows + Linux.**

## Why?

OS-level named shared memory is fast, but the raw API is tedious and platform-specific. This library wraps it with SIMD-optimized copy, lock-free queues, and schema-based type safety — without allocations in the hot path, and with the same code on Windows and Linux.

## Features

- **Cross-platform** — Windows (named `MemoryMappedFile`) and Linux (`/dev/shm` tmpfs). Same hot path, same raw-pointer access on both — no per-call OS dispatch
- **SIMD Copy** — `Vector<T>` parallel processing (16-32 bytes/op)
- **Lock-free SPSC/MPMC** — Circular buffers with cache-line padding and false-sharing prevention
- **Zero-allocation** — `Span<T>`, `MemoryMarshal`, no GC pressure in hot path
- **Schema Versioning** — Type-safe fields with compatibility modes (incl. schema-defined `IsCompatibleWith` veto); empty schema rejected at construction
- **Blob & UTF-8** — Binary data and UTF-8 strings with length prefix
- **Orphan Lock Recovery** — Detects dead lock holders via PID **and** process start time (defeats PID reuse), with double-check at 75% of timeout
- **CRC32 Checksum** — Hardware-accelerated integrity verification
- **Cancellable Waits** — `WaitWrite`/`WaitRead` accept `CancellationToken` on both SPSC and MPMC
- **Opt-in Statistics** — `EnableStatistics = false` removes hot-path `Interlocked` overhead for read-heavy workloads (20-40% throughput recovery under heavy reader contention)

## Requirements

- .NET 8.0+
- Windows or Linux. macOS is not supported in named-anonymous mode (use `FilePath` for file-backed access)

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
    EnableStatistics = true,       // Per-call Interlocked counters (default true; see Performance below)
    Alignment = 64,                // Cache-line alignment (default: 64)
    FilePath = null                // null = anonymous (Windows MMF / Linux /dev/shm); or path for persistent file-backed MMF
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

// Blocking (optional CancellationToken supported)
buffer.WaitWrite(data, TimeSpan.FromMilliseconds(100));
buffer.WaitRead(data, TimeSpan.FromMilliseconds(100));

var cts = new CancellationTokenSource();
buffer.WaitWrite(data, TimeSpan.FromSeconds(5), cts.Token);
buffer.WaitRead(data, TimeSpan.FromSeconds(5), cts.Token);
```

### MpmcCircularBuffer

```csharp
// maxSpins: how many times TryWrite/TryRead spins before giving up (default: 100)
//   - increase for high-contention workloads
//   - decrease (e.g. 10) for latency-sensitive scenarios that prefer fast failure
// enableStatistics: track TotalWrites/Reads/Failed*/SpinExhausted* counters in the shared header
//   - default true; set false in high-concurrency workloads where stats are tracked externally
//     to eliminate the cross-process Interlocked contention on the counter cache lines
using var buffer = new MpmcCircularBuffer(
    "MpmcQueue", slotCount: 16, slotSize: 256, maxSpins: 100, enableStatistics: true);

Parallel.For(0, 10, i => buffer.TryWrite(BitConverter.GetBytes(i)));

// Blocking with optional cancellation
var cts = new CancellationTokenSource();
buffer.WaitWrite(BitConverter.GetBytes(42), TimeSpan.FromSeconds(5), cts.Token);

byte[] dst = new byte[256];
buffer.WaitRead(dst, TimeSpan.FromSeconds(5), cts.Token);

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
2. **PID-reuse defense** — Captures the owner's `Process.StartTime` at acquire and compares on the orphan check. Even when the original PID has been recycled by the OS for an unrelated process, the start-time mismatch identifies it as an impostor. Falls back gracefully to PID-only check when StartTime is unreadable (restricted Linux containers, etc.)
3. **Timeout fallback** — If a lock is held longer than `OrphanLockTimeout` (default: 30s), it's considered orphaned
4. **Double check** — Orphan detection runs on the first CAS failure *and* again when 75% of the wait timeout has elapsed, recovering locks that become orphaned mid-wait
5. **Safe CAS release** — Uses compare-and-swap on the owner PID to avoid releasing a valid lock acquired by a new process between the check and release

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

> These figures were measured on the original implementation (i7-12700K, DDR5-4800, .NET 8). Subsequent hot-path changes (Interlocked removal on SPSC stats, `WriterLockState`/`ReaderCount` cache-line separation, `MemoryMarshal` instead of stackalloc, opt-in statistics, optimistic reader lock) likely improved these numbers further. Re-run `SharedMemory.Benchmark` to get current figures.

### Statistics opt-in

`EnableStatistics = false` removes the per-call `Interlocked.Increment` / `Interlocked.Add` on `_totalReads` / `_totalWrites` / `_totalBytes*`. Each was ~10ns uncontended and 20-40ns under heavy reader contention — meaningful when `Read()` itself is ~100ns for small payloads. Long-running stress measurements:

| Workload | Stats on | Stats off |
|---|---|---|
| 16 readers + 4 writers, 2s | data integrity holds | data integrity holds, `GetStatistics()` returns zero (Interlocked path skipped) |
| SPSC 2-min sustained | (counters tracked) | ~9.77M msg/s, 1.17B messages, 0 ordering violations |

For the MPMC buffer, counters live in the shared-memory header (cross-process visible). Same per-call cost characteristics apply — disable via the `enableStatistics:` constructor parameter when stats are tracked externally.

## Thread Safety

On x86-64, MOV is atomic for aligned values up to 8 bytes. Types wider than this threshold (`Guid`, `DateTimeOffset`, `decimal`, large structs, arrays, strings) require locking to prevent torn reads/writes. `StrictSharedMemory` applies this automatically.

Locks are **reentrant**: nested `AcquireWriteLock()` / `AcquireReadLock()` calls from the same thread increment a depth counter instead of deadlocking. A read lock acquired inside a write lock is also safe. All string, blob, UTF-8, and array operations follow the same pattern — no deadlock even when called under an existing lock.

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

450 tests pass on Windows across all standard categories. 4 long-running stability tests are marked `[Explicit]` and must be opted into manually:

```bash
# Run only the explicit long-running tests (~8 minutes total)
dotnet test --filter "FullyQualifiedName~MPMC_16Producers|FullyQualifiedName~Stability_MPMC|FullyQualifiedName~Stability_SPSC|FullyQualifiedName~Stability_Strict"
```

Test categories:

| Category | Count | Description |
|----------|-------|-------------|
| Unit / Concurrency | ~400 | Single-class functional, multi-thread, stress, boundary |
| **Verification** | ~40 | Targeted per-change functional validation (bug fixes, optimizations, cross-platform) |
| **Concurrency** | 6 | Recent-fix stress: opt-in stats, optimistic reader lock, orphan check under load, fairness |
| **CrossProcess** | 6 | Spawns `SharedMemory.IpcHelper.exe` — real two-process IPC |
| Extreme | 4 | Long-running stability (explicit only) — MPMC, SPSC, Strict, 1M-message MPMC stress |

> **Linux:** the test project currently targets `net8.0-windows` (legacy MMF-CrossProcess test harness). The library itself targets `net8.0` and is structurally correct for Linux runtime — add a CI job on `ubuntu-latest` and retarget the test project to validate on Linux.

### Coverage by Class

> Baseline figures captured before the Linux backend, opt-in stats, optimistic reader lock, and PID-reuse defense were added. Net coverage likely shifted modestly — re-run `dotnet test --collect:"XPlat Code Coverage"` for current numbers.

| Class | Line | Branch |
|-------|------|--------|
| `BufferEventArgs` | 100% | 100% |
| `LockOwnerInfo` | 100% | 100% |
| `SharedArray<T>` | 100% | 100% |
| `LockFreeCircularBuffer` | 99.3% | 95%+ |
| `StrictSharedMemory<T>` | 97.8% | 90%+ |
| `FieldDefinition` | 96.5% | 85%+ |
| `SharedMemoryBufferOptions` | 96.0% | 80%+ |
| `MpmcCircularBuffer` | 95.4% | 85%+ |
| `HighPerformanceSharedBuffer` | 88.3% | 80%+ |

> `HighPerformanceSharedBuffer` remaining uncovered lines are OS-level failure paths (MMF allocation failure, cleanup exceptions, the Linux `/dev/shm` branch on Windows CI) that require process death simulation or a Linux runtime.

## Project Structure

```text
SharedMemory/                          # Core library (cross-platform: Windows + Linux)
├── ISharedMemoryBuffer.cs             # Core interface + event types
├── SharedMemoryBufferOptions.cs       # Configuration options (incl. EnableStatistics)
├── HighPerformanceSharedBuffer.cs     # Raw byte buffer with SIMD, orphan lock detection,
│                                      #   and OS-specific MMF creation (Windows named /
│                                      #   Linux /dev/shm) split into 3 helper methods
├── UnmanagedMemoryManager.cs          # MemoryManager<T> wrapper for raw shared pointer
├── LockFreeCircularBuffer.cs          # SPSC queue (OPT-6: plain load for owned position)
├── MpmcCircularBuffer.cs              # MPMC Vyukov queue (opt-in stats, peek-before-CAS)
├── SchemaTypes.cs                     # SharedTypeCode, SchemaCompatibility, ISharedMemorySchema, IVersionedSchema
├── FieldDefinition.cs                 # FieldDefinition struct + TypeCodeCache<T> static generic
├── StrictSharedMemory.cs              # Schema-based typed memory access (reentrant locks,
│                                      #   read-lock-held-while-writing now throws explicitly)
└── SharedArray.cs                     # Generic shared T[] with indexer (Fill uses ArrayPool)

SharedMemory.Tests/                    # 450 tests (NUnit)
├── HighPerformanceSharedBufferTests.cs
├── LockFreeCircularBufferTests.cs
├── MpmcCircularBufferTests.cs
├── StrictSharedMemoryTests.cs
├── SharedArrayTests.cs
├── AdvancedTests.cs                   # Concurrency & edge cases
├── ExtremeStressTests.cs              # Extreme load (2 explicit long-running)
├── ConcurrencyStabilityTests.cs       # Recent-fix stress + 2 explicit long-running (SPSC + Strict)
├── CoverageBoostTests.cs              # Validation, reentrant locks, rare paths
├── ChangeVerificationTests.cs         # ~40 targeted per-change functional tests
└── CrossProcessTests.cs               # Real IPC tests (spawns SharedMemory.IpcHelper.exe)

SharedMemory.IpcHelper/                # Child process for cross-process IPC tests
└── Program.cs

SharedMemory.Benchmark/                # BenchmarkDotNet
└── *.cs
```

## License

MIT
