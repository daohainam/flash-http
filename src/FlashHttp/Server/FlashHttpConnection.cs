using FlashHttp.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using static FlashHttp.Server.HandlerSet;

namespace FlashHttp.Server;

internal partial class FlashHttpConnection
{
    private static readonly byte[] Http11Bytes = Encoding.ASCII.GetBytes("HTTP/1.1 ");

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
    private readonly int _maxHeaderCount;
    private readonly long _maxRequestBodySize;

    public FlashHttpConnection(
        TcpClient tcpClient,
        Stream stream,
        bool isHttps,
        FlashRequestAsyncDelegate app,
        bool metricsEnabled,
        ObjectPool<FlashHttpRequest> requestPool,
        ObjectPool<FlashHttpResponse> responsePool,
        ObjectPool<FlashHandlerContext> contextPool,
        IServiceProvider services,
        ILogger logger,
        int maxHeaderCount,
        long maxRequestBodySize)
    {
        this.tcpClient = tcpClient;
        this.stream = stream;
        this.isHttps = isHttps;
        this.app = app;
        this.metricsEnabled = metricsEnabled;
        _requestPool = requestPool;
        _responsePool = responsePool;
        _contextPool = contextPool;
        this.services = services;
        this.scopeFactory = services.GetService<IServiceScopeFactory>();
        this.logger = logger;
        _maxHeaderCount = maxHeaderCount;
        _maxRequestBodySize = maxRequestBodySize;
    }

    internal async Task Close()
    {
        if (stream != null)
        {
            stream.Flush();
            await stream.DisposeAsync();
        }
    }

