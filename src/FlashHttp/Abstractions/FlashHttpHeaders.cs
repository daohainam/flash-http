using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace FlashHttp.Abstractions;

public enum KnownHeader : byte
{
    Host = 0,
    Authorization = 1,
    ContentType = 2,
    Connection = 3,
    ContentLength = 4
}

internal readonly struct HeaderSlice
{
    public readonly int NameStart, NameLen;
    public readonly int ValueStart, ValueLen;

    public HeaderSlice(int ns, int nl, int vs, int vl)
        => (NameStart, NameLen, ValueStart, ValueLen) = (ns, nl, vs, vl);
}

/// <summary>
/// Stores raw header bytes + (name,value) slices.
/// Optimized for low allocations:
/// - No per-header string/object allocation
/// - Lazy string decoding on demand
/// - Optional "known header" indices for O(1) lookup
/// </summary>
public sealed class FlashHttpHeaders
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private byte[] _raw;
    private int _rawLen;

    private HeaderSlice[] _slices;
    private int _count;

    // Optional cache for decoded values (index-aligned with _slices)
    private string?[]? _valueStringCache;

    // Known header indices (-1 means not present)
    private int _idxHost = -1;
    private int _idxAuthorization = -1;
    private int _idxContentType = -1;
    private int _idxConnection = -1;
    private int _idxContentLength = -1;

    public FlashHttpHeaders(int initialSliceCapacity = 16, int initialRawCapacity = 512)
    {
        _raw = ArrayPool<byte>.Shared.Rent(initialRawCapacity);
        _rawLen = 0;

        _slices = ArrayPool<HeaderSlice>.Shared.Rent(initialSliceCapacity);
        _count = 0;
    }

    public int Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        _rawLen = 0;
        _count = 0;

        _idxHost = -1;
        _idxAuthorization = -1;
        _idxContentType = -1;
        _idxConnection = -1;
        _idxContentLength = -1;

        if (_valueStringCache != null)
        {
            Array.Clear(_valueStringCache, 0, _valueStringCache.Length);
        }
    }

    public void DisposeToPools()
    {
        if (_raw.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(_raw);
            _raw = Array.Empty<byte>();
        }

        if (_slices.Length != 0)
        {
            ArrayPool<HeaderSlice>.Shared.Return(_slices);
            _slices = Array.Empty<HeaderSlice>();
        }

        _valueStringCache = null;
        _rawLen = 0;
        _count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetNameBytes(int index)
    {
        var s = _slices[index];
        return _raw.AsSpan(s.NameStart, s.NameLen);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetValueBytes(int index)
    {
        var s = _slices[index];
        return _raw.AsSpan(s.ValueStart, s.ValueLen);
    }

    /// <summary>
    /// Add a header name/value as bytes (copies into pooled raw buffer) and returns its index.
    /// </summary>
    public int Add(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        EnsureSliceCapacity(_count + 1);

        int nameStart = AppendToRaw(name);
        int valueStart = AppendToRaw(value);

        int idx = _count++;
        _slices[idx] = new HeaderSlice(nameStart, name.Length, valueStart, value.Length);
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetKnownIndex(KnownHeader header, int index)
    {
        // Keep the first occurrence (common behavior); if you prefer last-wins, remove the checks.
        switch (header)
        {
            case KnownHeader.Host:
                if (_idxHost == -1) _idxHost = index;
                break;
            case KnownHeader.Authorization:
                if (_idxAuthorization == -1) _idxAuthorization = index;
                break;
            case KnownHeader.ContentType:
                if (_idxContentType == -1) _idxContentType = index;
                break;
            case KnownHeader.Connection:
                if (_idxConnection == -1) _idxConnection = index;
                break;
            case KnownHeader.ContentLength:
                if (_idxContentLength == -1) _idxContentLength = index;
                break;
        }
    }

    public bool TryGetKnownValueBytes(KnownHeader header, out ReadOnlySpan<byte> value)
    {
        int idx = header switch
        {
            KnownHeader.Host => _idxHost,
            KnownHeader.Authorization => _idxAuthorization,
            KnownHeader.ContentType => _idxContentType,
            KnownHeader.Connection => _idxConnection,
            KnownHeader.ContentLength => _idxContentLength,
            _ => -1
        };

        if (idx < 0)
        {
            value = default;
            return false;
        }

        var s = _slices[idx];
        value = _raw.AsSpan(s.ValueStart, s.ValueLen);
        return true;
    }

    public bool TryGetKnownValueString(KnownHeader header, out string? value, bool cache = true)
    {
        int idx = header switch
        {
            KnownHeader.Host => _idxHost,
            KnownHeader.Authorization => _idxAuthorization,
            KnownHeader.ContentType => _idxContentType,
            KnownHeader.Connection => _idxConnection,
            KnownHeader.ContentLength => _idxContentLength,
            _ => -1
        };

        if (idx < 0)
        {
            value = null;
            return false;
        }

        var s = _slices[idx];
        var v = _raw.AsSpan(s.ValueStart, s.ValueLen);

        if (!cache)
        {
            value = Latin1.GetString(v);
            return true;
        }

        EnsureValueCache();
        var cached = _valueStringCache![idx];
        if (cached != null)
        {
            value = cached;
            return true;
        }

        var decoded = Latin1.GetString(v);
        _valueStringCache[idx] = decoded;
        value = decoded;
        return true;
    }

    /// <summary>
    /// Try get a header value as bytes by ASCII case-insensitive header name.
    /// </summary>
    public bool TryGetValueBytes(ReadOnlySpan<byte> headerNameAscii, out ReadOnlySpan<byte> value)
    {
        for (int i = 0; i < _count; i++)
        {
            var s = _slices[i];
            var name = _raw.AsSpan(s.NameStart, s.NameLen);
            if (AsciiEqualsIgnoreCase(name, headerNameAscii))
            {
                value = _raw.AsSpan(s.ValueStart, s.ValueLen);
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Try get a header value decoded to string (Latin1). Optionally caches decoded strings.
    /// </summary>
    public bool TryGetValueString(ReadOnlySpan<byte> headerNameAscii, out string? value, bool cache = true)
    {
        for (int i = 0; i < _count; i++)
        {
            var s = _slices[i];
            var name = _raw.AsSpan(s.NameStart, s.NameLen);
            if (!AsciiEqualsIgnoreCase(name, headerNameAscii))
                continue;

            var v = _raw.AsSpan(s.ValueStart, s.ValueLen);

            if (!cache)
            {
                value = Latin1.GetString(v);
                return true;
            }

            EnsureValueCache();

            var cached = _valueStringCache![i];
            if (cached != null)
            {
                value = cached;
                return true;
            }

            var decoded = Latin1.GetString(v);
            _valueStringCache[i] = decoded;
            value = decoded;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Parse Int32 header value without allocating.</summary>
    public bool TryGetInt32(ReadOnlySpan<byte> headerNameAscii, out int value)
    {
        if (!TryGetValueBytes(headerNameAscii, out var v))
        {
            value = 0;
            return false;
        }
        return TryParseInt32(v, out value);
    }

    public bool TryGetKnownInt32(KnownHeader header, out int value)
    {
        if (!TryGetKnownValueBytes(header, out var v))
        {
            value = 0;
            return false;
        }
        return TryParseInt32(v, out value);
    }

    /// <summary>
    /// Materialize all headers into a List&lt;HttpHeader&gt; (allocates strings + HttpHeader objects).
    /// </summary>
    public List<HttpHeader> Materialize()
    {
        var list = new List<HttpHeader>(_count);
        for (int i = 0; i < _count; i++)
        {
            var s = _slices[i];
            var name = Latin1.GetString(_raw.AsSpan(s.NameStart, s.NameLen));
            var value = Latin1.GetString(_raw.AsSpan(s.ValueStart, s.ValueLen));
            list.Add(new HttpHeader(name, value));
        }
        return list;
    }

    // ===== internals =====

    private int AppendToRaw(ReadOnlySpan<byte> bytes)
    {
        EnsureRawCapacity(_rawLen + bytes.Length);
        bytes.CopyTo(_raw.AsSpan(_rawLen));
        int start = _rawLen;
        _rawLen += bytes.Length;
        return start;
    }

    private void EnsureRawCapacity(int needed)
    {
        if (needed <= _raw.Length) return;

        int newSize = Math.Max(needed, _raw.Length * 2);
        var newBuf = ArrayPool<byte>.Shared.Rent(newSize);
        _raw.AsSpan(0, _rawLen).CopyTo(newBuf);

        ArrayPool<byte>.Shared.Return(_raw);
        _raw = newBuf;
    }

    private void EnsureSliceCapacity(int needed)
    {
        if (needed <= _slices.Length) return;

        int newSize = Math.Max(needed, _slices.Length * 2);
        var newArr = ArrayPool<HeaderSlice>.Shared.Rent(newSize);
        Array.Copy(_slices, newArr, _count);

        ArrayPool<HeaderSlice>.Shared.Return(_slices);
        _slices = newArr;

        if (_valueStringCache != null)
        {
            var newCache = new string?[newSize];
            Array.Copy(_valueStringCache, newCache, _valueStringCache.Length);
            _valueStringCache = newCache;
        }
    }

    private void EnsureValueCache()
    {
        if (_valueStringCache != null) return;
        _valueStringCache = new string?[_slices.Length];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;

        for (int i = 0; i < a.Length; i++)
        {
            byte ca = a[i];
            byte cb = b[i];

            if ((uint)(ca - (byte)'A') <= (uint)('Z' - 'A')) ca = (byte)(ca + 32);
            if ((uint)(cb - (byte)'A') <= (uint)('Z' - 'A')) cb = (byte)(cb + 32);

            if (ca != cb) return false;
        }
        return true;
    }

    internal static bool TryParseInt32(ReadOnlySpan<byte> s, out int value)
    {
        int start = 0;
        int end = s.Length - 1;
        while (start <= end && (s[start] == (byte)' ' || s[start] == (byte)'\t')) start++;
        while (end >= start && (s[end] == (byte)' ' || s[end] == (byte)'\t')) end--;
        if (start > end) { value = 0; return false; }

        int v = 0;
        for (int i = start; i <= end; i++)
        {
            byte c = s[i];
            if (c < (byte)'0' || c > (byte)'9') { value = 0; return false; }
            v = checked(v * 10 + (c - (byte)'0'));
        }
        value = v;
        return true;
    }
}
