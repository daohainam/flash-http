using FlashHttp.Abstractions;
using FlashHttp.Server;
using System;

namespace FlashHttp.Server;

public static class FlashHttpServerMiddlewareExtensions
{
	public static FlashHttpServer WithHandler(
		this FlashHttpServer server,
		HttpMethodsEnum method,
		string path,
		Action<FlashPipelineBuilder> configurePipeline,
		HandlerSet.FlashRequestAsyncDelegate terminal)
	{
		ArgumentNullException.ThrowIfNull(server);
		ArgumentNullException.ThrowIfNull(path);
		ArgumentNullException.ThrowIfNull(configurePipeline);
		ArgumentNullException.ThrowIfNull(terminal);

		var pipeline = new FlashPipelineBuilder();
		configurePipeline(pipeline);

		return server.WithHandler(method, path, pipeline.Build(terminal));
	}

	public static FlashHttpServer SecureEndpoint(this FlashHttpServer server)
	{
		ArgumentNullException.ThrowIfNull(server);

		return server.WithHandler(
			HttpMethodsEnum.Get,
			"/secure",
			p =>
			{
				p.Use(async (ctx, next, ct) =>
				{
					// e.g. auth check
					// if unauthorized: set response and return
					await next(ctx, ct);
				});

				p.Use(async (ctx, next, ct) =>
				{
					// e.g. timing/logging
					await next(ctx, ct);
				});
			},
			async (ctx, ct) =>
			{
				ctx.Response.StatusCode = 200;
				ctx.Response.Body = "OK"u8.ToArray();
			});
	}
}