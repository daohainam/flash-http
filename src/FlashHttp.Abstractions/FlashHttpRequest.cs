using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Text;

namespace FlashHttp.Abstractions;

public class FlashHttpRequest
{
    public void Init(HttpMethodsEnum method, int port, string path, string queryString, bool keepAliveRequested, long contentLength, string contentType, bool isHttps, IPAddress? remoteAddress, int remotePort, HttpVersions httpVersion, List<HttpHeader> headers, byte[] body)
    {
        Method = method;
        Port = port;
        Path = path ?? throw new ArgumentNullException(nameof(path));
        QueryString = queryString ?? throw new ArgumentNullException(nameof(queryString));
        KeepAliveRequested = keepAliveRequested;
        ContentLength = contentLength;
        ContentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
        IsHttps = isHttps;
        RemoteAddress = remoteAddress;
        RemotePort = remotePort;
        HttpVersion = httpVersion;
        Headers = headers ?? throw new ArgumentNullException(nameof(headers));
        Body = body ?? throw new ArgumentNullException(nameof(body));
    }

    public HttpMethodsEnum Method { get; internal set; } = HttpMethodsEnum.Get;
    public int Port { get; internal set; } = 80;
    public string Path { get; internal set; } = "/";
    public string QueryString { get; internal set; } = "";
    public bool KeepAliveRequested { get; internal set; } = true;
    public long ContentLength { get; internal set; } = 0;
    public string ContentType { get; internal set; } = "";
    public bool IsHttps { get; internal set; } = false;
    public IPAddress? RemoteAddress { get; internal set; } = null;
    public int RemotePort { get; internal set; } = 0;
    public HttpVersions HttpVersion { get; internal set; } = HttpVersions.Http11;
    public List<HttpHeader> Headers { get; internal set; } = default!;
    public byte[] Body { get; internal set; } = default!;

    public void Reset()
    {
        Method = HttpMethodsEnum.Get;
        Path = "/";
        QueryString = "";
        KeepAliveRequested = true;
        ContentLength = 0;
        ContentType = "";
        IsHttps = false;
        RemoteAddress = null;
        RemotePort = 0;
        HttpVersion = HttpVersions.Http11;
        Headers = default!;
        Body = default!;
    }
}