    internal async Task ProcessRequestsAsync(CancellationToken cancellationToken)
    {
        var inputPipe = new Pipe();
        var outputWriter = PipeWriter.Create(stream);

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = connectionCts.Token;

        // Read from stream and fill pipe
        var reading = FillPipeAsync(stream, inputPipe.Writer, token);

        // Read from pipe and process requests
        var processing = ReadPipeAsync(inputPipe.Reader, outputWriter, connectionCts, token);

        // Wait for processing to finish, then cancel the reader task if still blocked in ReadAsync.
        await processing.ConfigureAwait(false);
        connectionCts.Cancel();

        try { await reading.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on connection close */ }

        await outputWriter.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task FillPipeAsync(Stream stream, PipeWriter writer, CancellationToken cancellationToken)
    {
        Exception? error = null;
        try
        {
            const int minimumBufferSize = 4096;

            while (true)
            {
                Memory<byte> memory = writer.GetMemory(minimumBufferSize);
                int bytesRead = await stream.ReadAsync(memory, cancellationToken);

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
    private async Task ReadPipeAsync(PipeReader reader, PipeWriter writer, CancellationTokenSource connectionCts, CancellationToken cancellationToken)
    {
        bool connectionClose = false;

        while (!connectionClose)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
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
                        isHttps: isHttps,
                        remoteEndPoint: tcpClient.Client.RemoteEndPoint as IPEndPoint,
                        localEndPoint: tcpClient.Client.LocalEndPoint as IPEndPoint,
                        _requestPool,
                        _maxHeaderCount,
                        _maxRequestBodySize
                        );

                if (readResult == FlashHttpParser.TryReadHttpRequestResults.Incomplete)
                {
                    break; // need more data
                }

                if (readResult == FlashHttpParser.TryReadHttpRequestResults.Success)
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

                        await app(context, cancellationToken);

                        _requestPool.Return(request);
                        request = null;

                        keepAlive = await WriteHttpResponseAsync(writer, response, keepAlive, cancellationToken);

                        if (metricsEnabled)
                        {
                            var durationMs = FlashHttpMetrics.GetElapsedMilliseconds(startTs);
                            
                            // Calculate response body size
                            int responseBodyBytes;
                            if (response.BodyStream != null && response.BodyStream.CanSeek)
                            {
                                long streamLength = response.BodyStream.Length;
                                long streamPosition = response.BodyStream.Position;
                                long remaining = streamLength > streamPosition ? streamLength - streamPosition : 0;
                                responseBodyBytes = (int)Math.Min(remaining, int.MaxValue);
                            }
                            else if (response.BodyStream != null)
                            {
                                responseBodyBytes = 0;
                            }
                            else
                            {
                                responseBodyBytes = response.Body?.Length ?? 0;
                            }
                            
                            FlashHttpMetrics.RecordRequest(
                                context.Request.Method,
                                response.StatusCode,
                                isHttps,
                                keepAlive,
                                durationMs,
                                requestBodyBytes: context.Request.Body?.Length ?? 0,
                                responseBodyBytes: responseBodyBytes);
                        }

                        _responsePool.Return(response);
                        response = null;

                        _contextPool.Return(context);
                        context = null;
                    }
                    catch
                    {
                        if (metricsEnabled)
                        {
                            FlashHttpMetrics.RecordRequestError(request!.Method, isHttps);
                        }
                        throw;
                    }
                    finally
                    {
                        servicesScope?.Dispose();
                        if (request != null)
                        {
                            _requestPool.Return(request);
                        }
                        if (response != null)
                        {
                            _responsePool.Return(response);
                        }
                        if (context != null)
                        {
                            _contextPool.Return(context);
                        }
                    }
                }
                else
                {
                    keepAlive = false;
                }

                if (!keepAlive)
                {
                    connectionClose = true;
                    connectionCts.Cancel();
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

    private static async ValueTask<bool> WriteHttpResponseAsync(
    PipeWriter writer,
    FlashHttpResponse response,
    bool keepAlive,
    CancellationToken ct)
    {
        Stream? bodyStream = response.BodyStream;
        byte[] body = response.Body ?? Array.Empty<byte>();
        
        // Prefer BodyStream over Body if present
        bool useStream = bodyStream != null;
        long contentLength;
        
        if (useStream)
        {
            // If stream can seek, get the length; otherwise use -1 to indicate unknown
            if (bodyStream!.CanSeek)
            {
                contentLength = bodyStream.Length - bodyStream.Position;
            }
            else
            {
                contentLength = -1;
                // For unknown length streams, we can't keep connection alive
                keepAlive = false;
            }
        }
        else
        {
            contentLength = body.Length;
        }

        // IMPORTANT: ReasonPhrase trong FlashHttpResponse hiện default là string.Empty,
        // nên dùng IsNullOrEmpty để fallback.
        string reason = string.IsNullOrEmpty(response.ReasonPhrase)
            ? GetReasonPhrase(response.StatusCode)
            : response.ReasonPhrase;

        // ---- Status line: HTTP/1.1 <status> <reason>\r\n
        WriteBytes(writer, Http11Bytes);
        WriteInt32Ascii(writer, response.StatusCode);
        WriteAscii(writer, " ");
        WriteAscii(writer, reason);
        WriteCRLF(writer);

        // ---- Core headers (server-owned): always write, always correct
        // Content-Length: <contentLength>\r\n (only if known)
        if (contentLength >= 0)
        {
            WriteBytes(writer, "Content-Length: "u8);
            WriteInt64Ascii(writer, contentLength);
            WriteCRLF(writer);
        }

        // Connection: keep-alive/close\r\n
        WriteBytes(writer, "Connection: "u8);
        WriteBytes(writer, keepAlive ? "keep-alive"u8 : "close"u8);
        WriteCRLF(writer);

        // ---- Extra headers from handler, but skip reserved ones (override)
        foreach (var h in response.Headers)
        {
            // Skip reserved headers to avoid duplicates / semantic mismatch
            if (IsReservedResponseHeader(h.Name))
                continue;

            WriteAscii(writer, h.Name);
            WriteAscii(writer, ": ");
            WriteAscii(writer, h.Value);
            WriteCRLF(writer);
        }

        // End of headers
        WriteCRLF(writer);

        // Body
        if (useStream)
        {
            // Stream the content in chunks
            const int bufferSize = 8192;
            const int flushThreshold = 65536; // Flush after 64KB to reduce I/O calls
            byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                int bytesRead;
                int bytesSinceLastFlush = 0;
                while ((bytesRead = await bodyStream!.ReadAsync(buffer.AsMemory(0, bufferSize), ct)) > 0)
                {
                    var span = writer.GetSpan(bytesRead);
                    buffer.AsSpan(0, bytesRead).CopyTo(span);
                    writer.Advance(bytesRead);
                    bytesSinceLastFlush += bytesRead;
                    
                    // Flush periodically to avoid buffering too much data
                    if (bytesSinceLastFlush >= flushThreshold)
                    {
                        await writer.FlushAsync(ct);
                        bytesSinceLastFlush = 0;
                    }
                }
                
                // Final flush for any remaining data
                if (bytesSinceLastFlush > 0)
                {
                    await writer.FlushAsync(ct);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else if (body.Length > 0)
        {
            writer.Write(body);
        }

        await writer.FlushAsync(ct);
        
        return keepAlive;
    }

    private static string GetReasonPhrase(int statusCode)
    {
        return ReasonPhrases.TryGetValue(statusCode, out var reason)
            ? reason
            : "Unknown";
    }

    private static void WriteAscii(PipeWriter writer, string text)
    {
        // Dự trù số byte cần
        var span = writer.GetSpan(text.Length);
        int bytes = Encoding.ASCII.GetBytes(text.AsSpan(), span);
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

    private const string HeaderContentLength = "Content-Length";
    private const string HeaderConnection = "Connection";

    private static bool IsReservedResponseHeader(string name)
    {
        // nhanh hơn một chút: check length trước
        if (name.Length == HeaderContentLength.Length &&
            name.Equals(HeaderContentLength, StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Length == HeaderConnection.Length &&
            name.Equals(HeaderConnection, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
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

}