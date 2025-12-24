using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;

namespace FlashHttp.Tests;

public sealed class GlobalMiddlewareTests
{
    [Fact]
    public async Task GlobalMiddleware_AddsHeader_ToAllResponses()
    {
        var options = new FlashHttpServerOptions
        {
            Address = IPAddress.Loopback,
            Port = 0
        };

        using var server = new FlashHttpServer(options, new DefaultServiceProviderFactory().CreateServiceProvider(new ServiceCollection()));

        server
            .Use(async (ctx, next, ct) =>
            {
                await next(ctx, ct);
                ctx.Response.Headers.Add(new HttpHeader("X-Server", "FlashHttp"));
            })
            .WithHandler(HttpMethodsEnum.Get, "/", static (ctx, ct) =>
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.Headers.Add(new HttpHeader("Content-Type", "text/plain; charset=utf-8"));
                ctx.Response.Body = Encoding.UTF8.GetBytes("ok");
                return ValueTask.CompletedTask;
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start server on an ephemeral port.
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);

        // Wait until listener is created and we can read the assigned port.
        // (Requires a small enhancement to expose the bound port; see note below.)
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var response = await client.GetAsync("/", cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body);
        Assert.True(response.Headers.TryGetValues("X-Server", out var values));
        Assert.Contains("FlashHttp", values);

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task GlobalMiddleware_Order_IsOuterToInner()
    {
        var options = new FlashHttpServerOptions
        {
            Address = IPAddress.Loopback,
            Port = 0
        };

        using var server = new FlashHttpServer(options, new DefaultServiceProviderFactory().CreateServiceProvider(new ServiceCollection()));

        server
            .Use(async (ctx, next, ct) =>
            {
                ctx.Response.Headers.Add(new HttpHeader("X-Order", "A-enter"));
                await next(ctx, ct);
                ctx.Response.Headers.Add(new HttpHeader("X-Order", "A-exit"));
            })
            .Use(async (ctx, next, ct) =>
            {
                ctx.Response.Headers.Add(new HttpHeader("X-Order", "B-enter"));
                await next(ctx, ct);
                ctx.Response.Headers.Add(new HttpHeader("X-Order", "B-exit"));
            })
            .WithHandler(HttpMethodsEnum.Get, "/", static (ctx, ct) =>
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.Body = "ok"u8.ToArray();
                return ValueTask.CompletedTask;
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);

        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var response = await client.GetAsync("/", cts.Token);

        // This header is appended multiple times; HttpClient flattens them as multiple values.
        Assert.True(response.Headers.TryGetValues("X-Order", out var values));
        Assert.Equal(["A-enter", "B-enter", "B-exit", "A-exit"], values);

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }
}