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
    /// Single-producer/single-consumer lock-free byte stream.
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
    /// For multi-producer/multi-consumer scenarios, use <see cref="ConcurrentMessageQueue"/> instead.
    /// Cross-platform: backed by <see cref="MemoryRegion"/> which supports Windows and Linux.
    /// </summary>
    public sealed unsafe class SingleProducerByteStream : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 8, Size = 128)]
        private struct Header
        {
            // Writer-owned cache line (64 bytes)
            public long WritePosition;
            public long Magic;
            public long Version;
            public long Capacity;
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
        private const long HeaderMagic = 0x534253504D504953;
        private const long HeaderVersion = 3;
        private readonly IMemoryRegion _buffer;
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
        /// Creates or opens a single-producer/single-consumer byte stream.
        /// </summary>
        public static SingleProducerByteStream CreateOrOpen(string name, long capacityBytes) =>
            new(name, capacityBytes, createOrOpen: true);

        /// <summary>Opens an existing stream and reads its capacity from shared metadata.</summary>
        public static SingleProducerByteStream OpenExisting(string name) =>
            new(name, capacityBytes: null, createOrOpen: false);

        internal SingleProducerByteStream(string name, long capacity, bool create = true)
            : this(name, create ? capacity : null, create)
        {
        }

        private SingleProducerByteStream(string name, long? capacityBytes, bool createOrOpen)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));
            if (capacityBytes is <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacityBytes));

            if (createOrOpen)
            {
                _capacity = RoundUpToPowerOf2(capacityBytes!.Value);
                if (_capacity <= 0 || _capacity > int.MaxValue - HeaderSize)
                    throw new ArgumentOutOfRangeException(
                        nameof(capacityBytes),
                        "The rounded stream capacity exceeds the supported region size.");
                _capacityMask = _capacity - 1;
                long totalSize = checked(HeaderSize + _capacity);

                _buffer = MemoryRegion.CreateOrOpen(
                    name, totalSize, options: null, RegionKind.SingleProducerByteStream);
            }
            else
            {
                _buffer = MemoryRegion.OpenExisting(
                    name, options: null, RegionKind.SingleProducerByteStream);
                if (_buffer.Capacity <= HeaderSize)
                {
                    _buffer.Dispose();
                    throw new InvalidDataException("The byte stream region is smaller than its header.");
                }

                _capacity = _buffer.Capacity - HeaderSize;
                _capacityMask = _capacity - 1;
            }

            // After _buffer is live, any throw in GetMemory/Pin/header-init would leak the
            // underlying shared section until finalization. Wrap to guarantee deterministic
            // cleanup, mirroring MemoryRegion's own constructor-leak fix.
            try
            {
                var memory = _buffer.GetMemory(0, (int)(HeaderSize + _capacity));
                _memoryHandle = memory.Pin();
                _header = (Header*)_memoryHandle.Pointer;
                _dataPtr = (byte*)_memoryHandle.Pointer + HeaderSize;

                if (createOrOpen && _buffer.IsOwner)
                {
                    InitializeBuffer();
                }
                else
                {
                    ValidateBuffer();
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
            _header->WritePosition = 0;
            _header->ReadPosition = 0;
            _header->Version = HeaderVersion;
            _header->Capacity = _capacity;
            Thread.MemoryBarrier();
            Volatile.Write(ref _header->Magic, HeaderMagic);
        }

        private void ValidateBuffer()
        {
            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref _header->Magic) != HeaderMagic)
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidDataException(
                        "Timed out waiting for the single-producer byte-stream header.");
                Thread.SpinWait(100);
            }

            long version = Volatile.Read(ref _header->Version);
            if (version != HeaderVersion)
                throw new InvalidDataException(
                    $"Byte-stream version mismatch: expected {HeaderVersion}, found {version}");

            long capacity = Volatile.Read(ref _header->Capacity);
            if (capacity != _capacity)
                throw new InvalidDataException(
                    $"Byte-stream capacity mismatch: expected {_capacity}, found {capacity}");
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

            // SPSC: only this thread (the writer) mutates WritePosition, so a plain load is safe —
            // no need for acquire semantics. ReadPosition IS mutated by the consumer, so it still
            // needs Volatile.Read to observe the latest published value.
            long writePos = _header->WritePosition;
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

            // Symmetric to TryWrite: SPSC reader owns ReadPosition (plain load) and observes
            // WritePosition published by the producer (Volatile.Read for acquire semantics).
            long writePos = Volatile.Read(ref _header->WritePosition);
            long readPos = _header->ReadPosition;

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
            ThrowIfDisposed();
            TimeoutHelper.Validate(timeout, nameof(timeout));

            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();

            while (!TryWrite(data))
            {
                if (cancellationToken.IsCancellationRequested || TimeoutHelper.HasExpired(sw, timeout))
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
            ThrowIfDisposed();
            TimeoutHelper.Validate(timeout, nameof(timeout));

            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();
            int bytesRead;

            while ((bytesRead = TryRead(destination)) == 0)
            {
                if (cancellationToken.IsCancellationRequested || TimeoutHelper.HasExpired(sw, timeout))
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
            if (used < 0)
                return _capacity;          // readPos raced ahead — treat as empty
            if (used > _capacity)
                return 0;          // bogus state — refuse writes
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
            if (used < 0)
                return 0;
            if (used > _capacity)
                return _capacity;
            return used;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(SingleProducerByteStream));
        }

        /// <summary>
        /// Releases all resources used by this buffer
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // MemoryHandle wraps an unmanaged pointer — safe to dispose anywhere.
            _memoryHandle.Dispose();
            // _buffer (MemoryRegion) is a managed object with its OWN finalizer.
            // We only proactively dispose it on the deterministic path; from our finalizer we
            // let the GC handle it to avoid touching a possibly-already-finalized peer.
            _buffer?.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged resources if Dispose was not called.
        /// Skips the managed <c>_buffer.Dispose()</c> — its own finalizer reclaims it.
        /// </summary>
        ~SingleProducerByteStream()
        {
            // Guard against double-dispose if Dispose() already ran. _disposed is volatile so
            // we observe its current value here.
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            { _memoryHandle.Dispose(); }
            catch { /* unmanaged release; best-effort */ }
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
