using FlashHttp.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using static FlashHttp.Server.HandlerSet;

namespace FlashHttp.Server;

internal partial class FlashHttpConnection
{
    private static readonly byte[] Http11Bytes = Encoding.ASCII.GetBytes("HTTP/1.1 ");
    private static readonly byte[] ContinueResponseBytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");

    private readonly IPEndPoint? _remoteEndPoint;
    private readonly IPEndPoint? _localEndPoint;
    private readonly Stream stream;
    private readonly bool isHttps;
    private readonly FlashRequestAsyncDelegate app;
    private readonly bool metricsEnabled;
    private readonly ObjectPool<FlashHttpRequest> _requestPool;
    private readonly ObjectPool<FlashHttpResponse> _responsePool;
    private readonly ObjectPool<FlashHandlerContext> _contextPool;
    private readonly IServiceProvider services;
    private readonly ILogger logger;
    private readonly int _maxHeaderCount;
    private readonly int _maxHeadersTotalSize;
    private readonly long _maxRequestBodySize;
    private readonly TimeSpan _receiveTimeout;
    private readonly TimeSpan _requestHeadersTimeout;
    private readonly CancellationToken _stoppingToken;
    private readonly PathLookup? _pathLookup;

    /// <summary>
    /// The connection only needs a duplex <paramref name="stream"/>; the endpoints are captured
    /// once by the caller so the request path never goes back to the socket for them.
    /// </summary>
    public FlashHttpConnection(
        IPEndPoint? remoteEndPoint,
        IPEndPoint? localEndPoint,
        Stream stream,
        bool isHttps,
        FlashRequestAsyncDelegate app,
        FlashHttpServerOptions options,
        ObjectPool<FlashHttpRequest> requestPool,
        ObjectPool<FlashHttpResponse> responsePool,
        ObjectPool<FlashHandlerContext> contextPool,
        IServiceProvider services,
        ILogger logger,
        CancellationToken stoppingToken,
        PathLookup? pathLookup = null)
    {
        _pathLookup = pathLookup;
        _remoteEndPoint = remoteEndPoint;
        _localEndPoint = localEndPoint;
        this.stream = stream;
        this.isHttps = isHttps;
        this.app = app;
        this.metricsEnabled = options.MetricsEnabled;
        _requestPool = requestPool;
        _responsePool = responsePool;
        _contextPool = contextPool;
        this.services = services;
        this.logger = logger;
        _maxHeaderCount = options.MaxHeaderCount;
        _maxHeadersTotalSize = options.MaxRequestHeadersTotalSize;
        _maxRequestBodySize = options.MaxRequestBodySize;
        _receiveTimeout = options.ReceiveTimeout;
        _requestHeadersTimeout = options.RequestHeadersTimeout;
        _stoppingToken = stoppingToken;
    }

    // The reader only has to hold request heads: bodies are read straight from the stream
    // into the body array (see ReadBodyFromStreamAsync). Reads start with a zero-byte read,
    // so an idle keep-alive connection holds no buffer at all while it waits.
    // 16 KB lets a burst of pipelined requests arrive in one read (and be answered with one
    // flush); with 4 KB the same burst was measured ~70% slower per request.
    private const int HeadReadBufferSize = 16 * 1024;
    private const int MinimumHeadReadSize = 4096;

    // A body buffer starts no larger than this unless the bytes are already here, and then
    // grows with what actually arrives, so a client cannot make the server commit memory just
    // by declaring a Content-Length it never sends.
    private const int InitialBodyBufferSize = 4096;

    /// <summary>
    /// True when the server ended the connection on its own initiative after an error response
    /// (rejected request, header timeout, handler fault). The client may still be sending, so
    /// the owner of the socket should close it with a lingering close: closing a socket that
    /// has unread input makes the OS reset the connection, which can destroy the error response
    /// before the client has read it.
    /// </summary>
    internal bool ClosedAfterError { get; private set; }

    // HTTP/1.1 handles one request at a time per connection, so the connection borrows a
    // single request/response/context for its whole lifetime and resets them between
    // requests, instead of going to the shared pools for every request.
    private FlashHttpRequest _request = default!;
    private FlashHttpResponse _response = default!;
    private FlashHandlerContext _context = default!;
    private ScopedFlashHandlerContext? _scopedContext;
    private List<HttpHeader> _headers = default!;

