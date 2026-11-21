using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace OmniBuf;

/// <summary>
/// Descriptor-based OmniBuf serializer for standard <c>protoc --csharp_out</c> <see cref="IMessage"/> types.
/// </summary>
public static class OmniBufProtobuf
{
    private static readonly ConcurrentDictionary<MessageDescriptor, bool> ValidatedDescriptors = new();

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

    public static void Marshal(IBufferWriter<byte> writer, IMessage message, MarshalOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(message);
        OmniBufEncoder encoder = OmniBufEncoder.RentThreadLocal(out bool rented);
        try
        {
            Marshal(encoder, message, options);
            encoder.CopyTo(writer);
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
        EnsureDescriptorSupported(message.Descriptor);

        if (options.SelfDescribing)
        {
            SchemaMeta? meta = BuildSchemaMeta(message.Descriptor, depth: 1, active: null, out bool cyclic);
            if (cyclic)
            {
                meta = BuildValueSchemaMeta(message, depth: 1, active: null);
            }

            if (meta is not null)
            {
                SchemaMeta.WriteField0(encoder, meta, options.Deterministic);
            }
        }

        EncodeMessage(encoder, message, options.Deterministic, depth: 0);
    }

    public static void Unmarshal(ReadOnlySpan<byte> data, IMessage target)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureDescriptorSupported(target.Descriptor);
        DecodeMessage(data, target, depth: 0, maxDepth: OmniBufConstants.MaxNestingDepth, merge: false);
    }

    public static T Unmarshal<T>(ReadOnlySpan<byte> data)
        where T : IMessage, new()
    {
        var msg = new T();
        Unmarshal(data, msg);
        return msg;
    }

    private static void EnsureDescriptorSupported(MessageDescriptor md)
    {
        if (ValidatedDescriptors.ContainsKey(md))
        {
            return;
        }

        ValidateDescriptorGraph(md, new HashSet<string>(StringComparer.Ordinal));
        ValidatedDescriptors[md] = true;
    }

    private static void ValidateDescriptorGraph(MessageDescriptor md, HashSet<string> visited)
    {
        if (!visited.Add(md.FullName))
        {
            return;
        }

        if (md.ToProto().ExtensionRange.Count > 0)
        {
            throw new OmniBufException($"omnibuf: Protobuf extensions on {md.FullName} are not supported");
        }

        foreach (FieldDescriptor fd in md.Fields.InFieldNumberOrder())
        {
            if (fd.FieldType == FieldType.Group)
            {
                throw new OmniBufException($"omnibuf: Protobuf group field {fd.FullName} is not supported");
            }

            if (!IsZeroDefaultValue(fd))
            {
                throw new OmniBufException($"omnibuf: custom non-zero default value on {fd.FullName} is not supported");
            }

            if (fd.IsMap)
            {
                FieldDescriptor valFd = fd.MessageType.FindFieldByNumber(2);
                if (valFd.FieldType == FieldType.Group)
                {
                    throw new OmniBufException($"omnibuf: Protobuf group map value on {fd.FullName} is not supported");
                }

                if (valFd.FieldType == FieldType.Message && !TryGetWrapperFieldType(valFd.MessageType, out _))
                {
                    ValidateDescriptorGraph(valFd.MessageType, visited);
                }
            }
            else if (fd.FieldType == FieldType.Message && !TryGetWrapperFieldType(fd.MessageType, out _))
            {
                ValidateDescriptorGraph(fd.MessageType, visited);
            }
        }
    }

    private static bool IsZeroDefaultValue(FieldDescriptor fd)
    {
        FieldDescriptorProto proto = fd.ToProto();
        if (!proto.HasDefaultValue || string.IsNullOrEmpty(proto.DefaultValue))
        {
            return true;
        }

        string dv = proto.DefaultValue;
        return fd.FieldType switch
        {
            FieldType.Bool => dv == "false",
            FieldType.String or FieldType.Bytes => dv.Length == 0,
            FieldType.Enum => fd.EnumType.FindValueByName(dv)?.Number == 0,
            FieldType.Float or FieldType.Double => double.TryParse(dv, System.Globalization.CultureInfo.InvariantCulture, out double d) && d == 0d && !double.IsNegative(d),
            _ => long.TryParse(dv, System.Globalization.CultureInfo.InvariantCulture, out long l) && l == 0L,
        };
    }

