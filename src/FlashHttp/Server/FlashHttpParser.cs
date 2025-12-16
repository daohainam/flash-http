using FlashHttp.Abstractions;
using FlashHttp.Extensions;
using FlashHttp.Helpers;
using System.Buffers;
using System.Net;
using System.Text;

namespace FlashHttp.Server;

internal static class FlashHttpParser
{
    private static readonly byte CR = (byte)'\r';
    private static readonly byte LF = (byte)'\n';
    private const int StackAllocThreshold = 256;

    // Common header names (ASCII)
    private static ReadOnlySpan<byte> HostName => "Host"u8;
    private static ReadOnlySpan<byte> AuthorizationName => "Authorization"u8;
    private static ReadOnlySpan<byte> ContentLengthName => "Content-Length"u8;
    private static ReadOnlySpan<byte> ConnectionName => "Connection"u8;
    private static ReadOnlySpan<byte> ContentTypeName => "Content-Type"u8;

    private static readonly List<HttpHeader> EmptyHeadersList = new(0);

    public static bool TryReadHttpRequest(
        ref ReadOnlySequence<byte> buffer,
        out FlashHttpRequest request,
        out bool keepAlive,
        bool isHttps,
        IPEndPoint? remoteEndPoint,
        IPEndPoint? localEndPoint,
        bool materializeHeadersList)
    {
        request = default!;
        keepAlive = true;

        var reader = new SequenceReader<byte>(buffer);

        // 1. Request line
        if (!TryReadLine(ref reader, out ReadOnlySequence<byte> requestLineSeq))
            return false;

        if (!TryParseRequestLine(requestLineSeq, out string method, out string path, out string version))
            throw new InvalidOperationException("Invalid HTTP request line");

        if (!string.Equals(version, "HTTP/1.1", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsupported HTTP version");

        // 2. Headers (raw)
        var rawHeaders = FlashHttpHeadersPool.Rent();

        int contentLength = 0;
        bool hasContentLength = false;
        string? connectionHeaderValue = null;
        string contentType = string.Empty;

        while (true)
        {
            if (!TryReadLine(ref reader, out ReadOnlySequence<byte> headerLineSeq))
                return false;

            if (headerLineSeq.Length == 0 || (headerLineSeq.Length == 1 && headerLineSeq.FirstSpan[0] == CR))
                break;

            int len = checked((int)headerLineSeq.Length);
            Span<byte> line = len <= StackAllocThreshold ? stackalloc byte[len] : new byte[len];
            headerLineSeq.CopyTo(line);

            if (line.Length > 0 && line[^1] == CR)
                line = line[..^1];

            if (line.Length == 0)
                continue;

            int colon = line.IndexOf((byte)':');
            if (colon <= 0)
                continue;

            ReadOnlySpan<byte> name = TrimAsciiWhitespace(line[..colon]);
            ReadOnlySpan<byte> value = TrimAsciiWhitespace(line[(colon + 1)..]);

            if (name.Length == 0)
                continue;

            int idx = rawHeaders.Add(name, value);

            // Record known header indices (first occurrence wins)
            if (AsciiEqualsIgnoreCase(name, HostName))
                rawHeaders.SetKnownIndex(KnownHeader.Host, idx);
            else if (AsciiEqualsIgnoreCase(name, AuthorizationName))
                rawHeaders.SetKnownIndex(KnownHeader.Authorization, idx);
            else if (AsciiEqualsIgnoreCase(name, ContentTypeName))
                rawHeaders.SetKnownIndex(KnownHeader.ContentType, idx);
            else if (AsciiEqualsIgnoreCase(name, ConnectionName))
                rawHeaders.SetKnownIndex(KnownHeader.Connection, idx);
            else if (AsciiEqualsIgnoreCase(name, ContentLengthName))
                rawHeaders.SetKnownIndex(KnownHeader.ContentLength, idx);

            // Parse "hot" headers into request fields
            if (!hasContentLength && AsciiEqualsIgnoreCase(name, ContentLengthName))
            {
                if (!FlashHttpHeaders.TryParseInt32(value, out contentLength) || contentLength < 0)
                    throw new InvalidOperationException("Invalid Content-Length");
                hasContentLength = true;
            }
            else if (AsciiEqualsIgnoreCase(name, ConnectionName))
            {
                connectionHeaderValue = Encoding.ASCII.GetString(value);
            }
            else if (AsciiEqualsIgnoreCase(name, ContentTypeName))
            {
                contentType = Encoding.ASCII.GetString(value);
            }
        }

        // 3. Keep-Alive default
        if (connectionHeaderValue is not null)
        {
            if (connectionHeaderValue.Equals("close", StringComparison.OrdinalIgnoreCase))
                keepAlive = false;
            else if (connectionHeaderValue.Equals("keep-alive", StringComparison.OrdinalIgnoreCase))
                keepAlive = true;
        }
        else
        {
            keepAlive = true;
        }

        // 4. Body
        ReadOnlySequence<byte> bodySeq = ReadOnlySequence<byte>.Empty;

        if (hasContentLength && contentLength > 0)
        {
            if (reader.Remaining < contentLength)
                return false;

            var bodyStart = reader.Position;
            var bodyEnd = buffer.GetPosition(contentLength, bodyStart);
            bodySeq = buffer.Slice(bodyStart, bodyEnd);
            reader.Advance(contentLength);
        }

        byte[] body = Array.Empty<byte>();
        if (hasContentLength && contentLength > 0)
        {
            body = new byte[contentLength];
            bodySeq.CopyTo(body);
        }

        // 5. Method map
        HttpMethodsEnum methodEnum = method switch
        {
            "GET" => HttpMethodsEnum.Get,
            "POST" => HttpMethodsEnum.Post,
            "PUT" => HttpMethodsEnum.Put,
            "DELETE" => HttpMethodsEnum.Delete,
            "HEAD" => HttpMethodsEnum.Head,
            "PATCH" => HttpMethodsEnum.Patch,
            "OPTIONS" => HttpMethodsEnum.Options,
            _ => throw new InvalidOperationException($"Unsupported HTTP method: {method}")
        };

        request = new FlashHttpRequest
        {
            Method = methodEnum,
            Path = path,
            Headers = materializeHeadersList ? rawHeaders.Materialize() : EmptyHeadersList,
            ContentLength = contentLength,
            ContentType = contentType,
            IsHttps = isHttps,
            KeepAliveRequested = keepAlive,
            RemoteAddress = remoteEndPoint?.Address,
            RemotePort = remoteEndPoint?.Port ?? 0,
            HttpVersion = HttpVersions.Http11,
            Port = localEndPoint?.Port ?? 0,
            Body = body
        };

        request.SetRawHeaders(rawHeaders);

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
            return ReadOnlySpan<byte>.Empty;

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
            method = path = version = string.Empty;
            return false;
        }

        int len = checked((int)lineSeq.Length);

        Span<byte> line = len <= StackAllocThreshold
            ? stackalloc byte[len]
            : new byte[len];

        lineSeq.CopyTo(line);

        if (line.Length > 0 && line[^1] == CR)
            line = line[..^1];

        int firstSpace = line.IndexOf((byte)' ');
        if (firstSpace <= 0)
        {
            method = path = version = string.Empty;
            return false;
        }

        int secondSpace = line.Slice(firstSpace + 1).IndexOf((byte)' ');
        if (secondSpace < 0)
        {
            method = path = version = string.Empty;
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

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;

        for (int i = 0; i < a.Length; i++)
        {
            byte ca = a[i];
            byte cb = b[i];

            if ((uint)(ca - (byte)'A') <= (uint)('Z' - 'A')) ca = (byte)(ca + 32);
            if ((uint)(cb - (byte)'A') <= (uint)('Z' - 'A')) cb = (byte)(cb + 32);

            if (ca != cb) return false;
        }
        return true;
    }
}
