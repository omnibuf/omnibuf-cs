using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Text.Unicode;
using Google.Protobuf.Collections;

namespace OmniBuf;

/// <summary>
/// Single-pass forward binary encoder for OmniBuf packets with dynamic container stack growth.
/// </summary>
public sealed class OmniBufEncoder
{
    private const int InitialBufferCapacity = 512;
    private const int InitialStackCapacity = 64;
    internal const int MaxRetainedBufferCapacity = 65_536;
    private const int MaxRetainedStackCapacity = 256;

    [ThreadStatic]
    private static OmniBufEncoder? t_shared;

    private byte[] _buf;
    private int _pos;
    private int[] _containerStack;
    private int _depth;
    private readonly int _maxDepth;
    private bool _inUse;

    public static OmniBufEncoder RentThreadLocal(out bool rented)
    {
        OmniBufEncoder enc = t_shared ??= new OmniBufEncoder(InitialBufferCapacity);
        if (!enc._inUse)
        {
            enc._inUse = true;
            enc.Reset();
            rented = true;
            return enc;
        }

        rented = false;
        return new OmniBufEncoder(InitialBufferCapacity);
    }

    public static void ReturnThreadLocal(OmniBufEncoder encoder, bool rented)
    {
        if (rented && encoder is not null)
        {
            try
            {
                encoder.Reset();
                if (encoder._buf.Length > MaxRetainedBufferCapacity)
                {
                    encoder._buf = new byte[InitialBufferCapacity];
                }

                if (encoder._containerStack.Length > MaxRetainedStackCapacity)
                {
                    encoder._containerStack = new int[InitialStackCapacity];
                }
            }
            finally
            {
                encoder._inUse = false;
            }
        }
    }

    public OmniBufEncoder(int initialCapacity = InitialBufferCapacity, int maxDepth = OmniBufConstants.MaxNestingDepth)
    {
        if (initialCapacity < 16)
        {
            initialCapacity = 16;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);

        _buf = new byte[initialCapacity];
        _containerStack = new int[InitialStackCapacity];
        _maxDepth = maxDepth;
    }

    public int Length => _pos;

    public int Capacity => _buf.Length;

    public int ContainerDepth => _depth;

    public int MaxDepth => _maxDepth;

    public ReadOnlySpan<byte> WrittenSpan => _buf.AsSpan(0, _pos);

    public void Reset()
    {
        _pos = 0;
        _depth = 0;
    }

    public byte[] ToArray()
    {
        if (_depth != 0)
        {
            throw new OmniBufException($"omnibuf: unclosed container scopes remaining ({_depth})");
        }

        if (_pos == 0)
        {
            return Array.Empty<byte>();
        }

        var result = new byte[_pos];
        Buffer.BlockCopy(_buf, 0, result, 0, _pos);
        return result;
    }

    public void CopyTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (_depth != 0)
        {
            throw new OmniBufException($"omnibuf: unclosed container scopes remaining ({_depth})");
        }

        if (_pos == 0)
        {
            return;
        }

