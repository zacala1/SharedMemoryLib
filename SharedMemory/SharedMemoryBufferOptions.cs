using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace SharedMemory
{
    /// <summary>
    /// Configuration options for high-performance shared memory buffers
    /// </summary>
    public sealed class SharedMemoryBufferOptions
    {
        /// <summary>
        /// Default capacity: 64MB
        /// </summary>
        public const long DefaultCapacity = 64L * 1024 * 1024;

        /// <summary>
        /// Default lock timeout: 5 seconds
        /// </summary>
        public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Default orphan lock timeout: 30 seconds
        /// </summary>
        public static readonly TimeSpan DefaultOrphanLockTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the buffer capacity in bytes
        /// </summary>
        public long Capacity { get; set; } = DefaultCapacity;

        /// <summary>
        /// Gets or sets the default lock acquisition timeout
        /// </summary>
        public TimeSpan LockTimeout { get; set; } = DefaultLockTimeout;

        /// <summary>
        /// Gets or sets whether to use lock-free synchronization (experimental)
        /// </summary>
        public bool UseLockFree { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to enable SIMD optimizations
        /// </summary>
        public bool EnableSimd { get; set; } = true;

        /// <summary>
        /// Gets or sets the memory alignment (must be power of 2)
        /// </summary>
        public int Alignment { get; set; } = 64; // Cache line size

        /// <summary>
        /// Gets or sets whether to create or open existing
        /// </summary>
        public bool CreateOrOpen { get; set; } = true;

        /// <summary>
        /// Gets or sets the file path for persistent storage (null for anonymous)
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// Gets or sets whether to enable orphan lock detection and recovery
        /// </summary>
        public bool EnableOrphanLockDetection { get; set; } = true;

        /// <summary>
        /// Gets or sets the timeout after which a lock is considered orphaned
        /// </summary>
        public TimeSpan OrphanLockTimeout { get; set; } = DefaultOrphanLockTimeout;

        /// <summary>
        /// Gets or sets whether to enable checksum verification
        /// </summary>
        public bool EnableChecksumVerification { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to enable events
        /// </summary>
        public bool EnableEvents { get; set; } = false;

        /// <summary>
        /// Gets or sets the logger instance
        /// </summary>
        public ILogger? Logger { get; set; }

        /// <summary>
        /// Validates the options
        /// </summary>
        public void Validate()
        {
            if (Capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(Capacity), "Capacity must be positive");

            if (LockTimeout < TimeSpan.Zero && LockTimeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(LockTimeout), "LockTimeout must be non-negative or infinite");

            if (Alignment <= 0 || (Alignment & (Alignment - 1)) != 0)
                throw new ArgumentException("Alignment must be a power of 2", nameof(Alignment));

            if (Capacity > int.MaxValue && Environment.Is64BitProcess == false)
                throw new ArgumentOutOfRangeException(nameof(Capacity), "Cannot allocate > 2GB on 32-bit process");

            if (OrphanLockTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(OrphanLockTimeout), "OrphanLockTimeout must be non-negative");
        }
    }
}
