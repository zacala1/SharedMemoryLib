using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace SharedMemory
{
    /// <summary>
    /// Type code enumeration for shared memory field types
    /// </summary>
    public enum SharedTypeCode
    {
        /// <summary>Unknown or unsupported type</summary>
        Unknown = 0,
        /// <summary>Boolean type (1 byte)</summary>
        Boolean,
        /// <summary>Unsigned byte (1 byte)</summary>
        Byte,
        /// <summary>Signed byte (1 byte)</summary>
        SByte,
        /// <summary>Unicode character (2 bytes)</summary>
        Char,
        /// <summary>16-bit signed integer (2 bytes)</summary>
        Int16,
        /// <summary>16-bit unsigned integer (2 bytes)</summary>
        UInt16,
        /// <summary>32-bit signed integer (4 bytes)</summary>
        Int32,
        /// <summary>32-bit unsigned integer (4 bytes)</summary>
        UInt32,
        /// <summary>64-bit signed integer (8 bytes)</summary>
        Int64,
        /// <summary>64-bit unsigned integer (8 bytes)</summary>
        UInt64,
        /// <summary>Single-precision floating point (4 bytes)</summary>
        Single,
        /// <summary>Double-precision floating point (8 bytes)</summary>
        Double,
        /// <summary>Decimal type (16 bytes)</summary>
        Decimal,
        /// <summary>GUID type (16 bytes)</summary>
        Guid,
        /// <summary>DateTime type (8 bytes)</summary>
        DateTime,
        /// <summary>TimeSpan type (8 bytes)</summary>
        TimeSpan,
        /// <summary>DateTimeOffset type (16 bytes)</summary>
        DateTimeOffset,
        /// <summary>Custom unmanaged struct</summary>
        Struct
    }

    /// <summary>
    /// Schema compatibility mode for version handling
    /// </summary>
    public enum SchemaCompatibility
    {
        /// <summary>Exact version match required</summary>
        Strict,
        /// <summary>Allow reading from newer compatible versions</summary>
        Forward,
        /// <summary>Allow reading from older compatible versions</summary>
        Backward,
        /// <summary>Allow both forward and backward compatibility</summary>
        Full
    }

    /// <summary>
    /// Strictly-typed shared memory with compile-time schema enforcement and versioning.
    /// All fields are declared upfront with fixed types, positions, and sizes.
    /// Provides zero-allocation access with full type safety.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class StrictSharedMemory<TSchema> : IDisposable where TSchema : struct, ISharedMemorySchema
    {
        private const int SchemaHeaderSize = 64; // Reserved for schema metadata
        private const uint SchemaMagic = 0x53534D53; // "SSMS"
        private const int AtomicThreshold = 8; // Types larger than 8 bytes need automatic locking
        private const int MaxStackAllocBytes = 1024; // Max bytes for stackalloc (prevent stack overflow)

        // Cached TimeSpan to avoid repeated allocations
        private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

        private readonly ISharedMemoryBuffer _buffer;
        private readonly TSchema _schema;
        private readonly Dictionary<string, FieldMetadata> _fields;
        private readonly SchemaCompatibility _compatibility;
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
        /// Creates or opens a strictly-typed shared memory region.
        /// Schema is validated at construction time for consistency.
        /// </summary>
        public StrictSharedMemory(string name, TSchema schema, bool create = true)
            : this(name, schema, create, SchemaCompatibility.Strict)
        {
        }

        /// <summary>
        /// Creates or opens a strictly-typed shared memory region with version compatibility options.
        /// </summary>
        public StrictSharedMemory(string name, TSchema schema, bool create, SchemaCompatibility compatibility)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));

            _schema = schema;
            _compatibility = compatibility;
            _fields = BuildFieldMetadata(schema);

            // Get schema version from interface if available
            SchemaVersion = schema is IVersionedSchema versioned ? versioned.Version : 1;

            long totalSize = SchemaHeaderSize + CalculateTotalSize(_fields);

            var options = new SharedMemoryBufferOptions
            {
                Capacity = totalSize,
                CreateOrOpen = create,
                EnableSimd = true,
                Alignment = 64
            };

            _buffer = new HighPerformanceSharedBuffer(name, options);

            if (create && _buffer.IsOwner)
            {
                InitializeMemory();
                WriteSchemaHeader();
            }
            else
            {
                ValidateSchemaCompatibility();
            }
        }

        /// <summary>
        /// Writes a strictly-typed value to a named field.
        /// For types larger than 8 bytes (non-atomic), automatic locking is applied.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(string fieldName, T value) where T : unmanaged
        {
            ThrowIfDisposed();

            if (!_fields.TryGetValue(fieldName, out var metadata))
                throw new ArgumentException($"Field '{fieldName}' not found in schema", nameof(fieldName));

            ValidateFieldType<T>(metadata);

            // Auto-lock for non-atomic types (>8 bytes) to prevent torn writes
            bool needsAutoLock = Unsafe.SizeOf<T>() > AtomicThreshold && !IsHoldingAnyLock();
            if (needsAutoLock)
            {
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
            Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<T>()];
            MemoryMarshal.Write(buffer, in value);
            _buffer.Write(buffer, SchemaHeaderSize + metadata.Offset);
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
            Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<T>()];
            _buffer.Read(buffer, SchemaHeaderSize + metadata.Offset);
            return MemoryMarshal.Read<T>(buffer);
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

            // Auto-lock for non-atomic operations (>8 bytes)
            bool needsAutoLock = bytes.Length > AtomicThreshold && !IsHoldingAnyLock();
            if (needsAutoLock)
            {
                using var _ = AcquireWriteLock();
                _buffer.Write(bytes, SchemaHeaderSize + metadata.Offset);
            }
            else
            {
                _buffer.Write(bytes, SchemaHeaderSize + metadata.Offset);
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
            bool needsAutoLock = bytes.Length > AtomicThreshold && !IsHoldingAnyLock();
            if (needsAutoLock)
            {
                using var _ = AcquireReadLock();
                _buffer.Read(bytes, SchemaHeaderSize + metadata.Offset);
            }
            else
            {
                _buffer.Read(bytes, SchemaHeaderSize + metadata.Offset);
            }
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

            // Auto-lock for string operations (always non-atomic)
            bool needsAutoLock = !IsHoldingAnyLock();
            if (needsAutoLock)
            {
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

            // Auto-lock for string operations (always non-atomic)
            bool needsAutoLock = !IsHoldingAnyLock();
            if (needsAutoLock)
            {
                using var _ = AcquireReadLock();
                return ReadStringInternal(metadata);
            }
            else
            {
                return ReadStringInternal(metadata);
            }
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
        /// Acquires an exclusive write lock on the entire memory region.
        /// </summary>
        public WriteLock AcquireWriteLock(TimeSpan timeout = default)
        {
            ThrowIfDisposed();

            if (timeout == default)
                timeout = DefaultLockTimeout;

            if (!_buffer.TryAcquireWriteLock(timeout))
                throw new TimeoutException($"Failed to acquire write lock within {timeout}");

            IncrementWriteLockDepth();
            return new WriteLock(_buffer, DecrementWriteLockDepth);
        }

        /// <summary>
        /// Acquires a shared read lock on the entire memory region.
        /// </summary>
        public ReadLock AcquireReadLock(TimeSpan timeout = default)
        {
            ThrowIfDisposed();

            if (timeout == default)
                timeout = DefaultLockTimeout;

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

            // Write magic number
            BitConverter.TryWriteBytes(header, SchemaMagic);
            // Write version
            BitConverter.TryWriteBytes(header.Slice(4), SchemaVersion);
            // Write field count
            BitConverter.TryWriteBytes(header.Slice(8), _fields.Count);
            // Write schema hash for quick compatibility check
            BitConverter.TryWriteBytes(header.Slice(12), CalculateSchemaHash());

            _buffer.Write(header, 0);
        }

        private void ValidateSchemaCompatibility()
        {
            Span<byte> header = stackalloc byte[SchemaHeaderSize];
            _buffer.Read(header, 0);

            uint magic = BitConverter.ToUInt32(header);
            if (magic != SchemaMagic)
            {
                // Old format without schema header - allow if compatibility is Full
                if (_compatibility == SchemaCompatibility.Full)
                {
                    StoredSchemaVersion = 1;
                    return;
                }
                throw new InvalidOperationException("Invalid schema header in shared memory");
            }

            StoredSchemaVersion = BitConverter.ToInt32(header.Slice(4));
            int storedFieldCount = BitConverter.ToInt32(header.Slice(8));
            int storedHash = BitConverter.ToInt32(header.Slice(12));

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
            }

            // Verify schema hash if versions match
            if (StoredSchemaVersion == SchemaVersion && storedHash != CalculateSchemaHash())
            {
                throw new InvalidOperationException(
                    "Schema hash mismatch - the schema structure has changed");
            }
        }

        private int CalculateSchemaHash()
        {
            unchecked
            {
                int hash = 17;
                foreach (var field in _fields.Values.OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    // Use stable hash instead of GetHashCode() which varies per process in .NET Core
                    hash = hash * 31 + StableStringHash(field.Name);
                    hash = hash * 31 + (int)field.TypeCode;
                    hash = hash * 31 + field.Size;
                    hash = hash * 31 + field.ArrayLength;
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
                if (string.IsNullOrWhiteSpace(field.Name))
                    throw new ArgumentException("Field name cannot be empty");

                if (metadata.ContainsKey(field.Name))
                    throw new ArgumentException($"Duplicate field name: {field.Name}");

                long alignment = Math.Max(field.Alignment, 1);
                currentOffset = (currentOffset + alignment - 1) & ~(alignment - 1);

                var meta = new FieldMetadata
                {
                    Name = field.Name,
                    Offset = currentOffset,
                    Size = field.Size,
                    ElementSize = field.ElementSize,
                    ArrayLength = field.ArrayLength,
                    TypeCode = field.TypeCode,
                    IsArray = field.ArrayLength > 1,
                    IsString = field.TypeCode == SharedTypeCode.Char && field.ArrayLength > 1
                };

                metadata[field.Name] = meta;
                currentOffset += field.Size;
            }

            return metadata;
        }

        private long CalculateTotalSize(Dictionary<string, FieldMetadata> fields)
        {
            if (fields.Count == 0)
                return 64;

            long maxEnd = fields.Values.Max(f => f.Offset + f.Size);
            return (maxEnd + 63) & ~63L;
        }

        private void InitializeMemory()
        {
            Span<byte> zeros = stackalloc byte[4096];
            zeros.Clear();

            long offset = 0;
            long remaining = _buffer.Capacity;

            while (remaining > 0)
            {
                int chunkSize = (int)Math.Min(zeros.Length, remaining);
                _buffer.Write(zeros.Slice(0, chunkSize), offset);
                offset += chunkSize;
                remaining -= chunkSize;
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
                if (metadata.TypeCode == SharedTypeCode.Struct && actualTypeCode == SharedTypeCode.Struct)
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
                throw new ObjectDisposedException(nameof(StrictSharedMemory<TSchema>));
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
        ~StrictSharedMemory()
        {
            _buffer?.Dispose();
            // Don't dispose ThreadLocal in finalizer (may already be cleaned up)
        }

        /// <summary>
        /// RAII wrapper for write lock with double-dispose protection
        /// </summary>
        public struct WriteLock : IDisposable
        {
            private ISharedMemoryBuffer? _buffer;
            private readonly Action? _onDispose;

            internal WriteLock(ISharedMemoryBuffer buffer, Action? onDispose = null)
            {
                _buffer = buffer;
                _onDispose = onDispose;
            }

            /// <summary>
            /// Releases the write lock if not already released
            /// </summary>
            public void Dispose()
            {
                var buffer = Interlocked.Exchange(ref _buffer, null);
                if (buffer != null)
                {
                    buffer.ReleaseWriteLock();
                    _onDispose?.Invoke();
                }
            }
        }

        /// <summary>
        /// RAII wrapper for read lock with double-dispose protection
        /// </summary>
        public struct ReadLock : IDisposable
        {
            private ISharedMemoryBuffer? _buffer;
            private readonly Action? _onDispose;

            internal ReadLock(ISharedMemoryBuffer buffer, Action? onDispose = null)
            {
                _buffer = buffer;
                _onDispose = onDispose;
            }

            /// <summary>
            /// Releases the read lock if not already released
            /// </summary>
            public void Dispose()
            {
                var buffer = Interlocked.Exchange(ref _buffer, null);
                if (buffer != null)
                {
                    buffer.ReleaseReadLock();
                    _onDispose?.Invoke();
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
            public SharedTypeCode TypeCode { get; set; }
            public bool IsArray { get; set; }
            public bool IsString { get; set; }
        }
    }

    /// <summary>
    /// Interface for versioned schemas
    /// </summary>
    public interface IVersionedSchema : ISharedMemorySchema
    {
        /// <summary>
        /// Gets the schema version number
        /// </summary>
        int Version { get; }

        /// <summary>
        /// Checks compatibility with another version
        /// </summary>
        bool IsCompatibleWith(int otherVersion);
    }

    /// <summary>
    /// Schema interface that must be implemented by all strict shared memory schemas.
    /// </summary>
    public interface ISharedMemorySchema
    {
        /// <summary>
        /// Returns all field definitions in the schema.
        /// Order determines memory layout.
        /// </summary>
        IEnumerable<FieldDefinition> GetFields();
    }

    /// <summary>
    /// Defines a single field in a strict shared memory schema.
    /// </summary>
    public readonly struct FieldDefinition
    {
        /// <summary>Gets the field name</summary>
        public string Name { get; init; }

        /// <summary>Gets the type code of the field</summary>
        public SharedTypeCode TypeCode { get; init; }

        /// <summary>Gets the size of a single element in bytes</summary>
        public int ElementSize { get; init; }

        /// <summary>Gets the array length (1 for scalars)</summary>
        public int ArrayLength { get; init; }

        /// <summary>Gets the memory alignment requirement</summary>
        public int Alignment { get; init; }

        /// <summary>Gets the total size of the field in bytes</summary>
        public int Size => ElementSize * ArrayLength;

        /// <summary>
        /// Creates a scalar field definition for a primitive type.
        /// </summary>
        /// <typeparam name="T">Unmanaged value type</typeparam>
        /// <param name="name">Field name</param>
        /// <returns>Field definition for a single value</returns>
        public static FieldDefinition Scalar<T>(string name) where T : unmanaged
        {
            return new FieldDefinition
            {
                Name = name,
                TypeCode = GetTypeCode<T>(),
                ElementSize = Unsafe.SizeOf<T>(),
                ArrayLength = 1,
                Alignment = Unsafe.SizeOf<T>()
            };
        }

        /// <summary>
        /// Creates an array field definition for a fixed-size array of primitive types.
        /// </summary>
        /// <typeparam name="T">Unmanaged element type</typeparam>
        /// <param name="name">Field name</param>
        /// <param name="length">Number of elements in the array</param>
        /// <returns>Field definition for a fixed-size array</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when length is not positive</exception>
        public static FieldDefinition Array<T>(string name, int length) where T : unmanaged
        {
            if (length <= 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            return new FieldDefinition
            {
                Name = name,
                TypeCode = GetTypeCode<T>(),
                ElementSize = Unsafe.SizeOf<T>(),
                ArrayLength = length,
                Alignment = Unsafe.SizeOf<T>()
            };
        }

        /// <summary>
        /// Creates a string field definition for a fixed-size null-terminated string.
        /// </summary>
        /// <param name="name">Field name</param>
        /// <param name="maxLength">Maximum string length including null terminator</param>
        /// <returns>Field definition for a fixed-size string</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when maxLength is not positive</exception>
        public static FieldDefinition String(string name, int maxLength)
        {
            if (maxLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxLength));

            return new FieldDefinition
            {
                Name = name,
                TypeCode = SharedTypeCode.Char,
                ElementSize = sizeof(char),
                ArrayLength = maxLength,
                Alignment = sizeof(char)
            };
        }

        internal static SharedTypeCode GetTypeCode<T>() where T : unmanaged
        {
            var type = typeof(T);

            // Primitive types
            if (type == typeof(bool))
                return SharedTypeCode.Boolean;
            if (type == typeof(byte))
                return SharedTypeCode.Byte;
            if (type == typeof(sbyte))
                return SharedTypeCode.SByte;
            if (type == typeof(char))
                return SharedTypeCode.Char;
            if (type == typeof(short))
                return SharedTypeCode.Int16;
            if (type == typeof(ushort))
                return SharedTypeCode.UInt16;
            if (type == typeof(int))
                return SharedTypeCode.Int32;
            if (type == typeof(uint))
                return SharedTypeCode.UInt32;
            if (type == typeof(long))
                return SharedTypeCode.Int64;
            if (type == typeof(ulong))
                return SharedTypeCode.UInt64;
            if (type == typeof(float))
                return SharedTypeCode.Single;
            if (type == typeof(double))
                return SharedTypeCode.Double;
            if (type == typeof(decimal))
                return SharedTypeCode.Decimal;

            // Extended types
            if (type == typeof(System.Guid))
                return SharedTypeCode.Guid;
            if (type == typeof(System.DateTime))
                return SharedTypeCode.DateTime;
            if (type == typeof(System.TimeSpan))
                return SharedTypeCode.TimeSpan;
            if (type == typeof(System.DateTimeOffset))
                return SharedTypeCode.DateTimeOffset;

            // Enum types - use underlying type's code
            if (type.IsEnum)
            {
                var underlyingType = Enum.GetUnderlyingType(type);
                if (underlyingType == typeof(int))
                    return SharedTypeCode.Int32;
                if (underlyingType == typeof(byte))
                    return SharedTypeCode.Byte;
                if (underlyingType == typeof(short))
                    return SharedTypeCode.Int16;
                if (underlyingType == typeof(long))
                    return SharedTypeCode.Int64;
                if (underlyingType == typeof(uint))
                    return SharedTypeCode.UInt32;
                if (underlyingType == typeof(ushort))
                    return SharedTypeCode.UInt16;
                if (underlyingType == typeof(ulong))
                    return SharedTypeCode.UInt64;
                if (underlyingType == typeof(sbyte))
                    return SharedTypeCode.SByte;
            }

            // Custom unmanaged struct
            if (type.IsValueType && !type.IsPrimitive)
                return SharedTypeCode.Struct;

            return SharedTypeCode.Unknown;
        }

        /// <summary>
        /// Creates a struct field definition for a custom unmanaged struct.
        /// </summary>
        /// <typeparam name="T">Unmanaged struct type</typeparam>
        /// <param name="name">Field name</param>
        /// <returns>Field definition for a struct value</returns>
        public static FieldDefinition Struct<T>(string name) where T : unmanaged
        {
            return new FieldDefinition
            {
                Name = name,
                TypeCode = SharedTypeCode.Struct,
                ElementSize = Unsafe.SizeOf<T>(),
                ArrayLength = 1,
                Alignment = IntPtr.Size
            };
        }

        /// <summary>
        /// Creates an array field definition for a fixed-size array of custom unmanaged structs.
        /// </summary>
        /// <typeparam name="T">Unmanaged struct type</typeparam>
        /// <param name="name">Field name</param>
        /// <param name="length">Number of elements in the array</param>
        /// <returns>Field definition for a fixed-size struct array</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when length is not positive</exception>
        public static FieldDefinition StructArray<T>(string name, int length) where T : unmanaged
        {
            if (length <= 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            return new FieldDefinition
            {
                Name = name,
                TypeCode = SharedTypeCode.Struct,
                ElementSize = Unsafe.SizeOf<T>(),
                ArrayLength = length,
                Alignment = IntPtr.Size
            };
        }
    }
}
