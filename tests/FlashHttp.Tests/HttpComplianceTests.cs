using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FlashHttp.Tests;

/// <summary>
/// HTTP/1.1 wire-level conformance and connection-stability tests. Parser-level cases use the
/// parser directly; everything that depends on framing, pipelining or connection lifetime
/// goes through a real socket.
/// </summary>
public sealed class HttpComplianceTests
{
    private static ReadOnlySequence<byte> Seq(string s) => new(Encoding.Latin1.GetBytes(s));

    private static FlashHttpParser.TryReadHttpRequestResults Parse(string text, out FlashHttpRequest req, out bool keepAlive, out bool expectContinue)
    {
        var buffer = Seq(text);
        return FlashHttpParser.TryReadHttpRequest(
            ref buffer, out req, out keepAlive, out expectContinue,
            isHttps: false, remoteEndPoint: null, localEndPoint: null, requestPool: null);
    }

    private static FlashHttpParser.TryReadHttpRequestResults Parse(string text)
        => Parse(text, out _, out _, out _);

    // ---------------------------------------------------------------- parser

    [Fact]
    public void Parser_TransferEncodingChunked_IsRejectedAsUnsupported()
    {
        var r = Parse("POST / HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n");
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.UnsupportedTransferEncoding, r);
    }

    [Fact]
    public void Parser_TransferEncodingWithContentLength_IsRejected()
    {
        // TE.CL smuggling: must never be framed by Content-Length.
        var r = Parse("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\nhello");
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.UnsupportedTransferEncoding, r);
    }

    [Fact]
    public void Parser_MissingHost_IsInvalid()
    {
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.InvalidRequest, Parse("GET / HTTP/1.1\r\n\r\n"));
    }

    [Fact]
    public void Parser_DuplicateHost_IsInvalid()
    {
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.InvalidRequest, Parse("GET / HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n"));
    }

    [Fact]
    public void Parser_WhitespaceBeforeColon_IsInvalid()
    {
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.InvalidRequest, Parse("POST / HTTP/1.1\r\nHost: a\r\nContent-Length : 5\r\n\r\nhello"));
    }

    [Fact]
    public void Parser_ObsoleteLineFolding_IsInvalid()
    {
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.InvalidRequest, Parse("GET / HTTP/1.1\r\nHost: a\r\nX-A: 1\r\n X-B: 2\r\n\r\n"));
    }

    [Fact]
    public void Parser_ConnectionCloseInTokenList_DisablesKeepAlive()
    {
        var r = Parse("GET / HTTP/1.1\r\nHost: a\r\nConnection: keep-alive, Close\r\n\r\n", out _, out bool keepAlive, out _);
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.Success, r);
        Assert.False(keepAlive);
    }

    [Fact]
    public void Parser_AbsoluteFormTarget_IsReducedToOriginForm()
    {
        var r = Parse("GET http://example.com:8080/foo/bar?x=1 HTTP/1.1\r\nHost: example.com\r\n\r\n", out var req, out _, out _);
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.Success, r);
        Assert.Equal("/foo/bar", req.Path);
        Assert.Equal("x=1", req.QueryString);

        r = Parse("GET HTTP://example.com HTTP/1.1\r\nHost: example.com\r\n\r\n", out req, out _, out _);
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.Success, r);
        Assert.Equal("/", req.Path);
        Assert.Equal("", req.QueryString);

        r = Parse("GET http://example.com?y=2 HTTP/1.1\r\nHost: example.com\r\n\r\n", out req, out _, out _);
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.Success, r);
        Assert.Equal("/", req.Path);
        Assert.Equal("y=2", req.QueryString);
    }

    [Fact]
    public void Parser_AsteriskForm_OnlyForOptions()
    {
        var r = Parse("OPTIONS * HTTP/1.1\r\nHost: a\r\n\r\n", out var req, out _, out _);
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.Success, r);
        Assert.Equal("*", req.Path);

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.InvalidRequest, Parse("GET * HTTP/1.1\r\nHost: a\r\n\r\n"));
    }

    [Fact]
    public void Parser_UnknownMethodToken_IsUnsupportedMethod()
    {
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.UnsupportedMethod, Parse("TRACE / HTTP/1.1\r\nHost: a\r\n\r\n"));
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.InvalidRequest, Parse("GE(T / HTTP/1.1\r\nHost: a\r\n\r\n"));
    }

    [Fact]
    public void Parser_OversizeRequestLineWithoutLF_IsRejectedImmediately()
    {
        var r = Parse("GET /" + new string('a', 9000));
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.RequestLineTooLong, r);
    }

    [Fact]
    public void Parser_OversizeHeaderLineWithoutLF_IsRejectedImmediately()
    {
        var r = Parse("GET / HTTP/1.1\r\nX: " + new string('a', 9000));
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.HeaderLineTooLong, r);
    }

    [Fact]
    public void Parser_HeaderSectionOverTotalLimit_IsHeadersTooLarge()
    {
        var sb = new StringBuilder("GET / HTTP/1.1\r\nHost: a\r\n");
        for (int i = 0; i < 10; i++)
        {
            sb.Append("X-").Append(i).Append(": ").Append('v', 4000).Append("\r\n");
        }
        sb.Append("\r\n");

        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.HeadersTooLarge, Parse(sb.ToString()));
    }

    [Fact]
    public void Parser_ExpectContinue_IsReportedWhileBodyIncomplete()
    {
        var r = Parse("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n", out _, out _, out bool expectContinue);
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.IncompleteBody, r);
        Assert.True(expectContinue);
    }

    [Fact]
    public void Parser_ContentLength_SignOrWhitespace_IsInvalid()
    {
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.InvalidRequest, Parse("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: +5\r\n\r\nhello"));
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.InvalidRequest, Parse("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 5 5\r\n\r\nhello"));
    }

    [Fact]
    public void Parser_ContentLength_OverflowingLong_IsTooLarge()
    {
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.RequestBodyTooLarge, Parse("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 99999999999999999999999\r\n\r\n"));
    }

    [Fact]
    public void Parser_HeaderValue_PreservesLatin1Bytes()
    {
        var r = Parse("GET / HTTP/1.1\r\nHost: a\r\nX-Name: café\r\n\r\n", out var req, out _, out _);
        Assert.Equal(FlashHttpParser.TryReadHttpRequestResults.Success, r);
        Assert.Contains(req.Headers, h => h.Name == "X-Name" && h.Value == "café");
    }

    // ---------------------------------------------------------------- socket

    private static FlashHttpServer CreateServer(Action<FlashHttpServerOptions>? configure = null)
    {
        var options = new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0, MetricsEnabled = false };
        configure?.Invoke(options);
        return new FlashHttpServer(options, new ServiceCollection().BuildServiceProvider());
    }

    private sealed class RawResponse
    {
        public string StatusLine = "";
        public int StatusCode;
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = [];
    }

    /// <summary>Reads one response framed by Content-Length (or up to EOF when there is none).</summary>
    private static async Task<RawResponse> ReadResponseAsync(NetworkStream stream, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var buf = new byte[4096];
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(buf, ct);
            if (n == 0) break;
            ms.Write(buf, 0, n);
            headerEnd = IndexOf(ms.GetBuffer(), (int)ms.Length, "\r\n\r\n"u8);
        }

        Assert.True(headerEnd >= 0, "No complete header section received");

        var head = Encoding.ASCII.GetString(ms.GetBuffer(), 0, headerEnd);
        var lines = head.Split("\r\n");
        var resp = new RawResponse { StatusLine = lines[0] };
        resp.StatusCode = int.Parse(lines[0].Split(' ')[1]);
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            resp.Headers[line[..c]] = line[(c + 1)..].Trim();
        }

        int bodyStart = headerEnd + 4;
        int have = (int)ms.Length - bodyStart;

        if (resp.Headers.TryGetValue("Content-Length", out var clText))
        {
            int cl = int.Parse(clText);
            while (have < cl)
            {
                int n = await stream.ReadAsync(buf, ct);
                if (n == 0) break;
                ms.Write(buf, 0, n);
                have += n;
            }
            resp.Body = ms.GetBuffer().AsSpan(bodyStart, Math.Min(cl, have)).ToArray();
            // Anything beyond Content-Length is left for the caller via the returned stream state;
            // our tests only pipeline when the first response has a fixed length.
            if (have > cl)
            {
                Leftover = ms.GetBuffer().AsSpan(bodyStart + cl, have - cl).ToArray();
            }
            else
            {
                Leftover = [];
            }
        }
        else
        {
            resp.Body = ms.GetBuffer().AsSpan(bodyStart, have).ToArray();
            Leftover = [];
        }

        return resp;
    }

    [ThreadStatic] private static byte[]? _leftover;
    private static byte[] Leftover
    {
        get => _leftover ?? [];
        set => _leftover = value;
    }

    private static int IndexOf(byte[] haystack, int length, ReadOnlySpan<byte> needle)
        => haystack.AsSpan(0, length).IndexOf(needle);

    private static async Task<(TcpClient client, NetworkStream stream)> ConnectAsync(int port, CancellationToken ct)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, ct);
        return (client, client.GetStream());
    }

    [Fact]
    public async Task Server_LargeRequestBody_RoundTrips()
    {
        using var server = CreateServer();
        server.WithHandler(HttpMethodsEnum.Post, "/echo-length", static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = Encoding.ASCII.GetBytes(ctx.Request.Body.Length.ToString());
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);

        var resp = await client.PostAsync("/echo-length", new ByteArrayContent(payload), cts.Token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(payload.Length.ToString(), await resp.Content.ReadAsStringAsync(cts.Token));

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Server_GarbageWithoutLineFeed_Gets414AndClose()
    {
        using var server = CreateServer();
        server.WithHandler(HttpMethodsEnum.Get, "/", static (ctx, _) => { ctx.Response.StatusCode = 200; return ValueTask.CompletedTask; });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        var (tcp, stream) = await ConnectAsync(port, cts.Token);
        using (tcp)
        {
            var garbage = new byte[100 * 1024];
            Array.Fill(garbage, (byte)'A');
            await stream.WriteAsync(garbage, cts.Token);

            var resp = await ReadResponseAsync(stream, cts.Token);
            Assert.Equal(414, resp.StatusCode);
            Assert.Equal("close", resp.Headers["Connection"]);
            Assert.True(resp.Headers.ContainsKey("Date"));

            // Server closes the socket afterwards.
            var tail = new byte[16];
            Assert.Equal(0, await stream.ReadAsync(tail, cts.Token));
        }

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Server_HandlerThrows_Returns500WithClose()
    {
        using var server = CreateServer();
        server.WithHandler(HttpMethodsEnum.Get, "/boom", static (_, _) => throw new InvalidOperationException("boom"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        var (tcp, stream) = await ConnectAsync(port, cts.Token);
        using (tcp)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /boom HTTP/1.1\r\nHost: localhost\r\n\r\n"), cts.Token);
            var resp = await ReadResponseAsync(stream, cts.Token);

            Assert.Equal(500, resp.StatusCode);
            Assert.Equal("close", resp.Headers["Connection"]);
            Assert.Equal("Internal Server Error", Encoding.UTF8.GetString(resp.Body));
        }

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Server_Head_UsesGetHandler_WithoutBody_AndKeepsFraming()
    {
        using var server = CreateServer();
        server.WithHandler(HttpMethodsEnum.Get, "/r", static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = "hello world"u8.ToArray();
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        var (tcp, stream) = await ConnectAsync(port, cts.Token);
        using (tcp)
        {
            // Pipeline HEAD then GET: if HEAD leaked a body, the GET response would be misframed.
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HEAD /r HTTP/1.1\r\nHost: localhost\r\n\r\n" +
                "GET /r HTTP/1.1\r\nHost: localhost\r\n\r\n"), cts.Token);

            var all = new MemoryStream();
            var buf = new byte[4096];
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                readCts.CancelAfter(500);
                int n;
                try { n = await stream.ReadAsync(buf, readCts.Token); }
                catch (OperationCanceledException) { break; }
                if (n == 0) break;
                all.Write(buf, 0, n);
                var textSoFar = Encoding.ASCII.GetString(all.GetBuffer(), 0, (int)all.Length);
                if (textSoFar.EndsWith("hello world")) break;
            }

            var text = Encoding.ASCII.GetString(all.GetBuffer(), 0, (int)all.Length);
            int secondStatus = text.IndexOf("HTTP/1.1 200", 1, StringComparison.Ordinal);
            Assert.True(secondStatus > 0, "Second response not found: " + text);

            var headPart = text[..secondStatus];
            Assert.Contains("Content-Length: 11", headPart);
            Assert.EndsWith("\r\n\r\n", headPart); // no body after HEAD headers

            var getPart = text[secondStatus..];
            Assert.EndsWith("\r\n\r\nhello world", getPart);
        }

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Server_NoContent204_SendsNoBodyAndNoContentLength()
    {
        using var server = CreateServer();
        server.WithHandler(HttpMethodsEnum.Delete, "/x", static (ctx, _) =>
        {
            ctx.Response.StatusCode = 204;
            ctx.Response.Body = "should not be sent"u8.ToArray();
            return ValueTask.CompletedTask;
        });
        server.WithHandler(HttpMethodsEnum.Get, "/ping", static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = "pong"u8.ToArray();
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        var (tcp, stream) = await ConnectAsync(port, cts.Token);
        using (tcp)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "DELETE /x HTTP/1.1\r\nHost: localhost\r\n\r\n" +
                "GET /ping HTTP/1.1\r\nHost: localhost\r\n\r\n"), cts.Token);

            var first = await ReadResponseAsync(stream, cts.Token);
            Assert.Equal(204, first.StatusCode);
            Assert.False(first.Headers.ContainsKey("Content-Length"));

            // With no Content-Length the reader slurped to the end of what arrived; the second
            // response must be intact inside it, proving nothing was inserted between them.
            var rest = Encoding.ASCII.GetString(first.Body);
            if (!rest.Contains("pong"))
            {
                var more = await ReadResponseAsync(stream, cts.Token);
                rest += Encoding.ASCII.GetString(more.Body);
                Assert.Equal(200, more.StatusCode);
            }
            Assert.DoesNotContain("should not be sent", rest);
            Assert.EndsWith("pong", rest);
        }

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Server_ExpectContinue_Receives100BeforeBody()
    {
        using var server = CreateServer();
        server.WithHandler(HttpMethodsEnum.Post, "/up", static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = ctx.Request.Body;
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        var (tcp, stream) = await ConnectAsync(port, cts.Token);
        using (tcp)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "POST /up HTTP/1.1\r\nHost: localhost\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n"), cts.Token);

            var interim = await ReadResponseAsync(stream, cts.Token);
            Assert.Equal(100, interim.StatusCode);

            await stream.WriteAsync(Encoding.ASCII.GetBytes("hello"), cts.Token);
            var final = await ReadResponseAsync(stream, cts.Token);
            Assert.Equal(200, final.StatusCode);
            Assert.Equal("hello", Encoding.ASCII.GetString(final.Body));
        }

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Server_ResponsesCarryDateHeader()
    {
        using var server = CreateServer();
        server.WithHandler(HttpMethodsEnum.Get, "/", static (ctx, _) => { ctx.Response.StatusCode = 200; return ValueTask.CompletedTask; });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var resp = await client.GetAsync("/", cts.Token);
        Assert.NotNull(resp.Headers.Date);
        Assert.True((DateTimeOffset.UtcNow - resp.Headers.Date!.Value).Duration() < TimeSpan.FromMinutes(1));

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Server_Http10_Gets505_UnknownMethod_Gets501()
    {
        using var server = CreateServer();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        var (tcp1, s1) = await ConnectAsync(port, cts.Token);
        using (tcp1)
        {
            await s1.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nHost: localhost\r\n\r\n"), cts.Token);
            Assert.Equal(505, (await ReadResponseAsync(s1, cts.Token)).StatusCode);
        }

        var (tcp2, s2) = await ConnectAsync(port, cts.Token);
        using (tcp2)
        {
            await s2.WriteAsync(Encoding.ASCII.GetBytes("TRACE / HTTP/1.1\r\nHost: localhost\r\n\r\n"), cts.Token);
            Assert.Equal(501, (await ReadResponseAsync(s2, cts.Token)).StatusCode);
        }

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Server_InvalidResponseHeader_Becomes500()
    {
        using var server = CreateServer();
        server.WithHandler(HttpMethodsEnum.Get, "/", static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add(new HttpHeader("X-Evil", "a\r\nSet-Cookie: pwned=1"));
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var resp = await client.GetAsync("/", cts.Token);
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.False(resp.Headers.Contains("Set-Cookie"));

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Server_StopAsync_WhileRunning_CompletesCleanly()
    {
        using var server = CreateServer();
        server.WithHandler(HttpMethodsEnum.Get, "/", static (ctx, _) => { ctx.Response.StatusCode = 200; return ValueTask.CompletedTask; });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var startTask = server.StartAsync(cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        // Keep an idle keep-alive connection open; a graceful stop must close it.
        var (tcp, stream) = await ConnectAsync(port, cts.Token);
        using (tcp)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"), cts.Token);
            Assert.Equal(200, (await ReadResponseAsync(stream, cts.Token)).StatusCode);

            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.StopAsync(stopTimeout.Token);

            // StartAsync returns normally on a graceful stop (no exception).
            await startTask.WaitAsync(TimeSpan.FromSeconds(5));

            var tail = new byte[16];
            Assert.Equal(0, await stream.ReadAsync(tail, cts.Token));
        }

        // New connections are refused after stop.
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port, cts.Token).AsTask());
    }
}
