using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OmniBuf;

/// <summary>
/// Schema-less decoder and deterministic UTF-8 JSON serializer for arbitrary OmniBuf packets.
/// </summary>
public static class OmniBufBlind
{
    private sealed class BlindSchema
    {
        public SchemaMeta? Inherited { get; init; }
        public SchemaMeta? Local { get; init; }

        public string FieldName(uint fieldNum)
        {
            if (Local is not null && Local.Fields.TryGetValue(fieldNum, out string? localName) && !string.IsNullOrEmpty(localName))
            {
                return localName;
            }

            if (Inherited is not null && Inherited.Fields.TryGetValue(fieldNum, out string? inhName) && !string.IsNullOrEmpty(inhName))
            {
                return inhName;
            }

            return $"field_{fieldNum}";
        }

        public BlindSchema? Child(uint fieldNum)
        {
            SchemaMeta? inhChild = null;
            SchemaMeta? locChild = null;
            Inherited?.Children.TryGetValue(fieldNum, out inhChild);
            Local?.Children.TryGetValue(fieldNum, out locChild);
            if (inhChild is null && locChild is null)
            {
                return null;
            }

            return new BlindSchema { Inherited = inhChild, Local = locChild };
        }
    }

    /// <summary>
    /// Decodes an arbitrary OmniBuf packet into a nested dictionary using wire tags and any embedded Field #0 metadata.
    /// Values use <see cref="ulong"/>, <see cref="long"/>, <see cref="bool"/>, <see cref="float"/>,
    /// <see cref="double"/>, <see cref="string"/>, <see cref="byte"/>[], <see cref="List{T}"/>, and <see cref="Dictionary{TKey,TValue}"/>.
    /// </summary>
    public static Dictionary<string, object?> DecodeBlind(
        ReadOnlySpan<byte> packet,
        int maxDepth = OmniBufConstants.MaxNestingDepth)
    {
        return DecodeMessage(packet, schema: null, depth: 0, maxDepth: maxDepth);
    }

    /// <summary>
    /// Decodes an arbitrary OmniBuf packet and formats it as canonical JSON using <see cref="Utf8JsonWriter"/>.
    /// When <paramref name="fallbackOnNonFinite"/> is true (used by TIER_BLIND conformance), messages containing
    /// NaN or Infinity return "{}" because RFC 8259 JSON cannot represent non-finite IEEE-754 floats.
    /// </summary>
    public static string DecodeBlindJson(
        ReadOnlySpan<byte> packet,
        bool fallbackOnNonFinite = true,
        int maxDepth = OmniBufConstants.MaxNestingDepth)
    {
        Dictionary<string, object?> decoded = DecodeBlind(packet, maxDepth);
        if (ContainsNonFiniteFloat(decoded))
        {
            if (fallbackOnNonFinite)
            {
                return "{}";
            }

            throw new OmniBufException("omnibuf: cannot format NaN or Infinity as strict JSON");
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteJsonValue(writer, decoded);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static Dictionary<string, object?> DecodeMessage(
        ReadOnlySpan<byte> data,
        BlindSchema? schema,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth)
        {
            throw new OmniBufException($"omnibuf: blind decode depth {depth} exceeds maximum {maxDepth}");
        }

        // Pass 1: Pre-scan all Field #0 (SchemaMeta) blocks in this message so field names apply regardless of position.
        SchemaMeta? localMeta = null;
        int scanPos = 0;
        while (scanPos < data.Length)
        {
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref scanPos);
            if (fnum == 0)
            {
                ReadOnlySpan<byte> metaBody = OmniBufDecoder.ReadLenSlice(data, ref scanPos, 0);
                SchemaMeta decodedMeta = SchemaMeta.Decode(metaBody, depth + 1, maxDepth);
                localMeta = SchemaMeta.Merge(localMeta, decodedMeta);
            }
            else if (!OmniBufDecoder.TryFastSkipValue(data, ref scanPos, wt))
            {
                throw new OmniBufException($"omnibuf: truncated or invalid field #{fnum} at {scanPos}");
            }
        }

        BlindSchema? activeSchema = localMeta is not null
            ? new BlindSchema { Inherited = schema?.Inherited, Local = SchemaMeta.Merge(schema?.Local, localMeta) }
            : schema;

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var nameOwner = new Dictionary<string, uint>(StringComparer.Ordinal);
        var fieldShapes = new Dictionary<uint, uint>();
        Dictionary<uint, List<byte>>? pendingSubMsgs = null;
        List<uint>? pendingSubMsgOrder = null;

        // Pass 2: Decode every field, enforcing name collision and wire-shape consistency across concatenated segments.
        int pos = 0;
        while (pos < data.Length)
        {
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
            if (fnum == 0)
            {
                _ = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
                continue;
            }

            string key = activeSchema is not null ? activeSchema.FieldName(fnum) : $"field_{fnum}";
            if (nameOwner.TryGetValue(key, out uint prevOwner) && prevOwner != fnum)
            {
                throw new OmniBufException($"omnibuf: field name collision between #{prevOwner} and #{fnum} on '{key}'");
            }

            nameOwner[key] = fnum;

            uint shape = ReadFieldShape(data, pos, wt);
            if (fieldShapes.TryGetValue(fnum, out uint prevShape) && prevShape != shape)
            {
                throw new OmniBufException($"omnibuf: field #{fnum} changed wire shape across concatenated occurrences");
            }

            fieldShapes[fnum] = shape;

            if (wt == OmniBufWireType.SubMsg)
            {
                ReadOnlySpan<byte> subSlice = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
                pendingSubMsgs ??= new Dictionary<uint, List<byte>>();
                pendingSubMsgOrder ??= new List<uint>();
                if (!pendingSubMsgs.TryGetValue(fnum, out List<byte>? acc))
                {
                    acc = new List<byte>(subSlice.Length);
                    pendingSubMsgs[fnum] = acc;
                    pendingSubMsgOrder.Add(fnum);
                }

                acc.AddRange(subSlice.ToArray());
                continue;
            }

            object? val = DecodeValue(data, ref pos, wt, activeSchema?.Child(fnum), depth, maxDepth);
            if (wt == OmniBufWireType.Array && result.TryGetValue(key, out object? prevArrObj) && prevArrObj is List<object?> prevList && val is List<object?> nextList)
            {
                prevList.AddRange(nextList);
            }
            else if (wt == OmniBufWireType.Map && result.TryGetValue(key, out object? prevMapObj) && prevMapObj is Dictionary<string, object?> prevMap && val is Dictionary<string, object?> nextMap)
            {
                foreach (KeyValuePair<string, object?> kv in nextMap)
                {
                    prevMap[kv.Key] = kv.Value;
                }
            }
            else
            {
                result[key] = val;
            }
        }

        if (pendingSubMsgOrder is not null && pendingSubMsgs is not null)
        {
            foreach (uint fnum in pendingSubMsgOrder)
            {
                string key = activeSchema is not null ? activeSchema.FieldName(fnum) : $"field_{fnum}";
                byte[] mergedSub = pendingSubMsgs[fnum].ToArray();
                result[key] = DecodeMessage(mergedSub, activeSchema?.Child(fnum), depth + 1, maxDepth);
            }
        }

        return result;
    }

