using BenchmarkDotNet.Attributes;
using FlashHttp.Abstractions;
using FlashHttp.Server;
using Microsoft.Extensions.ObjectPool;
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
        private ReadOnlySequence<byte> _simpleGet;
        private ReadOnlySequence<byte> _browserGet;
        private ReadOnlySequence<byte> _browserGetSegmented;
        private ReadOnlySequence<byte> _postWithBody;
        private IPEndPoint _remote = default!;
        private IPEndPoint _local = default!;
        private ObjectPool<FlashHttpRequest> _pool = default!;

        internal const string BrowserGetText =
            "GET /api/products/search?q=wireless+headphones&page=2&sort=price_asc HTTP/1.1\r\n" +
            "Host: shop.example.com\r\n" +
            "Connection: keep-alive\r\n" +
            "sec-ch-ua: \"Chromium\";v=\"130\", \"Google Chrome\";v=\"130\", \"Not?A_Brand\";v=\"99\"\r\n" +
            "sec-ch-ua-mobile: ?0\r\n" +
            "sec-ch-ua-platform: \"Windows\"\r\n" +
            "Upgrade-Insecure-Requests: 1\r\n" +
            "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36\r\n" +
            "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7\r\n" +
            "Sec-Fetch-Site: same-origin\r\n" +
            "Sec-Fetch-Mode: navigate\r\n" +
            "Sec-Fetch-Dest: document\r\n" +
            "Referer: https://shop.example.com/api/products\r\n" +
            "Accept-Encoding: gzip, deflate, br, zstd\r\n" +
            "Accept-Language: en-US,en;q=0.9,vi;q=0.8\r\n" +
            "Cookie: session=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef; theme=dark; cart=eyJpdGVtcyI6W3siaWQiOjEyMywicXR5IjoyfSx7ImlkIjo0NTYsInF0eSI6MX1dfQ; _ga=GA1.2.1234567890.1700000000; _gid=GA1.2.987654321.1700000000; consent=granted\r\n" +
            "\r\n";

        [GlobalSetup]
        public void Setup()
        {
            _simpleGet = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(
                "GET /hello/world?x=1&y=2 HTTP/1.1\r\n" +
                "Host: localhost:8080\r\n" +
                "User-Agent: Unknown\r\n" +
                "Accept: */*\r\n" +
                "Connection: keep-alive\r\n" +
                "\r\n"));

            byte[] browser = Encoding.ASCII.GetBytes(BrowserGetText);
            _browserGet = new ReadOnlySequence<byte>(browser);
            _browserGetSegmented = Segment.Split(browser, 64);

            byte[] body = new byte[4096];
            Array.Fill(body, (byte)'x');
            byte[] head = Encoding.ASCII.GetBytes(
                "POST /api/orders HTTP/1.1\r\n" +
                "Host: shop.example.com\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: 4096\r\n" +
                "\r\n");
            _postWithBody = new ReadOnlySequence<byte>([.. head, .. body]);

            _remote = new IPEndPoint(IPAddress.Loopback, 50000);
            _local = new IPEndPoint(IPAddress.Loopback, 8080);

            _pool = new DefaultObjectPoolProvider().Create(new FlashHttpRequestPooledObjectPolicy());
        }

        // Each benchmark copies the sequence first because TryReadHttpRequest slices it.

        [Benchmark(Baseline = true)]
        public FlashHttpRequest SimpleGet() => Parse(_simpleGet, null);

        [Benchmark]
        public FlashHttpRequest BrowserGet() => Parse(_browserGet, null);

        [Benchmark]
        public FlashHttpRequest BrowserGetSegmented() => Parse(_browserGetSegmented, null);

        [Benchmark]
        public FlashHttpRequest PostWithBody4K() => Parse(_postWithBody, null);

        [Benchmark]
        public void BrowserGetPooled()
        {
            var request = Parse(_browserGet, _pool);
            _pool.Return(request);
        }

        private FlashHttpRequest Parse(ReadOnlySequence<byte> buffer, ObjectPool<FlashHttpRequest>? pool)
        {
            var result = FlashHttpParser.TryReadHttpRequest(
                ref buffer,
                out var request,
                out _,
                false,
                _remote,
                _local,
                pool);

            if (result != FlashHttpParser.TryReadHttpRequestResults.Success)
                throw new InvalidOperationException($"Parse failed: {result}");

            return request;
        }

        private sealed class Segment : ReadOnlySequenceSegment<byte>
        {
            private Segment(ReadOnlyMemory<byte> memory, long runningIndex)
            {
                Memory = memory;
                RunningIndex = runningIndex;
            }

            public static ReadOnlySequence<byte> Split(byte[] data, int segmentSize)
            {
                Segment? first = null;
                Segment? last = null;

                for (int offset = 0; offset < data.Length; offset += segmentSize)
                {
                    int length = Math.Min(segmentSize, data.Length - offset);
                    var segment = new Segment(data.AsMemory(offset, length), offset);
                    if (last is null)
                        first = segment;
                    else
                        last.Next = segment;
                    last = segment;
                }

                return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
            }
        }
    }
}
