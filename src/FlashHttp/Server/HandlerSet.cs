using FlashHttp.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlashHttp.Server;

/// <summary>
/// Central registry for HTTP route handlers.
/// Registration is serialised by a lock and publishes a fresh, immutable route table
/// (copy-on-write), so dispatch on the hot path is lock-free.
/// </summary>
public sealed class HandlerSet
{
    public delegate ValueTask FlashRequestAsyncDelegate(
        IFlashHandlerContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Immutable snapshot of all route dictionaries. Replaced wholesale on every Register().
    /// </summary>
    private sealed class RouteTable
    {
        public readonly Dictionary<string, FlashRequestAsyncDelegate> Get;
        public readonly Dictionary<string, FlashRequestAsyncDelegate> Post;
        public readonly Dictionary<string, FlashRequestAsyncDelegate> Put;
        public readonly Dictionary<string, FlashRequestAsyncDelegate> Delete;
        public readonly Dictionary<string, FlashRequestAsyncDelegate> Head;
        public readonly Dictionary<string, FlashRequestAsyncDelegate> Patch;
        public readonly Dictionary<string, FlashRequestAsyncDelegate> Options;

        public RouteTable()
        {
            Get = [];
            Post = [];
            Put = [];
            Delete = [];
            Head = [];
            Patch = [];
            Options = [];
        }

        private RouteTable(RouteTable source, HttpMethodsEnum changed, Dictionary<string, FlashRequestAsyncDelegate> replacement)
        {
            Get = changed == HttpMethodsEnum.Get ? replacement : source.Get;
            Post = changed == HttpMethodsEnum.Post ? replacement : source.Post;
            Put = changed == HttpMethodsEnum.Put ? replacement : source.Put;
            Delete = changed == HttpMethodsEnum.Delete ? replacement : source.Delete;
            Head = changed == HttpMethodsEnum.Head ? replacement : source.Head;
            Patch = changed == HttpMethodsEnum.Patch ? replacement : source.Patch;
            Options = changed == HttpMethodsEnum.Options ? replacement : source.Options;
        }

        public Dictionary<string, FlashRequestAsyncDelegate>? For(HttpMethodsEnum method)
            => method switch
            {
                HttpMethodsEnum.Get => Get,
                HttpMethodsEnum.Post => Post,
                HttpMethodsEnum.Put => Put,
                HttpMethodsEnum.Delete => Delete,
                HttpMethodsEnum.Head => Head,
                HttpMethodsEnum.Patch => Patch,
                HttpMethodsEnum.Options => Options,
                _ => null
            };

        public RouteTable With(HttpMethodsEnum method, string path, FlashRequestAsyncDelegate handler)
        {
            var current = For(method) ?? throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported HTTP method");
            var copy = new Dictionary<string, FlashRequestAsyncDelegate>(current)
            {
                [path] = handler
            };
            return new RouteTable(this, method, copy);
        }
    }

    private readonly object _lock = new object();
    private RouteTable _routes = new();

    // Public access kept for backward compatibility. The dictionaries are snapshots: mutating
    // them directly is not thread-safe and may be lost on the next Register() call.
    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnGetHandlers => Volatile.Read(ref _routes).Get;

    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnPostHandlers => Volatile.Read(ref _routes).Post;

    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnPutHandlers => Volatile.Read(ref _routes).Put;

    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnDeleteHandlers => Volatile.Read(ref _routes).Delete;

    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnHeadHandlers => Volatile.Read(ref _routes).Head;

    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnPatchHandlers => Volatile.Read(ref _routes).Patch;

    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnOptionsHandlers => Volatile.Read(ref _routes).Options;

    #region Registration helpers

    public void Register(HttpMethodsEnum method, string path, FlashRequestAsyncDelegate handler)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            var next = _routes.With(method, path, handler);
            Volatile.Write(ref _routes, next);
        }
    }

    #endregion

    #region Dispatch

    public ValueTask HandleAsync(IFlashHandlerContext context, CancellationToken cancellationToken)
    {
        var routes = Volatile.Read(ref _routes);
        var method = context.Request.Method;
        var path = context.Request.Path;

        FlashRequestAsyncDelegate? handler = null;
        routes.For(method)?.TryGetValue(path, out handler);

        // RFC 9110 §9.3.2: HEAD is GET without a body. Fall back to the GET handler so routes
        // do not need to register HEAD explicitly; the connection strips the body.
        if (handler is null && method == HttpMethodsEnum.Head)
        {
            routes.Get.TryGetValue(path, out handler);
        }

        if (handler != null)
        {
            return handler(context, cancellationToken);
        }

        // If the request method has no handler for this path, check whether some
        // *other* method does; in which case the right response is 405, not 404.
        var allowHeader = BuildAllowHeaderForPath(routes, path);
        if (allowHeader is not null)
        {
            SetMethodNotAllowed(context.Response, allowHeader);
        }
        else
        {
            SetNotFound(context.Response);
        }
        return ValueTask.CompletedTask;
    }

    // Returns a comma-separated method list (RFC 9110 §10.2.1) or null if no method is
    // registered for the given path. HEAD is implied by GET.
    private static string? BuildAllowHeaderForPath(RouteTable routes, string path)
    {
        StringBuilder? sb = null;
        bool hasGet = routes.Get.ContainsKey(path);
        if (hasGet)
        {
            Append(ref sb, "GET");
        }
        if (hasGet || routes.Head.ContainsKey(path))
        {
            Append(ref sb, "HEAD");
        }
        AppendIfRegistered(routes.Post, path, ref sb, "POST");
        AppendIfRegistered(routes.Put, path, ref sb, "PUT");
        AppendIfRegistered(routes.Patch, path, ref sb, "PATCH");
        AppendIfRegistered(routes.Delete, path, ref sb, "DELETE");
        AppendIfRegistered(routes.Options, path, ref sb, "OPTIONS");
        return sb?.ToString();
    }

    private static void AppendIfRegistered(
        Dictionary<string, FlashRequestAsyncDelegate> dict,
        string path,
        ref StringBuilder? sb,
        string methodName)
    {
        if (!dict.ContainsKey(path)) return;
        Append(ref sb, methodName);
    }

    private static void Append(ref StringBuilder? sb, string methodName)
    {
        if (sb is null)
        {
            sb = new StringBuilder(methodName);
        }
        else
        {
            sb.Append(", ").Append(methodName);
        }
    }

    private static void SetNotFound(FlashHttpResponse response)
    {
        response.StatusCode = 404;
        response.ReasonPhrase = "Not Found";
        response.Body = Encoding.UTF8.GetBytes("Not Found");
    }

    private static void SetMethodNotAllowed(FlashHttpResponse response, string allowHeader)
    {
        response.StatusCode = 405;
        response.ReasonPhrase = "Method Not Allowed";
        response.Body = Encoding.UTF8.GetBytes("Method Not Allowed");
        // RFC 9110 §15.5.6: a 405 response MUST include an Allow header.
        response.Headers.Add(new HttpHeader("Allow", allowHeader));
    }

    #endregion
}
