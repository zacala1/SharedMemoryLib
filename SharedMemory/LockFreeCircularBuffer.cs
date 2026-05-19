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
    /// Ultra-high-performance lock-free circular buffer using atomic operations.
    /// Target: 80GB/s+ throughput (4x original implementation).
    ///
    /// Design:
    /// - Single-producer/single-consumer (SPSC) optimized
    /// - Cache-line padding to prevent false sharing
    /// - Memory barriers for cross-process visibility
    /// - Zero-allocation API using Span&lt;T&gt;
    ///
    /// WARNING: This buffer is designed for SPSC (Single-Producer/Single-Consumer) use only.
    /// Using multiple producers OR multiple consumers concurrently will cause data corruption.
    /// For multi-producer/multi-consumer scenarios, use <see cref="MpmcCircularBuffer"/> instead.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed unsafe class LockFreeCircularBuffer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 8, Size = 128)]
        private struct Header
        {
            // Writer-owned cache line (64 bytes)
            public long WritePosition;
            public long WriterPadding1;
            public long WriterPadding2;
            public long WriterPadding3;
            public long WriterPadding4;
            public long WriterPadding5;
            public long WriterPadding6;
            public long WriterPadding7;

            // Reader-owned cache line (64 bytes)
            public long ReadPosition;
            public long ReaderPadding1;
            public long ReaderPadding2;
            public long ReaderPadding3;
            public long ReaderPadding4;
            public long ReaderPadding5;
            public long ReaderPadding6;
            public long ReaderPadding7;
        }

        private const int HeaderSize = 128;
        private readonly ISharedMemoryBuffer _buffer;
        private readonly long _capacity;
        private readonly long _capacityMask; // For power-of-2 optimization
        private readonly byte* _dataPtr;
        private readonly Header* _header;
        private readonly MemoryHandle _memoryHandle;

        private volatile int _disposed;

        // Performance counters
        private long _totalWrites;
        private long _totalReads;
        private long _totalBytesWritten;
        private long _totalBytesRead;
        private long _totalSpins;

        /// <summary>
        /// Gets the total capacity of the buffer in bytes (power of 2)
        /// </summary>
        public long Capacity => _capacity;

        /// <summary>
        /// Gets the available space in bytes for writing
        /// </summary>
        public long Available => CalculateAvailable();

        /// <summary>
        /// Gets the used space in bytes (data ready for reading)
        /// </summary>
        public long Used => CalculateUsed();

        /// <summary>
        /// Gets performance statistics for the buffer
        /// </summary>
        /// <returns>Tuple containing write/read counts, bytes transferred, and spin counts</returns>
        public (long Writes, long Reads, long BytesWritten, long BytesRead, long Spins) GetStatistics() =>
            (Volatile.Read(ref _totalWrites), Volatile.Read(ref _totalReads),
             Volatile.Read(ref _totalBytesWritten), Volatile.Read(ref _totalBytesRead),
             Volatile.Read(ref _totalSpins));

        /// <summary>
        /// Creates or opens a lock-free SPSC circular buffer.
        /// </summary>
        /// <param name="name">Unique name for the shared memory region</param>
        /// <param name="capacity">Requested capacity (will be rounded up to power of 2)</param>
        /// <param name="create">True to create new, false to open existing</param>
        /// <exception cref="ArgumentException">Thrown when name is empty</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when capacity is invalid or total size exceeds int.MaxValue after power-of-2 rounding</exception>
        public LockFreeCircularBuffer(string name, long capacity, bool create = true)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            // Round up to power of 2 for fast modulo using bitwise AND
            _capacity = RoundUpToPowerOf2(capacity);
            _capacityMask = _capacity - 1;

            // Validate total size fits in int (required by GetMemory)
            long totalSize = HeaderSize + _capacity;
            if (totalSize > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(capacity),
                    $"Total buffer size {totalSize} (capacity {_capacity} + header {HeaderSize}) exceeds int.MaxValue");

            var options = new SharedMemoryBufferOptions
            {
                Capacity = HeaderSize + _capacity,
                CreateOrOpen = create,
                EnableSimd = true
            };

            _buffer = new HighPerformanceSharedBuffer(name, options);

            // Get direct pointer access for maximum performance
            var memory = _buffer.GetMemory(0, (int)(HeaderSize + _capacity));
            _memoryHandle = memory.Pin();
            _header = (Header*)_memoryHandle.Pointer;
            _dataPtr = (byte*)_memoryHandle.Pointer + HeaderSize;

            if (create)
            {
                _header->WritePosition = 0;
                _header->ReadPosition = 0;
                Thread.MemoryBarrier();
            }
        }

        /// <summary>
        /// Tries to write data to the circular buffer.
        /// Returns true if successful, false if insufficient space.
        /// Zero-allocation, lock-free operation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool TryWrite(ReadOnlySpan<byte> data)
        {
            ThrowIfDisposed();

            if (data.Length == 0)
                return true;

            if (data.Length > _capacity)
                throw new ArgumentException($"Data size {data.Length} exceeds capacity {_capacity}");

            long writePos = Volatile.Read(ref _header->WritePosition);
            long readPos = Volatile.Read(ref _header->ReadPosition);

            long available = CalculateAvailable(writePos, readPos);
            if (available < data.Length)
                return false;

            // Calculate position in circular buffer using bitwise AND (faster than modulo)
            long bufferPos = writePos & _capacityMask;
            long firstPartLength = Math.Min(data.Length, _capacity - bufferPos);

            // Copy first part
            data.Slice(0, (int)firstPartLength).CopyTo(
                new Span<byte>(_dataPtr + bufferPos, (int)firstPartLength));

            // Copy second part (wrap around)
            if (firstPartLength < data.Length)
            {
                data.Slice((int)firstPartLength).CopyTo(
                    new Span<byte>(_dataPtr, (int)(data.Length - firstPartLength)));
            }

            // Volatile.Write has release semantics - ensures data is visible before publishing
            Volatile.Write(ref _header->WritePosition, writePos + data.Length);

            // SPSC: only writer thread updates these — no atomics needed
            _totalWrites++;
            _totalBytesWritten += data.Length;

            return true;
        }

        /// <summary>
        /// Tries to read data from the circular buffer.
        /// Returns the number of bytes actually read (may be less than requested).
        /// Zero-allocation, lock-free operation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public int TryRead(Span<byte> destination)
        {
            ThrowIfDisposed();

            if (destination.Length == 0)
                return 0;

            long writePos = Volatile.Read(ref _header->WritePosition);
            long readPos = Volatile.Read(ref _header->ReadPosition);

            long used = CalculateUsed(writePos, readPos);
            if (used == 0)
                return 0;

            int bytesToRead = (int)Math.Min(destination.Length, used);

            // Calculate position in circular buffer using bitwise AND (faster than modulo)
            long bufferPos = readPos & _capacityMask;
            long firstPartLength = Math.Min(bytesToRead, _capacity - bufferPos);

            // Copy first part
            new ReadOnlySpan<byte>(_dataPtr + bufferPos, (int)firstPartLength).CopyTo(
                destination);

            // Copy second part (wrap around)
            if (firstPartLength < bytesToRead)
            {
                new ReadOnlySpan<byte>(_dataPtr, (int)(bytesToRead - firstPartLength)).CopyTo(
                    destination.Slice((int)firstPartLength));
            }

            // Volatile.Write has release semantics - ensures all stores complete before publishing
            Volatile.Write(ref _header->ReadPosition, readPos + bytesToRead);

            // SPSC: only reader thread updates these — no atomics needed
            _totalReads++;
            _totalBytesRead += bytesToRead;

            return bytesToRead;
        }

        /// <summary>
        /// Waits until data can be written, with spinning and yielding strategy
        /// </summary>
        /// <param name="data">Data to write</param>
        /// <param name="timeout">Maximum time to wait</param>
        /// <param name="cancellationToken">Token to cancel the wait</param>
        /// <returns>True if write succeeded; false on timeout or cancellation</returns>
        public bool WaitWrite(ReadOnlySpan<byte> data, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();

            while (!TryWrite(data))
            {
                if (cancellationToken.IsCancellationRequested || sw.Elapsed > timeout)
                    return false;

                spinner.SpinOnce();
                _totalSpins++;
            }

            return true;
        }

        /// <summary>
        /// Waits until data can be read, with spinning and yielding strategy
        /// </summary>
        /// <param name="destination">Buffer to read into</param>
        /// <param name="timeout">Maximum time to wait</param>
        /// <param name="cancellationToken">Token to cancel the wait</param>
        /// <returns>Number of bytes read; 0 on timeout or cancellation</returns>
        public int WaitRead(Span<byte> destination, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();
            int bytesRead;

            while ((bytesRead = TryRead(destination)) == 0)
            {
                if (cancellationToken.IsCancellationRequested || sw.Elapsed > timeout)
                    return 0;

                spinner.SpinOnce();
                _totalSpins++;
            }

            return bytesRead;
        }

        /// <summary>
        /// Clears the buffer (resets read/write positions)
        /// WARNING: Not thread-safe with concurrent readers/writers
        /// </summary>
        public void Clear()
        {
            ThrowIfDisposed();

            Volatile.Write(ref _header->WritePosition, 0);
            Volatile.Write(ref _header->ReadPosition, 0);
            Thread.MemoryBarrier();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long CalculateAvailable()
        {
            long writePos = Volatile.Read(ref _header->WritePosition);
            long readPos = Volatile.Read(ref _header->ReadPosition);
            return CalculateAvailable(writePos, readPos);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long CalculateAvailable(long writePos, long readPos)
        {
            // SPSC guarantees 0 <= writePos - readPos <= _capacity, but Clear() races and
            // observed-stale reads (writer sees an old readPos > writePos for a moment) can
            // produce values outside that range. Clamp both ends so TryWrite never sees
            // available > _capacity (which would falsely admit oversized writes).
            long used = writePos - readPos;
            if (used < 0) return _capacity;          // readPos raced ahead — treat as empty
            if (used > _capacity) return 0;          // bogus state — refuse writes
            return _capacity - used;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long CalculateUsed()
        {
            long writePos = Volatile.Read(ref _header->WritePosition);
            long readPos = Volatile.Read(ref _header->ReadPosition);
            return CalculateUsed(writePos, readPos);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long CalculateUsed(long writePos, long readPos)
        {
            // Same race window as CalculateAvailable — clamp to [0, _capacity] so
            // TryRead doesn't try to read more than what actually fits in the buffer.
            long used = writePos - readPos;
            if (used < 0) return 0;
            if (used > _capacity) return _capacity;
            return used;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(LockFreeCircularBuffer));
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
        ~LockFreeCircularBuffer()
        {
            _memoryHandle.Dispose();
            _buffer?.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long RoundUpToPowerOf2(long value)
        {
            if (value <= 0)
                return 1;
            // BitOperations.RoundUpToPowerOf2 uses lzcnt hardware acceleration
            return (long)System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)value);
        }
    }
}
