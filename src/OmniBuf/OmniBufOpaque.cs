using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OmniBuf;

/// <summary>
/// O(1) header-hop field locators and zero-decode binary field splicing for Opaque views.
/// </summary>
public static class OmniBufOpaque
{
    /// <summary>
    /// Locates the last occurrence of <paramref name="targetField"/> in <paramref name="packet"/>
    /// by hopping across field headers using TZCNT length prefixes without parsing skipped payloads.
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
                valueSlice = default;
                wireType = default;
                return false;
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                valueSlice = default;
                wireType = default;
                return false;
            }

            int valStart = pos;
            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                valueSlice = default;
                wireType = default;
                return false;
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
        if (LocateFieldMask(packet, fieldNum, 1 << (byte)OmniBufWireType.Uint, out ReadOnlySpan<byte> val, out _)
            && OmniBufDecoder.TryDecodeVarint(val, out value, out int n)
            && n == val.Length)
        {
            return true;
        }

        value = 0;
        return false;
    }

    public static bool TryReadSint(ReadOnlySpan<byte> packet, uint fieldNum, out long value)
    {
        if (LocateFieldMask(packet, fieldNum, 1 << (byte)OmniBufWireType.Sint, out ReadOnlySpan<byte> val, out _)
            && OmniBufDecoder.TryDecodeVarint(val, out ulong u, out int n)
            && n == val.Length)
        {
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
        if (LocateFieldMask(packet, fieldNum, (ushort)(1u << (byte)expectedWt), out ReadOnlySpan<byte> val, out _)
            && val.Length == 4)
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
        if (LocateFieldMask(packet, fieldNum, (ushort)(1u << (byte)expectedWt), out ReadOnlySpan<byte> val, out _)
            && val.Length == 8)
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
            if (OmniBufDecoder.TryDecodeVarint(val, out ulong len, out int ln)
                && ln + (int)len == val.Length)
            {
                value = OmniBufConstants.DecodeUtf8Strict(val.Slice(ln, (int)len));
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    public static bool TryReadBytes(ReadOnlySpan<byte> packet, uint fieldNum, out byte[] value)
    {
        if (LocateFieldMask(packet, fieldNum, 1 << (byte)OmniBufWireType.Bytes, out ReadOnlySpan<byte> val, out _)
            && OmniBufDecoder.TryDecodeVarint(val, out ulong len, out int ln)
            && ln + (int)len == val.Length)
        {
            value = val.Slice(ln, (int)len).ToArray();
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
                value = string.Empty;
                return false;
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                value = string.Empty;
                return false;
            }

            if (fnum == subMsgField && wt == OmniBufWireType.SubMsg)
            {
                if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong subLen, out int sn)
                    || subLen > (ulong)(packet.Length - pos - sn))
                {
                    value = string.Empty;
                    return false;
                }

                ReadOnlySpan<byte> sub = packet.Slice(pos + sn, (int)subLen);
                pos += sn + (int)subLen;

                if (LocateFieldMask(sub, targetField, 1 << (byte)OmniBufWireType.String, out ReadOnlySpan<byte> strField, out _)
                    && OmniBufDecoder.TryDecodeVarint(strField, out ulong sLen, out int sln)
                    && sln + (int)sLen == strField.Length)
                {
                    lastBytes = strField.Slice(sln, (int)sLen);
                    found = true;
                }

                continue;
            }

            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                value = string.Empty;
                return false;
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
                subMsgBytes = Array.Empty<byte>();
                return false;
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                subMsgBytes = Array.Empty<byte>();
                return false;
            }

            if (fnum == fieldNum && wt == OmniBufWireType.SubMsg)
            {
                if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong subLen, out int sn)
                    || subLen > (ulong)(packet.Length - pos - sn))
                {
                    subMsgBytes = Array.Empty<byte>();
                    return false;
                }

                ReadOnlySpan<byte> body = packet.Slice(pos + sn, (int)subLen);
                pos += sn + (int)subLen;
                matchCount++;
                if (matchCount == 1)
                {
                    singleSlice = body;
                }
                else
                {
                    if (merged is null)
                    {
                        merged = new List<byte>(singleSlice.Length + body.Length);
                        merged.AddRange(singleSlice.ToArray());
                    }

                    merged.AddRange(body.ToArray());
                }

                continue;
            }

            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                subMsgBytes = Array.Empty<byte>();
                return false;
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

    public static List<string> ReadStringArray(ReadOnlySpan<byte> packet, uint fieldNum)
    {
        var result = new List<string>();
        int pos = 0;
        while (pos < packet.Length)
        {
            if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong tag, out int tn)
                || (tag >> 4) > OmniBufConstants.MaxFieldNumber)
            {
                return new List<string>();
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                return new List<string>();
            }

            if (fnum == fieldNum && wt == OmniBufWireType.Array)
            {
                if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong bLen, out int bn)
                    || bLen < 2
                    || bLen > (ulong)(packet.Length - pos - bn))
                {
                    return new List<string>();
                }

                ReadOnlySpan<byte> body = packet.Slice(pos + bn, (int)bLen);
                pos += bn + (int)bLen;
                if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p)
                    || elemWt != OmniBufWireType.String)
                {
                    return new List<string>();
                }

                for (int i = 0; i < count; i++)
                {
                    string s = OmniBufDecoder.ReadString(body, ref p);
                    result.Add(s);
                }

                if (p != body.Length)
                {
                    return new List<string>();
                }

                continue;
            }

            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                return new List<string>();
            }
        }

        return result;
    }

    public static List<uint> ReadUint32Array(ReadOnlySpan<byte> packet, uint fieldNum)
    {
        var result = new List<uint>();
        int pos = 0;
        while (pos < packet.Length)
        {
            if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong tag, out int tn)
                || (tag >> 4) > OmniBufConstants.MaxFieldNumber)
            {
                return new List<uint>();
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                return new List<uint>();
            }

            if (fnum == fieldNum && wt == OmniBufWireType.Array)
            {
                if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong bLen, out int bn)
                    || bLen < 2
                    || bLen > (ulong)(packet.Length - pos - bn))
                {
                    return new List<uint>();
                }

                ReadOnlySpan<byte> body = packet.Slice(pos + bn, (int)bLen);
                pos += bn + (int)bLen;
                if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p)
                    || elemWt != OmniBufWireType.Uint)
                {
                    return new List<uint>();
                }

                for (int i = 0; i < count; i++)
                {
                    ulong v = OmniBufDecoder.ReadVarint(body, ref p);
                    result.Add((uint)v);
                }

                if (p != body.Length)
                {
                    return new List<uint>();
                }

                continue;
            }

            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                return new List<uint>();
            }
        }

        return result;
    }

    public static List<byte[]> ReadSubMsgArrayPayloads(ReadOnlySpan<byte> packet, uint fieldNum)
    {
        var result = new List<byte[]>();
        int pos = 0;
        while (pos < packet.Length)
        {
            if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong tag, out int tn)
                || (tag >> 4) > OmniBufConstants.MaxFieldNumber)
            {
                return new List<byte[]>();
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                return new List<byte[]>();
            }

            if (fnum == fieldNum && wt == OmniBufWireType.Array)
            {
                if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong bLen, out int bn)
                    || bLen < 2
                    || bLen > (ulong)(packet.Length - pos - bn))
                {
                    return new List<byte[]>();
                }

                ReadOnlySpan<byte> body = packet.Slice(pos + bn, (int)bLen);
                pos += bn + (int)bLen;
                if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p)
                    || elemWt != OmniBufWireType.SubMsg)
                {
                    return new List<byte[]>();
                }

                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);
                    result.Add(sub.ToArray());
                }

                if (p != body.Length)
                {
                    return new List<byte[]>();
                }

                continue;
            }

            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                return new List<byte[]>();
            }
        }

        return result;
    }

    public static Dictionary<string, ulong> ReadStringUint64Map(ReadOnlySpan<byte> packet, uint fieldNum)
    {
        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        int pos = 0;
        while (pos < packet.Length)
        {
            if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong tag, out int tn)
                || (tag >> 4) > OmniBufConstants.MaxFieldNumber)
            {
                return new Dictionary<string, ulong>(StringComparer.Ordinal);
            }

            pos += tn;
            uint fnum = (uint)(tag >> 4);
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                return new Dictionary<string, ulong>(StringComparer.Ordinal);
            }

            if (fnum == fieldNum && wt == OmniBufWireType.Map)
            {
                if (!OmniBufDecoder.TryDecodeVarint(packet.Slice(pos), out ulong bLen, out int bn)
                    || bLen < 3
                    || bLen > (ulong)(packet.Length - pos - bn))
                {
                    return new Dictionary<string, ulong>(StringComparer.Ordinal);
                }

                ReadOnlySpan<byte> body = packet.Slice(pos + bn, (int)bLen);
                pos += bn + (int)bLen;
                if (!OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType kWt, out OmniBufWireType vWt, out int count, out int p)
                    || kWt != OmniBufWireType.String
                    || vWt != OmniBufWireType.Uint)
                {
                    return new Dictionary<string, ulong>(StringComparer.Ordinal);
                }

                for (int i = 0; i < count; i++)
                {
                    string k = OmniBufDecoder.ReadString(body, ref p);
                    ulong v = OmniBufDecoder.ReadVarint(body, ref p);
                    result[k] = v;
                }

                if (p != body.Length)
                {
                    return new Dictionary<string, ulong>(StringComparer.Ordinal);
                }

                continue;
            }

            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                return new Dictionary<string, ulong>(StringComparer.Ordinal);
            }
        }

        return result;
    }

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
        Action<TState, OmniBufEncoder, uint> appendDirtyField)
    {
        ArgumentNullException.ThrowIfNull(appendDirtyField);

        if (dirtyMaskLow == 0 && (dirtyOverflow is null || dirtyOverflow.Count == 0))
        {
            return packet.ToArray();
        }

        OmniBufEncoder enc = OmniBufEncoder.RentThreadLocal(out bool rented);
        try
        {
            SpliceFieldsTo(enc, packet, dirtyMaskLow, dirtyOverflow, state, appendDirtyField);
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
        Action<TState, OmniBufEncoder, uint> appendDirtyField)
    {
        ArgumentNullException.ThrowIfNull(enc);
        ArgumentNullException.ThrowIfNull(appendDirtyField);

        if (dirtyMaskLow == 0 && (dirtyOverflow is null || dirtyOverflow.Count == 0))
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
            if (fnum == 0 && wt != OmniBufWireType.SubMsg)
            {
                throw new OmniBufException("omnibuf: invalid Field #0 wire type during SpliceFields");
            }

            if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                throw new OmniBufException($"omnibuf: truncated field #{fnum} during SpliceFields");
            }

            if (IsFieldDirty(fnum, dirtyMaskLow, dirtyOverflow))
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
    /// Splices modified fields while preserving <paramref name="prefix"/> at the start of the output buffer,
    /// even when <paramref name="prefix"/> and <paramref name="packet"/> alias the same backing array.
    /// Never mutates <paramref name="packet"/>.
    /// </summary>
    public static byte[] SpliceFieldsWithPrefix(
        ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> packet,
        ulong dirtyMaskLow,
        HashSet<uint>? dirtyOverflow,
        Action<OmniBufEncoder, uint> appendDirtyField)
    {
        ArgumentNullException.ThrowIfNull(appendDirtyField);

        if (dirtyMaskLow == 0 && (dirtyOverflow is null || dirtyOverflow.Count == 0))
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
                if (fnum == 0 && wt != OmniBufWireType.SubMsg)
                {
                    throw new OmniBufException("omnibuf: invalid Field #0 wire type during SpliceFields");
                }

                if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
                {
                    throw new OmniBufException($"omnibuf: truncated field #{fnum} during SpliceFields");
                }

                bool isDirty = IsFieldDirty(fnum, dirtyMaskLow, dirtyOverflow);
                if (isDirty)
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

            // Emit dirty low fields (1..63) in ascending order.
            ulong mask = dirtyMaskLow & ~1UL;
            while (mask != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(mask);
                appendDirtyField(enc, (uint)bit);
                mask &= mask - 1;
            }

            // Emit dirty overflow fields (>= 64) in ascending order.
            if (dirtyOverflow is { Count: > 0 })
            {
                var sorted = new List<uint>(dirtyOverflow);
                sorted.Sort();
                foreach (uint fnum in sorted)
                {
                    if (fnum >= 64)
                    {
                        appendDirtyField(enc, fnum);
                    }
                }
            }

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
}
