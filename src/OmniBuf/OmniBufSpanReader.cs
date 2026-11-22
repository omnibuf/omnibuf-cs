using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OmniBuf;

/// <summary>
/// Callback interface implemented by generated value-type decoder adapters for single-pin fast decoding.
/// </summary>
public interface IOmniBufFreshDecoder<out T>
{
    T Decode(OmniBufSpanReader reader);
}

/// <summary>
/// Pinned pointer cursor for fast sequential decoding in generated codecs.
/// Exposes a safe public API (no unsafe code required in generated files) while pinning the
/// packet once at the top-level parse boundary.
/// </summary>
[SkipLocalsInit]
public unsafe ref struct OmniBufSpanReader
{
    private byte* _cur;
    private byte* _end;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal OmniBufSpanReader(byte* start, int length)
    {
        _cur = start;
        _end = start + length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ParsePinned<T, TDecoder>(ReadOnlySpan<byte> data)
        where TDecoder : struct, IOmniBufFreshDecoder<T>
    {
        fixed (byte* start = &MemoryMarshal.GetReference(data))
        {
            return default(TDecoder).Decode(new OmniBufSpanReader(start, data.Length));
        }
    }

    public readonly bool IsAtEnd
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _cur >= _end;
    }

    public readonly int Remaining
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)(_end - _cur);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<byte> RemainingSpan() =>
        new(_cur, (int)(_end - _cur));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchTag1(byte encodedTag)
    {
        byte* cur = _cur;
        if (cur < _end && *cur == encodedTag)
        {
            _cur = cur + 1;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchTag2(ushort encodedTagLE)
    {
        byte* cur = _cur;
        if (_end - cur >= 2)
        {
            ushort v = Unsafe.ReadUnaligned<ushort>(cur);
            if (!BitConverter.IsLittleEndian)
            {
                v = BinaryPrimitives.ReverseEndianness(v);
            }

            if (v == encodedTagLE)
            {
                _cur = cur + 2;
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadVarint()
    {
        byte* cur = _cur;
        long rem = _end - cur;
        if (rem > 0)
        {
            byte b0 = *cur;
            if ((b0 & 1) != 0)
            {
                _cur = cur + 1;
                return (ulong)(b0 >> 1);
            }

            if ((b0 & 2) != 0)
            {
                if (rem >= 2)
                {
                    ushort u16 = Unsafe.ReadUnaligned<ushort>(cur);
                    if (!BitConverter.IsLittleEndian)
                    {
                        u16 = BinaryPrimitives.ReverseEndianness(u16);
                    }

                    _cur = cur + 2;
                    return (ulong)(u16 >> 2);
                }
            }
            else if ((b0 & 4) != 0)
            {
                if (rem >= 4)
                {
                    uint u32 = Unsafe.ReadUnaligned<uint>(cur);
                    if (!BitConverter.IsLittleEndian)
                    {
                        u32 = BinaryPrimitives.ReverseEndianness(u32);
                    }

                    _cur = cur + 4;
                    return (ulong)(u32 >> 3);
                }
            }
            else if (b0 == 0 && rem >= 9)
            {
                ulong u64 = Unsafe.ReadUnaligned<ulong>(cur + 1);
                if (!BitConverter.IsLittleEndian)
                {
                    u64 = BinaryPrimitives.ReverseEndianness(u64);
                }

                _cur = cur + 9;
                return u64;
            }
        }

        ThrowInvalidVarint();
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong ReadVarintSlow(byte* cur, long rem, out int consumed)
    {
        byte b0 = *cur;
        if ((b0 & 4) != 0 && rem >= 4)
        {
            uint u32 = Unsafe.ReadUnaligned<uint>(cur);
            if (!BitConverter.IsLittleEndian)
            {
                u32 = BinaryPrimitives.ReverseEndianness(u32);
            }

            consumed = 4;
            return (ulong)(u32 >> 3);
        }

        if (b0 == 0 && rem >= 9)
        {
            ulong u64 = Unsafe.ReadUnaligned<ulong>(cur + 1);
            if (!BitConverter.IsLittleEndian)
            {
                u64 = BinaryPrimitives.ReverseEndianness(u64);
            }

            consumed = 9;
            return u64;
        }

        ThrowInvalidVarint();
        consumed = 0;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadFixed32()
    {
        byte* cur = _cur;
        if (_end - cur < 4)
        {
            ThrowTruncatedFixed32();
        }

        uint v = Unsafe.ReadUnaligned<uint>(cur);
        if (!BitConverter.IsLittleEndian)
        {
            v = BinaryPrimitives.ReverseEndianness(v);
        }

        _cur = cur + 4;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadFixed64()
    {
        byte* cur = _cur;
        if (_end - cur < 8)
        {
            ThrowTruncatedFixed64();
        }

        ulong v = Unsafe.ReadUnaligned<ulong>(cur);
        if (!BitConverter.IsLittleEndian)
        {
            v = BinaryPrimitives.ReverseEndianness(v);
        }

        _cur = cur + 8;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBoolElement()
    {
        byte* cur = _cur;
        if (cur >= _end)
        {
            ThrowTruncatedBool();
        }

        byte b = *cur;
        _cur = cur + 1;
        if (b > 1)
        {
            ThrowInvalidBoolPadding(b);
        }

        return b == 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string ReadString()
    {
        byte* cur = _cur;
        long rem = _end - cur;
        ulong byteLen;
        if (rem > 0)
        {
            byte b0 = *cur;
            if ((b0 & 1) != 0)
            {
                byteLen = (ulong)(b0 >> 1);
                cur += 1;
                rem -= 1;
            }
            else if ((b0 & 2) != 0 && rem >= 2)
            {
                ushort u16 = Unsafe.ReadUnaligned<ushort>(cur);
                if (!BitConverter.IsLittleEndian)
                {
                    u16 = BinaryPrimitives.ReverseEndianness(u16);
                }

                byteLen = (ulong)(u16 >> 2);
                cur += 2;
                rem -= 2;
            }
            else
            {
                byteLen = ReadVarintSlow(cur, rem, out int consumed);
                cur += consumed;
                rem -= consumed;
            }
        }
        else
        {
            ThrowInvalidVarint();
            return string.Empty;
        }

        if (byteLen > (ulong)rem)
        {
            ThrowLenOutOfBounds(byteLen);
        }

        int len = (int)byteLen;
        _cur = cur + len;
        if (len == 0)
        {
            return string.Empty;
        }

        return OmniBufConstants.DecodeUtf8StrictPinned(cur, len);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadLenSpan(int minBodyLen = 0)
    {
        byte* cur = _cur;
        long rem = _end - cur;
        ulong byteLen;
        if (rem > 0)
        {
            byte b0 = *cur;
            if ((b0 & 1) != 0)
            {
                byteLen = (ulong)(b0 >> 1);
                cur += 1;
                rem -= 1;
            }
            else if ((b0 & 2) != 0 && rem >= 2)
            {
                ushort u16 = Unsafe.ReadUnaligned<ushort>(cur);
                if (!BitConverter.IsLittleEndian)
                {
                    u16 = BinaryPrimitives.ReverseEndianness(u16);
                }

                byteLen = (ulong)(u16 >> 2);
                cur += 2;
                rem -= 2;
            }
            else
            {
                byteLen = ReadVarintSlow(cur, rem, out int consumed);
                cur += consumed;
                rem -= consumed;
            }
        }
        else
        {
            ThrowInvalidVarint();
            return default;
        }

        if (byteLen < (ulong)minBodyLen || byteLen > (ulong)rem)
        {
            ThrowLenOutOfBounds(byteLen);
        }

        int len = (int)byteLen;
        _cur = cur + len;
        return new ReadOnlySpan<byte>(cur, len);
    }

    /// <summary>
    /// Reads a container length prefix (optimized for 2-byte Prefix-Varint emitted by OmniBufEncoder)
    /// and returns a bounded child reader while advancing this reader past the container.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OmniBufSpanReader ReadContainer(int minBodyLen = 0)
    {
        byte* cur = _cur;
        long rem = _end - cur;
        ulong byteLen;
        if (rem > 0)
        {
            byte b0 = *cur;
            if ((b0 & 3) == 2 && rem >= 2)
            {
                ushort u16 = Unsafe.ReadUnaligned<ushort>(cur);
                if (!BitConverter.IsLittleEndian)
                {
                    u16 = BinaryPrimitives.ReverseEndianness(u16);
                }

                byteLen = (ulong)(u16 >> 2);
                cur += 2;
                rem -= 2;
            }
            else if ((b0 & 1) != 0)
            {
                byteLen = (ulong)(b0 >> 1);
                cur += 1;
                rem -= 1;
            }
            else
            {
                byteLen = ReadVarintSlow(cur, rem, out int consumed);
                cur += consumed;
                rem -= consumed;
            }
        }
        else
        {
            ThrowInvalidVarint();
            return default;
        }

        if (byteLen < (ulong)minBodyLen || byteLen > (ulong)rem)
        {
            ThrowLenOutOfBounds(byteLen);
        }

        int len = (int)byteLen;
        _cur = cur + len;
        return new OmniBufSpanReader(cur, len);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryParseTypedArrayHeader(OmniBufWireType expectedElemType, out int count)
    {
        byte* cur = _cur;
        long rem = _end - cur;
        if (rem < 2 || (OmniBufWireType)(*cur) != expectedElemType)
        {
            count = 0;
            return false;
        }

        byte b1 = cur[1];
        ulong countU;
        if ((b1 & 1) != 0)
        {
            countU = (ulong)(b1 >> 1);
            cur += 2;
            rem -= 2;
        }
        else
        {
            cur += 1;
            rem -= 1;
            if (!TryReadVarintCount(cur, rem, out countU, out int consumed))
            {
                count = 0;
                return false;
            }

            cur += consumed;
            rem -= consumed;
        }

        _cur = cur;
        int width = OmniBufConstants.FixedElementWidth(expectedElemType);
        if (width == 1)
        {
            if (countU != (ulong)rem)
            {
                count = 0;
                return false;
            }
        }
        else if (width == 4)
        {
            if ((rem & 3) != 0 || countU != (ulong)(rem >> 2))
            {
                count = 0;
                return false;
            }
        }
        else if (width == 8)
        {
            if ((rem & 7) != 0 || countU != (ulong)(rem >> 3))
            {
                count = 0;
                return false;
            }
        }
        else
        {
            if (countU == 0)
            {
                if (rem != 0)
                {
                    count = 0;
                    return false;
                }
            }
            else if (countU > (ulong)rem)
            {
                count = 0;
                return false;
            }
        }

        count = (int)countU;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryReadVarintCount(byte* cur, long rem, out ulong value, out int consumed)
    {
        if (rem <= 0)
        {
            value = 0;
            consumed = 0;
            return false;
        }

        byte b0 = *cur;
        if ((b0 & 2) != 0 && rem >= 2)
        {
            ushort u16 = Unsafe.ReadUnaligned<ushort>(cur);
            if (!BitConverter.IsLittleEndian) u16 = BinaryPrimitives.ReverseEndianness(u16);
            consumed = 2;
            value = (ulong)(u16 >> 2);
            return true;
        }

        if ((b0 & 4) != 0 && rem >= 4)
        {
            uint u32 = Unsafe.ReadUnaligned<uint>(cur);
            if (!BitConverter.IsLittleEndian) u32 = BinaryPrimitives.ReverseEndianness(u32);
            consumed = 4;
            value = (ulong)(u32 >> 3);
            return true;
        }

        if (b0 == 0 && rem >= 9)
        {
            ulong u64 = Unsafe.ReadUnaligned<ulong>(cur + 1);
            if (!BitConverter.IsLittleEndian) u64 = BinaryPrimitives.ReverseEndianness(u64);
            consumed = 9;
            value = u64;
            return true;
        }

        value = 0;
        consumed = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadStringArray(int count, Google.Protobuf.Collections.RepeatedField<string> target)
    {
        _cur = ReadStringArrayCore(_cur, _end, count, target);
    }

    private static byte* ReadStringArrayCore(
        byte* cur,
        byte* end,
        int count,
        Google.Protobuf.Collections.RepeatedField<string> target)
    {
        int targetCap = target.Count + (count <= 4096 ? count : 4096);
        if (target.Capacity < targetCap)
        {
            target.Capacity = targetCap;
        }

        for (int i = 0; i < count; i++)
        {
            long rem = end - cur;
            if (rem <= 0)
            {
                ThrowInvalidVarint();
            }

            byte b0 = *cur;
            ulong byteLen;
            if ((b0 & 1) != 0)
            {
                byteLen = (ulong)(b0 >> 1);
                cur += 1;
                rem -= 1;
            }
            else if ((b0 & 2) != 0 && rem >= 2)
            {
                ushort u16 = Unsafe.ReadUnaligned<ushort>(cur);
                if (!BitConverter.IsLittleEndian) u16 = BinaryPrimitives.ReverseEndianness(u16);
                byteLen = (ulong)(u16 >> 2);
                cur += 2;
                rem -= 2;
            }
            else
            {
                byteLen = ReadVarintSlow(cur, rem, out int consumed);
                cur += consumed;
                rem -= consumed;
            }

            if (byteLen > (ulong)rem)
            {
                ThrowLenOutOfBounds(byteLen);
            }

            int len = (int)byteLen;
            if (len == 0)
            {
                target.Add(string.Empty);
            }
            else
            {
                byte* strPtr = cur;
                cur += len;
                target.Add(OmniBufConstants.DecodeUtf8StrictPinned(strPtr, len));
            }
        }

        return cur;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadUint32Array(int count, Google.Protobuf.Collections.RepeatedField<uint> target)
    {
        _cur = ReadUint32ArrayCore(_cur, _end, count, target);
    }

    private static byte* ReadUint32ArrayCore(
        byte* cur,
        byte* end,
        int count,
        Google.Protobuf.Collections.RepeatedField<uint> target)
    {
        int targetCap = target.Count + (count <= 4096 ? count : 4096);
        if (target.Capacity < targetCap)
        {
            target.Capacity = targetCap;
        }

        for (int i = 0; i < count; i++)
        {
            long rem = end - cur;
            if (rem <= 0)
            {
                ThrowInvalidVarint();
            }

            byte b0 = *cur;
            if (BitConverter.IsLittleEndian && rem >= 4)
            {
                int tz = BitOperations.TrailingZeroCount((uint)b0 | 0x100u);
                if (tz < 3)
                {
                    uint raw = Unsafe.ReadUnaligned<uint>(cur);
                    int shiftLeft = 32 - (8 << tz);
                    uint val = (raw << shiftLeft) >> (shiftLeft + tz + 1);
                    cur += 1 << tz;
                    target.Add(val);
                    continue;
                }
            }

            ulong v;
            if ((b0 & 1) != 0)
            {
                v = (ulong)(b0 >> 1);
                cur += 1;
            }
            else if ((b0 & 2) != 0 && rem >= 2)
            {
                ushort u16 = Unsafe.ReadUnaligned<ushort>(cur);
                if (!BitConverter.IsLittleEndian) u16 = BinaryPrimitives.ReverseEndianness(u16);
                v = (ulong)(u16 >> 2);
                cur += 2;
            }
            else if ((b0 & 4) != 0 && rem >= 4)
            {
                uint u32 = Unsafe.ReadUnaligned<uint>(cur);
                if (!BitConverter.IsLittleEndian) u32 = BinaryPrimitives.ReverseEndianness(u32);
                v = (ulong)(u32 >> 3);
                cur += 4;
            }
            else if (b0 == 0 && rem >= 9)
            {
                ulong u64 = Unsafe.ReadUnaligned<ulong>(cur + 1);
                if (!BitConverter.IsLittleEndian) u64 = BinaryPrimitives.ReverseEndianness(u64);
                v = u64;
                cur += 9;
            }
            else
            {
                ThrowInvalidVarint();
                return cur;
            }

            target.Add((uint)v);
        }

        return cur;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidVarint() =>
        throw new OmniBufException("omnibuf: invalid or truncated Prefix-Varint");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLenOutOfBounds(ulong byteLen) =>
        throw new OmniBufException($"omnibuf: length-prefixed payload ({byteLen}B) out of bounds");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTruncatedFixed32() =>
        throw new OmniBufException("omnibuf: truncated 32-bit fixed value");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTruncatedFixed64() =>
        throw new OmniBufException("omnibuf: truncated 64-bit fixed value");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTruncatedBool() =>
        throw new OmniBufException("omnibuf: truncated bool element");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidBoolPadding(byte b) =>
        throw new OmniBufException($"omnibuf: invalid bool padding bits (0x{b:x2})");
}
