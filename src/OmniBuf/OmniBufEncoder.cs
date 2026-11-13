using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace OmniBuf;

/// <summary>
/// Single-pass forward binary encoder for OmniBuf packets with dynamic container stack growth.
/// </summary>
public sealed class OmniBufEncoder
{
    private const int InitialBufferCapacity = 512;
    private const int InitialStackCapacity = 64;

    [ThreadStatic]
    private static OmniBufEncoder? t_shared;
    [ThreadStatic]
    private static bool t_sharedInUse;

    private byte[] _buf;
    private int _pos;
    private int[] _containerStack;
    private int _depth;

    public static OmniBufEncoder RentThreadLocal(out bool rented)
    {
        if (!t_sharedInUse)
        {
            t_sharedInUse = true;
            OmniBufEncoder enc = t_shared ??= new OmniBufEncoder(InitialBufferCapacity);
            enc.Reset();
            rented = true;
            return enc;
        }

        rented = false;
        return new OmniBufEncoder(InitialBufferCapacity);
    }

    public static void ReturnThreadLocal(OmniBufEncoder encoder, bool rented)
    {
        if (rented)
        {
            encoder.Reset();
            t_sharedInUse = false;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureSpace(int needed)
    {
        if ((uint)(_pos + needed) > (uint)_buf.Length)
        {
            GrowBuffer(_pos + needed);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowBuffer(int minCapacity)
    {
        long next = Math.Max((long)_buf.Length * 2, minCapacity);
        if (next > Array.MaxLength)
        {
            if (minCapacity > Array.MaxLength)
            {
                throw new OmniBufException("omnibuf: encoder buffer exceeds maximum array length");
            }

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
            _buf[_pos++] = (byte)((v << 1) | 1);
        }
        else if (v <= 16_383)
        {
            EnsureSpace(2);
            BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_pos, 2), (ushort)((v << 2) | 2));
            _pos += 2;
        }
        else if (v <= 536_870_911)
        {
            EnsureSpace(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_pos, 4), (uint)((v << 3) | 4));
            _pos += 4;
        }
        else
        {
            EnsureSpace(9);
            _buf[_pos] = 0;
            BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(_pos + 1, 8), v);
            _pos += 9;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTag(uint fieldNum, OmniBufWireType wireType)
    {
        if (fieldNum == 0 || fieldNum > OmniBufConstants.MaxFieldNumber)
        {
            throw new OmniBufException($"omnibuf: invalid field number {fieldNum}");
        }

        ulong tag = ((ulong)fieldNum << 4) | ((byte)wireType & 0x0Fu);
        WriteVarint(tag);
    }

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
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_pos, 4), v);
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
        BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(_pos, 8), v);
        _pos += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSfixed64Value(long v) => WriteFixed64Value((ulong)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFloat64Value(double v) => WriteFixed64Value(BitConverter.DoubleToUInt64Bits(v));

    public void WriteStringValue(string? value)
    {
        string s = value ?? string.Empty;
        if (s.Length == 0)
        {
            EnsureSpace(1);
            _buf[_pos++] = 1; // 1B Prefix-Varint for 0
            return;
        }

        if (s.Length <= 127)
        {
            EnsureSpace(1 + s.Length);
            if (Ascii.FromUtf16(s.AsSpan(), _buf.AsSpan(_pos + 1, s.Length), out int asciiWritten) == OperationStatus.Done)
            {
                _buf[_pos] = (byte)((asciiWritten << 1) | 1);
                _pos += 1 + asciiWritten;
                return;
            }
        }

        int byteCount = OmniBufConstants.GetUtf8ByteCountStrict(s);
        WriteVarint((ulong)byteCount);
        EnsureSpace(byteCount);
        try
        {
            int written = OmniBufConstants.StrictUtf8.GetBytes(s.AsSpan(), _buf.AsSpan(_pos, byteCount));
            _pos += written;
        }
        catch (EncoderFallbackException ex)
        {
            throw new OmniBufException("omnibuf: string contains invalid UTF-16 surrogates", ex);
        }
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

        int lenPos = _containerStack[--_depth];
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

    public void BeginSubMsgField(uint fieldNum)
    {
        WriteTag(fieldNum, OmniBufWireType.SubMsg);
        BeginContainer();
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
        BeginContainer();
        WriteRawByte((byte)elemType);
        WriteVarint((ulong)count);
    }

    public void BeginArrayElement(OmniBufWireType elemType, int count)
    {
        if (!OmniBufConstants.IsValidContainerElemType(elemType) || count < 0)
        {
            throw new OmniBufException($"omnibuf: invalid WireArray header ({elemType}, count={count})");
        }

        BeginContainer();
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
        BeginContainer();
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

        BeginContainer();
        WriteRawByte((byte)keyType);
        WriteRawByte((byte)valType);
        WriteVarint((ulong)count);
    }

    public void EndMap() => EndContainer();
}
