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

    private readonly TcpClient tcpClient;
    private readonly Stream stream;
    private readonly bool isHttps;
    private readonly FlashRequestAsyncDelegate app;
    private readonly bool metricsEnabled;
    private readonly ObjectPool<FlashHttpRequest> _requestPool;
    private readonly ObjectPool<FlashHttpResponse> _responsePool;
    private readonly ObjectPool<FlashHandlerContext> _contextPool;
    private readonly IServiceProvider services;
    private readonly IServiceScopeFactory? scopeFactory;
    private readonly ILogger logger;
    private readonly PipeOptions _pipeOptions;
    private readonly int _maxHeaderCount;
    private readonly int _maxHeadersTotalSize;
    private readonly long _maxRequestBodySize;
    private readonly TimeSpan _receiveTimeout;
    private readonly TimeSpan _requestHeadersTimeout;
    private readonly CancellationToken _stoppingToken;

    public FlashHttpConnection(
        TcpClient tcpClient,
        Stream stream,
        bool isHttps,
        FlashRequestAsyncDelegate app,
        FlashHttpServerOptions options,
        PipeOptions pipeOptions,
        ObjectPool<FlashHttpRequest> requestPool,
        ObjectPool<FlashHttpResponse> responsePool,
        ObjectPool<FlashHandlerContext> contextPool,
        IServiceProvider services,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        this.tcpClient = tcpClient;
        this.stream = stream;
        this.isHttps = isHttps;
        this.app = app;
        this.metricsEnabled = options.MetricsEnabled;
        _requestPool = requestPool;
        _responsePool = responsePool;
        _contextPool = contextPool;
        this.services = services;
        this.scopeFactory = services.GetService<IServiceScopeFactory>();
        this.logger = logger;
        _pipeOptions = pipeOptions;
        _maxHeaderCount = options.MaxHeaderCount;
        _maxHeadersTotalSize = options.MaxRequestHeadersTotalSize;
        _maxRequestBodySize = options.MaxRequestBodySize;
        _receiveTimeout = options.ReceiveTimeout;
        _requestHeadersTimeout = options.RequestHeadersTimeout;
        _stoppingToken = stoppingToken;
    }

    /// <summary>
    /// Builds the input <see cref="PipeOptions"/> for a server. The parser buffers a whole
    /// request (headers + Content-Length body) before it can consume anything, so the pipe
    /// must be allowed to hold at least one maximum-size request or the writer side pauses
    /// forever waiting for a consumer that is itself waiting for more bytes.
    /// </summary>
    internal static PipeOptions CreatePipeOptions(FlashHttpServerOptions options)
    {
        long maxRequest = options.MaxRequestBodySize
            + options.MaxRequestHeadersTotalSize
            + FlashHttpParser.MaxRequestLineSize
            + 4096;

        long pause = Math.Max(maxRequest, 64 * 1024);
        long resume = pause / 2;

        return new PipeOptions(pauseWriterThreshold: pause, resumeWriterThreshold: resume, useSynchronizationContext: false);
    }

    internal async Task ProcessRequestsAsync(CancellationToken cancellationToken)
    {
        var inputPipe = new Pipe(_pipeOptions);
        var outputWriter = PipeWriter.Create(stream);

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = connectionCts.Token;

        // Read from stream and fill pipe
        var reading = FillPipeAsync(stream, inputPipe.Writer, _receiveTimeout, token);

        try
        {
            // Read from pipe and process requests
            await ReadPipeAsync(inputPipe.Reader, outputWriter, token).ConfigureAwait(false);
        }
        finally
        {
            // Always unblock and await the reader, even when processing faulted; otherwise
            // the read task is left orphaned on the stream and faults unobserved on dispose.
            connectionCts.Cancel();
            try { await reading.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on connection close */ }
            catch (Exception) { /* socket-level errors surface through the pipe reader */ }

            try { await outputWriter.CompleteAsync().ConfigureAwait(false); } catch { }
        }
    }

    private static async Task FillPipeAsync(Stream stream, PipeWriter writer, TimeSpan receiveTimeout, CancellationToken cancellationToken)
    {
        Exception? error = null;
        bool applyTimeout = receiveTimeout > TimeSpan.Zero && receiveTimeout != Timeout.InfiniteTimeSpan;
        try
        {
            const int minimumBufferSize = 4096;

            while (true)
            {
                Memory<byte> memory = writer.GetMemory(minimumBufferSize);

                int bytesRead;
                if (applyTimeout)
                {
                    // Per-read idle timeout: protects against Slowloris-style attacks where
                    // a client holds the socket open but never (or barely) sends data.
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    readCts.CancelAfter(receiveTimeout);
                    try
                    {
                        bytesRead = await stream.ReadAsync(memory, readCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Idle timeout: graceful close, not an error.
                        break;
                    }
                }
                else
                {
                    bytesRead = await stream.ReadAsync(memory, cancellationToken);
                }

                if (bytesRead == 0)
                {
                    break;
                }

                writer.Advance(bytesRead);

                FlushResult result = await writer.FlushAsync(cancellationToken);

                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            try { await writer.CompleteAsync(error).ConfigureAwait(false); } catch { }
        }
    }

    private async Task ReadPipeAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken)
    {
        bool connectionClose = false;
        bool applyHeadersTimeout = _requestHeadersTimeout > TimeSpan.Zero && _requestHeadersTimeout != Timeout.InfiniteTimeSpan;
        long requestStartTs = 0;
        bool sentContinue = false;

        // Graceful shutdown: idle connections exit the read wait as soon as the server stops
        // accepting, while a request already being processed runs to completion.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingToken);

        while (!connectionClose)
        {
            ReadResult result;
            try
            {
                result = await reader.ReadAsync(readCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _stoppingToken.IsCancellationRequested)
            {
                break;
            }

            ReadOnlySequence<byte> buffer = result.Buffer;

            while (true)
            {
                if (buffer.Length == 0)
                {
                    break;
                }

                var readResult = FlashHttpParser.TryReadHttpRequest(
                        ref buffer,
                        out var request,
                        out bool keepAlive,
                        out bool expectContinue,
                        isHttps: isHttps,
                        remoteEndPoint: tcpClient.Client.RemoteEndPoint as IPEndPoint,
                        localEndPoint: tcpClient.Client.LocalEndPoint as IPEndPoint,
                        _requestPool,
                        _maxHeaderCount,
                        _maxRequestBodySize,
                        _maxHeadersTotalSize);

                if (readResult is FlashHttpParser.TryReadHttpRequestResults.Incomplete
                               or FlashHttpParser.TryReadHttpRequestResults.IncompleteBody)
                {
                    if (requestStartTs == 0)
                    {
                        requestStartTs = Stopwatch.GetTimestamp();
                    }

                    if (readResult == FlashHttpParser.TryReadHttpRequestResults.Incomplete)
                    {
                        // Headers still arriving: enforce the total headers deadline so a
                        // trickling client cannot sit below the idle timeout forever.
                        if (applyHeadersTimeout && Stopwatch.GetElapsedTime(requestStartTs) > _requestHeadersTimeout)
                        {
                            logger.LogWarning("Request rejected: headers not received within {Timeout} from {RemoteEndPoint}",
                                _requestHeadersTimeout, tcpClient.Client.RemoteEndPoint);
                            await SendErrorResponseAsync(writer, HttpStatusCodes.Status_408_RequestTimeout, cancellationToken);
                            if (metricsEnabled)
                            {
                                FlashHttpMetrics.RecordErrorResponse(HttpStatusCodes.Status_408_RequestTimeout, isHttps);
                            }
                            connectionClose = true;
                        }
                    }
                    else if (expectContinue && !sentContinue)
                    {
                        // RFC 9110 §10.1.1: headers are acceptable, tell the client to send the body.
                        sentContinue = true;
                        writer.Write(ContinueResponseBytes);
                        await writer.FlushAsync(cancellationToken);
                    }

                    break; // need more data
                }

                requestStartTs = 0;
                sentContinue = false;

                if (readResult == FlashHttpParser.TryReadHttpRequestResults.Success)
                {
                    keepAlive = await ProcessRequestAsync(request, keepAlive, writer, cancellationToken);
                }
                else
                {
                    int errorStatusCode = MapParseFailure(readResult);

                    logger.LogWarning("Request rejected: {Result} -> {StatusCode} from {RemoteEndPoint}",
                        readResult, errorStatusCode, tcpClient.Client.RemoteEndPoint);

                    await SendErrorResponseAsync(writer, errorStatusCode, cancellationToken);

                    if (metricsEnabled)
                    {
                        FlashHttpMetrics.RecordErrorResponse(errorStatusCode, isHttps);
                    }

                    keepAlive = false;
                }

                if (!keepAlive)
                {
                    connectionClose = true;
                    break;
                }
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync();
    }

    /// <summary>
    /// Runs the pipeline for one parsed request and writes the response. Returns whether the
    /// connection may stay open.
    /// </summary>
    private async ValueTask<bool> ProcessRequestAsync(FlashHttpRequest request, bool keepAlive, PipeWriter writer, CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Received HTTP request: {Method} {Path}", request.Method, request.Path);
        }

        var response = _responsePool.Get();
        var context = _contextPool.Get();

        context.Request = request;
        context.Response = response;

        IServiceScope? servicesScope = null;

        long startTs = 0;
        if (metricsEnabled)
        {
            startTs = FlashHttpMetrics.GetTimestamp();
        }

        try
        {
            if (scopeFactory is null)
            {
                context.Services = services;
            }
            else
            {
                servicesScope = scopeFactory.CreateScope();
                context.Services = servicesScope.ServiceProvider;
            }

            // A server that is shutting down finishes this request but asks the client to reconnect.
            if (_stoppingToken.IsCancellationRequested)
            {
                keepAlive = false;
            }

            bool handlerFailed = false;
            try
            {
                await app(context, cancellationToken);
                ValidateResponseHeaders(response);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Nothing has been written to the wire yet, so a 500 is always safe here.
                handlerFailed = true;
                logger.LogError(ex, "Unhandled exception while handling {Method} {Path}", request.Method, request.Path);

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

                await SendErrorResponseAsync(writer, HttpStatusCodes.Status_500_InternalServerError, cancellationToken);
                response.StatusCode = HttpStatusCodes.Status_500_InternalServerError;
                keepAlive = false;
                responseBodyBytes = 0;
            }
            else
            {
                (keepAlive, responseBodyBytes) = await WriteHttpResponseAsync(writer, request.Method, response, keepAlive, cancellationToken);
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
        finally
        {
            servicesScope?.Dispose();
            _requestPool.Return(request);
            _responsePool.Return(response);
            _contextPool.Return(context);
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

    private static bool ContainsInvalidHeaderNameChar(string name)
    {
        foreach (char c in name)
        {
            if (c <= ' ' || c >= 0x7F || c == ':')
                return true;
        }
        return false;
    }

    private static bool ContainsInvalidHeaderValueChar(string value)
    {
        foreach (char c in value)
        {
            if (c == '\r' || c == '\n' || c == '\0')
                return true;
        }
        return false;
    }

    private static async ValueTask<(bool keepAlive, long bodyBytes)> WriteHttpResponseAsync(
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

        string reason = string.IsNullOrEmpty(response.ReasonPhrase)
            ? GetReasonPhrase(statusCode)
            : response.ReasonPhrase;

        // ---- Status line: HTTP/1.1 <status> <reason>\r\n
        WriteBytes(writer, Http11Bytes);
        WriteInt32Ascii(writer, statusCode);
        WriteAscii(writer, " ");
        WriteAscii(writer, reason);
        WriteCRLF(writer);

        // ---- Server-owned headers: always written, always correct
        WriteBytes(writer, DateHeader.GetLine());

        if (chunked)
        {
            WriteBytes(writer, "Transfer-Encoding: chunked\r\n"u8);
        }
        else if (contentLength >= 0 && !omitContentLength)
        {
            WriteBytes(writer, "Content-Length: "u8);
            WriteInt64Ascii(writer, contentLength);
            WriteCRLF(writer);
        }

        WriteBytes(writer, "Connection: "u8);
        WriteBytes(writer, keepAlive ? "keep-alive"u8 : "close"u8);
        WriteCRLF(writer);

        // ---- Extra headers from handler, skipping the server-owned ones
        foreach (var h in response.Headers)
        {
            if (IsReservedResponseHeader(h.Name))
                continue;

            WriteAscii(writer, h.Name);
            WriteAscii(writer, ": ");
            WriteLatin1(writer, h.Value ?? string.Empty);
            WriteCRLF(writer);
        }

        // End of headers
        WriteCRLF(writer);

        long bodyBytes = 0;

        if (suppressBody)
        {
            // The body is intentionally not sent; the stream (if any) is disposed on pool return.
        }
        else if (useStream)
        {
            bodyBytes = await CopyStreamBodyAsync(writer, bodyStream!, chunked, ct);
        }
        else if (body.Length > 0)
        {
            writer.Write(body);
            bodyBytes = body.Length;
        }

        await writer.FlushAsync(ct);

        return (keepAlive, bodyBytes);
    }

    private static async ValueTask<long> CopyStreamBodyAsync(PipeWriter writer, Stream bodyStream, bool chunked, CancellationToken ct)
    {
        const int bufferSize = 8192;
        const int flushThreshold = 65536; // Flush after 64KB to reduce I/O calls
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long total = 0;
        try
        {
            int bytesRead;
            int bytesSinceLastFlush = 0;
            while ((bytesRead = await bodyStream.ReadAsync(buffer.AsMemory(0, bufferSize), ct)) > 0)
            {
                if (chunked)
                {
                    WriteChunkSize(writer, bytesRead);
                }

                var span = writer.GetSpan(bytesRead);
                buffer.AsSpan(0, bytesRead).CopyTo(span);
                writer.Advance(bytesRead);

                if (chunked)
                {
                    WriteCRLF(writer);
                }

                total += bytesRead;
                bytesSinceLastFlush += bytesRead;

                if (bytesSinceLastFlush >= flushThreshold)
                {
                    await writer.FlushAsync(ct);
                    bytesSinceLastFlush = 0;
                }
            }

            if (chunked)
            {
                // Last chunk, no trailers.
                WriteBytes(writer, "0\r\n\r\n"u8);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    private static void WriteChunkSize(PipeWriter writer, int size)
    {
        Span<byte> span = writer.GetSpan(10);
        if (!Utf8Formatter.TryFormat(size, span, out int written, new StandardFormat('X')))
            throw new InvalidOperationException("Failed to format chunk size.");
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        writer.Advance(written);
    }

    private static string GetReasonPhrase(int statusCode)
    {
        return ReasonPhrases.TryGetValue(statusCode, out var reason)
            ? reason
            : "Unknown";
    }

    private static void WriteAscii(PipeWriter writer, string text)
    {
        var span = writer.GetSpan(text.Length);
        int bytes = Encoding.ASCII.GetBytes(text.AsSpan(), span);
        writer.Advance(bytes);
    }

    private static void WriteLatin1(PipeWriter writer, string text)
    {
        var span = writer.GetSpan(text.Length);
        int bytes = Encoding.Latin1.GetBytes(text.AsSpan(), span);
        writer.Advance(bytes);
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

    private static void WriteInt32Ascii(PipeWriter writer, int value)
    {
        // Int32.MinValue = -2147483648 => 11 chars max
        Span<byte> span = writer.GetSpan(11);
        if (!Utf8Formatter.TryFormat(value, span, out int written))
            throw new InvalidOperationException("Failed to format int.");

        writer.Advance(written);
    }

    private static void WriteInt64Ascii(PipeWriter writer, long value)
    {
        // Int64.MinValue = -9223372036854775808 => 20 chars max
        Span<byte> span = writer.GetSpan(20);
        if (!Utf8Formatter.TryFormat(value, span, out int written))
            throw new InvalidOperationException("Failed to format long.");

        writer.Advance(written);
    }

    /// <summary>
    /// Writes a minimal, self-contained error response with the standard reason phrase as
    /// body and <c>Connection: close</c>. Used for parse failures and handler faults.
    /// </summary>
    private static async ValueTask SendErrorResponseAsync(
        PipeWriter writer,
        int statusCode,
        CancellationToken ct)
    {
        string reason = GetReasonPhrase(statusCode);
        byte[] body = Encoding.UTF8.GetBytes(reason);

        WriteBytes(writer, Http11Bytes);
        WriteInt32Ascii(writer, statusCode);
        WriteAscii(writer, " ");
        WriteAscii(writer, reason);
        WriteCRLF(writer);

        WriteBytes(writer, DateHeader.GetLine());

        WriteBytes(writer, "Content-Length: "u8);
        WriteInt64Ascii(writer, body.Length);
        WriteCRLF(writer);

        WriteBytes(writer, "Content-Type: text/plain; charset=utf-8\r\n"u8);
        WriteBytes(writer, "Connection: close\r\n"u8);

        WriteCRLF(writer);

        if (body.Length > 0)
        {
            writer.Write(body);
        }

        await writer.FlushAsync(ct);
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
