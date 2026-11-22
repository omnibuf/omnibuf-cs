using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// Reads an unsigned varint and truncates to the low 32 bits per Spec section 11.1.
    /// Use <see cref="ReadUint32Checked"/> to reject values exceeding <see cref="uint.MaxValue"/>.
    /// </summary>
    public uint ReadUint32() => (uint)ReadVarint(_data, ref _pos);

    public uint ReadUint32Checked()
    {
        ulong v = ReadVarint(_data, ref _pos);
        if (v > uint.MaxValue)
        {
            throw new OmniBufException($"omnibuf: uint32 overflow ({v})");
        }

        return (uint)v;
    }

    public long ReadSint64() => OmniBufConstants.ZigZagDecode64(ReadVarint(_data, ref _pos));

    /// <summary>
    /// Reads a ZigZag-encoded varint and truncates to 32 bits per Spec section 11.1.
    /// Use <see cref="ReadSint32Checked"/> to reject values outside <see cref="int"/> range.
    /// </summary>
    public int ReadSint32() => (int)OmniBufConstants.ZigZagDecode64(ReadVarint(_data, ref _pos));

    public int ReadSint32Checked()
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
        int len = data.Length;
        if (len == 0)
        {
            value = 0;
            bytesRead = 0;
            return false;
        }

        ref byte r = ref MemoryMarshal.GetReference(data);
        byte b0 = r;
        if ((b0 & 1) != 0)
        {
            value = (ulong)(b0 >> 1);
            bytesRead = 1;
            return true;
        }

        if ((b0 & 2) != 0)
        {
            if (len < 2)
            {
                value = 0;
                bytesRead = 0;
                return false;
            }

            ushort u16 = Unsafe.ReadUnaligned<ushort>(ref r);
            if (!BitConverter.IsLittleEndian)
            {
                u16 = BinaryPrimitives.ReverseEndianness(u16);
            }

            value = (ulong)(u16 >> 2);
            bytesRead = 2;
            return true;
        }

        if ((b0 & 4) != 0)
        {
            if (len < 4)
            {
                value = 0;
                bytesRead = 0;
                return false;
            }

            uint u32 = Unsafe.ReadUnaligned<uint>(ref r);
            if (!BitConverter.IsLittleEndian)
            {
                u32 = BinaryPrimitives.ReverseEndianness(u32);
            }

            value = (ulong)(u32 >> 3);
            bytesRead = 4;
            return true;
        }

        if (b0 != 0 || len < 9)
        {
            // Reserved marker ((b0 & 0x07) == 0 && b0 != 0) or truncated 9-byte varint.
            value = 0;
            bytesRead = 0;
            return false;
        }

        ulong u64 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, 1));
        if (!BitConverter.IsLittleEndian)
        {
            u64 = BinaryPrimitives.ReverseEndianness(u64);
        }

        value = u64;
        bytesRead = 9;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        int p = pos;
        int len = data.Length;
        if ((uint)p < (uint)len)
        {
            ref byte r = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), p);
            byte b0 = r;
            if ((b0 & 1) != 0)
            {
                pos = p + 1;
                return (ulong)(b0 >> 1);
            }

            int rem = len - p;
            if ((b0 & 2) != 0)
            {
                if (rem >= 2)
                {
                    ushort u16 = Unsafe.ReadUnaligned<ushort>(ref r);
                    if (!BitConverter.IsLittleEndian)
                    {
                        u16 = BinaryPrimitives.ReverseEndianness(u16);
                    }

                    pos = p + 2;
                    return (ulong)(u16 >> 2);
                }
            }
            else if ((b0 & 4) != 0)
            {
                if (rem >= 4)
                {
                    uint u32 = Unsafe.ReadUnaligned<uint>(ref r);
                    if (!BitConverter.IsLittleEndian)
                    {
                        u32 = BinaryPrimitives.ReverseEndianness(u32);
                    }

                    pos = p + 4;
                    return (ulong)(u32 >> 3);
                }
            }
            else if (b0 == 0 && rem >= 9)
            {
                ulong u64 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, 1));
                if (!BitConverter.IsLittleEndian)
                {
                    u64 = BinaryPrimitives.ReverseEndianness(u64);
                }

                pos = p + 9;
                return u64;
            }
        }

        ThrowInvalidVarint(p);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong ReadVarintSlow(ReadOnlySpan<byte> data, ref int pos)
    {
        int p = pos;
        int len = data.Length;
        if ((uint)p < (uint)len)
        {
            ref byte r = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), p);
            byte b0 = r;
            if ((b0 & 2) != 0)
            {
                if (len - p >= 2)
                {
                    ushort u16 = Unsafe.ReadUnaligned<ushort>(ref r);
                    if (!BitConverter.IsLittleEndian)
                    {
                        u16 = BinaryPrimitives.ReverseEndianness(u16);
                    }

                    pos = p + 2;
                    return (ulong)(u16 >> 2);
                }
            }
            else if ((b0 & 4) != 0)
            {
                if (len - p >= 4)
                {
                    uint u32 = Unsafe.ReadUnaligned<uint>(ref r);
                    if (!BitConverter.IsLittleEndian)
                    {
                        u32 = BinaryPrimitives.ReverseEndianness(u32);
                    }

                    pos = p + 4;
                    return (ulong)(u32 >> 3);
                }
            }
            else if (b0 == 0 && len - p >= 9)
            {
                ulong u64 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, 1));
                if (!BitConverter.IsLittleEndian)
                {
                    u64 = BinaryPrimitives.ReverseEndianness(u64);
                }

                pos = p + 9;
                return u64;
            }
        }

        ThrowInvalidVarint(p);
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MatchTag1(ReadOnlySpan<byte> data, int pos, byte encodedTag) =>
        (uint)pos < (uint)data.Length && Unsafe.Add(ref MemoryMarshal.GetReference(data), pos) == encodedTag;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MatchTag2(ReadOnlySpan<byte> data, int pos, ushort encodedTagLE)
    {
        if ((uint)pos > (uint)data.Length || data.Length - pos < 2)
        {
            return false;
        }

        ushort v = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), pos));
        if (!BitConverter.IsLittleEndian)
        {
            v = BinaryPrimitives.ReverseEndianness(v);
        }

        return v == encodedTagLE;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowMaxDepthExceeded(int depth, int maxDepth) =>
        throw new OmniBufException($"omnibuf: nesting depth {depth} exceeds maximum {maxDepth}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowInvalidArrayHeader(string fieldDesc) =>
        throw new OmniBufException($"omnibuf: invalid WireArray header on {fieldDesc}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowInvalidMapHeader(string fieldDesc) =>
        throw new OmniBufException($"omnibuf: invalid WireMap header on {fieldDesc}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowTrailingArrayBytes() =>
        throw new OmniBufException("omnibuf: trailing bytes in WireArray");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowTrailingMapBytes() =>
        throw new OmniBufException("omnibuf: trailing bytes in WireMap");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (uint FieldNum, OmniBufWireType WireType) ReadTag(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong tag = ReadVarint(data, ref pos);
        ulong fieldNum64 = tag >> 4;
        if (fieldNum64 > OmniBufConstants.MaxFieldNumber)
        {
            ThrowFieldNumExceedsMax(fieldNum64);
        }

        uint fieldNum = (uint)fieldNum64;
        var wt = (OmniBufWireType)(tag & 0x0Fu);
        if (fieldNum == 0 && wt != OmniBufWireType.SubMsg)
        {
            ThrowInvalidField0WireType(wt);
        }

        return (fieldNum, wt);
    }

    /// <summary>
    /// Validates and skips an unknown field, mismatched-wire-type field, or Field #0 SchemaMeta tag
    /// after switching directly on the raw tag varint.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SkipUnknownOrField0Tag(
        ReadOnlySpan<byte> data,
        ref int pos,
        ulong tag,
        int depth,
        int maxDepth)
    {
        ulong fieldNum64 = tag >> 4;
        if (fieldNum64 > OmniBufConstants.MaxFieldNumber)
        {
            ThrowFieldNumExceedsMax(fieldNum64);
        }

        var wt = (OmniBufWireType)(tag & 0x0Fu);
        if (fieldNum64 == 0)
        {
            if (wt != OmniBufWireType.SubMsg)
            {
                ThrowInvalidField0WireType(wt);
            }

            SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
            return;
        }

        ValidateValue(data, ref pos, wt, depth, maxDepth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> ReadLenSlice(ReadOnlySpan<byte> data, scoped ref int pos, int minBodyLen = 0)
    {
        int p = pos;
        int totalLen = data.Length;
        ulong byteLen;
        if ((uint)p < (uint)totalLen)
        {
            ref byte r = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), p);
            byte b0 = r;
            if ((b0 & 1) != 0)
            {
                byteLen = (ulong)(b0 >> 1);
                p++;
            }
            else if ((b0 & 2) != 0 && totalLen - p >= 2)
            {
                ushort u16 = Unsafe.ReadUnaligned<ushort>(ref r);
                if (!BitConverter.IsLittleEndian)
                {
                    u16 = BinaryPrimitives.ReverseEndianness(u16);
                }

                byteLen = (ulong)(u16 >> 2);
                p += 2;
            }
            else
            {
                byteLen = ReadVarintSlow(data, ref p);
            }
        }
        else
        {
            ThrowInvalidVarint(p);
            return default;
        }

        int remaining = totalLen - p;
        if (byteLen < (ulong)minBodyLen || byteLen > (ulong)remaining)
        {
            ThrowLenOutOfBounds(byteLen, p);
        }

        int len = (int)byteLen;
        pos = p + len;
        return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), p), len);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ReadBoolElement(ReadOnlySpan<byte> data, ref int pos)
    {
        int p = pos;
        if ((uint)p >= (uint)data.Length)
        {
            ThrowTruncatedBool(p);
        }

        byte b = Unsafe.Add(ref MemoryMarshal.GetReference(data), p);
        pos = p + 1;
        if (b > 1)
        {
            ThrowInvalidBoolPadding(b, p);
        }

        return b == 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadFixed32(ReadOnlySpan<byte> data, ref int pos)
    {
        int p = pos;
        if ((uint)p > (uint)data.Length || data.Length - p < 4)
        {
            ThrowTruncatedFixed32(p);
        }

        uint v = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), p));
        if (!BitConverter.IsLittleEndian)
        {
            v = BinaryPrimitives.ReverseEndianness(v);
        }

        pos = p + 4;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadFixed64(ReadOnlySpan<byte> data, ref int pos)
    {
        int p = pos;
        if ((uint)p > (uint)data.Length || data.Length - p < 8)
        {
            ThrowTruncatedFixed64(p);
        }

        ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), p));
        if (!BitConverter.IsLittleEndian)
        {
            v = BinaryPrimitives.ReverseEndianness(v);
        }

        pos = p + 8;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        ReadOnlySpan<byte> strBytes = ReadLenSlice(data, ref pos, 0);
        return OmniBufConstants.DecodeUtf8Strict(strBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ReadString(ReadOnlySpan<byte> data, ref int pos, char[] scratch)
    {
        ReadOnlySpan<byte> strBytes = ReadLenSlice(data, ref pos, 0);
        return OmniBufConstants.DecodeUtf8Strict(strBytes, scratch);
    }

    public static void ReadStringArray(
        ReadOnlySpan<byte> body,
        ref int pos,
        int count,
        Google.Protobuf.Collections.RepeatedField<string> target,
        char[] scratch)
    {
        int targetCap = target.Count + (count <= 4096 ? count : 4096);
        if (target.Capacity < targetCap)
        {
            target.Capacity = targetCap;
        }

        int p = pos;
        int totalLen = body.Length;
        ref byte start = ref MemoryMarshal.GetReference(body);
        for (int i = 0; i < count; i++)
        {
            ulong byteLen;
            if ((uint)p < (uint)totalLen)
            {
                byte b0 = Unsafe.Add(ref start, p);
                if ((b0 & 1) != 0)
                {
                    byteLen = (ulong)(b0 >> 1);
                    p++;
                }
                else
                {
                    byteLen = ReadVarintSlow(body, ref p);
                }
            }
            else
            {
                ThrowInvalidVarint(p);
                return;
            }

            if (byteLen > (ulong)(totalLen - p))
            {
                ThrowLenOutOfBounds(byteLen, p);
            }

            int len = (int)byteLen;
            ReadOnlySpan<byte> strBytes = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref start, p), len);
            p += len;
            target.Add(OmniBufConstants.DecodeUtf8Strict(strBytes, scratch));
        }

        pos = p;
    }

    public static void ReadUint32Array(
        ReadOnlySpan<byte> body,
        ref int pos,
        int count,
        Google.Protobuf.Collections.RepeatedField<uint> target)
    {
        int targetCap = target.Count + (count <= 4096 ? count : 4096);
        if (target.Capacity < targetCap)
        {
            target.Capacity = targetCap;
        }

        int p = pos;
        int totalLen = body.Length;
        ref byte start = ref MemoryMarshal.GetReference(body);
        for (int i = 0; i < count; i++)
        {
            if ((uint)p >= (uint)totalLen)
            {
                ThrowInvalidVarint(p);
            }

            ref byte r = ref Unsafe.Add(ref start, p);
            byte b0 = r;
            ulong v;
            if ((b0 & 1) != 0)
            {
                v = (ulong)(b0 >> 1);
                p += 1;
            }
            else if ((b0 & 2) != 0 && totalLen - p >= 2)
            {
                ushort u16 = Unsafe.ReadUnaligned<ushort>(ref r);
                if (!BitConverter.IsLittleEndian) u16 = BinaryPrimitives.ReverseEndianness(u16);
                v = (ulong)(u16 >> 2);
                p += 2;
            }
            else if ((b0 & 4) != 0 && totalLen - p >= 4)
            {
                uint u32 = Unsafe.ReadUnaligned<uint>(ref r);
                if (!BitConverter.IsLittleEndian) u32 = BinaryPrimitives.ReverseEndianness(u32);
                v = (ulong)(u32 >> 3);
                p += 4;
            }
            else if (b0 == 0 && totalLen - p >= 9)
            {
                ulong u64 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, 1));
                if (!BitConverter.IsLittleEndian) u64 = BinaryPrimitives.ReverseEndianness(u64);
                v = u64;
                p += 9;
            }
            else
            {
                ThrowInvalidVarint(p);
                return;
            }

            target.Add((uint)v);
        }

        pos = p;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidVarint(int pos) =>
        throw new OmniBufException($"omnibuf: invalid or truncated Prefix-Varint at byte {pos}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFieldNumExceedsMax(ulong fieldNum64) =>
        throw new OmniBufException($"omnibuf: field number {fieldNum64} exceeds maximum {OmniBufConstants.MaxFieldNumber}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidField0WireType(OmniBufWireType wt) =>
        throw new OmniBufException($"omnibuf: Field #0 requires SubMsg wire type, got {wt}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLenOutOfBounds(ulong byteLen, int pos) =>
        throw new OmniBufException($"omnibuf: length-prefixed payload ({byteLen}B) out of bounds at {pos}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTruncatedBool(int pos) =>
        throw new OmniBufException($"omnibuf: truncated bool element at {pos}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidBoolPadding(byte b, int pos) =>
        throw new OmniBufException($"omnibuf: invalid bool padding bits (0x{b:x2}) at {pos}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTruncatedFixed32(int pos) =>
        throw new OmniBufException($"omnibuf: truncated 32-bit fixed value at {pos}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTruncatedFixed64(int pos) =>
        throw new OmniBufException($"omnibuf: truncated 64-bit fixed value at {pos}");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParseTypedArrayHeader(
        ReadOnlySpan<byte> body,
        OmniBufWireType expectedElemType,
        out int count,
        out int payloadOffset)
    {
        int bodyLen = body.Length;
        if (bodyLen < 2)
        {
            count = 0;
            payloadOffset = 0;
            return false;
        }

        ref byte r = ref MemoryMarshal.GetReference(body);
        if ((OmniBufWireType)r != expectedElemType)
        {
            count = 0;
            payloadOffset = 0;
            return false;
        }

        byte b1 = Unsafe.Add(ref r, 1);
        ulong countU;
        int cn;
        if ((b1 & 1) != 0)
        {
            countU = (ulong)(b1 >> 1);
            cn = 1;
        }
        else if (!TryDecodeVarint(body.Slice(1), out countU, out cn))
        {
            count = 0;
            payloadOffset = 0;
            return false;
        }

        payloadOffset = 1 + cn;
        int remaining = bodyLen - payloadOffset;
        if (remaining < 0)
        {
            count = 0;
            return false;
        }

        int width = OmniBufConstants.FixedElementWidth(expectedElemType);
        if (width == 1)
        {
            if (countU != (ulong)remaining)
            {
                count = 0;
                return false;
            }
        }
        else if (width == 4)
        {
            if ((remaining & 3) != 0 || countU != (ulong)(remaining >> 2))
            {
                count = 0;
                return false;
            }
        }
        else if (width == 8)
        {
            if ((remaining & 7) != 0 || countU != (ulong)(remaining >> 3))
            {
                count = 0;
                return false;
            }
        }
        else
        {
            if (countU == 0)
            {
                if (remaining != 0)
                {
                    count = 0;
                    return false;
                }
            }
            else if (countU > (ulong)remaining)
            {
                count = 0;
                return false;
            }
        }

        count = (int)countU;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParseTypedMapHeader(
        ReadOnlySpan<byte> body,
        OmniBufWireType expectedKeyType,
        OmniBufWireType expectedValType,
        out int count,
        out int payloadOffset)
    {
        int bodyLen = body.Length;
        if (bodyLen < 3)
        {
            count = 0;
            payloadOffset = 0;
            return false;
        }

        ref byte r = ref MemoryMarshal.GetReference(body);
        if ((OmniBufWireType)r != expectedKeyType || (OmniBufWireType)Unsafe.Add(ref r, 1) != expectedValType)
        {
            count = 0;
            payloadOffset = 0;
            return false;
        }

        byte b2 = Unsafe.Add(ref r, 2);
        ulong countU;
        int cn;
        if ((b2 & 1) != 0)
        {
            countU = (ulong)(b2 >> 1);
            cn = 1;
        }
        else if (!TryDecodeVarint(body.Slice(2), out countU, out cn))
        {
            count = 0;
            payloadOffset = 0;
            return false;
        }

        payloadOffset = 2 + cn;
        int remaining = bodyLen - payloadOffset;
        if (remaining < 0)
        {
            count = 0;
            return false;
        }

        if (countU == 0)
        {
            if (remaining != 0)
            {
                count = 0;
                return false;
            }
        }
        else if (countU > (ulong)(remaining >> 1))
        {
            count = 0;
            return false;
        }

        count = (int)countU;
        return true;
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
                    int p = pos;
                    if ((uint)p >= (uint)n)
                    {
                        return false;
                    }

                    byte b0 = Unsafe.Add(ref MemoryMarshal.GetReference(data), p);
                    if ((b0 & 1) != 0)
                    {
                        pos = p + 1;
                        return true;
                    }

                    if ((b0 & 2) != 0)
                    {
                        if (n - p < 2) return false;
                        pos = p + 2;
                        return true;
                    }

                    if ((b0 & 4) != 0)
                    {
                        if (n - p < 4) return false;
                        pos = p + 4;
                        return true;
                    }

                    if (b0 != 0 || n - p < 9)
                    {
                        return false;
                    }

                    pos = p + 9;
                    return true;
                }

            case OmniBufWireType.BoolFalse:
            case OmniBufWireType.BoolTrue:
                return (uint)pos <= (uint)n;

            case OmniBufWireType.Fixed32:
            case OmniBufWireType.Sfixed32:
            case OmniBufWireType.Float32:
                if ((uint)pos > (uint)n || n - pos < 4)
                {
                    return false;
                }

                pos += 4;
                return true;

            case OmniBufWireType.Fixed64:
            case OmniBufWireType.Sfixed64:
            case OmniBufWireType.Float64:
                if ((uint)pos > (uint)n || n - pos < 8)
                {
                    return false;
                }

                pos += 8;
                return true;

            case OmniBufWireType.String:
            case OmniBufWireType.Bytes:
            case OmniBufWireType.SubMsg:
                {
                    int p = pos;
                    if ((uint)p >= (uint)n)
                    {
                        return false;
                    }

                    byte b0 = Unsafe.Add(ref MemoryMarshal.GetReference(data), p);
                    ulong len;
                    if ((b0 & 1) != 0)
                    {
                        len = (ulong)(b0 >> 1);
                        p++;
                    }
                    else
                    {
                        if (!TryDecodeVarint(data.Slice(p), out len, out int ln))
                        {
                            return false;
                        }

                        p += ln;
                    }

                    if (len > (ulong)(n - p))
                    {
                        return false;
                    }

                    pos = p + (int)len;
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
