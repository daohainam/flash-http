using FlashHttp.Abstractions;
using FlashHttp.Server;
using System.Text;

namespace FlashHttpDemo;

public sealed class Worker(FlashHttpServerOptions options, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var builder = FlashHttpServerBuilder.CreateBuilder().UseOptions(options);

        var server = builder.Build();
        server.WithHandler(HttpMethodsEnum.Get, "/", async (context, cancellationToken) =>
        {
            context.Response.StatusCode = 200;
            context.Response.Headers.Add(new HttpHeader("Content-Type", "text/plain; charset=utf-8"));
            context.Response.Body = Encoding.UTF8.GetBytes("Hello, FlashHttp!");
        })
        .WithHandler(HttpMethodsEnum.Get, "/file", async (context, cancellationToken) =>
        {
            // Example: Serve a file using BodyStream
            // This demonstrates the new file streaming capability
            // Find README.md in the repository root (relative to project directory)
            var currentDir = Directory.GetCurrentDirectory();
            var repoRoot = Path.GetFullPath(Path.Combine(currentDir, "..", ".."));
            var filePath = Path.Combine(repoRoot, "README.md");
            
            if (File.Exists(filePath))
            {
                context.Response.StatusCode = 200;
                context.Response.Headers.Add(new HttpHeader("Content-Type", "text/markdown; charset=utf-8"));
                context.Response.BodyStream = File.OpenRead(filePath);
            }
            else
            {
                context.Response.StatusCode = 404;
                context.Response.Body = Encoding.UTF8.GetBytes($"File not found at: {filePath}");
            }
        })
        .Use(async (ctx, next, ct) =>
        {
            await next(ctx, ct);
            ctx.Response.Headers.Add(new HttpHeader("X-Server", "FlashHttp"));
        });

        logger.LogInformation("Starting FlashHttp server...");
        logger.LogInformation("Try: http://localhost:8080/ for basic response");
        logger.LogInformation("Try: http://localhost:8080/file for file streaming example");

        await server.StartAsync(stoppingToken);
    }
}
