using System.Collections.Generic;

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
        Struct,
        /// <summary>Fixed-size binary blob with 4-byte length prefix</summary>
        Blob,
        /// <summary>UTF-8 encoded string with 4-byte length prefix</summary>
        Utf8String
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
}
