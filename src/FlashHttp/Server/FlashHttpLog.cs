using FlashHttp.Abstractions;
using Microsoft.Extensions.Logging;
using System.Net;

namespace FlashHttp.Server;

/// <summary>
/// Source-generated log methods for the request path: no boxing or params array, and the
/// level check happens before any argument is formatted.
/// </summary>
internal static partial class FlashHttpLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Received HTTP request: {Method} {Path}")]
    public static partial void RequestReceived(ILogger logger, HttpMethodsEnum method, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Request rejected: {Result} -> {StatusCode} from {RemoteEndPoint}")]
    public static partial void RequestRejected(ILogger logger, FlashHttpParser.TryReadHttpRequestResults result, int statusCode, EndPoint? remoteEndPoint);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Request rejected: headers not received within {Timeout} from {RemoteEndPoint}")]
    public static partial void RequestHeadersTimedOut(ILogger logger, TimeSpan timeout, EndPoint? remoteEndPoint);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Unhandled exception while handling {Method} {Path}")]
    public static partial void UnhandledHandlerException(ILogger logger, Exception exception, HttpMethodsEnum method, string path);
}
