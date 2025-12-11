using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace FlashHttp.Server;
public class FlashHttpServerBuilder
{
    private readonly FlashHttpServerOptions _options;
    public FlashHttpServerBuilder()
    {
        _options = new FlashHttpServerOptions();
    }
    public FlashHttpServerBuilder ConfigureOptions(Action<FlashHttpServerOptions> configureOptions)
    {
        configureOptions?.Invoke(_options);
        return this;
    }
    public FlashHttpServer Build()
    {
        return new FlashHttpServer(_options);
    }
    public FlashHttpServerBuilder WithPort(int port) 
    {
        _options.Port = port;
        return this;
    }
    public FlashHttpServerBuilder WithBindingAddress(IPAddress address)
    {
        _options.BindingAddress = address;
        return this;
    }
    public FlashHttpServerBuilder WithBindingAddress(string address)
    {
        _options.BindingAddress = IPAddress.Parse(address);
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

}
