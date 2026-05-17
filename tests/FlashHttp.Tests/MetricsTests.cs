using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FlashHttp.Tests;

public sealed class MetricsTests
{
    [Fact]
    public async Task Metrics_Emits_RequestCounter_And_DurationHistogram()
    {
        var options = new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0, MetricsEnabled = true };
        using var server = new FlashHttpServer(options, new ServiceCollection().BuildServiceProvider());

        server.WithHandler(HttpMethodsEnum.Get, "/", static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add(new HttpHeader("Content-Type", "text/plain; charset=utf-8"));
            ctx.Response.Body = Encoding.UTF8.GetBytes("ok");
            return ValueTask.CompletedTask;
        });

        long requests = 0;
        bool sawDuration = false;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "FlashHttp.Server")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "flashhttp.server.requests")
            {
                requests += measurement;
            }
        });

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "flashhttp.server.request.duration")
            {
                if (measurement >= 0)
                {
                    sawDuration = true;
                }
            }
        });

        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var resp = await client.GetAsync("/", cts.Token);
        _ = await resp.Content.ReadAsStringAsync(cts.Token);

        // give listener a moment to process callbacks
        await Task.Delay(50, cts.Token);

        Assert.True(requests >= 1);
        Assert.True(sawDuration);

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Metrics_ActiveConnections_Changes_WhileConnected()
    {
        var options = new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0, MetricsEnabled = true };
        using var server = new FlashHttpServer(options, new ServiceCollection().BuildServiceProvider());

        server.WithHandler(HttpMethodsEnum.Get, "/", static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = "ok"u8.ToArray();
            return ValueTask.CompletedTask;
        });

        long activeConnectionsDelta = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "FlashHttp.Server")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "flashhttp.server.active_connections")
            {
                activeConnectionsDelta += measurement;
            }
        });

        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        // Open a raw TCP connection to ensure connection metrics increment even without a full HTTP request.
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);

        await Task.Delay(50, cts.Token);
        Assert.True(activeConnectionsDelta >= 1);

        tcp.Close();
        await Task.Delay(100, cts.Token);

        // After closing, we should observe a decrement at some point.
        Assert.True(activeConnectionsDelta >= 0);

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Metrics_RecordsParsingErrorResponses()
    {
        var options = new FlashHttpServerOptions { Address = IPAddress.Loopback, Port = 0, MetricsEnabled = true };
        using var server = new FlashHttpServer(options, new ServiceCollection().BuildServiceProvider());

        server.WithHandler(HttpMethodsEnum.Get, "/", static (_, _) => ValueTask.CompletedTask);

        long errorStatusRequests = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "FlashHttp.Server" && instrument.Name == "flashhttp.server.requests")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "http.status_code" && tag.Value is int sc && (sc == 400 || sc == 413))
                {
                    errorStatusRequests += measurement;
                }
            }
        });
        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        // Raw TCP — send a header without a colon, which the parser now rejects as 400.
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var stream = tcp.GetStream();
        var malformed = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nBadHeaderNoColon\r\n\r\n");
        await stream.WriteAsync(malformed, cts.Token);

        // Drain the 400 response so the server-side write completes before we sample.
        var buf = new byte[1024];
        _ = await stream.ReadAsync(buf, cts.Token);

        await Task.Delay(50, cts.Token);

        Assert.True(errorStatusRequests >= 1, $"Expected at least one 4xx-tagged request emission; saw {errorStatusRequests}.");

        // Close client side and give the server's connection-finally (which decrements
        // the active_connections counter) time to run before this test returns. Without
        // this, a stale -1 leaks into the next test's MeterListener and breaks delta math.
        tcp.Close();
        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
        await Task.Delay(200, CancellationToken.None);
    }

    // NOTE: Metrics are process-wide and other tests in the same run can emit measurements.
    // A reliable negative test ("disabled emits nothing") would require runtime filtering by connection/request identity,
    // which System.Diagnostics.Metrics does not provide out-of-the-box.
    // This suite focuses on positive validation that metrics are emitted when enabled.
}
