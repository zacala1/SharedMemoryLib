namespace InterprocessMemory
{
    /// <summary>
    /// Identifies the logical data structure stored inside a version 3 memory region.
    /// </summary>
    internal enum RegionKind
    {
        RawMemory = 1,
        StructuredMemory = 2,
        SharedArray = 3,
        SingleProducerQueue = 4,
        ConcurrentQueue = 5,
        SingleProducerByteStream = 6,
        ConcurrentMessageQueue = 7
    }
}
