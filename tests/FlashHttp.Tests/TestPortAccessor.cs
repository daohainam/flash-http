using FlashHttp.Server;
using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FlashHttp.Tests;

internal static class TestPortAccessor
{
    public static async Task<int> WaitForBoundPortAsync(FlashHttpServer server, CancellationToken cancellationToken)
    {
        // Reflect the private `listener` field.
        var field = typeof(FlashHttpServer).GetField("listener", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested && sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (field!.GetValue(server) is TcpListener l && l.Server.LocalEndPoint is IPEndPoint ep)
            {
                return ep.Port;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException("Server did not bind to a port in time.");
    }
}