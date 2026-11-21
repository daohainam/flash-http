using FlashHttp.Abstractions;
using FlashHttp.Extensions;
using FlashHttp.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;
using System.Buffers;
using System.Text;
using Xunit;

using Results = FlashHttp.Server.FlashHttpParser.TryReadHttpRequestResults;

namespace FlashHttp.Tests;

/// <summary>
/// The read path parses a request head once, line by line as it arrives, and drains the body
/// out of the input pipe instead of buffering a whole request and re-parsing it on every read.
/// </summary>
public sealed class ReadPathTests
{
    private static ReadOnlySequence<byte> Seq(string s) => new(Encoding.Latin1.GetBytes(s));

    private static string Text(ReadOnlySequence<byte> sequence) => Encoding.Latin1.GetString(sequence.ToArray());

    // ------------------------------------------------------------- parser

    [Fact]
    public void Head_ParsedAcrossCalls_ConsumesOnlyCompleteLines()
    {
        var headers = new List<HttpHeader>();
        var head = new FlashHttpParser.RequestHead();

        var buffer = Seq("GET /a?b=1 HTTP/1.1\r\nHost: x\r\nUser-Ag");
        Assert.Equal(Results.Incomplete, FlashHttpParser.TryReadRequestHead(ref buffer, ref head, headers));
        Assert.Equal("User-Ag", Text(buffer));
        Assert.Single(headers);

        buffer = Seq("User-Agent: t\r\n\r\nrest");
        Assert.Equal(Results.Success, FlashHttpParser.TryReadRequestHead(ref buffer, ref head, headers));
        Assert.Equal("rest", Text(buffer));

        Assert.Equal(HttpMethodsEnum.Get, head.Method);
        Assert.Equal("/a", head.Path);
        Assert.Equal("b=1", head.QueryString);
        Assert.True(head.KeepAlive);
        Assert.Equal(["Host", "User-Agent"], headers.Select(h => h.Name));
    }

    [Fact]
    public void Head_IncompleteRequestLine_LeavesBufferUntouched()
    {
        var headers = new List<HttpHeader>();
        var head = new FlashHttpParser.RequestHead();

        var buffer = Seq("GET /a HT");
        Assert.Equal(Results.Incomplete, FlashHttpParser.TryReadRequestHead(ref buffer, ref head, headers));
        Assert.Equal("GET /a HT", Text(buffer));
    }

    [Fact]
    public void Head_TotalSizeLimit_CountsBytesFromEarlierCalls()
    {
        var headers = new List<HttpHeader>();
        var head = new FlashHttpParser.RequestHead();

        var buffer = Seq("GET / HTTP/1.1\r\nHost: x\r\nA: " + new string('a', 40) + "\r\n");
        Assert.Equal(Results.Incomplete, FlashHttpParser.TryReadRequestHead(ref buffer, ref head, headers, maxHeadersTotalSize: 64));

        // 54 bytes are already consumed; 20 pending bytes without a terminator cross the limit.
        buffer = Seq("B: " + new string('b', 17));
        Assert.Equal(Results.HeadersTooLarge, FlashHttpParser.TryReadRequestHead(ref buffer, ref head, headers, maxHeadersTotalSize: 64));
    }

    [Fact]
    public void Head_HeaderCountLimit_CountsHeadersFromEarlierCalls()
    {
        var headers = new List<HttpHeader>();
        var head = new FlashHttpParser.RequestHead();

        var buffer = Seq("GET / HTTP/1.1\r\nHost: x\r\nA: 1\r\n");
        Assert.Equal(Results.Incomplete, FlashHttpParser.TryReadRequestHead(ref buffer, ref head, headers, maxHeaderCount: 2));

        buffer = Seq("B: 2\r\n\r\n");
        Assert.Equal(Results.TooManyHeaders, FlashHttpParser.TryReadRequestHead(ref buffer, ref head, headers, maxHeaderCount: 2));
    }

    [Fact]
    public void PooledRequest_ReusesItsHeaderList()
    {
        var pool = new DefaultObjectPoolProvider().Create(new FlashHttpRequestPooledObjectPolicy());

        var first = ParsePooled("GET /one HTTP/1.1\r\nHost: a\r\nX-One: 1\r\n\r\n", pool);
        var firstHeaders = first.Headers;
        pool.Return(first);

        var second = ParsePooled("GET /two HTTP/1.1\r\nHost: b\r\n\r\n", pool);

        Assert.Same(first, second);
        Assert.Same(firstHeaders, second.Headers);
        Assert.Equal("/two", second.Path);
        Assert.Equal(["Host"], second.Headers.Select(h => h.Name));
        Assert.Equal("b", second.GetHeaderValue("Host"));
    }

