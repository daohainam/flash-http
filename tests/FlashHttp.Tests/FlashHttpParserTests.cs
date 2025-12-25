using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.ObjectPool;
using System;
using System.Buffers;
using System.Net;
using System.Text;
using Xunit;

namespace FlashHttp.Tests;

public sealed class FlashHttpParserTests
{
    private static ReadOnlySequence<byte> Seq(string s) => new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(s));

    [Fact]
    public void TryReadHttpRequest_Incomplete_WhenNoLF()
    {
        var buffer = Seq("GET / HTTP/1.1");

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out var req,
            out var keepAlive,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.Incomplete, r);
    }

    [Fact]
    public void TryReadHttpRequest_RequestLineTooLong()
    {
        var longPath = new string('a', 8200);
        var text = $"GET /{longPath} HTTP/1.1\r\n\r\n";
        var buffer = Seq(text);

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out _,
            out _,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.RequestLineTooLong, r);
    }

    [Fact]
    public void TryReadHttpRequest_InvalidRequest_WhenBadRequestLine()
    {
        var buffer = Seq("GET/ HTTP/1.1\r\n\r\n");

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out _,
            out _,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.InvalidRequest, r);
    }

    [Fact]
    public void TryReadHttpRequest_UnsupportedHttpVersion()
    {
        var buffer = Seq("GET / HTTP/1.0\r\n\r\n");

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out _,
            out _,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.UnsupportedHttpVersion, r);
    }

    [Fact]
    public void TryReadHttpRequest_HeaderLineTooLong()
    {
        var longValue = new string('b', 8200);
        var buffer = Seq($"GET / HTTP/1.1\r\nX: {longValue}\r\n\r\n");

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out _,
            out _,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.HeaderLineTooLong, r);
    }

    [Fact]
    public void TryReadHttpRequest_InvalidContentLength_Throws()
    {
        var buffer = Seq("GET / HTTP/1.1\r\nContent-Length: -1\r\n\r\n");

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = FlashHttpParser.TryReadHttpRequest(
                ref buffer,
                out _,
                out _,
                isHttps: false,
                remoteEndPoint: null,
                localEndPoint: null,
                requestPool: null);
        });
    }

    [Fact]
    public void TryReadHttpRequest_Incomplete_WhenBodyNotFullyAvailable()
    {
        var buffer = Seq("POST / HTTP/1.1\r\nContent-Length: 3\r\n\r\n12");

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out _,
            out _,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.Incomplete, r);
    }

    [Fact]
    public void TryReadHttpRequest_Success_ParsesHeadersBodyAndKeepAlive()
    {
        var remote = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 50000);
        var local = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 8080);

        var buffer = Seq(
            "POST /p?q=1 HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Connection: close\r\n" +
            "Content-Type: text/plain\r\n" +
            "Content-Length: 3\r\n" +
            "\r\n" +
            "abc");

        var poolProvider = new DefaultObjectPoolProvider();
        var pool = poolProvider.Create(new FlashHttpRequestPooledObjectPolicy());

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out var req,
            out var keepAlive,
            isHttps: true,
            remoteEndPoint: remote,
            localEndPoint: local,
            requestPool: pool);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.Success, r);
        Assert.Equal(HttpMethodsEnum.Post, req.Method);
        Assert.Equal("/p", req.Path);
        Assert.Equal("q=1", req.QueryString);
        Assert.Equal(8080, req.Port);
        Assert.Equal(3, req.ContentLength);
        Assert.Equal("text/plain", req.ContentType);
        Assert.True(req.IsHttps);
        Assert.Equal(remote.Address, req.RemoteAddress);
        Assert.Equal(remote.Port, req.RemotePort);
        Assert.False(keepAlive);
        Assert.Equal("abc", Encoding.UTF8.GetString(req.Body));
    }

    [Fact]
    public void TryReadHttpRequest_IgnoresInvalidHeaderLines_AndStopsOnCROnly()
    {
        var buffer = Seq(
            "GET / HTTP/1.1\r\n" +
            "BadHeaderWithoutColon\r\n" +
            "\r\n");

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out var req,
            out _,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.Success, r);
        Assert.Empty(req.Headers);
    }
}
