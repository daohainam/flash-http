using FlashHttp.Abstractions;
using Microsoft.Extensions.ObjectPool;
using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;

namespace FlashHttp.Server;

internal class FlashHttpParser
{
    public enum TryReadHttpRequestResults
    {
        /// <summary>More bytes are needed to finish the request line or the header section.</summary>
        Incomplete,
        /// <summary>Headers are complete and valid; the Content-Length body has not fully arrived yet.</summary>
        IncompleteBody,
        Success,
        /// <summary>Request line exceeds the limit (414).</summary>
        RequestLineTooLong,
        /// <summary>A single header line exceeds the limit (431).</summary>
        HeaderLineTooLong,
        /// <summary>The header section as a whole exceeds the limit (431).</summary>
        HeadersTooLarge,
        /// <summary>Anything other than HTTP/1.1 (505).</summary>
        UnsupportedHttpVersion,
        /// <summary>Syntactically valid but unrecognised method token (501).</summary>
        UnsupportedMethod,
        /// <summary>Request carries a Transfer-Encoding the server does not implement (501).</summary>
        UnsupportedTransferEncoding,
        /// <summary>Malformed request (400).</summary>
        InvalidRequest,
        /// <summary>More headers than allowed (431).</summary>
        TooManyHeaders,
        /// <summary>Declared Content-Length exceeds the limit (413).</summary>
        RequestBodyTooLarge
    }

    private enum RequestLineResult
    {
        Ok,
        Invalid,
        UnsupportedMethod
    }

    private static ReadOnlySpan<byte> Http11Bytes => "HTTP/1.1"u8;

    private static ReadOnlySpan<byte> GetBytes => "GET"u8;
    private static ReadOnlySpan<byte> PostBytes => "POST"u8;
    private static ReadOnlySpan<byte> PutBytes => "PUT"u8;
    private static ReadOnlySpan<byte> DeleteBytes => "DELETE"u8;
    private static ReadOnlySpan<byte> OptionsBytes => "OPTIONS"u8;
    private static ReadOnlySpan<byte> PatchBytes => "PATCH"u8;
    private static ReadOnlySpan<byte> HeadBytes => "HEAD"u8;

    private static ReadOnlySpan<byte> HttpSchemePrefix => "http://"u8;
    private static ReadOnlySpan<byte> HttpsSchemePrefix => "https://"u8;

    private const byte CR = (byte)'\r';
    private const byte LF = (byte)'\n';
    private const byte SP = (byte)' ';
    private const byte HTAB = (byte)'\t';
    private const int StackAllocThreshold = 128;
    internal const int MaxRequestLineSize = 8192;
    internal const int DefaultMaxHeadersTotalSize = 32 * 1024;

    // RFC 9110 §5.6.2 token characters: visible ASCII except delimiters.
    private static readonly bool[] TokenChars = BuildTokenTable();

    private static bool[] BuildTokenTable()
    {
        var table = new bool[256];
        for (int c = 0x21; c < 0x7F; c++)
        {
            table[c] = true;
        }
        foreach (char d in "\"(),/:;<=>?@[\\]{}")
        {
            table[d] = false;
        }
        return table;
    }

    public static TryReadHttpRequestResults TryReadHttpRequest(
        ref ReadOnlySequence<byte> buffer,
        out FlashHttpRequest request,
        out bool keepAlive,
        bool isHttps,
        IPEndPoint? remoteEndPoint,
        IPEndPoint? localEndPoint,
        ObjectPool<FlashHttpRequest>? requestPool,
        int maxHeaderCount = 100,
        long maxRequestBodySize = 10 * 1024 * 1024,
        int maxHeadersTotalSize = DefaultMaxHeadersTotalSize)
        => TryReadHttpRequest(
            ref buffer,
            out request,
            out keepAlive,
            out _,
            isHttps,
            remoteEndPoint,
            localEndPoint,
            requestPool,
            maxHeaderCount,
            maxRequestBodySize,
            maxHeadersTotalSize);

