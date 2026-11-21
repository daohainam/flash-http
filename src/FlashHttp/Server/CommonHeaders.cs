using System.Text;

namespace FlashHttp.Server;

/// <summary>Request headers the parser acts on, beyond storing them.</summary>
internal enum KnownHeader : byte
{
    None,
    Host,
    ContentLength,
    ContentType,
    Connection,
    TransferEncoding,
    Expect
}

internal static class CommonHeaders
{
    /// <summary>
    /// Matches a header name against the names clients send most, case-insensitively, and
    /// returns a shared string for it so the name is not decoded into a new string on every
    /// request. Names outside the table are left to the caller.
    /// </summary>
    internal static bool TryGetKnown(ReadOnlySpan<byte> name, out string? canonical, out KnownHeader known)
    {
        known = KnownHeader.None;

        // Switching on the length first leaves at most a handful of comparisons per header.
        switch (name.Length)
        {
            case 2:
                if (Is(name, "TE"u8)) { canonical = "TE"; return true; }
                break;

            case 3:
                if (Is(name, "DNT"u8)) { canonical = "DNT"; return true; }
                if (Is(name, "Via"u8)) { canonical = "Via"; return true; }
                break;

            case 4:
                if (Is(name, "Host"u8)) { canonical = "Host"; known = KnownHeader.Host; return true; }
                if (Is(name, "Date"u8)) { canonical = "Date"; return true; }
                break;

            case 5:
                if (Is(name, "Range"u8)) { canonical = "Range"; return true; }
                break;

            case 6:
                if (Is(name, "Accept"u8)) { canonical = "Accept"; return true; }
                if (Is(name, "Cookie"u8)) { canonical = "Cookie"; return true; }
                if (Is(name, "Origin"u8)) { canonical = "Origin"; return true; }
                if (Is(name, "Expect"u8)) { canonical = "Expect"; known = KnownHeader.Expect; return true; }
                if (Is(name, "Pragma"u8)) { canonical = "Pragma"; return true; }
                break;

            case 7:
                if (Is(name, "Referer"u8)) { canonical = "Referer"; return true; }
                if (Is(name, "Upgrade"u8)) { canonical = "Upgrade"; return true; }
                break;

            case 8:
                if (Is(name, "Priority"u8)) { canonical = "Priority"; return true; }
                if (Is(name, "If-Match"u8)) { canonical = "If-Match"; return true; }
                if (Is(name, "If-Range"u8)) { canonical = "If-Range"; return true; }
                break;

            case 9:
                if (Is(name, "sec-ch-ua"u8)) { canonical = "sec-ch-ua"; return true; }
                if (Is(name, "Forwarded"u8)) { canonical = "Forwarded"; return true; }
                if (Is(name, "X-Real-IP"u8)) { canonical = "X-Real-IP"; return true; }
                break;

            case 10:
                if (Is(name, "Connection"u8)) { canonical = "Connection"; known = KnownHeader.Connection; return true; }
                if (Is(name, "User-Agent"u8)) { canonical = "User-Agent"; return true; }
                if (Is(name, "Keep-Alive"u8)) { canonical = "Keep-Alive"; return true; }
                if (Is(name, "tracestate"u8)) { canonical = "tracestate"; return true; }
                break;

            case 11:
                if (Is(name, "traceparent"u8)) { canonical = "traceparent"; return true; }
                break;

            case 12:
                if (Is(name, "Content-Type"u8)) { canonical = "Content-Type"; known = KnownHeader.ContentType; return true; }
                if (Is(name, "X-Request-Id"u8)) { canonical = "X-Request-Id"; return true; }
                break;

            case 13:
                if (Is(name, "Authorization"u8)) { canonical = "Authorization"; return true; }
                if (Is(name, "Cache-Control"u8)) { canonical = "Cache-Control"; return true; }
                if (Is(name, "If-None-Match"u8)) { canonical = "If-None-Match"; return true; }
                break;

            case 14:
                if (Is(name, "Content-Length"u8)) { canonical = "Content-Length"; known = KnownHeader.ContentLength; return true; }
                if (Is(name, "Sec-Fetch-Site"u8)) { canonical = "Sec-Fetch-Site"; return true; }
                if (Is(name, "Sec-Fetch-Mode"u8)) { canonical = "Sec-Fetch-Mode"; return true; }
                if (Is(name, "Sec-Fetch-Dest"u8)) { canonical = "Sec-Fetch-Dest"; return true; }
                if (Is(name, "Sec-Fetch-User"u8)) { canonical = "Sec-Fetch-User"; return true; }
                if (Is(name, "Accept-Charset"u8)) { canonical = "Accept-Charset"; return true; }
                break;

            case 15:
                if (Is(name, "Accept-Encoding"u8)) { canonical = "Accept-Encoding"; return true; }
                if (Is(name, "Accept-Language"u8)) { canonical = "Accept-Language"; return true; }
                if (Is(name, "X-Forwarded-For"u8)) { canonical = "X-Forwarded-For"; return true; }
                break;

            case 16:
                if (Is(name, "sec-ch-ua-mobile"u8)) { canonical = "sec-ch-ua-mobile"; return true; }
                if (Is(name, "Content-Encoding"u8)) { canonical = "Content-Encoding"; return true; }
                if (Is(name, "X-Forwarded-Host"u8)) { canonical = "X-Forwarded-Host"; return true; }
                if (Is(name, "X-Requested-With"u8)) { canonical = "X-Requested-With"; return true; }
                break;

            case 17:
                if (Is(name, "Transfer-Encoding"u8)) { canonical = "Transfer-Encoding"; known = KnownHeader.TransferEncoding; return true; }
                if (Is(name, "If-Modified-Since"u8)) { canonical = "If-Modified-Since"; return true; }
                if (Is(name, "X-Forwarded-Proto"u8)) { canonical = "X-Forwarded-Proto"; return true; }
                break;

            case 18:
                if (Is(name, "sec-ch-ua-platform"u8)) { canonical = "sec-ch-ua-platform"; return true; }
                break;

            case 19:
                if (Is(name, "If-Unmodified-Since"u8)) { canonical = "If-Unmodified-Since"; return true; }
                break;

            case 25:
                if (Is(name, "Upgrade-Insecure-Requests"u8)) { canonical = "Upgrade-Insecure-Requests"; return true; }
                break;
        }

        canonical = null;
        return false;
    }

    private static bool Is(ReadOnlySpan<byte> name, ReadOnlySpan<byte> known)
        => Ascii.EqualsIgnoreCase(name, known);
}
