using BenchmarkDotNet.Attributes;
using FlashHttp.Abstractions;
using FlashHttp.Server;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BenchmarkSuite
{
    /// <summary>
    /// End-to-end round trips over a loopback socket against a real server instance with
    /// default options. Allocation figures include both the client and the server side.
    /// </summary>
    [MemoryDiagnoser]
    public class LoopbackBenchmarks
    {
        private const int PipelineDepth = 16;
        private const int LargeSize = 1024 * 1024;

        private FlashHttpServer _server = default!;
        private CancellationTokenSource _cts = default!;
        private Task _serverTask = default!;
        private Socket _client = default!;

        private byte[] _get = default!;
        private byte[] _pipelinedGet = default!;
        private byte[] _getBig = default!;
        private byte[] _getStream = default!;
        private byte[] _post = default!;

        private int _getResponseLength;
        private int _bigResponseLength;
        private int _streamResponseLength;
        private int _postResponseLength;

        private readonly byte[] _receiveBuffer = new byte[256 * 1024];

        [GlobalSetup]
        public void Setup()
        {
            int port = GetFreePort();
            byte[] hello = Encoding.UTF8.GetBytes("Hello, World!");
            byte[] big = new byte[LargeSize];
            Array.Fill(big, (byte)'x');

            _server = FlashHttpServerBuilder.CreateBuilder()
                .WithBindingAddress(IPAddress.Loopback)
                .WithPort(port)
                .Build();

            _server
                .WithHandler(HttpMethodsEnum.Get, "/", (ctx, ct) =>
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Headers.Add(new HttpHeader("Content-Type", "text/plain"));
                    ctx.Response.Body = hello;
                    return ValueTask.CompletedTask;
                })
                .WithHandler(HttpMethodsEnum.Get, "/big", (ctx, ct) =>
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Body = big;
                    return ValueTask.CompletedTask;
                })
                .WithHandler(HttpMethodsEnum.Get, "/stream", (ctx, ct) =>
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.BodyStream = new MemoryStream(big, writable: false);
                    return ValueTask.CompletedTask;
                })
                .WithHandler(HttpMethodsEnum.Post, "/upload", (ctx, ct) =>
                {
                    ctx.Response.StatusCode = ctx.Request.Body.Length == LargeSize ? 200 : 500;
                    ctx.Response.Body = hello;
                    return ValueTask.CompletedTask;
                });

            _cts = new CancellationTokenSource();
            _serverTask = _server.StartAsync(_cts.Token);

            _client = Connect(port);

            string browserHeaders = HttpParserBenchmarks.BrowserGetText[(HttpParserBenchmarks.BrowserGetText.IndexOf("\r\n", StringComparison.Ordinal) + 2)..];
            _get = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n" + browserHeaders);
            _getBig = Encoding.ASCII.GetBytes("GET /big HTTP/1.1\r\nHost: localhost\r\n\r\n");
            _getStream = Encoding.ASCII.GetBytes("GET /stream HTTP/1.1\r\nHost: localhost\r\n\r\n");

            _pipelinedGet = new byte[_get.Length * PipelineDepth];
            for (int i = 0; i < PipelineDepth; i++)
            {
                _get.CopyTo(_pipelinedGet, i * _get.Length);
            }

            byte[] postHead = Encoding.ASCII.GetBytes(
                $"POST /upload HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/octet-stream\r\nContent-Length: {LargeSize}\r\n\r\n");
            _post = [.. postHead, .. big];

            // Response sizes are constant (the Date header has a fixed width), so measure once.
            _getResponseLength = MeasureResponse(_get);
            _bigResponseLength = MeasureResponse(_getBig);
            _streamResponseLength = MeasureResponse(_getStream);
            _postResponseLength = MeasureResponse(_post);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _client.Dispose();
            _cts.Cancel();
            try { _serverTask.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            _server.Dispose();
        }

        [Benchmark(Baseline = true)]
        public void KeepAliveGet() => RoundTrip(_get, _getResponseLength);

        [Benchmark(OperationsPerInvoke = PipelineDepth)]
        public void PipelinedGet16() => RoundTrip(_pipelinedGet, _getResponseLength * PipelineDepth);

        [Benchmark]
        public void GetBody1MB() => RoundTrip(_getBig, _bigResponseLength);

        [Benchmark]
        public void GetStream1MB() => RoundTrip(_getStream, _streamResponseLength);

        [Benchmark]
        public void PostBody1MB() => RoundTrip(_post, _postResponseLength);

        private void RoundTrip(byte[] request, int responseLength)
        {
            Send(request);

            int remaining = responseLength;
            while (remaining > 0)
            {
                int read = _client.Receive(_receiveBuffer, 0, Math.Min(_receiveBuffer.Length, remaining), SocketFlags.None);
                if (read == 0)
                    throw new InvalidOperationException("Server closed the connection.");
                remaining -= read;
            }
        }

        private void Send(byte[] request)
        {
            int sent = 0;
            while (sent < request.Length)
            {
                sent += _client.Send(request, sent, request.Length - sent, SocketFlags.None);
            }
        }

        // Sends one request and reads its response by parsing Content-Length; returns the total size.
        private int MeasureResponse(byte[] request)
        {
            Send(request);

            var received = new MemoryStream();
            int headEnd = -1;
            int contentLength = 0;

            while (true)
            {
                int read = _client.Receive(_receiveBuffer);
                if (read == 0)
                    throw new InvalidOperationException("Server closed the connection.");
                received.Write(_receiveBuffer, 0, read);

                if (headEnd < 0)
                {
                    ReadOnlySpan<byte> data = received.GetBuffer().AsSpan(0, (int)received.Length);
                    int idx = data.IndexOf("\r\n\r\n"u8);
                    if (idx >= 0)
                    {
                        headEnd = idx + 4;
                        string head = Encoding.ASCII.GetString(data[..idx]);
                        if (!head.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
                            throw new InvalidOperationException("Unexpected response: " + head);

                        foreach (string line in head.Split("\r\n"))
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                contentLength = int.Parse(line.AsSpan("Content-Length:".Length).Trim());
                        }
                    }
                }

                if (headEnd >= 0 && received.Length >= headEnd + contentLength)
                    return headEnd + contentLength;
            }
        }

        private static Socket Connect(int port)
        {
            for (int attempt = 0; ; attempt++)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.Connect(IPAddress.Loopback, port);
                    return socket;
                }
                catch (SocketException) when (attempt < 100)
                {
                    socket.Dispose();
                    Thread.Sleep(20);
                }
            }
        }

        private static int GetFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
