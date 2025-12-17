using FlashHttp.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using static FlashHttp.Server.HandlerSet;

namespace FlashHttp.Server;
public class FlashHttpServer: IDisposable
{
    private readonly FlashHttpServerOptions _options;
    private readonly ILogger _logger;

    private readonly ObjectPool<FlashHttpRequest> _requestPool;
    private readonly ObjectPool<FlashHttpResponse> _responsePool;

    private readonly HandlerSet handlerSet = new();
    private TcpListener? listener;

    public FlashHttpServer(FlashHttpServerOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<FlashHttpServer>.Instance;

        var poolProvider = new DefaultObjectPoolProvider
        {
            MaximumRetained = _options.RequestPoolMaximumRetained
        };
        _requestPool = poolProvider.Create(new FlashHttpRequestPooledObjectPolicy());
        _responsePool = poolProvider.Create(new FlashHttpResponsePooledObjectPolicy());
    }

    public FlashHttpServer(Action<FlashHttpServerOptions>? configureOptions = null, ILogger? logger = null)
    {
        _options = new FlashHttpServerOptions();
        configureOptions?.Invoke(_options);

        _logger = logger ?? NullLogger<FlashHttpServer>.Instance;

        var poolProvider = new DefaultObjectPoolProvider
        {
            MaximumRetained = _options.RequestPoolMaximumRetained
        };
        _requestPool = poolProvider.Create(new FlashHttpRequestPooledObjectPolicy());
        _responsePool = poolProvider.Create(new FlashHttpResponsePooledObjectPolicy());
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
                    EnabledSslProtocols = SslProtocols.None, // use the system default version
                    ClientCertificateRequired = false,
                };

                await sslStream.AuthenticateAsServerAsync(options, cancellationToken);

                stream = sslStream;
                isHttps = true;
            }

            var connection = new FlashHttpConnection(tcpClient, stream, isHttps, handlerSet, _requestPool, _responsePool, _logger);
            await connection.ProcessRequestsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
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
        }
    }
    private static TcpListener CreateListener(IPAddress address, int port)
    {
        // Default Address.Any is IPv4-only (0.0.0.0). Many clients (and tools like bombardier)
        // resolve localhost to IPv6 (::1) first, which would get "connection refused".
        // If user didn't specify an explicit address, listen on IPv6Any with DualMode,
        // so we accept both IPv4 and IPv6 connections.
        if (address.Equals(IPAddress.Any))
        {
            var l = new TcpListener(IPAddress.IPv6Any, port);
            try { l.Server.DualMode = true; } catch { /* ignore if not supported */ }
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
