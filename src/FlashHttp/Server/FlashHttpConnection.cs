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
    private static readonly byte CR = (byte)'\r';
    private static readonly byte LF = (byte)'\n';
    private const int StackAllocThreshold = 128;
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

    private async Task ReadPipeAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken)
    {
        bool connectionClose = false;

        while (!connectionClose)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (TryReadHttpRequest(ref buffer, out var request, out bool keepAlive))
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

    public bool TryReadHttpRequest(
    ref ReadOnlySequence<byte> buffer,
    out FlashHttpRequest request,
    out bool keepAlive)
    {
        request = default!;
        keepAlive = true;

        var reader = new SequenceReader<byte>(buffer);

        // 1. Request line
        if (!TryReadLine(ref reader, out ReadOnlySequence<byte> requestLineSeq))
        {
            return false;
        }

        if (!TryParseRequestLine(requestLineSeq, out string method, out string path, out string version))
        {
            throw new InvalidOperationException("Invalid HTTP request line");
        }

        if (version != "HTTP/1.1")
        {
            throw new InvalidOperationException("Unsupported HTTP version");
        }

        // 2. Headers
        var headers = new List<HttpHeader>(16);

        int contentLength = 0;
        bool hasContentLength = false;
        string? connectionHeader = null;
        string contentType = "";

        while (tcpClient.Connected)
        {
            if (!TryReadLine(ref reader, out ReadOnlySequence<byte> headerLineSeq))
            {
                return false;
            }

            if (headerLineSeq.Length == 0)
            {
                break;
            }

            // Allocate a buffer on the heap if headerLineSeq.Length > 0
            byte[]? tmp = null;
            if (!TryParseHeaderLine(headerLineSeq, out string? name, out string? value))
            {
                int tmpLen = (int)headerLineSeq.Length;
                if (tmpLen > 0)
                {
                    tmp = new byte[tmpLen];
                    headerLineSeq.CopyTo(tmp);
                }
                if (tmp == null || tmp.Length == 0 || (tmp.Length == 1 && tmp[0] == CR))
                    break;

                continue;
            }

            headers.Add(new HttpHeader(name!, value!));

            // Some headers need special handling
            if (!hasContentLength &&
                name!.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, out contentLength) || contentLength < 0)
                {
                    throw new InvalidOperationException("Invalid Content-Length");
                }

                hasContentLength = true;
            }
            else if (name!.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                connectionHeader = value;
            }
            else if (name!.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value ?? "";
            }
        }

        if (connectionHeader is not null)
        {
            if (connectionHeader.Equals("close", StringComparison.OrdinalIgnoreCase))
            {
                keepAlive = false;
            }
            else if (connectionHeader.Equals("keep-alive", StringComparison.OrdinalIgnoreCase))
            {
                keepAlive = true;
            }
        }
        else
        {
            // HTTP/1.1 default keep-alive
            keepAlive = true;
        }

        // 4. Body
        ReadOnlySequence<byte> bodySeq = ReadOnlySequence<byte>.Empty;

        if (hasContentLength && contentLength > 0)
        {
            if (reader.Remaining < contentLength)
            {
                return false;
            }

            var bodyStart = reader.Position;
            var bodyEnd = buffer.GetPosition(contentLength, bodyStart);

            bodySeq = buffer.Slice(bodyStart, bodyEnd);

            reader.Advance(contentLength);
        }

        // it is safer to copy body to a new array
        byte[] body = [];
        if (hasContentLength && contentLength > 0)
        {
            body = new byte[contentLength];
            bodySeq.CopyTo(body);
        }

        if (!Enum.TryParse(method, true, out HttpMethodsEnum methodEnum))
        {
            throw new InvalidOperationException($"Unsupported HTTP method: {method}");
        }

        if (!tcpClient.Connected)
        {
            throw new InvalidOperationException("TCP client disconnected");
        }

        request = new FlashHttpRequest
        {
            Method = methodEnum,
            Path = path,
            Headers = headers,
            ContentLength = contentLength,
            ContentType = contentType,
            IsHttps = isHttps,
            KeepAliveRequested = keepAlive,
            RemoteAddress = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address,
            RemotePort = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Port ?? 0,
            HttpVersion = HttpVersions.Http11,
            Port = (tcpClient.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0,
            Body = body
        };

        buffer = buffer.Slice(reader.Position);

        return true;
    }

    private static bool TryReadLine(ref SequenceReader<byte> reader, out ReadOnlySequence<byte> line)
    {
        if (!reader.TryReadTo(out line, LF))
        {
            line = default;
            return false;
        }

        return true;
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        int start = 0;
        int end = span.Length - 1;

        while (start <= end && IsSpace(span[start])) start++;
        while (end >= start && IsSpace(span[end])) end--;

        if (start > end)
            return [];

        return span.Slice(start, end - start + 1);
    }

    private static bool IsSpace(byte b)
        => b == (byte)' ' || b == (byte)'\t';

    private static bool TryParseRequestLine(
        in ReadOnlySequence<byte> lineSeq,
        out string method,
        out string path,
        out string version)
    {
        if (lineSeq.Length == 0)
        {
            method = path = version = "";
            return false;
        }

        int len = checked((int)lineSeq.Length);

        Span<byte> line = len <= StackAllocThreshold
            ? stackalloc byte[len]
            : new byte[len];

        lineSeq.CopyTo(line);

        if (line.Length > 0 && line[^1] == CR)
        {
            line = line[..^1];
        }

        // METHOD SP PATH SP VERSION
        int firstSpace = line.IndexOf((byte)' ');
        if (firstSpace <= 0)
        {
            method = path = version = "";
            return false;
        }

        int secondSpace = line.Slice(firstSpace + 1).IndexOf((byte)' ');
        if (secondSpace < 0)
        {
            method = path = version = "";
            return false;
        }

        secondSpace += firstSpace + 1;

        ReadOnlySpan<byte> methodSpan = line[..firstSpace];
        ReadOnlySpan<byte> pathSpan = line.Slice(firstSpace + 1, secondSpace - firstSpace - 1);
        ReadOnlySpan<byte> versionSpan = line[(secondSpace + 1)..];

        method = Encoding.ASCII.GetString(methodSpan);
        path = Encoding.ASCII.GetString(pathSpan);
        version = Encoding.ASCII.GetString(versionSpan);

        return true;
    }

    private static bool TryParseHeaderLine(
        in ReadOnlySequence<byte> lineSeq,
        out string? name,
        out string? value)
    {
        if (lineSeq.Length == 0)
        {
            name = value = null;
            return false;
        }

        int len = checked((int)lineSeq.Length);

        Span<byte> line = len <= StackAllocThreshold
            ? stackalloc byte[len]
            : new byte[len];

        lineSeq.CopyTo(line);

        // Bỏ CR nếu có
        if (line.Length > 0 && line[^1] == CR)
        {
            line = line[..^1];
        }

        if (line.Length == 0)
        {
            name = value = null;
            return false;
        }

        int colonIndex = line.IndexOf((byte)':');
        if (colonIndex <= 0)
        {
            name = value = null;
            return false;
        }

        var nameSpan = TrimAsciiWhitespace(line[..colonIndex]);
        var valueSpan = TrimAsciiWhitespace(line[(colonIndex + 1)..]);

        if (nameSpan.Length == 0)
        {
            name = value = null;
            return false;
        }

        name = Encoding.ASCII.GetString(nameSpan);
        value = Encoding.ASCII.GetString(valueSpan);
        return true;
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