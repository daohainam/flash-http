using BenchmarkDotNet.Attributes;
using FlashHttp.Server;
using Microsoft.VSDiagnostics;
using System.Buffers;
using System.Net;
using System.Text;

namespace BenchmarkSuite
{
    // For more information on the VS BenchmarkDotNet Diagnosers see https://learn.microsoft.com/visualstudio/profiling/profiling-with-benchmark-dotnet
    [CPUUsageDiagnoser]
    [MemoryDiagnoser]
    public class HttpParserBenchmarks
    {
        private ReadOnlySequence<byte> _buffer;
        private IPEndPoint _remote;
        private IPEndPoint _local;

        [GlobalSetup]
        public void Setup()
        {
            var requestText =
                "GET /hello/world?x=1&y=2 HTTP/1.1\r\n" +
                "Host: localhost:8080\r\n" +
                "User-Agent: Unknown\r\n" +
                "Accept: */*\r\n" +
                "Connection: keep-alive\r\n" +
                "\r\n";

            var bytes = Encoding.ASCII.GetBytes(requestText);
            _buffer = new ReadOnlySequence<byte>(bytes);

            _remote = new IPEndPoint(IPAddress.Loopback, 50000);
            _local = new IPEndPoint(IPAddress.Loopback, 8080);
        }

        [Benchmark]
        public void ParseSimpleRequest()
        {
            var buffer = _buffer; // IMPORTANT: copy, vì TryReadHttpRequest sẽ Slice()
            FlashHttpParser.TryReadHttpRequest(
                ref buffer,
                false,
                _remote,
                _local, 
                out var req,
                out var keepAlive);
        }
    }
}
