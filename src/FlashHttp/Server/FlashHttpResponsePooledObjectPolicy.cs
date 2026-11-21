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
        Reset(obj);
        return true;
    }

    /// <summary>
    /// Puts a response back into its initial state. Also used between requests by a
    /// connection that keeps one response for its whole lifetime.
    /// </summary>
    internal static void Reset(FlashHttpResponse obj)
    {
        obj.StatusCode = 404;
        obj.ReasonPhrase = string.Empty;
        obj.Body = [];

        // A stream that fails to dispose must not leave the response half reset, nor abort
        // the connection cleanup this runs in.
        var bodyStream = obj.BodyStream;
        obj.BodyStream = null;
        try { bodyStream?.Dispose(); } catch { }

        obj.Headers.Clear();
    }
}
