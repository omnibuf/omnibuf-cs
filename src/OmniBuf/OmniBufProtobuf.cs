using System.Collections;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace OmniBuf;

/// <summary>
/// Descriptor-based OmniBuf serializer for standard <c>protoc --csharp_out</c> <see cref="IMessage"/> types.
/// </summary>
public static class OmniBufProtobuf
{
    public static byte[] Marshal(IMessage message, MarshalOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        OmniBufEncoder encoder = OmniBufEncoder.RentThreadLocal(out bool rented);
        try
        {
            Marshal(encoder, message, options);
            return encoder.ToArray();
        }
        finally
        {
            OmniBufEncoder.ReturnThreadLocal(encoder, rented);
        }
    }

    public static void Marshal(OmniBufEncoder encoder, IMessage message, MarshalOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(message);

        if (options.SelfDescribing)
        {
            SchemaMeta? meta = BuildSchemaMeta(message.Descriptor, depth: 0, active: null);
            if (meta is not null)
            {
                SchemaMeta.WriteField0(encoder, meta, options.Deterministic);
            }
        }

        EncodeMessage(encoder, message, options.Deterministic);
    }

    public static void Unmarshal(ReadOnlySpan<byte> data, IMessage target)
    {
        ArgumentNullException.ThrowIfNull(target);
        DecodeMessage(data, target, depth: 0, maxDepth: OmniBufConstants.MaxNestingDepth, merge: false);
    }

    public static T Unmarshal<T>(ReadOnlySpan<byte> data)
        where T : IMessage, new()
    {
        var msg = new T();
        Unmarshal(data, msg);
        return msg;
    }

    private static SchemaMeta? BuildSchemaMeta(MessageDescriptor? md, int depth, HashSet<string>? active)
    {
        if (md is null || depth > OmniBufConstants.MaxNestingDepth)
        {
            return null;
        }

        active ??= new HashSet<string>(StringComparer.Ordinal);
        if (!active.Add(md.FullName))
        {
            return null;
        }

        try
        {
            IList<FieldDescriptor> fields = md.Fields.InFieldNumberOrder();
            var meta = new SchemaMeta
            {
                TypeName = md.Name,
                Fields = new Dictionary<uint, string>(fields.Count),
            };

            foreach (FieldDescriptor fd in fields)
            {
                uint fnum = (uint)fd.FieldNumber;
                meta.Fields[fnum] = fd.Name;

                MessageDescriptor? childMd = null;
                int childDepth = depth + 1;
                if (fd.IsMap)
                {
                    FieldDescriptor valFd = fd.MessageType.FindFieldByNumber(2);
                    if (valFd.FieldType == FieldType.Message)
                    {
                        childMd = valFd.MessageType;
                        childDepth = depth + 2;
                    }
                }
                else if (fd.FieldType == FieldType.Message)
                {
                    childMd = fd.MessageType;
                    if (fd.IsRepeated)
                    {
                        childDepth = depth + 2;
                    }
                }

                if (childMd is not null)
                {
                    SchemaMeta? childMeta = BuildSchemaMeta(childMd, childDepth, active);
                    if (childMeta is not null)
                    {
                        meta.Children[fnum] = childMeta;
                    }
                }
            }

            return meta;
        }
        finally
        {
            active.Remove(md.FullName);
        }
    }

