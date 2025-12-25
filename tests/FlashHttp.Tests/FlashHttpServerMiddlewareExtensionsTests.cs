using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FlashHttp.Tests;

public sealed class FlashHttpServerMiddlewareExtensionsTests
{
    private static FlashHttpServer CreateServer()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var options = new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0 };
        return new FlashHttpServer(options, sp);
    }

    [Fact]
    public void WithHandler_NullServer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FlashHttpServerMiddlewareExtensions.WithHandler(
            null!,
            HttpMethodsEnum.Get,
            "/",
            _ => { },
            static (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public void WithHandler_NullPath_Throws()
    {
        using var server = CreateServer();
        Assert.Throws<ArgumentNullException>(() => server.WithHandler(
            HttpMethodsEnum.Get,
            null!,
            _ => { },
            static (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public void WithHandler_NullConfigure_Throws()
    {
        using var server = CreateServer();
        Assert.Throws<ArgumentNullException>(() => server.WithHandler(
            HttpMethodsEnum.Get,
            "/",
            null!,
            static (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public void WithHandler_NullTerminal_Throws()
    {
        using var server = CreateServer();
        Assert.Throws<ArgumentNullException>(() => server.WithHandler(
            HttpMethodsEnum.Get,
            "/",
            _ => { },
            null!));
    }

    [Fact]
    public async Task SecureEndpoint_RegistersRoute_AndMiddlewarePipeline_Works()
    {
        using var server = CreateServer();
        server.SecureEndpoint();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var response = await client.GetAsync("/secure", cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("OK", body);

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }
}
