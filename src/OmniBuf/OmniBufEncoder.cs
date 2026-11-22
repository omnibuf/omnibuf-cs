using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

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

    public OmniBufEncoder(int initialCapacity = InitialBufferCapacity)
    {
        if (initialCapacity < 16)
        {
            initialCapacity = 16;
        }

        _buf = new byte[initialCapacity];
        _containerStack = new int[InitialStackCapacity];
    }

    public int Length => _pos;

    public int Capacity => _buf.Length;

    public int ContainerDepth => _depth;

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
        long required = (long)_pos + needed;
        if (needed < 0 || required > _buf.Length)
        {
            GrowBuffer(required);
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
        string s = value ?? string.Empty;
        int charLen = s.Length;
        if (charLen == 0)
        {
            EnsureSpace(1);
            _buf[_pos++] = 1; // 1B Prefix-Varint for 0
            return;
        }

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
        if (width == 1)
        {
            _buf[offset] = (byte)((v << 1) | 1);
        }
        else if (width == 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(offset, 2), (ushort)((v << 2) | 2));
        }
        else if (width == 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(offset, 4), (uint)((v << 3) | 4));
        }
        else
        {
            _buf[offset] = 0;
            BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(offset + 1, 8), v);
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

    /// <summary>
    /// Reserves a 2-byte Prefix-Varint length slot on the container stack for single-pass encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginContainer()
    {
        if (_depth == _containerStack.Length)
        {
            GrowContainerStack();
        }

        EnsureSpace(2);
        _containerStack[_depth++] = _pos;
        _buf[_pos] = 0;
        _buf[_pos + 1] = 0;
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
        if (_depth == _containerStack.Length)
        {
            GrowContainerStack();
        }

        EnsureSpace(4);
        _containerStack[_depth++] = _pos | int.MinValue;
        _buf.AsSpan(_pos, 4).Clear();
        _pos += 4;
    }

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
            throw new OmniBufException("omnibuf: container stack underflow");
        }

        int rawEntry = _containerStack[--_depth];
        if (rawEntry < 0)
        {
            EndContainerLarge(rawEntry & int.MaxValue);
            return;
        }

        int lenPos = rawEntry;
        int bodyStart = lenPos + 2;
        int bodyLen = _pos - bodyStart;

        if (bodyLen <= 16_383)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                _buf.AsSpan(lenPos, 2),
                (ushort)(((uint)bodyLen << 2) | 2u));
        }
        else if (bodyLen <= 536_870_911)
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
