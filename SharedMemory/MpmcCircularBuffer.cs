using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace SharedMemory
{
    /// <summary>
    /// Multi-Producer/Multi-Consumer lock-free circular buffer.
    /// Thread-safe for concurrent access from multiple writers and readers.
    /// Uses sequence numbers for coordination instead of simple head/tail pointers.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed unsafe class MpmcCircularBuffer : IDisposable
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
            [FieldOffset(132)] public int SlotSize;

            // Writer statistics (cache line 4: offset 192-255) - separate from reader stats
            [FieldOffset(192)] public long TotalWrites;
            [FieldOffset(200)] public long FailedWrites;

            // Reader statistics (cache line 5: offset 256-319) - separate from writer stats
            [FieldOffset(256)] public long TotalReads;
            [FieldOffset(264)] public long FailedReads;

            // Reserved (cache line 6: offset 320-383)
            [FieldOffset(320)] public long Reserved;
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

        private readonly ISharedMemoryBuffer _buffer;
        private readonly int _slotCount;
        private readonly int _slotSize;
        private readonly int _slotTotalSize;
        private readonly long _slotMask;
        private readonly byte* _dataPtr;
        private readonly Header* _header;
        private readonly MemoryHandle _memoryHandle;

        private volatile int _disposed;

        /// <summary>
        /// Gets the number of slots in the buffer
        /// </summary>
        public int SlotCount => _slotCount;

        /// <summary>
        /// Gets the maximum message size per slot
        /// </summary>
        public int MaxMessageSize => _slotSize - SlotHeaderSize;

        /// <summary>
        /// Gets statistics about buffer operations
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
        /// Creates a new MPMC circular buffer.
        /// </summary>
        /// <param name="name">Unique name for the shared memory region</param>
        /// <param name="slotCount">Number of slots (will be rounded up to power of 2)</param>
        /// <param name="slotSize">Size of each slot in bytes (including header)</param>
        /// <param name="create">True to create new, false to open existing</param>
        public MpmcCircularBuffer(string name, int slotCount, int slotSize, bool create = true)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));
            if (slotCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(slotCount));
            if (slotSize <= SlotHeaderSize)
                throw new ArgumentOutOfRangeException(nameof(slotSize), $"Slot size must be > {SlotHeaderSize}");

            _slotCount = RoundUpToPowerOf2(slotCount);
            _slotSize = slotSize;
            _slotTotalSize = _slotSize;
            _slotMask = _slotCount - 1;

            long capacity = HeaderSize + (long)_slotCount * _slotTotalSize;

            var options = new SharedMemoryBufferOptions
            {
                Capacity = capacity,
                CreateOrOpen = create,
                EnableSimd = true
            };

            _buffer = new HighPerformanceSharedBuffer(name, options);

            var memory = _buffer.GetMemory(0, (int)capacity);
            _memoryHandle = memory.Pin();
            _header = (Header*)_memoryHandle.Pointer;
            _dataPtr = (byte*)_memoryHandle.Pointer + HeaderSize;

            if (create && _buffer.IsOwner)
            {
                InitializeBuffer();
            }
            else
            {
                ValidateBuffer();
            }
        }

        private void InitializeBuffer()
        {
            _header->WriteSequence = 0;
            _header->ReadSequence = 0;
            _header->SlotCount = _slotCount;
            _header->SlotSize = _slotSize;
            _header->TotalWrites = 0;
            _header->TotalReads = 0;
            _header->FailedWrites = 0;
            _header->FailedReads = 0;

            // Ensure header is visible before initializing slots (important for weakly-ordered architectures)
            Thread.MemoryBarrier();

            // Initialize all slots with their sequence numbers
            for (int i = 0; i < _slotCount; i++)
            {
                var slot = GetSlot(i);
                slot->Sequence = i;
                slot->DataLength = 0;
            }

            // Final barrier to ensure all initialization is visible
            Thread.MemoryBarrier();
        }

        private void ValidateBuffer()
        {
            if (_header->SlotCount != _slotCount)
                throw new InvalidOperationException(
                    $"Slot count mismatch: expected {_slotCount}, found {_header->SlotCount}");
            if (_header->SlotSize != _slotSize)
                throw new InvalidOperationException(
                    $"Slot size mismatch: expected {_slotSize}, found {_header->SlotSize}");
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
        public bool TryWrite(ReadOnlySpan<byte> data)
        {
            ThrowIfDisposed();

            if (data.Length > MaxMessageSize)
                throw new ArgumentException($"Data size {data.Length} exceeds max {MaxMessageSize}");

            int spinCount = 0;
            const int MaxSpins = 100;

            while (spinCount < MaxSpins)
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
                        Interlocked.Increment(ref _header->TotalWrites);

                        return true;
                    }
                    // CAS failed, another writer won - retry
                }
                else if (diff < 0)
                {
                    // Buffer is full
                    Interlocked.Increment(ref _header->FailedWrites);
                    return false;
                }

                // Slot not ready, spin
                Thread.SpinWait(1 << Math.Min(spinCount, 10));
                spinCount++;
            }

            Interlocked.Increment(ref _header->FailedWrites);
            return false;
        }

        /// <summary>
        /// Tries to read data from the buffer.
        /// Lock-free operation safe for concurrent readers.
        /// </summary>
        /// <returns>Number of bytes read, or 0 if buffer is empty</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public int TryRead(Span<byte> destination)
        {
            ThrowIfDisposed();

            int spinCount = 0;
            const int MaxSpins = 100;

            while (spinCount < MaxSpins)
            {
                long currentRead = Volatile.Read(ref _header->ReadSequence);
                long index = currentRead & _slotMask;
                var slot = GetSlot(index);
                long slotSeq = Volatile.Read(ref slot->Sequence);

                long diff = slotSeq - (currentRead + 1);

                if (diff == 0)
                {
                    // Slot has data ready for reading
                    if (Interlocked.CompareExchange(ref _header->ReadSequence, currentRead + 1, currentRead) == currentRead)
                    {
                        // Successfully claimed the slot
                        int dataLength = slot->DataLength;

                        if (dataLength > destination.Length)
                            dataLength = destination.Length;

                        var slotData = GetSlotData(slot);
                        new ReadOnlySpan<byte>(slotData, dataLength).CopyTo(destination);

                        // Release the slot for writing (Volatile.Write has release semantics)
                        Volatile.Write(ref slot->Sequence, currentRead + _slotCount);
                        Interlocked.Increment(ref _header->TotalReads);

                        return dataLength;
                    }
                    // CAS failed, another reader won - retry
                }
                else if (diff < 0)
                {
                    // Buffer is empty
                    Interlocked.Increment(ref _header->FailedReads);
                    return 0;
                }

                // Slot not ready, spin
                Thread.SpinWait(1 << Math.Min(spinCount, 10));
                spinCount++;
            }

            Interlocked.Increment(ref _header->FailedReads);
            return 0;
        }

        /// <summary>
        /// Waits until data can be written, with timeout.
        /// </summary>
        public bool WaitWrite(ReadOnlySpan<byte> data, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();

            while (!TryWrite(data))
            {
                if (sw.Elapsed > timeout)
                    return false;

                spinner.SpinOnce();
            }

            return true;
        }

        /// <summary>
        /// Waits until data can be read, with timeout.
        /// </summary>
        public int WaitRead(Span<byte> destination, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();
            int bytesRead;

            while ((bytesRead = TryRead(destination)) == 0)
            {
                if (sw.Elapsed > timeout)
                    return 0;

                spinner.SpinOnce();
            }

            return bytesRead;
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
                throw new ObjectDisposedException(nameof(MpmcCircularBuffer));
        }

        /// <summary>
        /// Releases all resources used by this buffer
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _memoryHandle.Dispose();
            _buffer?.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged resources if Dispose was not called
        /// </summary>
        ~MpmcCircularBuffer()
        {
            _memoryHandle.Dispose();
            _buffer?.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int RoundUpToPowerOf2(int value)
        {
            if (value <= 0)
                return 1;
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }
    }
}