    private static FlashHttpRequest ParsePooled(string text, ObjectPool<FlashHttpRequest> pool)
    {
        var buffer = Seq(text);
        var result = FlashHttpParser.TryReadHttpRequest(
            ref buffer, out var request, out _,
            isHttps: false, remoteEndPoint: null, localEndPoint: null, requestPool: pool);

        Assert.Equal(Results.Success, result);
        return request;
    }

    // --------------------------------------------------------- connection

    [Fact]
    public async Task Request_DeliveredOneByteAtATime_IsHandledOnce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new DuplexTestStream();

        int calls = 0;
        string? seen = null;

        var connection = ConnectionHarness.RunAsync(stream, (ctx, _) =>
        {
            calls++;
            var r = ctx.Request;
            seen = $"{r.Method} {r.Path}?{r.QueryString} {r.ContentType} {r.GetHeaderValue("X-Tag")} {r.Headers.Count} {Encoding.ASCII.GetString(r.Body)}";
            ctx.Response.StatusCode = 200;
            return ValueTask.CompletedTask;
        }, cts.Token);

        const string request =
            "POST /items?id=7 HTTP/1.1\r\nHost: test\r\nContent-Type: text/plain\r\nX-Tag: abc\r\nContent-Length: 11\r\n\r\nhello world";

        foreach (char c in request)
        {
            stream.Send(c.ToString());
        }

        await stream.WaitForWrittenAsync(w => w.Contains("\r\n\r\n", StringComparison.Ordinal), cts.Token);
        stream.CompleteInput();
        await connection;

