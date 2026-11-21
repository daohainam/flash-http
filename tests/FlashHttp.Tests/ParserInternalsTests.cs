using FlashHttp.Abstractions;
using FlashHttp.Server;
using System.Buffers;
using System.Text;
using Xunit;

using Results = FlashHttp.Server.FlashHttpParser.TryReadHttpRequestResults;

namespace FlashHttp.Tests;

/// <summary>
/// Allocation-avoidance behaviour of the parser (interned names and paths) and equivalence
/// of its single-segment and multi-segment code paths.
/// </summary>
public sealed class ParserInternalsTests
{
    private const string BrowserLikeRequest =
        "POST /api/orders?id=7&x=y HTTP/1.1\r\n" +
        "host: shop.example.com\r\n" +
        "USER-AGENT: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36\r\n" +
        "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7\r\n" +
        "accept-encoding: gzip, deflate, br\r\n" +
        "Cookie: session=0123456789abcdef; theme=dark\r\n" +
        "X-Custom-Thing: café\r\n" +
        "Content-Type: application/json\r\n" +
        "Connection: Keep-Alive, close\r\n" +
        "Expect: 100-continue\r\n" +
        "Content-Length: 12\r\n" +
        "\r\n";

    private static ReadOnlySequence<byte> Seq(string s) => new(Encoding.Latin1.GetBytes(s));

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public void SetNext(Segment next) => Next = next;
    }

    private static ReadOnlySequence<byte> Segmented(string s, int segmentSize)
    {
        byte[] data = Encoding.Latin1.GetBytes(s);
        Segment? first = null;
        Segment? last = null;

        for (int offset = 0; offset < data.Length; offset += segmentSize)
        {
            var segment = new Segment(data.AsMemory(offset, Math.Min(segmentSize, data.Length - offset)), offset);
            if (last is null) first = segment; else last.SetNext(segment);
            last = segment;
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private static string Describe(Results result, FlashHttpParser.RequestHead head, List<HttpHeader> headers, ReadOnlySequence<byte> rest)
        => $"{result}|{head.Method}|{head.Path}|{head.QueryString}|{head.ContentType}|{head.KeepAlive}|{head.ExpectContinue}|{head.ContentLength}|"
           + string.Join(";", headers.Select(h => h.Name + "=" + h.Value))
           + "|" + Encoding.Latin1.GetString(rest.ToArray());

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(200)]
    public void SegmentedInput_ParsesExactlyLikeContiguousInput(int segmentSize)
    {
        const string text = BrowserLikeRequest + "hello world!";

        var expectedHeaders = new List<HttpHeader>();
        var expectedHead = new FlashHttpParser.RequestHead();
        var contiguous = Seq(text);
        var expectedResult = FlashHttpParser.TryReadRequestHead(ref contiguous, ref expectedHead, expectedHeaders);
        Assert.Equal(Results.Success, expectedResult);

        var headers = new List<HttpHeader>();
        var head = new FlashHttpParser.RequestHead();
        var segmented = Segmented(text, segmentSize);
        var result = FlashHttpParser.TryReadRequestHead(ref segmented, ref head, headers);

        Assert.Equal(
            Describe(expectedResult, expectedHead, expectedHeaders, contiguous),
            Describe(result, head, headers, segmented));
    }

    [Fact]
    public void SegmentedInput_OverlongHeaderLine_IsRejected()
    {
        var headers = new List<HttpHeader>();
        var head = new FlashHttpParser.RequestHead();
        var buffer = Segmented("GET / HTTP/1.1\r\nHost: a\r\nX: " + new string('a', 9000) + "\r\n\r\n", 100);

        Assert.Equal(Results.HeaderLineTooLong, FlashHttpParser.TryReadRequestHead(ref buffer, ref head, headers));
    }

    [Fact]
    public void WellKnownHeaderNames_AreInternedRegardlessOfCase()
    {
        var headers = new List<HttpHeader>();
        var head = new FlashHttpParser.RequestHead();
        var buffer = Seq(BrowserLikeRequest);

        Assert.Equal(Results.Success, FlashHttpParser.TryReadRequestHead(ref buffer, ref head, headers));

        Assert.Equal(
            ["Host", "User-Agent", "Accept", "Accept-Encoding", "Cookie", "X-Custom-Thing", "Content-Type", "Connection", "Expect", "Content-Length"],
            headers.Select(h => h.Name));

        // Known names come from a fixed table instead of being decoded into a new string per
        // request; an unknown name is necessarily a fresh string.
        foreach (var header in headers)
        {
            bool isSharedInstance = ReferenceEquals(string.IsInterned(header.Name), header.Name);
            Assert.Equal(header.Name != "X-Custom-Thing", isSharedInstance);
        }

        // Values are untouched: Latin-1 decoded, case and inner whitespace preserved.
        Assert.Equal("café", headers[5].Value);
        Assert.Equal("Keep-Alive, close", headers[7].Value);
        Assert.False(head.KeepAlive);
        Assert.True(head.ExpectContinue);
        Assert.Equal(12, head.ContentLength);
        Assert.Equal("application/json", head.ContentType);
    }

    [Theory]
    [InlineData("0", "Success")]
    [InlineData("12", "Success")]
    [InlineData("", "InvalidRequest")]
    [InlineData("+12", "InvalidRequest")]
    [InlineData("-1", "InvalidRequest")]
    [InlineData("1 2", "InvalidRequest")]
    [InlineData("12,12", "InvalidRequest")]
    [InlineData("0x10", "InvalidRequest")]
    [InlineData("99999999999999999999999999", "RequestBodyTooLarge")]
    [InlineData("9223372036854775807", "RequestBodyTooLarge")]
    public void ContentLength_IsParsedStrictly(string value, string expected)
    {
        var headers = new List<HttpHeader>();
        var head = new FlashHttpParser.RequestHead();
        var buffer = Seq($"POST / HTTP/1.1\r\nHost: a\r\nContent-Length: {value}\r\n\r\n");

        Assert.Equal(expected, FlashHttpParser.TryReadRequestHead(ref buffer, ref head, headers).ToString());
    }

    [Fact]
    public void RegisteredPath_IsReusedInsteadOfAllocated()
    {
        var routes = new HandlerSet();
        string registered = new string("/api/orders".AsSpan()); // a distinct instance, not the literal
        routes.Register(HttpMethodsEnum.Post, registered, static (_, _) => ValueTask.CompletedTask);

        var headers = new List<HttpHeader>();
        var head = new FlashHttpParser.RequestHead();
        var buffer = Seq("POST /api/orders?id=7 HTTP/1.1\r\nHost: a\r\n\r\n");

        Assert.Equal(Results.Success, FlashHttpParser.TryReadRequestHead(ref buffer, ref head, headers, pathLookup: routes.FindRegisteredPath));

        Assert.Same(registered, head.Path);
        Assert.Equal("id=7", head.QueryString);
    }

    [Fact]
    public void UnregisteredPath_IsStillParsed()
    {
        var routes = new HandlerSet();
        routes.Register(HttpMethodsEnum.Get, "/known", static (_, _) => ValueTask.CompletedTask);

        var headers = new List<HttpHeader>();
        var head = new FlashHttpParser.RequestHead();
        var buffer = Seq("GET /unknown/" + new string('p', 600) + " HTTP/1.1\r\nHost: a\r\n\r\n");

        Assert.Equal(Results.Success, FlashHttpParser.TryReadRequestHead(ref buffer, ref head, headers, pathLookup: routes.FindRegisteredPath));

        Assert.Equal("/unknown/" + new string('p', 600), head.Path);
    }

    [Fact]
    public void FindRegisteredPath_SeesRoutesOfEveryMethod_AndLaterRegistrations()
    {
        var routes = new HandlerSet();
        routes.Register(HttpMethodsEnum.Get, "/a", static (_, _) => ValueTask.CompletedTask);

        Assert.Equal("/a", routes.FindRegisteredPath("/a"));
        Assert.Null(routes.FindRegisteredPath("/b"));

        routes.Register(HttpMethodsEnum.Delete, "/b", static (_, _) => ValueTask.CompletedTask);

        Assert.Equal("/b", routes.FindRegisteredPath("/b"));
    }
}
