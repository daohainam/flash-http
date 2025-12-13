using FlashHttp.Abstractions;
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
        try
        {
            var inputPipe = new Pipe();
            var outputWriter = PipeWriter.Create(stream);

            var reading = FillPipeAsync(stream, inputPipe.Writer, cancellationToken);
            var readingRequests = ReadPipeAsync(inputPipe.Reader, outputWriter, cancellationToken);

            await Task.WhenAll(reading, readingRequests);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in connection");
        }
        finally
        {
            try
            {
                await stream.DisposeAsync();
            }
            catch { }

            try
            {
                tcpClient.Close();
                tcpClient.Dispose();
            }
            catch { }
        }
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

    internal async Task ReadPipeAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken)
    {
        bool connectionClose = false;

        while (!connectionClose)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (FlashHttpParser.TryReadHttpRequest(ref buffer,
                isHttps, tcpClient.Client.RemoteEndPoint as IPEndPoint, tcpClient.Client.LocalEndPoint as IPEndPoint,
                out var request, out bool keepAlive))
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Received HTTP request: {Method} {Path}", request.Method, request.Path);
                }

                var response = new FlashHttpResponse();

                await handlerSet.HandleAsync(request, response, cancellationToken);

                await WriteHttpResponseAsync(writer, response, keepAlive, cancellationToken);

                if (!keepAlive)
                {
                    connectionClose = true;
                    break;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("Connection timed out while reading HTTP request.");
                }
                break;
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

    private static void WriteCRLF(PipeWriter writer)
    {
        var span = writer.GetSpan(2);
        span[0] = (byte)'\r';
        span[1] = (byte)'\n';
        writer.Advance(2);
    }
}