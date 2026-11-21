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

		// Nothing to compose: hand back the terminal itself instead of wrapping it in adapters
		// that would add delegate hops to every request.
		if (_components.Count == 0)
		{
			return terminal;
		}

		// terminal adapter
		FlashNext next = (context, cancellationToken) => terminal(context, cancellationToken);

		for (int i = _components.Count - 1; i >= 0; i--)
		{
			var component = _components[i];
			var capturedNext = next;

			next = (context, cancellationToken) => component(context, capturedNext, cancellationToken);
		}

		return (context, cancellationToken) => next(context, cancellationToken);
	}
}