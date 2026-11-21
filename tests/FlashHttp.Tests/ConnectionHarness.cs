using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using System.Net;
using System.Text;
using System.Threading.Channels;

namespace FlashHttp.Tests;

/// <summary>
/// In-memory duplex stream for driving a <see cref="FlashHttpConnection"/> without a socket.
/// Every chunk passed to <see cref="Send(byte[])"/> is delivered by its own read, and every
/// write the connection makes is recorded, so tests can assert on read and write patterns.
/// </summary>
internal sealed class DuplexTestStream : Stream
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly MemoryStream _written = new();
    private readonly List<int> _writeSizes = [];
    private readonly List<int> _readBufferSizes = [];
    private readonly object _gate = new();
    private byte[]? _current;
    private int _offset;

    public void Send(byte[] data) => _incoming.Writer.TryWrite(data);

    public void Send(string latin1) => Send(Encoding.Latin1.GetBytes(latin1));

    /// <summary>Signals end of input: the next read returns 0, as a closed socket would.</summary>
    public void CompleteInput() => _incoming.Writer.TryComplete();

    public byte[] Written
    {
        get { lock (_gate) return _written.ToArray(); }
    }

    public string WrittenText => Encoding.Latin1.GetString(Written);

    /// <summary>Size of each write the connection made, in order.</summary>
    public int[] WriteSizes
    {
        get { lock (_gate) return [.. _writeSizes]; }
    }

    /// <summary>Size of the buffer the connection offered to each read, in order.</summary>
    public int[] ReadBufferSizes
    {
        get { lock (_gate) return [.. _readBufferSizes]; }
    }

    public async Task WaitForWrittenAsync(Func<string, bool> predicate, CancellationToken ct)
    {
        while (!predicate(WrittenText))
        {
            await Task.Delay(5, ct);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        lock (_gate) _readBufferSizes.Add(buffer.Length);

        if (_current is null || _offset == _current.Length)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
                return 0;

            _incoming.Reader.TryRead(out _current);
            _offset = 0;
        }

        int count = Math.Min(buffer.Length, _current!.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _written.Write(buffer.Span);
            _writeSizes.Add(buffer.Length);
        }
        return ValueTask.CompletedTask;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal static class ConnectionHarness
{
    /// <summary>
    /// Runs a connection over <paramref name="stream"/> until the input is completed or the
    /// connection closes itself. The returned task completes when the connection is done.
    /// </summary>
    public static Task RunAsync(
        DuplexTestStream stream,
        HandlerSet.FlashRequestAsyncDelegate app,
        CancellationToken ct,
        Action<FlashHttpServerOptions>? configure = null)
    {
        var options = new FlashHttpServerOptions { MetricsEnabled = false };
        configure?.Invoke(options);

        var services = new ServiceCollection().BuildServiceProvider();
        var pools = new DefaultObjectPoolProvider();

        var connection = new FlashHttpConnection(
            new IPEndPoint(IPAddress.Loopback, 50000),
            new IPEndPoint(IPAddress.Loopback, 8080),
            stream,
            isHttps: false,
            app,
            options,
            pools.Create(new FlashHttpRequestPooledObjectPolicy()),
            pools.Create(new FlashHttpResponsePooledObjectPolicy()),
            pools.Create(new FlashHttpContextPooledObjectPolicy(services.GetService<IServiceScopeFactory>())),
            services,
            NullLogger.Instance,
            CancellationToken.None);

        return Task.Run(() => connection.ProcessRequestsAsync(ct), ct);
    }
}