    /// <summary>
    /// Parses one HTTP/1.1 request from <paramref name="buffer"/>. On <see cref="TryReadHttpRequestResults.Success"/>
    /// the buffer is advanced past the request. <paramref name="expectContinue"/> is set whenever the header
    /// section was parsed and contained <c>Expect: 100-continue</c>, including on
    /// <see cref="TryReadHttpRequestResults.IncompleteBody"/>, so the caller can answer with 100 Continue.
    /// </summary>
    public static TryReadHttpRequestResults TryReadHttpRequest(
        ref ReadOnlySequence<byte> buffer,
        out FlashHttpRequest request,
        out bool keepAlive,
        out bool expectContinue,
        bool isHttps,
        IPEndPoint? remoteEndPoint,
        IPEndPoint? localEndPoint,
        ObjectPool<FlashHttpRequest>? requestPool,
        int maxHeaderCount = 100,
        long maxRequestBodySize = 10 * 1024 * 1024,
        int maxHeadersTotalSize = DefaultMaxHeadersTotalSize)
    {
        request = default!;
        keepAlive = true;
        expectContinue = false;

        var reader = new SequenceReader<byte>(buffer);

        // 1. Request line
        if (!TryReadLine(ref reader, out ReadOnlySequence<byte> requestLineSeq))
        {
            // Reject over-long lines before the terminator arrives so a client cannot make
            // the server buffer unbounded data while waiting for an LF.
            return reader.Remaining > MaxRequestLineSize
                ? TryReadHttpRequestResults.RequestLineTooLong
                : TryReadHttpRequestResults.Incomplete;
        }

        if (requestLineSeq.Length > MaxRequestLineSize)
            return TryReadHttpRequestResults.RequestLineTooLong;

        var lineResult = TryParseRequestLine(requestLineSeq, out var method, out string path, out string queryString, out var version);

        if (lineResult == RequestLineResult.Invalid)
            return TryReadHttpRequestResults.InvalidRequest;

        if (version != HttpVersions.Http11)
            return TryReadHttpRequestResults.UnsupportedHttpVersion;

        if (lineResult == RequestLineResult.UnsupportedMethod)
            return TryReadHttpRequestResults.UnsupportedMethod;

        // 2. Headers
        var headers = new List<HttpHeader>(16);

        long headersStart = reader.Consumed;
        long contentLength = 0;
        bool hasContentLength = false;
        bool hasTransferEncoding = false;
        int hostCount = 0;
        string contentType = "";

        while (true)
        {
            if (!TryReadLine(ref reader, out ReadOnlySequence<byte> headerLineSeq))
            {
                long pending = reader.Remaining;
                if (pending > MaxRequestLineSize)
                    return TryReadHttpRequestResults.HeaderLineTooLong;
                if (reader.Consumed - headersStart + pending > maxHeadersTotalSize)
                    return TryReadHttpRequestResults.HeadersTooLarge;
                return TryReadHttpRequestResults.Incomplete;
            }

            if (headerLineSeq.Length > MaxRequestLineSize)
                return TryReadHttpRequestResults.HeaderLineTooLong;

            if (reader.Consumed - headersStart > maxHeadersTotalSize)
                return TryReadHttpRequestResults.HeadersTooLarge;

            if (headerLineSeq.Length == 0 || (headerLineSeq.Length == 1 && headerLineSeq.FirstSpan[0] == CR))
                break;

            // RFC 9112 §5.2: obsolete line folding must be rejected (or unfolded); we reject.
            byte first = headerLineSeq.FirstSpan[0];
            if (first == SP || first == HTAB)
                return TryReadHttpRequestResults.InvalidRequest;

            if (!TryParseHeaderLine(headerLineSeq, out string? name, out string? value))
            {
                // RFC 9112 §5: malformed header (no colon, bad token, whitespace before colon)
                // must be rejected, not skipped.
                return TryReadHttpRequestResults.InvalidRequest;
            }

            // Enforce header limit BEFORE adding to avoid an off-by-one and a wasted alloc.
            if (headers.Count >= maxHeaderCount)
            {
                return TryReadHttpRequestResults.TooManyHeaders;
            }

            headers.Add(new HttpHeader(name!, value!));

            // Some headers need special handling
            if (name!.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                // RFC 9112 §6.3: duplicate Content-Length is the canonical CL.CL smuggling vector.
                if (hasContentLength)
                {
                    return TryReadHttpRequestResults.InvalidRequest;
                }

                // Digits only: no sign, no whitespace, no thousands separators.
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength))
                {
                    // An all-digit value that overflows long is "too large"; anything else is malformed.
                    return IsAllDigits(value)
                        ? TryReadHttpRequestResults.RequestBodyTooLarge
                        : TryReadHttpRequestResults.InvalidRequest;
                }

                if (contentLength > maxRequestBodySize || contentLength > int.MaxValue)
                {
                    return TryReadHttpRequestResults.RequestBodyTooLarge;
                }

                hasContentLength = true;
            }
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                if (ContainsToken(value!, "close"))
                {
                    keepAlive = false;
                }
            }
            else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value ?? "";
            }
            else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                hostCount++;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                hasTransferEncoding = true;
            }
            else if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase))
            {
                if (ContainsToken(value!, "100-continue"))
                {
                    expectContinue = true;
                }
            }
        }

        // RFC 9112 §3.2: an HTTP/1.1 request must carry exactly one Host header.
        if (hostCount != 1)
            return TryReadHttpRequestResults.InvalidRequest;

        // RFC 9112 §6.1: Transfer-Encoding overrides Content-Length. Framing a TE request by
        // Content-Length is the TE.CL smuggling vector, so until chunked decoding exists the
        // only safe answer is to refuse the request and close the connection.
        if (hasTransferEncoding)
            return TryReadHttpRequestResults.UnsupportedTransferEncoding;

        // 3. Body
        if (hasContentLength && contentLength > 0 && reader.Remaining < contentLength)
            return TryReadHttpRequestResults.IncompleteBody;

        byte[] body = [];
        if (hasContentLength && contentLength > 0)
        {
            var bodyStart = reader.Position;
            var bodyEnd = buffer.GetPosition(contentLength, bodyStart);

            // The pipe buffer is recycled after AdvanceTo, so the body must be copied out.
            body = new byte[contentLength];
            buffer.Slice(bodyStart, bodyEnd).CopyTo(body);

            reader.Advance(contentLength);
        }

        request = requestPool?.Get() ?? new FlashHttpRequest();
        if (request == null)
            throw new InvalidOperationException("Failed to get FlashHttpRequest from pool.");

        request.Init(
            method,
            localEndPoint?.Port ?? 0,
            path,
            queryString,
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

    private static RequestLineResult TryParseRequestLine(
        in ReadOnlySequence<byte> lineSeq,
        out HttpMethodsEnum method,
        out string path,
        out string queryString,
        out HttpVersions version)
    {
        method = HttpMethodsEnum.Get;
        path = "";
        queryString = "";
        version = HttpVersions.Unknown;

        if (lineSeq.Length == 0)
            return RequestLineResult.Invalid;

        int len = checked((int)lineSeq.Length);

        Span<byte> line = len <= StackAllocThreshold
            ? stackalloc byte[len]
            : new byte[len];

        lineSeq.CopyTo(line);

        if (line.Length > 0 && line[^1] == CR)
        {
            line = line[..^1];
        }

        // METHOD SP request-target SP VERSION
        int firstSpace = line.IndexOf(SP);
        if (firstSpace <= 0)
            return RequestLineResult.Invalid;

        int secondSpace = line.Slice(firstSpace + 1).IndexOf(SP);
        if (secondSpace < 0)
            return RequestLineResult.Invalid;

        secondSpace += firstSpace + 1;

        ReadOnlySpan<byte> methodSpan = line[..firstSpace];
        ReadOnlySpan<byte> targetSpan = line.Slice(firstSpace + 1, secondSpace - firstSpace - 1);
        ReadOnlySpan<byte> versionSpan = line[(secondSpace + 1)..];

        if (targetSpan.Length == 0)
            return RequestLineResult.Invalid;

        // RFC 3986: control characters (and DEL) are not permitted in a request-target.
        // Allowing them risks log injection and downstream string-handling confusion.
        if (ContainsControlChar(targetSpan))
            return RequestLineResult.Invalid;

        version = versionSpan.SequenceEqual(Http11Bytes) ? HttpVersions.Http11 : HttpVersions.Unknown;

        bool knownMethod = true;
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
            // A well-formed token we don't implement is 501; garbage is 400.
            if (!IsToken(methodSpan))
                return RequestLineResult.Invalid;
            knownMethod = false;
        }

        // RFC 9112 §3.2: origin-form "/path", absolute-form "http://host/path" (servers MUST
        // accept it) and asterisk-form "*" (OPTIONS only).
        scoped ReadOnlySpan<byte> pathSpan;
        bool emptyAbsolutePath = false;
        if (targetSpan[0] == (byte)'/')
        {
            pathSpan = targetSpan;
        }
        else if (targetSpan.Length == 1 && targetSpan[0] == (byte)'*')
        {
            if (!knownMethod || method != HttpMethodsEnum.Options)
                return RequestLineResult.Invalid;
            pathSpan = targetSpan;
        }
        else if (TryStripAbsoluteForm(targetSpan, out int pathOffset, out emptyAbsolutePath))
        {
            // Points at "/path[?query]", at "?query" (no path) or is empty (no path, no query).
            pathSpan = targetSpan[pathOffset..];
        }
        else
        {
            return RequestLineResult.Invalid;
        }

        if (emptyAbsolutePath)
        {
            // "http://host" and "http://host?x=1" mean path "/".
            path = "/";
            queryString = pathSpan.Length > 0 ? Encoding.ASCII.GetString(pathSpan[1..]) : "";
        }
        else
        {
            int queryIndex = pathSpan.IndexOf((byte)'?');
            if (queryIndex >= 0)
            {
                path = Encoding.ASCII.GetString(pathSpan[..queryIndex]);
                queryString = Encoding.ASCII.GetString(pathSpan[(queryIndex + 1)..]);
            }
            else
            {
                path = Encoding.ASCII.GetString(pathSpan);
                queryString = "";
            }
        }

        return knownMethod ? RequestLineResult.Ok : RequestLineResult.UnsupportedMethod;
    }

    /// <summary>
    /// Locates the origin-form part of an absolute-form target ("http://host[:port]/path?query").
    /// <paramref name="pathOffset"/> is the index where "/path[?query]" starts; when the target
    /// has no path component it points at "?query" or at the end and <paramref name="pathIsEmpty"/>
    /// is set so the caller substitutes "/". Returns false when the target is not absolute-form.
    /// </summary>
    private static bool TryStripAbsoluteForm(ReadOnlySpan<byte> target, out int pathOffset, out bool pathIsEmpty)
    {
        pathOffset = 0;
        pathIsEmpty = false;

        int schemeLen;
        if (target.Length >= HttpSchemePrefix.Length && AsciiEqualsIgnoreCase(target[..HttpSchemePrefix.Length], HttpSchemePrefix))
            schemeLen = HttpSchemePrefix.Length;
        else if (target.Length >= HttpsSchemePrefix.Length && AsciiEqualsIgnoreCase(target[..HttpsSchemePrefix.Length], HttpsSchemePrefix))
            schemeLen = HttpsSchemePrefix.Length;
        else
            return false;

        ReadOnlySpan<byte> rest = target[schemeLen..];
        int slash = rest.IndexOf((byte)'/');
        int question = rest.IndexOf((byte)'?');

        int authorityEnd = slash >= 0 && (question < 0 || slash < question)
            ? slash
            : (question >= 0 ? question : rest.Length);

        if (authorityEnd == 0)
            return false; // empty authority

        pathOffset = schemeLen + authorityEnd;
        pathIsEmpty = authorityEnd != slash;
        return true;
    }

    private static bool TryParseHeaderLine(
        in ReadOnlySequence<byte> lineSeq,
        out string? name,
        out string? value)
    {
        name = value = null;

        if (lineSeq.Length == 0)
            return false;

        int len = checked((int)lineSeq.Length);

        Span<byte> line = len <= StackAllocThreshold
            ? stackalloc byte[len]
            : new byte[len];

        lineSeq.CopyTo(line);

        // Strip the trailing CR if present.
        if (line.Length > 0 && line[^1] == CR)
        {
            line = line[..^1];
        }

        if (line.Length == 0)
            return false;

        int colonIndex = line.IndexOf((byte)':');
        if (colonIndex <= 0)
            return false;

        // RFC 9112 §5.1: no whitespace is allowed between the field name and the colon,
        // and the name must be a token. Both rules close off header-smuggling tricks.
        ReadOnlySpan<byte> nameSpan = line[..colonIndex];
        if (!IsToken(nameSpan))
            return false;

        ReadOnlySpan<byte> valueSpan = TrimAsciiWhitespace(line[(colonIndex + 1)..]);

        // Field values may not contain control characters other than HTAB (obs-text >= 0x80 is allowed).
        if (ContainsFieldValueControlChar(valueSpan))
            return false;

        if (nameSpan.SequenceEqual(CommonHeaders.ContentLengthBytes))
        {
            name = "Content-Length";
        }
        else if (nameSpan.SequenceEqual(CommonHeaders.ContentTypeBytes))
        {
            name = "Content-Type";
        }
        else if (nameSpan.SequenceEqual(CommonHeaders.ConnectionBytes))
        {
            name = "Connection";
        }
        else if (nameSpan.SequenceEqual(CommonHeaders.HostBytes))
        {
            name = "Host";
        }
        else
        {
            name = Encoding.ASCII.GetString(nameSpan);
        }

        // Latin-1 keeps obs-text bytes intact instead of turning them into '?'.
        value = Encoding.Latin1.GetString(valueSpan);
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
        => b == SP || b == HTAB;

    private static bool IsToken(ReadOnlySpan<byte> span)
    {
        if (span.Length == 0)
            return false;

        foreach (byte b in span)
        {
            if (!TokenChars[b])
                return false;
        }
        return true;
    }

    private static bool IsAllDigits(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        foreach (char c in s)
        {
            if (c < '0' || c > '9')
                return false;
        }
        return true;
    }

    private static bool ContainsControlChar(ReadOnlySpan<byte> span)
    {
        foreach (byte b in span)
        {
            if (b < 0x20 || b == 0x7F)
                return true;
        }
        return false;
    }

    private static bool ContainsFieldValueControlChar(ReadOnlySpan<byte> span)
    {
        foreach (byte b in span)
        {
            if ((b < 0x20 && b != HTAB) || b == 0x7F)
                return true;
        }
        return false;
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            byte x = a[i], y = b[i];
            if (x >= (byte)'A' && x <= (byte)'Z') x += 32;
            if (y >= (byte)'A' && y <= (byte)'Z') y += 32;
            if (x != y)
                return false;
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="headerValue"/>, read as a comma-separated list, contains
    /// <paramref name="token"/> (case-insensitive, surrounding whitespace ignored).
    /// </summary>
    private static bool ContainsToken(string headerValue, string token)
    {
        ReadOnlySpan<char> span = headerValue;
        foreach (Range range in span.Split(','))
        {
            if (span[range].Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
