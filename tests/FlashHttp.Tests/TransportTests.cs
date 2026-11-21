using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using Xunit;

namespace FlashHttp.Tests;

public sealed class TransportTests
{
    /// <summary>Counts work posted to it and runs that work on the thread pool.</summary>
    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        public int Posts;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetSynchronizationContext(this);
                d(state);
            });
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref Posts);
            d(state);
        }
    }

    [Fact]
    public async Task Server_StartedUnderASynchronizationContext_NeverPostsBackToIt()
    {
        // An embedding host (UI app, legacy ASP.NET, custom scheduler) may call StartAsync with
        // a SynchronizationContext installed. Request processing must not be marshalled onto it.
        var context = new CountingSynchronizationContext();
        var options = new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0, MetricsEnabled = false };
        using var server = new FlashHttpServer(options, new ServiceCollection().BuildServiceProvider());

        server.WithHandler(HttpMethodsEnum.Post, "/echo", static async (ctx, ct) =>
        {
            await Task.Yield();
            ctx.Response.StatusCode = 200;
            ctx.Response.BodyStream = new MemoryStream(ctx.Request.Body, writable: false);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task? startTask = null;

        // Start the server on a dedicated thread that has the context installed, the way a
        // UI thread would.
        var starter = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            startTask = server.StartAsync(cts.Token);
        });
        starter.Start();
        starter.Join();

        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using (var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") })
        {
            for (int i = 0; i < 3; i++)
            {
                var payload = new byte[200 * 1024];
                var response = await client.PostAsync("/echo", new ByteArrayContent(payload), cts.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(payload.Length, (await response.Content.ReadAsByteArrayAsync(cts.Token)).Length);
            }
        }

        cts.Cancel();
        try { await startTask!; } catch (OperationCanceledException) { }

        Assert.Equal(0, Volatile.Read(ref context.Posts));
    }
}
