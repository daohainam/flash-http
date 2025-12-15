using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Text;

namespace FlashHttp.Abstractions;
public class FlashHttpRequest
{
    public HttpMethodsEnum Method { get; set; } = HttpMethodsEnum.Get;
    public int Port { get; init; } = 80;
    public string Path { get; init; } = "/";
    public bool KeepAliveRequested { get; init; } = true;
    public long ContentLength { get; init; } = 0;
    public string ContentType { get; init; } = "";
    public bool IsHttps { get; init; } = false;
    public IPAddress? RemoteAddress { get; init; } = null;
    public int RemotePort { get; init; } = 0;
    public HttpVersions HttpVersion { get; init; } = HttpVersions.Http11;
    public IEnumerable<HttpHeader> Headers { get; init; } = [];
    public byte[] Body { get; init; } = [];
}


