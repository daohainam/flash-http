using System;
using System.Collections.Generic;
using System.Text;

namespace FlashHttp.Abstractions;

public class FlashHttpResponse
{
    public int StatusCode { get; set; } = 404;
    public string ReasonPhrase { get; set; } = string.Empty;
    public byte[] Body { get; set; } = [];
    public List<HttpHeader> Headers { get; internal set; } = [];
}
