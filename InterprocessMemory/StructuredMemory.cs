using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace InterprocessMemory
{
    /// <summary>
    /// Structured shared memory with compile-time schema enforcement and versioning.
    /// All fields are declared upfront with fixed types, positions, and sizes.
    /// Provides zero-allocation access with full type safety.
    /// Cross-platform via the underlying <see cref="MemoryRegion"/> (Windows + Linux).
    /// </summary>
    public sealed class StructuredMemory<TSchema> : IDisposable where TSchema : struct, IMemorySchema
    {
        private const int SchemaHeaderSize = 64; // Reserved for schema metadata
        private const uint SchemaMagic = 0x53504D49; // "IMPS"
        // x86-64 guarantees atomic load/store for aligned values up to 8 bytes (MOV instruction).
        // Types wider than this threshold require automatic locking to prevent torn reads/writes.
        // Note: ARM64 supports 16-byte atomics (ldp/stp) but .NET on Windows ARM64 uses TSO
        // emulation, so 8 bytes is the safe cross-platform limit for this Windows-only library.
        private const int AtomicThreshold = 8;
        private const int MaxStackAllocBytes = 1024; // Max bytes for stackalloc (prevent stack overflow)

        // Cached TimeSpan to avoid repeated allocations
        private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

        private readonly IMemoryRegion _buffer;
        private readonly TSchema _schema;
        private readonly Dictionary<string, FieldMetadata> _fields;
        private readonly SchemaCompatibility _compatibility;
        private readonly int _schemaHash; // computed once at construction, reused for write and validate
        private volatile int _disposed;

        // Per-instance thread-local lock depth tracking (no boxing, no dictionary lookup)
        private readonly ThreadLocal<int> _writeLockDepth = new(() => 0);
        private readonly ThreadLocal<int> _readLockDepth = new(() => 0);

        /// <summary>
        /// Gets the schema instance defining the memory layout
        /// </summary>
        public TSchema Schema => _schema;

        /// <summary>
        /// Gets whether this instance owns the shared memory
        /// </summary>
        public bool IsOwner => _buffer.IsOwner;

        /// <summary>
        /// Gets the schema version
        /// </summary>
        public int SchemaVersion { get; }

        /// <summary>
        /// Gets the stored schema version (from existing memory)
        /// </summary>
        public int StoredSchemaVersion { get; private set; }

        /// <summary>
        /// Creates or opens structured interprocess memory.
        /// </summary>
        public static StructuredMemory<TSchema> CreateOrOpen(
            string name,
            TSchema schema,
            SchemaCompatibility compatibility = SchemaCompatibility.Strict) =>
            new(name, schema, create: true, compatibility);

        /// <summary>
        /// Opens existing structured memory and validates it against the supplied schema.
        /// </summary>
        public static StructuredMemory<TSchema> OpenExisting(
            string name,
            TSchema schema,
            SchemaCompatibility compatibility = SchemaCompatibility.Strict) =>
            new(name, schema, create: false, compatibility);

        internal StructuredMemory(string name, TSchema schema, bool create = true)
            : this(name, schema, create, SchemaCompatibility.Strict)
        {
        }

        internal StructuredMemory(
            string name,
            TSchema schema,
            bool create,
            SchemaCompatibility compatibility)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));

            _schema = schema;
            _compatibility = compatibility;
            _fields = BuildFieldMetadata(schema);

            if (_fields.Count == 0)
                throw new ArgumentException("Schema must define at least one field", nameof(schema));

            // Get schema version from interface if available
            SchemaVersion = schema is IVersionedSchema versioned ? versioned.Version : 1;

            // Compute hash once; reused in WriteSchemaHeader and ValidateSchemaCompatibility
            _schemaHash = ComputeSchemaHash();

            long totalSize = SchemaHeaderSize + CalculateTotalSize(_fields);

            if (create)
            {
                _buffer = MemoryRegion.CreateOrOpen(
                    name, totalSize, options: null, RegionKind.StructuredMemory);
            }
            else
            {
                _buffer = MemoryRegion.OpenExisting(
                    name, options: null, RegionKind.StructuredMemory);
                if (_buffer.Capacity != totalSize)
                {
                    _buffer.Dispose();
                    throw new InvalidDataException(
                        $"Structured-memory size mismatch: schema requires {totalSize} bytes, " +
                        $"but the region contains {_buffer.Capacity} bytes.");
                }
            }

            try
            {
                if (create && _buffer.IsOwner)
                {
                    InitializeMemory();
                    WriteSchemaHeader();
                    StoredSchemaVersion = SchemaVersion;
                }
                else
                {
                    ValidateSchemaCompatibility();
                }
            }
            catch
            {
                _buffer.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Writes a strictly-typed value to a named field.
        /// For types larger than 8 bytes (non-atomic), automatic locking is applied.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the caller holds only a read lock and attempts to write a non-atomic value
        /// (writing while holding the read lock would corrupt readers and upgrading is unsafe — it would deadlock).
        /// </exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(string fieldName, T value) where T : unmanaged
        {
            ThrowIfDisposed();

            if (!_fields.TryGetValue(fieldName, out var metadata))
                throw new ArgumentException($"Field '{fieldName}' not found in schema", nameof(fieldName));

            EnsureScalarField(metadata);
            ValidateFieldType<T>(metadata);

            bool isNonAtomic = Unsafe.SizeOf<T>() > AtomicThreshold;
            if (isNonAtomic && !IsHoldingWriteLock())
            {
                ThrowIfHoldingReadLock();
                using var _ = AcquireWriteLock();
                WriteInternal(value, metadata);
            }
            else
            {
                WriteInternal(value, metadata);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteInternal<T>(T value, FieldMetadata metadata) where T : unmanaged
        {
            // MemoryMarshal.CreateSpan + AsBytes avoids a stackalloc by reinterpreting
            // the local variable directly as bytes without any extra copy.
            _buffer.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)),
                SchemaHeaderSize + metadata.Offset);
        }

        /// <summary>
        /// Reads a strictly-typed value from a named field.
        /// For types larger than 8 bytes (non-atomic), automatic locking is applied.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Read<T>(string fieldName) where T : unmanaged
        {
            ThrowIfDisposed();

            if (!_fields.TryGetValue(fieldName, out var metadata))
                throw new ArgumentException($"Field '{fieldName}' not found in schema", nameof(fieldName));

            EnsureScalarField(metadata);
            ValidateFieldType<T>(metadata);

            // Auto-lock for non-atomic types (>8 bytes) to prevent torn reads
            bool needsAutoLock = Unsafe.SizeOf<T>() > AtomicThreshold && !IsHoldingAnyLock();
            if (needsAutoLock)
            {
                using var _ = AcquireReadLock();
                return ReadInternal<T>(metadata);
            }
            else
            {
                return ReadInternal<T>(metadata);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private T ReadInternal<T>(FieldMetadata metadata) where T : unmanaged
        {
            T value = default;
            _buffer.Read(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)),
                SchemaHeaderSize + metadata.Offset);
            return value;
        }

        /// <summary>
        /// Writes an array to a fixed-size array field.
        /// For arrays larger than 8 bytes total, automatic locking is applied.
        /// </summary>
        public void WriteArray<T>(string fieldName, ReadOnlySpan<T> values) where T : unmanaged
        {
            ThrowIfDisposed();

            if (!_fields.TryGetValue(fieldName, out var metadata))
                throw new ArgumentException($"Field '{fieldName}' not found in schema", nameof(fieldName));

            if (!metadata.IsArray)
                throw new InvalidOperationException($"Field '{fieldName}' is not an array");

            ValidateFieldType<T>(metadata);

            if (values.Length > metadata.ArrayLength)
                throw new ArgumentException(
                    $"Array length {values.Length} exceeds field capacity {metadata.ArrayLength}",
                    nameof(values));

            var bytes = MemoryMarshal.AsBytes(values);
            bool isNonAtomic = metadata.Size > AtomicThreshold;

            // Auto-lock for non-atomic operations (>8 bytes)
            if (isNonAtomic && !IsHoldingWriteLock())
            {
                ThrowIfHoldingReadLock();
                using var _ = AcquireWriteLock();
                WriteArrayInternal(bytes, metadata);
            }
            else
            {
                WriteArrayInternal(bytes, metadata);
            }
        }

        private void WriteArrayInternal(ReadOnlySpan<byte> bytes, FieldMetadata metadata)
        {
            long baseOffset = SchemaHeaderSize + metadata.Offset;
            if (bytes.Length > 0)
                _buffer.Write(bytes, baseOffset);

            int remaining = metadata.Size - bytes.Length;
            if (remaining > 0)
                ClearBytes(baseOffset + bytes.Length, remaining);
        }

        private void ClearBytes(long offset, int count)
        {
            Span<byte> zeros = stackalloc byte[Math.Min(count, MaxStackAllocBytes)];
            zeros.Clear();

            int remaining = count;
            long currentOffset = offset;
            while (remaining > 0)
            {
                int chunk = Math.Min(zeros.Length, remaining);
                _buffer.Write(zeros.Slice(0, chunk), currentOffset);
                currentOffset += chunk;
                remaining -= chunk;
            }
        }

        /// <summary>
        /// Reads an array from a fixed-size array field.
        /// For arrays larger than 8 bytes total, automatic locking is applied.
        /// </summary>
        public void ReadArray<T>(string fieldName, Span<T> destination) where T : unmanaged
        {
            ThrowIfDisposed();

            if (!_fields.TryGetValue(fieldName, out var metadata))
                throw new ArgumentException($"Field '{fieldName}' not found in schema", nameof(fieldName));

            if (!metadata.IsArray)
                throw new InvalidOperationException($"Field '{fieldName}' is not an array");

            ValidateFieldType<T>(metadata);

            if (destination.Length > metadata.ArrayLength)
                throw new ArgumentException(
                    $"Destination length {destination.Length} exceeds field capacity {metadata.ArrayLength}",
                    nameof(destination));

            var bytes = MemoryMarshal.AsBytes(destination);

            // Auto-lock for non-atomic operations (>8 bytes)
            using var _ = (bytes.Length > AtomicThreshold && !IsHoldingAnyLock())
                ? AcquireReadLock() : default;
            _buffer.Read(bytes, SchemaHeaderSize + metadata.Offset);
        }

        /// <summary>
        /// Writes a string to a fixed-size string field.
        /// Automatic locking is applied for thread safety.
        /// </summary>
        public void WriteString(string fieldName, string value)
        {
            ThrowIfDisposed();

            if (value == null)
                throw new ArgumentNullException(nameof(value));

            if (!_fields.TryGetValue(fieldName, out var metadata))
                throw new ArgumentException($"Field '{fieldName}' not found in schema", nameof(fieldName));

            if (!metadata.IsString)
                throw new InvalidOperationException($"Field '{fieldName}' is not a string");

            if (value.Length >= metadata.ArrayLength)
                throw new ArgumentException(
                    $"String length {value.Length} exceeds field capacity {metadata.ArrayLength - 1} (including null terminator)",
                    nameof(value));

            // String writes are always non-atomic (>8 bytes). Reentrant: skip if already holding write lock.
            // Holding only a read lock here is unsafe — upgrading would deadlock the current reader.
            if (!IsHoldingWriteLock())
            {
                ThrowIfHoldingReadLock();
                using var _ = AcquireWriteLock();
                WriteStringInternal(value, metadata);
            }
            else
            {
                WriteStringInternal(value, metadata);
            }
        }

        private void WriteStringInternal(string value, FieldMetadata metadata)
        {
            int bufferSizeBytes = metadata.ArrayLength * sizeof(char);

            if (bufferSizeBytes <= MaxStackAllocBytes)
            {
                // Fast path: use stackalloc for small buffers
                Span<char> buffer = stackalloc char[metadata.ArrayLength];
                buffer.Clear();
                value.AsSpan().CopyTo(buffer);
                buffer[value.Length] = '\0';

                var bytes = MemoryMarshal.AsBytes(buffer);
                _buffer.Write(bytes, SchemaHeaderSize + metadata.Offset);
            }
            else
            {
                // Slow path: use ArrayPool for large buffers to prevent stack overflow
                char[] rented = ArrayPool<char>.Shared.Rent(metadata.ArrayLength);
                try
                {
                    Span<char> buffer = rented.AsSpan(0, metadata.ArrayLength);
                    buffer.Clear();
                    value.AsSpan().CopyTo(buffer);
                    buffer[value.Length] = '\0';

                    var bytes = MemoryMarshal.AsBytes(buffer);
                    _buffer.Write(bytes, SchemaHeaderSize + metadata.Offset);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(rented);
                }
            }
        }

        /// <summary>
        /// Reads a string from a fixed-size string field.
        /// Automatic locking is applied for thread safety.
        /// </summary>
        public string ReadString(string fieldName)
        {
            ThrowIfDisposed();

            if (!_fields.TryGetValue(fieldName, out var metadata))
                throw new ArgumentException($"Field '{fieldName}' not found in schema", nameof(fieldName));

            if (!metadata.IsString)
                throw new InvalidOperationException($"Field '{fieldName}' is not a string");

            using var _ = IsHoldingAnyLock() ? default : AcquireReadLock();
            return ReadStringInternal(metadata);
        }

        private string ReadStringInternal(FieldMetadata metadata)
        {
            int bufferSizeBytes = metadata.ArrayLength * sizeof(char);

            if (bufferSizeBytes <= MaxStackAllocBytes)
            {
                // Fast path: use stackalloc for small buffers
                Span<char> buffer = stackalloc char[metadata.ArrayLength];
                var bytes = MemoryMarshal.AsBytes(buffer);
                _buffer.Read(bytes, SchemaHeaderSize + metadata.Offset);

                int nullIndex = buffer.IndexOf('\0');
                if (nullIndex < 0)
                    nullIndex = buffer.Length;

                // Empty string optimization - avoid allocation
                return nullIndex == 0 ? string.Empty : new string(buffer.Slice(0, nullIndex));
            }
            else
            {
                // Slow path: use ArrayPool for large buffers to prevent stack overflow
                char[] rented = ArrayPool<char>.Shared.Rent(metadata.ArrayLength);
                try
                {
                    Span<char> buffer = rented.AsSpan(0, metadata.ArrayLength);
                    var bytes = MemoryMarshal.AsBytes(buffer);
                    _buffer.Read(bytes, SchemaHeaderSize + metadata.Offset);

                    int nullIndex = buffer.IndexOf('\0');
                    if (nullIndex < 0)
                        nullIndex = buffer.Length;

                    // Empty string optimization - avoid allocation
                    return nullIndex == 0 ? string.Empty : new string(buffer.Slice(0, nullIndex));
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(rented);
                }
            }
        }

        /// <summary>
        /// Writes binary data to a fixed-size blob field.
        /// Layout: [4-byte int32 length] + [data bytes]. Automatic locking is applied.
        /// </summary>
        /// <param name="fieldName">Name of the blob field</param>
        /// <param name="data">Data to write</param>
        /// <exception cref="ArgumentException">Thrown when field not found or data exceeds capacity</exception>
        /// <exception cref="InvalidOperationException">Thrown when field is not a blob</exception>
        public void WriteBlob(string fieldName, ReadOnlySpan<byte> data)
        {
            ThrowIfDisposed();

            if (!_fields.TryGetValue(fieldName, out var metadata))
                throw new ArgumentException($"Field '{fieldName}' not found in schema", nameof(fieldName));

            if (!metadata.IsBlob)
                throw new InvalidOperationException($"Field '{fieldName}' is not a blob");

            int maxDataSize = metadata.ArrayLength - 4; // subtract length prefix
            if (data.Length > maxDataSize)
                throw new ArgumentException(
                    $"Data length {data.Length} exceeds blob capacity {maxDataSize}",
                    nameof(data));

            // Blob writes are non-atomic. Reentrant: skip if already holding write lock.
            if (!IsHoldingWriteLock())
            {
                ThrowIfHoldingReadLock();
                using var _ = AcquireWriteLock();
                WriteBlobInternal(data, metadata);
            }
            else
            {
                WriteBlobInternal(data, metadata);
            }
        }

        private void WriteBlobInternal(ReadOnlySpan<byte> data, FieldMetadata metadata)
        {
            long baseOffset = SchemaHeaderSize + metadata.Offset;

            // Write 4-byte length prefix
            Span<byte> lengthBuf = stackalloc byte[4];
            BitConverter.TryWriteBytes(lengthBuf, data.Length);
            _buffer.Write(lengthBuf, baseOffset);

            // Write data
            if (data.Length > 0)
                _buffer.Write(data, baseOffset + 4);

            // Zero remaining space to prevent stale data leaking. Use a larger stackalloc
            // (1024 vs the previous 256) so a 4KB tail clears in 4 _buffer.Write calls instead
            // of 16. 1024 matches MaxStackAllocBytes used elsewhere in this class and stays
            // well within typical stack budgets — WriteBlob is not recursive.
            int remaining = metadata.ArrayLength - 4 - data.Length;
            if (remaining > 0)
            {
                Span<byte> zeros = stackalloc byte[Math.Min(remaining, MaxStackAllocBytes)];
                zeros.Clear();
                long offset = baseOffset + 4 + data.Length;
                int left = remaining;
                while (left > 0)
                {
                    int chunk = Math.Min(zeros.Length, left);
                    _buffer.Write(zeros.Slice(0, chunk), offset);
                    offset += chunk;
                    left -= chunk;
                }
            }
        }

        /// <summary>
        /// Reads binary data from a fixed-size blob field.
        /// Returns only the valid portion (up to the stored length).
        /// Automatic locking is applied.
        /// </summary>
        /// <param name="fieldName">Name of the blob field</param>
        /// <returns>A new byte array containing the blob data</returns>
        public byte[] ReadBlob(string fieldName)
        {
            ThrowIfDisposed();

            if (!_fields.TryGetValue(fieldName, out var metadata))
                throw new ArgumentException($"Field '{fieldName}' not found in schema", nameof(fieldName));

            if (!metadata.IsBlob)
                throw new InvalidOperationException($"Field '{fieldName}' is not a blob");

            using var _ = IsHoldingAnyLock() ? default : AcquireReadLock();
            return ReadBlobInternal(metadata);
        }

        private byte[] ReadBlobInternal(FieldMetadata metadata)
        {
            long baseOffset = SchemaHeaderSize + metadata.Offset;

            // Read 4-byte length prefix
            Span<byte> lengthBuf = stackalloc byte[4];
            _buffer.Read(lengthBuf, baseOffset);
            int length = BitConverter.ToInt32(lengthBuf);

            int maxDataSize = metadata.ArrayLength - 4;
            if (length == 0)
                return Array.Empty<byte>();
            if (length < 0 || length > maxDataSize)
                throw new InvalidDataException(
                    $"Stored blob length {length} is outside the valid range 0..{maxDataSize}");

            var result = new byte[length];
            _buffer.Read(result, baseOffset + 4);
            return result;
        }

        /// <summary>
        /// Writes a UTF-8 encoded string to a field.
        /// Layout: [4-byte int32 byte-length] + [UTF-8 bytes]. Automatic locking is applied.
        /// More memory-efficient than WriteString (UTF-16) for ASCII/Latin text.
        /// </summary>
        /// <param name="fieldName">Name of the UTF-8 string field</param>
        /// <param name="value">String to write</param>
        /// <exception cref="ArgumentNullException">Thrown when value is null</exception>
        /// <exception cref="ArgumentException">Thrown when encoded size exceeds capacity</exception>
        /// <exception cref="InvalidOperationException">Thrown when field is not a UTF-8 string</exception>
        public void WriteUtf8String(string fieldName, string value)
        {
            ThrowIfDisposed();

            if (value == null)
                throw new ArgumentNullException(nameof(value));

            if (!_fields.TryGetValue(fieldName, out var metadata))
                throw new ArgumentException($"Field '{fieldName}' not found in schema", nameof(fieldName));

            if (!metadata.IsUtf8String)
                throw new InvalidOperationException($"Field '{fieldName}' is not a UTF-8 string");

            int maxDataSize = metadata.ArrayLength - 4;
            int byteCount = System.Text.Encoding.UTF8.GetByteCount(value);
            if (byteCount > maxDataSize)
                throw new ArgumentException(
                    $"UTF-8 encoded length {byteCount} exceeds field capacity {maxDataSize}",
                    nameof(value));

            // UTF-8 string writes are non-atomic. Reentrant: skip if already holding write lock.
            if (!IsHoldingWriteLock())
            {
                ThrowIfHoldingReadLock();
                using var _ = AcquireWriteLock();
                WriteUtf8StringInternal(value, byteCount, metadata);
            }
            else
            {
                WriteUtf8StringInternal(value, byteCount, metadata);
            }
        }

        private void WriteUtf8StringInternal(string value, int byteCount, FieldMetadata metadata)
        {
            long baseOffset = SchemaHeaderSize + metadata.Offset;

            // Write 4-byte length prefix
            Span<byte> lengthBuf = stackalloc byte[4];
            BitConverter.TryWriteBytes(lengthBuf, byteCount);
            _buffer.Write(lengthBuf, baseOffset);

            if (byteCount > 0)
            {
                int totalSize = metadata.ArrayLength - 4;
                if (totalSize <= MaxStackAllocBytes)
                {
                    Span<byte> utf8Buf = stackalloc byte[totalSize];
                    utf8Buf.Clear();
                    System.Text.Encoding.UTF8.GetBytes(value, utf8Buf);
                    _buffer.Write(utf8Buf, baseOffset + 4);
                }
                else
                {
                    byte[] rented = ArrayPool<byte>.Shared.Rent(totalSize);
                    try
                    {
                        var span = rented.AsSpan(0, totalSize);
                        span.Clear();
                        System.Text.Encoding.UTF8.GetBytes(value, span);
                        _buffer.Write(span, baseOffset + 4);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }
        }

        /// <summary>
        /// Reads a UTF-8 encoded string from a field.
        /// Returns the decoded string based on the stored byte-length prefix.
        /// Automatic locking is applied.
        /// </summary>
        /// <param name="fieldName">Name of the UTF-8 string field</param>
        /// <returns>The decoded string</returns>
        public string ReadUtf8String(string fieldName)
        {
            ThrowIfDisposed();

            if (!_fields.TryGetValue(fieldName, out var metadata))
                throw new ArgumentException($"Field '{fieldName}' not found in schema", nameof(fieldName));

            if (!metadata.IsUtf8String)
                throw new InvalidOperationException($"Field '{fieldName}' is not a UTF-8 string");

            using var _ = IsHoldingAnyLock() ? default : AcquireReadLock();
            return ReadUtf8StringInternal(metadata);
        }

        private string ReadUtf8StringInternal(FieldMetadata metadata)
        {
            long baseOffset = SchemaHeaderSize + metadata.Offset;

            // Read 4-byte length prefix
            Span<byte> lengthBuf = stackalloc byte[4];
            _buffer.Read(lengthBuf, baseOffset);
            int byteLength = BitConverter.ToInt32(lengthBuf);

            int maxDataSize = metadata.ArrayLength - 4;
            if (byteLength == 0)
                return string.Empty;
            if (byteLength < 0 || byteLength > maxDataSize)
                throw new InvalidDataException(
                    $"Stored UTF-8 string length {byteLength} is outside the valid range 0..{maxDataSize}");

            if (byteLength <= MaxStackAllocBytes)
            {
                Span<byte> utf8Buf = stackalloc byte[byteLength];
                _buffer.Read(utf8Buf, baseOffset + 4);
                return System.Text.Encoding.UTF8.GetString(utf8Buf);
            }
            else
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(byteLength);
                try
                {
                    var span = rented.AsSpan(0, byteLength);
                    _buffer.Read(span, baseOffset + 4);
                    return System.Text.Encoding.UTF8.GetString(span);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        /// <summary>
        /// Acquires an exclusive write lock on the entire memory region.
        /// This lock is reentrant: if the current thread already holds a write lock,
        /// the depth counter is incremented without acquiring the underlying lock again.
        /// </summary>
        /// <returns>A disposable lock guard that releases the lock on dispose</returns>
        /// <exception cref="TimeoutException">Thrown when the lock cannot be acquired within the timeout</exception>
        public WriteLock AcquireWriteLock()
        {
            return AcquireWriteLock(DefaultLockTimeout);
        }

        /// <summary>
        /// Acquires an exclusive write lock on the entire memory region.
        /// This lock is reentrant: if the current thread already holds a write lock,
        /// the depth counter is incremented without acquiring the underlying lock again.
        /// </summary>
        /// <param name="timeout">Lock acquisition timeout</param>
        /// <returns>A disposable lock guard that releases the lock on dispose</returns>
        /// <exception cref="TimeoutException">Thrown when the lock cannot be acquired within the timeout</exception>
        public WriteLock AcquireWriteLock(TimeSpan timeout)
        {
            ThrowIfDisposed();
            TimeoutHelper.Validate(timeout, nameof(timeout));

            // Reentrant: if already holding write lock, just increment depth
            if (_writeLockDepth.Value > 0)
            {
                IncrementWriteLockDepth();
                return new WriteLock(null, DecrementWriteLockDepth);
            }

            if (!_buffer.TryAcquireWriteLock(timeout))
                throw new TimeoutException($"Failed to acquire write lock within {timeout}");

            IncrementWriteLockDepth();
            return new WriteLock(_buffer, DecrementWriteLockDepth);
        }

        /// <summary>
        /// Acquires a shared read lock on the entire memory region.
        /// This lock is reentrant: if the current thread already holds any lock (read or write),
        /// the depth counter is incremented without acquiring the underlying lock again.
        /// </summary>
        /// <returns>A disposable lock guard that releases the lock on dispose</returns>
        /// <exception cref="TimeoutException">Thrown when the lock cannot be acquired within the timeout</exception>
        public ReadLock AcquireReadLock()
        {
            return AcquireReadLock(DefaultLockTimeout);
        }

        /// <summary>
        /// Acquires a shared read lock on the entire memory region.
        /// This lock is reentrant: if the current thread already holds any lock (read or write),
        /// the depth counter is incremented without acquiring the underlying lock again.
        /// </summary>
        /// <param name="timeout">Lock acquisition timeout</param>
        /// <returns>A disposable lock guard that releases the lock on dispose</returns>
        /// <exception cref="TimeoutException">Thrown when the lock cannot be acquired within the timeout</exception>
        public ReadLock AcquireReadLock(TimeSpan timeout)
        {
            ThrowIfDisposed();
            TimeoutHelper.Validate(timeout, nameof(timeout));

            // Reentrant: if already holding any lock, just increment depth
            if (_readLockDepth.Value > 0 || _writeLockDepth.Value > 0)
            {
                IncrementReadLockDepth();
                return new ReadLock(null, DecrementReadLockDepth);
            }

            if (!_buffer.TryAcquireReadLock(timeout))
                throw new TimeoutException($"Failed to acquire read lock within {timeout}");

            IncrementReadLockDepth();
            return new ReadLock(_buffer, DecrementReadLockDepth);
        }

        /// <summary>
        /// Checks if a field exists in the schema
        /// </summary>
        public bool HasField(string fieldName)
        {
            return _fields.ContainsKey(fieldName);
        }

        /// <summary>
        /// Gets all field names in the schema
        /// </summary>
        public IEnumerable<string> GetFieldNames()
        {
            return _fields.Keys;
        }

        private void WriteSchemaHeader()
        {
            Span<byte> header = stackalloc byte[SchemaHeaderSize];
            header.Clear();

            // Write version
            BitConverter.TryWriteBytes(header.Slice(4), SchemaVersion);
            // Write field count
            BitConverter.TryWriteBytes(header.Slice(8), _fields.Count);
            // Write schema hash for quick compatibility check
            BitConverter.TryWriteBytes(header.Slice(12), _schemaHash);

            _buffer.Write(header, 0);
            BitConverter.TryWriteBytes(header, SchemaMagic);
            _buffer.Write(header.Slice(0, sizeof(uint)), 0);
        }

        private void ValidateSchemaCompatibility()
        {
            Span<byte> header = stackalloc byte[SchemaHeaderSize];
            var sw = Stopwatch.StartNew();
            while (true)
            {
                _buffer.Read(header, 0);
                if (BitConverter.ToUInt32(header) == SchemaMagic)
                    break;
                if (sw.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidDataException(
                        "Timed out waiting for the version 3 structured-memory schema header.");
                Thread.SpinWait(100);
            }

            StoredSchemaVersion = BitConverter.ToInt32(header.Slice(4));
            int storedFieldCount = BitConverter.ToInt32(header.Slice(8));
            int storedHash = BitConverter.ToInt32(header.Slice(12));

            if (StoredSchemaVersion == SchemaVersion && storedFieldCount != _fields.Count)
            {
                throw new InvalidOperationException(
                    $"Schema field count mismatch: expected {_fields.Count}, found {storedFieldCount}");
            }

            // Check version compatibility
            if (StoredSchemaVersion != SchemaVersion)
            {
                bool compatible = _compatibility switch
                {
                    SchemaCompatibility.Strict => false,
                    SchemaCompatibility.Forward => StoredSchemaVersion > SchemaVersion,
                    SchemaCompatibility.Backward => StoredSchemaVersion < SchemaVersion,
                    SchemaCompatibility.Full => true,
                    _ => false
                };

                if (!compatible)
                {
                    throw new InvalidOperationException(
                        $"Schema version mismatch: expected {SchemaVersion}, found {StoredSchemaVersion}. " +
                        $"Compatibility mode: {_compatibility}");
                }

                // Schema-side veto: if the schema itself can declare incompatibility for this
                // specific pair (e.g., v3 schema knows it cannot safely read v1 even under Full mode),
                // honor that. Previously IVersionedSchema.IsCompatibleWith was never invoked, leaving
                // the interface contract unfulfilled — schemas could lie about compatibility and the
                // library would still accept the open.
                if (_schema is IVersionedSchema versioned && !versioned.IsCompatibleWith(StoredSchemaVersion))
                {
                    throw new InvalidOperationException(
                        $"Schema rejected stored version {StoredSchemaVersion} via IsCompatibleWith " +
                        $"(current schema version: {SchemaVersion})");
                }
            }

            // Verify schema hash if versions match
            if (StoredSchemaVersion == SchemaVersion && storedHash != _schemaHash)
            {
                throw new InvalidOperationException(
                    "Schema hash mismatch - the schema structure has changed");
            }
        }

        private int ComputeSchemaHash()
        {
            var fieldValues = new List<FieldMetadata>(_fields.Values);
            fieldValues.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            unchecked
            {
                int hash = 17;
                hash = hash * 31 + fieldValues.Count;
                foreach (var field in fieldValues)
                {
                    hash = hash * 31 + StableStringHash(field.Name);
                    hash = hash * 31 + (int)field.TypeCode;
                    hash = hash * 31 + field.ElementSize;
                    hash = hash * 31 + field.Size;
                    hash = hash * 31 + field.ArrayLength;
                    hash = hash * 31 + field.Alignment;
                    hash = hash * 31 + field.Offset.GetHashCode();
                }
                return hash;
            }
        }

        /// <summary>
        /// Computes a stable hash code for a string that is consistent across processes
        /// </summary>
        private static int StableStringHash(string str)
        {
            unchecked
            {
                int hash = 5381;
                foreach (char c in str)
                {
                    hash = ((hash << 5) + hash) ^ c;
                }
                return hash;
            }
        }

        private Dictionary<string, FieldMetadata> BuildFieldMetadata(TSchema schema)
        {
            var fields = schema.GetFields();
            var metadata = new Dictionary<string, FieldMetadata>(StringComparer.Ordinal);

            long currentOffset = 0;

            foreach (var field in fields)
            {
                int fieldSize = ValidateFieldDefinition(field);

                if (metadata.ContainsKey(field.Name))
                    throw new ArgumentException($"Duplicate field name: {field.Name}");

                long alignment = field.Alignment;
                currentOffset = AlignUp(currentOffset, alignment);

                var meta = new FieldMetadata
                {
                    Name = field.Name,
                    Offset = currentOffset,
                    Size = fieldSize,
                    ElementSize = field.ElementSize,
                    ArrayLength = field.ArrayLength,
                    Alignment = field.Alignment,
                    TypeCode = field.TypeCode,
                    IsArray = field.ArrayLength > 1
                        && field.TypeCode != FieldTypeCode.Blob
                        && field.TypeCode != FieldTypeCode.Utf8String,
                    IsString = field.TypeCode == FieldTypeCode.Char && field.ArrayLength > 1,
                    IsBlob = field.TypeCode == FieldTypeCode.Blob,
                    IsUtf8String = field.TypeCode == FieldTypeCode.Utf8String
                };

                metadata[field.Name] = meta;
                currentOffset = checked(currentOffset + fieldSize);
            }

            return metadata;
        }

        private static int ValidateFieldDefinition(FieldDefinition field)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
                throw new ArgumentException("Field name cannot be empty");

            if (field.TypeCode == FieldTypeCode.Unknown || !Enum.IsDefined(typeof(FieldTypeCode), field.TypeCode))
                throw new ArgumentException($"Field '{field.Name}' has an invalid type code: {field.TypeCode}");

            if (field.ElementSize <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(field.ElementSize),
                    $"Field '{field.Name}' element size must be positive");

            if (field.ArrayLength <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(field.ArrayLength),
                    $"Field '{field.Name}' array length must be positive");

            if (field.Alignment <= 0 || (field.Alignment & (field.Alignment - 1)) != 0)
                throw new ArgumentException(
                    $"Field '{field.Name}' alignment must be a positive power of 2",
                    nameof(field.Alignment));

            long size = (long)field.ElementSize * field.ArrayLength;
            if (size > int.MaxValue)
                throw new ArgumentOutOfRangeException(
                    nameof(field.ArrayLength),
                    $"Field '{field.Name}' size {size} exceeds Int32.MaxValue");

            return (int)size;
        }

        private static long AlignUp(long value, long alignment)
        {
            checked
            {
                return (value + alignment - 1) & ~(alignment - 1);
            }
        }

        private long CalculateTotalSize(Dictionary<string, FieldMetadata> fields)
        {
            if (fields.Count == 0)
                return 64;

            long maxEnd = 0;
            foreach (var field in fields.Values)
            {
                long end = field.Offset + field.Size;
                if (end > maxEnd)
                    maxEnd = end;
            }
            return (maxEnd + 63) & ~63L;
        }

        private void InitializeMemory()
        {
            // Allocate only as much stack space as needed; avoids wasting 4096 bytes
            // when the buffer is smaller (e.g. a schema with a single small field).
            int chunkSize = (int)Math.Min(4096, _buffer.Capacity);
            Span<byte> zeros = stackalloc byte[chunkSize];
            zeros.Clear();

            long offset = 0;
            long remaining = _buffer.Capacity;

            while (remaining > 0)
            {
                int write = (int)Math.Min(zeros.Length, remaining);
                _buffer.Write(zeros.Slice(0, write), offset);
                offset += write;
                remaining -= write;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ValidateFieldType<T>(FieldMetadata metadata) where T : unmanaged
        {
            int actualSize = Unsafe.SizeOf<T>();
            if (actualSize != metadata.ElementSize)
            {
                throw new InvalidOperationException(
                    $"Type size mismatch for field '{metadata.Name}': " +
                    $"expected {metadata.ElementSize} bytes, got {actualSize} bytes");
            }

            // Validate TypeCode to prevent mismatched types of same size
            var actualTypeCode = FieldDefinition.GetTypeCode<T>();
            if (actualTypeCode != metadata.TypeCode)
            {
                // Allow Struct TypeCode for any unmanaged struct (can't validate specific struct type)
                if (metadata.TypeCode == FieldTypeCode.Struct && actualTypeCode == FieldTypeCode.Struct)
                    return;

                throw new InvalidOperationException(
                    $"Type mismatch for field '{metadata.Name}': " +
                    $"expected {metadata.TypeCode}, got {actualTypeCode}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(StructuredMemory<TSchema>));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetWriteLockDepth() => _writeLockDepth.Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void IncrementWriteLockDepth() => _writeLockDepth.Value++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecrementWriteLockDepth() => _writeLockDepth.Value--;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetReadLockDepth() => _readLockDepth.Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void IncrementReadLockDepth() => _readLockDepth.Value++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecrementReadLockDepth() => _readLockDepth.Value--;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsHoldingAnyLock() => _writeLockDepth.Value > 0 || _readLockDepth.Value > 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsHoldingWriteLock() => _writeLockDepth.Value > 0;

        /// <summary>
        /// Throws when the current thread holds a read lock but no write lock.
        /// Used to prevent a thread that is one of the active readers from attempting a write —
        /// auto-acquiring a write lock from such a thread would deadlock (writer waits for ReaderCount=0,
        /// but this very thread is one of the readers and cannot release until the call returns).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfHoldingReadLock()
        {
            if (_readLockDepth.Value > 0 && _writeLockDepth.Value == 0)
            {
                throw new InvalidOperationException(
                    "Cannot write while holding only a read lock. Release the read lock first, " +
                    "or acquire a write lock before performing reads and writes together.");
            }
        }

        /// <summary>
        /// Releases all resources used by this shared memory region
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _buffer?.Dispose();
            _writeLockDepth.Dispose();
            _readLockDepth.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged resources if Dispose was not called
        /// </summary>
        ~StructuredMemory()
        {
            Interlocked.Exchange(ref _disposed, 1);
            // MemoryRegion owns its unmanaged finalizer path. Avoid invoking
            // managed Dispose logic from this finalizer.
        }

        private static void EnsureScalarField(FieldMetadata metadata)
        {
            if (metadata.IsArray || metadata.IsString || metadata.IsBlob || metadata.IsUtf8String)
            {
                throw new InvalidOperationException(
                    $"Field '{metadata.Name}' is not a scalar field. Use the matching array, string, blob, or UTF-8 API.");
            }
        }

        /// <summary>
        /// RAII wrapper for write lock with double-dispose and reentrant safety.
        /// When acquired via reentrant path, buffer is null and Dispose only decrements the depth counter.
        ///
        /// <para><b>Do not copy this struct.</b> The double-dispose guard (an
        /// <c>Interlocked.Exchange</c> on <c>_onDispose</c>) operates on the struct's own field,
        /// not a shared one. Copying the struct duplicates the field, and each copy's
        /// <c>Dispose()</c> will independently decrement the lock depth — corrupting the
        /// reentrant depth counter and potentially releasing the underlying buffer lock twice.
        /// Always consume via <c>using var ... = AcquireWriteLock(...)</c>, never <c>var copy = lock;</c>.</para>
        /// </summary>
        public struct WriteLock : IDisposable
        {
            private IMemoryRegion? _buffer;
            private Action? _onDispose;

            internal WriteLock(IMemoryRegion? buffer, Action? onDispose = null)
            {
                _buffer = buffer;
                _onDispose = onDispose;
            }

            /// <summary>
            /// Releases the write lock if not already released
            /// </summary>
            public void Dispose()
            {
                var onDispose = Interlocked.Exchange(ref _onDispose, null);
                if (onDispose != null)
                {
                    _buffer?.ReleaseWriteLock();
                    _buffer = null;
                    onDispose.Invoke();
                }
            }
        }

        /// <summary>
        /// RAII wrapper for read lock with double-dispose and reentrant safety.
        /// When acquired via reentrant path, buffer is null and Dispose only decrements the depth counter.
        ///
        /// <para><b>Do not copy this struct.</b> Same reasoning as <see cref="WriteLock"/> —
        /// copies will each run <c>Dispose()</c>, double-decrementing the reentrant depth and
        /// potentially releasing the underlying buffer lock twice.</para>
        /// </summary>
        public struct ReadLock : IDisposable
        {
            private IMemoryRegion? _buffer;
            private Action? _onDispose;

            internal ReadLock(IMemoryRegion? buffer, Action? onDispose = null)
            {
                _buffer = buffer;
                _onDispose = onDispose;
            }

            /// <summary>
            /// Releases the read lock if not already released
            /// </summary>
            public void Dispose()
            {
                var onDispose = Interlocked.Exchange(ref _onDispose, null);
                if (onDispose != null)
                {
                    _buffer?.ReleaseReadLock();
                    _buffer = null;
                    onDispose.Invoke();
                }
            }
        }

        private sealed class FieldMetadata
        {
            public string Name { get; set; } = string.Empty;
            public long Offset { get; set; }
            public int Size { get; set; }
            public int ElementSize { get; set; }
            public int ArrayLength { get; set; }
            public int Alignment { get; set; }
            public FieldTypeCode TypeCode { get; set; }
            public bool IsArray { get; set; }
            public bool IsString { get; set; }
            public bool IsBlob { get; set; }
            public bool IsUtf8String { get; set; }
        }
    }

}
