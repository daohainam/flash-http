using FlashHttp.Abstractions;
using FlashHttp.Server;
using System.Globalization;
using System.Text;
using Xunit;

namespace FlashHttp.Tests;

/// <summary>
/// How responses reach the stream: as few writes as the framing allows, without holding a
/// finished response back behind work that is still running.
/// </summary>
public sealed class WritePathTests
{
    private const string Get = "GET / HTTP/1.1\r\nHost: test\r\n\r\n";

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer.AsSpan(offset));
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Runs one connection, sends <paramref name="input"/> in a single read and returns the stream once the connection is done.</summary>
    private static async Task<DuplexTestStream> ExchangeAsync(string input, HandlerSet.FlashRequestAsyncDelegate app)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new DuplexTestStream();
        var connection = ConnectionHarness.RunAsync(stream, app, cts.Token);

        stream.Send(input);
        stream.CompleteInput();
        await connection;

        return stream;
    }

    private static byte[] BodyAfterHead(byte[] response)
    {
        int headEnd = response.AsSpan().IndexOf("\r\n\r\n"u8);
        Assert.True(headEnd >= 0);
        return response[(headEnd + 4)..];
    }

    private static byte[] DecodeChunked(byte[] chunked)
    {
        var body = new MemoryStream();
        ReadOnlySpan<byte> rest = chunked;

        while (true)
        {
            int lineEnd = rest.IndexOf("\r\n"u8);
            int size = int.Parse(Encoding.ASCII.GetString(rest[..lineEnd]), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            rest = rest[(lineEnd + 2)..];

            if (size == 0)
            {
                Assert.True(rest.SequenceEqual("\r\n"u8));
                return body.ToArray();
            }

            body.Write(rest[..size]);
            Assert.True(rest.Slice(size, 2).SequenceEqual("\r\n"u8));
            rest = rest[(size + 2)..];
        }
    }

    [Fact]
    public async Task SingleSmallResponse_IsOneWrite()
    {
        var stream = await ExchangeAsync(Get, static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = "hello"u8.ToArray();
            return ValueTask.CompletedTask;
        });

        Assert.Single(stream.WriteSizes);
        Assert.EndsWith("\r\n\r\nhello", stream.WrittenText);
    }

    [Fact]
    public async Task PipelinedRequests_AreAnsweredWithOneWrite()
    {
        int calls = 0;

        var stream = await ExchangeAsync(string.Concat(Enumerable.Repeat(Get, 16)), (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = Encoding.ASCII.GetBytes($"r{++calls};");
            return ValueTask.CompletedTask;
        });

        Assert.Equal(16, calls);
        Assert.Single(stream.WriteSizes);

        // All sixteen responses, in order.
        string text = stream.WrittenText;
        int position = 0;
        for (int i = 1; i <= 16; i++)
        {
            position = text.IndexOf($"\r\n\r\nr{i};", position, StringComparison.Ordinal);
            Assert.True(position >= 0, $"Response {i} missing or out of order.");
        }
    }

    [Fact]
    public async Task PipelinedResponses_AreFlushedBeforeTheyPileUp()
    {
        byte[] body = new byte[4096];

        var stream = await ExchangeAsync(string.Concat(Enumerable.Repeat(Get, 200)), (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = body;
            return ValueTask.CompletedTask;
        });

        int[] writes = stream.WriteSizes;
        Assert.True(writes.Length > 1, "200 x 4 KB of responses went out as a single write.");
        Assert.True(writes.Max() <= 160 * 1024, $"Largest write was {writes.Max()} bytes.");
        Assert.True(stream.Written.Length > 200 * 4096);
    }

    [Fact]
    public async Task FinishedResponse_IsNotHeldBack_ByALaterSlowHandler()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new DuplexTestStream();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var connection = ConnectionHarness.RunAsync(stream, async (ctx, _) =>
        {
            if (ctx.Request.Path == "/slow")
            {
                await release.Task;
            }
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = Encoding.ASCII.GetBytes(ctx.Request.Path);
        }, cts.Token);

        stream.Send("GET /fast HTTP/1.1\r\nHost: test\r\n\r\nGET /slow HTTP/1.1\r\nHost: test\r\n\r\n");

        // The first response must reach the client while the second handler is still running.
        await stream.WaitForWrittenAsync(w => w.EndsWith("\r\n\r\n/fast", StringComparison.Ordinal), cts.Token);

        release.SetResult();
        await stream.WaitForWrittenAsync(w => w.EndsWith("\r\n\r\n/slow", StringComparison.Ordinal), cts.Token);

        stream.CompleteInput();
        await connection;
    }

    [Fact]
    public async Task MediumBody_SharesOneWriteWithItsHead()
    {
        byte[] body = RandomBytes(20 * 1024);

        var stream = await ExchangeAsync(Get, (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = body;
            return ValueTask.CompletedTask;
        });

        Assert.Single(stream.WriteSizes);
        Assert.Equal(body, BodyAfterHead(stream.Written));
    }

    [Fact]
    public async Task LargeBody_IsWrittenWithoutBeingCutIntoSmallBlocks()
    {
        byte[] body = RandomBytes(1024 * 1024);

        var stream = await ExchangeAsync(Get, (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = body;
            return ValueTask.CompletedTask;
        });

        // Head, then the body array itself in 64 KB blocks (not the 4 KB blocks of the pipe).
        int[] writes = stream.WriteSizes;
        Assert.True(writes.Length <= 20, $"1 MB body took {writes.Length} writes.");
        Assert.All(writes[1..^1], size => Assert.True(size >= 32 * 1024, $"Body block of {size} bytes."));
        Assert.Equal(body, BodyAfterHead(stream.Written));
    }

    [Fact]
    public async Task LargeBody_AfterPipelinedSmallResponse_KeepsOrder()
    {
        byte[] body = RandomBytes(300 * 1024);

        var stream = await ExchangeAsync("GET /small HTTP/1.1\r\nHost: test\r\n\r\nGET /large HTTP/1.1\r\nHost: test\r\n\r\n", (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = ctx.Request.Path == "/small" ? "small"u8.ToArray() : body;
            return ValueTask.CompletedTask;
        });

        byte[] written = stream.Written;
        int firstBodyEnd = written.AsSpan().IndexOf("\r\n\r\nsmall"u8) + "\r\n\r\nsmall".Length;
        Assert.True(firstBodyEnd > 9);
        Assert.Equal(body, BodyAfterHead(written[firstBodyEnd..]));
    }

    [Fact]
    public async Task SeekableStreamBody_IsWrittenInLargeBlocks()
    {
        byte[] body = RandomBytes(1024 * 1024);

        var stream = await ExchangeAsync(Get, (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.BodyStream = new MemoryStream(body, writable: false);
            return ValueTask.CompletedTask;
        });

        Assert.True(stream.WriteSizes.Length <= 20, $"1 MB stream took {stream.WriteSizes.Length} writes.");
        Assert.Contains($"Content-Length: {body.Length}\r\n", stream.WrittenText);
        Assert.Equal(body, BodyAfterHead(stream.Written));
    }

    [Fact]
    public async Task ChunkedStreamBody_WritesWholeChunksInLargeBlocks()
    {
        byte[] body = RandomBytes(300 * 1024);

        var stream = await ExchangeAsync(Get, (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.BodyStream = new ForwardOnlyStream(body);
            return ValueTask.CompletedTask;
        });

        Assert.True(stream.WriteSizes.Length <= 8, $"300 KB chunked stream took {stream.WriteSizes.Length} writes.");
        Assert.Contains("Transfer-Encoding: chunked\r\n", stream.WrittenText);
        Assert.Equal(body, DecodeChunked(BodyAfterHead(stream.Written)));
    }
}
