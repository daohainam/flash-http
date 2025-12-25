using FlashHttp.Abstractions;
using FlashHttp.Extensions;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Xunit;

namespace FlashHttp.Tests;

public sealed class FlashHttpRequestExtensionsTests
{
    private static FlashHttpRequest CreateRequest(string path, List<HttpHeader>? headers = null, byte[]? body = null)
    {
        var idx = path.IndexOf('?');
        var queryString = (idx >= 0 && idx < path.Length - 1) ? path[(idx + 1)..] : "";

        var req = new FlashHttpRequest();
        req.Init(
            HttpMethodsEnum.Get,
            port: 80,
            path: path,
            queryString: queryString,
            keepAliveRequested: true,
            contentLength: body?.Length ?? 0,
            contentType: "",
            isHttps: false,
            remoteAddress: IPAddress.Loopback,
            remotePort: 1,
            httpVersion: HttpVersions.Http11,
            headers: headers ?? [],
            body: body ?? []);
        return req;
    }

    [Fact]
    public void GetHeaderValue_FindsCaseInsensitive()
    {
        var req = CreateRequest("/", headers: [new HttpHeader("X-Test", "1")]);
        Assert.Equal("1", req.GetHeaderValue("x-test"));
    }

    [Fact]
    public void GetHeaderValue_Missing_ReturnsEmpty()
    {
        var req = CreateRequest("/", headers: []);
        Assert.Equal(string.Empty, req.GetHeaderValue("X-Missing"));
    }

    [Fact]
    public void GetBodyAsString_DefaultUtf8()
    {
        var req = CreateRequest("/", body: Encoding.UTF8.GetBytes("hi"));
        Assert.Equal("hi", req.GetBodyAsString());
    }

    private sealed record Payload(string Name);

    [Fact]
    public void GetBodyAsJson_Deserializes()
    {
        var json = "{\"Name\":\"a\"}";
        var req = CreateRequest("/", body: Encoding.UTF8.GetBytes(json));
        var obj = req.GetBodyAsJson<Payload>();
        Assert.NotNull(obj);
        Assert.Equal("a", obj!.Name);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("/a/b", 2)]
    [InlineData("/a/b?x=1", 2)]
    public void GetPathParts_SplitsAndStripsQuery(string path, int expected)
    {
        var req = CreateRequest(path);
        Assert.Equal(expected, req.GetPathParts().Length);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("/a", "")]
    [InlineData("/a?", "")]
    [InlineData("/a?x=1&y=2", "x=1&y=2")]
    public void GetQueryString_Extracts(string path, string expected)
    {
        var req = CreateRequest(path);
        Assert.Equal(expected, req.QueryString);
    }

    [Fact]
    public void ParseQueryParameters_Empty_ReturnsEmptyDict()
    {
        var req = CreateRequest("/a");
        Assert.Empty(req.ParseQueryParameters());
    }

    [Fact]
    public void ParseQueryParameters_ParsesAndDecodesAndCaseInsensitive()
    {
        var req = CreateRequest("/a?A=1&b=hello%20world&c=");
        var dict = req.ParseQueryParameters();

        Assert.Equal("1", dict["a"]);
        Assert.Equal("hello world", dict["B"]);
        Assert.Equal(string.Empty, dict["c"]);
    }
}
