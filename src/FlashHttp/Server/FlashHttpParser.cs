using FlashHttp.Abstractions;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace FlashHttp.Server;

internal class FlashHttpParser
{
    private static readonly byte CR = (byte)'\r';
    private static readonly byte LF = (byte)'\n';
    private const int StackAllocThreshold = 128;

    public static bool TryReadHttpRequest(
        ref ReadOnlySequence<byte> buffer,
        bool isHttps,
        IPEndPoint? remoteEndPoint,
        IPEndPoint? localEndPoint,
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

        while (true)
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

        request = new FlashHttpRequest
        {
            Method = methodEnum,
            Path = path,
            Headers = headers,
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

}
