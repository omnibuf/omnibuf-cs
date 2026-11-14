using System.Runtime.CompilerServices;
using Google.Protobuf;

namespace OmniBuf;

/// <summary>
/// O(1) header-hop field locators and zero-decode binary field splicing for Opaque views.
/// </summary>
public static class OmniBufOpaque
{
    public delegate T ElemReader<out T>(ReadOnlySpan<byte> data, ref int pos);

    /// <summary>
    /// Locates the last occurrence of <paramref name="targetField"/> in <paramref name="packet"/>
    /// by hopping across field headers using TZCNT length prefixes without parsing skipped payloads.
    /// Throws <see cref="OmniBufException"/> if the outer packet framing is corrupt.
    /// </summary>
    public static bool LocateField(
        ReadOnlySpan<byte> packet,
        uint targetField,
        out ReadOnlySpan<byte> valueSlice,
        out OmniBufWireType wireType)
    {
        return LocateFieldMask(packet, targetField, 0xFFFF, out valueSlice, out wireType);
    }

    public static bool LocateFieldMask(
        ReadOnlySpan<byte> packet,
        uint targetField,
        ushort expectedMask,
        out ReadOnlySpan<byte> valueSlice,
        out OmniBufWireType wireType)
    {
        valueSlice = default;
        wireType = default;
        bool found = false;

        int pos = 0;
        while (pos < packet.Length)
        {
            if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong tag, out int tn)
                || (tag >> 4) > OmniBufConstants.MaxFieldNumber)
            {
                ThrowCorruptPacket("invalid tag in Opaque packet", pos);
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                ThrowCorruptPacket("Field #0 requires SubMsg wire type", pos - tn);
            }

