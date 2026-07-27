using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace InterprocessMemory
{
    [StructLayout(LayoutKind.Explicit, Size = 320)]
    internal struct ConcurrentQueueHeader
    {
        [FieldOffset(0)] public long WriteSequence;
        [FieldOffset(64)] public long ReadSequence;
        [FieldOffset(128)] public int Capacity;
        [FieldOffset(132)] public int ElementSize;
        [FieldOffset(136)] public int SlotStride;
        [FieldOffset(140)] public int Version;
        [FieldOffset(144)] public long Magic;
        [FieldOffset(152)] public ulong FingerprintLow;
        [FieldOffset(160)] public ulong FingerprintHigh;
        [FieldOffset(192)] public long TotalEnqueues;
        [FieldOffset(200)] public long FailedEnqueues;
        [FieldOffset(256)] public long TotalDequeues;
        [FieldOffset(264)] public long FailedDequeues;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct ConcurrentQueueSlot
    {
        public long Sequence;
    }

    /// <summary>
    /// Fixed-size lock-free queue for multiple producers and multiple consumers across processes.
    /// </summary>
    public sealed unsafe class ConcurrentQueue<T> : IDisposable where T : unmanaged
    {
        private const int HeaderSize = 320;
        private const int SlotHeaderSize = 8;
        private const int FormatVersion = 3;
        private const long HeaderMagic = 0x5143504D504953;

        private readonly IMemoryRegion _region;
        private readonly MemoryHandle _memoryHandle;
        private readonly ConcurrentQueueHeader* _header;
        private readonly byte* _data;
        private readonly int _elementSize;
        private readonly TypeLayoutFingerprint _fingerprint;
        private readonly int _maxSpins;
        private readonly bool _statisticsEnabled;
        private int _capacity;
        private int _capacityMask;
        private int _slotStride;
        private int _disposed;

        public int Capacity => _capacity;

        public long ApproximateCount
        {
            get
            {
                ThrowIfDisposed();
                long write = Volatile.Read(ref _header->WriteSequence);
                long read = Volatile.Read(ref _header->ReadSequence);
                return Math.Clamp(write - read, 0, _capacity);
            }
        }

        public static ConcurrentQueue<T> CreateOrOpen(
            string name,
            int capacity,
            ConcurrentQueueOptions? options = null) =>
            new(name, capacity, options, createOrOpen: true);

        public static ConcurrentQueue<T> OpenExisting(
            string name,
            ConcurrentQueueOptions? options = null) =>
            new(name, capacity: null, options, createOrOpen: false);

        private ConcurrentQueue(
            string name,
            int? capacity,
            ConcurrentQueueOptions? options,
            bool createOrOpen)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty.", nameof(name));
            if (capacity is <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            options ??= new ConcurrentQueueOptions();
            options.Validate();
            _maxSpins = options.MaxSpins;
            _statisticsEnabled = options.EnableStatistics;
            _elementSize = Unsafe.SizeOf<T>();
            _fingerprint = TypeLayoutFingerprint.Create<T>();

            if (createOrOpen)
            {
                _capacity = RoundUpToPowerOf2(capacity!.Value);
                _capacityMask = _capacity - 1;
                _slotStride = RoundUpToMultiple(checked(SlotHeaderSize + _elementSize), 8);
                long regionCapacity = checked(HeaderSize + (long)_capacity * _slotStride);
                if (regionCapacity > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(capacity), "The queue exceeds the supported region size.");
                _region = MemoryRegion.CreateOrOpen(
                    name, regionCapacity, options: null, RegionKind.ConcurrentQueue);
            }
            else
            {
                _region = MemoryRegion.OpenExisting(
                    name, options: null, RegionKind.ConcurrentQueue);
            }

            try
            {
                var memory = _region.GetMemory(0, checked((int)_region.Capacity));
                _memoryHandle = memory.Pin();
                _header = (ConcurrentQueueHeader*)_memoryHandle.Pointer;
                _data = (byte*)_memoryHandle.Pointer + HeaderSize;

                if (createOrOpen && _region.IsOwner)
                    Initialize();
                else
                    ValidateAndLoad(createOrOpen ? capacity : null);
            }
            catch
            {
                try
                { _memoryHandle.Dispose(); }
                catch { }
                _region.Dispose();
                throw;
            }
        }

        private void Initialize()
        {
            _header->WriteSequence = 0;
            _header->ReadSequence = 0;
            _header->Capacity = _capacity;
            _header->ElementSize = _elementSize;
            _header->SlotStride = _slotStride;
            _header->Version = FormatVersion;
            _header->FingerprintLow = _fingerprint.Low;
            _header->FingerprintHigh = _fingerprint.High;
            _header->TotalEnqueues = 0;
            _header->FailedEnqueues = 0;
            _header->TotalDequeues = 0;
            _header->FailedDequeues = 0;

            for (int i = 0; i < _capacity; i++)
                GetSlot(i)->Sequence = i;

            Thread.MemoryBarrier();
            Volatile.Write(ref _header->Magic, HeaderMagic);
        }

        private void ValidateAndLoad(int? requestedCapacity)
        {
            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref _header->Magic) != HeaderMagic)
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidDataException("Timed out waiting for the concurrent-queue header.");
                Thread.SpinWait(100);
            }

            int storedCapacity = _header->Capacity;
            int storedStride = _header->SlotStride;
            if (_header->Version != FormatVersion ||
                storedCapacity <= 0 ||
                (storedCapacity & (storedCapacity - 1)) != 0 ||
                _header->ElementSize != _elementSize ||
                storedStride < SlotHeaderSize + _elementSize ||
                _header->FingerprintLow != _fingerprint.Low ||
                _header->FingerprintHigh != _fingerprint.High)
                throw new InvalidDataException("The queue has a different format or element type.");

            if (requestedCapacity.HasValue &&
                RoundUpToPowerOf2(requestedCapacity.Value) != storedCapacity)
                throw new InvalidOperationException(
                    $"Capacity mismatch: expected {RoundUpToPowerOf2(requestedCapacity.Value)}, found {storedCapacity}.");

            long expectedRegionSize = checked(HeaderSize + (long)storedCapacity * storedStride);
            if (_region.Capacity != expectedRegionSize)
                throw new InvalidDataException("The queue capacity does not match its header.");

            _capacity = storedCapacity;
            _capacityMask = storedCapacity - 1;
            _slotStride = storedStride;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ConcurrentQueueSlot* GetSlot(long index) =>
            (ConcurrentQueueSlot*)(_data + (index & _capacityMask) * _slotStride);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* GetSlotData(ConcurrentQueueSlot* slot) => (byte*)slot + SlotHeaderSize;

        public bool TryEnqueue(in T item)
        {
            ThrowIfDisposed();
            for (int spin = 0; spin < _maxSpins; spin++)
            {
                long write = Volatile.Read(ref _header->WriteSequence);
                ConcurrentQueueSlot* slot = GetSlot(write);
                long difference = Volatile.Read(ref slot->Sequence) - write;
                if (difference == 0)
                {
                    if (Interlocked.CompareExchange(
                            ref _header->WriteSequence, write + 1, write) == write)
                    {
                        Unsafe.WriteUnaligned(GetSlotData(slot), item);
                        Volatile.Write(ref slot->Sequence, write + 1);
                        if (_statisticsEnabled)
                            Interlocked.Increment(ref _header->TotalEnqueues);
                        return true;
                    }
                }
                else if (difference < 0)
                {
                    if (_statisticsEnabled)
                        Interlocked.Increment(ref _header->FailedEnqueues);
                    return false;
                }

                Thread.SpinWait(1 << Math.Min(spin, 10));
            }
            return false;
        }

        public bool TryDequeue(out T item)
        {
            ThrowIfDisposed();
            for (int spin = 0; spin < _maxSpins; spin++)
            {
                long read = Volatile.Read(ref _header->ReadSequence);
                ConcurrentQueueSlot* slot = GetSlot(read);
                long difference = Volatile.Read(ref slot->Sequence) - (read + 1);
                if (difference == 0)
                {
                    if (Interlocked.CompareExchange(
                            ref _header->ReadSequence, read + 1, read) == read)
                    {
                        item = Unsafe.ReadUnaligned<T>(GetSlotData(slot));
                        Volatile.Write(ref slot->Sequence, read + _capacity);
                        if (_statisticsEnabled)
                            Interlocked.Increment(ref _header->TotalDequeues);
                        return true;
                    }
                }
                else if (difference < 0)
                {
                    if (_statisticsEnabled)
                        Interlocked.Increment(ref _header->FailedDequeues);
                    item = default;
                    return false;
                }

                Thread.SpinWait(1 << Math.Min(spin, 10));
            }

            item = default;
            return false;
        }

        public bool TryEnqueue(
            in T item,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            TimeoutHelper.Validate(timeout, nameof(timeout));
            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();
            while (!TryEnqueue(in item))
            {
                if (cancellationToken.IsCancellationRequested || TimeoutHelper.HasExpired(sw, timeout))
                    return false;
                spinner.SpinOnce();
            }
            return true;
        }

        public bool TryDequeue(
            out T item,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            TimeoutHelper.Validate(timeout, nameof(timeout));
            var sw = Stopwatch.StartNew();
            var spinner = new SpinWait();
            while (!TryDequeue(out item))
            {
                if (cancellationToken.IsCancellationRequested || TimeoutHelper.HasExpired(sw, timeout))
                {
                    item = default;
                    return false;
                }
                spinner.SpinOnce();
            }
            return true;
        }

        public (long Enqueues, long Dequeues, long FailedEnqueues, long FailedDequeues) GetStatistics()
        {
            ThrowIfDisposed();
            return (
                Volatile.Read(ref _header->TotalEnqueues),
                Volatile.Read(ref _header->TotalDequeues),
                Volatile.Read(ref _header->FailedEnqueues),
                Volatile.Read(ref _header->FailedDequeues));
        }

        private static int RoundUpToPowerOf2(int value)
        {
            if (value > 1 << 30)
                throw new ArgumentOutOfRangeException(nameof(value));
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }

        private static int RoundUpToMultiple(int value, int multiple) =>
            checked((value + multiple - 1) / multiple * multiple);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ConcurrentQueue<T>));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _memoryHandle.Dispose();
            _region.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
