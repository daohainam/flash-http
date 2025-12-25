using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FlashHttp.Server;
public class FlashHttpServerOptions
{
    public int Port { get; set; } = 80;
    public IPAddress Address { get; set; } = IPAddress.Any;
    public X509Certificate2? Certificate { get; set; }

    public int RequestPoolMaximumRetained { get; set; } = 1024;

    public bool MetricsEnabled { get; set; } = true;
}
