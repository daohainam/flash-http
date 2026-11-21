using FlashHttp.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using static FlashHttp.Server.HandlerSet;

namespace FlashHttp.Server;

public class FlashHttpServer : IDisposable
{
    private readonly FlashHttpServerOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;

    private readonly ObjectPool<FlashHttpRequest> _requestPool;
    private readonly ObjectPool<FlashHttpResponse> _responsePool;
    private readonly ObjectPool<FlashHandlerContext> _contextPool;

    private readonly HandlerSet handlerSet = new();
    private readonly PathLookup _pathLookup;
    private TcpListener? listener;
    private SemaphoreSlim? _connectionThrottle;
    private SslServerAuthenticationOptions? _sslOptions;

    private readonly FlashPipelineBuilder _globalPipeline = new();
    private FlashRequestAsyncDelegate? _app;
    private int _started; // 0 = not started, 1 = started; guards one-time pipeline build

    // Graceful shutdown: _stoppingCts stops the accept loop and lets idle connections exit;
    // _connectionsCts aborts whatever is still running once the drain period is over.
    private readonly CancellationTokenSource _stoppingCts = new();
    private readonly CancellationTokenSource _connectionsCts = new();
    private readonly TaskCompletionSource _acceptLoopFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _connectionsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeConnections;

    public FlashHttpServer(FlashHttpServerOptions options, IServiceProvider serviceProvider, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? NullLogger<FlashHttpServer>.Instance;

        // Lets the parser hand out the registered route string for a matching path.
        _pathLookup = handlerSet.FindRegisteredPath;

        var poolProvider = new DefaultObjectPoolProvider
        {
            MaximumRetained = _options.RequestPoolMaximumRetained
        };
        _requestPool = poolProvider.Create(new FlashHttpRequestPooledObjectPolicy());
        _responsePool = poolProvider.Create(new FlashHttpResponsePooledObjectPolicy());

        // Resolved once: with a scope factory, contexts create their DI scope lazily per request.
        _contextPool = poolProvider.Create(
            new FlashHttpContextPooledObjectPolicy(_serviceProvider.GetService<IServiceScopeFactory>()));
    }

    public FlashHttpServer(IServiceProvider serviceProvider, Action<FlashHttpServerOptions>? configureOptions = null, ILogger? logger = null)
        : this(CreateOptions(configureOptions), serviceProvider, logger)
    {
    }

    private static FlashHttpServerOptions CreateOptions(Action<FlashHttpServerOptions>? configureOptions)
    {
        var options = new FlashHttpServerOptions();
        configureOptions?.Invoke(options);
        return options;
    }

    public FlashHttpServer Use(FlashMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        // Adding middleware after StartAsync would silently fail to apply to the already-built
        // pipeline; fail loudly instead so users can fix their wiring.
        if (Volatile.Read(ref _started) != 0)
            throw new InvalidOperationException("Use(...) must be called before StartAsync.");

        _globalPipeline.Use(middleware);
        return this;
    }

    public FlashHttpServer WithHandler(HttpMethodsEnum method, string path, FlashRequestAsyncDelegate handler)
    {
        handlerSet.Register(method, path, handler);
        return this;
    }

    /// <summary>
    /// Binds the listener and runs the accept loop until <paramref name="cancellationToken"/>
    /// is cancelled (hard stop: in-flight connections are aborted) or <see cref="StopAsync"/>
    /// is called (graceful stop).
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // One-shot start: atomic CAS guarantees the pipeline is built exactly once
        // even if StartAsync is called concurrently.
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("StartAsync has already been called.");

        _app = _globalPipeline.Build(handlerSet.HandleAsync);

        if (_options.Certificate != null)
        {
            _sslOptions = CreateSslOptions(_options.Certificate);
        }

        if (_options.MaxConcurrentConnections > 0)
        {
            _connectionThrottle = new SemaphoreSlim(_options.MaxConcurrentConnections, _options.MaxConcurrentConnections);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Starting listening on {address}:{port}", _options.Address, _options.Port);
        }