    internal static bool TryGetWrapperFieldType(MessageDescriptor md, out FieldType innerType)
    {
        switch (md.FullName)
        {
            case "google.protobuf.DoubleValue":
                innerType = FieldType.Double;
                return true;
            case "google.protobuf.FloatValue":
                innerType = FieldType.Float;
                return true;
            case "google.protobuf.Int64Value":
                innerType = FieldType.Int64;
                return true;
            case "google.protobuf.UInt64Value":
                innerType = FieldType.UInt64;
                return true;
            case "google.protobuf.Int32Value":
                innerType = FieldType.Int32;
                return true;
            case "google.protobuf.UInt32Value":
                innerType = FieldType.UInt32;
                return true;
            case "google.protobuf.BoolValue":
                innerType = FieldType.Bool;
                return true;
            case "google.protobuf.StringValue":
                innerType = FieldType.String;
                return true;
            case "google.protobuf.BytesValue":
                innerType = FieldType.Bytes;
                return true;
            default:
                innerType = default;
                return false;
        }
    }

    private static SchemaMeta? BuildSchemaMeta(MessageDescriptor? md, int depth, HashSet<string>? active, out bool cyclic)
    {
        cyclic = false;
        if (md is null || depth + 1 > OmniBufConstants.MaxNestingDepth)
        {
            return null;
        }

        active ??= new HashSet<string>(StringComparer.Ordinal);
        if (!active.Add(md.FullName))
        {
            cyclic = true;
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
                if (fd.IsMap)
                {
                    FieldDescriptor valFd = fd.MessageType.FindFieldByNumber(2);
                    if (valFd.FieldType == FieldType.Message)
                    {
                        childMd = valFd.MessageType;
                    }
                }
                else if (fd.FieldType == FieldType.Message)
                {
                    childMd = fd.MessageType;
                }

                if (childMd is not null)
                {
                    // Every child entry in SchemaMeta.Children adds WireMap + WireSubMsg (+2 depth).
                    SchemaMeta? childMeta = BuildSchemaMeta(childMd, depth + 2, active, out bool childCyclic);
                    cyclic |= childCyclic;
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

    private static SchemaMeta? BuildValueSchemaMeta(IMessage? message, int depth, HashSet<object>? active)
    {
        if (message is null || depth + 1 > OmniBufConstants.MaxNestingDepth)
        {
            return null;
        }

        active ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!active.Add(message))
        {
            return null;
        }

        try
        {
            MessageDescriptor md = message.Descriptor;
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

                if (depth + 3 > OmniBufConstants.MaxNestingDepth)
                {
                    continue;
                }

                if (fd.IsMap)
                {
                    FieldDescriptor valFd = fd.MessageType.FindFieldByNumber(2);
                    if (valFd.FieldType == FieldType.Message)
                    {
                        var dict = (IDictionary)fd.Accessor.GetValue(message)!;
                        if (dict.Count == 0)
                        {
                            continue;
                        }

                        if (TryGetWrapperFieldType(valFd.MessageType, out _))
                        {
                            SchemaMeta? wrapMeta = BuildSchemaMeta(valFd.MessageType, depth + 2, null, out _);
                            if (wrapMeta is not null)
                            {
                                meta.Children[fnum] = wrapMeta;
                            }
                        }
                        else
                        {
                            SchemaMeta? merged = null;
                            foreach (DictionaryEntry entry in dict)
                            {
                                if (entry.Value is IMessage subMsg)
                                {
                                    merged = SchemaMeta.Merge(merged, BuildValueSchemaMeta(subMsg, depth + 2, active));
                                }
                            }

                            if (merged is not null)
                            {
                                meta.Children[fnum] = merged;
                            }
                        }
                    }
                }
                else if (fd.IsRepeated && fd.FieldType == FieldType.Message)
                {
                    var list = (IList)fd.Accessor.GetValue(message)!;
                    if (list.Count == 0)
                    {
                        continue;
                    }

                    if (TryGetWrapperFieldType(fd.MessageType, out _))
                    {
                        SchemaMeta? wrapMeta = BuildSchemaMeta(fd.MessageType, depth + 2, null, out _);
                        if (wrapMeta is not null)
                        {
                            meta.Children[fnum] = wrapMeta;
                        }
                    }
                    else
                    {
                        SchemaMeta? merged = null;
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (list[i] is IMessage elemMsg)
                            {
                                merged = SchemaMeta.Merge(merged, BuildValueSchemaMeta(elemMsg, depth + 2, active));
                            }
                        }

                        if (merged is not null)
                        {
                            meta.Children[fnum] = merged;
                        }
                    }
                }
                else if (fd.FieldType == FieldType.Message)
                {
                    object? val = fd.Accessor.GetValue(message);
                    if (val is null)
                    {
                        continue;
                    }

                    SchemaMeta? childMeta = TryGetWrapperFieldType(fd.MessageType, out _)
                        ? BuildSchemaMeta(fd.MessageType, depth + 2, null, out _)
                        : BuildValueSchemaMeta(val as IMessage, depth + 2, active);
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
            active.Remove(message);
        }
    }

    private static void EncodeMessage(OmniBufEncoder encoder, IMessage message, bool deterministic, int depth)
    {
        if (depth > OmniBufConstants.MaxNestingDepth)
        {
            throw new OmniBufException($"omnibuf: encode nesting depth {depth} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
        }

        IList<FieldDescriptor> fields = message.Descriptor.Fields.InFieldNumberOrder();
        for (int i = 0; i < fields.Count; i++)
        {
            FieldDescriptor fd = fields[i];
            uint fnum = (uint)fd.FieldNumber;

            if (fd.FieldType == FieldType.Group)
            {
                throw new OmniBufException($"omnibuf: Protobuf group field {fd.FullName} is not supported");
            }

            if (fd.IsMap)
            {
                if (depth + 1 > OmniBufConstants.MaxNestingDepth)
                {
                    throw new OmniBufException($"omnibuf: WireMap encode depth {depth + 1} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
                }

                object? mapObj = fd.Accessor.GetValue(message);
                FieldDescriptor keyFd = fd.MessageType.FindFieldByNumber(1);
                FieldDescriptor valFd = fd.MessageType.FindFieldByNumber(2);
                OmniBufWireType keyWt = ScalarWireType(keyFd.FieldType);
                OmniBufWireType valWt = ScalarWireType(valFd.FieldType);

                var dict = (IDictionary)mapObj!;
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
                            WriteMapValuePayload(encoder, valFd, dict[k], deterministic, depth + 2);
                        }
                    }
                    else
                    {
                        foreach (DictionaryEntry entry in dict)
                        {
                            WriteScalarPayload(encoder, keyFd.FieldType, entry.Key);
                            WriteMapValuePayload(encoder, valFd, entry.Value, deterministic, depth + 2);
                        }
                    }
                }

                encoder.EndMap();
                continue;
            }

            if (fd.IsRepeated)
            {
                if (depth + 1 > OmniBufConstants.MaxNestingDepth)
                {
                    throw new OmniBufException($"omnibuf: WireArray encode depth {depth + 1} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
                }

                object? listObj = fd.Accessor.GetValue(message);
                OmniBufWireType elemWt = ScalarWireType(fd.FieldType);
                var list = (IList)listObj!;
                int count = list.Count;
                encoder.BeginArrayField(fnum, elemWt, count);
                if (elemWt == OmniBufWireType.SubMsg)
                {
                    bool isWrapper = TryGetWrapperFieldType(fd.MessageType, out FieldType wrapperFt);
                    for (int j = 0; j < count; j++)
                    {
                        if (depth + 2 > OmniBufConstants.MaxNestingDepth)
                        {
                            throw new OmniBufException($"omnibuf: encode nesting depth {depth + 2} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
                        }

                        encoder.BeginContainer();
                        object? elem = list[j];
                        if (isWrapper)
                        {
                            if (elem is not null)
                            {
                                WriteWrapperScalarField(encoder, wrapperFt, elem);
                            }
                        }
                        else if (elem is IMessage subMsg)
                        {
                            EncodeMessage(encoder, subMsg, deterministic, depth + 2);
                        }

                        encoder.EndContainer();
                    }
                }
                else
                {
                    for (int j = 0; j < count; j++)
                    {
                        WriteScalarPayload(encoder, fd.FieldType, list[j]);
                    }
                }

                encoder.EndArray();
                continue;
            }

            if (fd.FieldType == FieldType.Message)
            {
                object? msgVal = fd.Accessor.GetValue(message);
                if (msgVal is null)
                {
                    continue;
                }

                if (depth + 1 > OmniBufConstants.MaxNestingDepth)
                {
                    throw new OmniBufException($"omnibuf: encode nesting depth {depth + 1} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
                }

                encoder.BeginSubMsgField(fnum);
                if (TryGetWrapperFieldType(fd.MessageType, out FieldType wrapperFt))
                {
                    WriteWrapperScalarField(encoder, wrapperFt, msgVal);
                }
                else
                {
                    EncodeMessage(encoder, (IMessage)msgVal, deterministic, depth + 1);
                }

                encoder.EndSubMsg();
                continue;
            }

            if (fd.ContainingOneof is { IsSynthetic: false } realOneof)
            {
                if (realOneof.Accessor.GetCaseFieldDescriptor(message) != fd)
                {
                    continue;
                }
            }
            else if (fd.HasPresence && !fd.Accessor.HasValue(message))
            {
                continue;
            }

            object? val = fd.Accessor.GetValue(message);
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

    private static void WriteMapValuePayload(OmniBufEncoder encoder, FieldDescriptor valFd, object? value, bool deterministic, int childDepth)
    {
        if (valFd.FieldType == FieldType.Message)
        {
            if (childDepth > OmniBufConstants.MaxNestingDepth)
            {
                throw new OmniBufException($"omnibuf: encode nesting depth {childDepth} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
            }

            encoder.BeginContainer();
            if (TryGetWrapperFieldType(valFd.MessageType, out FieldType wrapperFt))
            {
                if (value is not null)
                {
                    WriteWrapperScalarField(encoder, wrapperFt, value);
                }
            }
            else if (value is IMessage subMsg)
            {
                EncodeMessage(encoder, subMsg, deterministic, childDepth);
            }

            encoder.EndContainer();
        }
        else
        {
            WriteScalarPayload(encoder, valFd.FieldType, value);
        }
    }

    private static void WriteWrapperScalarField(OmniBufEncoder encoder, FieldType innerFt, object unwrappedValue)
    {
        if (innerFt == FieldType.Bool)
        {
            encoder.WriteBoolField(1, unwrappedValue is true);
            return;
        }

        encoder.WriteTag(1, ScalarWireType(innerFt));
        WriteScalarPayload(encoder, innerFt, unwrappedValue);
    }

    private static object DecodeWrapperSubMsg(
        ReadOnlySpan<byte> subBytes,
        FieldType innerFt,
        object initialValue,
        int depth,
        int maxDepth)
    {
        return innerFt switch
        {
            FieldType.Double => OmniBufWrappers.DecodeDouble(subBytes, initialValue is double d ? d : 0d, depth, maxDepth),
            FieldType.Float => OmniBufWrappers.DecodeFloat(subBytes, initialValue is float f ? f : 0f, depth, maxDepth),
            FieldType.Int64 => OmniBufWrappers.DecodeInt64(subBytes, initialValue is long i64 ? i64 : 0L, depth, maxDepth),
            FieldType.UInt64 => OmniBufWrappers.DecodeUInt64(subBytes, initialValue is ulong u64 ? u64 : 0UL, depth, maxDepth),
            FieldType.Int32 => OmniBufWrappers.DecodeInt32(subBytes, initialValue is int i32 ? i32 : 0, depth, maxDepth),
            FieldType.UInt32 => OmniBufWrappers.DecodeUInt32(subBytes, initialValue is uint u32 ? u32 : 0u, depth, maxDepth),
            FieldType.Bool => OmniBufWrappers.DecodeBool(subBytes, initialValue is true, depth, maxDepth),
            FieldType.String => OmniBufWrappers.DecodeString(subBytes, initialValue as string ?? string.Empty, depth, maxDepth),
            FieldType.Bytes => OmniBufWrappers.DecodeBytes(subBytes, initialValue as ByteString ?? ByteString.Empty, depth, maxDepth),
            _ => throw new OmniBufException($"omnibuf: unsupported wrapper scalar type {innerFt}"),
        };
    }

    private static object DefaultWrapperScalar(FieldType innerFt) => innerFt switch
    {
        FieldType.Double => 0d,
        FieldType.Float => 0f,
        FieldType.Int64 => 0L,
        FieldType.UInt64 => 0UL,
        FieldType.Int32 => 0,
        FieldType.UInt32 => 0u,
        FieldType.Bool => false,
        FieldType.String => string.Empty,
        FieldType.Bytes => ByteString.Empty,
        _ => throw new OmniBufException($"omnibuf: unsupported wrapper scalar type {innerFt}"),
    };

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
        if (!merge)
        {
            foreach (OneofDescriptor oneof in md.Oneofs)
            {
                if (!oneof.IsSynthetic)
                {
                    oneof.Accessor.Clear(target);
                }
            }
        }

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

            if (fd.FieldType == FieldType.Group)
            {
                throw new OmniBufException($"omnibuf: Protobuf group field {fd.FullName} is not supported");
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

                FieldType resolvedMapWrapperFt = default;
                bool isValWrapper = valFd.FieldType == FieldType.Message && TryGetWrapperFieldType(valFd.MessageType, out resolvedMapWrapperFt);
                for (int i = 0; i < count; i++)
                {
                    object key = ReadScalarPayload(body, ref p, keyFd);
                    object val;
                    if (valFd.FieldType == FieldType.Message)
                    {
                        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);
                        if (isValWrapper)
                        {
                            val = DecodeWrapperSubMsg(sub, resolvedMapWrapperFt, DefaultWrapperScalar(resolvedMapWrapperFt), depth + 2, maxDepth);
                        }
                        else
                        {
                            IMessage childMsg = valFd.MessageType.Parser.ParseFrom(ByteString.Empty);
                            DecodeMessage(sub, childMsg, depth + 2, maxDepth, merge: false);
                            val = childMsg;
                        }
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

                FieldType resolvedElemWrapperFt = default;
                bool isElemWrapper = elemWt == OmniBufWireType.SubMsg && TryGetWrapperFieldType(fd.MessageType, out resolvedElemWrapperFt);
                for (int i = 0; i < count; i++)
                {
                    if (elemWt == OmniBufWireType.SubMsg)
                    {
                        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);
                        if (isElemWrapper)
                        {
                            list.Add(DecodeWrapperSubMsg(sub, resolvedElemWrapperFt, DefaultWrapperScalar(resolvedElemWrapperFt), depth + 2, maxDepth));
                        }
                        else
                        {
                            IMessage elemMsg = fd.MessageType.Parser.ParseFrom(ByteString.Empty);
                            DecodeMessage(sub, elemMsg, depth + 2, maxDepth, merge: false);
                            list.Add(elemMsg);
                        }
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
                if (TryGetWrapperFieldType(fd.MessageType, out FieldType wrapperFt))
                {
                    object? prevVal = fieldAlreadySeen ? fd.Accessor.GetValue(target) : null;
                    object initVal = prevVal ?? DefaultWrapperScalar(wrapperFt);
                    object decodedWrapperVal = DecodeWrapperSubMsg(subBytes, wrapperFt, initVal, depth + 1, maxDepth);
                    fd.Accessor.SetValue(target, decodedWrapperVal);
                    continue;
                }

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
                if (fd.ContainingOneof is { IsSynthetic: false })
                {
                    continue;
                }

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

    private static void SortMapKeys(object[] keys, FieldType keyType)
    {
        Array.Sort(keys, (a, b) => keyType switch
        {
            FieldType.String => Utf8OrdinalStringComparer.CompareStatic((string)a, (string)b),
            FieldType.Bool => ((bool)a).CompareTo((bool)b),
            FieldType.UInt32 or FieldType.Fixed32 => ((uint)a).CompareTo((uint)b),
            FieldType.UInt64 or FieldType.Fixed64 => ((ulong)a).CompareTo((ulong)b),
            FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => ((int)a).CompareTo((int)b),
            FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => ((long)a).CompareTo((long)b),
            _ => Comparer.DefaultInvariant.Compare(a, b),
        });
    }
}
