using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace FlashHttp.Tests;

/// <summary>
/// Guards for per-request overheads on the request path: DI scopes, logging and metrics.
/// </summary>
public sealed class HotPathTests
{
    private sealed class CountingScopeProvider(IServiceProvider inner) : IServiceProvider, IServiceScopeFactory
    {
        public int ScopesCreated;
        public int ScopesDisposed;

        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceScopeFactory) ? this : inner.GetService(serviceType);

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref ScopesCreated);
            return new CountingScope(this, inner.CreateScope());
        }

        private sealed class CountingScope(CountingScopeProvider owner, IServiceScope scope) : IServiceScope
        {
            public IServiceProvider ServiceProvider => scope.ServiceProvider;

            public void Dispose()
            {
                Interlocked.Increment(ref owner.ScopesDisposed);
                scope.Dispose();
            }
        }
    }

    private sealed class CapturingLogger(LogLevel minimumLevel) : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Entries.Enqueue((logLevel, formatter(state, exception)));
            }
        }
    }

    private static async Task RunServerAsync(FlashHttpServer server, Func<int, CancellationToken, Task> body)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        try
        {
            await body(port, cts.Token);
        }
        finally
        {
            cts.Cancel();
            try { await startTask; } catch (OperationCanceledException) { }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            await Task.Delay(10, ct);
        }
    }

    [Fact]
    public async Task Scope_IsNotCreated_WhenHandlerNeverTouchesServices()
    {
        var provider = new CountingScopeProvider(new ServiceCollection().BuildServiceProvider());
        using var server = new FlashHttpServer(new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0, MetricsEnabled = false }, provider);

        server.WithHandler(HttpMethodsEnum.Get, "/", static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            return ValueTask.CompletedTask;
        });

        await RunServerAsync(server, async (port, ct) =>
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/", ct)).StatusCode);
        });

        Assert.Equal(0, provider.ScopesCreated);
    }

    [Fact]
    public async Task Scope_IsCreatedOncePerRequest_AndDisposed_WhenHandlerUsesServices()
    {
        var provider = new CountingScopeProvider(new ServiceCollection().BuildServiceProvider());
        using var server = new FlashHttpServer(new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0, MetricsEnabled = false }, provider);

        bool sameProviderWithinRequest = false;
        IServiceProvider? previousRequestProvider = null;
        bool freshProviderPerRequest = true;

        server.WithHandler(HttpMethodsEnum.Get, "/", (ctx, _) =>
        {
            var first = ctx.Services;
            var second = ctx.Services;
            sameProviderWithinRequest = ReferenceEquals(first, second);
            freshProviderPerRequest &= !ReferenceEquals(first, previousRequestProvider);
            previousRequestProvider = first;

            ctx.Response.StatusCode = 200;
            return ValueTask.CompletedTask;
        });

        await RunServerAsync(server, async (port, ct) =>
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/", ct)).StatusCode);

            // The scope is disposed after the response is written, so the client can get ahead of it.
            await WaitUntilAsync(() => Volatile.Read(ref provider.ScopesDisposed) == 2, ct);
        });

        Assert.True(sameProviderWithinRequest);
        Assert.True(freshProviderPerRequest);
        Assert.Equal(2, provider.ScopesCreated);
        Assert.Equal(2, provider.ScopesDisposed);
    }

    [Theory]
    [InlineData(LogLevel.Information, false)]
    [InlineData(LogLevel.Debug, true)]
    public async Task ReceivedRequest_IsLoggedAtDebugOnly(LogLevel minimumLevel, bool expectLogged)
    {
        var logger = new CapturingLogger(minimumLevel);
        using var server = new FlashHttpServer(
            new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0, MetricsEnabled = false },
            new ServiceCollection().BuildServiceProvider(),
            logger);

        server.WithHandler(HttpMethodsEnum.Get, "/logged", static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            return ValueTask.CompletedTask;
        });

        await RunServerAsync(server, async (port, ct) =>
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/logged", ct)).StatusCode);
        });

        bool logged = logger.Entries.Any(e => e.Message.Contains("Received HTTP request: Get /logged", StringComparison.Ordinal));
        Assert.Equal(expectLogged, logged);
    }

    [Fact]
    public async Task RejectedRequest_LogsWarning_WithResultAndStatus()
    {
        var logger = new CapturingLogger(LogLevel.Warning);
        using var server = new FlashHttpServer(
            new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0, MetricsEnabled = false },
            new ServiceCollection().BuildServiceProvider(),
            logger);

        await RunServerAsync(server, async (port, ct) =>
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nBadHeaderNoColon\r\n\r\n"), ct);

            var buffer = new byte[1024];
            _ = await stream.ReadAsync(buffer, ct);
        });

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("InvalidRequest", StringComparison.Ordinal)
            && e.Message.Contains("400", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordRequest_DoesNotAllocate_WhenNoListenerIsAttached()
    {
        Assert.Equal(0, MeasureRecordRequestAllocations());
    }

    [Fact]
    public void RecordRequest_DoesNotAllocate_WhenListenerIsAttached()
    {
        long measurements = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "FlashHttp.Server")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => measurements += measurement);
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => measurements++);
        listener.Start();

        Assert.Equal(0, MeasureRecordRequestAllocations());
        Assert.True(measurements > 0);
    }

    private static long MeasureRecordRequestAllocations()
    {
        const int iterations = 1000;

        // Warm up so JIT and lazy static initialisation do not count.
        for (int i = 0; i < 10; i++)
        {
            FlashHttpMetrics.RecordRequest(HttpMethodsEnum.Get, 200, isHttps: false, keepAlive: true, durationMs: 1.5, requestBodyBytes: 0, responseBodyBytes: 13);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            FlashHttpMetrics.RecordRequest(HttpMethodsEnum.Get, 200, isHttps: false, keepAlive: true, durationMs: 1.5, requestBodyBytes: 0, responseBodyBytes: 13);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
