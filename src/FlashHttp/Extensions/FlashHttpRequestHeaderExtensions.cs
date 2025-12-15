using FlashHttp.Abstractions;
using FlashHttp.Helpers;
using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace FlashHttp.Extensions;
public static class FlashHttpRequestHeaderExtensions
{
    private static readonly ConditionalWeakTable<FlashHttpRequest, FlashHttpHeaders> _rawHeaders = new();

    public static void SetRawHeaders(this FlashHttpRequest request, FlashHttpHeaders headers)
    {
        _rawHeaders.Remove(request);
        _rawHeaders.Add(request, headers);
    }

    public static void ReleaseRawHeaders(this FlashHttpRequest request)
    {
        if (_rawHeaders.TryGetValue(request, out var headers))
        {
            _rawHeaders.Remove(request);
            FlashHttpHeadersPool.Return(headers);
        }
    }

    public static bool TryGetHeaderBytes(this FlashHttpRequest request, ReadOnlySpan<byte> headerNameAscii, out ReadOnlySpan<byte> value)
    {
        if (_rawHeaders.TryGetValue(request, out var headers))
            return headers.TryGetValueBytes(headerNameAscii, out value);

        value = default;
        return false;
    }

    public static bool TryGetHeaderString(this FlashHttpRequest request, ReadOnlySpan<byte> headerNameAscii, out string? value, bool cache = true)
    {
        if (_rawHeaders.TryGetValue(request, out var headers))
            return headers.TryGetValueString(headerNameAscii, out value, cache);

        value = null;
        return false;
    }

    public static bool TryGetHeaderString(this FlashHttpRequest request, string headerNameAscii, out string? value, bool cache = true)
    {
        int len = headerNameAscii.Length;
        Span<byte> tmp = len <= 256 ? stackalloc byte[len] : new byte[len];
        Encoding.ASCII.GetBytes(headerNameAscii.AsSpan(), tmp);
        return request.TryGetHeaderString(tmp, out value, cache);
    }

    public static bool TryGetHeaderInt32(this FlashHttpRequest request, ReadOnlySpan<byte> headerNameAscii, out int value)
    {
        if (_rawHeaders.TryGetValue(request, out var headers))
            return headers.TryGetInt32(headerNameAscii, out value);

        value = 0;
        return false;
    }

    // ===== Known header fast paths =====

    public static bool TryGetAuthorizationBytesFast(this FlashHttpRequest request, out ReadOnlySpan<byte> value)
    {
        if (_rawHeaders.TryGetValue(request, out var headers))
        {
            if (headers.TryGetKnownValueBytes(KnownHeader.Authorization, out value))
                return true;

            // fallback scan if index wasn't set for some reason
            return headers.TryGetValueBytes("Authorization"u8, out value);
        }

        value = default;
        return false;
    }

    public static bool TryGetHostBytesFast(this FlashHttpRequest request, out ReadOnlySpan<byte> value)
    {
        if (_rawHeaders.TryGetValue(request, out var headers))
        {
            if (headers.TryGetKnownValueBytes(KnownHeader.Host, out value))
                return true;

            return headers.TryGetValueBytes("Host"u8, out value);
        }

        value = default;
        return false;
    }

    public static bool TryGetContentTypeStringFast(this FlashHttpRequest request, out string? value, bool cache = true)
    {
        if (_rawHeaders.TryGetValue(request, out var headers))
        {
            if (headers.TryGetKnownValueString(KnownHeader.ContentType, out value, cache))
                return true;

            return headers.TryGetValueString("Content-Type"u8, out value, cache);
        }

        value = null;
        return false;
    }

    public static bool TryGetContentLengthFast(this FlashHttpRequest request, out int value)
    {
        if (_rawHeaders.TryGetValue(request, out var headers))
        {
            if (headers.TryGetKnownInt32(KnownHeader.ContentLength, out value))
                return true;

            return headers.TryGetInt32("Content-Length"u8, out value);
        }

        value = 0;
        return false;
    }
}
