using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Text;

namespace FlashHttp;
public class FlashHttpRequest
{
    public HttpMethodsEnum Method { get; set; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Url { get; init; }
    public required bool KeepAliveRequested { get; init; }
    public required string Hash { get; init; }
    public required string QueryString { get; init; }
    public required long ContentLength { get; init; }
    public required string ContentType { get; init; }
    public required bool IsHttps { get; init; }
    public required IPAddress? RemoteAddress { get; init; }
    public required int RemotePort { get; init; }
    public HttpVersions HttpVersion { get; init; } = HttpVersions.Http11;
}
