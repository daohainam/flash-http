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

public sealed class FlashHttpFileStreamTests : IDisposable
{
    private readonly string _testFilePath;
    private readonly string _testFileContent;

    public FlashHttpFileStreamTests()
    {
        // Create a temporary test file
        _testFilePath = Path.Combine(Path.GetTempPath(), $"flashhttp-test-{Guid.NewGuid()}.txt");
        _testFileContent = "This is a test file for streaming.\nIt contains multiple lines of text.\nLine 3\nLine 4\nEnd of file.";
        File.WriteAllText(_testFilePath, _testFileContent);
    }

    public void Dispose()
    {
        // Clean up test file
        if (File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
    }

    private static FlashHttpServer CreateServer(Func<IServiceCollection, IServiceProvider>? buildServices = null)
    {
        var services = new ServiceCollection();
        var sp = (buildServices ?? (sc => sc.BuildServiceProvider()))(services);
        var options = new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0 };
        return new FlashHttpServer(options, sp);
    }

    [Fact]
    public async Task Response_WithFileStream_ServesFileContent()
    {
        using var server = CreateServer();

        server.WithHandler(HttpMethodsEnum.Get, "/file", (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add(new HttpHeader("Content-Type", "text/plain"));
            ctx.Response.BodyStream = File.OpenRead(_testFilePath);
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var resp = await client.GetAsync("/file", cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(_testFileContent, body);
        Assert.Equal(_testFileContent.Length, resp.Content.Headers.ContentLength);
        Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Response_WithFileStream_MultipleRequests_WorksCorrectly()
    {
        using var server = CreateServer();

        server.WithHandler(HttpMethodsEnum.Get, "/file", (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add(new HttpHeader("Content-Type", "text/plain"));
            // Each request gets a new stream - important for pooling
            ctx.Response.BodyStream = File.OpenRead(_testFilePath);
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        
        // Make multiple requests to ensure stream disposal works correctly
        for (int i = 0; i < 3; i++)
        {
            var resp = await client.GetAsync("/file", cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(_testFileContent, body);
        }

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Response_WithBinaryFileStream_ServesBinaryContent()
    {
        var binaryFilePath = Path.Combine(Path.GetTempPath(), $"flashhttp-binary-{Guid.NewGuid()}.bin");
        try
        {
            // Create a binary file with specific byte pattern
            var binaryData = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                binaryData[i] = (byte)i;
            }
            File.WriteAllBytes(binaryFilePath, binaryData);

            using var server = CreateServer();

            server.WithHandler(HttpMethodsEnum.Get, "/binary", (ctx, _) =>
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.Headers.Add(new HttpHeader("Content-Type", "application/octet-stream"));
                ctx.Response.BodyStream = File.OpenRead(binaryFilePath);
                return ValueTask.CompletedTask;
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
            var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var resp = await client.GetAsync("/binary", cts.Token);
            var body = await resp.Content.ReadAsByteArrayAsync(cts.Token);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(binaryData, body);
            Assert.Equal(256, resp.Content.Headers.ContentLength);

            cts.Cancel();
            try { await startTask; } catch (OperationCanceledException) { }
        }
        finally
        {
            if (File.Exists(binaryFilePath))
            {
                File.Delete(binaryFilePath);
            }
        }
    }
}