    private static void EncodeMessage(OmniBufEncoder encoder, IMessage message, bool deterministic)
    {
        IList<FieldDescriptor> fields = message.Descriptor.Fields.InFieldNumberOrder();
        for (int i = 0; i < fields.Count; i++)
        {
            FieldDescriptor fd = fields[i];
            uint fnum = (uint)fd.FieldNumber;

            if (fd.HasPresence && !fd.Accessor.HasValue(message))
            {
                continue;
            }

            object? val = fd.Accessor.GetValue(message);

            if (fd.IsMap)
            {
                FieldDescriptor keyFd = fd.MessageType.FindFieldByNumber(1);
                FieldDescriptor valFd = fd.MessageType.FindFieldByNumber(2);
                OmniBufWireType keyWt = ScalarWireType(keyFd.FieldType);
                OmniBufWireType valWt = ScalarWireType(valFd.FieldType);

                var dict = (IDictionary)val!;
                int count = dict.Count;
                encoder.BeginMapField(fnum, keyWt, valWt, count);
                if (count > 0)
                {
                    if (deterministic && count > 1)
                    {
                        var keys = new object[count];
                        dict.Keys.CopyTo(keys, 0);
                        SortMapKeys(keys, keyFd.FieldType);
                        foreach (object k in keys)
                        {
                            WriteScalarPayload(encoder, keyFd.FieldType, k);
                            WriteMapValuePayload(encoder, valFd, dict[k], deterministic);
                        }
                    }
                    else
                    {
                        foreach (DictionaryEntry entry in dict)
                        {
                            WriteScalarPayload(encoder, keyFd.FieldType, entry.Key);
                            WriteMapValuePayload(encoder, valFd, entry.Value, deterministic);
                        }
                    }
                }

                encoder.EndMap();
                continue;
            }

            if (fd.IsRepeated)
            {
                OmniBufWireType elemWt = ScalarWireType(fd.FieldType);
                var list = (IList)val!;
                int count = list.Count;
                encoder.BeginArrayField(fnum, elemWt, count);
                for (int j = 0; j < count; j++)
                {
                    if (elemWt == OmniBufWireType.SubMsg)
                    {
                        encoder.BeginContainer();
                        if (list[j] is IMessage subMsg)
                        {
                            EncodeMessage(encoder, subMsg, deterministic);
                        }

                        encoder.EndContainer();
                    }
                    else
                    {
                        WriteScalarPayload(encoder, fd.FieldType, list[j]);
                    }
                }

                encoder.EndArray();
                continue;
            }

            if (fd.FieldType == FieldType.Message)
            {
                if (val is not IMessage subMsg)
                {
                    continue;
                }

                encoder.BeginSubMsgField(fnum);
                EncodeMessage(encoder, subMsg, deterministic);
                encoder.EndSubMsg();
                continue;
            }

            if (fd.FieldType == FieldType.Bool)
            {
                encoder.WriteBoolField(fnum, val is true);
                continue;
            }

            OmniBufWireType wt = ScalarWireType(fd.FieldType);
            encoder.WriteTag(fnum, wt);
            WriteScalarPayload(encoder, fd.FieldType, val);
        }
    }

    private static void WriteMapValuePayload(OmniBufEncoder encoder, FieldDescriptor valFd, object? value, bool deterministic)
    {
        if (valFd.FieldType == FieldType.Message)
        {
            encoder.BeginContainer();
            if (value is IMessage subMsg)
            {
                EncodeMessage(encoder, subMsg, deterministic);
            }

            encoder.EndContainer();
        }
        else
        {
            WriteScalarPayload(encoder, valFd.FieldType, value);
        }
    }

    private static void DecodeMessage(
        ReadOnlySpan<byte> data,
        IMessage target,
        int depth,
        int maxDepth,
        bool merge)
    {
        if (depth > maxDepth)
        {
            throw new OmniBufException($"omnibuf: nesting depth {depth} exceeds maximum {maxDepth}");
        }

        MessageDescriptor md = target.Descriptor;
        var seen = new HashSet<int>();
        int pos = 0;

        while (pos < data.Length)
        {
            (uint fnumU, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
            if (fnumU == 0)
            {
                SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
                continue;
            }

            int fnum = (int)fnumU;
            FieldDescriptor? fd = md.FindFieldByNumber(fnum);
            if (fd is null)
            {
                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                continue;
            }

            if (fd.IsMap)
            {
                if (wt != OmniBufWireType.Map)
                {
                    OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                    continue;
                }

                if (depth + 1 > maxDepth)
                {
                    throw new OmniBufException($"omnibuf: WireMap depth {depth + 1} exceeds maximum {maxDepth}");
                }

                bool alreadySeen = !seen.Add(fnum) || merge;
                ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 3);
                FieldDescriptor keyFd = fd.MessageType.FindFieldByNumber(1);
                FieldDescriptor valFd = fd.MessageType.FindFieldByNumber(2);
                OmniBufWireType expKeyWt = ScalarWireType(keyFd.FieldType);
                OmniBufWireType expValWt = ScalarWireType(valFd.FieldType);

                if (!OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType keyWt, out OmniBufWireType valWt, out int count, out int p)
                    || keyWt != expKeyWt
                    || valWt != expValWt)
                {
                    throw new OmniBufException($"omnibuf: WireMap header mismatch on {fd.FullName}");
                }

                var dict = (IDictionary)fd.Accessor.GetValue(target);
                if (!alreadySeen)
                {
                    dict.Clear();
                }

                for (int i = 0; i < count; i++)
                {
                    object key = ReadScalarPayload(body, ref p, keyFd);
                    object val;
                    if (valFd.FieldType == FieldType.Message)
                    {
                        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);
                        IMessage childMsg = valFd.MessageType.Parser.ParseFrom(ByteString.Empty);
                        DecodeMessage(sub, childMsg, depth + 2, maxDepth, merge: false);
                        val = childMsg;
                    }
                    else
                    {
                        val = ReadScalarPayload(body, ref p, valFd);
                    }

                    dict[key] = val;
                }

                if (p != body.Length)
                {
                    throw new OmniBufException("omnibuf: trailing bytes in WireMap");
                }

                continue;
            }

