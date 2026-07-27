using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace InterprocessMemory
{
    /// <summary>
    /// High-performance generic shared array with type safety and zero-allocation indexer.
    /// Provides array-like access to shared memory with compile-time type checking.
    /// Cross-platform: backed by <see cref="MemoryRegion"/> which supports Windows and Linux.
    /// </summary>
    /// <typeparam name="T">Unmanaged value type</typeparam>
    public sealed class SharedArray<T> : IDisposable where T : unmanaged
    {
        private readonly IMemoryRegion _buffer;
        private const int ArrayHeaderSize = 64;
        private const uint ArrayMagic = 0x59415249; // "IRAY"
        private const int FormatVersion = 3;

        private int _length;
        private readonly int _elementSize;
        private readonly TypeLayoutFingerprint _fingerprint;
        private volatile int _disposed;

        /// <summary>
        /// Gets the number of elements in the array
        /// </summary>
        public int Length => _length;

        /// <summary>
        /// Creates or opens a shared array.
        /// </summary>
        public static SharedArray<T> CreateOrOpen(string name, int length) =>
            new(name, length, createOrOpen: true);

        /// <summary>Opens an existing shared array and loads its length from shared metadata.</summary>
        public static SharedArray<T> OpenExisting(string name) =>
            new(name, length: null, createOrOpen: false);

        internal SharedArray(string name, int length, bool create = true)
            : this(name, create ? length : null, create)
        {
        }

        private SharedArray(string name, int? length, bool createOrOpen)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));
            if (length is <= 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            _elementSize = Unsafe.SizeOf<T>();
            _fingerprint = TypeLayoutFingerprint.Create<T>();

            if (createOrOpen)
            {
                _length = length!.Value;
                long dataSize = checked((long)_length * _elementSize);
                _buffer = MemoryRegion.CreateOrOpen(
                    name,
                    checked(ArrayHeaderSize + dataSize),
                    options: null,
                    RegionKind.SharedArray);

                if (_buffer.IsOwner)
                    InitializeHeader();
                else
                    ValidateAndLoadHeader(expectedLength: _length);
            }
            else
            {
                _buffer = MemoryRegion.OpenExisting(
                    name,
                    options: null,
                    RegionKind.SharedArray);
                ValidateAndLoadHeader(expectedLength: null);
            }
        }

        private void InitializeHeader()
        {
            Span<byte> header = stackalloc byte[ArrayHeaderSize];
            header.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4), FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8), _length);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12), _elementSize);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(16), _fingerprint.Low);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(24), _fingerprint.High);
            _buffer.Write(header, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header, ArrayMagic);
            _buffer.Write(header.Slice(0, sizeof(uint)), 0);
        }

        private void ValidateAndLoadHeader(int? expectedLength)
        {
            Span<byte> header = stackalloc byte[ArrayHeaderSize];
            var sw = Stopwatch.StartNew();
            while (true)
            {
                _buffer.Read(header, 0);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) == ArrayMagic)
                    break;
                if (sw.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidDataException("Timed out waiting for the shared-array header.");
                Thread.SpinWait(100);
            }

            int version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4));
            int storedLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8));
            int storedElementSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12));
            var storedFingerprint = new TypeLayoutFingerprint(
                BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(16)),
                BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(24)));

            if (version != FormatVersion || storedLength <= 0 ||
                storedElementSize != _elementSize ||
                storedFingerprint != _fingerprint)
                throw new InvalidDataException(
                    "The existing shared array has a different format or element type.");
            if (expectedLength.HasValue && expectedLength.Value != storedLength)
                throw new InvalidOperationException(
                    $"Length mismatch: expected {expectedLength.Value}, found {storedLength}.");

            long expectedCapacity = checked(ArrayHeaderSize + (long)storedLength * storedElementSize);
            if (_buffer.Capacity != expectedCapacity)
                throw new InvalidDataException("The shared-array capacity does not match its header.");

            _length = storedLength;
        }

        /// <summary>
        /// Gets or sets the element at the specified index.
        /// Zero-allocation accessor using direct memory access.
        /// </summary>
        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ThrowIfDisposed();
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException();

                Span<byte> buffer = stackalloc byte[_elementSize];
                _buffer.Read(buffer, ArrayHeaderSize + (long)index * _elementSize);
                return MemoryMarshal.Read<T>(buffer);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                ThrowIfDisposed();
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException();

                ReadOnlySpan<byte> buffer = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
                _buffer.Write(buffer, ArrayHeaderSize + (long)index * _elementSize);
            }
        }

        /// <summary>
        /// Copies a range of elements to a span.
        /// High-performance batch operation with SIMD optimization.
        /// </summary>
        /// <param name="startIndex">Starting index in the array</param>
        /// <param name="destination">Destination span to copy elements to</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when range exceeds array bounds</exception>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void CopyTo(int startIndex, Span<T> destination)
        {
            ThrowIfDisposed();
            // Use long arithmetic for the bound check: (uint)+(uint) wraps mod 2^32, so a
            // hostile/buggy startIndex≈2 000 000 000 with a comparable length would silently
            // pass the test and then read past the array. Span.Length is non-negative by
            // contract, but startIndex isn't, so we explicitly reject negatives first.
            if (startIndex < 0 || (long)startIndex + destination.Length > _length)
                throw new ArgumentOutOfRangeException(nameof(startIndex));

            var byteSpan = MemoryMarshal.AsBytes(destination);
            _buffer.Read(byteSpan, ArrayHeaderSize + (long)startIndex * _elementSize);
        }

        /// <summary>
        /// Copies a span of elements to the array.
        /// High-performance batch operation with SIMD optimization.
        /// </summary>
        /// <param name="startIndex">Starting index in the array</param>
        /// <param name="source">Source span to copy elements from</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when range exceeds array bounds</exception>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void CopyFrom(int startIndex, ReadOnlySpan<T> source)
        {
            ThrowIfDisposed();
            // Long arithmetic — see CopyTo for the same overflow rationale.
            if (startIndex < 0 || (long)startIndex + source.Length > _length)
                throw new ArgumentOutOfRangeException(nameof(startIndex));

            var byteSpan = MemoryMarshal.AsBytes(source);
            _buffer.Write(byteSpan, ArrayHeaderSize + (long)startIndex * _elementSize);
        }

        /// <summary>
        /// Fills a range with a value.
        /// Optimized for large ranges using vectorization.
        /// </summary>
        /// <param name="value">Value to fill with</param>
        /// <param name="startIndex">Starting index (default: 0)</param>
        /// <param name="count">Number of elements to fill (-1 for remaining elements)</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when range exceeds array bounds</exception>
        public void Fill(T value, int startIndex = 0, int count = -1)
        {
            ThrowIfDisposed();
            if (count == -1)
                count = _length - startIndex;

            // Long arithmetic — see CopyTo. Plus explicit negative-count rejection because
            // Fill(value, 0, -2) would compute -2 in long and pass the upper-bound check.
            if (startIndex < 0 || count < 0 || (long)startIndex + count > _length)
                throw new ArgumentOutOfRangeException();

            // Batch fill: create a filled buffer and write in chunks
            int batchCount = Math.Min(count, 4096);
            int batchBytes = batchCount * _elementSize;

            // Use stackalloc for small batches, ArrayPool for large.
            // ArrayPool (vs GC.AllocateUninitializedArray) eliminates GC pressure when Fill is
            // called repeatedly — common in init/reset patterns for shared arrays — and the
            // rented buffer is short-lived, exactly the workload ArrayPool is tuned for.
            if (batchBytes <= 1024)
            {
                Span<T> temp = stackalloc T[batchCount];
                temp.Fill(value);
                FillBatched(startIndex, count, temp);
            }
            else
            {
                T[] rented = ArrayPool<T>.Shared.Rent(batchCount);
                try
                {
                    var span = rented.AsSpan(0, batchCount);
                    span.Fill(value);
                    FillBatched(startIndex, count, span);
                }
                finally
                {
                    ArrayPool<T>.Shared.Return(rented);
                }
            }
        }

        /// <summary>
        /// Clears the entire array to default(T)
        /// </summary>
        public void Clear()
        {
            ThrowIfDisposed();
            Fill(default, 0, _length);
        }

        private void FillBatched(int startIndex, int count, Span<T> batch)
        {
            int offset = 0;
            while (offset < count)
            {
                int batchSize = Math.Min(batch.Length, count - offset);
                CopyFrom(startIndex + offset, batch.Slice(0, batchSize));
                offset += batchSize;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(SharedArray<T>));
        }

        /// <summary>
        /// Releases all resources used by this array
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _buffer?.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged resources if Dispose was not called. Does NOT proactively dispose
        /// the inner <see cref="MemoryRegion"/> — that has its own finalizer and
        /// touching it from here risks running against an already-finalized peer (finalizer
        /// order is undefined). The peer's finalizer reclaims its unmanaged handles directly.
        /// </summary>
        ~SharedArray()
        {
            // Just mark disposed so a racing manual Dispose is a no-op. No managed work here.
            Interlocked.Exchange(ref _disposed, 1);
        }
    }
}
