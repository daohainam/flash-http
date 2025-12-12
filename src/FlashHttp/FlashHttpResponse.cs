using FlashHttp.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;

namespace FlashHttp;

public class FlashHttpResponse
{
    public int StatusCode { get; internal set; } = 404;
    public string ReasonPhrase { get; internal set; } = string.Empty;
    public byte[] Body { get; internal set; } = [];
    public List<HttpHeader> Headers { get; internal set; } = [];
}
