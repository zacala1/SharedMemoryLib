# Migrating from 2.x to InterprocessMemory 3.0

Version 3 is intentionally source-, binary-, package-, and shared-memory-format incompatible
with 2.x.

## Package and namespace

```shell
dotnet remove package SharedMemory.HighPerformance
dotnet add package InterprocessMemory --version 3.0.0
```

Replace:

```csharp
using SharedMemory;
```

with:

```csharp
using InterprocessMemory;
```

## Type names

| 2.x | 3.0 |
|---|---|
| `HighPerformanceSharedBuffer` | `MemoryRegion` |
| `ISharedMemoryBuffer` | `IMemoryRegion` |
| `SharedMemoryBufferOptions` | `MemoryRegionOptions` |
| `StrictSharedMemory<TSchema>` | `StructuredMemory<TSchema>` |
| `ISharedMemorySchema` | `IMemorySchema` |
| `SharedTypeCode` | `FieldTypeCode` |
| `LockFreeCircularBuffer` | `SingleProducerByteStream` |
| `MpmcCircularBuffer` | `ConcurrentMessageQueue` |
| `BufferEventHandler/Args/Type` | `MemoryRegionEventHandler/Args/Type` |

`SharedArray<T>`, `FieldDefinition`, `IVersionedSchema`, `SchemaCompatibility`, and
`LockOwnerInfo` keep their names.

## Creation and opening

Constructors with `bool create` were removed from the public API.

```csharp
// 2.x
using var owner = new HighPerformanceSharedBuffer(
    name,
    new SharedMemoryBufferOptions { Capacity = 4096 });

using var reader = new HighPerformanceSharedBuffer(
    name,
    new SharedMemoryBufferOptions
    {
        Capacity = 4096,
        CreateOrOpen = false
    });

// 3.0
using var owner = MemoryRegion.CreateOrOpen(name, 4096);
using var reader = MemoryRegion.OpenExisting(name);
```

Openers no longer repeat capacity, element count, or maximum message size. The values are read
from the shared header.

## Queue migration

The old SPSC circular buffer was a byte stream, not a message queue. Its direct replacement is:

```csharp
SingleProducerByteStream.CreateOrOpen(name, capacityBytes);
SingleProducerByteStream.OpenExisting(name);
```

The old MPMC circular buffer preserved variable-length message boundaries. Its replacement is:

```csharp
ConcurrentMessageQueue.CreateOrOpen(
    name,
    capacity: oldSlotCount,
    maxMessageSize: oldSlotSize - 16);
```

The internal 16-byte slot header is no longer part of the public size argument.

For fixed-size values, migrate to `SingleProducerQueue<T>` or `ConcurrentQueue<T>`, where
`T : unmanaged`. Capacity is the number of items rather than bytes.

## Recreating shared regions

Version 3 rejects version 2 headers and never overwrites them.

1. Stop all processes using the 2.x region.
2. Preserve data externally if it must survive the upgrade.
3. Remove the explicit backing file or stale Linux `/dev/shm` entry when applicable.
4. Start the version 3 creating process.
5. Start remaining processes with `OpenExisting`.

There is no in-place migration or mixed 2.x/3.x operation.
