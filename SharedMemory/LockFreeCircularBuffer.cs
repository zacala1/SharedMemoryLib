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
            (_totalWrites, _totalReads, _totalBytesWritten, _totalBytesRead, _totalSpins);

        /// <summary>
        /// Creates or opens a lock-free SPSC circular buffer.
        /// </summary>
        /// <param name="name">Unique name for the shared memory region</param>
        /// <param name="capacity">Requested capacity (will be rounded up to power of 2)</param>
        /// <param name="create">True to create new, false to open existing</param>
        /// <exception cref="ArgumentException">Thrown when name is empty</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when capacity is invalid</exception>
        public LockFreeCircularBuffer(string name, long capacity, bool create = true)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));
            if (capacity <= 0 || capacity > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            // Round up to power of 2 for fast modulo using bitwise AND
            _capacity = RoundUpToPowerOf2(capacity);
            _capacityMask = _capacity - 1;

            var options = new SharedMemoryBufferOptions
            {
                Capacity = HeaderSize + _capacity,
                CreateOrOpen = create,
                EnableSimd = true,
                UseLockFree = true
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

            Interlocked.Increment(ref _totalWrites);
            Interlocked.Add(ref _totalBytesWritten, data.Length);

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

            Interlocked.Increment(ref _totalReads);
            Interlocked.Add(ref _totalBytesRead, bytesToRead);

            return bytesToRead;
        }

        /// <summary>
        /// Waits until data can be written, with spinning and yielding strategy
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
                Interlocked.Increment(ref _totalSpins);
            }

            return true;
        }

        /// <summary>
        /// Waits until data can be read, with spinning and yielding strategy
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
                Interlocked.Increment(ref _totalSpins);
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
            return _capacity - (writePos - readPos);
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
            return writePos - readPos;
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
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            value |= value >> 32;
            return value + 1;
        }
    }
}
