using FlashHttp.Abstractions;
using Microsoft.Extensions.ObjectPool;

namespace FlashHttp.Server;

internal sealed class FlashHttpRequestPooledObjectPolicy : PooledObjectPolicy<FlashHttpRequest>
{
    public override FlashHttpRequest Create()
    {
        var request = new FlashHttpRequest();

        // Give the request its header list once; the parser fills that list in place for
        // every request this object serves, so the list is pooled together with the request.
        request.Init(
            HttpMethodsEnum.Get,
            port: 80,
            path: "/",
            queryString: "",
            keepAliveRequested: true,
            contentLength: 0,
            contentType: "",
            isHttps: false,
            remoteAddress: null,
            remotePort: 0,
            httpVersion: HttpVersions.Http11,
            headers: new List<HttpHeader>(16),
            body: []);

        return request;
    }

    public override bool Return(FlashHttpRequest request)
    {
        // Reset EVERYTHING to avoid leaking data between requests.
        request.Reset();

        return true;
    }
}
