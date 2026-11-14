using Google.Protobuf;

namespace OmniBuf;

/// <summary>
/// Helpers for encoding and decoding Protobuf Well-Known Wrapper types
/// (<c>google.protobuf.*Value</c>) as OmniBuf <see cref="OmniBufWireType.SubMsg"/> payloads wrapping field #1.
/// </summary>
public static class OmniBufWrappers
{
    public static void WriteDoubleBody(OmniBufEncoder encoder, double value) => encoder.WriteFloat64Field(1, value);
    public static void WriteFloatBody(OmniBufEncoder encoder, float value) => encoder.WriteFloat32Field(1, value);
    public static void WriteInt64Body(OmniBufEncoder encoder, long value) => encoder.WriteSintField(1, value);
    public static void WriteUInt64Body(OmniBufEncoder encoder, ulong value) => encoder.WriteUintField(1, value);
    public static void WriteInt32Body(OmniBufEncoder encoder, int value) => encoder.WriteSintField(1, value);
    public static void WriteUInt32Body(OmniBufEncoder encoder, uint value) => encoder.WriteUintField(1, value);
    public static void WriteBoolBody(OmniBufEncoder encoder, bool value) => encoder.WriteBoolField(1, value);
    public static void WriteStringBody(OmniBufEncoder encoder, string value) => encoder.WriteStringField(1, value);
    public static void WriteBytesBody(OmniBufEncoder encoder, ByteString value) => encoder.WriteBytesField(1, value.Span);

    public static double DecodeDouble(ReadOnlySpan<byte> data, double initial, int depth, int maxDepth)
    {
        EnsureDepth(depth, maxDepth);
        double result = initial;
        int pos = 0;
        while (pos < data.Length)
        {
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
            if (fnum == 0)
            {
                SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
            }
            else if (fnum == 1 && wt == OmniBufWireType.Float64)
            {
                result = BitConverter.UInt64BitsToDouble(OmniBufDecoder.ReadFixed64(data, ref pos));
            }
            else
            {
                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
            }
        }

        return result;
    }

    public static float DecodeFloat(ReadOnlySpan<byte> data, float initial, int depth, int maxDepth)
    {
        EnsureDepth(depth, maxDepth);
        float result = initial;
        int pos = 0;
        while (pos < data.Length)
        {
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
            if (fnum == 0)
            {
                SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
            }
            else if (fnum == 1 && wt == OmniBufWireType.Float32)
            {
                result = BitConverter.UInt32BitsToSingle(OmniBufDecoder.ReadFixed32(data, ref pos));
            }
            else
            {
                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
            }
        }

        return result;
    }

