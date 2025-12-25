using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FlashHttp.Tests;

public sealed class FlashHttpServerTests
{
    private static FlashHttpServer CreateServer(Func<IServiceCollection, IServiceProvider>? buildServices = null)
    {
        var services = new ServiceCollection();
        var sp = (buildServices ?? (sc => sc.BuildServiceProvider()))(services);
        var options = new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0 };
        return new FlashHttpServer(options, sp);
    }

    [Theory]
    [InlineData(HttpMethodsEnum.Get)]
    [InlineData(HttpMethodsEnum.Post)]
    [InlineData(HttpMethodsEnum.Put)]
    [InlineData(HttpMethodsEnum.Delete)]
    [InlineData(HttpMethodsEnum.Head)]
    [InlineData(HttpMethodsEnum.Patch)]
    [InlineData(HttpMethodsEnum.Options)]
    public async Task WithHandler_AllMethods_Dispatches(HttpMethodsEnum method)
    {
        using var server = CreateServer();

        server.WithHandler(method, "/m", static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = "ok"u8.ToArray();
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        HttpResponseMessage resp = method switch
        {
            HttpMethodsEnum.Get => await client.GetAsync("/m", cts.Token),
            HttpMethodsEnum.Post => await client.PostAsync("/m", new StringContent(""), cts.Token),
            HttpMethodsEnum.Put => await client.PutAsync("/m", new StringContent(""), cts.Token),
            HttpMethodsEnum.Delete => await client.DeleteAsync("/m", cts.Token),
            HttpMethodsEnum.Head => await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/m"), cts.Token),
            HttpMethodsEnum.Patch => await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/m"), cts.Token),
            HttpMethodsEnum.Options => await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/m"), cts.Token),
            _ => throw new InvalidOperationException()
        };

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public void WithHandler_UnsupportedMethod_Throws()
    {
        using var server = CreateServer();
        Assert.Throws<ArgumentOutOfRangeException>(() => server.WithHandler((HttpMethodsEnum)999, "/", static (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public void Use_AllowsNullCheckViaThrowIfNull()
    {
        using var server = CreateServer();
        Assert.Throws<ArgumentNullException>(() => server.Use(null!));
    }

    [Fact]
    public async Task GlobalMiddleware_ShortCircuit_PreventsHandler()
    {
        using var server = CreateServer();

        bool handlerCalled = false;

        server
            .Use(static (ctx, _, _) =>
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Body = Encoding.UTF8.GetBytes("no");
                return ValueTask.CompletedTask;
            })
            .WithHandler(HttpMethodsEnum.Get, "/", (_, _) =>
            {
                handlerCalled = true;
                return ValueTask.CompletedTask;
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var resp = await client.GetAsync("/", cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("no", body);
        Assert.False(handlerCalled);

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Middleware_Exception_ClosesConnection_RequestFails()
    {
        using var server = CreateServer();

        server
            .Use(static (_, _, _) => throw new InvalidOperationException("boom"))
            .WithHandler(HttpMethodsEnum.Get, "/", static (ctx, _) =>
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.Body = "ok"u8.ToArray();
                return ValueTask.CompletedTask;
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/", cts.Token));

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }
}