        Span<byte> dest = writer.GetSpan(_pos);
        _buf.AsSpan(0, _pos).CopyTo(dest);
        writer.Advance(_pos);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureSpace(int needed)
    {
        if ((uint)(_buf.Length - _pos) < (uint)needed)
        {
            GrowBuffer(needed < 0 ? -1L : (long)_pos + needed);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowBuffer(long minCapacity)
    {
        if (minCapacity < 0 || minCapacity > Array.MaxLength)
        {
            throw new OmniBufException("omnibuf: encoder buffer exceeds maximum array length");
        }

        long next = Math.Max((long)_buf.Length * 2, minCapacity);
        if (next > Array.MaxLength)
        {
            next = Array.MaxLength;
        }

        var nextBuf = new byte[(int)next];
        Buffer.BlockCopy(_buf, 0, nextBuf, 0, _pos);
        _buf = nextBuf;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteRawByte(byte b)
    {
        EnsureSpace(1);
        _buf[_pos++] = b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteRawBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        EnsureSpace(bytes.Length);
        bytes.CopyTo(_buf.AsSpan(_pos));
        _pos += bytes.Length;
    }

    /// <summary>
    /// Encodes a 64-bit unsigned integer using TZCNT Prefix-Varint encoding (1, 2, 4, or 9 bytes).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteVarint(ulong v)
    {
        if (v <= 127)
        {
            EnsureSpace(1);
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos++) = (byte)((v << 1) | 1);
        }
        else if (v <= 16_383)
        {
            EnsureSpace(2);
            ushort enc = (ushort)((v << 2) | 2);
            if (!BitConverter.IsLittleEndian)
            {
                enc = BinaryPrimitives.ReverseEndianness(enc);
            }

            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos), enc);
            _pos += 2;
        }
        else if (v <= 536_870_911)
        {
            EnsureSpace(4);
            uint enc = (uint)((v << 3) | 4);
            if (!BitConverter.IsLittleEndian)
            {
                enc = BinaryPrimitives.ReverseEndianness(enc);
            }

            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos), enc);
            _pos += 4;
        }
        else
        {
            EnsureSpace(9);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            dst = 0;
            ulong enc = BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 1), enc);
            _pos += 9;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTag(uint fieldNum, OmniBufWireType wireType)
    {
        if (fieldNum == 0 || fieldNum > OmniBufConstants.MaxFieldNumber)
        {
            ThrowInvalidFieldNumber(fieldNum);
        }

        ulong tag = ((ulong)fieldNum << 4) | ((byte)wireType & 0x0Fu);
        WriteVarint(tag);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidFieldNumber(uint fieldNum) =>
        throw new OmniBufException($"omnibuf: invalid field number {fieldNum}");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteField0Tag()
    {
        WriteRawByte(OmniBufConstants.Field0SubMsgTag);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUintValue(ulong v) => WriteVarint(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSintValue(long v) => WriteVarint(OmniBufConstants.ZigZagEncode64(v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBoolElement(bool v) => WriteRawByte(v ? (byte)1 : (byte)0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFixed32Value(uint v)
    {
        EnsureSpace(4);
        uint enc = BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos), enc);
        _pos += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSfixed32Value(int v) => WriteFixed32Value((uint)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFloat32Value(float v) => WriteFixed32Value(BitConverter.SingleToUInt32Bits(v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFixed64Value(ulong v)
    {
        EnsureSpace(8);
        ulong enc = BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos), enc);
        _pos += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSfixed64Value(long v) => WriteFixed64Value((ulong)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFloat64Value(double v) => WriteFixed64Value(BitConverter.DoubleToUInt64Bits(v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteStringValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            EnsureSpace(1);
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos++) = 1; // 1B Prefix-Varint for 0
            return;
        }

        int charLen = value.Length;
        if (charLen <= 127)
        {
            EnsureSpace(1 + charLen);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            dst = (byte)((charLen << 1) | 1);
            if (TryNarrowAsciiShort(ref MemoryMarshal.GetReference(value.AsSpan()), ref Unsafe.Add(ref dst, 1), charLen))
            {
                _pos += 1 + charLen;
                return;
            }
        }

        WriteStringValueSlow(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryNarrowAsciiShort(ref char src, ref byte dst, int charLen)
    {
        if (Vector128.IsHardwareAccelerated && charLen >= 8)
        {
            if (charLen <= 16)
            {
                ref ushort s = ref Unsafe.As<char, ushort>(ref src);
                Vector128<ushort> v0 = Vector128.LoadUnsafe(ref s);
                Vector128<ushort> v1 = Vector128.LoadUnsafe(ref s, (nuint)(charLen - 8));
                if (Vector128.GreaterThanAny(v0 | v1, Vector128.Create((ushort)0x007F)))
                {
                    return false;
                }

                Vector128<byte> narrowed = Vector128.Narrow(v0, v1);
                narrowed.GetLower().StoreUnsafe(ref dst);
                narrowed.GetUpper().StoreUnsafe(ref dst, (nuint)(charLen - 8));
                return true;
            }

            return TryNarrowAsciiMedium(ref src, ref dst, charLen);
        }

        if (BitConverter.IsLittleEndian && charLen >= 4)
        {
            ref byte sb = ref Unsafe.As<char, byte>(ref src);
            ulong u0 = Unsafe.ReadUnaligned<ulong>(ref sb);
            int tail = charLen - 4;
            ulong u1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref sb, tail * 2));
            if (((u0 | u1) & 0xFF80FF80FF80FF80UL) != 0)
            {
                return false;
            }

            dst = (byte)u0;
            Unsafe.Add(ref dst, 1) = (byte)(u0 >> 16);
            Unsafe.Add(ref dst, 2) = (byte)(u0 >> 32);
            Unsafe.Add(ref dst, 3) = (byte)(u0 >> 48);
            Unsafe.Add(ref dst, tail) = (byte)u1;
            Unsafe.Add(ref dst, tail + 1) = (byte)(u1 >> 16);
            Unsafe.Add(ref dst, tail + 2) = (byte)(u1 >> 32);
            Unsafe.Add(ref dst, tail + 3) = (byte)(u1 >> 48);
            return true;
        }

        uint orAll = 0;
        for (int i = 0; i < charLen; i++)
        {
            uint c = Unsafe.Add(ref src, i);
            orAll |= c;
            Unsafe.Add(ref dst, i) = (byte)c;
        }

        return (orAll & ~0x7Fu) == 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryNarrowAsciiMedium(ref char src, ref byte dst, int charLen)
    {
        ref ushort s = ref Unsafe.As<char, ushort>(ref src);
        Vector128<ushort> asciiMax = Vector128.Create((ushort)0x007F);
        int i = 0;
        int end16 = charLen - 16;
        while (true)
        {
            Vector128<ushort> v0 = Vector128.LoadUnsafe(ref s, (nuint)i);
            Vector128<ushort> v1 = Vector128.LoadUnsafe(ref s, (nuint)(i + 8));
            if (Vector128.GreaterThanAny(v0 | v1, asciiMax))
            {
                return false;
            }

            Vector128.Narrow(v0, v1).StoreUnsafe(ref dst, (nuint)i);
            if (i == end16)
            {
                return true;
            }

            i += 16;
            if (i > end16)
            {
                i = end16;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteStringValueSlow(string s)
    {
        int charLen = s.Length;
        int headerLen = charLen <= 127 ? 1 : (charLen <= 16_383 ? 2 : (charLen <= 536_870_911 ? 4 : 9));
        long minNeeded = (long)headerLen + charLen;
        if (minNeeded > Array.MaxLength)
        {
            throw new OmniBufException("omnibuf: encoder buffer exceeds maximum array length");
        }

        EnsureSpace((int)minNeeded);
        if (Ascii.FromUtf16(s.AsSpan(), _buf.AsSpan(_pos + headerLen, charLen), out int asciiWritten) == OperationStatus.Done)
        {
            WriteFixedWidthVarintAt(_pos, (ulong)asciiWritten, headerLen);
            _pos += headerLen + asciiWritten;
            return;
        }

        WriteNonAsciiStringTail(s, headerLen, asciiWritten);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteFixedWidthVarintAt(int offset, ulong v, int width)
    {
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), offset);
        if (width == 1)
        {
            dst = (byte)((v << 1) | 1);
        }
        else if (width == 2)
        {
            ushort enc = (ushort)((v << 2) | 2);
            if (!BitConverter.IsLittleEndian)
            {
                enc = BinaryPrimitives.ReverseEndianness(enc);
            }

            Unsafe.WriteUnaligned(ref dst, enc);
        }
        else if (width == 4)
        {
            uint enc = (uint)((v << 3) | 4);
            if (!BitConverter.IsLittleEndian)
            {
                enc = BinaryPrimitives.ReverseEndianness(enc);
            }

            Unsafe.WriteUnaligned(ref dst, enc);
        }
        else
        {
            dst = 0;
            ulong enc = BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 1), enc);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteNonAsciiStringTail(string s, int headerLen, int asciiWritten)
    {
        ReadOnlySpan<char> remChars = s.AsSpan(asciiWritten);
        long worstCaseNeeded = 9L + asciiWritten + (long)remChars.Length * 3;
        byte[] prevBuf = _buf;
        if ((long)_pos + worstCaseNeeded <= Array.MaxLength)
        {
            EnsureSpace((int)worstCaseNeeded);
            if (!ReferenceEquals(_buf, prevBuf) && asciiWritten > 0)
            {
                prevBuf.AsSpan(_pos + headerLen, asciiWritten).CopyTo(_buf.AsSpan(_pos + headerLen, asciiWritten));
            }
        }
        else
        {
            int exactByteCount = OmniBufConstants.GetUtf8ByteCountStrict(s);
            int exactHeaderLen = exactByteCount <= 127 ? 1 : (exactByteCount <= 16_383 ? 2 : (exactByteCount <= 536_870_911 ? 4 : 9));
            long exactTotal = (long)exactHeaderLen + exactByteCount;
            if ((long)_pos + exactTotal > Array.MaxLength)
            {
                throw new OmniBufException("omnibuf: encoder buffer exceeds maximum array length");
            }

            EnsureSpace((int)exactTotal);
            if (asciiWritten > 0 && (!ReferenceEquals(_buf, prevBuf) || exactHeaderLen != headerLen))
            {
                prevBuf.AsSpan(_pos + headerLen, asciiWritten).CopyTo(_buf.AsSpan(_pos + exactHeaderLen, asciiWritten));
            }

            headerLen = exactHeaderLen;
        }

        OperationStatus status = Utf8.FromUtf16(
            remChars,
            _buf.AsSpan(_pos + headerLen + asciiWritten),
            out _,
            out int remWritten,
            replaceInvalidSequences: false,
            isFinalBlock: true);
        if (status != OperationStatus.Done)
        {
            throw new OmniBufException("omnibuf: string contains invalid UTF-16 surrogates");
        }

        int totalBytes = asciiWritten + remWritten;
        int actualHeaderLen = totalBytes <= 127 ? 1 : (totalBytes <= 16_383 ? 2 : (totalBytes <= 536_870_911 ? 4 : 9));
        if (actualHeaderLen != headerLen)
        {
            _buf.AsSpan(_pos + headerLen, totalBytes).CopyTo(_buf.AsSpan(_pos + actualHeaderLen, totalBytes));
        }

        WriteFixedWidthVarintAt(_pos, (ulong)totalBytes, actualHeaderLen);
        _pos += actualHeaderLen + totalBytes;
    }

    public void WriteBytesValue(ReadOnlySpan<byte> value)
    {
        WriteVarint((ulong)value.Length);
        WriteRawBytes(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUintField(uint fieldNum, ulong v)
    {
        WriteTag(fieldNum, OmniBufWireType.Uint);
        WriteVarint(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSintField(uint fieldNum, long v)
    {
        WriteTag(fieldNum, OmniBufWireType.Sint);
        WriteVarint(OmniBufConstants.ZigZagEncode64(v));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBoolField(uint fieldNum, bool v)
    {
        WriteTag(fieldNum, v ? OmniBufWireType.BoolTrue : OmniBufWireType.BoolFalse);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFixed32Field(uint fieldNum, uint v)
    {
        WriteTag(fieldNum, OmniBufWireType.Fixed32);
        WriteFixed32Value(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSfixed32Field(uint fieldNum, int v)
    {
        WriteTag(fieldNum, OmniBufWireType.Sfixed32);
        WriteSfixed32Value(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFloat32Field(uint fieldNum, float v)
    {
        WriteTag(fieldNum, OmniBufWireType.Float32);
        WriteFloat32Value(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFixed64Field(uint fieldNum, ulong v)
    {
        WriteTag(fieldNum, OmniBufWireType.Fixed64);
        WriteFixed64Value(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSfixed64Field(uint fieldNum, long v)
    {
        WriteTag(fieldNum, OmniBufWireType.Sfixed64);
        WriteSfixed64Value(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFloat64Field(uint fieldNum, double v)
    {
        WriteTag(fieldNum, OmniBufWireType.Float64);
        WriteFloat64Value(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteStringField(uint fieldNum, string? value)
    {
        WriteTag(fieldNum, OmniBufWireType.String);
        WriteStringValue(value);
    }

    public void WriteBytesField(uint fieldNum, ReadOnlySpan<byte> value)
    {
        WriteTag(fieldNum, OmniBufWireType.Bytes);
        WriteBytesValue(value);
    }

    public void WriteExtendedField(uint fieldNum, byte extType, ReadOnlySpan<byte> payload)
    {
        WriteTag(fieldNum, OmniBufWireType.Extended);
        WriteVarint((ulong)payload.Length + 1);
        WriteRawByte(extType);
        WriteRawBytes(payload);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTag1(byte encodedTag)
    {
        EnsureSpace(1);
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos++) = encodedTag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTag2(ushort encodedTagLE)
    {
        EnsureSpace(2);
        ushort enc = BitConverter.IsLittleEndian ? encodedTagLE : BinaryPrimitives.ReverseEndianness(encodedTagLE);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos), enc);
        _pos += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBoolFieldTag1(byte encodedFalseTag, bool v)
    {
        EnsureSpace(1);
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos++) = (byte)(encodedFalseTag + (v ? 2 : 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBoolFieldTag2(ushort encodedFalseTagLE, bool v)
    {
        EnsureSpace(2);
        ushort enc = (ushort)(encodedFalseTagLE + (v ? 4 : 0));
        if (!BitConverter.IsLittleEndian)
        {
            enc = BinaryPrimitives.ReverseEndianness(enc);
        }

        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos), enc);
        _pos += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUintFieldTag1(byte encodedTag, ulong v)
    {
        if (v <= 127)
        {
            EnsureSpace(2);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            dst = encodedTag;
            Unsafe.Add(ref dst, 1) = (byte)((v << 1) | 1);
            _pos += 2;
        }
        else if (v <= 16_383)
        {
            EnsureSpace(3);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            dst = encodedTag;
            ushort enc = (ushort)((v << 2) | 2);
            if (!BitConverter.IsLittleEndian)
            {
                enc = BinaryPrimitives.ReverseEndianness(enc);
            }

            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 1), enc);
            _pos += 3;
        }
        else if (v <= 536_870_911)
        {
            EnsureSpace(5);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            dst = encodedTag;
            uint enc = (uint)((v << 3) | 4);
            if (!BitConverter.IsLittleEndian)
            {
                enc = BinaryPrimitives.ReverseEndianness(enc);
            }

            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 1), enc);
            _pos += 5;
        }
        else
        {
            EnsureSpace(10);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            dst = encodedTag;
            Unsafe.Add(ref dst, 1) = 0;
            ulong enc = BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 2), enc);
            _pos += 10;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUintFieldTag2(ushort encodedTagLE, ulong v)
    {
        ushort tagEnc = BitConverter.IsLittleEndian ? encodedTagLE : BinaryPrimitives.ReverseEndianness(encodedTagLE);
        if (v <= 127)
        {
            EnsureSpace(3);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            Unsafe.WriteUnaligned(ref dst, tagEnc);
            Unsafe.Add(ref dst, 2) = (byte)((v << 1) | 1);
            _pos += 3;
        }
        else if (v <= 16_383)
        {
            EnsureSpace(4);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            ushort valEnc = (ushort)((v << 2) | 2);
            if (!BitConverter.IsLittleEndian)
            {
                valEnc = BinaryPrimitives.ReverseEndianness(valEnc);
            }

            Unsafe.WriteUnaligned(ref dst, tagEnc);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 2), valEnc);
            _pos += 4;
        }
        else if (v <= 536_870_911)
        {
            EnsureSpace(6);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            uint valEnc = (uint)((v << 3) | 4);
            if (!BitConverter.IsLittleEndian)
            {
                valEnc = BinaryPrimitives.ReverseEndianness(valEnc);
            }

            Unsafe.WriteUnaligned(ref dst, tagEnc);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 2), valEnc);
            _pos += 6;
        }
        else
        {
            EnsureSpace(11);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            ulong valEnc = BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
            Unsafe.WriteUnaligned(ref dst, tagEnc);
            Unsafe.Add(ref dst, 2) = 0;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 3), valEnc);
            _pos += 11;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSintFieldTag1(byte encodedTag, long v) =>
        WriteUintFieldTag1(encodedTag, OmniBufConstants.ZigZagEncode64(v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSintFieldTag2(ushort encodedTagLE, long v) =>
        WriteUintFieldTag2(encodedTagLE, OmniBufConstants.ZigZagEncode64(v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFixed32FieldTag1(byte encodedTag, uint v)
    {
        EnsureSpace(5);
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
        dst = encodedTag;
        uint enc = BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 1), enc);
        _pos += 5;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFixed32FieldTag2(ushort encodedTagLE, uint v)
    {
        EnsureSpace(6);
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
        ushort tagEnc = BitConverter.IsLittleEndian ? encodedTagLE : BinaryPrimitives.ReverseEndianness(encodedTagLE);
        uint valEnc = BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
        Unsafe.WriteUnaligned(ref dst, tagEnc);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 2), valEnc);
        _pos += 6;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSfixed32FieldTag1(byte encodedTag, int v) => WriteFixed32FieldTag1(encodedTag, (uint)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSfixed32FieldTag2(ushort encodedTagLE, int v) => WriteFixed32FieldTag2(encodedTagLE, (uint)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFloat32FieldTag1(byte encodedTag, float v) => WriteFixed32FieldTag1(encodedTag, BitConverter.SingleToUInt32Bits(v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFloat32FieldTag2(ushort encodedTagLE, float v) => WriteFixed32FieldTag2(encodedTagLE, BitConverter.SingleToUInt32Bits(v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFixed64FieldTag1(byte encodedTag, ulong v)
    {
        EnsureSpace(9);
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
        dst = encodedTag;
        ulong enc = BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 1), enc);
        _pos += 9;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFixed64FieldTag2(ushort encodedTagLE, ulong v)
    {
        EnsureSpace(10);
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
        ushort tagEnc = BitConverter.IsLittleEndian ? encodedTagLE : BinaryPrimitives.ReverseEndianness(encodedTagLE);
        ulong valEnc = BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
        Unsafe.WriteUnaligned(ref dst, tagEnc);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 2), valEnc);
        _pos += 10;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSfixed64FieldTag1(byte encodedTag, long v) => WriteFixed64FieldTag1(encodedTag, (ulong)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSfixed64FieldTag2(ushort encodedTagLE, long v) => WriteFixed64FieldTag2(encodedTagLE, (ulong)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFloat64FieldTag1(byte encodedTag, double v) => WriteFixed64FieldTag1(encodedTag, BitConverter.DoubleToUInt64Bits(v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFloat64FieldTag2(ushort encodedTagLE, double v) => WriteFixed64FieldTag2(encodedTagLE, BitConverter.DoubleToUInt64Bits(v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteStringFieldTag1(byte encodedTag, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            EnsureSpace(2);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            dst = encodedTag;
            Unsafe.Add(ref dst, 1) = 1;
            _pos += 2;
            return;
        }

        int charLen = value.Length;
        if (charLen <= 127)
        {
            EnsureSpace(2 + charLen);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            dst = encodedTag;
            Unsafe.Add(ref dst, 1) = (byte)((charLen << 1) | 1);
            if (TryNarrowAsciiShort(ref MemoryMarshal.GetReference(value.AsSpan()), ref Unsafe.Add(ref dst, 2), charLen))
            {
                _pos += 2 + charLen;
                return;
            }
        }

        WriteStringFieldTag1Slow(encodedTag, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteStringFieldTag1Slow(byte encodedTag, string value)
    {
        WriteTag1(encodedTag);
        WriteStringValueSlow(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteStringFieldTag2(ushort encodedTagLE, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            EnsureSpace(3);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            ushort tagEnc = BitConverter.IsLittleEndian ? encodedTagLE : BinaryPrimitives.ReverseEndianness(encodedTagLE);
            Unsafe.WriteUnaligned(ref dst, tagEnc);
            Unsafe.Add(ref dst, 2) = 1;
            _pos += 3;
            return;
        }

        int charLen = value.Length;
        if (charLen <= 127)
        {
            EnsureSpace(3 + charLen);
            ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), _pos);
            ushort tagEnc = BitConverter.IsLittleEndian ? encodedTagLE : BinaryPrimitives.ReverseEndianness(encodedTagLE);
            Unsafe.WriteUnaligned(ref dst, tagEnc);
            Unsafe.Add(ref dst, 2) = (byte)((charLen << 1) | 1);
            if (TryNarrowAsciiShort(ref MemoryMarshal.GetReference(value.AsSpan()), ref Unsafe.Add(ref dst, 3), charLen))
            {
                _pos += 3 + charLen;
                return;
            }
        }

        WriteStringFieldTag2Slow(encodedTagLE, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteStringFieldTag2Slow(ushort encodedTagLE, string value)
    {
        WriteTag2(encodedTagLE);
        WriteStringValueSlow(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBytesFieldTag1(byte encodedTag, ReadOnlySpan<byte> value)
    {
        WriteTag1(encodedTag);
        WriteBytesValue(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBytesFieldTag2(ushort encodedTagLE, ReadOnlySpan<byte> value)
    {
        WriteTag2(encodedTagLE);
        WriteBytesValue(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte BeginBulkFixedArrayTag1(byte encodedTag, OmniBufWireType elemType, int count, int elemSize)
    {
        if (_depth >= _maxDepth)
        {
            ThrowMaxDepthExceeded(_depth + 1, _maxDepth);
        }

        int countVarintLen = count <= 127 ? 1 : (count <= 16_383 ? 2 : (count <= 536_870_911 ? 4 : 9));
        long dataBytes = (long)count * elemSize;
        long bodyLen = 1L + countVarintLen + dataBytes;
        int lenVarintLen = bodyLen <= 16_383 ? 2 : (bodyLen <= 536_870_911 ? 4 : 9);
        long totalNeeded = 1L + lenVarintLen + bodyLen;
        if (totalNeeded > Array.MaxLength)
        {
            GrowBuffer(totalNeeded);
        }

        EnsureSpace((int)totalNeeded);
        int p = _pos;
        ref byte bufRef = ref MemoryMarshal.GetArrayDataReference(_buf);
        Unsafe.Add(ref bufRef, p++) = encodedTag;
        WriteFixedWidthVarintAt(p, (ulong)bodyLen, lenVarintLen);
        p += lenVarintLen;
        Unsafe.Add(ref bufRef, p++) = (byte)elemType;
        WriteFixedWidthVarintAt(p, (ulong)count, countVarintLen);
        p += countVarintLen;
        _pos = p;
        return ref Unsafe.Add(ref bufRef, p);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte BeginBulkFixedArrayTag2(ushort encodedTagLE, OmniBufWireType elemType, int count, int elemSize)
    {
        if (_depth >= _maxDepth)
        {
            ThrowMaxDepthExceeded(_depth + 1, _maxDepth);
        }

        int countVarintLen = count <= 127 ? 1 : (count <= 16_383 ? 2 : (count <= 536_870_911 ? 4 : 9));
        long dataBytes = (long)count * elemSize;
        long bodyLen = 1L + countVarintLen + dataBytes;
        int lenVarintLen = bodyLen <= 16_383 ? 2 : (bodyLen <= 536_870_911 ? 4 : 9);
        long totalNeeded = 2L + lenVarintLen + bodyLen;
        if (totalNeeded > Array.MaxLength)
        {
            GrowBuffer(totalNeeded);
        }

        EnsureSpace((int)totalNeeded);
        int p = _pos;
        ref byte bufRef = ref MemoryMarshal.GetArrayDataReference(_buf);
        ushort tagEnc = BitConverter.IsLittleEndian ? encodedTagLE : BinaryPrimitives.ReverseEndianness(encodedTagLE);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref bufRef, p), tagEnc);
        p += 2;
        WriteFixedWidthVarintAt(p, (ulong)bodyLen, lenVarintLen);
        p += lenVarintLen;
        Unsafe.Add(ref bufRef, p++) = (byte)elemType;
        WriteFixedWidthVarintAt(p, (ulong)count, countVarintLen);
        p += countVarintLen;
        _pos = p;
        return ref Unsafe.Add(ref bufRef, p);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte BeginBulkFixedArray(uint fieldNum, OmniBufWireType elemType, int count, int elemSize)
    {
        if (_depth >= _maxDepth)
        {
            ThrowMaxDepthExceeded(_depth + 1, _maxDepth);
        }

        WriteTag(fieldNum, OmniBufWireType.Array);
        int countVarintLen = count <= 127 ? 1 : (count <= 16_383 ? 2 : (count <= 536_870_911 ? 4 : 9));
        long dataBytes = (long)count * elemSize;
        long bodyLen = 1L + countVarintLen + dataBytes;
        int lenVarintLen = bodyLen <= 16_383 ? 2 : (bodyLen <= 536_870_911 ? 4 : 9);
        long totalNeeded = lenVarintLen + bodyLen;
        if (totalNeeded > Array.MaxLength)
        {
            GrowBuffer(totalNeeded);
        }

        EnsureSpace((int)totalNeeded);
        int p = _pos;
        ref byte bufRef = ref MemoryMarshal.GetArrayDataReference(_buf);
        WriteFixedWidthVarintAt(p, (ulong)bodyLen, lenVarintLen);
        p += lenVarintLen;
        Unsafe.Add(ref bufRef, p++) = (byte)elemType;
        WriteFixedWidthVarintAt(p, (ulong)count, countVarintLen);
        p += countVarintLen;
        _pos = p;
        return ref Unsafe.Add(ref bufRef, p);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteFloat64Elements(ref byte dst, RepeatedField<double> values, int count)
    {
        if (BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref dst, values[i]);
                dst = ref Unsafe.Add(ref dst, 8);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref dst, BinaryPrimitives.ReverseEndianness(BitConverter.DoubleToUInt64Bits(values[i])));
                dst = ref Unsafe.Add(ref dst, 8);
            }
        }

        _pos += count * 8;
    }

    public void WriteFloat64ArrayFieldTag1(byte encodedTag, RepeatedField<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArrayTag1(encodedTag, OmniBufWireType.Float64, count, 8);
        WriteFloat64Elements(ref dst, values, count);
    }

    public void WriteFloat64ArrayFieldTag2(ushort encodedTagLE, RepeatedField<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArrayTag2(encodedTagLE, OmniBufWireType.Float64, count, 8);
        WriteFloat64Elements(ref dst, values, count);
    }

    public void WriteFloat64ArrayField(uint fieldNum, RepeatedField<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArray(fieldNum, OmniBufWireType.Float64, count, 8);
        WriteFloat64Elements(ref dst, values, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteFixed64Elements(ref byte dst, RepeatedField<ulong> values, int count)
    {
        if (BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref dst, values[i]);
                dst = ref Unsafe.Add(ref dst, 8);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref dst, BinaryPrimitives.ReverseEndianness(values[i]));
                dst = ref Unsafe.Add(ref dst, 8);
            }
        }

        _pos += count * 8;
    }

    public void WriteFixed64ArrayFieldTag1(byte encodedTag, RepeatedField<ulong> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArrayTag1(encodedTag, OmniBufWireType.Fixed64, count, 8);
        WriteFixed64Elements(ref dst, values, count);
    }

    public void WriteFixed64ArrayFieldTag2(ushort encodedTagLE, RepeatedField<ulong> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArrayTag2(encodedTagLE, OmniBufWireType.Fixed64, count, 8);
        WriteFixed64Elements(ref dst, values, count);
    }

    public void WriteFixed64ArrayField(uint fieldNum, RepeatedField<ulong> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArray(fieldNum, OmniBufWireType.Fixed64, count, 8);
        WriteFixed64Elements(ref dst, values, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSfixed64Elements(ref byte dst, RepeatedField<long> values, int count)
    {
        if (BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref dst, values[i]);
                dst = ref Unsafe.Add(ref dst, 8);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref dst, BinaryPrimitives.ReverseEndianness((ulong)values[i]));
                dst = ref Unsafe.Add(ref dst, 8);
            }
        }

        _pos += count * 8;
    }

    public void WriteSfixed64ArrayFieldTag1(byte encodedTag, RepeatedField<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArrayTag1(encodedTag, OmniBufWireType.Sfixed64, count, 8);
        WriteSfixed64Elements(ref dst, values, count);
    }

    public void WriteSfixed64ArrayFieldTag2(ushort encodedTagLE, RepeatedField<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArrayTag2(encodedTagLE, OmniBufWireType.Sfixed64, count, 8);
        WriteSfixed64Elements(ref dst, values, count);
    }

    public void WriteSfixed64ArrayField(uint fieldNum, RepeatedField<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArray(fieldNum, OmniBufWireType.Sfixed64, count, 8);
        WriteSfixed64Elements(ref dst, values, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteFloat32Elements(ref byte dst, RepeatedField<float> values, int count)
    {
        if (BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref dst, values[i]);
                dst = ref Unsafe.Add(ref dst, 4);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref dst, BinaryPrimitives.ReverseEndianness(BitConverter.SingleToUInt32Bits(values[i])));
                dst = ref Unsafe.Add(ref dst, 4);
            }
        }

        _pos += count * 4;
    }

    public void WriteFloat32ArrayFieldTag1(byte encodedTag, RepeatedField<float> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArrayTag1(encodedTag, OmniBufWireType.Float32, count, 4);
        WriteFloat32Elements(ref dst, values, count);
    }

    public void WriteFloat32ArrayFieldTag2(ushort encodedTagLE, RepeatedField<float> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArrayTag2(encodedTagLE, OmniBufWireType.Float32, count, 4);
        WriteFloat32Elements(ref dst, values, count);
    }

    public void WriteFloat32ArrayField(uint fieldNum, RepeatedField<float> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArray(fieldNum, OmniBufWireType.Float32, count, 4);
        WriteFloat32Elements(ref dst, values, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteFixed32Elements(ref byte dst, RepeatedField<uint> values, int count)
    {
        if (BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref dst, values[i]);
                dst = ref Unsafe.Add(ref dst, 4);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref dst, BinaryPrimitives.ReverseEndianness(values[i]));
                dst = ref Unsafe.Add(ref dst, 4);
            }
        }

        _pos += count * 4;
    }

    public void WriteFixed32ArrayFieldTag1(byte encodedTag, RepeatedField<uint> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArrayTag1(encodedTag, OmniBufWireType.Fixed32, count, 4);
        WriteFixed32Elements(ref dst, values, count);
    }

    public void WriteFixed32ArrayFieldTag2(ushort encodedTagLE, RepeatedField<uint> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArrayTag2(encodedTagLE, OmniBufWireType.Fixed32, count, 4);
        WriteFixed32Elements(ref dst, values, count);
    }

    public void WriteFixed32ArrayField(uint fieldNum, RepeatedField<uint> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArray(fieldNum, OmniBufWireType.Fixed32, count, 4);
        WriteFixed32Elements(ref dst, values, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSfixed32Elements(ref byte dst, RepeatedField<int> values, int count)
    {
        if (BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref dst, values[i]);
                dst = ref Unsafe.Add(ref dst, 4);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref dst, BinaryPrimitives.ReverseEndianness((uint)values[i]));
                dst = ref Unsafe.Add(ref dst, 4);
            }
        }

        _pos += count * 4;
    }

    public void WriteStringArrayFieldTag1(byte encodedTag, RepeatedField<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        BeginArrayFieldTag1(encodedTag, OmniBufWireType.String, count);
        if (count > 0)
        {
            WriteStringElementsCore(values, count);
        }

        EndContainer();
    }

    public void WriteStringArrayFieldTag2(ushort encodedTagLE, RepeatedField<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        BeginArrayFieldTag2(encodedTagLE, OmniBufWireType.String, count);
        if (count > 0)
        {
            WriteStringElementsCore(values, count);
        }

        EndContainer();
    }

    public void WriteStringArrayField(uint fieldNum, RepeatedField<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        BeginArrayField(fieldNum, OmniBufWireType.String, count);
        if (count > 0)
        {
            WriteStringElementsCore(values, count);
        }

        EndContainer();
    }

    private void WriteStringElementsCore(RepeatedField<string> values, int count)
    {
        byte[] buf = _buf;
        int pos = _pos;
        int bufLen = buf.Length;
        for (int i = 0; i < count; i++)
        {
            string? s = values[i];
            if (string.IsNullOrEmpty(s))
            {
                if (bufLen - pos < 1)
                {
                    _pos = pos;
                    EnsureSpace(1);
                    buf = _buf;
                    bufLen = buf.Length;
                }

                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buf), pos++) = 1;
                continue;
            }

            int charLen = s.Length;
            if (charLen <= 127)
            {
                int needed = 1 + charLen;
                if (bufLen - pos < needed)
                {
                    _pos = pos;
                    EnsureSpace(needed);
                    buf = _buf;
                    bufLen = buf.Length;
                }

                ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buf), pos);
                dst = (byte)((charLen << 1) | 1);
                if (TryNarrowAsciiShort(ref MemoryMarshal.GetReference(s.AsSpan()), ref Unsafe.Add(ref dst, 1), charLen))
                {
                    pos += needed;
                    continue;
                }
            }

            _pos = pos;
            WriteStringValueSlow(s);
            buf = _buf;
            pos = _pos;
            bufLen = buf.Length;
        }

        _pos = pos;
    }

    public void WriteSfixed32ArrayFieldTag1(byte encodedTag, RepeatedField<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArrayTag1(encodedTag, OmniBufWireType.Sfixed32, count, 4);
        WriteSfixed32Elements(ref dst, values, count);
    }

    public void WriteSfixed32ArrayFieldTag2(ushort encodedTagLE, RepeatedField<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArrayTag2(encodedTagLE, OmniBufWireType.Sfixed32, count, 4);
        WriteSfixed32Elements(ref dst, values, count);
    }

    public void WriteSfixed32ArrayField(uint fieldNum, RepeatedField<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = values.Count;
        ref byte dst = ref BeginBulkFixedArray(fieldNum, OmniBufWireType.Sfixed32, count, 4);
        WriteSfixed32Elements(ref dst, values, count);
    }

    /// <summary>
    /// Reserves a 2-byte Prefix-Varint length slot on the container stack for single-pass encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginContainer()
    {
        if (_depth >= _maxDepth)
        {
            ThrowMaxDepthExceeded(_depth + 1, _maxDepth);
        }

        if (_depth == _containerStack.Length)
        {
            GrowContainerStack();
        }

        EnsureSpace(2);
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_containerStack), _depth++) = _pos;
        _pos += 2;
    }

    /// <summary>
    /// Reserves a 2-byte or 4-byte Prefix-Varint length slot based on a body byte size hint.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginContainer(int sizeHint)
    {
        if (sizeHint > 16_383)
        {
            BeginContainerLarge();
            return;
        }

        BeginContainer();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BeginContainerLarge()
    {
        if (_depth >= _maxDepth)
        {
            ThrowMaxDepthExceeded(_depth + 1, _maxDepth);
        }

        if (_depth == _containerStack.Length)
        {
            GrowContainerStack();
        }

        EnsureSpace(4);
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_containerStack), _depth++) = _pos | int.MinValue;
        _pos += 4;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowMaxDepthExceeded(int depth, int maxDepth = OmniBufConstants.MaxNestingDepth) =>
        throw new OmniBufException($"omnibuf: encode nesting depth {depth} exceeds maximum {maxDepth}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowContainerUnderflow() =>
        throw new OmniBufException("omnibuf: container stack underflow");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowContainerStack()
    {
        var next = new int[_containerStack.Length * 2];
        Array.Copy(_containerStack, next, _containerStack.Length);
        _containerStack = next;
    }

    /// <summary>
    /// Closes the active container scope and backpatches its Prefix-Varint byte length.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndContainer()
    {
        if (_depth <= 0)
        {
            ThrowContainerUnderflow();
        }

        int rawEntry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_containerStack), --_depth);
        if (rawEntry < 0)
        {
            EndContainerLarge(rawEntry & int.MaxValue);
            return;
        }

        int lenPos = rawEntry;
        int bodyLen = _pos - (lenPos + 2);
        if (bodyLen <= 16_383)
        {
            ushort enc = (ushort)(((uint)bodyLen << 2) | 2u);
            if (!BitConverter.IsLittleEndian)
            {
                enc = BinaryPrimitives.ReverseEndianness(enc);
            }

            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), lenPos), enc);
            return;
        }

        EndContainerOverflow(lenPos, bodyLen);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EndContainerOverflow(int lenPos, int bodyLen)
    {
        int bodyStart = lenPos + 2;
        if (bodyLen <= 536_870_911)
        {
            EnsureSpace(2);
            _buf.AsSpan(bodyStart, bodyLen).CopyTo(_buf.AsSpan(lenPos + 4, bodyLen));
            BinaryPrimitives.WriteUInt32LittleEndian(
                _buf.AsSpan(lenPos, 4),
                ((uint)bodyLen << 3) | 4u);
            _pos += 2;
        }
        else
        {
            EnsureSpace(7);
            _buf.AsSpan(bodyStart, bodyLen).CopyTo(_buf.AsSpan(lenPos + 9, bodyLen));
            _buf[lenPos] = 0;
            BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(lenPos + 1, 8), (ulong)bodyLen);
            _pos += 7;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EndContainerLarge(int lenPos)
    {
        int bodyStart = lenPos + 4;
        int bodyLen = _pos - bodyStart;
        if (bodyLen <= 16_383)
        {
            if (bodyLen > 0)
            {
                _buf.AsSpan(bodyStart, bodyLen).CopyTo(_buf.AsSpan(lenPos + 2, bodyLen));
            }

            BinaryPrimitives.WriteUInt16LittleEndian(
                _buf.AsSpan(lenPos, 2),
                (ushort)(((uint)bodyLen << 2) | 2u));
            _pos -= 2;
        }
        else if (bodyLen <= 536_870_911)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                _buf.AsSpan(lenPos, 4),
                ((uint)bodyLen << 3) | 4u);
        }
        else
        {
            EnsureSpace(5);
            _buf.AsSpan(bodyStart, bodyLen).CopyTo(_buf.AsSpan(lenPos + 9, bodyLen));
            _buf[lenPos] = 0;
            BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(lenPos + 1, 8), (ulong)bodyLen);
            _pos += 5;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginSubMsgFieldTag1(byte encodedTag)
    {
        if (_depth >= _maxDepth)
        {
            ThrowMaxDepthExceeded(_depth + 1, _maxDepth);
        }

        if (_depth == _containerStack.Length)
        {
            GrowContainerStack();
        }

        EnsureSpace(3);
        int p = _pos;
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), p) = encodedTag;
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_containerStack), _depth++) = p + 1;
        _pos = p + 3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginSubMsgFieldTag1(byte encodedTag, int sizeHint)
    {
        if (sizeHint > 16_383)
        {
            WriteTag1(encodedTag);
            BeginContainerLarge();
            return;
        }

        BeginSubMsgFieldTag1(encodedTag);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginSubMsgFieldTag2(ushort encodedTagLE)
    {
        if (_depth >= _maxDepth)
        {
            ThrowMaxDepthExceeded(_depth + 1, _maxDepth);
        }

        if (_depth == _containerStack.Length)
        {
            GrowContainerStack();
        }

        EnsureSpace(4);
        int p = _pos;
        ushort tagEnc = BitConverter.IsLittleEndian ? encodedTagLE : BinaryPrimitives.ReverseEndianness(encodedTagLE);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buf), p), tagEnc);
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_containerStack), _depth++) = p + 2;
        _pos = p + 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginSubMsgFieldTag2(ushort encodedTagLE, int sizeHint)
    {
        if (sizeHint > 16_383)
        {
            WriteTag2(encodedTagLE);
            BeginContainerLarge();
            return;
        }

        BeginSubMsgFieldTag2(encodedTagLE);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginArrayFieldTag1(byte encodedTag, OmniBufWireType elemType, int count, int minBodyHint = 0)
    {
        if ((uint)count <= 127u && minBodyHint <= 16_383)
        {
            int fixedW = OmniBufConstants.FixedElementWidth(elemType);
            if (fixedW == 0 || 2 + count * fixedW <= 16_383)
            {
                if (_depth >= _maxDepth)
                {
                    ThrowMaxDepthExceeded(_depth + 1, _maxDepth);
                }

                if (_depth == _containerStack.Length)
                {
                    GrowContainerStack();
                }

                EnsureSpace(5);
                int p = _pos;
                ref byte bufRef = ref MemoryMarshal.GetArrayDataReference(_buf);
                Unsafe.Add(ref bufRef, p) = encodedTag;
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_containerStack), _depth++) = p + 1;
                Unsafe.Add(ref bufRef, p + 3) = (byte)elemType;
                Unsafe.Add(ref bufRef, p + 4) = (byte)((count << 1) | 1);
                _pos = p + 5;
                return;
            }
        }

        BeginArrayFieldTag1Slow(encodedTag, elemType, count, minBodyHint);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BeginArrayFieldTag1Slow(byte encodedTag, OmniBufWireType elemType, int count, int minBodyHint)
    {
        if (!OmniBufConstants.IsValidContainerElemType(elemType) || count < 0)
        {
            throw new OmniBufException($"omnibuf: invalid WireArray header ({elemType}, count={count})");
        }

        WriteTag1(encodedTag);
        long minElemWidth = Math.Max(1, OmniBufConstants.FixedElementWidth(elemType));
        long minBody = Math.Max(minBodyHint, 2L + (long)count * minElemWidth);
        if (minBody > 16_383)
        {
            BeginContainerLarge();
        }
        else
        {
            BeginContainer();
        }

        WriteRawByte((byte)elemType);
        WriteVarint((ulong)count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginArrayFieldTag2(ushort encodedTagLE, OmniBufWireType elemType, int count, int minBodyHint = 0)
    {
        if ((uint)count <= 127u && minBodyHint <= 16_383)
        {
            int fixedW = OmniBufConstants.FixedElementWidth(elemType);
            if (fixedW == 0 || 2 + count * fixedW <= 16_383)
            {
                if (_depth >= _maxDepth)
                {
                    ThrowMaxDepthExceeded(_depth + 1, _maxDepth);
                }

                if (_depth == _containerStack.Length)
                {
                    GrowContainerStack();
                }

                EnsureSpace(6);
                int p = _pos;
                ref byte bufRef = ref MemoryMarshal.GetArrayDataReference(_buf);
                ushort tagEnc = BitConverter.IsLittleEndian ? encodedTagLE : BinaryPrimitives.ReverseEndianness(encodedTagLE);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref bufRef, p), tagEnc);
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_containerStack), _depth++) = p + 2;
                Unsafe.Add(ref bufRef, p + 4) = (byte)elemType;
                Unsafe.Add(ref bufRef, p + 5) = (byte)((count << 1) | 1);
                _pos = p + 6;
                return;
            }
        }

        BeginArrayFieldTag2Slow(encodedTagLE, elemType, count, minBodyHint);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BeginArrayFieldTag2Slow(ushort encodedTagLE, OmniBufWireType elemType, int count, int minBodyHint)
    {
        if (!OmniBufConstants.IsValidContainerElemType(elemType) || count < 0)
        {
            throw new OmniBufException($"omnibuf: invalid WireArray header ({elemType}, count={count})");
        }

        WriteTag2(encodedTagLE);
        long minElemWidth = Math.Max(1, OmniBufConstants.FixedElementWidth(elemType));
        long minBody = Math.Max(minBodyHint, 2L + (long)count * minElemWidth);
        if (minBody > 16_383)
        {
            BeginContainerLarge();
        }
        else
        {
            BeginContainer();
        }

        WriteRawByte((byte)elemType);
        WriteVarint((ulong)count);
    }

    public void BeginSubMsgField(uint fieldNum)
    {
        WriteTag(fieldNum, OmniBufWireType.SubMsg);
        BeginContainer();
    }

    public void BeginSubMsgField(uint fieldNum, int sizeHint)
    {
        WriteTag(fieldNum, OmniBufWireType.SubMsg);
        BeginContainer(sizeHint);
    }

    internal void BeginField0SubMsg()
    {
        WriteField0Tag();
        BeginContainer();
    }

    public void EndSubMsg() => EndContainer();

    public void BeginArrayField(uint fieldNum, OmniBufWireType elemType, int count)
    {
        if (!OmniBufConstants.IsValidContainerElemType(elemType) || count < 0)
        {
            throw new OmniBufException($"omnibuf: invalid WireArray header ({elemType}, count={count})");
        }

        WriteTag(fieldNum, OmniBufWireType.Array);
        long minElemWidth = Math.Max(1, OmniBufConstants.FixedElementWidth(elemType));
        long minBody = 2L + (long)count * minElemWidth;
        if (minBody > 16_383)
        {
            BeginContainerLarge();
        }
        else
        {
            BeginContainer();
        }

        WriteRawByte((byte)elemType);
        WriteVarint((ulong)count);
    }

    public void BeginArrayField(uint fieldNum, OmniBufWireType elemType, int count, int minBodyHint)
    {
        if (!OmniBufConstants.IsValidContainerElemType(elemType) || count < 0)
        {
            throw new OmniBufException($"omnibuf: invalid WireArray header ({elemType}, count={count})");
        }

        WriteTag(fieldNum, OmniBufWireType.Array);
        long minElemWidth = Math.Max(1, OmniBufConstants.FixedElementWidth(elemType));
        long minBody = Math.Max(minBodyHint, 2L + (long)count * minElemWidth);
        if (minBody > 16_383)
        {
            BeginContainerLarge();
        }
        else
        {
            BeginContainer();
        }

        WriteRawByte((byte)elemType);
        WriteVarint((ulong)count);
    }

    public void BeginArrayElement(OmniBufWireType elemType, int count)
    {
        if (!OmniBufConstants.IsValidContainerElemType(elemType) || count < 0)
        {
            throw new OmniBufException($"omnibuf: invalid WireArray header ({elemType}, count={count})");
        }

        long minElemWidth = Math.Max(1, OmniBufConstants.FixedElementWidth(elemType));
        long minBody = 2L + (long)count * minElemWidth;
        if (minBody > 16_383)
        {
            BeginContainerLarge();
        }
        else
        {
            BeginContainer();
        }

        WriteRawByte((byte)elemType);
        WriteVarint((ulong)count);
    }

    public void EndArray() => EndContainer();

    public void BeginMapField(uint fieldNum, OmniBufWireType keyType, OmniBufWireType valType, int count)
    {
        if (!OmniBufConstants.IsValidMapKeyType(keyType)
            || !OmniBufConstants.IsValidContainerElemType(valType)
            || count < 0)
        {
            throw new OmniBufException($"omnibuf: invalid WireMap header ({keyType}, {valType}, count={count})");
        }

        WriteTag(fieldNum, OmniBufWireType.Map);
        long minEntryWidth = Math.Max(1, OmniBufConstants.FixedElementWidth(keyType))
            + Math.Max(1, OmniBufConstants.FixedElementWidth(valType));
        long minBody = 3L + (long)count * minEntryWidth;
        if (minBody > 16_383)
        {
            BeginContainerLarge();
        }
        else
        {
            BeginContainer();
        }

        WriteRawByte((byte)keyType);
        WriteRawByte((byte)valType);
        WriteVarint((ulong)count);
    }

    public void BeginMapElement(OmniBufWireType keyType, OmniBufWireType valType, int count)
    {
        if (!OmniBufConstants.IsValidMapKeyType(keyType)
            || !OmniBufConstants.IsValidContainerElemType(valType)
            || count < 0)
        {
            throw new OmniBufException($"omnibuf: invalid WireMap header ({keyType}, {valType}, count={count})");
        }

        long minEntryWidth = Math.Max(1, OmniBufConstants.FixedElementWidth(keyType))
            + Math.Max(1, OmniBufConstants.FixedElementWidth(valType));
        long minBody = 3L + (long)count * minEntryWidth;
        if (minBody > 16_383)
        {
            BeginContainerLarge();
        }
        else
        {
            BeginContainer();
        }

        WriteRawByte((byte)keyType);
        WriteRawByte((byte)valType);
        WriteVarint((ulong)count);
    }

    public void EndMap() => EndContainer();
}
