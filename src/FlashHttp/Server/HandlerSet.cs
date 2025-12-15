using FlashHttp.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlashHttp.Server;

/// <summary>
/// Central registry for HTTP route handlers.
/// Supports both synchronous and asynchronous handlers while remaining
/// backwards compatible with the original sync-only design.
/// </summary>
public sealed class HandlerSet
{
    // Delegate cho async handlers 
    public delegate ValueTask FlashRequestAsyncDelegate(
        FlashHttpRequest request,
        FlashHttpResponse response,
        CancellationToken cancellationToken);

    public readonly Dictionary<string, FlashRequestAsyncDelegate> OnGetHandlers = [];
    public readonly Dictionary<string, FlashRequestAsyncDelegate> OnPostHandlers = [];
    public readonly Dictionary<string, FlashRequestAsyncDelegate> OnPutHandlers = [];
    public readonly Dictionary<string, FlashRequestAsyncDelegate> OnDeleteHandlers = [];
    public readonly Dictionary<string, FlashRequestAsyncDelegate> OnHeadHandlers = [];
    public readonly Dictionary<string, FlashRequestAsyncDelegate> OnPatchHandlers = [];
    public readonly Dictionary<string, FlashRequestAsyncDelegate> OnOptionsHandlers = [];

    #region Registration helpers

    public void Register(HttpMethodsEnum method, string path, FlashRequestAsyncDelegate handler)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(handler);

        var dict = GetAsyncDictionary(method);
        dict[path] = handler;
    }

    private Dictionary<string, FlashRequestAsyncDelegate> GetAsyncDictionary(HttpMethodsEnum method)
        => method switch
        {
            HttpMethodsEnum.Get => OnGetHandlers,
            HttpMethodsEnum.Post => OnPostHandlers,
            HttpMethodsEnum.Put => OnPutHandlers,
            HttpMethodsEnum.Delete => OnDeleteHandlers,
            HttpMethodsEnum.Head => OnHeadHandlers,
            HttpMethodsEnum.Patch => OnPatchHandlers,
            HttpMethodsEnum.Options => OnOptionsHandlers,
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported HTTP method")
        };

    #endregion

    #region Dispatch

    public ValueTask HandleAsync(FlashHttpRequest request, FlashHttpResponse response, CancellationToken cancellationToken)
    {
        Dictionary<string, FlashRequestAsyncDelegate>? asyncHandlers = null;

        switch (request.Method)
        {
            case HttpMethodsEnum.Get:
                asyncHandlers = OnGetHandlers;
                break;
            case HttpMethodsEnum.Post:
                asyncHandlers = OnPostHandlers;
                break;
            case HttpMethodsEnum.Put:
                asyncHandlers = OnPutHandlers;
                break;
            case HttpMethodsEnum.Delete:
                asyncHandlers = OnDeleteHandlers;
                break;
            case HttpMethodsEnum.Head:
                asyncHandlers = OnHeadHandlers;
                break;
            case HttpMethodsEnum.Patch:
                asyncHandlers = OnPatchHandlers;
                break;
            case HttpMethodsEnum.Options:
                asyncHandlers = OnOptionsHandlers;
                break;
        }

        if (asyncHandlers != null && asyncHandlers.TryGetValue(request.Path, out var asyncHandler))
        {
            return asyncHandler(request, response, cancellationToken);
        }

        SetNotFound(response);
        return ValueTask.CompletedTask;
    }

    private static void SetNotFound(FlashHttpResponse response)
    {
        response.StatusCode = 404;
        response.ReasonPhrase = "Not Found";
        response.Body = Encoding.UTF8.GetBytes("Not Found");
    }

    #endregion
}
