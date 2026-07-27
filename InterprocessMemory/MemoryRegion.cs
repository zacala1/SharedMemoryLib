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

namespace InterprocessMemory
{
    /// <summary>
    /// An offset-addressable memory region shared by separate processes.
    ///
    /// <para>Cross-platform: on Windows uses named MemoryMappedFile; on Linux uses a file in
    /// <c>/dev/shm</c> (tmpfs) wrapped by <c>MemoryMappedFile.CreateFromFile</c>. Both paths
    /// yield the same raw pointer after construction, so the hot path (Read/Write/locks/SIMD)
    /// is byte-for-byte identical and there is no per-call OS dispatch.</para>
    /// </summary>
    public sealed unsafe class MemoryRegion : IMemoryRegion
    {
        private readonly string _name;
        private long _capacity;
        private readonly bool _isOwner;
        private readonly MemoryRegionOptions _options;
        private readonly ILogger? _logger;
        private readonly bool _createOrOpen;
        private readonly RegionKind _regionKind;

        // Captured from options at construction so the hot path doesn't dereference _options.
        // readonly + JIT inlining means each Read/Write checks a single field load (often
        // constant-folded if the JIT proves the value); when false, all Interlocked stats
        // updates are skipped entirely.
        private readonly bool _statsEnabled;

        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _accessor;
        // On Linux, we may also hold a FileStream backing the /dev/shm file. The MMF takes
        // ownership when leaveOpen=false (default), so we only track it for diagnostic purposes
        // and to clean up the path on the owner's dispose if no other openers remain.
        private FileStream? _backingFile;
        private string? _backingFilePath;
        // Set true if we freshly created (or were the first to set the size of) the /dev/shm
        // file on Linux. Lets Cleanup unlink the file when construction fails partway through —
        // otherwise a half-sized file would persist in tmpfs and corrupt the next open.
        private bool _createdBackingFile;
        private bool _initializedSuccessfully;
        private byte* _basePtr;

        // Performance counters
        private long _totalReads;
        private long _totalWrites;
        private long _totalBytesRead;
        private long _totalBytesWritten;

        private volatile int _disposed;

        // Cached once per process: stamped into the header at lock acquire so an orphan check
        // can distinguish "same PID, same process" from "same PID, recycled by the OS for an
        // unrelated process". Process.StartTime can throw under restricted permissions (Linux
        // containers without /proc, certain Windows ACLs) — in that case we store 0 and the
        // orphan check silently falls back to PID-only matching.
        private static readonly long s_processStartTimeBinary = TryCaptureProcessStartTime();

        private static long TryCaptureProcessStartTime()
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                return p.StartTime.ToBinary();
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Extended header structure with orphan lock detection support.
        /// WriterLockState and ReaderCount are placed in separate 64-byte cache lines
        /// to prevent false sharing when multiple processes or threads contend concurrently.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 128)]
        private struct SharedHeader
        {
            public const uint MagicNumber = 0x524D5049; // "IPMR"
            // Intermediate value written by the winner of the init CAS before it has finished writing
            // the rest of the header. Losers spin-wait until they see MagicNumber (not this value)
            // before reading Capacity etc., preventing a torn read of half-initialized state.
            public const uint MagicInitializing = 0x494D5049; // "IPMI"
            public const uint FormatVersion = 3;
            public const int Size = 128;

            // Cache line 0 (offsets 0–63): writer-side fields
            [FieldOffset(0)] public uint Magic;
            [FieldOffset(4)] public uint Version;
            [FieldOffset(8)] public long Capacity;
            [FieldOffset(16)] public long CreationTimestamp;
            [FieldOffset(24)] public int WriterLockState;       // 0 = free, 1 = locked (Interlocked/Volatile)
            [FieldOffset(28)] public int LockOwnerProcessId;
            [FieldOffset(32)] public long LockOwnerThreadId;
            [FieldOffset(40)] public long LockAcquiredTimestamp;
            // PID reuse defense: if a lock-holding process dies and the OS recycles its PID for an
            // unrelated process, GetProcessById would find the new process alive and skip orphan
            // recovery — leaving the lock permanently held. By recording the owner's Process.StartTime
            // at acquire and comparing on the orphan check, we detect the impostor. Stored as
            // DateTime.ToBinary() (signed long). Value 0 means "not recorded" — older binaries that
            // didn't write it, or hosts where StartTime is unreadable (permission denied); in that
            // case orphan detection falls back to the PID-only check, preserving prior behavior.
            [FieldOffset(48)] public long LockOwnerProcessStartTime;
            // bytes 56–63: implicit padding

