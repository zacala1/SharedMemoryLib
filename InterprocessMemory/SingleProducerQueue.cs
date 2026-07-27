using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace InterprocessMemory
{
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    internal struct SingleProducerQueueHeader
    {
        [FieldOffset(0)] public long WriteSequence;
        [FieldOffset(64)] public long ReadSequence;
        [FieldOffset(128)] public int Capacity;
        [FieldOffset(132)] public int ElementSize;
        [FieldOffset(136)] public int Version;
        [FieldOffset(144)] public long Magic;
        [FieldOffset(152)] public ulong FingerprintLow;
        [FieldOffset(160)] public ulong FingerprintHigh;
    }

    /// <summary>
    /// Fixed-size queue for exactly one producer and one consumer, which may live in separate processes.
    /// </summary>
    public sealed unsafe class SingleProducerQueue<T> : IDisposable where T : unmanaged
    {
        private const int HeaderSize = 192;
        private const int FormatVersion = 3;
        private const long HeaderMagic = 0x5150534D504953;

        private readonly IMemoryRegion _region;
        private readonly MemoryHandle _memoryHandle;
        private readonly SingleProducerQueueHeader* _header;
        private readonly byte* _data;
        private readonly int _elementSize;
        private readonly TypeLayoutFingerprint _fingerprint;
        private int _capacity;
        private int _capacityMask;
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

        public static SingleProducerQueue<T> CreateOrOpen(string name, int capacity) =>
            new(name, capacity, createOrOpen: true);

        public static SingleProducerQueue<T> OpenExisting(string name) =>
            new(name, capacity: null, createOrOpen: false);

        private SingleProducerQueue(string name, int? capacity, bool createOrOpen)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty.", nameof(name));
            if (capacity is <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _elementSize = Unsafe.SizeOf<T>();
            _fingerprint = TypeLayoutFingerprint.Create<T>();

            if (createOrOpen)
            {
                _capacity = RoundUpToPowerOf2(capacity!.Value);
                _capacityMask = _capacity - 1;
                long regionCapacity = checked(HeaderSize + (long)_capacity * _elementSize);
                if (regionCapacity > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(capacity), "The queue exceeds the supported region size.");
                _region = MemoryRegion.CreateOrOpen(
                    name, regionCapacity, options: null, RegionKind.SingleProducerQueue);
            }
            else
            {
                _region = MemoryRegion.OpenExisting(
                    name, options: null, RegionKind.SingleProducerQueue);
            }

            try
            {
                var memory = _region.GetMemory(0, checked((int)_region.Capacity));
                _memoryHandle = memory.Pin();
                _header = (SingleProducerQueueHeader*)_memoryHandle.Pointer;
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
            _header->Version = FormatVersion;
            _header->FingerprintLow = _fingerprint.Low;
            _header->FingerprintHigh = _fingerprint.High;
            Thread.MemoryBarrier();
            Volatile.Write(ref _header->Magic, HeaderMagic);
        }

        private void ValidateAndLoad(int? requestedCapacity)
        {
            WaitForHeader();
            int storedCapacity = _header->Capacity;
            if (_header->Version != FormatVersion ||
                storedCapacity <= 0 ||
                (storedCapacity & (storedCapacity - 1)) != 0 ||
                _header->ElementSize != _elementSize ||
                _header->FingerprintLow != _fingerprint.Low ||
                _header->FingerprintHigh != _fingerprint.High)
                throw new InvalidDataException("The queue has a different format or element type.");

            if (requestedCapacity.HasValue &&
                RoundUpToPowerOf2(requestedCapacity.Value) != storedCapacity)
                throw new InvalidOperationException(
                    $"Capacity mismatch: expected {RoundUpToPowerOf2(requestedCapacity.Value)}, found {storedCapacity}.");

            long expectedRegionSize = checked(HeaderSize + (long)storedCapacity * _elementSize);
            if (_region.Capacity != expectedRegionSize)
                throw new InvalidDataException("The queue capacity does not match its header.");

            _capacity = storedCapacity;
            _capacityMask = storedCapacity - 1;
        }

        private void WaitForHeader()
        {
            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref _header->Magic) != HeaderMagic)
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidDataException("Timed out waiting for the single-producer queue header.");
                Thread.SpinWait(100);
            }
        }

        public bool TryEnqueue(in T item)
        {
            ThrowIfDisposed();
            long write = _header->WriteSequence;
            long read = Volatile.Read(ref _header->ReadSequence);
            if (write - read >= _capacity)
                return false;

            byte* destination = _data + (write & _capacityMask) * _elementSize;
            Unsafe.WriteUnaligned(destination, item);
            Volatile.Write(ref _header->WriteSequence, write + 1);
            return true;
        }

        public bool TryDequeue(out T item)
        {
            ThrowIfDisposed();
            long write = Volatile.Read(ref _header->WriteSequence);
            long read = _header->ReadSequence;
            if (write == read)
            {
                item = default;
                return false;
            }

            byte* source = _data + (read & _capacityMask) * _elementSize;
            item = Unsafe.ReadUnaligned<T>(source);
            Volatile.Write(ref _header->ReadSequence, read + 1);
            return true;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(SingleProducerQueue<T>));
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
