using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FlashHttp.Tests;

public sealed class TlsTests
{
    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());

        using var template = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));

        // Round-trip through PFX so the private key sticks on Windows (server-auth needs it).
        var pfx = template.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
    }

    private static bool AcceptAnyCert(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors) => true;

    [Fact]
    public async Task RealTlsHandshake_NegotiatesHttp11AndServesRequest()
    {
        using var cert = CreateSelfSignedCert();
        var options = new FlashHttpServerOptions
        {
            Address = IPAddress.Loopback,
            Port = 0,
            Certificate = cert,
            MetricsEnabled = false,
        };
        using var server = new FlashHttpServer(options, new ServiceCollection().BuildServiceProvider());

        server.WithHandler(HttpMethodsEnum.Get, "/", static (ctx, _) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Body = "secure"u8.ToArray();
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);

        using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, AcceptAnyCert);
        var clientOptions = new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ApplicationProtocols = new System.Collections.Generic.List<SslApplicationProtocol>
            {
                SslApplicationProtocol.Http11,
            },
        };
        await ssl.AuthenticateAsClientAsync(clientOptions, cts.Token);

        Assert.True(ssl.IsAuthenticated);
        Assert.True(ssl.IsEncrypted);
        Assert.Equal(SslApplicationProtocol.Http11, ssl.NegotiatedApplicationProtocol);

        var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await ssl.WriteAsync(request, cts.Token);
        await ssl.FlushAsync(cts.Token);

        // Drain the response.
        var sb = new StringBuilder();
        var buffer = new byte[1024];
        while (true)
        {
            int read = await ssl.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        var response = sb.ToString();

        Assert.Contains("HTTP/1.1 200", response);
        Assert.Contains("secure", response);

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
        await Task.Delay(200, CancellationToken.None);
    }

    [Fact]
    public async Task TlsHandshakeTimeout_ClosesIdleClient()
    {
        using var cert = CreateSelfSignedCert();
        var options = new FlashHttpServerOptions
        {
            Address = IPAddress.Loopback,
            Port = 0,
            Certificate = cert,
            TlsHandshakeTimeout = TimeSpan.FromMilliseconds(500),
            MetricsEnabled = false,
        };
        using var server = new FlashHttpServer(options, new ServiceCollection().BuildServiceProvider());
        server.WithHandler(HttpMethodsEnum.Get, "/", static (_, _) => ValueTask.CompletedTask);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var startTask = Task.Run(() => server.StartAsync(cts.Token), cts.Token);
        var port = await TestPortAccessor.WaitForBoundPortAsync(server, cts.Token);

        // Open a TCP connection but never send a ClientHello — the server should close us
        // out via the TlsHandshakeTimeout.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var stream = tcp.GetStream();

        var sw = Stopwatch.StartNew();
        var buf = new byte[16];
        int bytesRead = await stream.ReadAsync(buf, cts.Token);
        sw.Stop();

        // Server closes the socket once the handshake timer fires.
        Assert.Equal(0, bytesRead);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected close shortly after 500ms; took {sw.Elapsed.TotalMilliseconds}ms");

        cts.Cancel();
        try { await startTask; } catch (OperationCanceledException) { }
        await Task.Delay(200, CancellationToken.None);
    }
}