            if (fd.IsRepeated)
            {
                if (wt != OmniBufWireType.Array)
                {
                    OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                    continue;
                }

                if (depth + 1 > maxDepth)
                {
                    throw new OmniBufException($"omnibuf: WireArray depth {depth + 1} exceeds maximum {maxDepth}");
                }

                bool alreadySeen = !seen.Add(fnum) || merge;
                ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 2);
                OmniBufWireType expElemWt = ScalarWireType(fd.FieldType);
                if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p)
                    || elemWt != expElemWt)
                {
                    throw new OmniBufException($"omnibuf: WireArray header mismatch on {fd.FullName}");
                }

                var list = (IList)fd.Accessor.GetValue(target);
                if (!alreadySeen)
                {
                    list.Clear();
                }

                for (int i = 0; i < count; i++)
                {
                    if (elemWt == OmniBufWireType.SubMsg)
                    {
                        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);
                        IMessage elemMsg = fd.MessageType.Parser.ParseFrom(ByteString.Empty);
                        DecodeMessage(sub, elemMsg, depth + 2, maxDepth, merge: false);
                        list.Add(elemMsg);
                    }
                    else
                    {
                        list.Add(ReadScalarPayload(body, ref p, fd));
                    }
                }

                if (p != body.Length)
                {
                    throw new OmniBufException("omnibuf: trailing bytes in WireArray");
                }

                continue;
            }

            if (fd.FieldType == FieldType.Bool)
            {
                if (wt != OmniBufWireType.BoolFalse && wt != OmniBufWireType.BoolTrue)
                {
                    OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                    continue;
                }

                seen.Add(fnum);
                fd.Accessor.SetValue(target, wt == OmniBufWireType.BoolTrue);
                continue;
            }

            OmniBufWireType expectedWt = ScalarWireType(fd.FieldType);
            if (wt != expectedWt)
            {
                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                continue;
            }

            bool fieldAlreadySeen = !seen.Add(fnum) || merge;
            if (fd.FieldType == FieldType.Message)
            {
                ReadOnlySpan<byte> subBytes = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
                var subMsg = fd.Accessor.GetValue(target) as IMessage;
                if (subMsg is null || !fieldAlreadySeen)
                {
                    subMsg = fd.MessageType.Parser.ParseFrom(ByteString.Empty);
                    DecodeMessage(subBytes, subMsg, depth + 1, maxDepth, merge: false);
                    fd.Accessor.SetValue(target, subMsg);
                }
                else
                {
                    DecodeMessage(subBytes, subMsg, depth + 1, maxDepth, merge: true);
                }

                continue;
            }

            object scalarVal = ReadScalarPayload(data, ref pos, fd);
            fd.Accessor.SetValue(target, scalarVal);
        }

        if (!merge)
        {
            foreach (FieldDescriptor fd in md.Fields.InFieldNumberOrder())
            {
                if (!seen.Contains(fd.FieldNumber))
                {
                    fd.Accessor.Clear(target);
                }
            }
        }
    }

    private static OmniBufWireType ScalarWireType(FieldType ft) => ft switch
    {
        FieldType.UInt32 or FieldType.UInt64 => OmniBufWireType.Uint,
        FieldType.Int32 or FieldType.SInt32 or FieldType.Int64 or FieldType.SInt64 or FieldType.Enum => OmniBufWireType.Sint,
        FieldType.Bool => OmniBufWireType.BoolFalse,
        FieldType.Fixed32 => OmniBufWireType.Fixed32,
        FieldType.Fixed64 => OmniBufWireType.Fixed64,
        FieldType.SFixed32 => OmniBufWireType.Sfixed32,
        FieldType.SFixed64 => OmniBufWireType.Sfixed64,
        FieldType.Float => OmniBufWireType.Float32,
        FieldType.Double => OmniBufWireType.Float64,
        FieldType.String => OmniBufWireType.String,
        FieldType.Bytes => OmniBufWireType.Bytes,
        FieldType.Message => OmniBufWireType.SubMsg,
        _ => throw new OmniBufException($"omnibuf: unsupported Protobuf field type {ft}"),
    };

    private static void WriteScalarPayload(OmniBufEncoder encoder, FieldType ft, object? val)
    {
        switch (ft)
        {
            case FieldType.Bool:
                encoder.WriteBoolElement(val is true);
                break;

            case FieldType.UInt32:
                encoder.WriteUintValue(val is uint u32 ? u32 : Convert.ToUInt32(val));
                break;

            case FieldType.UInt64:
                encoder.WriteUintValue(val is ulong u64 ? u64 : Convert.ToUInt64(val));
                break;

            case FieldType.Int32:
            case FieldType.SInt32:
            case FieldType.Enum:
                encoder.WriteSintValue(val is int i32 ? i32 : Convert.ToInt32(val));
                break;

            case FieldType.Int64:
            case FieldType.SInt64:
                encoder.WriteSintValue(val is long i64 ? i64 : Convert.ToInt64(val));
                break;

            case FieldType.Fixed32:
                encoder.WriteFixed32Value(val is uint f32 ? f32 : Convert.ToUInt32(val));
                break;

            case FieldType.Fixed64:
                encoder.WriteFixed64Value(val is ulong f64 ? f64 : Convert.ToUInt64(val));
                break;

            case FieldType.SFixed32:
                encoder.WriteSfixed32Value(val is int sf32 ? sf32 : Convert.ToInt32(val));
                break;

            case FieldType.SFixed64:
                encoder.WriteSfixed64Value(val is long sf64 ? sf64 : Convert.ToInt64(val));
                break;

            case FieldType.Float:
                encoder.WriteFloat32Value(val is float flt ? flt : Convert.ToSingle(val));
                break;

            case FieldType.Double:
                encoder.WriteFloat64Value(val is double dbl ? dbl : Convert.ToDouble(val));
                break;

            case FieldType.String:
                encoder.WriteStringValue(val as string);
                break;

            case FieldType.Bytes:
                if (val is ByteString bs)
                {
                    encoder.WriteBytesValue(bs.Span);
                }
                else
                {
                    encoder.WriteBytesValue(ReadOnlySpan<byte>.Empty);
                }

                break;

            default:
                throw new OmniBufException($"omnibuf: unsupported Protobuf scalar type {ft}");
        }
    }

    private static object ReadScalarPayload(ReadOnlySpan<byte> data, ref int pos, FieldDescriptor fd)
    {
        switch (fd.FieldType)
        {
            case FieldType.Bool:
                return OmniBufDecoder.ReadBoolElement(data, ref pos);

            case FieldType.UInt32:
                return (uint)OmniBufDecoder.ReadVarint(data, ref pos);

            case FieldType.UInt64:
                return OmniBufDecoder.ReadVarint(data, ref pos);

            case FieldType.Int32:
            case FieldType.SInt32:
                return (int)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos));

            case FieldType.Enum:
                {
                    int enumInt = (int)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos));
                    return Enum.ToObject(fd.EnumType.ClrType, enumInt);
                }

            case FieldType.Int64:
            case FieldType.SInt64:
                return OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos));

            case FieldType.Fixed32:
                return OmniBufDecoder.ReadFixed32(data, ref pos);

            case FieldType.Fixed64:
                return OmniBufDecoder.ReadFixed64(data, ref pos);

            case FieldType.SFixed32:
                return (int)OmniBufDecoder.ReadFixed32(data, ref pos);

            case FieldType.SFixed64:
                return (long)OmniBufDecoder.ReadFixed64(data, ref pos);

            case FieldType.Float:
                return BitConverter.UInt32BitsToSingle(OmniBufDecoder.ReadFixed32(data, ref pos));

            case FieldType.Double:
                return BitConverter.UInt64BitsToDouble(OmniBufDecoder.ReadFixed64(data, ref pos));

            case FieldType.String:
                return OmniBufDecoder.ReadString(data, ref pos);

            case FieldType.Bytes:
                return ByteString.CopyFrom(OmniBufDecoder.ReadLenSlice(data, ref pos, 0));

            default:
                throw new OmniBufException($"omnibuf: unsupported Protobuf scalar type {fd.FieldType}");
        }
    }

    private static void SortMapKeys(object[] keys, FieldType keyFt)
    {
        Array.Sort(keys, (a, b) => keyFt switch
        {
            FieldType.String => StringComparer.Ordinal.Compare((string)a, (string)b),
            FieldType.Bool => ((bool)a).CompareTo((bool)b),
            FieldType.UInt32 or FieldType.Fixed32 => ((uint)a).CompareTo((uint)b),
            FieldType.UInt64 or FieldType.Fixed64 => ((ulong)a).CompareTo((ulong)b),
            FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => ((int)a).CompareTo((int)b),
            FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => ((long)a).CompareTo((long)b),
            _ => Comparer.DefaultInvariant.Compare(a, b),
        });
    }
}
