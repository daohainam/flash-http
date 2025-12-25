using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FlashHttp.Tests;

public sealed class FlashPipelineBuilderTests
{
    private static FlashHandlerContext CreateContext()
    {
        var request = new FlashHttpRequest();
        request.Init(
            HttpMethodsEnum.Get,
            port: 80,
            path: "/",
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
    public void Use_NullMiddleware_Throws()
    {
        var b = new FlashPipelineBuilder();
        Assert.Throws<ArgumentNullException>(() => b.Use(null!));
    }

    [Fact]
    public void Build_NullTerminal_Throws()
    {
        var b = new FlashPipelineBuilder();
        Assert.Throws<ArgumentNullException>(() => b.Build(null!));
    }

    [Fact]
    public async Task Build_EmptyPipeline_CallsTerminal()
    {
        var b = new FlashPipelineBuilder();
        var ctx = CreateContext();

        bool called = false;
        var app = b.Build((context, _) =>
        {
            context.Response.StatusCode = 200;
            called = true;
            return ValueTask.CompletedTask;
        });

        await app(ctx, CancellationToken.None);

        Assert.True(called);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Build_Middleware_Order_IsOuterToInner()
    {
        var b = new FlashPipelineBuilder();
        var ctx = CreateContext();

        b.Use(static async (context, next, ct) =>
        {
            context.Response.Headers.Add(new HttpHeader("X-Order", "A-enter"));
            await next(context, ct);
            context.Response.Headers.Add(new HttpHeader("X-Order", "A-exit"));
        });

        b.Use(static async (context, next, ct) =>
        {
            context.Response.Headers.Add(new HttpHeader("X-Order", "B-enter"));
            await next(context, ct);
            context.Response.Headers.Add(new HttpHeader("X-Order", "B-exit"));
        });

        var app = b.Build(static (context, _) =>
        {
            context.Response.Headers.Add(new HttpHeader("X-Order", "terminal"));
            return ValueTask.CompletedTask;
        });

        await app(ctx, CancellationToken.None);

        Assert.Equal(
            [
                "A-enter",
                "B-enter",
                "terminal",
                "B-exit",
                "A-exit"
            ],
            ctx.Response.Headers.FindAll(h => h.Name == "X-Order").ConvertAll(h => h.Value));
    }

    [Fact]
    public async Task Build_Middleware_ShortCircuits_WhenNextNotCalled()
    {
        var b = new FlashPipelineBuilder();
        var ctx = CreateContext();

        bool terminalCalled = false;

        b.Use(static (context, _, _) =>
        {
            context.Response.StatusCode = 401;
            return ValueTask.CompletedTask;
        });

        var app = b.Build((_, _) =>
        {
            terminalCalled = true;
            return ValueTask.CompletedTask;
        });

        await app(ctx, CancellationToken.None);

        Assert.False(terminalCalled);
        Assert.Equal(401, ctx.Response.StatusCode);
    }
}
