using FlashHttp.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;

namespace FlashHttp.Server;

internal class FlashHttpContextPooledObjectPolicy(IServiceScopeFactory? scopeFactory = null) : IPooledObjectPolicy<FlashHandlerContext>
{
    public FlashHandlerContext Create()
        => scopeFactory is null
            ? new FlashHandlerContext()
            : new ScopedFlashHandlerContext(scopeFactory);

    public bool Return(FlashHandlerContext obj)
    {
        obj.Request = default!;
        obj.Response = default!;

        if (obj is ScopedFlashHandlerContext scoped)
        {
            scoped.ReleaseScope();
        }
        else
        {
            obj.Services = default!;
        }

        return true;
    }
}

/// <summary>
/// Context whose <see cref="Services"/> is a per-request DI scope created on first access,
/// so requests whose handlers never resolve a service do not pay for a scope.
/// </summary>
internal sealed class ScopedFlashHandlerContext(IServiceScopeFactory scopeFactory) : FlashHandlerContext
{
    private IServiceScope? _scope;
    private IServiceProvider? _services;

    public override IServiceProvider Services
    {
        get => _services ??= (_scope = scopeFactory.CreateScope()).ServiceProvider;
        set => _services = value;
    }

    /// <summary>Disposes the scope of the request that just finished, if one was created.</summary>
    public void ReleaseScope()
    {
        var scope = _scope;
        _scope = null;
        _services = null;
        scope?.Dispose();
    }
}
