using System;
using System.Collections.Generic;
using System.Text;

namespace FlashHttp.Server;
internal class HandlerSet
{
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnGetHandlers = [];
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnPostHandlers = [];
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnPutHandlers = [];
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnDeleteHandlers = [];
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnHeadHandlers = [];
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnPatchHandlers = [];
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnOptionsHandlers = [];

    internal async Task HandleAsync(FlashHttpRequest request, FlashHttpResponse response, CancellationToken cancellationToken)
    {
        Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>>? _handlers = null;

        switch (request.Method) { 
            case HttpMethodsEnum.Get:
                _handlers = OnGetHandlers;
                break;
            case HttpMethodsEnum.Post:
                _handlers = OnPostHandlers;
                break;
            case HttpMethodsEnum.Put:
                _handlers = OnPutHandlers;
                break;
            case HttpMethodsEnum.Delete:
                _handlers = OnDeleteHandlers;
                break;
            case HttpMethodsEnum.Head:
                _handlers = OnHeadHandlers;
                break;
            case HttpMethodsEnum.Patch:
                _handlers = OnPatchHandlers;
                break;
            case HttpMethodsEnum.Options:
                _handlers = OnOptionsHandlers;
                break;
        }

        if (_handlers != null && _handlers.TryGetValue(request.Path, out var handler))
        {
            handler(request, response);
        }
        else
        {
            response.StatusCode = 404;
            response.ReasonPhrase = "Not Found";
            response.Body = Encoding.UTF8.GetBytes("Not Found");
        }
    }
}
