using FlashHttp.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
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
    private TcpListener? listener;

    private readonly FlashPipelineBuilder _globalPipeline = new();
    private FlashRequestAsyncDelegate? _app;

    public FlashHttpServer(FlashHttpServerOptions options, IServiceProvider serviceProvider, ILogger? logger = null)
    {
        _options = options;
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? NullLogger<FlashHttpServer>.Instance;

        var poolProvider = new DefaultObjectPoolProvider
        {
            MaximumRetained = _options.RequestPoolMaximumRetained
        };
        _requestPool = poolProvider.Create(new FlashHttpRequestPooledObjectPolicy());
        _responsePool = poolProvider.Create(new FlashHttpResponsePooledObjectPolicy());
        _contextPool = poolProvider.Create(new FlashHttpContextPooledObjectPolicy());
    }

    public FlashHttpServer(IServiceProvider serviceProvider, Action<FlashHttpServerOptions>? configureOptions = null, ILogger? logger = null)
    {
        _options = new FlashHttpServerOptions();
        configureOptions?.Invoke(_options);

        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? NullLogger<FlashHttpServer>.Instance;

        var poolProvider = new DefaultObjectPoolProvider
        {
            MaximumRetained = _options.RequestPoolMaximumRetained
        };
        _requestPool = poolProvider.Create(new FlashHttpRequestPooledObjectPolicy());
        _responsePool = poolProvider.Create(new FlashHttpResponsePooledObjectPolicy());
        _contextPool = poolProvider.Create(new FlashHttpContextPooledObjectPolicy());
    }

    public FlashHttpServer Use(FlashMiddleware middleware)
    {
        _globalPipeline.Use(middleware);
        return this;
    }

    public FlashHttpServer WithHandler(HttpMethodsEnum method, string path, FlashRequestAsyncDelegate handler)
    {
        switch (method)
        {
            case HttpMethodsEnum.Get:
                handlerSet.OnGetHandlers[path] = handler;
                break;
            case HttpMethodsEnum.Post:
                handlerSet.OnPostHandlers[path] = handler;
                break;
            case HttpMethodsEnum.Put:
                handlerSet.OnPutHandlers[path] = handler;
                break;
            case HttpMethodsEnum.Delete:
                handlerSet.OnDeleteHandlers[path] = handler;
                break;
            case HttpMethodsEnum.Head:
                handlerSet.OnHeadHandlers[path] = handler;
                break;
            case HttpMethodsEnum.Patch:
                handlerSet.OnPatchHandlers[path] = handler;
                break;
            case HttpMethodsEnum.Options:
                handlerSet.OnOptionsHandlers[path] = handler;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, null);
        }

        return this;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _app ??= _globalPipeline.Build(handlerSet.HandleAsync);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Starting listening on {address}:{port}", _options.Address, _options.Port);
        }

        listener = CreateListener(_options.Address, _options.Port);
        listener.Start(1024);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);

                _ = HandleNewClientConnectionAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Server socket stopped listening on {address}:{port}", _options.Address, _options.Port);
                }
                break;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError(ex, "Error accepting client socket on {address}:{port}", _options.Address, _options.Port);
                }
                break;
            }
        }

        listener.Stop();
    }

    private async Task HandleNewClientConnectionAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Accepted new client connection from {remoteEndPoint}", tcpClient.Client.RemoteEndPoint);

        if (_options.MetricsEnabled)
        {
            FlashHttpMetrics.ActiveConnections.Add(1);
        }

        Stream? stream = null;

        try
        {
            stream = tcpClient.GetStream();
            bool isHttps = false;
            if (_options.Certificate != null)
            {
                var sslStream = new SslStream(stream);

                SslServerAuthenticationOptions options = new()
                {
                    ApplicationProtocols =
                    [
                        SslApplicationProtocol.Http11
                    ],
                    ServerCertificate = _options.Certificate,
                    EnabledSslProtocols = SslProtocols.None,
                    ClientCertificateRequired = false,
                };

                await sslStream.AuthenticateAsServerAsync(options, cancellationToken);

                stream = sslStream;
                isHttps = true;
            }

            var app = _app ?? _globalPipeline.Build(handlerSet.HandleAsync);

            var connection = new FlashHttpConnection(
                tcpClient,
                stream,
                isHttps,
                app,
                _options.MetricsEnabled,
                _requestPool,
                _responsePool,
                _contextPool,
                _serviceProvider,
                _logger,
                _options.MaxHeaderCount,
                _options.MaxRequestBodySize);

            await connection.ProcessRequestsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (AuthenticationException ex)
        {
            _logger.LogError(ex, "Error accepting client");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accepting client");
        }
        finally
        {
            try { if (stream != null) await stream.DisposeAsync().ConfigureAwait(false); } catch { }
            try { tcpClient.Close(); } catch { }
            tcpClient.Dispose();

            if (_options.MetricsEnabled)
            {
                FlashHttpMetrics.ActiveConnections.Add(-1);
            }
        }
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

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        listener?.Stop();
        listener = null;
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
            listener?.Stop();
            listener = null;
        }
    }
}
