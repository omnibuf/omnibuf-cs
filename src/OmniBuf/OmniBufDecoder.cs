using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Unicode;

namespace OmniBuf;

/// <summary>
/// Span-based reader, O(1) header-hop locator, and structural validator for OmniBuf binary packets.
/// </summary>
public ref struct OmniBufDecoder
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private readonly int _maxDepth;

    public OmniBufDecoder(ReadOnlySpan<byte> data, int maxDepth = OmniBufConstants.MaxNestingDepth)
    {
        _data = data;
        _pos = 0;
        _maxDepth = maxDepth;
    }

    public ReadOnlySpan<byte> Data => _data;

    public int Position
    {
        readonly get => _pos;
        set
        {
            if ((uint)value > (uint)_data.Length)
            {
                throw new OmniBufException($"omnibuf: position {value} out of bounds ({_data.Length})");
            }

            _pos = value;
        }
    }

    public readonly int Remaining => _data.Length - _pos;

    public readonly bool IsAtEnd => _pos >= _data.Length;

    public readonly int MaxDepth => _maxDepth;

    public ulong ReadVarint() => ReadVarint(_data, ref _pos);

    public (uint FieldNum, OmniBufWireType WireType) ReadTag() => ReadTag(_data, ref _pos);

    public ulong ReadUint() => ReadVarint(_data, ref _pos);

    public uint ReadUint32()
    {
        ulong v = ReadVarint(_data, ref _pos);
        if (v > uint.MaxValue)
        {
            throw new OmniBufException($"omnibuf: uint32 overflow ({v})");
        }

        return (uint)v;
    }

    public long ReadSint64() => OmniBufConstants.ZigZagDecode64(ReadVarint(_data, ref _pos));

    public int ReadSint32()
    {
        long v = OmniBufConstants.ZigZagDecode64(ReadVarint(_data, ref _pos));
        if (v < int.MinValue || v > int.MaxValue)
        {
            throw new OmniBufException($"omnibuf: sint32 overflow ({v})");
        }

        return (int)v;
    }

    public bool ReadBoolElement() => ReadBoolElement(_data, ref _pos);

    public uint ReadFixed32() => ReadFixed32(_data, ref _pos);

    public int ReadSfixed32() => (int)ReadFixed32(_data, ref _pos);

    public float ReadFloat32() => BitConverter.UInt32BitsToSingle(ReadFixed32(_data, ref _pos));

    public ulong ReadFixed64() => ReadFixed64(_data, ref _pos);

    public long ReadSfixed64() => (long)ReadFixed64(_data, ref _pos);

    public double ReadFloat64() => BitConverter.UInt64BitsToDouble(ReadFixed64(_data, ref _pos));

    public string ReadString() => ReadString(_data, ref _pos);

    public ReadOnlySpan<byte> ReadBytesSpan() => ReadLenSlice(_data, ref _pos, 0);

    public byte[] ReadBytes() => ReadLenSlice(_data, ref _pos, 0).ToArray();

    public void SkipValue(OmniBufWireType wt, int depth = 0) =>
        ValidateValue(_data, ref _pos, wt, depth, _maxDepth);

    /// <summary>
    /// Decodes a TZCNT Prefix-Varint without throwing. Returns false on truncation or reserved markers.
    /// Rejects non-zero prefix bytes whose low 3 bits are zero: (b &amp; 0x07) == 0 &amp;&amp; b != 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryDecodeVarint(ReadOnlySpan<byte> data, out ulong value, out int bytesRead)
    {
        if (data.IsEmpty)
        {
            value = 0;
            bytesRead = 0;
            return false;
        }

        byte b0 = data[0];
        uint low3 = (uint)(b0 & 0x07);
        if (low3 != 0)
        {
            int tz = BitOperations.TrailingZeroCount(low3);
            if (tz == 0)
            {
                value = (ulong)(b0 >> 1);
                bytesRead = 1;
                return true;
            }

            if (tz == 1)
            {
                if (data.Length < 2)
                {
                    value = 0;
                    bytesRead = 0;
                    return false;
                }

                value = (ulong)(BinaryPrimitives.ReadUInt16LittleEndian(data) >> 2);
                bytesRead = 2;
                return true;
            }

            if (data.Length < 4)
            {
                value = 0;
                bytesRead = 0;
                return false;
            }

            value = (ulong)(BinaryPrimitives.ReadUInt32LittleEndian(data) >> 3);
            bytesRead = 4;
            return true;
        }

        if (b0 != 0 || data.Length < 9)
        {
            // Reserved marker ((b0 & 0x07) == 0 && b0 != 0) or truncated 9-byte varint.
            value = 0;
            bytesRead = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(1, 8));
        bytesRead = 9;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        if ((uint)pos >= (uint)data.Length || !TryDecodeVarint(data.Slice(pos), out ulong v, out int n))
        {
            throw new OmniBufException($"omnibuf: invalid or truncated Prefix-Varint at byte {pos}");
        }

        pos += n;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (uint FieldNum, OmniBufWireType WireType) ReadTag(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong tag = ReadVarint(data, ref pos);
        ulong fieldNum64 = tag >> 4;
        if (fieldNum64 > OmniBufConstants.MaxFieldNumber)
        {
            throw new OmniBufException($"omnibuf: field number {fieldNum64} exceeds maximum {OmniBufConstants.MaxFieldNumber}");
        }

        uint fieldNum = (uint)fieldNum64;
        var wt = (OmniBufWireType)(tag & 0x0Fu);
        if (fieldNum == 0 && wt != OmniBufWireType.SubMsg)
        {
            throw new OmniBufException($"omnibuf: Field #0 requires SubMsg wire type, got {wt}");
        }

        return (fieldNum, wt);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> ReadLenSlice(ReadOnlySpan<byte> data, scoped ref int pos, int minBodyLen = 0)
    {
        ulong byteLen = ReadVarint(data, ref pos);
        int remaining = data.Length - pos;
        if (byteLen < (ulong)minBodyLen || byteLen > (ulong)remaining)
        {
            throw new OmniBufException($"omnibuf: length-prefixed payload ({byteLen}B) out of bounds at {pos}");
        }

        int len = (int)byteLen;
        ReadOnlySpan<byte> slice = data.Slice(pos, len);
        pos += len;
        return slice;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ReadBoolElement(ReadOnlySpan<byte> data, ref int pos)
    {
        if ((uint)pos >= (uint)data.Length)
        {
            throw new OmniBufException($"omnibuf: truncated bool element at {pos}");
        }

        byte b = data[pos++];
        if (b > 1)
        {
            throw new OmniBufException($"omnibuf: invalid bool padding bits (0x{b:x2}) at {pos - 1}");
        }

        return b == 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadFixed32(ReadOnlySpan<byte> data, ref int pos)
    {
        if (data.Length - pos < 4)
        {
            throw new OmniBufException($"omnibuf: truncated 32-bit fixed value at {pos}");
        }

        uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
        pos += 4;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadFixed64(ReadOnlySpan<byte> data, ref int pos)
    {
        if (data.Length - pos < 8)
        {
            throw new OmniBufException($"omnibuf: truncated 64-bit fixed value at {pos}");
        }

        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos, 8));
        pos += 8;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        ReadOnlySpan<byte> strBytes = ReadLenSlice(data, ref pos, 0);
        return OmniBufConstants.DecodeUtf8Strict(strBytes);
    }

    public static bool TryParseArrayHeader(
        ReadOnlySpan<byte> body,
        out OmniBufWireType elemType,
        out int count,
        out int payloadOffset)
    {
        elemType = default;
        count = 0;
        payloadOffset = 0;

        if (body.Length < 2)
        {
            return false;
        }

        elemType = (OmniBufWireType)body[0];
        if (!OmniBufConstants.IsValidContainerElemType(elemType))
        {
            return false;
        }

        if (!TryDecodeVarint(body.Slice(1), out ulong countU, out int cn))
        {
            return false;
        }

        payloadOffset = 1 + cn;
        int remaining = body.Length - payloadOffset;
        if (remaining < 0)
        {
            return false;
        }

        int width = OmniBufConstants.FixedElementWidth(elemType);
        if (width == 1)
        {
            if (countU != (ulong)remaining)
            {
                return false;
            }
        }
        else if (width == 4)
        {
            if ((remaining & 3) != 0 || countU != (ulong)(remaining >> 2))
            {
                return false;
            }
        }
        else if (width == 8)
        {
            if ((remaining & 7) != 0 || countU != (ulong)(remaining >> 3))
            {
                return false;
            }
        }
        else
        {
            if (countU == 0)
            {
                if (remaining != 0)
                {
                    return false;
                }
            }
            else if (countU > (ulong)remaining)
            {
                return false;
            }
        }

        count = (int)countU;
        return true;
    }

    public static bool TryParseMapHeader(
        ReadOnlySpan<byte> body,
        out OmniBufWireType keyType,
        out OmniBufWireType valType,
        out int count,
        out int payloadOffset)
    {
        keyType = default;
        valType = default;
        count = 0;
        payloadOffset = 0;

        if (body.Length < 3)
        {
            return false;
        }

        keyType = (OmniBufWireType)body[0];
        valType = (OmniBufWireType)body[1];
        if (!OmniBufConstants.IsValidMapKeyType(keyType) || !OmniBufConstants.IsValidContainerElemType(valType))
        {
            return false;
        }

        if (!TryDecodeVarint(body.Slice(2), out ulong countU, out int cn))
        {
            return false;
        }

        payloadOffset = 2 + cn;
        int remaining = body.Length - payloadOffset;
        if (remaining < 0)
        {
            return false;
        }

        if (countU == 0)
        {
            if (remaining != 0)
            {
                return false;
            }
        }
        else if (countU > (ulong)(remaining >> 1))
        {
            return false;
        }

        count = (int)countU;
        return true;
    }

    /// <summary>
    /// Fast header-level skip for O(1) field hopping (validates varint prefixes and bounds without deep recursion).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryFastSkipValue(ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt)
    {
        int n = data.Length;
        switch (wt)
        {
            case OmniBufWireType.Uint:
            case OmniBufWireType.Sint:
                {
                    if ((uint)pos >= (uint)n || !TryDecodeVarint(data.Slice(pos), out _, out int vn))
                    {
                        return false;
                    }

                    pos += vn;
                    return true;
                }

            case OmniBufWireType.BoolFalse:
            case OmniBufWireType.BoolTrue:
                return (uint)pos <= (uint)n;

            case OmniBufWireType.Fixed32:
            case OmniBufWireType.Sfixed32:
            case OmniBufWireType.Float32:
                if (n - pos < 4)
                {
                    return false;
                }

                pos += 4;
                return true;

            case OmniBufWireType.Fixed64:
            case OmniBufWireType.Sfixed64:
            case OmniBufWireType.Float64:
                if (n - pos < 8)
                {
                    return false;
                }

                pos += 8;
                return true;

            case OmniBufWireType.String:
            case OmniBufWireType.Bytes:
            case OmniBufWireType.SubMsg:
                {
                    if ((uint)pos >= (uint)n || !TryDecodeVarint(data.Slice(pos), out ulong len, out int ln))
                    {
                        return false;
                    }

                    pos += ln;
                    if (len > (ulong)(n - pos))
                    {
                        return false;
                    }

                    pos += (int)len;
                    return true;
                }

            case OmniBufWireType.Array:
            case OmniBufWireType.Map:
            case OmniBufWireType.Extended:
                {
                    int minBody = wt switch
                    {
                        OmniBufWireType.Array => 2,
                        OmniBufWireType.Map => 3,
                        _ => 1, // Extended requires >= 1 byte (1B ExtType subtype header)
                    };

                    if ((uint)pos >= (uint)n || !TryDecodeVarint(data.Slice(pos), out ulong len, out int ln))
                    {
                        return false;
                    }

                    pos += ln;
                    if (len < (ulong)minBody || len > (ulong)(n - pos))
                    {
                        return false;
                    }

                    ReadOnlySpan<byte> body = data.Slice(pos, (int)len);
                    if (wt == OmniBufWireType.Array && !TryParseArrayHeader(body, out _, out _, out _))
                    {
                        return false;
                    }

                    if (wt == OmniBufWireType.Map && !TryParseMapHeader(body, out _, out _, out _, out _))
                    {
                        return false;
                    }

                    pos += (int)len;
                    return true;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// Validates and skips a field value, enforcing container headers, UTF-8 validity, bool padding bits,
    /// WireExtended >= 1B subtype header, Field #0 SchemaMeta rules, and nesting depth bounds.
    /// </summary>
    public static void ValidateValue(
        ReadOnlySpan<byte> data,
        ref int pos,
        OmniBufWireType wt,
        int depth = 0,
        int maxDepth = OmniBufConstants.MaxNestingDepth)
    {
        if (depth > maxDepth)
        {
            throw new OmniBufException($"omnibuf: nesting depth {depth} exceeds maximum {maxDepth}");
        }

        switch (wt)
        {
            case OmniBufWireType.Uint:
            case OmniBufWireType.Sint:
                _ = ReadVarint(data, ref pos);
                return;

            case OmniBufWireType.BoolFalse:
            case OmniBufWireType.BoolTrue:
                if ((uint)pos > (uint)data.Length)
                {
                    throw new OmniBufException("omnibuf: position out of bounds");
                }

                return;

            case OmniBufWireType.Fixed32:
            case OmniBufWireType.Sfixed32:
            case OmniBufWireType.Float32:
                _ = ReadFixed32(data, ref pos);
                return;

            case OmniBufWireType.Fixed64:
            case OmniBufWireType.Sfixed64:
            case OmniBufWireType.Float64:
                _ = ReadFixed64(data, ref pos);
                return;

            case OmniBufWireType.String:
                {
                    ReadOnlySpan<byte> strBytes = ReadLenSlice(data, ref pos, 0);
                    if (!Utf8.IsValid(strBytes))
                    {
                        throw new OmniBufException("omnibuf: invalid UTF-8 string payload");
                    }

                    return;
                }

            case OmniBufWireType.Bytes:
                _ = ReadLenSlice(data, ref pos, 0);
                return;

            case OmniBufWireType.SubMsg:
                {
                    ReadOnlySpan<byte> sub = ReadLenSlice(data, ref pos, 0);
                    ValidateMessage(sub, depth + 1, maxDepth);
                    return;
                }

            case OmniBufWireType.Array:
                {
                    if (depth + 1 > maxDepth)
                    {
                        throw new OmniBufException($"omnibuf: nesting depth {depth + 1} exceeds maximum {maxDepth}");
                    }

                    ReadOnlySpan<byte> body = ReadLenSlice(data, ref pos, 2);
                    if (!TryParseArrayHeader(body, out OmniBufWireType elemType, out int count, out int p))
                    {
                        throw new OmniBufException("omnibuf: invalid WireArray header");
                    }

                    ValidateArrayBody(body, p, elemType, count, depth + 1, maxDepth);
                    return;
                }

            case OmniBufWireType.Map:
                {
                    if (depth + 1 > maxDepth)
                    {
                        throw new OmniBufException($"omnibuf: nesting depth {depth + 1} exceeds maximum {maxDepth}");
                    }

                    ReadOnlySpan<byte> body = ReadLenSlice(data, ref pos, 3);
                    if (!TryParseMapHeader(body, out OmniBufWireType keyType, out OmniBufWireType valType, out int count, out int p))
                    {
                        throw new OmniBufException("omnibuf: invalid WireMap header");
                    }

                    for (int i = 0; i < count; i++)
                    {
                        ValidateContainerEntry(body, ref p, keyType, depth + 1, maxDepth);
                        ValidateContainerEntry(body, ref p, valType, depth + 1, maxDepth);
                    }

                    if (p != body.Length)
                    {
                        throw new OmniBufException("omnibuf: trailing bytes in WireMap");
                    }

                    return;
                }

            case OmniBufWireType.Extended:
                {
                    // WireExtended requires at least 1 byte for the 1-byte ExtType header (0x00..0xFF).
                    _ = ReadLenSlice(data, ref pos, 1);
                    return;
                }

            default:
                throw new OmniBufException($"omnibuf: unknown wire type {(byte)wt}");
        }
    }

    private static void ValidateArrayBody(
        ReadOnlySpan<byte> body,
        int p,
        OmniBufWireType elemType,
        int count,
        int depth,
        int maxDepth)
    {
        switch (elemType)
        {
            case OmniBufWireType.BoolFalse:
                if (body.Length - p != count)
                {
                    throw new OmniBufException("omnibuf: invalid bool array byte length");
                }

                for (int i = 0; i < count; i++)
                {
                    if (body[p + i] > 1)
                    {
                        throw new OmniBufException("omnibuf: invalid bool byte in WireArray");
                    }
                }

                return;

            case OmniBufWireType.Fixed32:
            case OmniBufWireType.Sfixed32:
            case OmniBufWireType.Float32:
                if (body.Length - p != count * 4)
                {
                    throw new OmniBufException("omnibuf: invalid 32-bit array byte length");
                }

                return;

            case OmniBufWireType.Fixed64:
            case OmniBufWireType.Sfixed64:
            case OmniBufWireType.Float64:
                if (body.Length - p != count * 8)
                {
                    throw new OmniBufException("omnibuf: invalid 64-bit array byte length");
                }

                return;
        }

        for (int i = 0; i < count; i++)
        {
            ValidateContainerEntry(body, ref p, elemType, depth, maxDepth);
        }

        if (p != body.Length)
        {
            throw new OmniBufException("omnibuf: trailing bytes in WireArray");
        }
    }

    private static void ValidateContainerEntry(
        ReadOnlySpan<byte> body,
        ref int p,
        OmniBufWireType wt,
        int depth,
        int maxDepth)
    {
        if (wt == OmniBufWireType.BoolFalse)
        {
            _ = ReadBoolElement(body, ref p);
            return;
        }

        ValidateValue(body, ref p, wt, depth, maxDepth);
    }

    public static void ValidateMessage(
        ReadOnlySpan<byte> data,
        int depth = 0,
        int maxDepth = OmniBufConstants.MaxNestingDepth)
    {
        if (depth > maxDepth)
        {
            throw new OmniBufException($"omnibuf: nesting depth {depth} exceeds maximum {maxDepth}");
        }

        int pos = 0;
        while (pos < data.Length)
        {
            (uint fieldNum, OmniBufWireType wt) = ReadTag(data, ref pos);
            if (fieldNum == 0)
            {
                SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
            }
            else
            {
                ValidateValue(data, ref pos, wt, depth, maxDepth);
            }
        }
    }
}