        Assert.Equal(1, calls);
        Assert.Equal("Post /items?id=7 text/plain abc 4 hello world", seen);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", stream.WrittenText);
    }

    [Fact]
    public async Task LargeBody_DeliveredInSmallReads_ArrivesIntact()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new DuplexTestStream();

        byte[] payload = new byte[300 * 1024];
        Random.Shared.NextBytes(payload);
        bool intact = false;

        var connection = ConnectionHarness.RunAsync(stream, (ctx, _) =>
        {
            intact = ctx.Request.Body.AsSpan().SequenceEqual(payload) && ctx.Request.ContentLength == payload.Length;
            ctx.Response.StatusCode = 200;
            return ValueTask.CompletedTask;
        }, cts.Token);

        stream.Send($"POST /up HTTP/1.1\r\nHost: test\r\nContent-Length: {payload.Length}\r\n\r\n");
        for (int offset = 0; offset < payload.Length; offset += 1000)
        {
            stream.Send(payload.AsSpan(offset, Math.Min(1000, payload.Length - offset)).ToArray());
        }

        await stream.WaitForWrittenAsync(w => w.Contains("\r\n\r\n", StringComparison.Ordinal), cts.Token);
        stream.CompleteInput();
        await connection;

        Assert.True(intact);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", stream.WrittenText);
    }

    [Fact]
    public async Task PipelinedRequest_FollowingABodySplitAcrossReads_IsNotSwallowedByTheBody()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new DuplexTestStream();
        var seen = new List<string>();

        var connection = ConnectionHarness.RunAsync(stream, (ctx, _) =>
        {
            seen.Add($"{ctx.Request.Method} {ctx.Request.Path} {Encoding.ASCII.GetString(ctx.Request.Body)}");
            ctx.Response.StatusCode = 200;
            return ValueTask.CompletedTask;
        }, cts.Token);

        // The body tail and the next request arrive in the same read.
        stream.Send("POST /a HTTP/1.1\r\nHost: test\r\nContent-Length: 10\r\n\r\n0123");
        stream.Send("456789GET /b HTTP/1.1\r\nHost: test\r\n\r\n");
        stream.CompleteInput();
        await connection;

        Assert.Equal(["Post /a 0123456789", "Get /b "], seen);
    }

    [Fact]
    public async Task BodyReads_UseLargeBuffers_WhileIdleReadsStaySmall()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new DuplexTestStream();

        var connection = ConnectionHarness.RunAsync(stream, static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            return ValueTask.CompletedTask;
        }, cts.Token);

        const int bodySize = 200 * 1024;
        stream.Send($"POST /up HTTP/1.1\r\nHost: test\r\nContent-Length: {bodySize}\r\n\r\n");
        for (int sent = 0; sent < bodySize; sent += 1000)
        {
            stream.Send(new byte[1000]);
        }

        await stream.WaitForWrittenAsync(w => w.Contains("\r\n\r\n", StringComparison.Ordinal), cts.Token);
        stream.CompleteInput();
        await connection;

        int[] sizes = stream.ReadBufferSizes;

        // An idle connection must not pin a large buffer while it waits for the next request...
        Assert.True(sizes[0] <= 8 * 1024, $"First read offered {sizes[0]} bytes.");
        // ...but once a body is pending, reads should be able to take much more per call.
        Assert.True(sizes.Max() >= 32 * 1024, $"Largest read buffer offered was {sizes.Max()} bytes.");
    }

    [Fact]
    public async Task KeepAlive_RequestsWithinReceiveTimeout_KeepConnectionOpen_ThenIdleCloses()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new DuplexTestStream();
        int calls = 0;

        var connection = ConnectionHarness.RunAsync(stream, (ctx, _) =>
        {
            calls++;
            ctx.Response.StatusCode = 200;
            return ValueTask.CompletedTask;
        }, cts.Token, o => o.ReceiveTimeout = TimeSpan.FromMilliseconds(1500));

        // Total elapsed time is well past the timeout; each gap is well inside it.
        for (int i = 1; i <= 4; i++)
        {
            stream.Send("GET / HTTP/1.1\r\nHost: test\r\n\r\n");
            int expected = i;
            await stream.WaitForWrittenAsync(_ => Volatile.Read(ref calls) == expected, cts.Token);
            Assert.False(connection.IsCompleted, $"Connection closed after request {i}.");
            await Task.Delay(500, cts.Token);
        }

        // No more input: the idle timeout closes the connection without CompleteInput().
        await connection.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task RejectedUpload_StillDeliversErrorResponse_WhenClientKeepsSending()
    {
        var options = new FlashHttpServerOptions { Address = System.Net.IPAddress.Loopback, Port = 0, MaxRequestBodySize = 1024, MetricsEnabled = false };
        using var server = new FlashHttpServer(options, new ServiceCollection().BuildServiceProvider());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token);
            var stream = tcp.GetStream();

            // The declared length is over the limit, so the server answers 413 after the head;
            // the client has already queued far more than the server will ever read.
            byte[] upload = new byte[512 * 1024];
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /up HTTP/1.1\r\nHost: test\r\nContent-Length: {upload.Length}\r\n\r\n"), cts.Token);
            await stream.WriteAsync(upload, cts.Token);

            var received = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                received.Write(buffer, 0, read);
            }

            string response = Encoding.ASCII.GetString(received.ToArray());
            Assert.StartsWith("HTTP/1.1 413 ", response);
            Assert.Contains("Connection: close\r\n", response);
        }

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Theory]
    [InlineData(4096, 1024 * 1024, 16384)]          // far from the end: grow geometrically
    [InlineData(16290, 1024 * 1024, 65160)]
    [InlineData(260640, 1024 * 1024, 1024 * 1024)]  // 4x would stop just short: go to the exact size
    [InlineData(200000, 1024 * 1024, 1024 * 1024)]
    [InlineData(4096, 5000, 5000)]
    public void BodyBuffer_Grows4x_ButFinishesInOneStepWhenTheEndIsNear(int filled, int contentLength, int expectedSize)
    {
        byte[] full = new byte[filled];
        full[0] = 1;
        full[^1] = 2;

        byte[] grown = FlashHttpConnection.GrowBody(full, filled, filled + 1, contentLength);

        // Never more than 8x what has actually been received, and no reallocation of
        // (almost) the whole body because the geometric sequence missed the declared length.
        Assert.Equal(expectedSize, grown.Length);
        Assert.Equal(1, grown[0]);
        Assert.Equal(2, grown[filled - 1]);
    }

    [Fact]
    public async Task DeclaredBody_WithNothingReceivedYet_DoesNotCommitALargeBuffer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new DuplexTestStream();

        var connection = ConnectionHarness.RunAsync(stream, static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            return ValueTask.CompletedTask;
        }, cts.Token);

        // A head that only declares a body: the buffer the server offers to the first body
        // read shows how much it allocated on the strength of the declaration alone.
        stream.Send("POST /up HTTP/1.1\r\nHost: test\r\nContent-Length: 65536\r\n\r\n");

        int[] sizes = [];
        while (sizes.Length < 3)
        {
            await Task.Delay(5, cts.Token);
            sizes = stream.ReadBufferSizes;
        }

        Assert.True(sizes[2] <= 4096, $"Committed {sizes[2]} bytes before any body byte arrived.");

        stream.CompleteInput();
        await connection;
    }

    [Fact]
    public async Task SlowHead_PastRequestHeadersTimeout_Gets408AndClose()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new DuplexTestStream();
        int calls = 0;

        var connection = ConnectionHarness.RunAsync(stream, (ctx, _) =>
        {
            calls++;
            ctx.Response.StatusCode = 200;
            return ValueTask.CompletedTask;
        }, cts.Token, o =>
        {
            o.RequestHeadersTimeout = TimeSpan.FromMilliseconds(200);
            o.ReceiveTimeout = TimeSpan.FromSeconds(8);
        });

        // Each read is well inside the idle timeout, but the head as a whole takes too long.
        stream.Send("GET / HTTP/1.1\r\n");
        await Task.Delay(500, cts.Token);
        stream.Send("Host: te");

        // The server closes on its own: no CompleteInput().
        await connection.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        Assert.StartsWith("HTTP/1.1 408 ", stream.WrittenText);
        Assert.Contains("Connection: close\r\n", stream.WrittenText);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task StalledBody_PastReceiveTimeout_ClosesWithoutCallingTheHandler()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new DuplexTestStream();
        int calls = 0;

        var connection = ConnectionHarness.RunAsync(stream, (ctx, _) =>
        {
            calls++;
            ctx.Response.StatusCode = 200;
            return ValueTask.CompletedTask;
        }, cts.Token, o => o.ReceiveTimeout = TimeSpan.FromMilliseconds(300));

        stream.Send("POST /up HTTP/1.1\r\nHost: test\r\nContent-Length: 10\r\n\r\n0123");

        await connection.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        Assert.Equal(0, calls);
        Assert.Empty(stream.Written);
    }

    [Fact]
    public async Task ClientClosingMidBody_EndsTheConnectionWithoutCallingTheHandler()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new DuplexTestStream();
        int calls = 0;

        var connection = ConnectionHarness.RunAsync(stream, (ctx, _) =>
        {
            calls++;
            ctx.Response.StatusCode = 200;
            return ValueTask.CompletedTask;
        }, cts.Token);

        stream.Send("POST /up HTTP/1.1\r\nHost: test\r\nContent-Length: 10\r\n\r\n");
        stream.Send("0123");
        stream.CompleteInput();

        await connection.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        Assert.Equal(0, calls);
        Assert.Empty(stream.Written);
    }

    [Fact]
    public async Task Response_StartsCleanForEveryRequestOnAConnection()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new DuplexTestStream();
        string? secondResponseState = null;

        var connection = ConnectionHarness.RunAsync(stream, (ctx, _) =>
        {
            if (ctx.Request.Path == "/first")
            {
                ctx.Response.StatusCode = 201;
                ctx.Response.ReasonPhrase = "Made";
                ctx.Response.Headers.Add(new HttpHeader("X-First", "1"));
                ctx.Response.Body = "first"u8.ToArray();
            }
            else
            {
                var r = ctx.Response;
                secondResponseState = $"{r.StatusCode}|{r.ReasonPhrase}|{r.Headers.Count}|{r.Body.Length}|{r.BodyStream is null}|{ctx.Request.Headers.Count}";
                r.StatusCode = 200;
            }
            return ValueTask.CompletedTask;
        }, cts.Token);

        stream.Send("GET /first HTTP/1.1\r\nHost: test\r\nX-A: 1\r\nX-B: 2\r\n\r\nGET /second HTTP/1.1\r\nHost: test\r\n\r\n");

        await stream.WaitForWrittenAsync(w => w.Contains("HTTP/1.1 200 OK", StringComparison.Ordinal), cts.Token);
        stream.CompleteInput();
        await connection;

        Assert.Equal("404||0|0|True|1", secondResponseState);
        Assert.Contains("HTTP/1.1 201 Made\r\n", stream.WrittenText);
        Assert.DoesNotContain("X-First", stream.WrittenText[stream.WrittenText.IndexOf("HTTP/1.1 200 OK", StringComparison.Ordinal)..]);
    }
}
