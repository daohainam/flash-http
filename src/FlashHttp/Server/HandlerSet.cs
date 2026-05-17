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
/// Thread-safe for concurrent registration and dispatch.
/// </summary>
public sealed class HandlerSet
{
    // Delegate cho async handlers 
    public delegate ValueTask FlashRequestAsyncDelegate(
        IFlashHandlerContext context,
        CancellationToken cancellationToken);

    private readonly object _lock = new object();
    private readonly Dictionary<string, FlashRequestAsyncDelegate> _onGetHandlers = [];
    private readonly Dictionary<string, FlashRequestAsyncDelegate> _onPostHandlers = [];
    private readonly Dictionary<string, FlashRequestAsyncDelegate> _onPutHandlers = [];
    private readonly Dictionary<string, FlashRequestAsyncDelegate> _onDeleteHandlers = [];
    private readonly Dictionary<string, FlashRequestAsyncDelegate> _onHeadHandlers = [];
    private readonly Dictionary<string, FlashRequestAsyncDelegate> _onPatchHandlers = [];
    private readonly Dictionary<string, FlashRequestAsyncDelegate> _onOptionsHandlers = [];

    // Public readonly access for backward compatibility
    // WARNING: Direct modifications to these dictionaries bypass thread safety.
    // Use the Register() method for thread-safe handler registration.
    /// <summary>
    /// Gets the dictionary of GET handlers. For thread-safe registration, use Register() method.
    /// </summary>
    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnGetHandlers => _onGetHandlers;
    
    /// <summary>
    /// Gets the dictionary of POST handlers. For thread-safe registration, use Register() method.
    /// </summary>
    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnPostHandlers => _onPostHandlers;
    
    /// <summary>
    /// Gets the dictionary of PUT handlers. For thread-safe registration, use Register() method.
    /// </summary>
    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnPutHandlers => _onPutHandlers;
    
    /// <summary>
    /// Gets the dictionary of DELETE handlers. For thread-safe registration, use Register() method.
    /// </summary>
    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnDeleteHandlers => _onDeleteHandlers;
    
    /// <summary>
    /// Gets the dictionary of HEAD handlers. For thread-safe registration, use Register() method.
    /// </summary>
    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnHeadHandlers => _onHeadHandlers;
    
    /// <summary>
    /// Gets the dictionary of PATCH handlers. For thread-safe registration, use Register() method.
    /// </summary>
    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnPatchHandlers => _onPatchHandlers;
    
    /// <summary>
    /// Gets the dictionary of OPTIONS handlers. For thread-safe registration, use Register() method.
    /// </summary>
    [Obsolete("Direct dictionary access bypasses thread safety. Use Register() method instead.")]
    public Dictionary<string, FlashRequestAsyncDelegate> OnOptionsHandlers => _onOptionsHandlers;

    #region Registration helpers

    public void Register(HttpMethodsEnum method, string path, FlashRequestAsyncDelegate handler)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            var dict = GetAsyncDictionary(method);
            dict[path] = handler;
        }
    }

    private Dictionary<string, FlashRequestAsyncDelegate> GetAsyncDictionary(HttpMethodsEnum method)
        => method switch
        {
            HttpMethodsEnum.Get => _onGetHandlers,
            HttpMethodsEnum.Post => _onPostHandlers,
            HttpMethodsEnum.Put => _onPutHandlers,
            HttpMethodsEnum.Delete => _onDeleteHandlers,
            HttpMethodsEnum.Head => _onHeadHandlers,
            HttpMethodsEnum.Patch => _onPatchHandlers,
            HttpMethodsEnum.Options => _onOptionsHandlers,
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported HTTP method")
        };

    #endregion

    #region Dispatch

    public ValueTask HandleAsync(IFlashHandlerContext context, CancellationToken cancellationToken)
    {
        FlashRequestAsyncDelegate? asyncHandler = null;
        string? allowHeader = null;

        lock (_lock)
        {
            Dictionary<string, FlashRequestAsyncDelegate>? asyncHandlers = context.Request.Method switch
            {
                HttpMethodsEnum.Get => _onGetHandlers,
                HttpMethodsEnum.Post => _onPostHandlers,
                HttpMethodsEnum.Put => _onPutHandlers,
                HttpMethodsEnum.Delete => _onDeleteHandlers,
                HttpMethodsEnum.Head => _onHeadHandlers,
                HttpMethodsEnum.Patch => _onPatchHandlers,
                HttpMethodsEnum.Options => _onOptionsHandlers,
                _ => null
            };

            asyncHandlers?.TryGetValue(context.Request.Path, out asyncHandler);

            // If the request method has no handler for this path, check whether some
            // *other* method does — in which case the right response is 405, not 404.
            if (asyncHandler is null)
            {
                allowHeader = BuildAllowHeaderForPath(context.Request.Path);
            }
        }

        if (asyncHandler != null)
        {
            return asyncHandler(context, cancellationToken);
        }

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

    // Caller must hold _lock. Returns a comma-separated method list (RFC 7231 §7.4.1)
    // or null if no method is registered for the given path.
    private string? BuildAllowHeaderForPath(string path)
    {
        StringBuilder? sb = null;
        AppendIfRegistered(_onGetHandlers, path, ref sb, "GET");
        AppendIfRegistered(_onHeadHandlers, path, ref sb, "HEAD");
        AppendIfRegistered(_onPostHandlers, path, ref sb, "POST");
        AppendIfRegistered(_onPutHandlers, path, ref sb, "PUT");
        AppendIfRegistered(_onPatchHandlers, path, ref sb, "PATCH");
        AppendIfRegistered(_onDeleteHandlers, path, ref sb, "DELETE");
        AppendIfRegistered(_onOptionsHandlers, path, ref sb, "OPTIONS");
        return sb?.ToString();
    }

    private static void AppendIfRegistered(
        Dictionary<string, FlashRequestAsyncDelegate> dict,
        string path,
        ref StringBuilder? sb,
        string methodName)
    {
        if (!dict.ContainsKey(path)) return;
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
        // RFC 7231 §6.5.5: a 405 response MUST include an Allow header.
        response.Headers.Add(new HttpHeader("Allow", allowHeader));
    }

    #endregion
}
