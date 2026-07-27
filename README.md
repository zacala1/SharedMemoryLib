# InterprocessMemory

`InterprocessMemory` lets separate .NET processes exchange raw bytes, typed values, messages,
arrays, and structured records through named shared memory.

- Windows: named `MemoryMappedFile`
- Linux: named files in `/dev/shm`
- .NET 8 or later
- Process-wide reader/writer synchronization with orphan-lock recovery
- Allocation-free `Span<T>` and `unmanaged` typed-queue hot paths

## Install

```shell
dotnet add package InterprocessMemory --version 3.0.0
```

```csharp
using InterprocessMemory;
```

## Raw memory shared by two processes

The process that knows the required size creates or opens the region:

```csharp
using var memory = MemoryRegion.CreateOrOpen(
    name: "telemetry",
    capacityBytes: 1024 * 1024);

memory.Write(new byte[] { 1, 2, 3, 4 }, offset: 0);
```

Other processes only need the name. `OpenExisting` reads the capacity from the version 3
header:

```csharp
using var memory = MemoryRegion.OpenExisting("telemetry");

Span<byte> value = stackalloc byte[4];
memory.Read(value, offset: 0);
```

`Read` and `Write` do not automatically lock the region. Use the shared reader/writer lock
when multiple bytes or fields must be observed atomically:

```csharp
if (memory.TryAcquireWriteLock(TimeSpan.FromSeconds(1)))
{
    try
    {
        memory.Write(payload, 0);
    }
    finally
    {
        memory.ReleaseWriteLock();
    }
}
```

Lock ownership includes the process ID, managed thread ID, and process start time. A process
waiting for a write lock can recover a lock left behind by a terminated process.

## Choosing a data structure

| Type | Use it for |
|---|---|
| `MemoryRegion` | Offset-addressed raw bytes |
| `StructuredMemory<TSchema>` | Named, typed fields with schema versioning |
| `SharedArray<T>` | A fixed-length shared array of `unmanaged` elements |
| `SingleProducerQueue<T>` | Fixed-size items with exactly one producer and one consumer |
| `ConcurrentQueue<T>` | Fixed-size items with multiple producers and consumers |
| `SingleProducerByteStream` | SPSC bytes where write boundaries are not messages |
| `ConcurrentMessageQueue` | Variable-length messages with preserved boundaries |

All types use `CreateOrOpen(...)` for the process that supplies sizing metadata and
`OpenExisting(...)` for processes that discover it from the shared header.

The `name` must be the same non-empty flat identifier in every process. Path separators,
control characters, NUL, and UTF-8 names longer than 255 bytes are rejected consistently on
Windows and Linux.

## Typed queues

Typed queues accept only fixed-size `unmanaged` values. They copy the value directly to a slot;
there is no serializer or managed allocation.

```csharp
public struct SensorSample
{
    public long Timestamp;
    public double Value;
}

using var producer =
    SingleProducerQueue<SensorSample>.CreateOrOpen(
        "sensor.samples",
        capacity: 1000);

var sample = new SensorSample
{
    Timestamp = DateTime.UtcNow.Ticks,
    Value = 23.5
};

producer.TryEnqueue(sample);
```

```csharp
using var consumer =
    SingleProducerQueue<SensorSample>.OpenExisting("sensor.samples");

if (consumer.TryDequeue(out SensorSample sample))
{
    Console.WriteLine(sample.Value);
}
```

Queue capacity is an item count, not bytes, and is rounded up to the next power of two.
`Capacity` reports the resulting number of slots.

The shared header records `sizeof(T)` and a deterministic fingerprint of the type name,
assembly name, struct layout, field types, sizes, and offsets. Opening the queue with a
different or modified `T` throws `InvalidDataException`. Producer and consumer applications
should reference the same DTO assembly.

Use `ConcurrentQueue<T>` when more than one producer or consumer may access the queue:

```csharp
using var queue =
    InterprocessMemory.ConcurrentQueue<SensorSample>.CreateOrOpen(
        "sensor.concurrent",
        capacity: 4096,
        options: new ConcurrentQueueOptions
        {
            MaxSpins = 100,
            EnableStatistics = false
        });
```

Both typed queues provide immediate and timeout/cancellation overloads of `TryEnqueue` and
`TryDequeue`.

## Byte stream and variable-length messages

`SingleProducerByteStream` is a continuous SPSC ring. A six-byte write may be returned as a
two-byte read followed by a four-byte read:

```csharp
using var stream =
    SingleProducerByteStream.CreateOrOpen("camera.bytes", 64 * 1024);

stream.TryWrite(bytes);
```

Use `ConcurrentMessageQueue` when each write must remain one message:

```csharp
using var messages = ConcurrentMessageQueue.CreateOrOpen(
    name: "commands",
    capacity: 1024,
    maxMessageSize: 4096);

messages.TryEnqueue(encodedCommand);

Span<byte> destination = stackalloc byte[messages.MaxMessageSize];
if (messages.TryDequeue(destination, out int length))
{
    ReadOnlySpan<byte> message = destination[..length];
}
```

Empty messages are rejected. If the destination is too small, dequeue throws without consuming
the message. Serialization is intentionally outside the core library; encode managed objects
before enqueueing them.

## Structured memory

```csharp
public struct SensorSchema : IMemorySchema
{
    public IEnumerable<FieldDefinition> GetFields()
    {
        yield return FieldDefinition.Scalar<int>("Sequence");
        yield return FieldDefinition.Scalar<double>("Temperature");
        yield return FieldDefinition.String("Status", 32);
    }
}

using var memory =
    StructuredMemory<SensorSchema>.CreateOrOpen(
        "sensor.state",
        new SensorSchema());

using (memory.AcquireWriteLock())
{
    memory.Write("Sequence", 42);
    memory.Write("Temperature", 23.5);
    memory.WriteString("Status", "Ready");
}
```

`StructuredMemory<TSchema>` automatically locks values wider than eight bytes, strings, blobs,
UTF-8 strings, and arrays to prevent torn reads and writes. Use an explicit lock when several
fields form one transaction.

## Shared arrays

```csharp
using var owner = SharedArray<long>.CreateOrOpen("samples", 10_000);
owner[0] = 42;

using var reader = SharedArray<long>.OpenExisting("samples");
Console.WriteLine(reader.Length);
Console.WriteLine(reader[0]);
```

The array header validates the element type fingerprint and restores its length for openers.

## Version 3 format

Every region has a version 3 magic value, format version, and data-structure kind. Opening a
queue as raw memory, using a different element type, or opening a 2.x region is rejected without
modifying the existing bytes.

Version 3 does not migrate live 2.x regions. Stop every 2.x process, remove the named/file-backed
region, and recreate it with version 3. See [MIGRATION.md](MIGRATION.md).

## Build and test

```shell
dotnet restore InterprocessMemory.sln
dotnet test InterprocessMemory.sln
dotnet build InterprocessMemory.sln --configuration Release
```

The test suite includes real child-process transfer, multi-process typed MPMC delivery,
cross-process lock exclusion, and orphan-lock recovery.
