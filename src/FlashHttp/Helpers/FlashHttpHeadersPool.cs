using FlashHttp.Abstractions;
using System.Collections.Concurrent;

namespace FlashHttp.Helpers
{
    /// <summary>
    /// Simple pool for FlashHttpHeaders to avoid per-request object allocations.
    /// The pooled instance keeps its internal arrays rented from ArrayPool.
    /// </summary>
    internal static class FlashHttpHeadersPool
    {
        private static readonly ConcurrentQueue<FlashHttpHeaders> _pool = new();

        public static FlashHttpHeaders Rent()
        {
            if (_pool.TryDequeue(out var h))
            {
                h.Reset();
                return h;
            }

            return new FlashHttpHeaders();
        }

        public static void Return(FlashHttpHeaders headers)
        {
            headers.Reset();
            _pool.Enqueue(headers);
        }
    }
}
