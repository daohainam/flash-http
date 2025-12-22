using FlashHttp.Abstractions;
using Microsoft.Extensions.ObjectPool;

namespace FlashHttp.Server;

/// <summary>
/// IMPORTANT: FlashHttpResponse instances are pooled.
/// Handlers MUST NOT retain references to the response (or response.Headers) after returning.
/// </summary>
internal sealed class FlashHttpResponsePooledObjectPolicy : PooledObjectPolicy<FlashHttpResponse>
{
    public override FlashHttpResponse Create()
    {
        return new FlashHttpResponse
        {
            StatusCode = 404,
            ReasonPhrase = string.Empty,
            Body = []
        };
    }

    public override bool Return(FlashHttpResponse obj)
    {
        obj.StatusCode = 404;
        obj.ReasonPhrase = string.Empty;
        obj.Body = [];
        obj.Headers.Clear();
        return true;
    }
}
