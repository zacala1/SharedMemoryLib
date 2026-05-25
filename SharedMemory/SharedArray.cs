using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace SharedMemory
{
    /// <summary>
    /// High-performance generic shared array with type safety and zero-allocation indexer.
    /// Provides array-like access to shared memory with compile-time type checking.
    /// Cross-platform: backed by <see cref="HighPerformanceSharedBuffer"/> which supports Windows and Linux.
    /// </summary>
    /// <typeparam name="T">Unmanaged value type</typeparam>
    public sealed class SharedArray<T> : IDisposable where T : unmanaged
    {
        private readonly ISharedMemoryBuffer _buffer;
        private readonly int _length;
        private readonly int _elementSize;
        private volatile int _disposed;

        /// <summary>
        /// Gets the number of elements in the array
        /// </summary>
        public int Length => _length;

        /// <summary>
        /// Creates or opens a shared memory array with the specified name and length.
        /// </summary>
        /// <param name="name">Unique name for the shared memory region</param>
        /// <param name="length">Number of elements in the array</param>
        /// <param name="create">True to create new, false to open existing</param>
        /// <exception cref="ArgumentException">Thrown when name is empty or whitespace</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when length is not positive</exception>
        public SharedArray(string name, int length, bool create = true)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));
            if (length <= 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            _length = length;
            _elementSize = Unsafe.SizeOf<T>();

            long capacity = (long)_length * _elementSize;

            var options = new SharedMemoryBufferOptions
            {
                Capacity = capacity,
                CreateOrOpen = create,
                EnableSimd = true
            };

            _buffer = new HighPerformanceSharedBuffer(name, options);
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
                _buffer.Read(buffer, (long)index * _elementSize);
                return MemoryMarshal.Read<T>(buffer);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                ThrowIfDisposed();
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException();

                ReadOnlySpan<byte> buffer = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
                _buffer.Write(buffer, (long)index * _elementSize);
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
            _buffer.Read(byteSpan, (long)startIndex * _elementSize);
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
            _buffer.Write(byteSpan, (long)startIndex * _elementSize);
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
        /// the inner <see cref="HighPerformanceSharedBuffer"/> — that has its own finalizer and
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
