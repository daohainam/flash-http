using FlashHttp.Abstractions;
using FlashHttp.Extensions;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace FlashHttp.Server;

internal class FlashHttpConnection
{
    private readonly TcpClient tcpClient;
    private readonly Stream stream;
    private readonly bool isHttps;
    private readonly HandlerSet handlerSet;
    private readonly ILogger logger;

    public FlashHttpConnection(TcpClient tcpClient, Stream stream, bool isHttps, HandlerSet handlerSet, ILogger logger)
    {
        this.tcpClient = tcpClient;
        this.stream = stream;
        this.isHttps = isHttps;
        this.handlerSet = handlerSet;
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

        // Read from stream and fill pipe
        var reading = FillPipeAsync(stream, inputPipe.Writer, cancellationToken);

        // Read from pipe and process requests
        var readingRequests = ReadPipeAsync(inputPipe.Reader, outputWriter, cancellationToken);

        await Task.WhenAll(reading, readingRequests);
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

    private async Task ReadPipeAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken)
    {
        bool connectionClose = false;

        while (!connectionClose)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (true)
            {
                var localBuffer = buffer;

                if (!FlashHttpParser.TryReadHttpRequest(
                        ref localBuffer,
                        out var request,
                        out bool keepAlive,
                        isHttps: isHttps,
                        remoteEndPoint: tcpClient.Client.RemoteEndPoint as IPEndPoint,
                        localEndPoint: tcpClient.Client.LocalEndPoint as IPEndPoint,
                        materializeHeadersList: false))
                {
                    break; // need more data
                }

                // consumed
                buffer = localBuffer;

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Received HTTP request: {Method} {Path}", request.Method, request.Path);
                }

                var response = new FlashHttpResponse();

                try
                {
                    await handlerSet.HandleAsync(request, response, cancellationToken);
                }
                finally
                {
                    // IMPORTANT: release pooled header storage
                    request.ReleaseRawHeaders();
                }

                await WriteHttpResponseAsync(writer, response, keepAlive, cancellationToken);

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
        WriteAscii(writer, "HTTP/1.1 ");
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
            writer.Write(body);
        }

        await writer.FlushAsync(ct);
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
        var span = writer.GetSpan(text.Length);
        int bytes = Encoding.ASCII.GetBytes(text.AsSpan(), span);
        writer.Advance(bytes);
    }

    private static void WriteCRLF(PipeWriter writer)
    {
        var span = writer.GetSpan(2);
        span[0] = (byte)'\r';
        span[1] = (byte)'\n';
        writer.Advance(2);
    }
}