        // A hard cancel from the caller aborts connections too; StopAsync only stops accepting.
        using var abortRegistration = cancellationToken.Register(static s => ((CancellationTokenSource)s!).Cancel(), _connectionsCts);
        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingCts.Token);
        var acceptToken = acceptCts.Token;

        var localListener = CreateListener(_options.Address, _options.Port);
        listener = localListener;

        try
        {
            localListener.Start(1024);

            while (!acceptToken.IsCancellationRequested)
            {
                bool throttleAcquired = false;
                try
                {
                    if (_connectionThrottle != null)
                    {
                        // Throttle before accept so pending clients wait in the kernel backlog
                        // instead of holding accepted sockets open with no timeout.
                        await _connectionThrottle.WaitAsync(acceptToken).ConfigureAwait(false);
                        throttleAcquired = true;
                    }

                    TcpClient client = await localListener.AcceptTcpClientAsync(acceptToken).ConfigureAwait(false);

                    throttleAcquired = false; // ownership moves to the connection task
                    _ = HandleNewClientConnectionAsync(client, _connectionsCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (throttleAcquired) _connectionThrottle!.Release();

                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Server socket stopped listening on {address}:{port}", _options.Address, _options.Port);
                    }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    if (throttleAcquired) _connectionThrottle!.Release();
                    // Listener was disposed via StopAsync/Dispose: exit cleanly.
                    break;
                }
                catch (Exception ex)
                {
                    if (throttleAcquired) _connectionThrottle!.Release();

                    // Transient errors (FD exhaustion, single-client socket faults) must not
                    // permanently stop the listener: log and back off briefly, then retry.
                    if (_logger.IsEnabled(LogLevel.Error))
                    {
                        _logger.LogError(ex, "Transient error accepting client socket on {address}:{port}; retrying", _options.Address, _options.Port);
                    }

                    try
                    {
                        await Task.Delay(100, acceptToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            try { localListener.Stop(); } catch { }
            _acceptLoopFinished.TrySetResult();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Preserve the historical contract: a hard cancel surfaces as OperationCanceledException.
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task HandleNewClientConnectionAsync(TcpClient tcpClient, CancellationToken abortToken)
    {
        Interlocked.Increment(ref _activeConnections);
        try
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Accepted new client connection from {remoteEndPoint}", tcpClient.Client.RemoteEndPoint);

            if (_options.MetricsEnabled)
            {
                FlashHttpMetrics.IncrementActiveConnections();
            }

            Stream? stream = null;

            try
            {
                try { tcpClient.NoDelay = true; } catch { /* best effort */ }

                stream = tcpClient.GetStream();
                bool isHttps = false;
                // Built once in StartAsync and shared by every handshake.
                var sslOptions = _sslOptions;
                if (sslOptions != null)
                {
                    var sslStream = new SslStream(stream);

                    var handshakeTimeout = _options.TlsHandshakeTimeout;
                    if (handshakeTimeout > TimeSpan.Zero && handshakeTimeout != Timeout.InfiniteTimeSpan)
                    {
                        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(abortToken);
                        handshakeCts.CancelAfter(handshakeTimeout);
                        try
                        {
                            await sslStream.AuthenticateAsServerAsync(sslOptions, handshakeCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!abortToken.IsCancellationRequested)
                        {
                            // Slow / abandoned handshake: normal attacker/scanner behaviour, not an error.
                            if (_logger.IsEnabled(LogLevel.Information))
                                _logger.LogInformation("TLS handshake timed out for {remoteEndPoint}", tcpClient.Client.RemoteEndPoint);
                            return;
                        }
                    }
                    else
                    {
                        await sslStream.AuthenticateAsServerAsync(sslOptions, abortToken).ConfigureAwait(false);
                    }

                    stream = sslStream;
                    isHttps = true;
                }

                // _app is non-null here: StartAsync sets it before accepting.
                var connection = new FlashHttpConnection(
                    tcpClient.Client.RemoteEndPoint as IPEndPoint,
                    tcpClient.Client.LocalEndPoint as IPEndPoint,
                    stream,
                    isHttps,
                    _app!,
                    _options,
                    _requestPool,
                    _responsePool,
                    _contextPool,
                    _serviceProvider,
                    _logger,
                    _stoppingCts.Token,
                    _pathLookup);

                await connection.ProcessRequestsAsync(abortToken).ConfigureAwait(false);

                if (connection.ClosedAfterError)
                {
                    await LingeringCloseAsync(tcpClient.Client, stream as SslStream, abortToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (AuthenticationException ex)
            {
                // Port scanners and plaintext probes against a TLS port end up here.
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(ex, "TLS handshake failed for {remoteEndPoint}", tcpClient.Client.RemoteEndPoint);
            }
            catch (Exception ex) when (IsConnectionReset(ex))
            {
                // The peer went away mid-connection. Routine under real traffic, not a server error.
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(ex, "Connection closed by peer {remoteEndPoint}", tcpClient.Client.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing client connection");
            }
            finally
            {
                try { if (stream != null) await stream.DisposeAsync().ConfigureAwait(false); } catch { }
                try { tcpClient.Close(); } catch { }
                tcpClient.Dispose();

                if (_options.MetricsEnabled)
                {
                    FlashHttpMetrics.DecrementActiveConnections();
                }
            }
        }
        finally
        {
            _connectionThrottle?.Release();

            if (Interlocked.Decrement(ref _activeConnections) == 0 && _stoppingCts.IsCancellationRequested)
            {
                _connectionsDrained.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Builds the TLS options shared by all connections. The certificate context (chain and
    /// credentials) is created once here; passing the bare certificate instead makes the
    /// runtime rebuild that context for every handshake.
    /// </summary>
    internal static SslServerAuthenticationOptions CreateSslOptions(X509Certificate2 certificate)
    {
        var options = new SslServerAuthenticationOptions
        {
            ApplicationProtocols =
            [
                SslApplicationProtocol.Http11
            ],
            EnabledSslProtocols = SslProtocols.None,
            ClientCertificateRequired = false,
        };

        try
        {
            options.ServerCertificateContext = SslStreamCertificateContext.Create(certificate, additionalCertificates: null);
        }
        catch (Exception)
        {
            // A certificate the context cannot be built from (for example one without a
            // private key) keeps the previous behaviour: each handshake reports the problem.
            options.ServerCertificate = certificate;
        }

        return options;
    }

    // Upper bound on how long a rejected connection is kept around to let the client read the
    // error response. Well-behaved clients close as soon as they see the server's FIN.
    private static readonly TimeSpan LingerTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Closes a connection the server gave up on while the client may still be sending: signal
    /// end of output first, then discard whatever keeps arriving until the client closes or the
    /// linger period ends. Without this, unread input turns the close into a TCP reset and the
    /// client can lose the error response it was just sent.
    /// </summary>
    private static async Task LingeringCloseAsync(Socket socket, SslStream? sslStream, CancellationToken abortToken)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var lingerCts = CancellationTokenSource.CreateLinkedTokenSource(abortToken);
            lingerCts.CancelAfter(LingerTimeout);

            if (sslStream is not null)
            {
                // close_notify, so the client does not see a truncated TLS stream.
                await sslStream.ShutdownAsync().WaitAsync(lingerCts.Token).ConfigureAwait(false);
            }

            socket.Shutdown(SocketShutdown.Send);

            // Raw socket on purpose: the content is discarded, so there is nothing to decrypt.
            while (await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, lingerCts.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Best effort: the connection is being closed either way.
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsConnectionReset(Exception ex)
    {
        var inner = ex is IOException io && io.InnerException is not null ? io.InnerException : ex;
        return inner is SocketException se
            && se.SocketErrorCode is SocketError.ConnectionReset
                                  or SocketError.ConnectionAborted
                                  or SocketError.Shutdown
                                  or SocketError.OperationAborted;
    }

    private static TcpListener CreateListener(IPAddress address, int port)
    {
        if (address.Equals(IPAddress.Any))
        {
            var l = new TcpListener(IPAddress.IPv6Any, port);
            try { l.Server.DualMode = true; } catch { }
            return l;
        }

        return new TcpListener(address, port);
    }

    /// <summary>
    /// Stops accepting new connections, lets idle keep-alive connections close and in-flight
    /// requests finish, then aborts whatever is still running when <paramref name="cancellationToken"/>
    /// fires. Returns once the accept loop has exited and connections are drained or aborted.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _stoppingCts.Cancel();

        try { listener?.Stop(); } catch { }

        if (Volatile.Read(ref _started) != 0)
        {
            await _acceptLoopFinished.Task.ConfigureAwait(false);
        }

        if (Volatile.Read(ref _activeConnections) == 0)
        {
            _connectionsDrained.TrySetResult();
        }

        if (!_connectionsDrained.Task.IsCompleted)
        {
            var drained = _connectionsDrained.Task;
            var timeout = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var finished = await Task.WhenAny(drained, timeout).ConfigureAwait(false);
            if (finished != drained)
            {
                _connectionsCts.Cancel();
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _stoppingCts.Cancel(); } catch { }
            try { _connectionsCts.Cancel(); } catch { }

            var l = listener;
            listener = null;
            try { l?.Stop(); } catch { }

            _connectionThrottle?.Dispose();
            _connectionThrottle = null;
        }
    }
}
