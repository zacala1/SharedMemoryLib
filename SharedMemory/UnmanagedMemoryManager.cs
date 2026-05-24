using System;
using System.Buffers;

namespace SharedMemory
{
    /// <summary>
    /// Wraps a raw unmanaged pointer in a <see cref="MemoryManager{T}"/> so it can be returned
    /// as a <see cref="Memory{T}"/> from <see cref="HighPerformanceSharedBuffer.GetMemory"/>.
    ///
    /// <para><b>Lifetime contract.</b> The wrapped pointer must outlive every <see cref="Memory{T}"/>
    /// or <see cref="Span{T}"/> derived from this manager. Disposing the owning buffer while a
    /// consumer still holds the Memory/Span is undefined behavior (use-after-free). This is the
    /// same contract as <see cref="System.IO.MemoryMappedFiles.MemoryMappedViewAccessor"/>.</para>
    ///
    /// <para><b>Pin/Unpin.</b> The unmanaged memory is, by definition, already pinned at a fixed
    /// virtual address — there is nothing to unpin, so <see cref="Unpin"/> is a no-op and
    /// <see cref="Pin(int)"/> simply hands back the offset pointer.</para>
    /// </summary>
    /// <typeparam name="T">Unmanaged element type</typeparam>
    internal sealed unsafe class UnmanagedMemoryManager<T> : MemoryManager<T> where T : unmanaged
    {
        private readonly T* _pointer;
        private readonly int _length;

        public UnmanagedMemoryManager(T* pointer, int length)
        {
            _pointer = pointer;
            _length = length;
        }

        public override Span<T> GetSpan() => new(_pointer, _length);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if (elementIndex < 0 || elementIndex >= _length)
                throw new ArgumentOutOfRangeException(nameof(elementIndex));

            return new MemoryHandle(_pointer + elementIndex);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
}
