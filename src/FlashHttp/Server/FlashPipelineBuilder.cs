using FlashHttp.Abstractions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FlashHttp.Server;

public sealed class FlashPipelineBuilder
{
	private readonly List<FlashMiddleware> _components = [];

	public FlashPipelineBuilder Use(FlashMiddleware middleware)
	{
		ArgumentNullException.ThrowIfNull(middleware);
		_components.Add(middleware);
		return this;
	}

	public HandlerSet.FlashRequestAsyncDelegate Build(HandlerSet.FlashRequestAsyncDelegate terminal)
	{
		ArgumentNullException.ThrowIfNull(terminal);

		FlashNext next = static (context, cancellationToken) => ValueTask.CompletedTask;

		// terminal adapter
		next = (context, cancellationToken) => terminal(context, cancellationToken);

		for (int i = _components.Count - 1; i >= 0; i--)
		{
			var component = _components[i];
			var capturedNext = next;

			next = (context, cancellationToken) => component(context, capturedNext, cancellationToken);
		}

		return (context, cancellationToken) => next(context, cancellationToken);
	}
}