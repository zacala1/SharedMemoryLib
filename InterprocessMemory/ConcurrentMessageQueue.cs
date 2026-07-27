using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace InterprocessMemory
{
    /// <summary>
    /// Multi-producer/multi-consumer lock-free message queue.
    /// Thread-safe for concurrent access from multiple writers and readers.
    /// Uses sequence numbers for coordination instead of simple head/tail pointers.
    /// Cross-platform: backed by <see cref="MemoryRegion"/> which supports Windows and Linux.
    /// </summary>
    public sealed unsafe class ConcurrentMessageQueue : IDisposable
    {
        [StructLayout(LayoutKind.Explicit, Size = 384)]
        private struct Header
        {
            // Writer coordination (cache line 1: offset 0-63)
            [FieldOffset(0)] public long WriteSequence;

            // Reader coordination (cache line 2: offset 64-127)
            [FieldOffset(64)] public long ReadSequence;

            // Metadata (cache line 3: offset 128-191)
            [FieldOffset(128)] public int SlotCount;
            [FieldOffset(132)] public int SlotStride;
            [FieldOffset(136)] public int MaxMessageSize;
            [FieldOffset(140)] public int Version;
            [FieldOffset(144)] public long Magic;

            // Writer statistics (cache line 4: offset 192-255) - separate from reader stats
            [FieldOffset(192)] public long TotalWrites;
            [FieldOffset(200)] public long FailedWrites;

            // Reader statistics (cache line 5: offset 256-319) - separate from writer stats
            [FieldOffset(256)] public long TotalReads;
            [FieldOffset(264)] public long FailedReads;

            // Extended counters (cache line 6: offset 320-383) — added for unambiguous diagnostics.
            // SpinExhausted* counts the maxSpins-exceeded case (transient contention, retryable),
            // distinct from FailedWrites/FailedReads which now count only "buffer full / empty".
            // Older readers that don't know these fields simply see 0 — backward compatible.
            [FieldOffset(320)] public long SpinExhaustedWrites;
            [FieldOffset(328)] public long SpinExhaustedReads;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct Slot
        {
            public long Sequence;
            public int DataLength;
            public int Reserved;
        }

        private const int HeaderSize = 384; // 6 cache lines for false-sharing prevention
        private const int SlotHeaderSize = 16;
        private const int FormatVersion = 3;
        private const long HeaderMagic = 0x514D434D504953;

        private readonly IMemoryRegion _buffer;
        private int _slotCount;
        private int _maxMessageSize;
        private int _slotTotalSize;
        private long _slotMask;
        private readonly byte* _dataPtr;
        private readonly Header* _header;
        private readonly MemoryHandle _memoryHandle;

        private volatile int _disposed;
        private readonly int _maxSpins;
        // When false, the per-call Interlocked.Increment on TotalWrites/TotalReads/SpinExhausted*
        // counters in shared-memory header is skipped. Each of those is a cross-process
        // contention point — disabling can recover meaningful throughput in MPMC scenarios with
        // many concurrent producers/consumers where stats are observed externally.
        private readonly bool _statsEnabled;

        /// <summary>
        /// Gets the number of slots in the buffer
        /// </summary>
        internal int SlotCount => _slotCount;

        /// <summary>Gets the actual number of message slots.</summary>
        public int Capacity => _slotCount;

        /// <summary>
        /// Gets the maximum message size per slot
        /// </summary>
        public int MaxMessageSize => _maxMessageSize;

        /// <summary>
        /// Gets statistics about buffer operations.
        /// <para>
        /// <c>FailedWrites</c> counts genuine "buffer full" outcomes (TryWrite returned false because
        /// all slots are occupied). <c>FailedReads</c> counts genuine "buffer empty" outcomes.
        /// Transient maxSpins-exhausted failures are tracked separately via
        /// <see cref="GetExtendedStatistics"/> so that the basic counters reflect capacity pressure
        /// rather than contention pressure.
        /// </para>
        /// </summary>
        public (long TotalWrites, long TotalReads, long FailedWrites, long FailedReads) GetStatistics()
        {
            ThrowIfDisposed();
            return (
                Volatile.Read(ref _header->TotalWrites),
                Volatile.Read(ref _header->TotalReads),
                Volatile.Read(ref _header->FailedWrites),
                Volatile.Read(ref _header->FailedReads)
            );
        }

        /// <summary>
        /// Gets extended statistics that distinguish capacity failures from contention failures.
        /// <para>
        /// <c>SpinExhaustedWrites</c>/<c>SpinExhaustedReads</c> count operations that gave up after
        /// reaching <c>maxSpins</c> while the slot was still being prepared by another thread —
        /// these are typically retryable. High values suggest tuning maxSpins or reducing contention.
        /// </para>
        /// </summary>
        public (long SpinExhaustedWrites, long SpinExhaustedReads) GetExtendedStatistics()
        {
            ThrowIfDisposed();
            return (
                Volatile.Read(ref _header->SpinExhaustedWrites),
                Volatile.Read(ref _header->SpinExhaustedReads)
            );
        }

        /// <summary>
        /// Creates or opens a variable-length multi-producer/multi-consumer message queue.
        /// </summary>
        public static ConcurrentMessageQueue CreateOrOpen(
            string name,
            int capacity,
            int maxMessageSize,
            ConcurrentQueueOptions? options = null) =>
            new(name, capacity, maxMessageSize, options, createOrOpen: true);

        /// <summary>Opens an existing message queue and loads its sizing metadata.</summary>
        public static ConcurrentMessageQueue OpenExisting(
            string name,
            ConcurrentQueueOptions? options = null) =>
            new(name, capacity: null, maxMessageSize: null, options, createOrOpen: false);

        internal ConcurrentMessageQueue(
            string name,
            int slotCount,
            int slotSize,
            bool create = true,
            int maxSpins = 100,
            bool enableStatistics = true)
            : this(
                name,
                slotCount,
                checked(slotSize - SlotHeaderSize),
                new ConcurrentQueueOptions
                {
                    MaxSpins = maxSpins,
                    EnableStatistics = enableStatistics
                },
                create)
        {
        }

        private ConcurrentMessageQueue(
            string name,
            int? capacity,
            int? maxMessageSize,
            ConcurrentQueueOptions? options,
            bool createOrOpen)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));
            if (capacity is <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            if (maxMessageSize is <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxMessageSize));
            if (capacity > 1 << 30)
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    "Capacity is too large to round to a positive power of two.");
            if (maxMessageSize > int.MaxValue - SlotHeaderSize - 7)
                throw new ArgumentOutOfRangeException(
                    nameof(maxMessageSize),
                    "The message size exceeds the supported slot size.");

            options ??= new ConcurrentQueueOptions();
            options.Validate();
            _maxSpins = options.MaxSpins;
            _statsEnabled = options.EnableStatistics;

            if (createOrOpen)
            {
                _slotCount = RoundUpToPowerOf2(capacity!.Value);
                _maxMessageSize = maxMessageSize!.Value;
                _slotTotalSize = RoundUpToMultiple(
                    checked(SlotHeaderSize + _maxMessageSize), 8);
                _slotMask = _slotCount - 1;

                long regionCapacity = checked(HeaderSize + (long)_slotCount * _slotTotalSize);
                if (regionCapacity > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(capacity),
                        $"Total queue size {regionCapacity} exceeds int.MaxValue");

                _buffer = MemoryRegion.CreateOrOpen(
                    name, regionCapacity, options: null, RegionKind.ConcurrentMessageQueue);
            }
            else
            {
                _buffer = MemoryRegion.OpenExisting(
                    name, options: null, RegionKind.ConcurrentMessageQueue);
            }

            // After _buffer is live, any throw in GetMemory/Pin/InitializeBuffer/ValidateBuffer
            // would leak the underlying shared section until finalization. Wrap to guarantee
            // deterministic cleanup — ValidateBuffer in particular can throw on mismatch when
            // a non-owner opens an existing region.
            try
            {
                var memory = _buffer.GetMemory(0, checked((int)_buffer.Capacity));
                _memoryHandle = memory.Pin();
                _header = (Header*)_memoryHandle.Pointer;
                _dataPtr = (byte*)_memoryHandle.Pointer + HeaderSize;

                if (createOrOpen && _buffer.IsOwner)
                {
                    InitializeBuffer();
                }
                else
                {
                    ValidateAndLoadBuffer(capacity, maxMessageSize);
                }
            }
            catch
            {
                try
                { _memoryHandle.Dispose(); }
                catch { /* may be uninitialized */ }
                _buffer.Dispose();
                throw;
            }
        }

        private void InitializeBuffer()
        {
            _header->WriteSequence = 0;
            _header->ReadSequence = 0;
            _header->SlotCount = _slotCount;
            _header->SlotStride = _slotTotalSize;
            _header->MaxMessageSize = _maxMessageSize;
            _header->Version = FormatVersion;
            _header->TotalWrites = 0;
            _header->TotalReads = 0;
            _header->FailedWrites = 0;
            _header->FailedReads = 0;
            _header->SpinExhaustedWrites = 0;
            _header->SpinExhaustedReads = 0;

            // Ensure header is visible before initializing slots (important for weakly-ordered architectures)
            Thread.MemoryBarrier();

            // Initialize all slots with their sequence numbers
            for (int i = 0; i < _slotCount; i++)
            {
                var slot = GetSlot(i);
                slot->Sequence = i;
                slot->DataLength = 0;
            }

            Thread.MemoryBarrier();
            Volatile.Write(ref _header->Magic, HeaderMagic);
        }

        private void ValidateAndLoadBuffer(int? requestedCapacity, int? requestedMaxMessageSize)
        {
            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref _header->Magic) != HeaderMagic)
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidDataException("Timed out waiting for the message queue header.");
                Thread.SpinWait(100);
            }

            if (_header->Version != FormatVersion)
                throw new InvalidDataException(
                    $"Message queue format version {_header->Version} is not supported.");

            int storedSlotCount = _header->SlotCount;
            int storedMaxMessageSize = _header->MaxMessageSize;
            int storedSlotStride = _header->SlotStride;
            int expectedSlotStride;
            try
            {
                expectedSlotStride = RoundUpToMultiple(
                    checked(SlotHeaderSize + storedMaxMessageSize),
                    8);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException(
                    "The message queue header contains overflowing sizing metadata.",
                    ex);
            }

            if (storedSlotCount <= 0 || (storedSlotCount & (storedSlotCount - 1)) != 0 ||
                storedMaxMessageSize <= 0 || storedSlotStride != expectedSlotStride)
                throw new InvalidDataException("The message queue header contains invalid sizing metadata.");

            if (requestedCapacity.HasValue &&
                RoundUpToPowerOf2(requestedCapacity.Value) != storedSlotCount)
                throw new InvalidOperationException(
                    $"Capacity mismatch: expected {RoundUpToPowerOf2(requestedCapacity.Value)}, found {storedSlotCount}");
            if (requestedMaxMessageSize.HasValue &&
                requestedMaxMessageSize.Value != storedMaxMessageSize)
                throw new InvalidOperationException(
                    $"Maximum message size mismatch: expected {requestedMaxMessageSize.Value}, found {storedMaxMessageSize}");

            long expectedRegionCapacity;
            try
            {
                expectedRegionCapacity = checked(
                    HeaderSize + (long)storedSlotCount * storedSlotStride);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException(
                    "The message queue header describes a region that is too large.",
                    ex);
            }

            if (_buffer.Capacity != expectedRegionCapacity)
                throw new InvalidDataException(
                    "The message queue capacity does not match its header.");

            _slotCount = storedSlotCount;
            _maxMessageSize = storedMaxMessageSize;
            _slotTotalSize = storedSlotStride;
            _slotMask = storedSlotCount - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Slot* GetSlot(long index)
        {
            return (Slot*)(_dataPtr + (index & _slotMask) * _slotTotalSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte* GetSlotData(Slot* slot)
        {
            return (byte*)slot + SlotHeaderSize;
        }

        /// <summary>
        /// Tries to write data to the buffer.
        /// Lock-free operation safe for concurrent writers.
        /// </summary>
        /// <returns>True if write succeeded, false if buffer is full</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool TryEnqueue(ReadOnlySpan<byte> data)
        {
            ThrowIfDisposed();

            if (data.Length > MaxMessageSize)
                throw new ArgumentException($"Data size {data.Length} exceeds max {MaxMessageSize}");
            if (data.Length == 0)
                throw new ArgumentException("MPMC messages must contain at least one byte", nameof(data));

            int spinCount = 0;

            while (spinCount < _maxSpins)
            {
                long currentWrite = Volatile.Read(ref _header->WriteSequence);
                long index = currentWrite & _slotMask;
                var slot = GetSlot(index);
                long slotSeq = Volatile.Read(ref slot->Sequence);

                long diff = slotSeq - currentWrite;

                if (diff == 0)
                {
                    // Slot is ready for writing
                    if (Interlocked.CompareExchange(ref _header->WriteSequence, currentWrite + 1, currentWrite) == currentWrite)
                    {
                        // Successfully claimed the slot
                        var slotData = GetSlotData(slot);
                        data.CopyTo(new Span<byte>(slotData, data.Length));
                        slot->DataLength = data.Length;

                        // Release the slot for reading (Volatile.Write has release semantics)
                        Volatile.Write(ref slot->Sequence, currentWrite + 1);
                        if (_statsEnabled)
                            Interlocked.Increment(ref _header->TotalWrites);

                        return true;
                    }
                    // CAS failed, another writer won - retry
                }
                else if (diff < 0)
                {
                    // Buffer is full
                    if (_statsEnabled)
                        Interlocked.Increment(ref _header->FailedWrites);
                    return false;
                }

                // Slot not ready, spin
                Thread.SpinWait(1 << Math.Min(spinCount, 10));
                spinCount++;
            }

            // Exhausted maxSpins waiting for a slot that another writer was preparing —
            // not a "buffer full" condition. Track separately so diagnostics aren't muddied.
            if (_statsEnabled)
                Interlocked.Increment(ref _header->SpinExhaustedWrites);
            return false;
        }

        /// <summary>
        /// Tries to read data from the buffer.
        /// Lock-free operation safe for concurrent readers.
        /// </summary>
        /// <param name="destination">
        /// Destination span. Must be at least as large as the next message; otherwise an
        /// <see cref="ArgumentException"/> is thrown WITHOUT consuming the message — caller can
        /// retry with a larger buffer. Use <see cref="MaxMessageSize"/> to size the destination safely.
        /// </param>
        /// <returns>Number of bytes read, or 0 if buffer is empty</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the next message does not fit in <paramref name="destination"/>. The slot is
        /// left intact so the caller can retry with an adequately sized buffer.
        /// </exception>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private int TryDequeueCore(Span<byte> destination)
        {
            ThrowIfDisposed();

            int spinCount = 0;

            while (spinCount < _maxSpins)
            {
                long currentRead = Volatile.Read(ref _header->ReadSequence);
                long index = currentRead & _slotMask;
                var slot = GetSlot(index);
                long slotSeq = Volatile.Read(ref slot->Sequence);

                long diff = slotSeq - (currentRead + 1);

                if (diff == 0)
                {
                    // Peek the message length BEFORE claiming the slot. If destination is too small,
                    // throw without CAS so the message is preserved for a retry with a larger buffer.
                    // This prevents silent data loss that would occur if we claimed and truncated.
                    int peekLength = Volatile.Read(ref slot->DataLength);
                    if (peekLength <= 0 || peekLength > _maxMessageSize)
                        throw new InvalidDataException(
                            $"The next message declares invalid length {peekLength}.");
                    if (peekLength > destination.Length)
                    {
                        throw new ArgumentException(
                            $"Destination size {destination.Length} is smaller than next message size {peekLength}. " +
                            $"Slot was NOT consumed — retry with a buffer of at least {peekLength} bytes.",
                            nameof(destination));
                    }

                    // Slot has data ready for reading
                    if (Interlocked.CompareExchange(ref _header->ReadSequence, currentRead + 1, currentRead) == currentRead)
                    {
                        // peekLength was published by the writer (it set DataLength BEFORE
                        // Volatile.Write to slot->Sequence). No other thread can mutate this slot
                        // until WE bump slot->Sequence on the line below — so re-reading would
                        // just return the same value at the cost of an extra load.
                        var slotData = GetSlotData(slot);
                        new ReadOnlySpan<byte>(slotData, peekLength).CopyTo(destination);

                        // Release the slot for writing (Volatile.Write has release semantics)
                        Volatile.Write(ref slot->Sequence, currentRead + _slotCount);
                        if (_statsEnabled)
                            Interlocked.Increment(ref _header->TotalReads);

                        return peekLength;
                    }
                    // CAS failed, another reader won - retry
                }
                else if (diff < 0)
                {
                    // Buffer is empty
                    if (_statsEnabled)
                        Interlocked.Increment(ref _header->FailedReads);
                    return 0;
                }

                // Slot not ready, spin
                Thread.SpinWait(1 << Math.Min(spinCount, 10));
                spinCount++;
            }

            // Exhausted maxSpins waiting for a slot that another reader was preparing —
            // not a "buffer empty" condition. Track separately so diagnostics aren't muddied.
            if (_statsEnabled)
                Interlocked.Increment(ref _header->SpinExhaustedReads);
            return 0;
        }

        /// <summary>
        /// Tries to remove one complete message without consuming it when the destination is too small.
        /// </summary>
        public bool TryDequeue(Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = TryDequeueCore(destination);
            return bytesWritten != 0;
        }

        internal bool TryWrite(ReadOnlySpan<byte> data) => TryEnqueue(data);

        internal int TryRead(Span<byte> destination) => TryDequeueCore(destination);

        /// <summary>
        /// Waits until data can be written, with timeout.
        /// </summary>
        /// <param name="data">Data to write</param>
        /// <param name="timeout">Maximum time to wait</param>
        /// <param name="cancellationToken">Token to cancel the wait</param>
        /// <returns>True if write succeeded; false on timeout or cancellation</returns>
        public bool TryEnqueue(ReadOnlySpan<byte> data, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            TimeoutHelper.Validate(timeout, nameof(timeout));

            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();

            while (!TryEnqueue(data))
            {
                if (cancellationToken.IsCancellationRequested || TimeoutHelper.HasExpired(sw, timeout))
                    return false;

                spinner.SpinOnce();
            }

            return true;
        }

        /// <summary>
        /// Waits until data can be read, with timeout.
        /// </summary>
        /// <param name="destination">Buffer to read into</param>
        /// <param name="bytesWritten">Receives the complete message length on success.</param>
        /// <param name="timeout">Maximum time to wait</param>
        /// <param name="cancellationToken">Token to cancel the wait</param>
        /// <returns>Number of bytes read; 0 on timeout or cancellation</returns>
        public bool TryDequeue(
            Span<byte> destination,
            out int bytesWritten,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            TimeoutHelper.Validate(timeout, nameof(timeout));

            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();
            bytesWritten = 0;

            while (!TryDequeue(destination, out bytesWritten))
            {
                if (cancellationToken.IsCancellationRequested || TimeoutHelper.HasExpired(sw, timeout))
                    return false;

                spinner.SpinOnce();
            }

            return true;
        }

        internal bool WaitWrite(
            ReadOnlySpan<byte> data,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            TryEnqueue(data, timeout, cancellationToken);

        internal int WaitRead(
            Span<byte> destination,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return TryDequeue(destination, out int bytesWritten, timeout, cancellationToken)
                ? bytesWritten
                : 0;
        }

        /// <summary>
        /// Gets approximate number of items available for reading
        /// </summary>
        public long ApproximateCount
        {
            get
            {
                ThrowIfDisposed();
                long write = Volatile.Read(ref _header->WriteSequence);
                long read = Volatile.Read(ref _header->ReadSequence);
                return Math.Max(0, write - read);
            }
        }

        /// <summary>
        /// Gets approximate available space for writing
        /// </summary>
        public long ApproximateAvailable
        {
            get
            {
                ThrowIfDisposed();
                return _slotCount - ApproximateCount;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(ConcurrentMessageQueue));
        }

        /// <summary>
        /// Releases all resources used by this buffer
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _memoryHandle.Dispose();
            // See SingleProducerByteStream: only dispose the managed _buffer on the deterministic
            // path. The finalizer below skips it to avoid touching peer objects whose own
            // finalizers may have already run.
            _buffer?.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged resources if Dispose was not called.
        /// </summary>
        ~ConcurrentMessageQueue()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            { _memoryHandle.Dispose(); }
            catch { /* best-effort */ }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int RoundUpToPowerOf2(int value)
        {
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int RoundUpToMultiple(int value, int multiple)
        {
            return checked((value + multiple - 1) / multiple * multiple);
        }
    }
}
