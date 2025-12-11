using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace FlashHttp.Server;
public class FlashHttpServer: IDisposable
{
    private readonly FlashHttpServerOptions _options;
    private readonly ILogger _logger;

    private readonly HandlerSet handlerSet = new();
    private TcpListener? listener;

    public FlashHttpServer(FlashHttpServerOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<FlashHttpServer>.Instance;
    }

    public FlashHttpServer(Action<FlashHttpServerOptions>? configureOptions = null, ILogger? logger = null)
    {
        _options = new FlashHttpServerOptions();
        configureOptions?.Invoke(_options);

        _logger = logger ?? NullLogger<FlashHttpServer>.Instance;
    }

    public FlashHttpServer WithHandler(HttpMethodsEnum method, string path, Action<FlashHttpRequest, FlashHttpResponse> handler)
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

        listener = new TcpListener(_options.Address, _options.Port);
        listener.Start();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);

                Task t = HandleNewClientConnectionAsync(client, cancellationToken);
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

        try
        {
            Stream stream = tcpClient.GetStream();
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

            var connection = new FlashHttpConnection(stream, isHttps, handlerSet, _logger);
            await connection.ProcessRequestsAsync(cancellationToken);
            await connection.CloseAsync(cancellationToken);
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
            tcpClient.Close();
            tcpClient.Dispose();
        }
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
