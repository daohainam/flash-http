using FlashHttp.Abstractions;
using FlashHttp.Server;
using System.Text;

namespace FlashHttpDemo;

public sealed class Worker(FlashHttpServerOptions options, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var server = new FlashHttpServer(options, null);
        server.WithHandler(HttpMethodsEnum.Get, "/", async (context, cancellationToken) =>
        {
            context.Response.StatusCode = 200;
            context.Response.Headers.Add(new HttpHeader("Content-Type", "text/plain; charset=utf-8"));
            context.Response.Body = Encoding.UTF8.GetBytes("Hello, FlashHttp!");
        });

        logger.LogInformation("Starting FlashHttp server...");

        await server.StartAsync(stoppingToken);
    }
}
