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

public sealed class HandlerSetTests
{
    private static FlashHandlerContext CreateContext(HttpMethodsEnum method, string path)
    {
        var request = new FlashHttpRequest();
        request.Init(
            method,
            port: 80,
            path: path,
            queryString: "",
            keepAliveRequested: true,
            contentLength: 0,
            contentType: "",
            isHttps: false,
            remoteAddress: IPAddress.Loopback,
            remotePort: 12345,
            httpVersion: HttpVersions.Http11,
            headers: [],
            body: []);

        return new FlashHandlerContext
        {
            Request = request,
            Response = new FlashHttpResponse(),
            Services = new ServiceCollection().BuildServiceProvider()
        };
    }

    [Fact]
    public void Register_NullPath_Throws()
    {
        var set = new HandlerSet();
        Assert.Throws<ArgumentNullException>(() => set.Register(HttpMethodsEnum.Get, null!, static (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public void Register_NullHandler_Throws()
    {
        var set = new HandlerSet();
        Assert.Throws<ArgumentNullException>(() => set.Register(HttpMethodsEnum.Get, "/", null!));
    }

    [Fact]
    public void Register_UnsupportedMethod_Throws()
    {
        var set = new HandlerSet();
        Assert.Throws<ArgumentOutOfRangeException>(() => set.Register((HttpMethodsEnum)999, "/", static (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task HandleAsync_NoHandler_SetsNotFound()
    {
        var set = new HandlerSet();
        var ctx = CreateContext(HttpMethodsEnum.Get, "/missing");

        await set.HandleAsync(ctx, CancellationToken.None);

        Assert.Equal(404, ctx.Response.StatusCode);
        Assert.Equal("Not Found", ctx.Response.ReasonPhrase);
        Assert.Equal("Not Found", Encoding.UTF8.GetString(ctx.Response.Body));
    }

    [Theory]
    [InlineData(HttpMethodsEnum.Get)]
    [InlineData(HttpMethodsEnum.Post)]
    [InlineData(HttpMethodsEnum.Put)]
    [InlineData(HttpMethodsEnum.Delete)]
    [InlineData(HttpMethodsEnum.Head)]
    [InlineData(HttpMethodsEnum.Patch)]
    [InlineData(HttpMethodsEnum.Options)]
    public async Task HandleAsync_RoutesByMethod_InvokesRegisteredHandler(HttpMethodsEnum method)
    {
        var set = new HandlerSet();
        var ctx = CreateContext(method, "/route");

        set.Register(method, "/route", static (context, _) =>
        {
            context.Response.StatusCode = 200;
            context.Response.ReasonPhrase = "OK";
            context.Response.Body = "hit"u8.ToArray();
            return ValueTask.CompletedTask;
        });

        await set.HandleAsync(ctx, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("OK", ctx.Response.ReasonPhrase);
        Assert.Equal("hit", Encoding.UTF8.GetString(ctx.Response.Body));
    }

    [Fact]
    public async Task HandleAsync_UnknownMethod_SetsNotFound()
    {
        var set = new HandlerSet();
        var ctx = CreateContext((HttpMethodsEnum)123, "/route");

        await set.HandleAsync(ctx, CancellationToken.None);

        Assert.Equal(404, ctx.Response.StatusCode);
    }
}