    public static long DecodeInt64(ReadOnlySpan<byte> data, long initial, int depth, int maxDepth)
    {
        EnsureDepth(depth, maxDepth);
        long result = initial;
        int pos = 0;
        while (pos < data.Length)
        {
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
            if (fnum == 0)
            {
                SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
            }
            else if (fnum == 1 && wt == OmniBufWireType.Sint)
            {
                result = OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos));
            }
            else
            {
                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
            }
        }

        return result;
    }

    public static ulong DecodeUInt64(ReadOnlySpan<byte> data, ulong initial, int depth, int maxDepth)
    {
        EnsureDepth(depth, maxDepth);
        ulong result = initial;
        int pos = 0;
        while (pos < data.Length)
        {
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
            if (fnum == 0)
            {
                SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
            }
            else if (fnum == 1 && wt == OmniBufWireType.Uint)
            {
                result = OmniBufDecoder.ReadVarint(data, ref pos);
            }
            else
            {
                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
            }
        }

        return result;
    }

    public static int DecodeInt32(ReadOnlySpan<byte> data, int initial, int depth, int maxDepth)
    {
        EnsureDepth(depth, maxDepth);
        int result = initial;
        int pos = 0;
        while (pos < data.Length)
        {
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
            if (fnum == 0)
            {
                SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
            }
            else if (fnum == 1 && wt == OmniBufWireType.Sint)
            {
                result = (int)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos));
            }
            else
            {
                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
            }
        }

        return result;
    }

    public static uint DecodeUInt32(ReadOnlySpan<byte> data, uint initial, int depth, int maxDepth)
    {
        EnsureDepth(depth, maxDepth);
        uint result = initial;
        int pos = 0;
        while (pos < data.Length)
        {
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
            if (fnum == 0)
            {
                SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
            }
            else if (fnum == 1 && wt == OmniBufWireType.Uint)
            {
                result = (uint)OmniBufDecoder.ReadVarint(data, ref pos);
            }
            else
            {
                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
            }
        }

        return result;
    }

    public static bool DecodeBool(ReadOnlySpan<byte> data, bool initial, int depth, int maxDepth)
    {
        EnsureDepth(depth, maxDepth);
        bool result = initial;
        int pos = 0;
        while (pos < data.Length)
        {
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
            if (fnum == 0)
            {
                SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
            }
            else if (fnum == 1 && (wt == OmniBufWireType.BoolFalse || wt == OmniBufWireType.BoolTrue))
            {
                result = wt == OmniBufWireType.BoolTrue;
            }
            else
            {
                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
            }
        }

        return result;
    }

    public static string DecodeString(ReadOnlySpan<byte> data, string initial, int depth, int maxDepth)
    {
        EnsureDepth(depth, maxDepth);
        string result = initial;
        int pos = 0;
        while (pos < data.Length)
        {
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
            if (fnum == 0)
            {
                SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
            }
            else if (fnum == 1 && wt == OmniBufWireType.String)
            {
                result = OmniBufDecoder.ReadString(data, ref pos);
            }
            else
            {
                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
            }
        }

        return result;
    }

    public static ByteString DecodeBytes(ReadOnlySpan<byte> data, ByteString initial, int depth, int maxDepth)
    {
        EnsureDepth(depth, maxDepth);
        ByteString result = initial;
        int pos = 0;
        while (pos < data.Length)
        {
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
            if (fnum == 0)
            {
                SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
            }
            else if (fnum == 1 && wt == OmniBufWireType.Bytes)
            {
                result = ByteString.CopyFrom(OmniBufDecoder.ReadLenSlice(data, ref pos, 0));
            }
            else
            {
                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
            }
        }

        return result;
    }

    public static double? ReadElemDouble(ReadOnlySpan<byte> data, ref int pos)
    {
        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
        return DecodeDouble(sub, 0d, 0, OmniBufConstants.MaxNestingDepth);
    }

    public static float? ReadElemFloat(ReadOnlySpan<byte> data, ref int pos)
    {
        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
        return DecodeFloat(sub, 0f, 0, OmniBufConstants.MaxNestingDepth);
    }

    public static long? ReadElemInt64(ReadOnlySpan<byte> data, ref int pos)
    {
        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
        return DecodeInt64(sub, 0L, 0, OmniBufConstants.MaxNestingDepth);
    }

    public static ulong? ReadElemUInt64(ReadOnlySpan<byte> data, ref int pos)
    {
        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
        return DecodeUInt64(sub, 0UL, 0, OmniBufConstants.MaxNestingDepth);
    }

    public static int? ReadElemInt32(ReadOnlySpan<byte> data, ref int pos)
    {
        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
        return DecodeInt32(sub, 0, 0, OmniBufConstants.MaxNestingDepth);
    }

    public static uint? ReadElemUInt32(ReadOnlySpan<byte> data, ref int pos)
    {
        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
        return DecodeUInt32(sub, 0u, 0, OmniBufConstants.MaxNestingDepth);
    }

    public static bool? ReadElemBool(ReadOnlySpan<byte> data, ref int pos)
    {
        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
        return DecodeBool(sub, false, 0, OmniBufConstants.MaxNestingDepth);
    }

    public static string ReadElemString(ReadOnlySpan<byte> data, ref int pos)
    {
        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
        return DecodeString(sub, string.Empty, 0, OmniBufConstants.MaxNestingDepth);
    }

    public static ByteString ReadElemBytes(ReadOnlySpan<byte> data, ref int pos)
    {
        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
        return DecodeBytes(sub, ByteString.Empty, 0, OmniBufConstants.MaxNestingDepth);
    }

    public static SchemaMeta? BuildSchemaMeta(string wrapperTypeName, int depth)
    {
        if (depth + 1 > OmniBufConstants.MaxNestingDepth)
        {
            return null;
        }

        return new SchemaMeta
        {
            TypeName = wrapperTypeName,
            Fields = new Dictionary<uint, string>(1) { [1u] = "value" },
        };
    }

    private static void EnsureDepth(int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            throw new OmniBufException($"omnibuf: wrapper nesting depth {depth} exceeds maximum {maxDepth}");
        }
    }
}
