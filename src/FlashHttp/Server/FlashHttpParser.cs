using FlashHttp.Abstractions;
using Microsoft.Extensions.ObjectPool;
using System.Buffers;
using System.Net;
using System.Text;

namespace FlashHttp.Server;

/// <summary>
/// Returns the registered route string equal to <paramref name="path"/>, or null. Lets the
/// parser reuse that string instead of allocating a new one for every request.
/// </summary>
internal delegate string? PathLookup(ReadOnlySpan<char> path);

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
    internal const int MaxRequestLineSize = 8192;
    internal const int DefaultMaxHeadersTotalSize = 32 * 1024;

    // Paths longer than this are not looked up among the registered routes; they are decoded
    // into a new string like any unregistered path.
    private const int MaxInternedPathLength = 256;

    // RFC 9110 §5.6.2 token characters: visible ASCII except delimiters.
    private static readonly SearchValues<byte> TokenBytes =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8);

    // Control characters (and DEL), which are not permitted in a request-target.
    private static readonly SearchValues<byte> ControlBytes = SearchValues.Create(BuildControlBytes(includeHtab: true));

    // Field values may not contain control characters other than HTAB (obs-text >= 0x80 is allowed).
    private static readonly SearchValues<byte> FieldValueControlBytes = SearchValues.Create(BuildControlBytes(includeHtab: false));

    private static byte[] BuildControlBytes(bool includeHtab)
    {
        var bytes = new List<byte>(33);
        for (int b = 0; b < 0x20; b++)
        {
            if (b != HTAB || includeHtab)
            {
                bytes.Add((byte)b);
            }
        }
        bytes.Add(0x7F);
        return [.. bytes];
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
    /// Parses one whole HTTP/1.1 request from <paramref name="buffer"/>. On <see cref="TryReadHttpRequestResults.Success"/>
    /// the buffer is advanced past the request; on any other result it is left untouched.
    /// <paramref name="expectContinue"/> is set whenever the header section was parsed and contained
    /// <c>Expect: 100-continue</c>, including on <see cref="TryReadHttpRequestResults.IncompleteBody"/>.
    /// The connection does not use this method: it parses the head incrementally with
    /// <see cref="TryReadRequestHead"/> and drains the body itself.
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

        // The request is taken up front so its header list can be filled in place; it goes
        // straight back to the pool when the buffer does not hold a complete, valid request.
        var candidate = requestPool?.Get() ?? new FlashHttpRequest();
        var headers = candidate.Headers ?? new List<HttpHeader>(16);

        var head = default(RequestHead);
        var remaining = buffer;

        var result = TryReadRequestHead(ref remaining, ref head, headers, maxHeaderCount, maxRequestBodySize, maxHeadersTotalSize);

        keepAlive = !head.RequestLineParsed || head.KeepAlive;
        expectContinue = head.ExpectContinue;

        // 3. Body
        if (result == TryReadHttpRequestResults.Success && head.ContentLength > 0 && remaining.Length < head.ContentLength)
            result = TryReadHttpRequestResults.IncompleteBody;

        if (result != TryReadHttpRequestResults.Success)
        {
            headers.Clear();
            requestPool?.Return(candidate);
            return result;
        }

        byte[] body = [];
        if (head.ContentLength > 0)
        {
            // The pipe buffer is recycled after AdvanceTo, so the body must be copied out.
            body = new byte[head.ContentLength];
            remaining.Slice(0, head.ContentLength).CopyTo(body);
            remaining = remaining.Slice(head.ContentLength);
        }

        candidate.Init(
            head.Method,
            localEndPoint?.Port ?? 0,
            head.Path,
            head.QueryString,
            head.KeepAlive,
            head.ContentLength,
            head.ContentType,
            isHttps,
            remoteEndPoint?.Address,
            remoteEndPoint?.Port ?? 0,
            HttpVersions.Http11,
            headers,
            body);

        request = candidate;
        buffer = remaining;

        return TryReadHttpRequestResults.Success;
    }

    /// <summary>
    /// Parse state and result of a request head (request line + headers) that may arrive over
    /// several reads. Start each request with <c>default</c>.
    /// </summary>
    internal struct RequestHead
    {
        public HttpMethodsEnum Method;
        public string Path;
        public string QueryString;
        public string ContentType;
        public bool KeepAlive;
        public bool ExpectContinue;

        /// <summary>Declared body length; 0 when the request has no Content-Length.</summary>
        public long ContentLength;

        // Progress carried between calls.
        internal bool RequestLineParsed;
        internal bool HasContentLength;
        internal bool HasTransferEncoding;
        internal int HostCount;
        internal long HeaderBytes;
    }

    /// <summary>
    /// Parses a request head incrementally. Every complete line found in <paramref name="buffer"/>
    /// is parsed exactly once and sliced off the buffer, so the caller can release it right away;
    /// an unterminated trailing line is left in place. Returns
    /// <see cref="TryReadHttpRequestResults.Incomplete"/> until the blank line ending the head has
    /// been consumed, then <see cref="TryReadHttpRequestResults.Success"/> with the buffer positioned
    /// at the body. All size limits are enforced on partial data, across calls.
    /// </summary>
    internal static TryReadHttpRequestResults TryReadRequestHead(
        ref ReadOnlySequence<byte> buffer,
        ref RequestHead head,
        List<HttpHeader> headers,
        int maxHeaderCount = 100,
        long maxRequestBodySize = 10 * 1024 * 1024,
        int maxHeadersTotalSize = DefaultMaxHeadersTotalSize,
        PathLookup? pathLookup = null)
    {
        var lines = new LineReader(buffer);
        try
        {
            return ReadHead(ref lines, ref buffer, ref head, headers, maxHeaderCount, maxRequestBodySize, maxHeadersTotalSize, pathLookup);
        }
        finally
        {
            lines.Dispose();
        }
    }

    private static TryReadHttpRequestResults ReadHead(
        ref LineReader lines,
        ref ReadOnlySequence<byte> buffer,
        ref RequestHead head,
        List<HttpHeader> headers,
        int maxHeaderCount,
        long maxRequestBodySize,
        int maxHeadersTotalSize,
        PathLookup? pathLookup)
    {
        // 1. Request line
        if (!head.RequestLineParsed)
        {
            if (!lines.TryReadLine(out ReadOnlySpan<byte> requestLine, out long requestLineLength))
            {
                // Reject over-long lines before the terminator arrives so a client cannot make
                // the server buffer unbounded data while waiting for an LF.
                return lines.Remaining > MaxRequestLineSize
                    ? TryReadHttpRequestResults.RequestLineTooLong
                    : TryReadHttpRequestResults.Incomplete;
            }

            if (requestLineLength > MaxRequestLineSize)
                return TryReadHttpRequestResults.RequestLineTooLong;

            var lineResult = TryParseRequestLine(requestLine, pathLookup, out var method, out string path, out string queryString, out var version);

            if (lineResult == RequestLineResult.Invalid)
                return TryReadHttpRequestResults.InvalidRequest;

            if (version != HttpVersions.Http11)
                return TryReadHttpRequestResults.UnsupportedHttpVersion;

            if (lineResult == RequestLineResult.UnsupportedMethod)
                return TryReadHttpRequestResults.UnsupportedMethod;

            headers.Clear();
            head = new RequestHead
            {
                RequestLineParsed = true,
                Method = method,
                Path = path,
                QueryString = queryString,
                ContentType = "",
                KeepAlive = true
            };
        }

        // 2. Headers
        while (true)
        {
            if (!lines.TryReadLine(out ReadOnlySpan<byte> line, out long lineLength))
            {
                long pending = lines.Remaining;
                if (pending > MaxRequestLineSize)
                    return TryReadHttpRequestResults.HeaderLineTooLong;
                if (head.HeaderBytes + pending > maxHeadersTotalSize)
                    return TryReadHttpRequestResults.HeadersTooLarge;

                // Everything up to the unterminated line has been parsed and can be released.
                buffer = buffer.Slice(lines.Position);
                return TryReadHttpRequestResults.Incomplete;
            }

            if (lineLength > MaxRequestLineSize)
                return TryReadHttpRequestResults.HeaderLineTooLong;

            head.HeaderBytes += lineLength + 1; // + LF
            if (head.HeaderBytes > maxHeadersTotalSize)
                return TryReadHttpRequestResults.HeadersTooLarge;

            if (line.Length == 0 || (line.Length == 1 && line[0] == CR))
                break;

            // RFC 9112 §5.2: obsolete line folding must be rejected (or unfolded); we reject.
            byte first = line[0];
            if (first == SP || first == HTAB)
                return TryReadHttpRequestResults.InvalidRequest;

            if (!TryParseHeaderLine(line, out KnownHeader known, out string? name, out ReadOnlySpan<byte> valueBytes))
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

            // The overwhelmingly common "Connection: keep-alive" gets a shared string and, below,
            // no token scan. Everything else is decoded: Latin-1 keeps obs-text bytes intact
            // instead of turning them into '?'.
            bool isPlainKeepAlive = known == KnownHeader.Connection && valueBytes.SequenceEqual("keep-alive"u8);
            string value = isPlainKeepAlive
                ? "keep-alive"
                : valueBytes.IsEmpty ? "" : Encoding.Latin1.GetString(valueBytes);

            // Some headers need special handling
            switch (known)
            {
                case KnownHeader.ContentLength:
                    // RFC 9112 §6.3: duplicate Content-Length is the canonical CL.CL smuggling vector.
                    if (head.HasContentLength)
                    {
                        return TryReadHttpRequestResults.InvalidRequest;
                    }

                    // Digits only: no sign, no whitespace, no thousands separators.
                    if (!TryParseContentLength(valueBytes, out long contentLength, out bool overflow))
                    {
                        // An all-digit value that overflows long is "too large"; anything else is malformed.
                        return overflow
                            ? TryReadHttpRequestResults.RequestBodyTooLarge
                            : TryReadHttpRequestResults.InvalidRequest;
                    }

                    if (contentLength > maxRequestBodySize || contentLength > int.MaxValue)
                    {
                        return TryReadHttpRequestResults.RequestBodyTooLarge;
                    }

                    head.ContentLength = contentLength;
                    head.HasContentLength = true;
                    break;

                case KnownHeader.Connection:
                    if (!isPlainKeepAlive && ContainsToken(value, "close"))
                    {
                        head.KeepAlive = false;
                    }
                    break;

                case KnownHeader.ContentType:
                    head.ContentType = value;
                    break;

                case KnownHeader.Host:
                    head.HostCount++;
                    break;

                case KnownHeader.TransferEncoding:
                    head.HasTransferEncoding = true;
                    break;

                case KnownHeader.Expect:
                    if (ContainsToken(value, "100-continue"))
                    {
                        head.ExpectContinue = true;
                    }
                    break;
            }

            headers.Add(new HttpHeader(name!, value));
        }

        // RFC 9112 §3.2: an HTTP/1.1 request must carry exactly one Host header.
        if (head.HostCount != 1)
            return TryReadHttpRequestResults.InvalidRequest;

        // RFC 9112 §6.1: Transfer-Encoding overrides Content-Length. Framing a TE request by
        // Content-Length is the TE.CL smuggling vector, so until chunked decoding exists the
        // only safe answer is to refuse the request and close the connection.
        if (head.HasTransferEncoding)
            return TryReadHttpRequestResults.UnsupportedTransferEncoding;

        buffer = buffer.Slice(lines.Position);
        return TryReadHttpRequestResults.Success;
    }

    /// <summary>
    /// Reads LF-terminated lines. When the input is one contiguous block (the normal case for a
    /// request that arrived in a single read) lines are plain slices of it; otherwise a line is
    /// a slice of its segment, or a copy in a pooled array when it straddles segments.
    /// </summary>
    private ref struct LineReader
    {
        private readonly ReadOnlySequence<byte> _sequence;
        private readonly ReadOnlySpan<byte> _span;
        private readonly bool _contiguous;
        private int _offset;
        private SequenceReader<byte> _reader;
        private byte[]? _rented;

        public LineReader(ReadOnlySequence<byte> sequence)
        {
            _sequence = sequence;
            _contiguous = sequence.IsSingleSegment;
            _offset = 0;
            _rented = null;

            if (_contiguous)
            {
                _span = sequence.FirstSpan;
                _reader = default;
            }
            else
            {
                _span = default;
                _reader = new SequenceReader<byte>(sequence);
            }
        }

        /// <summary>Bytes after the last line returned.</summary>
        public readonly long Remaining => _contiguous ? _span.Length - _offset : _reader.Remaining;

        /// <summary>Position right after the last line returned.</summary>
        public readonly SequencePosition Position => _contiguous ? _sequence.GetPosition(_offset) : _reader.Position;

        /// <summary>
        /// Reads the next line without its LF. <paramref name="length"/> is always the true line
        /// length; the content of a line longer than <see cref="MaxRequestLineSize"/> is not
        /// materialised because the caller rejects it. The returned span is only valid until
        /// the next call.
        /// </summary>
        public bool TryReadLine(out ReadOnlySpan<byte> line, out long length)
        {
            if (_contiguous)
            {
                ReadOnlySpan<byte> rest = _span[_offset..];
                int lf = rest.IndexOf(LF);
                if (lf < 0)
                {
                    line = default;
                    length = 0;
                    return false;
                }

                line = rest[..lf];
                length = lf;
                _offset += lf + 1;
                return true;
            }

            if (!_reader.TryReadTo(out ReadOnlySequence<byte> lineSequence, LF))
            {
                line = default;
                length = 0;
                return false;
            }

            length = lineSequence.Length;

            if (length > MaxRequestLineSize)
            {
                line = default;
            }
            else if (lineSequence.IsSingleSegment)
            {
                line = lineSequence.FirstSpan;
            }
            else
            {
                int size = (int)length;
                if (_rented is null || _rented.Length < size)
                {
                    if (_rented is not null)
                    {
                        ArrayPool<byte>.Shared.Return(_rented);
                    }
                    _rented = ArrayPool<byte>.Shared.Rent(size);
                }

                lineSequence.CopyTo(_rented);
                line = _rented.AsSpan(0, size);
            }

            return true;
        }

        public void Dispose()
        {
            if (_rented is not null)
            {
                ArrayPool<byte>.Shared.Return(_rented);
                _rented = null;
            }
        }
    }

    private static RequestLineResult TryParseRequestLine(
        ReadOnlySpan<byte> line,
        PathLookup? pathLookup,
        out HttpMethodsEnum method,
        out string path,
        out string queryString,
        out HttpVersions version)
    {
        method = HttpMethodsEnum.Get;
        path = "";
        queryString = "";
        version = HttpVersions.Unknown;

        if (line.Length > 0 && line[^1] == CR)
        {
            line = line[..^1];
        }

        if (line.Length == 0)
            return RequestLineResult.Invalid;

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
        if (targetSpan.ContainsAny(ControlBytes))
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
            queryString = pathSpan.Length > 1 ? Encoding.ASCII.GetString(pathSpan[1..]) : "";
        }
        else
        {
            int queryIndex = pathSpan.IndexOf((byte)'?');
            if (queryIndex >= 0)
            {
                path = CreatePath(pathSpan[..queryIndex], pathLookup);
                queryString = queryIndex + 1 < pathSpan.Length ? Encoding.ASCII.GetString(pathSpan[(queryIndex + 1)..]) : "";
            }
            else
            {
                path = CreatePath(pathSpan, pathLookup);
                queryString = "";
            }
        }

        return knownMethod ? RequestLineResult.Ok : RequestLineResult.UnsupportedMethod;
    }

    /// <summary>
    /// Turns the path bytes into a string, reusing the registered route string when the path
    /// matches a route so that the common case allocates nothing.
    /// </summary>
    private static string CreatePath(ReadOnlySpan<byte> pathBytes, PathLookup? pathLookup)
    {
        if (pathBytes.Length == 1 && pathBytes[0] == (byte)'/')
            return "/";

        if (pathLookup is not null && pathBytes.Length <= MaxInternedPathLength)
        {
            // Same decoding as the fallback below, so a lookup hit is exactly the string that
            // would otherwise have been allocated.
            Span<char> chars = stackalloc char[pathBytes.Length];
            int written = Encoding.ASCII.GetChars(pathBytes, chars);

            string? registered = pathLookup(chars[..written]);
            if (registered is not null)
                return registered;
        }

        return Encoding.ASCII.GetString(pathBytes);
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
        if (target.Length >= HttpSchemePrefix.Length && Ascii.EqualsIgnoreCase(target[..HttpSchemePrefix.Length], HttpSchemePrefix))
            schemeLen = HttpSchemePrefix.Length;
        else if (target.Length >= HttpsSchemePrefix.Length && Ascii.EqualsIgnoreCase(target[..HttpsSchemePrefix.Length], HttpsSchemePrefix))
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

    /// <summary>
    /// Splits a header line into its name and (trimmed, still encoded) value. Well-known names
    /// come back as shared strings, whatever their case on the wire; the value is left as bytes
    /// so the caller decides whether a string is needed at all.
    /// </summary>
    private static bool TryParseHeaderLine(
        ReadOnlySpan<byte> line,
        out KnownHeader known,
        out string? name,
        out ReadOnlySpan<byte> value)
    {
        known = KnownHeader.None;
        name = null;
        value = default;

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

        value = TrimAsciiWhitespace(line[(colonIndex + 1)..]);

        if (value.ContainsAny(FieldValueControlBytes))
            return false;

        if (!CommonHeaders.TryGetKnown(nameSpan, out name, out known))
        {
            name = Encoding.ASCII.GetString(nameSpan);
        }

        return true;
    }

    /// <summary>
    /// Strict Content-Length: one or more ASCII digits and nothing else. Returns false with
    /// <paramref name="overflow"/> set when the value is all digits but does not fit in a long.
    /// </summary>
    private static bool TryParseContentLength(ReadOnlySpan<byte> value, out long result, out bool overflow)
    {
        result = 0;
        overflow = false;

        if (value.IsEmpty)
            return false;

        foreach (byte b in value)
        {
            uint digit = (uint)(b - '0');
            if (digit > 9)
            {
                overflow = false;
                return false;
            }

            if (overflow)
                continue;

            if (result > (long.MaxValue - digit) / 10)
            {
                overflow = true;
            }
            else
            {
                result = result * 10 + digit;
            }
        }

        return !overflow;
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
        => span.Length != 0 && !span.ContainsAnyExcept(TokenBytes);

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
