using System;
using System.Collections.Generic;
using System.Text;

namespace FlashHttp.Server;
internal class CommonHeaders
{
    internal static ReadOnlySpan<byte> ContentTypeBytes => "Content-Type"u8;
    internal static ReadOnlySpan<byte> ContentLengthBytes => "Content-Length"u8;
    internal static ReadOnlySpan<byte> ConnectionBytes => "Connection"u8;
    internal static ReadOnlySpan<byte> HostBytes => "Host"u8;
}
