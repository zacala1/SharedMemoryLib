using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SharedMemory
{
    /// <summary>
    /// High-performance shared memory buffer with zero-allocation APIs, SIMD optimizations,
    /// and lock-free synchronization. Designed for .NET 8+ with modern performance patterns.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed unsafe class HighPerformanceSharedBuffer : ISharedMemoryBuffer
    {
        private readonly string _name;
        private readonly long _capacity;
        private readonly bool _isOwner;
        private readonly SharedMemoryBufferOptions _options;
        private readonly ILogger? _logger;

        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _accessor;
        private byte* _basePtr;

        // Lock-free synchronization state
        private volatile int _writerCount;
        private volatile int _readerCount;

        // Performance counters
        private long _totalReads;
        private long _totalWrites;
        private long _totalBytesRead;
        private long _totalBytesWritten;

        private volatile int _disposed;

        /// <summary>
        /// Extended header structure with orphan lock detection support
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct SharedHeader
        {
            public const uint MagicNumber = 0x48504D53; // "SMHP"
            public const int Size = 128; // Extended for lock owner info

            public uint Magic;
            public uint Version;
            public long Capacity;
            public long CreationTimestamp;
            public int WriterLockState; // 0 = free, 1 = locked (accessed via Interlocked/Volatile)
            public int ReaderCount; // accessed via Interlocked/Volatile

            // Orphan lock detection fields
            public int LockOwnerProcessId;
            public long LockOwnerThreadId;
            public long LockAcquiredTimestamp;

            // Checksum for integrity verification
            public uint DataChecksum;
            public long ChecksumOffset;
            public int ChecksumLength;

            // Padding to 128 bytes
            public long Padding1;
            public long Padding2;
        }

        private const long HeaderSize = SharedHeader.Size;

        /// <inheritdoc/>
        public string Name => _name;

        /// <inheritdoc/>
        public long Capacity => _capacity;

        /// <inheritdoc/>
        public bool IsOwner => _isOwner;

        /// <inheritdoc/>
        public event BufferEventHandler? OnDataWritten;

        /// <inheritdoc/>
        public event BufferEventHandler? OnOrphanLockDetected;

        /// <summary>
        /// Gets performance statistics for the buffer
        /// </summary>
        /// <returns>Tuple containing read/write counts and bytes transferred</returns>
        public (long Reads, long Writes, long BytesRead, long BytesWritten) GetStatistics() =>
            (_totalReads, _totalWrites, _totalBytesRead, _totalBytesWritten);

        /// <summary>
        /// Creates or opens a high-performance shared memory buffer.
        /// </summary>
        /// <param name="name">Unique name for the shared memory region</param>
        /// <param name="options">Configuration options for the buffer</param>
        /// <exception cref="ArgumentException">Thrown when name is empty or whitespace</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when options contain invalid values</exception>
        public HighPerformanceSharedBuffer(string name, SharedMemoryBufferOptions options)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));

            _options = options ?? new SharedMemoryBufferOptions();
            _options.Validate(); // Always validate (including defaults)
            _logger = _options.Logger;

            _name = name;
            _capacity = _options.Capacity;

            _logger?.LogDebug("Creating shared buffer '{Name}' with capacity {Capacity}", name, _capacity);

            Initialize();
            _isOwner = InitializeOrOpen();

            _logger?.LogInformation("Shared buffer '{Name}' initialized. IsOwner: {IsOwner}", name, _isOwner);
        }

        private void Initialize()
        {
            long totalSize = HeaderSize + _capacity;

            try
            {
                if (string.IsNullOrEmpty(_options.FilePath))
                {
                    _mmf = MemoryMappedFile.CreateOrOpen(
                        _name,
                        totalSize,
                        MemoryMappedFileAccess.ReadWrite,
                        MemoryMappedFileOptions.None,
                        HandleInheritability.None);
                }
                else
                {
                    _mmf = MemoryMappedFile.CreateFromFile(
                        _options.FilePath,
                        FileMode.OpenOrCreate,
                        _name,
                        totalSize,
                        MemoryMappedFileAccess.ReadWrite);
                }

                _accessor = _mmf.CreateViewAccessor(0, totalSize, MemoryMappedFileAccess.ReadWrite);
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);

                if (_basePtr == null)
                    throw new InvalidOperationException("Failed to acquire pointer to shared memory");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize shared buffer '{Name}'", _name);
                Cleanup();
                throw;
            }
        }

        private bool InitializeOrOpen()
        {
            var header = (SharedHeader*)_basePtr;

            if (Interlocked.CompareExchange(ref header->Magic, SharedHeader.MagicNumber, 0) == 0)
            {
                header->Version = 2; // Version 2 with extended header
                header->Capacity = _capacity;
                header->CreationTimestamp = Stopwatch.GetTimestamp();
                header->WriterLockState = 0;
                header->ReaderCount = 0;
                header->LockOwnerProcessId = 0;
                header->LockOwnerThreadId = 0;
                header->LockAcquiredTimestamp = 0;
                header->DataChecksum = 0;

                Thread.MemoryBarrier();
                return true;
            }

            if (header->Magic != SharedHeader.MagicNumber)
                throw new InvalidDataException("Invalid shared memory header");

            if (header->Capacity != _capacity)
                throw new InvalidOperationException(
                    $"Capacity mismatch: expected {_capacity}, found {header->Capacity}");

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte* GetDataPtr() => _basePtr + HeaderSize;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ValidateOffset(long offset, int length)
        {
            // Check for negative offset or length first (prevents ulong overflow wraparound)
            if (offset < 0 || length < 0 || (ulong)offset + (ulong)length > (ulong)_capacity)
                throw new ArgumentOutOfRangeException(nameof(offset),
                    $"Access at offset {offset} with length {length} exceeds capacity {_capacity}");
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public int Write(ReadOnlySpan<byte> source, long offset)
        {
            ThrowIfDisposed();
            ValidateOffset(offset, source.Length);

            byte* destPtr = GetDataPtr() + offset;

            if (_options.EnableSimd && IsAligned(destPtr) && source.Length >= Vector<byte>.Count)
            {
                WriteSimd(source, destPtr);
            }
            else
            {
                source.CopyTo(new Span<byte>(destPtr, source.Length));
            }

            Interlocked.Increment(ref _totalWrites);
            Interlocked.Add(ref _totalBytesWritten, source.Length);

            if (_options.EnableEvents)
            {
                OnDataWritten?.Invoke(this, new BufferEventArgs
                {
                    EventType = BufferEventType.DataWritten,
                    BytesAffected = source.Length,
                    Offset = offset
                });
            }

            _logger?.LogTrace("Written {Bytes} bytes at offset {Offset}", source.Length, offset);

            return source.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void WriteSimd(ReadOnlySpan<byte> source, byte* destPtr)
        {
            int vectorSize = Vector<byte>.Count;
            int vectorCount = source.Length / vectorSize;
            int remainder = source.Length % vectorSize;

            ref byte sourceRef = ref MemoryMarshal.GetReference(source);

            for (int i = 0; i < vectorCount; i++)
            {
                var vector = Unsafe.ReadUnaligned<Vector<byte>>(
                    ref Unsafe.Add(ref sourceRef, i * vectorSize));
                Unsafe.WriteUnaligned(destPtr + i * vectorSize, vector);
            }

            if (remainder > 0)
            {
                source.Slice(vectorCount * vectorSize).CopyTo(
                    new Span<byte>(destPtr + vectorCount * vectorSize, remainder));
            }
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public int Read(Span<byte> destination, long offset)
        {
            ThrowIfDisposed();
            ValidateOffset(offset, destination.Length);

            byte* srcPtr = GetDataPtr() + offset;

            if (_options.EnableSimd && IsAligned(srcPtr) && destination.Length >= Vector<byte>.Count)
            {
                ReadSimd(destination, srcPtr);
            }
            else
            {
                new ReadOnlySpan<byte>(srcPtr, destination.Length).CopyTo(destination);
            }

            Interlocked.Increment(ref _totalReads);
            Interlocked.Add(ref _totalBytesRead, destination.Length);

            return destination.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ReadSimd(Span<byte> destination, byte* srcPtr)
        {
            int vectorSize = Vector<byte>.Count;
            int vectorCount = destination.Length / vectorSize;
            int remainder = destination.Length % vectorSize;

            ref byte destRef = ref MemoryMarshal.GetReference(destination);

            for (int i = 0; i < vectorCount; i++)
            {
                var vector = Unsafe.ReadUnaligned<Vector<byte>>(srcPtr + i * vectorSize);
                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref destRef, i * vectorSize), vector);
            }

            if (remainder > 0)
            {
                new ReadOnlySpan<byte>(srcPtr + vectorCount * vectorSize, remainder).CopyTo(
                    destination.Slice(vectorCount * vectorSize));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsAligned(void* ptr) =>
            ((nuint)ptr & (nuint)(_options.Alignment - 1)) == 0;

        /// <inheritdoc/>
        public ValueTask<int> WriteAsync(ReadOnlyMemory<byte> source, long offset,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<int>(cancellationToken);

            try
            {
                int written = Write(source.Span, offset);
                return ValueTask.FromResult(written);
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<int>(ex);
            }
        }

        /// <inheritdoc/>
        public ValueTask<int> ReadAsync(Memory<byte> destination, long offset,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<int>(cancellationToken);

            try
            {
                int read = Read(destination.Span, offset);
                return ValueTask.FromResult(read);
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<int>(ex);
            }
        }

        /// <inheritdoc/>
        public Memory<byte> GetMemory(long offset, int length)
        {
            ThrowIfDisposed();
            ValidateOffset(offset, length);

            byte* ptr = GetDataPtr() + offset;
            return new UnmanagedMemoryManager<byte>(ptr, length).Memory;
        }

        /// <inheritdoc/>
        public bool TryAcquireWriteLock(TimeSpan timeout)
        {
            ThrowIfDisposed();

            var header = (SharedHeader*)_basePtr;
            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();
            bool orphanCheckDone = false;

            while (true)
            {
                if (Interlocked.CompareExchange(ref header->WriterLockState, 1, 0) == 0)
                {
                    bool success = false;
                    try
                    {
                        // Record lock ownership for orphan detection
                        header->LockOwnerProcessId = Environment.ProcessId;
                        header->LockOwnerThreadId = Environment.CurrentManagedThreadId;
                        header->LockAcquiredTimestamp = Stopwatch.GetTimestamp();
                        Thread.MemoryBarrier();

                        var readerSpinner = new SpinWait();
                        while (Volatile.Read(ref header->ReaderCount) > 0)
                        {
                            if (sw.Elapsed > timeout)
                            {
                                return false; // Will release lock in finally
                            }

                            readerSpinner.SpinOnce();
                        }

                        Interlocked.Increment(ref _writerCount);
                        _logger?.LogTrace("Write lock acquired by process {ProcessId}", Environment.ProcessId);
                        success = true;
                        return true;
                    }
                    finally
                    {
                        if (!success)
                        {
                            // Clear ownership and release on failure
                            header->LockOwnerProcessId = 0;
                            header->LockOwnerThreadId = 0;
                            header->LockAcquiredTimestamp = 0;
                            Interlocked.Exchange(ref header->WriterLockState, 0);
                        }
                    }
                }

                if (sw.Elapsed > timeout)
                    return false;

                // Check for orphan lock only once per acquisition attempt (after first CAS failure)
                // This avoids TOCTOU race by checking inside the spin loop
                if (_options.EnableOrphanLockDetection && !orphanCheckDone)
                {
                    orphanCheckDone = true;
                    if (IsWriteLockOrphaned())
                    {
                        _logger?.LogWarning("Detected orphan write lock, attempting recovery");
                        TryForceReleaseWriteLock();
                        // Continue loop to retry CAS immediately after recovery
                        continue;
                    }
                }

                spinner.SpinOnce();
            }
        }

        /// <inheritdoc/>
        public void ReleaseWriteLock()
        {
            ThrowIfDisposed();

            var header = (SharedHeader*)_basePtr;

            // Clear ownership info
            header->LockOwnerProcessId = 0;
            header->LockOwnerThreadId = 0;
            header->LockAcquiredTimestamp = 0;

            Thread.MemoryBarrier();
            Volatile.Write(ref header->WriterLockState, 0);
            Interlocked.Decrement(ref _writerCount);

            _logger?.LogTrace("Write lock released");
        }

        /// <inheritdoc/>
        public bool TryAcquireReadLock(TimeSpan timeout)
        {
            ThrowIfDisposed();

            var header = (SharedHeader*)_basePtr;
            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();

            while (true)
            {
                // Fast path: check writer first before any atomic operations
                int writerState = Volatile.Read(ref header->WriterLockState);
                if (writerState != 0)
                {
                    if (sw.Elapsed > timeout)
                        return false;
                    spinner.SpinOnce();
                    continue;
                }

                // Try to increment reader count with CAS (avoids separate rollback on failure)
                int currentReaders = Volatile.Read(ref header->ReaderCount);
                if (Interlocked.CompareExchange(ref header->ReaderCount, currentReaders + 1, currentReaders) == currentReaders)
                {
                    // CAS succeeded - verify no writer came in
                    if (Volatile.Read(ref header->WriterLockState) == 0)
                    {
                        // Success: update local counter only after confirmed success
                        Interlocked.Increment(ref _readerCount);
                        return true;
                    }

                    // Writer acquired lock while we were incrementing - rollback shared counter
                    Interlocked.Decrement(ref header->ReaderCount);
                }

                // CAS failed (contention) or writer came in - retry
                if (sw.Elapsed > timeout)
                    return false;

                spinner.SpinOnce();
            }
        }

        /// <inheritdoc/>
        public void ReleaseReadLock()
        {
            ThrowIfDisposed();

            var header = (SharedHeader*)_basePtr;
            Interlocked.Decrement(ref header->ReaderCount);
            Interlocked.Decrement(ref _readerCount);
        }

        /// <inheritdoc/>
        public bool IsWriteLockOrphaned()
        {
            ThrowIfDisposed();

            var header = (SharedHeader*)_basePtr;

            if (Volatile.Read(ref header->WriterLockState) == 0)
                return false;

            int ownerPid = header->LockOwnerProcessId;
            if (ownerPid == 0)
                return false;

            // Check if process is still alive
            try
            {
                using var process = Process.GetProcessById(ownerPid);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                // Process not found - it's dead
                return true;
            }
            catch (InvalidOperationException)
            {
                // Process has exited
                return true;
            }

            // Check timeout-based orphan detection
            if (_options.OrphanLockTimeout > TimeSpan.Zero)
            {
                long acquiredTimestamp = header->LockAcquiredTimestamp;
                if (acquiredTimestamp > 0)
                {
                    long elapsed = Stopwatch.GetTimestamp() - acquiredTimestamp;
                    double elapsedMs = elapsed * 1000.0 / Stopwatch.Frequency;

                    if (elapsedMs > _options.OrphanLockTimeout.TotalMilliseconds)
                    {
                        _logger?.LogWarning("Lock held for {Elapsed}ms exceeds timeout {Timeout}ms",
                            elapsedMs, _options.OrphanLockTimeout.TotalMilliseconds);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <inheritdoc/>
        public bool TryForceReleaseWriteLock()
        {
            ThrowIfDisposed();

            if (!IsWriteLockOrphaned())
                return false;

            var header = (SharedHeader*)_basePtr;

            _logger?.LogWarning("Force releasing orphan lock from process {ProcessId}",
                header->LockOwnerProcessId);

            header->LockOwnerProcessId = 0;
            header->LockOwnerThreadId = 0;
            header->LockAcquiredTimestamp = 0;
            Thread.MemoryBarrier();
            Volatile.Write(ref header->WriterLockState, 0);

            OnOrphanLockDetected?.Invoke(this, new BufferEventArgs
            {
                EventType = BufferEventType.OrphanLockDetected
            });

            return true;
        }

        /// <inheritdoc/>
        public LockOwnerInfo GetLockOwnerInfo()
        {
            ThrowIfDisposed();

            var header = (SharedHeader*)_basePtr;

            return new LockOwnerInfo
            {
                ProcessId = header->LockOwnerProcessId,
                ThreadId = header->LockOwnerThreadId,
                AcquiredTimestamp = header->LockAcquiredTimestamp,
                IsOrphan = IsWriteLockOrphaned()
            };
        }

        /// <inheritdoc/>
        public uint CalculateChecksum(long offset, int length)
        {
            ThrowIfDisposed();
            ValidateOffset(offset, length);

            byte* ptr = GetDataPtr() + offset;
            return ComputeCrc32(ptr, length);
        }

        /// <inheritdoc/>
        public bool VerifyIntegrity()
        {
            ThrowIfDisposed();

            var header = (SharedHeader*)_basePtr;

            if (header->ChecksumLength == 0)
                return true; // No checksum stored

            uint stored = header->DataChecksum;
            uint calculated = CalculateChecksum(header->ChecksumOffset, header->ChecksumLength);

            bool valid = stored == calculated;
            if (!valid)
            {
                _logger?.LogError("Integrity check failed: stored={Stored:X8}, calculated={Calculated:X8}",
                    stored, calculated);
            }

            return valid;
        }

        /// <summary>
        /// Updates the stored checksum for a data region
        /// </summary>
        public void UpdateChecksum(long offset, int length)
        {
            ThrowIfDisposed();
            ValidateOffset(offset, length);

            var header = (SharedHeader*)_basePtr;
            header->ChecksumOffset = offset;
            header->ChecksumLength = length;
            header->DataChecksum = CalculateChecksum(offset, length);
            Thread.MemoryBarrier();
        }

        /// <summary>
        /// Computes CRC32 checksum using hardware acceleration when available
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ComputeCrc32(byte* data, int length)
        {
            // Use .NET's hardware-accelerated CRC32 implementation
            return System.IO.Hashing.Crc32.HashToUInt32(new ReadOnlySpan<byte>(data, length));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(HighPerformanceSharedBuffer));
        }

        /// <summary>
        /// Releases all resources used by this buffer
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _logger?.LogDebug("Disposing shared buffer '{Name}'", _name);
            Cleanup();
            GC.SuppressFinalize(this);
        }

        private void Cleanup()
        {
            try
            {
                if (_basePtr != null && _accessor != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    _basePtr = null;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to release memory pointer during cleanup");
            }

            try
            {
                _accessor?.Dispose();
                _accessor = null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to dispose accessor during cleanup");
            }

            try
            {
                _mmf?.Dispose();
                _mmf = null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to dispose memory-mapped file during cleanup");
            }
        }

        /// <summary>
        /// Releases unmanaged resources if Dispose was not called
        /// </summary>
        ~HighPerformanceSharedBuffer()
        {
            Cleanup();
        }

        private sealed unsafe class UnmanagedMemoryManager<T> : MemoryManager<T> where T : unmanaged
        {
            private readonly T* _pointer;
            private readonly int _length;

            public UnmanagedMemoryManager(T* pointer, int length)
            {
                _pointer = pointer;
                _length = length;
            }

            public override Span<T> GetSpan() => new(_pointer, _length);

            public override MemoryHandle Pin(int elementIndex = 0)
            {
                if (elementIndex < 0 || elementIndex >= _length)
                    throw new ArgumentOutOfRangeException(nameof(elementIndex));

                return new MemoryHandle(_pointer + elementIndex);
            }

            public override void Unpin() { }

            protected override void Dispose(bool disposing) { }
        }
    }
}