            int valStart = pos;
            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                ThrowCorruptPacket("truncated or malformed field payload in Opaque packet", valStart);
            }

            if (fnum == targetField && ((expectedMask & (1u << (byte)wt)) != 0))
            {
                valueSlice = packet.Slice(valStart, pos - valStart);
                wireType = wt;
                found = true;
            }
        }

        return found;
    }

    public static bool TryReadUint(ReadOnlySpan<byte> packet, uint fieldNum, out ulong value)
    {
        if (LocateFieldMask(packet, fieldNum, 1 << (byte)OmniBufWireType.Uint, out ReadOnlySpan<byte> val, out _))
        {
            int p = 0;
            value = OmniBufDecoder.ReadVarint(val, ref p);
            if (p != val.Length)
            {
                ThrowCorruptPacket("trailing bytes in Uint field", p);
            }

            return true;
        }

        value = 0;
        return false;
    }

    public static bool TryReadSint(ReadOnlySpan<byte> packet, uint fieldNum, out long value)
    {
        if (LocateFieldMask(packet, fieldNum, 1 << (byte)OmniBufWireType.Sint, out ReadOnlySpan<byte> val, out _))
        {
            int p = 0;
            ulong u = OmniBufDecoder.ReadVarint(val, ref p);
            if (p != val.Length)
            {
                ThrowCorruptPacket("trailing bytes in Sint field", p);
            }

            value = OmniBufConstants.ZigZagDecode64(u);
            return true;
        }

        value = 0;
        return false;
    }

    public static bool TryReadBool(ReadOnlySpan<byte> packet, uint fieldNum, out bool value)
    {
        const ushort mask = (1 << (byte)OmniBufWireType.BoolFalse) | (1 << (byte)OmniBufWireType.BoolTrue);
        if (LocateFieldMask(packet, fieldNum, mask, out _, out OmniBufWireType wt))
        {
            value = wt == OmniBufWireType.BoolTrue;
            return true;
        }

        value = false;
        return false;
    }

    public static bool TryReadFixed32(ReadOnlySpan<byte> packet, uint fieldNum, OmniBufWireType expectedWt, out uint bits)
    {
        if (LocateFieldMask(packet, fieldNum, (ushort)(1u << (byte)expectedWt), out ReadOnlySpan<byte> val, out _))
        {
            int p = 0;
            bits = OmniBufDecoder.ReadFixed32(val, ref p);
            return true;
        }

        bits = 0;
        return false;
    }

    public static bool TryReadFixed64(ReadOnlySpan<byte> packet, uint fieldNum, OmniBufWireType expectedWt, out ulong bits)
    {
        if (LocateFieldMask(packet, fieldNum, (ushort)(1u << (byte)expectedWt), out ReadOnlySpan<byte> val, out _))
        {
            int p = 0;
            bits = OmniBufDecoder.ReadFixed64(val, ref p);
            return true;
        }

        bits = 0;
        return false;
    }

    public static bool TryReadString(ReadOnlySpan<byte> packet, uint fieldNum, out string value)
    {
        if (LocateFieldMask(packet, fieldNum, 1 << (byte)OmniBufWireType.String, out ReadOnlySpan<byte> val, out _))
        {
            int p = 0;
            value = OmniBufDecoder.ReadString(val, ref p);
            if (p != val.Length)
            {
                ThrowCorruptPacket("trailing bytes in String field", p);
            }

            return true;
        }

        value = string.Empty;
        return false;
    }

    public static bool TryReadBytes(ReadOnlySpan<byte> packet, uint fieldNum, out byte[] value)
    {
        if (LocateFieldMask(packet, fieldNum, 1 << (byte)OmniBufWireType.Bytes, out ReadOnlySpan<byte> val, out _))
        {
            int p = 0;
            ReadOnlySpan<byte> slice = OmniBufDecoder.ReadLenSlice(val, ref p, 0);
            if (p != val.Length)
            {
                ThrowCorruptPacket("trailing bytes in Bytes field", p);
            }

            value = slice.ToArray();
            return true;
        }

        value = Array.Empty<byte>();
        return false;
    }

    /// <summary>
    /// Reads a nested string (for example, peer.location.city) by hopping across outer and inner headers
    /// without decoding unrelated fields or allocating intermediate message objects.
    /// </summary>
    public static bool TryReadNestedString(ReadOnlySpan<byte> packet, uint subMsgField, uint targetField, out string value)
    {
        ReadOnlySpan<byte> lastBytes = default;
        bool found = false;
        int pos = 0;

        while (pos < packet.Length)
        {
            if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong tag, out int tn)
                || (tag >> 4) > OmniBufConstants.MaxFieldNumber)
            {
                ThrowCorruptPacket("invalid outer tag in TryReadNestedString", pos);
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                ThrowCorruptPacket("Field #0 requires SubMsg wire type", pos - tn);
            }

            if (fnum == subMsgField && wt == OmniBufWireType.SubMsg)
            {
                ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(packet, ref pos, 0);
                if (LocateFieldMask(sub, targetField, 1 << (byte)OmniBufWireType.String, out ReadOnlySpan<byte> strField, out _))
                {
                    int sp = 0;
                    lastBytes = OmniBufDecoder.ReadLenSlice(strField, ref sp, 0);
                    if (sp != strField.Length)
                    {
                        ThrowCorruptPacket("trailing bytes in nested String field", sp);
                    }

                    found = true;
                }

                continue;
            }

            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                ThrowCorruptPacket("truncated field payload in TryReadNestedString", pos);
            }
        }

        if (!found)
        {
            value = string.Empty;
            return false;
        }

        value = OmniBufConstants.DecodeUtf8Strict(lastBytes);
        return true;
    }

    /// <summary>
    /// Extracts the inner payload of a WireSubMsg field, concatenating segments if the field repeats.
    /// </summary>
    public static bool TryReadSubMsgBytes(ReadOnlySpan<byte> packet, uint fieldNum, out byte[] subMsgBytes)
    {
        ReadOnlySpan<byte> singleSlice = default;
        List<byte>? merged = null;
        int matchCount = 0;
        int pos = 0;

        while (pos < packet.Length)
        {
            if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong tag, out int tn)
                || (tag >> 4) > OmniBufConstants.MaxFieldNumber)
            {
                ThrowCorruptPacket("invalid tag in TryReadSubMsgBytes", pos);
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                ThrowCorruptPacket("Field #0 requires SubMsg wire type", pos - tn);
            }

            if (fnum == fieldNum && wt == OmniBufWireType.SubMsg)
            {
                ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(packet, ref pos, 0);
                matchCount++;
                if (matchCount == 1)
                {
                    singleSlice = body;
                }
                else
                {
                    if (merged is null)
                    {
                        OmniBufDecoder.ValidateMessage(singleSlice, 1, OmniBufConstants.MaxNestingDepth);
                        OmniBufDecoder.ValidateMessage(body, 1, OmniBufConstants.MaxNestingDepth);
                        merged = new List<byte>(singleSlice.Length + body.Length);
                        merged.AddRange(singleSlice.ToArray());
                    }
                    else
                    {
                        OmniBufDecoder.ValidateMessage(body, 1, OmniBufConstants.MaxNestingDepth);
                    }

                    merged.AddRange(body.ToArray());
                }

                continue;
            }

            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                ThrowCorruptPacket("truncated field payload in TryReadSubMsgBytes", pos);
            }
        }

        if (matchCount == 0)
        {
            subMsgBytes = Array.Empty<byte>();
            return false;
        }

        subMsgBytes = matchCount == 1 ? singleSlice.ToArray() : merged!.ToArray();
        return true;
    }

    public static List<T> ReadArray<T>(
        ReadOnlySpan<byte> packet,
        uint fieldNum,
        OmniBufWireType expectedElemWt,
        ElemReader<T> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var result = new List<T>();
        int pos = 0;
        while (pos < packet.Length)
        {
            if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong tag, out int tn)
                || (tag >> 4) > OmniBufConstants.MaxFieldNumber)
            {
                ThrowCorruptPacket("invalid tag in ReadArray", pos);
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                ThrowCorruptPacket("Field #0 requires SubMsg wire type", pos - tn);
            }

            if (fnum == fieldNum && wt == OmniBufWireType.Array)
            {
                ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(packet, ref pos, 2);
                if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p)
                    || elemWt != expectedElemWt)
                {
                    ThrowCorruptPacket($"invalid WireArray header on field #{fieldNum}", pos);
                }

                result.EnsureCapacity(result.Count + Math.Min(count, 4096));
                for (int i = 0; i < count; i++)
                {
                    result.Add(reader(body, ref p));
                }

                if (p != body.Length)
                {
                    ThrowCorruptPacket($"trailing bytes in WireArray on field #{fieldNum}", p);
                }

                continue;
            }

            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                ThrowCorruptPacket("truncated field payload in ReadArray", pos);
            }
        }

        return result;
    }

    public static Dictionary<TKey, TVal> ReadMap<TKey, TVal>(
        ReadOnlySpan<byte> packet,
        uint fieldNum,
        OmniBufWireType expectedKeyWt,
        OmniBufWireType expectedValWt,
        ElemReader<TKey> keyReader,
        ElemReader<TVal> valReader,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(keyReader);
        ArgumentNullException.ThrowIfNull(valReader);

        var result = new Dictionary<TKey, TVal>(comparer);
        int pos = 0;
        while (pos < packet.Length)
        {
            if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong tag, out int tn)
                || (tag >> 4) > OmniBufConstants.MaxFieldNumber)
            {
                ThrowCorruptPacket("invalid tag in ReadMap", pos);
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                ThrowCorruptPacket("Field #0 requires SubMsg wire type", pos - tn);
            }

            if (fnum == fieldNum && wt == OmniBufWireType.Map)
            {
                ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(packet, ref pos, 3);
                if (!OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType kWt, out OmniBufWireType vWt, out int count, out int p)
                    || kWt != expectedKeyWt
                    || vWt != expectedValWt)
                {
                    ThrowCorruptPacket($"invalid WireMap header on field #{fieldNum}", pos);
                }

                result.EnsureCapacity(result.Count + Math.Min(count, 4096));
                for (int i = 0; i < count; i++)
                {
                    TKey k = keyReader(body, ref p);
                    TVal v = valReader(body, ref p);
                    result[k] = v;
                }

                if (p != body.Length)
                {
                    ThrowCorruptPacket($"trailing bytes in WireMap on field #{fieldNum}", p);
                }

                continue;
            }

            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                ThrowCorruptPacket("truncated field payload in ReadMap", pos);
            }
        }

        return result;
    }

    public static uint ReadElemUint32(ReadOnlySpan<byte> data, ref int pos) => (uint)OmniBufDecoder.ReadVarint(data, ref pos);

    public static ulong ReadElemUint64(ReadOnlySpan<byte> data, ref int pos) => OmniBufDecoder.ReadVarint(data, ref pos);

    public static int ReadElemSint32(ReadOnlySpan<byte> data, ref int pos) => (int)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos));

    public static long ReadElemSint64(ReadOnlySpan<byte> data, ref int pos) => OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos));

    public static bool ReadElemBool(ReadOnlySpan<byte> data, ref int pos) => OmniBufDecoder.ReadBoolElement(data, ref pos);

    public static uint ReadElemFixed32(ReadOnlySpan<byte> data, ref int pos) => OmniBufDecoder.ReadFixed32(data, ref pos);

    public static ulong ReadElemFixed64(ReadOnlySpan<byte> data, ref int pos) => OmniBufDecoder.ReadFixed64(data, ref pos);

    public static int ReadElemSfixed32(ReadOnlySpan<byte> data, ref int pos) => (int)OmniBufDecoder.ReadFixed32(data, ref pos);

    public static long ReadElemSfixed64(ReadOnlySpan<byte> data, ref int pos) => (long)OmniBufDecoder.ReadFixed64(data, ref pos);

    public static float ReadElemFloat32(ReadOnlySpan<byte> data, ref int pos) => BitConverter.UInt32BitsToSingle(OmniBufDecoder.ReadFixed32(data, ref pos));

    public static double ReadElemFloat64(ReadOnlySpan<byte> data, ref int pos) => BitConverter.UInt64BitsToDouble(OmniBufDecoder.ReadFixed64(data, ref pos));

    public static string ReadElemString(ReadOnlySpan<byte> data, ref int pos) => OmniBufDecoder.ReadString(data, ref pos);

    public static byte[] ReadElemBytes(ReadOnlySpan<byte> data, ref int pos) => OmniBufDecoder.ReadLenSlice(data, ref pos, 0).ToArray();

    public static ByteString ReadElemByteString(ReadOnlySpan<byte> data, ref int pos) => ByteString.CopyFrom(OmniBufDecoder.ReadLenSlice(data, ref pos, 0));

    public static List<string> ReadStringArray(ReadOnlySpan<byte> packet, uint fieldNum) =>
        ReadArray(packet, fieldNum, OmniBufWireType.String, ReadElemString);

    public static List<uint> ReadUint32Array(ReadOnlySpan<byte> packet, uint fieldNum) =>
        ReadArray(packet, fieldNum, OmniBufWireType.Uint, ReadElemUint32);

    public static List<byte[]> ReadSubMsgArrayPayloads(ReadOnlySpan<byte> packet, uint fieldNum) =>
        ReadArray(packet, fieldNum, OmniBufWireType.SubMsg, ReadElemBytes);

    public static Dictionary<string, ulong> ReadStringUint64Map(ReadOnlySpan<byte> packet, uint fieldNum) =>
        ReadMap(packet, fieldNum, OmniBufWireType.String, OmniBufWireType.Uint, ReadElemString, ReadElemUint64, StringComparer.Ordinal);

    /// <summary>
    /// Splices modified fields into an OmniBuf packet without decoding untouched fields.
    /// Untouched byte ranges are block-copied verbatim; modified fields are written in ascending field order.
    /// </summary>
    public static byte[] SpliceFields(
        ReadOnlySpan<byte> packet,
        ulong dirtyMaskLow,
        HashSet<uint>? dirtyOverflow,
        Action<OmniBufEncoder, uint> appendDirtyField)
    {
        return SpliceFieldsWithPrefix(ReadOnlySpan<byte>.Empty, packet, dirtyMaskLow, dirtyOverflow, appendDirtyField);
    }

    public static byte[] SpliceFields<TState>(
        ReadOnlySpan<byte> packet,
        ulong dirtyMaskLow,
        HashSet<uint>? dirtyOverflow,
        TState state,
        Action<TState, OmniBufEncoder, uint> appendDirtyField,
        bool stripField0 = false)
    {
        ArgumentNullException.ThrowIfNull(appendDirtyField);

        if (!stripField0 && dirtyMaskLow == 0 && (dirtyOverflow is null || dirtyOverflow.Count == 0))
        {
            return packet.ToArray();
        }

        OmniBufEncoder enc = OmniBufEncoder.RentThreadLocal(out bool rented);
        try
        {
            SpliceFieldsTo(enc, packet, dirtyMaskLow, dirtyOverflow, state, appendDirtyField, stripField0);
            return enc.ToArray();
        }
        finally
        {
            OmniBufEncoder.ReturnThreadLocal(enc, rented);
        }
    }

    public static void SpliceFieldsTo<TState>(
        OmniBufEncoder enc,
        ReadOnlySpan<byte> packet,
        ulong dirtyMaskLow,
        HashSet<uint>? dirtyOverflow,
        TState state,
        Action<TState, OmniBufEncoder, uint> appendDirtyField,
        bool stripField0 = false)
    {
        ArgumentNullException.ThrowIfNull(enc);
        ArgumentNullException.ThrowIfNull(appendDirtyField);

        if (!stripField0 && dirtyMaskLow == 0 && (dirtyOverflow is null || dirtyOverflow.Count == 0))
        {
            enc.WriteRawBytes(packet);
            return;
        }

        int runStart = 0;
        int pos = 0;
        while (pos < packet.Length)
        {
            int fieldStart = pos;
            if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong tag, out int tn)
                || (tag >> 4) > OmniBufConstants.MaxFieldNumber)
            {
                throw new OmniBufException($"omnibuf: invalid tag during SpliceFields at {pos}");
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0)
            {
                SchemaMeta.ValidateField0(packet, ref pos, wt, 0, OmniBufConstants.MaxNestingDepth);
            }
            else if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                throw new OmniBufException($"omnibuf: truncated field #{fnum} during SpliceFields");
            }

            bool skipExisting = (fnum == 0 && stripField0) || IsFieldDirty(fnum, dirtyMaskLow, dirtyOverflow);
            if (skipExisting)
            {
                if (fieldStart > runStart)
                {
                    enc.WriteRawBytes(packet.Slice(runStart, fieldStart - runStart));
                }

                runStart = pos;
            }
        }

        if (packet.Length > runStart)
        {
            enc.WriteRawBytes(packet.Slice(runStart, packet.Length - runStart));
        }

        ulong mask = dirtyMaskLow & ~1UL;
        while (mask != 0)
        {
            int bit = System.Numerics.BitOperations.TrailingZeroCount(mask);
            appendDirtyField(state, enc, (uint)bit);
            mask &= mask - 1;
        }

        if (dirtyOverflow is { Count: > 0 })
        {
            var sorted = new List<uint>(dirtyOverflow);
            sorted.Sort();
            foreach (uint fnum in sorted)
            {
                if (fnum >= 64)
                {
                    appendDirtyField(state, enc, fnum);
                }
            }
        }
    }

    /// <summary>
    /// Fail-closed splice variant that validates all untouched wire payloads (including nested containers,
    /// UTF-8 strings, and sub-message recursion limits) before block-copying them into the output buffer.
    /// </summary>
    public static void SpliceFieldsValidatedTo<TState>(
        OmniBufEncoder enc,
        ReadOnlySpan<byte> packet,
        ulong dirtyMaskLow,
        HashSet<uint>? dirtyOverflow,
        TState state,
        Action<TState, OmniBufEncoder, uint> appendDirtyField,
        bool stripField0 = false,
        int depth = 0,
        int maxDepth = OmniBufConstants.MaxNestingDepth)
    {
        ArgumentNullException.ThrowIfNull(enc);
        ArgumentNullException.ThrowIfNull(appendDirtyField);

        if (!stripField0 && dirtyMaskLow == 0 && (dirtyOverflow is null || dirtyOverflow.Count == 0))
        {
            OmniBufDecoder.ValidateMessage(packet, depth, maxDepth);
            enc.WriteRawBytes(packet);
            return;
        }

        int runStart = 0;
        int pos = 0;
        while (pos < packet.Length)
        {
            int fieldStart = pos;
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(packet, ref pos);
            if (fnum == 0)
            {
                SchemaMeta.ValidateField0(packet, ref pos, wt, depth, maxDepth);
            }
            else
            {
                OmniBufDecoder.ValidateValue(packet, ref pos, wt, depth, maxDepth);
            }

            bool skipExisting = (fnum == 0 && stripField0) || IsFieldDirty(fnum, dirtyMaskLow, dirtyOverflow);
            if (skipExisting)
            {
                if (fieldStart > runStart)
                {
                    enc.WriteRawBytes(packet.Slice(runStart, fieldStart - runStart));
                }

                runStart = pos;
            }
        }

        if (packet.Length > runStart)
        {
            enc.WriteRawBytes(packet.Slice(runStart, packet.Length - runStart));
        }

        ulong mask = dirtyMaskLow & ~1UL;
        while (mask != 0)
        {
            int bit = System.Numerics.BitOperations.TrailingZeroCount(mask);
            appendDirtyField(state, enc, (uint)bit);
            mask &= mask - 1;
        }

        if (dirtyOverflow is { Count: > 0 })
        {
            var sorted = new List<uint>(dirtyOverflow);
            sorted.Sort();
            foreach (uint fnum in sorted)
            {
                if (fnum >= 64)
                {
                    appendDirtyField(state, enc, fnum);
                }
            }
        }
    }

    public static byte[] SpliceFieldsValidated<TState>(
        ReadOnlySpan<byte> packet,
        ulong dirtyMaskLow,
        HashSet<uint>? dirtyOverflow,
        TState state,
        Action<TState, OmniBufEncoder, uint> appendDirtyField,
        bool stripField0 = false)
    {
        ArgumentNullException.ThrowIfNull(appendDirtyField);

        if (!stripField0 && dirtyMaskLow == 0 && (dirtyOverflow is null || dirtyOverflow.Count == 0))
        {
            OmniBufDecoder.ValidateMessage(packet, 0, OmniBufConstants.MaxNestingDepth);
            return packet.ToArray();
        }

        OmniBufEncoder enc = OmniBufEncoder.RentThreadLocal(out bool rented);
        try
        {
            SpliceFieldsValidatedTo(enc, packet, dirtyMaskLow, dirtyOverflow, state, appendDirtyField, stripField0);
            return enc.ToArray();
        }
        finally
        {
            OmniBufEncoder.ReturnThreadLocal(enc, rented);
        }
    }

    public static bool TrySpliceFields<TState>(
        ReadOnlySpan<byte> packet,
        ulong dirtyMaskLow,
        HashSet<uint>? dirtyOverflow,
        TState state,
        Action<TState, OmniBufEncoder, uint> appendDirtyField,
        out byte[] result,
        out string? error,
        bool stripField0 = false)
    {
        try
        {
            result = SpliceFieldsValidated(packet, dirtyMaskLow, dirtyOverflow, state, appendDirtyField, stripField0);
            error = null;
            return true;
        }
        catch (OmniBufException ex)
        {
            result = Array.Empty<byte>();
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Splices modified fields while preserving <paramref name="prefix"/> at the start of the output buffer,
    /// even when <paramref name="prefix"/> and <paramref name="packet"/> alias the same backing array.
    /// Never mutates <paramref name="packet"/>.
    /// </summary>
    public static byte[] SpliceFieldsWithPrefix(
        ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> packet,
        ulong dirtyMaskLow,
        HashSet<uint>? dirtyOverflow,
        Action<OmniBufEncoder, uint> appendDirtyField,
        bool stripField0 = false)
    {
        ArgumentNullException.ThrowIfNull(appendDirtyField);

        if (!stripField0 && dirtyMaskLow == 0 && (dirtyOverflow is null || dirtyOverflow.Count == 0))
        {
            var copyOnly = new byte[prefix.Length + packet.Length];
            prefix.CopyTo(copyOnly);
            packet.CopyTo(copyOnly.AsSpan(prefix.Length));
            return copyOnly;
        }

        OmniBufEncoder enc = OmniBufEncoder.RentThreadLocal(out bool rented);
        try
        {
            if (!prefix.IsEmpty)
            {
                enc.WriteRawBytes(prefix);
            }

            SpliceFieldsTo(
                enc,
                packet,
                dirtyMaskLow,
                dirtyOverflow,
                appendDirtyField,
                static (cb, encoder, fnum) => cb(encoder, fnum),
                stripField0);

            return enc.ToArray();
        }
        finally
        {
            OmniBufEncoder.ReturnThreadLocal(enc, rented);
        }
    }

    /// <summary>
    /// ArraySegment overload matching Go's append-oriented wire.SpliceFields(dst[:prefixLen], dst[prefixLen:], ...):
    /// preserves any prefix bytes in <paramref name="dstPrefix"/> and never mutates <paramref name="packetSegment"/>,
    /// even when both segments share the same underlying byte[].
    /// </summary>
    public static byte[] SpliceFields(
        ArraySegment<byte> dstPrefix,
        ArraySegment<byte> packetSegment,
        ulong dirtyMaskLow,
        HashSet<uint>? dirtyOverflow,
        Action<OmniBufEncoder, uint> appendDirtyField)
    {
        ReadOnlySpan<byte> prefix = dstPrefix.Array is null ? ReadOnlySpan<byte>.Empty : dstPrefix.AsSpan();
        ReadOnlySpan<byte> packet = packetSegment.Array is null ? ReadOnlySpan<byte>.Empty : packetSegment.AsSpan();
        return SpliceFieldsWithPrefix(prefix, packet, dirtyMaskLow, dirtyOverflow, appendDirtyField);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsFieldDirty(uint fnum, ulong dirtyMaskLow, HashSet<uint>? dirtyOverflow)
    {
        if (fnum == 0)
        {
            return false;
        }

        if (fnum < 64)
        {
            return (dirtyMaskLow & (1UL << (int)fnum)) != 0;
        }

        return dirtyOverflow is not null && dirtyOverflow.Contains(fnum);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCorruptPacket(string message, int position) =>
        throw new OmniBufException($"omnibuf: {message} at {position}");
}
