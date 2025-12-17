using FlashHttp.Abstractions;
using Microsoft.Extensions.ObjectPool;

namespace FlashHttp.Server;

internal sealed class FlashHttpRequestPooledObjectPolicy : PooledObjectPolicy<FlashHttpRequest>
{
    public override FlashHttpRequest Create()
    {
        // Pre-allocate a small header list; it will grow if needed.
        return new FlashHttpRequest();
    }

    public override bool Return(FlashHttpRequest request)
    {
        // Reset EVERYTHING to avoid leaking data between requests.
        request.Reset();

        return true;
    }
}
