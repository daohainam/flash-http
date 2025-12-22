using FlashHttp.Abstractions;
using Microsoft.Extensions.ObjectPool;
using System.Buffers;
using System.Net;
using System.Text;

namespace FlashHttp.Server;

internal class FlashHttpParser
{
    public enum TryReadHttpRequestResults
    {
        Incomplete,
        Success,
        RequestLineTooLong,
        HeaderLineTooLong,
        UnsupportedHttpVersion,
        InvalidRequest
    }

    private static ReadOnlySpan<byte> Http11Bytes => "HTTP/1.1"u8;

    private static ReadOnlySpan<byte> GetBytes => "GET"u8;
    private static ReadOnlySpan<byte> PostBytes => "POST"u8;
    private static ReadOnlySpan<byte> PutBytes => "PUT"u8;
    private static ReadOnlySpan<byte> DeleteBytes => "DELETE"u8;
    private static ReadOnlySpan<byte> OptionsBytes => "OPTIONS"u8;
    private static ReadOnlySpan<byte> PatchBytes => "PATCH"u8;
    private static ReadOnlySpan<byte> HeadBytes => "HEAD"u8;

    private static readonly byte CR = (byte)'\r';
    private static readonly byte LF = (byte)'\n';
    private const int StackAllocThreshold = 128;
    private const int MaxRequestLineSize = 8192;

    public static TryReadHttpRequestResults TryReadHttpRequest(
        ref ReadOnlySequence<byte> buffer,
        out FlashHttpRequest request,
        out bool keepAlive,
        bool isHttps,
        IPEndPoint? remoteEndPoint,
        IPEndPoint? localEndPoint,
        ObjectPool<FlashHttpRequest>? requestPool)
    {
        request = default!;
        keepAlive = true;

        var reader = new SequenceReader<byte>(buffer);

        // 1. Request line
        if (!TryReadLine(ref reader, out ReadOnlySequence<byte> requestLineSeq))
            return TryReadHttpRequestResults.Incomplete;

        if (requestLineSeq.Length > MaxRequestLineSize)
            return TryReadHttpRequestResults.RequestLineTooLong;

        if (!TryParseRequestLine(requestLineSeq, out var method, out string path, out var version))
            return TryReadHttpRequestResults.InvalidRequest;

        if (version != HttpVersions.Http11)
            return TryReadHttpRequestResults.UnsupportedHttpVersion;

        // 2. Headers
        var headers = new List<HttpHeader>(16);

        int contentLength = 0;
        bool hasContentLength = false;
        string? connectionHeader = null;
        string contentType = "";

        while (true)
        {
            if (!TryReadLine(ref reader, out ReadOnlySequence<byte> headerLineSeq))
                return TryReadHttpRequestResults.Incomplete;

            if (headerLineSeq.Length > MaxRequestLineSize)
                return TryReadHttpRequestResults.HeaderLineTooLong;

            if (headerLineSeq.Length == 0 || (headerLineSeq.Length == 1 && headerLineSeq.FirstSpan[0] == CR))
                break;

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
                return TryReadHttpRequestResults.Incomplete;

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

        request = requestPool?.Get() ?? new FlashHttpRequest();
        if (request == null)
            throw new InvalidOperationException("Failed to get FlashHttpRequest from pool.");

        request.Init(
            method,
            localEndPoint?.Port ?? 0,
            path,  
            keepAlive,
            contentLength,
            contentType,
            isHttps,
            remoteEndPoint?.Address,
            remoteEndPoint?.Port ?? 0,
            HttpVersions.Http11,
            headers,
            body);

        buffer = buffer.Slice(reader.Position);

        return TryReadHttpRequestResults.Success;
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
    out HttpMethodsEnum method,
    out string path,
    out HttpVersions version)
    {
        if (lineSeq.Length == 0)
        {
            method = HttpMethodsEnum.Get;
            path = "";
            version = HttpVersions.Unknown;
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
            method = HttpMethodsEnum.Get;
            path = "";
            version = HttpVersions.Unknown;
            return false;
        }

        int secondSpace = line.Slice(firstSpace + 1).IndexOf((byte)' ');
        if (secondSpace < 0)
        {
            method = HttpMethodsEnum.Get;
            path = "";
            version = HttpVersions.Unknown;
            return false;
        }

        secondSpace += firstSpace + 1;

        ReadOnlySpan<byte> methodSpan = line[..firstSpace];
        ReadOnlySpan<byte> pathSpan = line.Slice(firstSpace + 1, secondSpace - firstSpace - 1);
        ReadOnlySpan<byte> versionSpan = line[(secondSpace + 1)..];

        if (methodSpan.SequenceEqual(GetBytes))
            method = HttpMethodsEnum.Get;
        else if (methodSpan.SequenceEqual(PostBytes))
            method = HttpMethodsEnum.Post;
        else if (methodSpan.SequenceEqual(PutBytes))
            method = HttpMethodsEnum.Put;
        else if (methodSpan.SequenceEqual(DeleteBytes))
            method = HttpMethodsEnum.Delete;
        else if (methodSpan.SequenceEqual(OptionsBytes))
            method = HttpMethodsEnum.Options;
        else if (methodSpan.SequenceEqual(PatchBytes))
            method = HttpMethodsEnum.Patch;
        else if (methodSpan.SequenceEqual(HeadBytes))
            method = HttpMethodsEnum.Head;
        else
        {
            method = HttpMethodsEnum.Get; // default
            path = "";
            version = HttpVersions.Unknown;
            return false;
        }

        path = Encoding.ASCII.GetString(pathSpan);
        version = versionSpan.SequenceEqual(Http11Bytes) ? HttpVersions.Http11 : HttpVersions.Unknown;

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

        if (nameSpan.SequenceEqual(CommonHeaders.ContentLengthBytes))
        {
            name = string.Intern("Content-Length");
        }
        else if (nameSpan.SequenceEqual(CommonHeaders.ContentTypeBytes))
        {
            name = string.Intern("Content-Type");
        }
        else if (nameSpan.SequenceEqual(CommonHeaders.ConnectionBytes))
        {
            name = string.Intern("Connection");
        }
        else if (nameSpan.SequenceEqual(CommonHeaders.HostBytes))
        {
            name = string.Intern("Host");
        }
        else
        {
            name = Encoding.ASCII.GetString(nameSpan);
        }

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