    // Parse state of the request currently arriving. The head is parsed line by line as it
    // comes in and the body is collected in its own array, so nothing is ever re-parsed.
    private FlashHttpParser.RequestHead _head;
    private bool _headComplete;
    private bool _sentContinue;
    private byte[] _body = [];
    private int _bodyRead;

    // One cancellation source for every read on the connection: it follows the connection
    // and graceful-stop tokens and is re-armed with the idle timeout before each read,
    // instead of allocating a linked source and a timer per read.
    private CancellationTokenSource? _readCts;
    private bool _applyReceiveTimeout;

    internal async Task ProcessRequestsAsync(CancellationToken cancellationToken)
    {
        // Both ends wrap the stream directly. The reader pulls from the stream only when the
        // request loop asks for more input, which gives natural back-pressure and lets a read
        // completion continue straight into parsing, with no intermediate pipe or pump task.
        // leaveOpen: the server owns the stream and may still need it for a lingering close.
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(
            bufferSize: HeadReadBufferSize,
            minimumReadSize: MinimumHeadReadSize,
            leaveOpen: true,
            useZeroByteReads: true));
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));

        _applyReceiveTimeout = _receiveTimeout > TimeSpan.Zero && _receiveTimeout != Timeout.InfiniteTimeSpan;

        _request = _requestPool.Get();
        _response = _responsePool.Get();
        _context = _contextPool.Get();

        _context.Request = _request;
        _context.Response = _response;

        // A scoped context creates its DI scope on first use; only the scope-less case needs
        // the root provider assigned.
        _scopedContext = _context as ScopedFlashHandlerContext;
        if (_scopedContext is null)
        {
            _context.Services = services;
        }

        _headers = _request.Headers ?? new List<HttpHeader>(16);

        try
        {
            await RunRequestLoopAsync(reader, writer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readCts?.Dispose();

            // The pool policies reset the objects (and release the scope and any body stream
            // of a request that was cut short).
            _requestPool.Return(_request);
            _responsePool.Return(_response);
            _contextPool.Return(_context);

            try { await reader.CompleteAsync().ConfigureAwait(false); } catch { }
            try { await writer.CompleteAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Token for the next read: cancelled when the connection is aborted, when the server
    /// starts a graceful stop (idle connections leave the read wait right away), or when
    /// nothing arrives within <see cref="FlashHttpServerOptions.ReceiveTimeout"/>.
    /// </summary>
    private CancellationToken ArmReadTimeout(CancellationToken cancellationToken)
    {
        _readCts ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingToken);

        if (_applyReceiveTimeout)
        {
            // Per-read idle timeout: protects against Slowloris-style attacks where a client
            // holds the socket open but never (or barely) sends data.
            _readCts.CancelAfter(_receiveTimeout);
        }

        return _readCts.Token;
    }

    private void DisarmReadTimeout()
    {
        // TryReset fails only when the timer fired while the read was completing; that
        // source is spent, so the next read gets a fresh one.
        if (_applyReceiveTimeout && _readCts is not null && !_readCts.TryReset())
        {
            _readCts.Dispose();
            _readCts = null;
        }
    }

    private async Task RunRequestLoopAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken)
    {
        bool connectionClose = false;
        bool applyHeadersTimeout = _requestHeadersTimeout > TimeSpan.Zero && _requestHeadersTimeout != Timeout.InfiniteTimeSpan;
        long requestStartTs = 0;

        while (!connectionClose)
        {
            ReadResult result;
            try
            {
                result = await reader.ReadAsync(ArmReadTimeout(cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Idle timeout or graceful stop while waiting for input: close quietly.
                break;
            }

            DisarmReadTimeout();

            ReadOnlySequence<byte> buffer = result.Buffer;
            bool bodyPending = false;

            // Answer every request the buffer already holds.
            while (true)
            {
                if (!_headComplete)
                {
                    if (buffer.IsEmpty)
                    {
                        break;
                    }

                    var readResult = FlashHttpParser.TryReadRequestHead(
                        ref buffer,
                        ref _head,
                        _headers,
                        _maxHeaderCount,
                        _maxRequestBodySize,
                        _maxHeadersTotalSize,
                        _pathLookup);

                    if (readResult == FlashHttpParser.TryReadHttpRequestResults.Incomplete)
                    {
                        if (requestStartTs == 0)
                        {
                            requestStartTs = Stopwatch.GetTimestamp();
                        }

                        // Headers still arriving: enforce the total headers deadline so a
                        // trickling client cannot sit below the idle timeout forever.
                        if (applyHeadersTimeout && Stopwatch.GetElapsedTime(requestStartTs) > _requestHeadersTimeout)
                        {
                            FlashHttpLog.RequestHeadersTimedOut(logger, _requestHeadersTimeout, _remoteEndPoint);
                            await SendErrorResponseAsync(writer, HttpStatusCodes.Status_408_RequestTimeout, cancellationToken).ConfigureAwait(false);
                            if (metricsEnabled)
                            {
                                FlashHttpMetrics.RecordErrorResponse(HttpStatusCodes.Status_408_RequestTimeout, isHttps);
                            }
                            ClosedAfterError = true;
                            connectionClose = true;
                        }

                        break; // need more data
                    }

                    requestStartTs = 0;

                    if (readResult != FlashHttpParser.TryReadHttpRequestResults.Success)
                    {
                        int errorStatusCode = MapParseFailure(readResult);

                        FlashHttpLog.RequestRejected(logger, readResult, errorStatusCode, _remoteEndPoint);

                        await SendErrorResponseAsync(writer, errorStatusCode, cancellationToken).ConfigureAwait(false);

                        if (metricsEnabled)
                        {
                            FlashHttpMetrics.RecordErrorResponse(errorStatusCode, isHttps);
                        }

                        ClosedAfterError = true;
                        connectionClose = true;
                        break;
                    }

                    _headComplete = true;
                    _bodyRead = 0;
                    _body = _head.ContentLength > 0 ? AllocateBody(_head.ContentLength, buffer.Length) : [];
                }

                if (_bodyRead < _head.ContentLength)
                {
                    // Whatever part of the body arrived together with the head.
                    int take = (int)Math.Min(_head.ContentLength - _bodyRead, buffer.Length);
                    if (take > 0)
                    {
                        if (_bodyRead + take > _body.Length)
                        {
                            _body = GrowBody(_body, _bodyRead, _bodyRead + take, (int)_head.ContentLength);
                        }

                        buffer.Slice(0, take).CopyTo(_body.AsSpan(_bodyRead));
                        _bodyRead += take;
                        buffer = buffer.Slice(take);
                    }

                    if (_bodyRead < _head.ContentLength)
                    {
                        bodyPending = true;
                        break;
                    }
                }

                if (!await DispatchRequestAsync(writer, cancellationToken).ConfigureAwait(false))
                {
                    connectionClose = true;
                    break;
                }
            }

            if (bodyPending && _head.ExpectContinue && !_sentContinue)
            {
                // RFC 9110 §10.1.1: headers are acceptable, tell the client to send the body.
                _sentContinue = true;
                writer.Write(ContinueResponseBytes);
            }

            // Every request that was already buffered has been answered: send the responses
            // in one go before waiting for more input (or closing).
            if (writer.UnflushedBytes > 0)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                break;
            }

            if (bodyPending)
            {
                // The reader holds nothing now (the buffer above was drained completely), so
                // the rest of the body can bypass it.
                if (!await ReadBodyFromStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    break; // the client went away, timed out or the server is stopping
                }

                if (!await DispatchRequestAsync(writer, cancellationToken).ConfigureAwait(false))
                {
                    connectionClose = true;
                }

                if (writer.UnflushedBytes > 0)
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Reads the remaining body bytes from the stream directly into the body array: no
    /// intermediate buffer, no extra copy, and each read can take as much as the socket has.
    /// Never reads past the body, so a pipelined next request stays in the socket for the
    /// reader. Returns false when the body cannot be completed.
    /// </summary>
    private async ValueTask<bool> ReadBodyFromStreamAsync(CancellationToken cancellationToken)
    {
        int contentLength = (int)_head.ContentLength;

        while (_bodyRead < contentLength)
        {
            if (_bodyRead == _body.Length)
            {
                _body = GrowBody(_body, _bodyRead, _bodyRead + 1, contentLength);
            }

            int read;
            try
            {
                // The array is never larger than the declared length, so this cannot over-read.
                read = await stream.ReadAsync(_body.AsMemory(_bodyRead), ArmReadTimeout(cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            DisarmReadTimeout();

            if (read == 0)
            {
                return false;
            }

            _bodyRead += read;
        }

        return true;
    }

    /// <summary>
    /// Hands the fully received request to the pipeline, then resets the per-request state.
    /// Returns whether the connection may stay open.
    /// </summary>
    private async ValueTask<bool> DispatchRequestAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        // The strings are always set once the request line has been parsed.
        _request.Init(
            _head.Method,
            _localEndPoint?.Port ?? 0,
            _head.Path!,
            _head.QueryString!,
            _head.KeepAlive,
            _head.ContentLength,
            _head.ContentType!,
            isHttps,
            _remoteEndPoint?.Address,
            _remoteEndPoint?.Port ?? 0,
            HttpVersions.Http11,
            _headers,
            _body);

        bool keepAlive = await ProcessRequestAsync(_request, _response, _context, _head.KeepAlive, writer, cancellationToken).ConfigureAwait(false);

        // Back to a clean slate for the next request on this connection.
        _head = default;
        _headComplete = false;
        _sentContinue = false;
        _body = [];
        _bodyRead = 0;

        _request.Reset();
        FlashHttpResponsePooledObjectPolicy.Reset(_response);
        _scopedContext?.ReleaseScope();

        return keepAlive;
    }

    internal static byte[] AllocateBody(long contentLength, long available)
    {
        long size = Math.Min(contentLength, Math.Max(InitialBodyBufferSize, available));

        // Every byte is overwritten before the handler can see the array.
        return GC.AllocateUninitializedArray<byte>((int)size);
    }

    /// <summary>
    /// Grows a partially filled body buffer. Growth is geometric (x4) so copying stays linear,
    /// and once the declared length is within 8x of what has been received the buffer goes
    /// straight to that exact length: the final array is exactly the body, and a geometric
    /// step that lands just short of it never forces one more copy of nearly everything.
    /// Memory committed therefore stays within 8x of the bytes a client has actually sent.
    /// </summary>
    internal static byte[] GrowBody(byte[] body, int used, int required, int contentLength)
    {
        long target = Math.Max((long)body.Length * 4, required);
        if (contentLength <= (long)body.Length * 8)
        {
            target = contentLength;
        }

        var grown = GC.AllocateUninitializedArray<byte>((int)Math.Min(contentLength, target));
        body.AsSpan(0, used).CopyTo(grown);
        return grown;
    }

    /// <summary>
    /// Runs the pipeline for one parsed request and writes the response. Returns whether the
    /// connection may stay open.
    /// </summary>
    private async ValueTask<bool> ProcessRequestAsync(
        FlashHttpRequest request,
        FlashHttpResponse response,
        FlashHandlerContext context,
        bool keepAlive,
        PipeWriter writer,
        CancellationToken cancellationToken)
    {
        FlashHttpLog.RequestReceived(logger, request.Method, request.Path);

        long startTs = 0;
        if (metricsEnabled)
        {
            startTs = FlashHttpMetrics.GetTimestamp();
        }

        try
        {
            // A server that is shutting down finishes this request but asks the client to reconnect.
            if (_stoppingToken.IsCancellationRequested)
            {
                keepAlive = false;
            }

            bool handlerFailed = false;
            bool writeFailed = false;
            try
            {
                ValueTask pending = app(context, cancellationToken);

                // Responses to earlier pipelined requests may still be buffered. If this
                // handler does not finish right away, send them now instead of making a
                // finished response wait for work that is still running.
                if (!pending.IsCompleted && writer.UnflushedBytes > 0)
                {
                    try
                    {
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The connection is lost, but the handler still holds this connection's
                        // request and response: let it finish before they are released.
                        writeFailed = true;
                        try { await pending.ConfigureAwait(false); } catch { }
                        throw;
                    }
                }

                await pending.ConfigureAwait(false);
                ValidateResponseHeaders(response);
            }
            catch (Exception ex) when (!writeFailed && !cancellationToken.IsCancellationRequested)
            {
                // Nothing has been written to the wire yet, so a 500 is always safe here.
                handlerFailed = true;
                FlashHttpLog.UnhandledHandlerException(logger, ex, request.Method, request.Path);

                if (metricsEnabled)
                {
                    FlashHttpMetrics.RecordRequestError(request.Method, isHttps);
                }
            }

            long responseBodyBytes;
            if (handlerFailed)
            {
                try { response.BodyStream?.Dispose(); } catch { }
                response.BodyStream = null;

                await SendErrorResponseAsync(writer, HttpStatusCodes.Status_500_InternalServerError, cancellationToken).ConfigureAwait(false);
                response.StatusCode = HttpStatusCodes.Status_500_InternalServerError;
                ClosedAfterError = true;
                keepAlive = false;
                responseBodyBytes = 0;
            }
            else
            {
                (keepAlive, responseBodyBytes) = await WriteHttpResponseAsync(writer, request.Method, response, keepAlive, cancellationToken).ConfigureAwait(false);

                // Normally the caller flushes once no further request is ready; this only
                // keeps a long run of pipelined responses from accumulating in memory.
                if (writer.UnflushedBytes >= MaxUnflushedBytes)
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (metricsEnabled)
            {
                var durationMs = FlashHttpMetrics.GetElapsedMilliseconds(startTs);

                FlashHttpMetrics.RecordRequest(
                    request.Method,
                    response.StatusCode,
                    isHttps,
                    keepAlive,
                    durationMs,
                    requestBodyBytes: request.Body?.Length ?? 0,
                    responseBodyBytes: (int)Math.Min(responseBodyBytes, int.MaxValue));
            }

            return keepAlive;
        }
        catch
        {
            // Failure while writing the response: the wire may be half-written, so the only
            // option is to drop the connection.
            if (metricsEnabled)
            {
                FlashHttpMetrics.RecordRequestError(request.Method, isHttps);
            }
            try { response.BodyStream?.Dispose(); } catch { }
            throw;
        }
    }

    private static int MapParseFailure(FlashHttpParser.TryReadHttpRequestResults result) => result switch
    {
        FlashHttpParser.TryReadHttpRequestResults.RequestLineTooLong => HttpStatusCodes.Status_414_URITooLong,
        FlashHttpParser.TryReadHttpRequestResults.HeaderLineTooLong => HttpStatusCodes.Status_431_RequestHeaderFieldsTooLarge,
        FlashHttpParser.TryReadHttpRequestResults.HeadersTooLarge => HttpStatusCodes.Status_431_RequestHeaderFieldsTooLarge,
        FlashHttpParser.TryReadHttpRequestResults.TooManyHeaders => HttpStatusCodes.Status_431_RequestHeaderFieldsTooLarge,
        FlashHttpParser.TryReadHttpRequestResults.RequestBodyTooLarge => HttpStatusCodes.Status_413_ContentTooLarge,
        FlashHttpParser.TryReadHttpRequestResults.UnsupportedHttpVersion => HttpStatusCodes.Status_505_HTTPVersionNotSupported,
        FlashHttpParser.TryReadHttpRequestResults.UnsupportedMethod => HttpStatusCodes.Status_501_NotImplemented,
        FlashHttpParser.TryReadHttpRequestResults.UnsupportedTransferEncoding => HttpStatusCodes.Status_501_NotImplemented,
        _ => HttpStatusCodes.Status_400_BadRequest
    };

    /// <summary>
    /// Rejects handler-supplied headers that would corrupt the response framing
    /// (response splitting via CR/LF, NUL, or a non-token name).
    /// </summary>
    private static void ValidateResponseHeaders(FlashHttpResponse response)
    {
        foreach (var h in response.Headers)
        {
            if (string.IsNullOrEmpty(h.Name) || ContainsInvalidHeaderNameChar(h.Name))
                throw new InvalidOperationException($"Invalid response header name '{h.Name}'.");

            if (h.Value is not null && ContainsInvalidHeaderValueChar(h.Value))
                throw new InvalidOperationException($"Response header '{h.Name}' contains CR, LF or NUL.");
        }
    }

    // Visible ASCII except the colon.
    private static readonly SearchValues<char> ValidHeaderNameChars = SearchValues.Create(
        string.Concat(Enumerable.Range(0x21, 0x7F - 0x21).Where(c => c != ':').Select(c => (char)c)));

    private static bool ContainsInvalidHeaderNameChar(string name)
        => name.AsSpan().ContainsAnyExcept(ValidHeaderNameChars);

    private static bool ContainsInvalidHeaderValueChar(string value)
        => value.AsSpan().ContainsAny('\r', '\n', '\0');

    // A body up to this size is copied next to its head so both leave in one write; a larger
    // one is written straight from the handler's array instead of being cut into pipe blocks.
    private const int DirectWriteThreshold = 64 * 1024;
    private const int DirectWriteBlockSize = 64 * 1024;

    // Responses are not flushed one by one (see RunRequestLoopAsync); this bounds what may pile up.
    private const int MaxUnflushedBytes = 64 * 1024;

    private const int StreamCopyBlockSize = 64 * 1024;

    /// <summary>
    /// Serialises the response into <paramref name="writer"/>. The tail of the response may be
    /// left unflushed: the caller flushes once no further pipelined request is ready, so a
    /// burst of small responses shares one write.
    /// </summary>
    private async ValueTask<(bool keepAlive, long bodyBytes)> WriteHttpResponseAsync(
        PipeWriter writer,
        HttpMethodsEnum requestMethod,
        FlashHttpResponse response,
        bool keepAlive,
        CancellationToken ct)
    {
        Stream? bodyStream = response.BodyStream;
        byte[] body = response.Body ?? Array.Empty<byte>();
        int statusCode = response.StatusCode;

        // RFC 9110 §6.4.1: HEAD, 1xx, 204 and 304 responses never carry content.
        bool isInformational = statusCode >= 100 && statusCode < 200;
        bool suppressBody = requestMethod == HttpMethodsEnum.Head
            || isInformational
            || statusCode == HttpStatusCodes.Status_204_NoContent
            || statusCode == HttpStatusCodes.Status_304_NotModified;

        // RFC 9110 §8.6: no Content-Length on 1xx or 204.
        bool omitContentLength = isInformational || statusCode == HttpStatusCodes.Status_204_NoContent;

        // Prefer BodyStream over Body if present
        bool useStream = bodyStream != null;
        long contentLength;
        bool chunked = false;

        if (useStream)
        {
            if (bodyStream!.CanSeek)
            {
                contentLength = bodyStream.Length - bodyStream.Position;
            }
            else
            {
                // Unknown length: chunked transfer coding keeps the connection reusable.
                contentLength = -1;
                chunked = !suppressBody;
            }
        }
        else
        {
            contentLength = body.Length;
        }

        bool sendArrayBody = !suppressBody && !useStream && body.Length > 0;
        bool inlineBody = sendArrayBody && body.Length <= DirectWriteThreshold;

        WriteResponseHead(
            writer, response, statusCode, contentLength, chunked, omitContentLength, keepAlive,
            inlineBody ? body : default);

        long bodyBytes = 0;

        if (suppressBody)
        {
            // The body is intentionally not sent; the stream (if any) is disposed on pool return.
        }
        else if (useStream)
        {
            bodyBytes = await CopyStreamBodyAsync(writer, bodyStream!, chunked, ct).ConfigureAwait(false);
        }
        else if (inlineBody)
        {
            bodyBytes = body.Length;
        }
        else if (sendArrayBody)
        {
            // Everything buffered so far (earlier pipelined responses and this head) has to
            // reach the stream before the body does.
            await writer.FlushAsync(ct).ConfigureAwait(false);

            // Straight from the handler's array (no copy), in blocks rather than one write:
            // a single multi-megabyte socket write was measured ~4x slower over loopback
            // than the same bytes in 64 KB writes.
            for (int offset = 0; offset < body.Length; offset += DirectWriteBlockSize)
            {
                int count = Math.Min(DirectWriteBlockSize, body.Length - offset);
                await stream.WriteAsync(body.AsMemory(offset, count), ct).ConfigureAwait(false);
            }

            await stream.FlushAsync(ct).ConfigureAwait(false);
            bodyBytes = body.Length;
        }

        return (keepAlive, bodyBytes);
    }

    // Date + Content-Length (the longer of the two framing headers) + Connection + final CRLF.
    private const int MaxServerOwnedHeadSize = 128;

    /// <summary>
    /// Writes the status line and all headers through one span: the size is known up front
    /// (Latin-1 is one byte per char), so the head costs a single GetSpan/Advance pair.
    /// </summary>
    private static void WriteResponseHead(
        PipeWriter writer,
        FlashHttpResponse response,
        int statusCode,
        long contentLength,
        bool chunked,
        bool omitContentLength,
        bool keepAlive,
        ReadOnlySpan<byte> inlineBody)
    {
        List<HttpHeader> headers = response.Headers;

        // A handler-supplied reason phrase (or a status code without a standard one) takes the
        // slow path; everything else uses a pre-rendered status line.
        string customReason = response.ReasonPhrase;
        byte[]? statusLine = StatusLineCache.GetStatusLine(statusCode, customReason);
        if (statusLine is null && string.IsNullOrEmpty(customReason))
        {
            customReason = UnknownReasonPhrase;
        }

        int size = MaxServerOwnedHeadSize
            + inlineBody.Length
            + (statusLine?.Length ?? Http11Bytes.Length + 11 + 1 + customReason.Length + 2);

        foreach (var h in headers)
        {
            size += h.Name.Length + 2 + (h.Value?.Length ?? 0) + 2;
        }

        var head = new HeadWriter(writer.GetSpan(size));

        // ---- Status line: HTTP/1.1 <status> <reason>\r\n
        if (statusLine is not null)
        {
            head.Write(statusLine);
        }
        else
        {
            head.Write(Http11Bytes);
            head.WriteNumber(statusCode);
            head.Write(" "u8);
            head.WriteLatin1(customReason);
            head.WriteCrLf();
        }

        // ---- Server-owned headers: always written, always correct
        head.Write(DateHeader.GetLine());

        if (chunked)
        {
            head.Write("Transfer-Encoding: chunked\r\n"u8);
        }
        else if (contentLength >= 0 && !omitContentLength)
        {
            head.Write("Content-Length: "u8);
            head.WriteNumber(contentLength);
            head.WriteCrLf();
        }

        head.Write(keepAlive ? "Connection: keep-alive\r\n"u8 : "Connection: close\r\n"u8);

        // ---- Extra headers from handler, skipping the server-owned ones
        foreach (var h in headers)
        {
            if (IsReservedResponseHeader(h.Name))
                continue;

            head.WriteLatin1(h.Name);
            head.Write(": "u8);
            if (h.Value is not null)
            {
                head.WriteLatin1(h.Value);
            }
            head.WriteCrLf();
        }

        // End of headers
        head.WriteCrLf();

        // A small body rides in the same block as its head, so both leave in one write.
        head.Write(inlineBody);

        writer.Advance(head.Written);
    }

    /// <summary>Cursor over a span obtained from the <see cref="PipeWriter"/>.</summary>
    private ref struct HeadWriter(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;

        public int Written { get; private set; }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_buffer[Written..]);
            Written += bytes.Length;
        }

        public void WriteLatin1(string text)
        {
            Written += Encoding.Latin1.GetBytes(text, _buffer[Written..]);
        }

        public void WriteNumber(long value)
        {
            if (!Utf8Formatter.TryFormat(value, _buffer[Written..], out int written))
                throw new InvalidOperationException("Failed to format number.");
            Written += written;
        }

        public void WriteCrLf()
        {
            _buffer[Written] = (byte)'\r';
            _buffer[Written + 1] = (byte)'\n';
            Written += 2;
        }
    }

    private const string UnknownReasonPhrase = "Unknown";

    /// <summary>
    /// Pre-rendered "HTTP/1.1 &lt;code&gt; &lt;reason&gt;\r\n" lines and reason-phrase bytes for
    /// every status code with a standard reason phrase.
    /// </summary>
    private static class StatusLineCache
    {
        private static readonly byte[]?[] StatusLines = new byte[600][];
        private static readonly byte[]?[] Reasons = new byte[600][];
        private static readonly string?[] ReasonStrings = new string[600];
        private static readonly byte[] UnknownReason = Encoding.ASCII.GetBytes(UnknownReasonPhrase);

        static StatusLineCache()
        {
            foreach (var (code, reason) in ReasonPhrases)
            {
                if ((uint)code >= (uint)StatusLines.Length)
                    continue;

                ReasonStrings[code] = reason;
                Reasons[code] = Encoding.ASCII.GetBytes(reason);
                StatusLines[code] = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 " + code.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n");
            }
        }

        public static byte[]? GetStatusLine(int statusCode)
            => (uint)statusCode < (uint)StatusLines.Length ? StatusLines[statusCode] : null;

        /// <summary>
        /// The pre-rendered line when <paramref name="reasonPhrase"/> is empty or is the
        /// standard phrase for the status code (as the built-in 404/405 responses set it).
        /// </summary>
        public static byte[]? GetStatusLine(int statusCode, string? reasonPhrase)
        {
            if ((uint)statusCode >= (uint)StatusLines.Length)
                return null;

            return string.IsNullOrEmpty(reasonPhrase) || string.Equals(reasonPhrase, ReasonStrings[statusCode], StringComparison.Ordinal)
                ? StatusLines[statusCode]
                : null;
        }

        public static byte[] GetReasonBytes(int statusCode)
            => ((uint)statusCode < (uint)Reasons.Length ? Reasons[statusCode] : null) ?? UnknownReason;
    }

    private static async ValueTask<long> CopyStreamBodyAsync(PipeWriter writer, Stream bodyStream, bool chunked, CancellationToken ct)
    {
        long total = 0;

        if (!chunked)
        {
            // Known length: read straight into the writer's buffer. No intermediate copy, and
            // each block reaches the stream as one write.
            while (true)
            {
                // Use what is left of the current block unless it is too small to be worth a read.
                Memory<byte> memory = writer.GetMemory();
                if (memory.Length < MinStreamReadSize)
                {
                    memory = writer.GetMemory(StreamCopyBlockSize);
                }

                int bytesRead = await bodyStream.ReadAsync(memory, ct).ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                writer.Advance(bytesRead);
                total += bytesRead;

                if (writer.UnflushedBytes >= MaxUnflushedBytes)
                {
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                }
            }

            return total;
        }

        // Unknown length: the chunk size has to be written before the data, so the data is
        // read into a scratch buffer first and then framed as one contiguous chunk.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(StreamCopyBlockSize);
        try
        {
            int readSize = StreamCopyBlockSize - MaxChunkFramingSize;
            int bytesRead;
            while ((bytesRead = await bodyStream.ReadAsync(buffer.AsMemory(0, readSize), ct).ConfigureAwait(false)) > 0)
            {
                WriteChunk(writer, buffer.AsSpan(0, bytesRead));
                total += bytesRead;

                if (writer.UnflushedBytes >= MaxUnflushedBytes)
                {
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                }
            }

            // Last chunk, no trailers.
            WriteBytes(writer, "0\r\n\r\n"u8);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    private const int MinStreamReadSize = 8 * 1024;

    // Up to 8 hex digits + CRLF before the data, CRLF after it.
    private const int MaxChunkFramingSize = 12;

    /// <summary>Writes "&lt;size in hex&gt;\r\n&lt;data&gt;\r\n" as one contiguous block.</summary>
    private static void WriteChunk(PipeWriter writer, ReadOnlySpan<byte> data)
    {
        Span<byte> span = writer.GetSpan(data.Length + MaxChunkFramingSize);

        if (!Utf8Formatter.TryFormat(data.Length, span, out int written, new StandardFormat('X')))
            throw new InvalidOperationException("Failed to format chunk size.");

        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        data.CopyTo(span[written..]);
        written += data.Length;
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';

        writer.Advance(written);
    }

    private static void WriteBytes(PipeWriter writer, ReadOnlySpan<byte> bytes)
    {
        var span = writer.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        writer.Advance(bytes.Length);
    }

    private static void WriteCRLF(PipeWriter writer)
    {
        var span = writer.GetSpan(2);
        span[0] = (byte)'\r';
        span[1] = (byte)'\n';
        writer.Advance(2);
    }

    private static bool IsReservedResponseHeader(string name)
    {
        return name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Date", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes a minimal, self-contained error response with the standard reason phrase as
    /// body and <c>Connection: close</c>. Used for parse failures and handler faults.
    /// </summary>
    private static ValueTask<FlushResult> SendErrorResponseAsync(
        PipeWriter writer,
        int statusCode,
        CancellationToken ct)
    {
        byte[] body = StatusLineCache.GetReasonBytes(statusCode);
        byte[]? statusLine = StatusLineCache.GetStatusLine(statusCode);

        var head = new HeadWriter(writer.GetSpan(256 + body.Length));

        if (statusLine is not null)
        {
            head.Write(statusLine);
        }
        else
        {
            head.Write(Http11Bytes);
            head.WriteNumber(statusCode);
            head.Write(" "u8);
            head.Write(body);
            head.WriteCrLf();
        }

        head.Write(DateHeader.GetLine());

        head.Write("Content-Length: "u8);
        head.WriteNumber(body.Length);
        head.WriteCrLf();

        head.Write("Content-Type: text/plain; charset=utf-8\r\nConnection: close\r\n\r\n"u8);
        head.Write(body);

        writer.Advance(head.Written);

        return writer.FlushAsync(ct);
    }

    /// <summary>
    /// Cached <c>Date:</c> header line, re-rendered at most once per second (RFC 9110 §10.2.2).
    /// </summary>
    private static class DateHeader
    {
        private static long _second = -1;
        private static byte[] _line = [];

        public static ReadOnlySpan<byte> GetLine()
        {
            var now = DateTime.UtcNow;
            long second = now.Ticks / TimeSpan.TicksPerSecond;

            if (Volatile.Read(ref _second) != second)
            {
                // Benign race: two threads may render the same second; both results are identical.
                var line = Encoding.ASCII.GetBytes("Date: " + now.ToString("r", CultureInfo.InvariantCulture) + "\r\n");
                Volatile.Write(ref _line, line);
                Volatile.Write(ref _second, second);
            }

            return Volatile.Read(ref _line);
        }
    }
}
