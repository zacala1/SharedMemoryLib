using System;

namespace InterprocessMemory
{
    /// <summary>Process-local tuning options for concurrent queues.</summary>
    public sealed class ConcurrentQueueOptions
    {
        public int MaxSpins { get; set; } = 100;

        public bool EnableStatistics { get; set; } = true;

        internal void Validate()
        {
            if (MaxSpins <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxSpins), "MaxSpins must be positive.");
        }
    }
}