            // Cache line 1 (offsets 64–127): reader-side fields and checksum metadata
            [FieldOffset(64)] public int ReaderCount;           // accessed via Interlocked/Volatile
            [FieldOffset(68)] public uint DataChecksum;
            [FieldOffset(72)] public long ChecksumOffset;
            [FieldOffset(80)] public int ChecksumLength;
            [FieldOffset(84)] public int RegionKind;
            // bytes 88–127: reserved
        }

        private const long HeaderSize = SharedHeader.Size;

        /// <inheritdoc/>
        public string Name => _name;

        /// <inheritdoc/>
        public long Capacity => _capacity;

        /// <inheritdoc/>
        public bool IsOwner => _isOwner;

        /// <inheritdoc/>
        public event MemoryRegionEventHandler? OnDataWritten;

        /// <inheritdoc/>
        public event MemoryRegionEventHandler? OnOrphanLockDetected;

        /// <summary>
        /// Gets performance statistics for the buffer
        /// </summary>
        /// <returns>Tuple containing read/write counts and bytes transferred</returns>
        public (long Reads, long Writes, long BytesRead, long BytesWritten) GetStatistics() =>
            (Volatile.Read(ref _totalReads), Volatile.Read(ref _totalWrites),
             Volatile.Read(ref _totalBytesRead), Volatile.Read(ref _totalBytesWritten));

        /// <summary>
        /// Creates or opens a raw interprocess memory region.
        /// </summary>
        public static MemoryRegion CreateOrOpen(
            string name,
            long capacityBytes,
            MemoryRegionOptions? options = null) =>
            new(name, capacityBytes, options, createOrOpen: true, RegionKind.RawMemory);

        /// <summary>
        /// Opens an existing raw interprocess memory region and discovers its capacity from its header.
        /// </summary>
        public static MemoryRegion OpenExisting(
            string name,
            MemoryRegionOptions? options = null) =>
            new(name, capacityBytes: null, options, createOrOpen: false, RegionKind.RawMemory);

        internal static MemoryRegion CreateOrOpen(
            string name,
            long capacityBytes,
            MemoryRegionOptions? options,
            RegionKind regionKind) =>
            new(name, capacityBytes, options, createOrOpen: true, regionKind);

        internal static MemoryRegion OpenExisting(
            string name,
            MemoryRegionOptions? options,
            RegionKind regionKind) =>
            new(name, capacityBytes: null, options, createOrOpen: false, regionKind);

        internal MemoryRegion(string name, MemoryRegionOptions? options = null)
            : this(
                name,
                (options ?? new MemoryRegionOptions()).CreateOrOpen
                    ? (options ?? new MemoryRegionOptions()).Capacity
                    : null,
                options,
                (options ?? new MemoryRegionOptions()).CreateOrOpen,
                RegionKind.RawMemory)
        {
        }

