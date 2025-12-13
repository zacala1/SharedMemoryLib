using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace SharedMemory
{
    /// <summary>
    /// Delegate for buffer events
    /// </summary>
    public delegate void BufferEventHandler(object sender, BufferEventArgs e);

    /// <summary>
    /// Buffer event arguments containing details about buffer operations
    /// </summary>
    public class BufferEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the type of event that occurred
        /// </summary>
        public BufferEventType EventType { get; init; }

        /// <summary>
        /// Gets the number of bytes affected by the operation
        /// </summary>
        public long BytesAffected { get; init; }

        /// <summary>
        /// Gets the offset in the buffer where the operation occurred
        /// </summary>
        public long Offset { get; init; }
    }

    /// <summary>
    /// Buffer event types indicating what operation triggered the event
    /// </summary>
    public enum BufferEventType
    {
        /// <summary>Data was written to the buffer</summary>
        DataWritten,
        /// <summary>Data was read from the buffer</summary>
        DataRead,
        /// <summary>A lock was acquired on the buffer</summary>
        LockAcquired,
        /// <summary>A lock was released on the buffer</summary>
        LockReleased,
        /// <summary>The buffer is full and cannot accept more data</summary>
        BufferFull,
        /// <summary>An orphaned lock was detected (owner process died)</summary>
        OrphanLockDetected
    }

    /// <summary>
    /// Lock ownership information for orphan detection
    /// </summary>
    public readonly struct LockOwnerInfo
    {
        /// <summary>
        /// Gets the process ID of the lock owner
        /// </summary>
        public int ProcessId { get; init; }

        /// <summary>
        /// Gets the thread ID of the lock owner
        /// </summary>
        public long ThreadId { get; init; }

        /// <summary>
        /// Gets the timestamp when the lock was acquired
        /// </summary>
        public long AcquiredTimestamp { get; init; }

        /// <summary>
        /// Gets whether the lock is orphaned (owner process died)
        /// </summary>
        public bool IsOrphan { get; init; }
    }

    /// <summary>
    /// High-performance shared memory buffer interface with zero-allocation APIs
    /// </summary>
    public interface ISharedMemoryBuffer : IDisposable
    {
        /// <summary>
        /// Gets the name of this shared memory region
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the total capacity in bytes
        /// </summary>
        long Capacity { get; }

        /// <summary>
        /// Gets whether this instance owns the underlying memory (creator vs consumer)
        /// </summary>
        bool IsOwner { get; }

        /// <summary>
        /// Writes data from a span to the buffer at the specified offset.
        /// Zero-allocation, SIMD-optimized when aligned.
        /// </summary>
        int Write(ReadOnlySpan<byte> source, long offset);

        /// <summary>
        /// Reads data from the buffer into a span at the specified offset.
        /// Zero-allocation, SIMD-optimized when aligned.
        /// </summary>
        int Read(Span<byte> destination, long offset);

        /// <summary>
        /// Asynchronously writes data with cancellation support
        /// </summary>
        ValueTask<int> WriteAsync(ReadOnlyMemory<byte> source, long offset, CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously reads data with cancellation support
        /// </summary>
        ValueTask<int> ReadAsync(Memory<byte> destination, long offset, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a memory view of the buffer region for zero-copy access.
        /// UNSAFE: Caller must ensure thread safety and lifetime management.
        /// </summary>
        Memory<byte> GetMemory(long offset, int length);

        /// <summary>
        /// Tries to acquire an exclusive write lock with timeout
        /// </summary>
        bool TryAcquireWriteLock(TimeSpan timeout);

        /// <summary>
        /// Releases the write lock
        /// </summary>
        void ReleaseWriteLock();

        /// <summary>
        /// Tries to acquire a shared read lock with timeout
        /// </summary>
        bool TryAcquireReadLock(TimeSpan timeout);

        /// <summary>
        /// Releases the read lock
        /// </summary>
        void ReleaseReadLock();

        /// <summary>
        /// Checks if the current write lock is orphaned (owner process died)
        /// </summary>
        bool IsWriteLockOrphaned();

        /// <summary>
        /// Forces release of an orphaned write lock
        /// </summary>
        /// <returns>True if lock was orphaned and released</returns>
        bool TryForceReleaseWriteLock();

        /// <summary>
        /// Gets information about the current lock owner
        /// </summary>
        LockOwnerInfo GetLockOwnerInfo();

        /// <summary>
        /// Calculates checksum for data integrity verification
        /// </summary>
        uint CalculateChecksum(long offset, int length);

        /// <summary>
        /// Verifies data integrity using stored checksum
        /// </summary>
        bool VerifyIntegrity();

        /// <summary>
        /// Event raised when data is written
        /// </summary>
        event BufferEventHandler? OnDataWritten;

        /// <summary>
        /// Event raised when orphan lock is detected
        /// </summary>
        event BufferEventHandler? OnOrphanLockDetected;
    }
}
