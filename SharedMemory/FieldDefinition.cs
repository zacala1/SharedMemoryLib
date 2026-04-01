using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SharedMemory
{
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

        /// <summary>
        /// Creates a blob field definition for fixed-size binary data.
        /// Layout: [4-byte length prefix] + [maxSize bytes of data].
        /// Total storage = maxSize + 4 bytes.
        /// </summary>
        /// <param name="name">Field name</param>
        /// <param name="maxSize">Maximum data size in bytes (excluding the 4-byte length prefix)</param>
        /// <returns>Field definition for a blob value</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when maxSize is not positive</exception>
        public static FieldDefinition Blob(string name, int maxSize)
        {
            if (maxSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSize));

            return new FieldDefinition
            {
                Name = name,
                TypeCode = SharedTypeCode.Blob,
                ElementSize = 1,
                ArrayLength = maxSize + 4, // 4-byte length prefix + data
                Alignment = 4
            };
        }

        /// <summary>
        /// Creates a UTF-8 string field definition.
        /// Layout: [4-byte byte-length prefix] + [maxByteLength bytes of UTF-8 data].
        /// Total storage = maxByteLength + 4 bytes.
        /// </summary>
        /// <param name="name">Field name</param>
        /// <param name="maxByteLength">Maximum UTF-8 encoded size in bytes (excluding the 4-byte length prefix)</param>
        /// <returns>Field definition for a UTF-8 string value</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when maxByteLength is not positive</exception>
        public static FieldDefinition Utf8String(string name, int maxByteLength)
        {
            if (maxByteLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxByteLength));

            return new FieldDefinition
            {
                Name = name,
                TypeCode = SharedTypeCode.Utf8String,
                ElementSize = 1,
                ArrayLength = maxByteLength + 4, // 4-byte length prefix + data
                Alignment = 4
            };
        }

        // Pre-built lookup table: O(1) dictionary lookup instead of O(n) if-chain.
        // Enum and custom struct types are handled separately after the table miss.
        private static readonly Dictionary<Type, SharedTypeCode> s_typeCodes = new(18)
        {
            [typeof(bool)]           = SharedTypeCode.Boolean,
            [typeof(byte)]           = SharedTypeCode.Byte,
            [typeof(sbyte)]          = SharedTypeCode.SByte,
            [typeof(char)]           = SharedTypeCode.Char,
            [typeof(short)]          = SharedTypeCode.Int16,
            [typeof(ushort)]         = SharedTypeCode.UInt16,
            [typeof(int)]            = SharedTypeCode.Int32,
            [typeof(uint)]           = SharedTypeCode.UInt32,
            [typeof(long)]           = SharedTypeCode.Int64,
            [typeof(ulong)]          = SharedTypeCode.UInt64,
            [typeof(float)]          = SharedTypeCode.Single,
            [typeof(double)]         = SharedTypeCode.Double,
            [typeof(decimal)]        = SharedTypeCode.Decimal,
            [typeof(Guid)]           = SharedTypeCode.Guid,
            [typeof(DateTime)]       = SharedTypeCode.DateTime,
            [typeof(TimeSpan)]       = SharedTypeCode.TimeSpan,
            [typeof(DateTimeOffset)] = SharedTypeCode.DateTimeOffset,
        };

        internal static SharedTypeCode GetTypeCode<T>() where T : unmanaged
        {
            var type = typeof(T);

            if (s_typeCodes.TryGetValue(type, out var code))
                return code;

            // Enum types - resolve through underlying type
            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                return s_typeCodes.TryGetValue(underlying, out code) ? code : SharedTypeCode.Unknown;
            }

            // Custom unmanaged struct
            if (type.IsValueType && !type.IsPrimitive)
                return SharedTypeCode.Struct;

            return SharedTypeCode.Unknown;
        }
    }
}
