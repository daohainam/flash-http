using FlashHttp.Abstractions;
using FlashHttp.Server;
using System.Buffers;
using System.Text;
using Xunit;

namespace FlashHttp.Tests;

/// <summary>
/// Tests for security-related fixes including DoS protections
/// </summary>
public sealed class SecurityTests
{
    private static ReadOnlySequence<byte> Seq(string s) => new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(s));

    [Fact]
    public void TryReadHttpRequest_TooManyHeaders_ReturnsError()
    {
        // Build a request with more than the default max (100) headers
        var sb = new StringBuilder();
        sb.AppendLine("GET / HTTP/1.1");
        
        // Add 101 headers to exceed the default limit of 100
        for (int i = 0; i < 101; i++)
        {
            sb.AppendLine($"X-Header-{i}: value{i}");
        }
        sb.AppendLine();

        var buffer = Seq(sb.ToString());

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out _,
            out _,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null,
            maxHeaderCount: 100,
            maxRequestBodySize: 10 * 1024 * 1024);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.TooManyHeaders, r);
    }

    [Fact]
    public void TryReadHttpRequest_WithinHeaderLimit_Success()
    {
        // Build a request with exactly the max number of headers
        var sb = new StringBuilder();
        sb.AppendLine("GET / HTTP/1.1");
        
        // Add exactly 50 headers (within limit)
        for (int i = 0; i < 50; i++)
        {
            sb.AppendLine($"X-Header-{i}: value{i}");
        }
        sb.AppendLine();

        var buffer = Seq(sb.ToString());

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out var req,
            out _,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null,
            maxHeaderCount: 100,
            maxRequestBodySize: 10 * 1024 * 1024);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.Success, r);
        Assert.Equal(50, req.Headers.Count);
    }

    [Fact]
    public void TryReadHttpRequest_ExcessiveContentLength_ReturnsError()
    {
        // Try to send a Content-Length larger than the max allowed (default 10MB)
        var maxSize = 10 * 1024 * 1024; // 10 MB
        var excessiveSize = maxSize + 1;

        var buffer = Seq($"POST / HTTP/1.1\r\nContent-Length: {excessiveSize}\r\n\r\n");

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out _,
            out _,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null,
            maxHeaderCount: 100,
            maxRequestBodySize: maxSize);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.RequestBodyTooLarge, r);
    }

    [Fact]
    public void TryReadHttpRequest_ContentLengthAtMax_Success()
    {
        // Send a Content-Length exactly at the max allowed
        var maxSize = 1024; // 1 KB for this test
        var body = new string('a', maxSize);

        var buffer = Seq($"POST / HTTP/1.1\r\nContent-Length: {maxSize}\r\n\r\n{body}");

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out var req,
            out _,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null,
            maxHeaderCount: 100,
            maxRequestBodySize: maxSize);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.Success, r);
        Assert.Equal(maxSize, req.ContentLength);
        Assert.Equal(maxSize, req.Body.Length);
    }

    [Fact]
    public void TryReadHttpRequest_CustomLimits_AreRespected()
    {
        // Test with custom, stricter limits
        var customMaxHeaders = 5;
        var customMaxBodySize = 100;

        // Test header limit
        var sb = new StringBuilder();
        sb.AppendLine("GET / HTTP/1.1");
        for (int i = 0; i < 6; i++) // One more than limit
        {
            sb.AppendLine($"X-Header-{i}: value{i}");
        }
        sb.AppendLine();

        var buffer = Seq(sb.ToString());

        var r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out _,
            out _,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null,
            maxHeaderCount: customMaxHeaders,
            maxRequestBodySize: customMaxBodySize);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.TooManyHeaders, r);

        // Test body size limit
        buffer = Seq($"POST / HTTP/1.1\r\nContent-Length: {customMaxBodySize + 1}\r\n\r\n");

        r = FlashHttpParser.TryReadHttpRequest(
            ref buffer,
            out _,
            out _,
            isHttps: false,
            remoteEndPoint: null,
            localEndPoint: null,
            requestPool: null,
            maxHeaderCount: customMaxHeaders,
            maxRequestBodySize: customMaxBodySize);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.RequestBodyTooLarge, r);
    }

    [Fact]
    public void FlashHttpRequest_Reset_ClearsHeadersData()
    {
        // Test that Reset() properly clears headers to prevent data leaks
        var req = new FlashHttpRequest();
        var headers = new System.Collections.Generic.List<HttpHeader>
        {
            new HttpHeader("Authorization", "Bearer secret-token-12345"),
            new HttpHeader("X-API-Key", "super-secret-api-key")
        };

        req.Init(
            HttpMethodsEnum.Post,
            8080,
            "/api/data",
            "param=value",
            true,
            100,
            "application/json",
            true,
            System.Net.IPAddress.Parse("192.168.1.1"),
            54321,
            HttpVersions.Http11,
            headers,
            Encoding.UTF8.GetBytes("sensitive body data"));

        // Verify headers are set
        Assert.NotNull(req.Headers);
        Assert.Equal(2, req.Headers.Count);
        Assert.Contains(req.Headers, h => h.Name == "Authorization");

        // Reset the request
        req.Reset();

        // Verify headers list still exists but is cleared
        Assert.NotNull(req.Headers);
        Assert.Empty(req.Headers);
        
        // Verify other fields are reset
        Assert.Equal(HttpMethodsEnum.Get, req.Method);
        Assert.Equal("/", req.Path);
        Assert.Equal("", req.QueryString);
        Assert.Equal(0, req.ContentLength);
    }
}
