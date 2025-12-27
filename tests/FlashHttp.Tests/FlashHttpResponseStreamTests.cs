using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FlashHttp.Tests;

public sealed class FlashHttpResponseStreamTests
{
    private static FlashHttpServer CreateServer(Func<IServiceCollection, IServiceProvider>? buildServices = null)
    {
        var services = new ServiceCollection();
        var sp = (buildServices ?? (sc => sc.BuildServiceProvider()))(services);
        var options = new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0 };
        return new FlashHttpServer(options, sp);
    }

    [Fact]
    public async Task Response_WithBodyStream_SendsStreamContent()
    {
        using var server = CreateServer();

        var testContent = "Hello from stream!";
        var contentBytes = Encoding.UTF8.GetBytes(testContent);

        server.WithHandler(HttpMethodsEnum.Get, "/stream", (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add(new HttpHeader("Content-Type", "text/plain"));
            ctx.Response.BodyStream = new MemoryStream(contentBytes);
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var resp = await client.GetAsync("/stream", cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(testContent, body);
        Assert.Equal(contentBytes.Length.ToString(), resp.Content.Headers.ContentLength?.ToString());

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Response_WithLargeBodyStream_SendsAllContent()
    {
        using var server = CreateServer();

        // Create a larger stream (100KB) to test chunked streaming
        var largeContent = new byte[100 * 1024];
        for (int i = 0; i < largeContent.Length; i++)
        {
            largeContent[i] = (byte)(i % 256);
        }

        server.WithHandler(HttpMethodsEnum.Get, "/large", (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.BodyStream = new MemoryStream(largeContent);
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var resp = await client.GetAsync("/large", cts.Token);
        var body = await resp.Content.ReadAsByteArrayAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(largeContent.Length, body.Length);
        Assert.Equal(largeContent, body);

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Response_WithBodyStreamAtPosition_SendsRemainingContent()
    {
        using var server = CreateServer();

        var testContent = "0123456789";
        var contentBytes = Encoding.UTF8.GetBytes(testContent);
        var expectedContent = "56789"; // Starting from position 5

        server.WithHandler(HttpMethodsEnum.Get, "/positioned", (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            var stream = new MemoryStream(contentBytes);
            stream.Position = 5; // Skip first 5 bytes
            ctx.Response.BodyStream = stream;
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var resp = await client.GetAsync("/positioned", cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(expectedContent, body);

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Response_PreferBodyStreamOverBody_WhenBothPresent()
    {
        using var server = CreateServer();

        var streamContent = "From stream";
        var bodyContent = "From body array";

        server.WithHandler(HttpMethodsEnum.Get, "/both", (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = Encoding.UTF8.GetBytes(bodyContent);
            ctx.Response.BodyStream = new MemoryStream(Encoding.UTF8.GetBytes(streamContent));
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var resp = await client.GetAsync("/both", cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(streamContent, body); // Should use stream, not body array

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Response_WithEmptyBodyStream_SendsNoContent()
    {
        using var server = CreateServer();

        server.WithHandler(HttpMethodsEnum.Get, "/empty", (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.BodyStream = new MemoryStream();
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var resp = await client.GetAsync("/empty", cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("", body);
        Assert.Equal(0, resp.Content.Headers.ContentLength);

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Response_WithNonSeekableStream_SendsContent()
    {
        using var server = CreateServer();

        var testContent = "Non-seekable stream content";
        var contentBytes = Encoding.UTF8.GetBytes(testContent);

        server.WithHandler(HttpMethodsEnum.Get, "/nonseekable", (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.BodyStream = new NonSeekableStream(contentBytes);
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var resp = await client.GetAsync("/nonseekable", cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(testContent, body);
        // For non-seekable streams, connection should be closed after response
        Assert.Equal("close", resp.Headers.Connection.FirstOrDefault());

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// A simple non-seekable stream wrapper for testing purposes
    /// </summary>
    private class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] data)
        {
            _inner = new MemoryStream(data);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position 
        { 
            get => throw new NotSupportedException(); 
            set => throw new NotSupportedException(); 
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) 
            => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) 
            => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
