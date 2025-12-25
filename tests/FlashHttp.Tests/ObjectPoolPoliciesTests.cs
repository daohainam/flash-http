using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Net;
using Xunit;

namespace FlashHttp.Tests;

public sealed class ObjectPoolPoliciesTests
{
    [Fact]
    public void FlashHttpRequestPooledObjectPolicy_Return_ResetsRequest()
    {
        var policy = new FlashHttpRequestPooledObjectPolicy();
        var req = policy.Create();

        req.Init(
            HttpMethodsEnum.Post,
            port: 123,
            path: "/x",
            queryString: "q=1",
            keepAliveRequested: false,
            contentLength: 1,
            contentType: "text/plain",
            isHttps: true,
            remoteAddress: IPAddress.Loopback,
            remotePort: 9,
            httpVersion: HttpVersions.Http11,
            headers: new List<HttpHeader> { new("A", "B") },
            body: [1]);

        Assert.True(policy.Return(req));

        Assert.Equal(HttpMethodsEnum.Get, req.Method);
        Assert.Equal("/", req.Path);
        Assert.Equal("", req.QueryString);
        Assert.True(req.KeepAliveRequested);
        Assert.Equal(0, req.ContentLength);
        Assert.Equal("", req.ContentType);
        Assert.False(req.IsHttps);
        Assert.Null(req.RemoteAddress);
        Assert.Equal(0, req.RemotePort);
        Assert.Equal(HttpVersions.Http11, req.HttpVersion);
    }

    [Fact]
    public void FlashHttpResponsePooledObjectPolicy_Create_SetsDefaults()
    {
        var policy = new FlashHttpResponsePooledObjectPolicy();
        var resp = policy.Create();
        Assert.Equal(404, resp.StatusCode);
        Assert.Equal(string.Empty, resp.ReasonPhrase);
        Assert.Empty(resp.Body);
    }

    [Fact]
    public void FlashHttpResponsePooledObjectPolicy_Return_ResetsResponse()
    {
        var policy = new FlashHttpResponsePooledObjectPolicy();
        var resp = policy.Create();

        resp.StatusCode = 200;
        resp.ReasonPhrase = "OK";
        resp.Body = [1, 2];
        resp.Headers.Add(new HttpHeader("X", "1"));

        Assert.True(policy.Return(resp));

        Assert.Equal(404, resp.StatusCode);
        Assert.Equal(string.Empty, resp.ReasonPhrase);
        Assert.Empty(resp.Body);
        Assert.Empty(resp.Headers);
    }

    [Fact]
    public void FlashHttpContextPooledObjectPolicy_Return_ResetsContext()
    {
        var policy = new FlashHttpContextPooledObjectPolicy();
        var ctx = policy.Create();

        ctx.Request = new FlashHttpRequest();
        ctx.Response = new FlashHttpResponse();
        ctx.Services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();

        Assert.True(policy.Return(ctx));
        Assert.Null(ctx.Request);
        Assert.Null(ctx.Response);
        Assert.Null(ctx.Services);
    }
}
