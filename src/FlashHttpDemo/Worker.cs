using FlashHttp.Server;
using System;
using System.Collections.Generic;
using System.Text;

namespace FlashHttpDemo;

public sealed class Worker(FlashHttpServerOptions options, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var server = new FlashHttpServerBuilder(options).WithLogger(logger).Build();

        server.WithGetHandler("/", (request, response) =>
        {
            response.StatusCode = 200;
            response.Headers.Add(new FlashHttp.Abstractions.HttpHeader("Content-Type", "text/plain; charset=utf-8"));
            response.Body = Encoding.UTF8.GetBytes("Hello, FlashHttp!");
        });

        await server.StartAsync(stoppingToken);
    }
}
