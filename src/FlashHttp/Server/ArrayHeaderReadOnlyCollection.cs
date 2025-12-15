using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;

namespace FlashHttp.Server;

internal class ArrayHeaderReadOnlyCollection : IHeaderReadOnlyCollection
{
    private int _count;
    private readonly Span<byte>[] _nameSpans = new Span<byte>[128];
    private readonly Memory<byte>[] _valueSpans = new Memory<byte>[128];
    private byte[] _buffer;
    public bool TryGetValue(string name, out string value)
    {
        value = null!;
        return false;
    }

    public static ArrayHeaderReadOnlyCollection CreateFrom(ReadOnlySequence<byte> buffer)
    {
        var collection = new ArrayHeaderReadOnlyCollection();
        int bufferSize = 0;
        int len = checked((int)buffer.Length);

        collection._buffer = ArrayPool<byte>.Shared.Rent(len);
        buffer.CopyTo(collection._buffer);

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(collection._buffer));

        while (true)
        {
            if (reader.TryReadTo(out ReadOnlySequence<byte> name, (byte)':'))
            {
                if (name.Length == 0)
                {
                    break;
                }

                collection._nameSpans[collection._count] = name.IsSingleSegment ? name.FirstSpan : name.ToArray();
                collection._count++;

                bufferSize += checked((int)name.Length);
            }
            else
            {
                break;
            }   

            if (!TryReadLine(ref reader, out ReadOnlySequence<byte> headerLineSeq))
            {
                break;
            }

            if (headerLineSeq.Length == 0)
            {
                break;
            }

            int len = checked((int)headerLineSeq.Length);
            if (len > 0 && headerLineSeq.Slice(len - 1, 1).FirstSpan[0] == CR)
            {
                headerLineSeq = headerLineSeq.Slice(0, len - 1);
                len--;
            }

            if (len == 0)
            {
                break;
            }

            int colonIndex = headerLineSeq.g..IndexOf((byte)':');
            if (colonIndex <= 0)
            {
                name = value = null;
                return false;
            }
        }

        return collection;
    }

    private static bool TryReadLine(ref SequenceReader<byte> reader, out ReadOnlySequence<byte> line)
    {
        if (!reader.TryReadTo(out line, LF))
        {
            line = default;
            return false;
        }

        return true;
    }

    private static readonly byte LF = (byte)'\n';
    private static readonly byte CR = (byte)'\r';

}
