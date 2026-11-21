using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
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
        public SchemaMeta? Local { get; init; }
        public BlindSchema? Parent { get; init; }

        public string FieldName(uint fieldNum)
        {
            for (BlindSchema? s = this; s is not null; s = s.Parent)
            {
                if (s.Local is not null && s.Local.Fields.TryGetValue(fieldNum, out string? name))
                {
                    return string.IsNullOrEmpty(name) ? $"field_{fieldNum}" : name;
                }
            }

            return $"field_{fieldNum}";
        }

        public BlindSchema? Child(uint fieldNum)
        {
            BlindSchema? parentChild = Parent?.Child(fieldNum);
            SchemaMeta? localChild = null;
            Local?.Children.TryGetValue(fieldNum, out localChild);
            if (parentChild is null && localChild is null)
            {
                return null;
            }

            return localChild is not null
                ? new BlindSchema { Local = localChild, Parent = parentChild }
                : parentChild;
        }
    }

    private struct SubMsgAccum
    {
        public int FirstOffset;
        public int FirstLength;
        public List<byte>? Merged;
    }

    private sealed record ContainerShape(
        OmniBufWireType Kind,
        OmniBufWireType KeyOrElem,
        OmniBufWireType Val,
        ContainerShape? Inner);

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
    /// By default (<paramref name="fallbackOnNonFinite"/> = false), packets containing NaN or Infinity throw
    /// <see cref="OmniBufException"/> because RFC 8259 JSON cannot represent non-finite IEEE-754 floats.
    /// When <paramref name="fallbackOnNonFinite"/> is true (used by TIER_BLIND conformance), "{}" is returned instead.
    /// </summary>
    public static string DecodeBlindJson(
        ReadOnlySpan<byte> packet,
        bool fallbackOnNonFinite = false,
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
            ? new BlindSchema { Local = localMeta, Parent = schema }
            : schema;

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var nameOwner = new Dictionary<string, uint>(StringComparer.Ordinal);
        var fieldShapes = new Dictionary<uint, uint>();
        Dictionary<uint, ContainerShape>? nestedContainerShapes = null;
        Dictionary<uint, SubMsgAccum>? pendingSubMsgs = null;
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

            uint shape = ReadFieldShape(data, pos, wt, depth, maxDepth, out ContainerShape? nestedShape);
            if (fieldShapes.TryGetValue(fnum, out uint prevShape) && prevShape != shape)
            {
                throw new OmniBufException($"omnibuf: field #{fnum} changed wire shape across concatenated occurrences");
            }

            fieldShapes[fnum] = shape;

            if (nestedShape is not null)
            {
                nestedContainerShapes ??= new Dictionary<uint, ContainerShape>();
                if (nestedContainerShapes.TryGetValue(fnum, out ContainerShape? prevNested))
                {
                    if (!TryUnifyContainerShape(prevNested, nestedShape, out ContainerShape? unified))
                    {
                        throw new OmniBufException($"omnibuf: field #{fnum} changed nested container wire shape across concatenated occurrences");
                    }

                    nestedContainerShapes[fnum] = unified!;
                }
                else
                {
                    nestedContainerShapes[fnum] = nestedShape;
                }
            }

            if (wt == OmniBufWireType.SubMsg)
            {
                ulong subByteLen = OmniBufDecoder.ReadVarint(data, ref pos);
                if (subByteLen > (ulong)(data.Length - pos))
                {
                    throw new OmniBufException($"omnibuf: length-prefixed payload ({subByteLen}B) out of bounds at {pos}");
                }

                int subLen = (int)subByteLen;
                int subOffset = pos;
                pos += subLen;

                pendingSubMsgs ??= new Dictionary<uint, SubMsgAccum>();
                pendingSubMsgOrder ??= new List<uint>();
                if (!pendingSubMsgs.TryGetValue(fnum, out SubMsgAccum acc))
                {
                    pendingSubMsgs[fnum] = new SubMsgAccum
                    {
                        FirstOffset = subOffset,
                        FirstLength = subLen,
                        Merged = null,
                    };
                    pendingSubMsgOrder.Add(fnum);
                }
                else
                {
                    ReadOnlySpan<byte> nextSeg = data.Slice(subOffset, subLen);
                    if (acc.Merged is null)
                    {
                        ReadOnlySpan<byte> firstSeg = data.Slice(acc.FirstOffset, acc.FirstLength);
                        OmniBufDecoder.ValidateMessage(firstSeg, depth + 1, maxDepth);
                        OmniBufDecoder.ValidateMessage(nextSeg, depth + 1, maxDepth);
                        acc.Merged = new List<byte>(acc.FirstLength + subLen);
                        AppendSpanToList(acc.Merged, firstSeg);
                    }
                    else
                    {
                        OmniBufDecoder.ValidateMessage(nextSeg, depth + 1, maxDepth);
                    }

                    AppendSpanToList(acc.Merged, nextSeg);
                    pendingSubMsgs[fnum] = acc;
                }

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
                SubMsgAccum acc = pendingSubMsgs[fnum];
                ReadOnlySpan<byte> subSpan = acc.Merged is null
                    ? data.Slice(acc.FirstOffset, acc.FirstLength)
                    : CollectionsMarshal.AsSpan(acc.Merged);
                result[key] = DecodeMessage(subSpan, activeSchema?.Child(fnum), depth + 1, maxDepth);
            }
        }

        return result;
    }

    private static void AppendSpanToList(List<byte> dst, ReadOnlySpan<byte> src)
    {
        if (src.IsEmpty)
        {
            return;
        }

        int oldCount = dst.Count;
        CollectionsMarshal.SetCount(dst, oldCount + src.Length);
        src.CopyTo(CollectionsMarshal.AsSpan(dst).Slice(oldCount, src.Length));
    }

    private static uint ReadFieldShape(
        ReadOnlySpan<byte> data,
        int pos,
        OmniBufWireType wt,
        int depth,
        int maxDepth,
        out ContainerShape? nestedShape)
    {
        nestedShape = null;
        OmniBufWireType norm = wt == OmniBufWireType.BoolTrue ? OmniBufWireType.BoolFalse : wt;
        if (norm == OmniBufWireType.Array)
        {
            int p = pos;
            ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref p, 2);
            if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out _, out _))
            {
                throw new OmniBufException("omnibuf: invalid WireArray header");
            }

            if (elemWt is OmniBufWireType.Array or OmniBufWireType.Map)
            {
                nestedShape = InspectContainerBodyShape(body, OmniBufWireType.Array, depth + 1, maxDepth);
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

            if (valWt is OmniBufWireType.Array or OmniBufWireType.Map)
            {
                nestedShape = InspectContainerBodyShape(body, OmniBufWireType.Map, depth + 1, maxDepth);
            }

            return (uint)OmniBufWireType.Map | ((uint)keyWt << 8) | ((uint)valWt << 16);
        }

        return (uint)norm;
    }

    private static ContainerShape InspectContainerBodyShape(
        ReadOnlySpan<byte> body,
        OmniBufWireType containerKind,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth)
        {
            throw new OmniBufException($"omnibuf: container nesting depth {depth} exceeds maximum {maxDepth}");
        }

        if (containerKind == OmniBufWireType.Array)
        {
            if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p))
            {
                throw new OmniBufException("omnibuf: invalid WireArray header in DecodeBlind");
            }

            ContainerShape? unifiedInner = null;
            if (elemWt is OmniBufWireType.Array or OmniBufWireType.Map)
            {
                int minChildLen = elemWt == OmniBufWireType.Array ? 2 : 3;
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> childBody = OmniBufDecoder.ReadLenSlice(body, ref p, minChildLen);
                    ContainerShape elemShape = InspectContainerBodyShape(childBody, elemWt, depth + 1, maxDepth);
                    if (unifiedInner is null)
                    {
                        unifiedInner = elemShape;
                    }
                    else if (!TryUnifyContainerShape(unifiedInner, elemShape, out unifiedInner))
                    {
                        throw new OmniBufException("omnibuf: conflicting nested container element wire types in WireArray");
                    }
                }

                if (p != body.Length)
                {
                    throw new OmniBufException("omnibuf: trailing bytes in WireArray");
                }
            }

            return new ContainerShape(OmniBufWireType.Array, elemWt, default, unifiedInner);
        }

        if (!OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType keyWt, out OmniBufWireType valWt, out int mapCount, out int mp))
        {
            throw new OmniBufException("omnibuf: invalid WireMap header in DecodeBlind");
        }

        ContainerShape? unifiedMapInner = null;
        if (valWt is OmniBufWireType.Array or OmniBufWireType.Map)
        {
            int minChildLen = valWt == OmniBufWireType.Array ? 2 : 3;
            for (int i = 0; i < mapCount; i++)
            {
                if (keyWt == OmniBufWireType.BoolFalse)
                {
                    _ = OmniBufDecoder.ReadBoolElement(body, ref mp);
                }
                else
                {
                    OmniBufDecoder.ValidateValue(body, ref mp, keyWt, depth + 1, maxDepth);
                }

                ReadOnlySpan<byte> childBody = OmniBufDecoder.ReadLenSlice(body, ref mp, minChildLen);
                ContainerShape valShape = InspectContainerBodyShape(childBody, valWt, depth + 1, maxDepth);
                if (unifiedMapInner is null)
                {
                    unifiedMapInner = valShape;
                }
                else if (!TryUnifyContainerShape(unifiedMapInner, valShape, out unifiedMapInner))
                {
                    throw new OmniBufException("omnibuf: conflicting nested container value wire types in WireMap");
                }
            }

            if (mp != body.Length)
            {
                throw new OmniBufException("omnibuf: trailing bytes in WireMap");
            }
        }

        return new ContainerShape(OmniBufWireType.Map, keyWt, valWt, unifiedMapInner);
    }

    private static bool TryUnifyContainerShape(
        ContainerShape a,
        ContainerShape b,
        out ContainerShape? unified)
    {
        if (a.Kind != b.Kind || a.KeyOrElem != b.KeyOrElem || a.Val != b.Val)
        {
            unified = null;
            return false;
        }

        if (a.Inner is null)
        {
            unified = a with { Inner = b.Inner };
            return true;
        }

        if (b.Inner is null)
        {
            unified = a;
            return true;
        }

        if (!TryUnifyContainerShape(a.Inner, b.Inner, out ContainerShape? innerUnified))
        {
            unified = null;
            return false;
        }

        unified = a with { Inner = innerUnified };
        return true;
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
                keys.Sort(Utf8OrdinalStringComparer.Instance);
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
