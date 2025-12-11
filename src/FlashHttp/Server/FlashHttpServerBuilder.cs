using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace FlashHttp.Server;
public class FlashHttpServerBuilder
{
    private readonly FlashHttpServerOptions _options;
    private ILogger? _logger;

    public FlashHttpServerBuilder()
    {
        _options = new FlashHttpServerOptions();
    }
    public FlashHttpServerBuilder(FlashHttpServerOptions options)
    {
        _options = options;
    }
    public FlashHttpServerBuilder ConfigureOptions(Action<FlashHttpServerOptions> configureOptions)
    {
        configureOptions?.Invoke(_options);
        return this;
    }
    public FlashHttpServer Build()
    {
        return new FlashHttpServer(_options, _logger);
    }
    public FlashHttpServerBuilder WithPort(int port) 
    {
        _options.Port = port;
        return this;
    }
    public FlashHttpServerBuilder WithBindingAddress(IPAddress address)
    {
        _options.Address = address;
        return this;
    }
    public FlashHttpServerBuilder WithBindingAddress(string address)
    {
        _options.Address = IPAddress.Parse(address);
        return this;
    }
    public FlashHttpServerBuilder WithCertificate(X509Certificate2 certificate)
    {
        _options.Certificate = certificate;
        return this;
    }
    public FlashHttpServerBuilder WithCertificateFile(string certificatePath)
    {
        _options.Certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        return this;
    }
    public FlashHttpServerBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

}
