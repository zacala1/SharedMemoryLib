using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace SharedMemory
{
    /// <summary>
    /// High-performance generic shared array with type safety and zero-allocation indexer.
    /// Provides array-like access to shared memory with compile-time type checking.
    /// </summary>
    /// <typeparam name="T">Unmanaged value type</typeparam>
    [SupportedOSPlatform("windows")]
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
            if ((uint)startIndex + (uint)destination.Length > (uint)_length)
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
            if ((uint)startIndex + (uint)source.Length > (uint)_length)
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

            if ((uint)startIndex + (uint)count > (uint)_length)
                throw new ArgumentOutOfRangeException();

            // For small counts, use simple loop
            if (count < 64)
            {
                for (int i = 0; i < count; i++)
                {
                    this[startIndex + i] = value;
                }
                return;
            }

            // For large counts, use batch copy
            T[] temp = GC.AllocateUninitializedArray<T>(Math.Min(count, 4096));
            temp.AsSpan().Fill(value);

            int offset = 0;
            while (offset < count)
            {
                int batchSize = Math.Min(temp.Length, count - offset);
                CopyFrom(startIndex + offset, temp.AsSpan(0, batchSize));
                offset += batchSize;
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
        /// Releases unmanaged resources if Dispose was not called
        /// </summary>
        ~SharedArray()
        {
            _buffer?.Dispose();
        }
    }
}
