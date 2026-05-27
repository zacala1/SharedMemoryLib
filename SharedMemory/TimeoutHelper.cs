using System;
using System.Diagnostics;
using System.Threading;

namespace SharedMemory
{
    internal static class TimeoutHelper
    {
        public static void Validate(TimeSpan timeout, string parameterName)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Timeout must be non-negative or Timeout.InfiniteTimeSpan.");
            }
        }

        public static bool HasExpired(Stopwatch stopwatch, TimeSpan timeout)
        {
            return timeout != Timeout.InfiniteTimeSpan && stopwatch.Elapsed > timeout;
        }

        public static bool IsNearExpiry(Stopwatch stopwatch, TimeSpan timeout, double fraction)
        {
            return timeout != Timeout.InfiniteTimeSpan
                && timeout > TimeSpan.Zero
                && stopwatch.Elapsed.TotalMilliseconds >= timeout.TotalMilliseconds * fraction;
        }
    }
}
