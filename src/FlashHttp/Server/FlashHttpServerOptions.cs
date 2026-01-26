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

    /// <summary>
    /// Maximum number of headers allowed per request. Default is 100.
    /// Set to prevent DoS attacks via excessive headers.
    /// </summary>
    public int MaxHeaderCount { get; set; } = 100;

    /// <summary>
    /// Maximum request body size in bytes. Default is 10MB.
    /// Set to prevent DoS attacks via large Content-Length values.
    /// </summary>
    public long MaxRequestBodySize { get; set; } = 10 * 1024 * 1024; // 10 MB
}
