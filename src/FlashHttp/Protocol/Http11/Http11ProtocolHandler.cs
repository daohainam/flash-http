using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Reflection.PortableExecutable;
using System.Text;

namespace FlashHttp.Protocol.Http11;
internal class Http11ProtocolHandler : IProtocolHandler
{
    private Stream stream;
    private bool isHttps;
    private ILogger logger;

    public Http11ProtocolHandler(Stream stream, bool isHttps, ILogger logger)
    {
        this.stream = stream;
        this.isHttps = isHttps;
        this.logger = logger;
    }

    public async Task<FlashHttpRequest?> ReadRequestAsync(CancellationToken cancellationToken = default)
    {
        PipeReader reader = PipeReader.Create(stream);

        ReadResult readResult = await reader.ReadAsync(cancellationToken);
        ReadOnlySequence<byte> buffer = readResult.Buffer;

        /*
        HttpVersions httpVersion = TryGetHttpVersion(buffer);
        if (httpVersion != HttpVersions.Http11)
        {
            throw new NotSupportedException("Unsupported HTTP version");
        }
        */

        var httpMethod = HttpMethodsEnum.Get;
        if (TryReadLine(ref buffer, out ReadOnlySequence<byte> line))
        {
            //var requestLine = httpComponentParser.ParseRequestLine(line);

            //if (requestLine != null)
            //{
            //    logger.LogDebug("Parsed request line: {requestLine}", requestLine);

            //    // when implementing as a sequence of octets, if method length exceeds method buffer length, you should return 501 Not Implemented 
            //    // if Url length exceeds Url buffer length, you should return 414 URI Too Long

            //    // todo: parse the Url with percent-encoding (https://www.rfc-editor.org/rfc/rfc3986)

            //    httpMethod = requestLine.Method;
            //    requestBuilder
            //        .SetMethod(requestLine.Method)
            //        .SetUrl(requestLine.Url)
            //        .SetParameters(requestLine.Parameters)
            //        .SetQueryString(requestLine.QueryString)
            //        .SetHash(requestLine.Hash)
            //        .SetSegments(requestLine.Segments);
            //}
            //else
            //{
            //    logger.LogError("Invalid request line");

            //    return null;
            //}

            reader.AdvanceTo(buffer.Start); // after a successful TryReadLine, buffer.Start advanced to the byte after '\n'
        }
        else
        {
            // we could not read header line
            return null;
        }

        return null; // Placeholder
    }

    public void Initialize(CancellationToken cancellationToken = default)
    {
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        SequencePosition? pos = buffer.PositionOf((byte)'\n');

        if (pos != null)
        {
            line = buffer.Slice(0, pos.Value);
            buffer = buffer.Slice(buffer.GetPosition(1, pos.Value));
            return true;
        }
        else
        {
            line = default;
            return false;
        }
    }

    private static HttpVersions TryGetHttpVersion(ReadOnlySequence<byte> buffer)
    {
        var httpVersion = HttpVersions.Http11;

        if (buffer.Length >= HTTP2_MAGIC.Length)
        {
            var span = ToSpan(buffer.Slice(0, HTTP2_MAGIC.Length));

            if (span.SequenceEqual(HTTP2_MAGIC))
            {
                return HttpVersions.Http2;
            }
        }

        return httpVersion;
    }

    private static ReadOnlySpan<byte> ToSpan(ReadOnlySequence<byte> buffer)
    {
        if (buffer.IsSingleSegment)
        {
            return buffer.FirstSpan;
        }
        return buffer.ToArray();
    }

    private static readonly byte[] HTTP2_MAGIC = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

}
