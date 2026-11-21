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

        /// <summary>
        /// Pre-rendered <c>Allow</c> header value for every path registered through
        /// <see cref="With"/>, so a request that misses its method table needs one lookup to
        /// tell 405 from 404 instead of probing every verb.
        /// </summary>
        public readonly Dictionary<string, string> AllowByPath;

        /// <summary>
        /// Span-keyed view of <see cref="AllowByPath"/>: finds the registered string for a path
        /// that has not been turned into a string yet.
        /// </summary>
        public readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> PathLookup;

        public RouteTable()
        {
            Get = [];
            Post = [];
            Put = [];
            Delete = [];
            Head = [];
            Patch = [];
            Options = [];
            AllowByPath = new Dictionary<string, string>(StringComparer.Ordinal);
            PathLookup = AllowByPath.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        private RouteTable(RouteTable source, HttpMethodsEnum changed, Dictionary<string, FlashRequestAsyncDelegate> replacement, string changedPath)
        {
            Get = changed == HttpMethodsEnum.Get ? replacement : source.Get;
            Post = changed == HttpMethodsEnum.Post ? replacement : source.Post;
            Put = changed == HttpMethodsEnum.Put ? replacement : source.Put;
            Delete = changed == HttpMethodsEnum.Delete ? replacement : source.Delete;
            Head = changed == HttpMethodsEnum.Head ? replacement : source.Head;
            Patch = changed == HttpMethodsEnum.Patch ? replacement : source.Patch;
            Options = changed == HttpMethodsEnum.Options ? replacement : source.Options;

            AllowByPath = new Dictionary<string, string>(source.AllowByPath, StringComparer.Ordinal)
            {
                [changedPath] = BuildAllowHeader(changedPath)
            };
            PathLookup = AllowByPath.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        // Comma-separated method list (RFC 9110 §10.2.1). HEAD is implied by GET.
        private string BuildAllowHeader(string path)
        {
            var methods = new List<string>(7);
            bool hasGet = Get.ContainsKey(path);
            if (hasGet) methods.Add("GET");
            if (hasGet || Head.ContainsKey(path)) methods.Add("HEAD");
            if (Post.ContainsKey(path)) methods.Add("POST");
            if (Put.ContainsKey(path)) methods.Add("PUT");
            if (Patch.ContainsKey(path)) methods.Add("PATCH");
            if (Delete.ContainsKey(path)) methods.Add("DELETE");
            if (Options.ContainsKey(path)) methods.Add("OPTIONS");
            return string.Join(", ", methods);
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
            return new RouteTable(this, method, copy, path);
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

    /// <summary>
    /// Returns the registered path string equal to <paramref name="path"/> (for any method),
    /// or null when no route uses that path.
    /// </summary>
    internal string? FindRegisteredPath(ReadOnlySpan<char> path)
        => Volatile.Read(ref _routes).PathLookup.TryGetValue(path, out string? registered, out _)
            ? registered
            : null;

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
        if (routes.AllowByPath.TryGetValue(path, out var allowHeader))
        {
            SetMethodNotAllowed(context.Response, allowHeader);
        }
        else
        {
            SetNotFound(context.Response);
        }
        return ValueTask.CompletedTask;
    }

    // Shared, never mutated: unmatched requests (scanners, typos) must not allocate a body each.
    private static readonly byte[] NotFoundBody = "Not Found"u8.ToArray();
    private static readonly byte[] MethodNotAllowedBody = "Method Not Allowed"u8.ToArray();

    private static void SetNotFound(FlashHttpResponse response)
    {
        response.StatusCode = 404;
        response.ReasonPhrase = "Not Found";
        response.Body = NotFoundBody;
    }

    private static void SetMethodNotAllowed(FlashHttpResponse response, string allowHeader)
    {
        response.StatusCode = 405;
        response.ReasonPhrase = "Method Not Allowed";
        response.Body = MethodNotAllowedBody;
        // RFC 9110 §15.5.6: a 405 response MUST include an Allow header.
        response.Headers.Add(new HttpHeader("Allow", allowHeader));
    }

    #endregion
}
