using FlashHttp.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace FlashHttp.Server;

internal class FlashHttpConnection
{
    private static readonly byte[] Http11Bytes = Encoding.ASCII.GetBytes("HTTP/1.1 ");

    private readonly TcpClient tcpClient;
    private readonly Stream stream;
    private readonly bool isHttps;
    private readonly HandlerSet handlerSet;
    private readonly ObjectPool<FlashHttpRequest> _requestPool;
    private readonly ObjectPool<FlashHttpResponse> _responsePool;
    private readonly ObjectPool<FlashHandlerContext> _contextPool;
    private readonly IServiceProvider services;
    private readonly ILogger logger;

    public FlashHttpConnection(TcpClient tcpClient, Stream stream, bool isHttps, HandlerSet handlerSet, 
        ObjectPool<FlashHttpRequest> requestPool, ObjectPool<FlashHttpResponse> responsePool, ObjectPool<FlashHandlerContext> contextPool,
        IServiceProvider services, ILogger logger)
    {
        this.tcpClient = tcpClient;
        this.stream = stream;
        this.isHttps = isHttps;
        this.handlerSet = handlerSet;
        _requestPool = requestPool;
        _responsePool = responsePool;
        _contextPool = contextPool;
        this.services = services;
        this.logger = logger;
    }

    internal async Task Close()
    {
        stream.Flush();
        await stream.DisposeAsync();
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

        await writer.CompleteAsync();
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
                        _requestPool
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

                    using var servicesScope = services.CreateScope();
                    context.Services = servicesScope.ServiceProvider;

                    try
                    {
                        await handlerSet.HandleAsync(context, cancellationToken);

                        _requestPool.Return(request);
                        request = null;

                        await WriteHttpResponseAsync(writer, response, keepAlive, cancellationToken);

                        _responsePool.Return(response);
                        response = null;

                        _contextPool.Return(context);
                        context = null;
                    }
                    finally
                    {
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
        await writer.CompleteAsync();
    }

    private static async ValueTask WriteHttpResponseAsync(
    PipeWriter writer,
    FlashHttpResponse response,
    bool keepAlive,
    CancellationToken ct)
    {
        var body = response.Body ?? Array.Empty<byte>();

        bool hasContentLength = false;
        bool hasConnection = false;

        foreach (var h in response.Headers)
        {
            if (h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                hasContentLength = true;
            }
            else if (h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                hasConnection = true;
            }
        }

        if (!hasContentLength)
        {
            response.Headers.Add(new HttpHeader("Content-Length", body.Length.ToString()));
        }

        if (!hasConnection)
        {
            response.Headers.Add(new HttpHeader("Connection", keepAlive ? "keep-alive" : "close"));
        }

        string reason = response.ReasonPhrase ?? GetReasonPhrase(response.StatusCode);

        // Status line
        // HTTP/1.1 200 OK\r\n
        WriteBytes(writer, Http11Bytes);
        WriteAscii(writer, response.StatusCode.ToString());
        WriteAscii(writer, " ");
        WriteAscii(writer, reason);
        WriteCRLF(writer);

        // Headers
        foreach (var h in response.Headers)
        {
            // Name: Value\r\n
            WriteAscii(writer, h.Name);
            WriteAscii(writer, ": ");
            WriteAscii(writer, h.Value);
            WriteCRLF(writer);
        }

        // End of headers
        WriteCRLF(writer);

        // Body
        if (body.Length > 0)
        {
            writer.Write(body); // PipeWriterExtensions.Write(ReadOnlySpan<byte>)
        }

        FlushResult result = await writer.FlushAsync(ct);

        // check result.IsCompleted/IsCanceled
    }

    private static string GetReasonPhrase(int statusCode)
    {
        return ReasonPhrases.TryGetValue(statusCode, out var reason)
            ? reason
            : "Unknown";
    }

    private static readonly Dictionary<int, string> ReasonPhrases = new()
    {
        { 200, "OK" },
        { 400, "Bad Request" },
        { 404, "Not Found" },
        { 500, "Internal Server Error" },
    };

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
}