    private static uint ReadFieldShape(ReadOnlySpan<byte> data, int pos, OmniBufWireType wt)
    {
        OmniBufWireType norm = wt == OmniBufWireType.BoolTrue ? OmniBufWireType.BoolFalse : wt;
        if (norm == OmniBufWireType.Array)
        {
            int p = pos;
            ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref p, 2);
            if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out _, out _))
            {
                throw new OmniBufException("omnibuf: invalid WireArray header");
            }

            return (uint)OmniBufWireType.Array | ((uint)elemWt << 8);
        }

        if (norm == OmniBufWireType.Map)
        {
            int p = pos;
            ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref p, 3);
            if (!OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType keyWt, out OmniBufWireType valWt, out _, out _))
            {
                throw new OmniBufException("omnibuf: invalid WireMap header");
            }

            return (uint)OmniBufWireType.Map | ((uint)keyWt << 8) | ((uint)valWt << 16);
        }

        return (uint)norm;
    }

    private static object? DecodeValue(
        ReadOnlySpan<byte> data,
        ref int pos,
        OmniBufWireType wt,
        BlindSchema? childSchema,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth)
        {
            throw new OmniBufException($"omnibuf: blind decode depth {depth} exceeds maximum {maxDepth}");
        }

        switch (wt)
        {
            case OmniBufWireType.Uint:
                return OmniBufDecoder.ReadVarint(data, ref pos);

            case OmniBufWireType.Sint:
                return OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos));

            case OmniBufWireType.BoolFalse:
                return false;

            case OmniBufWireType.BoolTrue:
                return true;

            case OmniBufWireType.Fixed32:
                return (ulong)OmniBufDecoder.ReadFixed32(data, ref pos);

            case OmniBufWireType.Sfixed32:
                return (long)(int)OmniBufDecoder.ReadFixed32(data, ref pos);

            case OmniBufWireType.Float32:
                return BitConverter.UInt32BitsToSingle(OmniBufDecoder.ReadFixed32(data, ref pos));

            case OmniBufWireType.Fixed64:
                return OmniBufDecoder.ReadFixed64(data, ref pos);

            case OmniBufWireType.Sfixed64:
                return (long)OmniBufDecoder.ReadFixed64(data, ref pos);

            case OmniBufWireType.Float64:
                return BitConverter.UInt64BitsToDouble(OmniBufDecoder.ReadFixed64(data, ref pos));

            case OmniBufWireType.String:
                return OmniBufDecoder.ReadString(data, ref pos);

            case OmniBufWireType.Bytes:
                return OmniBufDecoder.ReadLenSlice(data, ref pos, 0).ToArray();

            case OmniBufWireType.SubMsg:
                {
                    ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
                    return DecodeMessage(sub, childSchema, depth + 1, maxDepth);
                }

            case OmniBufWireType.Array:
                {
                    if (depth + 1 > maxDepth)
                    {
                        throw new OmniBufException($"omnibuf: WireArray depth {depth + 1} exceeds maximum {maxDepth}");
                    }

                    ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 2);
                    if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p))
                    {
                        throw new OmniBufException("omnibuf: invalid WireArray header in DecodeBlind");
                    }

                    var items = new List<object?>(Math.Min(count, 4096));
                    for (int i = 0; i < count; i++)
                    {
                        if (elemWt == OmniBufWireType.BoolFalse)
                        {
                            items.Add(OmniBufDecoder.ReadBoolElement(body, ref p));
                        }
                        else
                        {
                            items.Add(DecodeValue(body, ref p, elemWt, childSchema, depth + 1, maxDepth));
                        }
                    }

                    if (p != body.Length)
                    {
                        throw new OmniBufException("omnibuf: trailing bytes in WireArray");
                    }

                    return items;
                }

            case OmniBufWireType.Map:
                {
                    if (depth + 1 > maxDepth)
                    {
                        throw new OmniBufException($"omnibuf: WireMap depth {depth + 1} exceeds maximum {maxDepth}");
                    }

                    ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 3);
                    if (!OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType keyWt, out OmniBufWireType valWt, out int count, out int p))
                    {
                        throw new OmniBufException("omnibuf: invalid WireMap header in DecodeBlind");
                    }

                    var map = new Dictionary<string, object?>(Math.Min(count, 4096), StringComparer.Ordinal);
                    for (int i = 0; i < count; i++)
                    {
                        object? keyObj = keyWt == OmniBufWireType.BoolFalse
                            ? OmniBufDecoder.ReadBoolElement(body, ref p)
                            : DecodeValue(body, ref p, keyWt, null, depth + 1, maxDepth);
                        string keyStr = FormatMapKey(keyObj);

                        object? valObj = valWt == OmniBufWireType.BoolFalse
                            ? OmniBufDecoder.ReadBoolElement(body, ref p)
                            : DecodeValue(body, ref p, valWt, childSchema, depth + 1, maxDepth);
                        map[keyStr] = valObj;
                    }

                    if (p != body.Length)
                    {
                        throw new OmniBufException("omnibuf: trailing bytes in WireMap");
                    }

                    return map;
                }

            case OmniBufWireType.Extended:
                // DecodeBlind cannot represent unknown WireExtended payloads without schema knowledge.
                _ = OmniBufDecoder.ReadLenSlice(data, ref pos, 1);
                throw new OmniBufException("omnibuf: unsupported WireExtended in DecodeBlind");

            default:
                throw new OmniBufException($"omnibuf: unsupported wire type {(byte)wt}");
        }
    }

    private static string FormatMapKey(object? keyObj) => keyObj switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        ulong u => u.ToString(CultureInfo.InvariantCulture),
        long i => i.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(keyObj, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static bool ContainsNonFiniteFloat(object? value) => value switch
    {
        float f => !float.IsFinite(f),
        double d => !double.IsFinite(d),
        List<object?> list => list.Exists(ContainsNonFiniteFloat),
        Dictionary<string, object?> dict => dict.Values.Any(ContainsNonFiniteFloat),
        _ => false,
    };

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;

            case bool b:
                writer.WriteBooleanValue(b);
                break;

            case ulong u:
                writer.WriteNumberValue(u);
                break;

            case long i:
                writer.WriteNumberValue(i);
                break;

            case float f:
                if (f == 0f && float.IsNegative(f))
                {
                    writer.WriteRawValue("-0"u8);
                }
                else
                {
                    writer.WriteNumberValue(f);
                }

                break;

            case double d:
                if (d == 0d && double.IsNegative(d))
                {
                    writer.WriteRawValue("-0"u8);
                }
                else
                {
                    writer.WriteNumberValue(d);
                }

                break;

            case string s:
                writer.WriteStringValue(s);
                break;

            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;

            case List<object?> list:
                writer.WriteStartArray();
                foreach (object? item in list)
                {
                    WriteJsonValue(writer, item);
                }

                writer.WriteEndArray();
                break;

            case Dictionary<string, object?> dict:
                writer.WriteStartObject();
                var keys = new List<string>(dict.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string k in keys)
                {
                    writer.WritePropertyName(k);
                    WriteJsonValue(writer, dict[k]);
                }

                writer.WriteEndObject();
                break;

            default:
                throw new OmniBufException($"omnibuf: unsupported blind JSON value type {value.GetType()}");
        }
    }
}