        private MemoryRegion(
            string name,
            long? capacityBytes,
            MemoryRegionOptions? options,
            bool createOrOpen,
            RegionKind regionKind)
        {
            _name = ValidateFlatName(name);
            if (capacityBytes is <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Capacity must be positive");
            if (capacityBytes > int.MaxValue && !Environment.Is64BitProcess)
                throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Cannot allocate more than 2 GB in a 32-bit process");

            _options = options ?? new MemoryRegionOptions();
            _options.Validate();
            _logger = _options.Logger;
            _statsEnabled = _options.EnableStatistics;
            _createOrOpen = createOrOpen;
            _regionKind = regionKind;

            _capacity = capacityBytes ?? 0;

            _logger?.LogDebug("Creating shared buffer '{Name}' with capacity {Capacity}", name, _capacity);

            Initialize();
            try
            {
                // InitializeOrOpen can throw (capacity mismatch, invalid magic, init timeout).
                // Initialize() has its own try/catch+Cleanup, but once it returns successfully
                // the MMF/accessor/pointer (and on Linux, the /dev/shm file) are live and would
                // leak until finalization if we let the constructor throw uncaught. Mirror the
                // Initialize() catch here so the failure mode is deterministic.
                _isOwner = InitializeOrOpen();
                _initializedSuccessfully = true;
                if (!_isOwner)
                    _createdBackingFile = false;
            }
            catch
            {
                Cleanup();
                throw;
            }

            _logger?.LogInformation("Shared buffer '{Name}' initialized. IsOwner: {IsOwner}", name, _isOwner);
        }

        private void Initialize()
        {
            long totalSize = _createOrOpen ? checked(HeaderSize + _capacity) : 0;

            try
            {
                // Pick the backing primitive once at construction. Each branch sets _mmf (and
                // on Linux, _backingFilePath/_createdBackingFile). The accessor + pointer
                // acquisition is identical across platforms and lives below the dispatch.
                if (!string.IsNullOrEmpty(_options.FilePath))
                    CreateMmfFromExplicitFilePath(totalSize);
                else if (OperatingSystem.IsWindows())
                    CreateMmfFromWindowsNamedRegion(totalSize);
                else if (OperatingSystem.IsLinux())
                    CreateMmfFromLinuxDevShm(totalSize);
                else
                    throw new PlatformNotSupportedException(
                        "MemoryRegion requires Windows or Linux. " +
                        "macOS/other POSIX is not currently supported in the anonymous-named mode; " +
                        "set MemoryRegionOptions.FilePath to use file-backed mode instead.");

                _accessor = _mmf!.CreateViewAccessor(
                    0,
                    _createOrOpen ? totalSize : 0,
                    MemoryMappedFileAccess.ReadWrite);
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

        /// <summary>
        /// Explicit on-disk file backing. Cross-platform: Windows uses the kernel-namespace
        /// alias <c>mapName</c> for shared-by-name access; Linux ignores it (mmap binds to the
        /// inode), so we pass null there.
        /// </summary>
        private void CreateMmfFromExplicitFilePath(long totalSize)
        {
            string? mapName = OperatingSystem.IsWindows() ? _name : null;
            FileMode mode = _createOrOpen ? FileMode.OpenOrCreate : FileMode.Open;
            _mmf = MemoryMappedFile.CreateFromFile(
                _options.FilePath!,
                mode,
                mapName,
                _createOrOpen ? totalSize : 0,
                MemoryMappedFileAccess.ReadWrite);
        }

        /// <summary>
        /// Windows named global shared memory — the kernel manages the namespace and reclaims
        /// the section when the last handle closes, so we don't need to track any backing file.
        /// </summary>
        /// <remarks>
        /// Attributed Windows-only because the underlying API is Windows-only. The caller in
        /// <see cref="Initialize"/> already gates on <see cref="OperatingSystem.IsWindows"/>;
        /// this attribute just tells CA1416 not to flag the call site.
        /// </remarks>
        [SupportedOSPlatform("windows")]
        private void CreateMmfFromWindowsNamedRegion(long totalSize)
        {
            _mmf = _createOrOpen
                ? MemoryMappedFile.CreateOrOpen(
                    _name,
                    totalSize,
                    MemoryMappedFileAccess.ReadWrite,
                    MemoryMappedFileOptions.None,
                    HandleInheritability.None)
                : MemoryMappedFile.OpenExisting(
                    _name,
                    MemoryMappedFileRights.ReadWrite,
                    HandleInheritability.None);
        }

        /// <summary>
        /// Linux: emulate Windows named MMF via a file under /dev/shm (tmpfs). POSIX shm_open(3)
        /// internally creates the file in this same tmpfs, so the resulting mapping is
        /// functionally and performance-wise identical, while letting us reuse the cross-platform
        /// <see cref="MemoryMappedFile.CreateFromFile(FileStream, string?, long, MemoryMappedFileAccess, HandleInheritability, bool)"/>
        /// API (no P/Invoke, no glibc version assumptions, no arch-specific O_ flag values).
        /// </summary>
        private void CreateMmfFromLinuxDevShm(long totalSize)
        {
            _backingFilePath = "/dev/shm/" + _name;
            FileMode mode = _createOrOpen ? FileMode.OpenOrCreate : FileMode.Open;
            _backingFile = new FileStream(_backingFilePath,
                mode, FileAccess.ReadWrite, FileShare.ReadWrite);

            if (_backingFile.Length == 0)
            {
                if (!_createOrOpen)
                {
                    _backingFile.Dispose();
                    _backingFile = null;
                    throw new InvalidDataException(
                        $"Existing shared memory '{_backingFilePath}' is empty and has not been initialized.");
                }

                // Fresh region — set the size up front. Subsequent openers will see this length
                // and skip the SetLength call below. Mark that WE were the process to size this
                // file: if construction fails between here and AcquirePointer, Cleanup will
                // unlink the file so the next caller doesn't trip over a half-initialized blob.
                _createdBackingFile = true;
                _backingFile.SetLength(totalSize);
            }
            else if (_createOrOpen && _backingFile.Length != totalSize)
            {
                // Mirror the Windows behavior where capacity mismatch on open throws.
                long actualLen = _backingFile.Length;
                _backingFile.Dispose();
                _backingFile = null;
                throw new InvalidOperationException(
                    $"Existing shared memory '{_backingFilePath}' has size {actualLen} but {totalSize} was requested. " +
                    $"Either match the existing size or remove the file.");
            }

            _mmf = MemoryMappedFile.CreateFromFile(
                _backingFile,
                mapName: null,                  // Linux ignores/rejects non-null mapName
                _createOrOpen ? totalSize : 0,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: false);              // MMF takes ownership of the FileStream
            _backingFile = null;                // ownership transferred — don't double-dispose
        }

        /// <summary>
        /// Validates the cross-platform flat identifier used for the named region.
        /// </summary>
        private static string ValidateFlatName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty.", nameof(name));

            if (name.Contains('/') || name.Contains('\\') || name == "." || name == "..")
                throw new ArgumentException(
                    $"Name '{name}' contains path separators or traversal fragments; " +
                    $"use a flat identifier for cross-platform compatibility.",
                    nameof(name));

            if (name.Contains('\0'))
                throw new ArgumentException(
                    $"Name '{name}' contains a NUL character.",
                    nameof(name));

            foreach (char c in name)
            {
                if (char.IsControl(c))
                    throw new ArgumentException(
                        $"Name '{name}' contains a control character (U+{(int)c:X4}); use printable ASCII.",
                        nameof(name));
            }

            const int NameMax = 255;
            int utf8Len = System.Text.Encoding.UTF8.GetByteCount(name);
            if (utf8Len > NameMax)
                throw new ArgumentException(
                    $"Name '{name}' is {utf8Len} bytes in UTF-8; the maximum is {NameMax}.",
                    nameof(name));

            return name;
        }

        private bool InitializeOrOpen()
        {
            var header = (SharedHeader*)_basePtr;

            // Two-phase init to make cross-process concurrent open race-safe:
            //   Phase 1: CAS Magic 0 → Initializing. Winner gets exclusive init rights.
            //   Phase 2: Winner writes all header fields, then promotes Magic to MagicNumber
            //            via a release-store. Losers spin until they observe MagicNumber and
            //            only then read the rest of the header, guaranteeing they never see
            //            partially-initialized state (the previous code could read Magic=
            //            MagicNumber but Capacity=0, falsely triggering a capacity mismatch).
            uint prev = _createOrOpen
                ? Interlocked.CompareExchange(ref header->Magic, SharedHeader.MagicInitializing, 0)
                : Volatile.Read(ref header->Magic);

            if (_createOrOpen && prev == 0)
            {
                header->Version = SharedHeader.FormatVersion;
                header->Capacity = _capacity;
                header->CreationTimestamp = Stopwatch.GetTimestamp();
                header->WriterLockState = 0;
                header->ReaderCount = 0;
                header->LockOwnerProcessId = 0;
                header->LockOwnerThreadId = 0;
                header->LockOwnerProcessStartTime = 0;
                header->LockAcquiredTimestamp = 0;
                header->DataChecksum = 0;
                header->ChecksumOffset = 0;
                header->ChecksumLength = 0;
                header->RegionKind = (int)_regionKind;

                Thread.MemoryBarrier();

                // Release-store: makes all prior header writes visible before any reader sees MagicNumber.
                Volatile.Write(ref header->Magic, SharedHeader.MagicNumber);
                return true;
            }

            // Loser path: wait for winner to publish the final magic value. Bounded spin —
            // initialization is normally microseconds, so even a generous timeout is fine.
            var sw = Stopwatch.StartNew();
            uint observed;
            while (true)
            {
                observed = Volatile.Read(ref header->Magic);
                if (observed == SharedHeader.MagicNumber)
                    break;
                if (observed != SharedHeader.MagicInitializing)
                {
                    if (observed == 0x48504D53)
                    {
                        throw new InvalidDataException(
                            $"Memory region '{_name}' uses the 2.x format. Stop all 2.x processes, " +
                            "remove the old region, and recreate it with InterprocessMemory 3.0.");
                    }

                    throw new InvalidDataException(
                        $"Memory region '{_name}' has an invalid version 3 header (magic=0x{observed:X8}).");
                }
                if (sw.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException(
                        "Timed out waiting for shared memory to be initialized by another process");
                Thread.SpinWait(100);
            }

            if (header->Version != SharedHeader.FormatVersion)
                throw new InvalidDataException(
                    $"Memory region '{_name}' uses format version {header->Version}; version 3 is required. " +
                    "Stop all 2.x processes, remove the old region, and recreate it.");

            if (header->RegionKind != (int)_regionKind)
                throw new InvalidDataException(
                    $"Memory region '{_name}' contains region kind {(RegionKind)header->RegionKind}, " +
                    $"not the requested {_regionKind}.");

            long declaredCapacity = header->Capacity;
            if (declaredCapacity <= 0)
                throw new InvalidDataException(
                    $"Memory region '{_name}' declares invalid capacity {declaredCapacity}.");

            long expectedMappingSize;
            try
            {
                expectedMappingSize = checked(HeaderSize + declaredCapacity);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException(
                    $"Memory region '{_name}' declares a capacity that exceeds the supported range.",
                    ex);
            }

            long mappedCapacity = _accessor!.Capacity;
            bool isWindowsNamedRegion =
                OperatingSystem.IsWindows() && string.IsNullOrEmpty(_options.FilePath);
            bool mappingSizeIsInvalid = isWindowsNamedRegion
                ? mappedCapacity < expectedMappingSize
                : mappedCapacity != expectedMappingSize;
            if (mappingSizeIsInvalid)
                throw new InvalidDataException(
                    $"Memory region '{_name}' declares {declaredCapacity} payload bytes, but its " +
                    $"mapping contains {mappedCapacity - HeaderSize} payload bytes.");

            if (!_createOrOpen)
                _capacity = declaredCapacity;
            else if (declaredCapacity != _capacity)
                throw new InvalidOperationException(
                    $"Capacity mismatch: expected {_capacity}, found {declaredCapacity}");

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte* GetDataPtr() => _basePtr + HeaderSize;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ValidateOffset(long offset, int length)
        {
            // Explicit negative checks are required before the ulong cast:
            // (ulong)(-1) + (ulong)10 wraps around to 9 in unchecked arithmetic,
            // so a single ulong comparison would silently pass a negative offset.
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

            // Use SIMD when length is at least one vector. On modern x86-64, unaligned SIMD
            // via ReadUnaligned/WriteUnaligned has essentially no penalty, so gating on alignment
            // (as the previous IsAligned check did) only hurt small writes by forcing the scalar fallback.
            if (_options.EnableSimd && source.Length >= Vector<byte>.Count)
            {
                WriteSimd(source, destPtr);
            }
            else
            {
                source.CopyTo(new Span<byte>(destPtr, source.Length));
            }

            // Stats are opt-in: under heavy concurrent access the LOCK-prefixed atomics here
            // become a cache-line contention point even though each call is "only" ~10ns.
            // Read-heavy workloads with stats disabled see 20-40% throughput gain. Default-on
            // preserves backward compatibility for callers that consume GetStatistics().
            if (_statsEnabled)
            {
                Interlocked.Increment(ref _totalWrites);
                Interlocked.Add(ref _totalBytesWritten, source.Length);
            }

            if (_options.EnableEvents)
            {
                try
                {
                    OnDataWritten?.Invoke(this, new MemoryRegionEventArgs
                    {
                        EventType = MemoryRegionEventType.DataWritten,
                        BytesAffected = source.Length,
                        Offset = offset
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Event handler threw an exception during OnDataWritten");
                }
            }

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

            // Same rationale as Write — alignment gating was costing performance for small reads
            // without buying anything on modern hardware that handles unaligned SIMD natively.
            if (_options.EnableSimd && destination.Length >= Vector<byte>.Count)
            {
                ReadSimd(destination, srcPtr);
            }
            else
            {
                new ReadOnlySpan<byte>(srcPtr, destination.Length).CopyTo(destination);
            }

            // Stats opt-in (same rationale as Write — see EnableStatistics doc on
            // MemoryRegionOptions). Reader-heavy code paths benefit most from disabling.
            if (_statsEnabled)
            {
                Interlocked.Increment(ref _totalReads);
                Interlocked.Add(ref _totalBytesRead, destination.Length);
            }

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

        // IsAligned helper was removed in favor of unconditional SIMD for length >= Vector<byte>.Count.
        // _options.Alignment is still validated as a power of 2 for forward compatibility and
        // for consumers that may use it for their own offset calculations.

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
            TimeoutHelper.Validate(timeout, nameof(timeout));

            var header = (SharedHeader*)_basePtr;
            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();
            bool orphanCheckDone = false;
            bool orphanCheckNearTimeout = false;

            while (true)
            {
                if (Interlocked.CompareExchange(ref header->WriterLockState, 1, 0) == 0)
                {
                    bool success = false;
                    try
                    {
                        // Record lock ownership for orphan detection. StartTime defeats PID-reuse
                        // attacks on the orphan check (see IsWriteLockOrphaned).
                        header->LockOwnerProcessId = Environment.ProcessId;
                        header->LockOwnerThreadId = Environment.CurrentManagedThreadId;
                        header->LockOwnerProcessStartTime = s_processStartTimeBinary;
                        header->LockAcquiredTimestamp = Stopwatch.GetTimestamp();
                        Thread.MemoryBarrier();

                        var readerSpinner = new SpinWait();
                        while (Volatile.Read(ref header->ReaderCount) > 0)
                        {
                            if (TimeoutHelper.HasExpired(sw, timeout))
                            {
                                return false; // Will release lock in finally
                            }

                            readerSpinner.SpinOnce();
                        }

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
                            header->LockOwnerProcessStartTime = 0;
                            header->LockAcquiredTimestamp = 0;
                            Interlocked.Exchange(ref header->WriterLockState, 0);
                        }
                    }
                }

                if (TimeoutHelper.HasExpired(sw, timeout))
                    return false;

                if (_options.EnableOrphanLockDetection)
                {
                    // Check on first CAS failure; re-check when nearing timeout (≥75% elapsed)
                    // so a lock that becomes orphaned mid-wait is still recovered before giving up.
                    bool nearTimeout = TimeoutHelper.IsNearExpiry(sw, timeout, 0.75);

                    if (!orphanCheckDone || (nearTimeout && !orphanCheckNearTimeout))
                    {
                        if (!orphanCheckDone)
                            orphanCheckDone = true;
                        else
                            orphanCheckNearTimeout = true;

                        if (IsWriteLockOrphaned())
                        {
                            _logger?.LogWarning("Detected orphan write lock, attempting recovery");
                            TryForceReleaseWriteLock();
                            continue;
                        }
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

            int currentPid = Environment.ProcessId;
            long currentThreadId = Environment.CurrentManagedThreadId;
            int ownerPid = Volatile.Read(ref header->LockOwnerProcessId);
            long ownerThreadId = Volatile.Read(ref header->LockOwnerThreadId);
            if (ownerPid != currentPid || ownerThreadId != currentThreadId)
            {
                _logger?.LogWarning(
                    "ReleaseWriteLock called from PID {Pid}/thread {ThreadId} but lock owner is PID {OwnerPid}/thread {OwnerThreadId} — ignored",
                    currentPid, currentThreadId, ownerPid, ownerThreadId);
                return;
            }

            int prev = Interlocked.CompareExchange(ref header->LockOwnerProcessId, 0, currentPid);
            if (prev != currentPid)
            {
                _logger?.LogWarning(
                    "ReleaseWriteLock called from PID {Pid} but lock owner is {OwnerPid} — ignored",
                    currentPid, prev);
                return;
            }

            header->LockOwnerThreadId = 0;
            header->LockOwnerProcessStartTime = 0;
            header->LockAcquiredTimestamp = 0;

            Thread.MemoryBarrier();
            Volatile.Write(ref header->WriterLockState, 0);

            _logger?.LogTrace("Write lock released");
        }

        /// <inheritdoc/>
        public bool TryAcquireReadLock(TimeSpan timeout)
        {
            ThrowIfDisposed();
            TimeoutHelper.Validate(timeout, nameof(timeout));

            var header = (SharedHeader*)_basePtr;
            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();

            while (true)
            {
                // Fast path: peek the writer flag without any atomic. If a writer is active,
                // wait — touching ReaderCount unnecessarily would create cache-line traffic on
                // the reader-side line and prolong the writer's release-then-drain phase.
                int writerState = Volatile.Read(ref header->WriterLockState);
                if (writerState != 0)
                {
                    if (TimeoutHelper.HasExpired(sw, timeout))
                        return false;
                    spinner.SpinOnce();
                    continue;
                }

                // Optimistic claim: unconditional Interlocked.Increment instead of the previous
                // read-CAS-recheck dance. Two wins under reader contention:
                //   1. No CAS retry loop when N readers race — every one of them succeeds on
                //      the first atomic (`lock inc` is a single µop on x86 vs cmpxchg).
                //   2. Cleaner code path. The brief window where we "claim" the reader slot
                //      before re-checking the writer is identical to the old code's CAS-then-
                //      recheck window — no new race introduced.
                Interlocked.Increment(ref header->ReaderCount);
                if (Volatile.Read(ref header->WriterLockState) == 0)
                    return true;

                // A writer acquired between our reader check and our increment. Roll back.
                // The writer's drain loop will briefly see ReaderCount > 0 and spin once or
                // twice extra — same penalty as the previous design's CAS-rollback path.
                Interlocked.Decrement(ref header->ReaderCount);

                if (TimeoutHelper.HasExpired(sw, timeout))
                    return false;

                spinner.SpinOnce();
            }
        }

        /// <inheritdoc/>
        public void ReleaseReadLock()
        {
            ThrowIfDisposed();

            var header = (SharedHeader*)_basePtr;
            while (true)
            {
                int current = Volatile.Read(ref header->ReaderCount);
                if (current <= 0)
                {
                    _logger?.LogWarning(
                        "ReleaseReadLock called when reader count is {ReaderCount} — ignored",
                        current);
                    return;
                }

                if (Interlocked.CompareExchange(ref header->ReaderCount, current - 1, current) == current)
                    return;
            }
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

                // PID-reuse defense: even when a process with this PID exists, it might be an
                // unrelated process that the OS recycled the PID for after the real owner died.
                // Compare the captured StartTime; mismatch ⇒ impostor ⇒ orphan.
                long storedStartTime = header->LockOwnerProcessStartTime;
                if (storedStartTime != 0)
                {
                    try
                    {
                        long currentStartTime = process.StartTime.ToBinary();
                        if (currentStartTime != storedStartTime)
                        {
                            _logger?.LogWarning(
                                "Lock owner PID {Pid} still exists but its StartTime differs (orphan from PID reuse)",
                                ownerPid);
                            return true;
                        }
                    }
                    catch
                    {
                        // StartTime can throw under restricted permissions (e.g., Linux container
                        // without /proc, Windows ACL). Fall through to timestamp-based detection
                        // — degraded but no worse than pre-feature behavior.
                    }
                }
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

            var header = (SharedHeader*)_basePtr;

            // Snapshot the owner PID before orphan check to prevent TOCTOU race
            int orphanPid = Volatile.Read(ref header->LockOwnerProcessId);
            if (orphanPid == 0)
                return false;

            if (!IsWriteLockOrphaned())
                return false;

            // Verify the same process still holds the lock (prevent releasing a valid lock
            // that was acquired by a different process between our check and this point)
            if (Interlocked.CompareExchange(ref header->LockOwnerProcessId, 0, orphanPid) != orphanPid)
                return false; // Lock owner changed, another process took it — do not release

            _logger?.LogWarning("Force releasing orphan lock from process {ProcessId}", orphanPid);

            header->LockOwnerThreadId = 0;
            header->LockOwnerProcessStartTime = 0;
            header->LockAcquiredTimestamp = 0;
            Thread.MemoryBarrier();
            Volatile.Write(ref header->WriterLockState, 0);

            if (_options.EnableEvents)
            {
                try
                {
                    OnOrphanLockDetected?.Invoke(this, new MemoryRegionEventArgs
                    {
                        EventType = MemoryRegionEventType.OrphanLockDetected
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Event handler threw an exception during OnOrphanLockDetected");
                }
            }

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
                return !_options.EnableChecksumVerification;

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

        /// <inheritdoc/>
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
                throw new ObjectDisposedException(nameof(MemoryRegion));
        }

        /// <summary>
        /// Releases all resources used by this buffer
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _logger?.LogDebug("Disposing shared buffer '{Name}'", _name);
            Cleanup(disposing: true);
            GC.SuppressFinalize(this);
        }

        private void Cleanup(bool disposing = true)
        {
            // We always release the unmanaged pointer and dispose the OS handles, regardless of
            // disposing — those are unmanaged resources and that's exactly what a finalizer must
            // reclaim. What we skip from the finalizer is anything touching managed state with
            // unpredictable lifetime: the logger (may itself be finalized), File.Delete (heavy
            // syscall path during shutdown), and any code that allocates.
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
                if (disposing)
                    _logger?.LogWarning(ex, "Failed to release memory pointer during cleanup");
            }

            try
            {
                _accessor?.Dispose();
                _accessor = null;
            }
            catch (Exception ex)
            {
                if (disposing)
                    _logger?.LogWarning(ex, "Failed to dispose accessor during cleanup");
            }

            try
            {
                _mmf?.Dispose();
                _mmf = null;
            }
            catch (Exception ex)
            {
                if (disposing)
                    _logger?.LogWarning(ex, "Failed to dispose memory-mapped file during cleanup");
            }

            try
            {
                // Only set if the MMF didn't take ownership (i.e., construction aborted partway).
                _backingFile?.Dispose();
                _backingFile = null;
            }
            catch (Exception ex)
            {
                if (disposing)
                    _logger?.LogWarning(ex, "Failed to dispose backing file during cleanup");
            }

            // Do not unlink a successfully initialized /dev/shm region on normal Dispose:
            // existing mappings would keep working while later openers get a brand-new file,
            // splitting one logical name into two independent regions. Only remove files that
            // this instance sized but failed to initialize.
            if (disposing)
            {
                bool constructionFailed = !_initializedSuccessfully;
                bool shouldUnlink = _backingFilePath != null && constructionFailed && _createdBackingFile;
                if (shouldUnlink)
                {
                    try
                    {
                        if (File.Exists(_backingFilePath!))
                            File.Delete(_backingFilePath!);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Could not unlink /dev/shm file '{Path}' during cleanup", _backingFilePath);
                    }
                }
                if (constructionFailed)
                    _backingFilePath = null;
                _createdBackingFile = false;
            }
        }

        /// <summary>
        /// Releases unmanaged resources if Dispose was not called. Touches only unmanaged
        /// state — see <see cref="Cleanup"/> for the disposing-flag rationale.
        /// </summary>
        ~MemoryRegion()
        {
            Cleanup(disposing: false);
        }
    }
}
