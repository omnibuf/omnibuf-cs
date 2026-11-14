namespace OmniBuf;

/// <summary>
/// Self-describing schema metadata stored in Field #0 (Tag = 0x19, SubMsg) of an OmniBuf packet.
/// </summary>
public sealed class SchemaMeta
{
    public string TypeName { get; set; } = string.Empty;

    public Dictionary<uint, string> Fields { get; set; } = new();

    public Dictionary<uint, SchemaMeta> Children { get; set; } = new();

    public static void WriteField0(OmniBufEncoder encoder, SchemaMeta meta, bool deterministic = true)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(meta);

        encoder.BeginField0SubMsg();
        WriteBody(encoder, meta, deterministic, depth: 1);
        encoder.EndSubMsg();
    }

    private static void WriteBody(OmniBufEncoder encoder, SchemaMeta meta, bool deterministic, int depth)
    {
        if (depth > OmniBufConstants.MaxNestingDepth)
        {
            throw new OmniBufException($"omnibuf: SchemaMeta nesting depth {depth} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
        }

        if (!string.IsNullOrEmpty(meta.TypeName))
        {
            encoder.WriteStringField(1, meta.TypeName);
        }

        if (meta.Fields.Count > 0)
        {
            if (depth + 1 > OmniBufConstants.MaxNestingDepth)
            {
                throw new OmniBufException($"omnibuf: SchemaMeta fields map depth {depth + 1} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
            }

            encoder.BeginMapField(2, OmniBufWireType.Uint, OmniBufWireType.String, meta.Fields.Count);
            if (deterministic && meta.Fields.Count > 1)
            {
                var keys = new List<uint>(meta.Fields.Keys);
                keys.Sort();
                foreach (uint k in keys)
                {
                    if (k == 0 || k > OmniBufConstants.MaxFieldNumber)
                    {
                        throw new OmniBufException($"omnibuf: invalid SchemaMeta field key {k}");
                    }

                    encoder.WriteUintValue(k);
                    encoder.WriteStringValue(meta.Fields[k]);
                }
            }
            else
            {
                foreach (KeyValuePair<uint, string> kv in meta.Fields)
                {
                    if (kv.Key == 0 || kv.Key > OmniBufConstants.MaxFieldNumber)
                    {
                        throw new OmniBufException($"omnibuf: invalid SchemaMeta field key {kv.Key}");
                    }

                    encoder.WriteUintValue(kv.Key);
                    encoder.WriteStringValue(kv.Value);
                }
            }

            encoder.EndMap();
        }

        if (meta.Children.Count > 0)
        {
            if (depth + 1 > OmniBufConstants.MaxNestingDepth)
            {
                throw new OmniBufException($"omnibuf: SchemaMeta children map depth {depth + 1} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
            }

            encoder.BeginMapField(3, OmniBufWireType.Uint, OmniBufWireType.SubMsg, meta.Children.Count);
            if (deterministic && meta.Children.Count > 1)
            {
                var keys = new List<uint>(meta.Children.Keys);
                keys.Sort();
                foreach (uint k in keys)
                {
                    if (k == 0 || k > OmniBufConstants.MaxFieldNumber)
                    {
                        throw new OmniBufException($"omnibuf: invalid SchemaMeta child key {k}");
                    }

                    encoder.WriteUintValue(k);
                    encoder.BeginContainer();
                    WriteBody(encoder, meta.Children[k], deterministic, depth + 2);
                    encoder.EndContainer();
                }
            }
            else
            {
                foreach (KeyValuePair<uint, SchemaMeta> kv in meta.Children)
                {
                    if (kv.Key == 0 || kv.Key > OmniBufConstants.MaxFieldNumber)
                    {
                        throw new OmniBufException($"omnibuf: invalid SchemaMeta child key {kv.Key}");
                    }

                    encoder.WriteUintValue(kv.Key);
                    encoder.BeginContainer();
                    WriteBody(encoder, kv.Value, deterministic, depth + 2);
                    encoder.EndContainer();
                }
            }

            encoder.EndMap();
        }
    }

    public static void ValidateField0(
        ReadOnlySpan<byte> data,
        ref int pos,
        OmniBufWireType wt,
        int depth = 0,
        int maxDepth = OmniBufConstants.MaxNestingDepth)
    {
        if (wt != OmniBufWireType.SubMsg)
        {
            throw new OmniBufException($"omnibuf: Field #0 requires SubMsg wire type, got {wt}");
        }

        ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
        _ = Decode(body, depth + 1, maxDepth);
    }

    public static SchemaMeta? ReadField0(ReadOnlySpan<byte> packet)
    {
        SchemaMeta? merged = null;
        int pos = 0;
        while (pos < packet.Length)
        {
            (uint fieldNum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(packet, ref pos);
            if (fieldNum == 0)
            {
                ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(packet, ref pos, 0);
                SchemaMeta current = Decode(body, 1, OmniBufConstants.MaxNestingDepth);
                merged = Merge(merged, current);
            }
            else if (!OmniBufDecoder.TryFastSkipValue(packet, ref pos, wt))
            {
                throw new OmniBufException($"omnibuf: invalid field #{fieldNum} while scanning Field #0");
            }
        }

        return merged;
    }

    public static SchemaMeta Decode(
        ReadOnlySpan<byte> data,
        int depth = 0,
        int maxDepth = OmniBufConstants.MaxNestingDepth)
    {
        if (depth > maxDepth)
        {
            throw new OmniBufException($"omnibuf: SchemaMeta nesting depth {depth} exceeds maximum {maxDepth}");
        }

        var meta = new SchemaMeta();
        int pos = 0;
        while (pos < data.Length)
        {
            ulong tag = OmniBufDecoder.ReadVarint(data, ref pos);
            ulong fnum64 = tag >> 4;
            if (fnum64 > OmniBufConstants.MaxFieldNumber)
            {
                throw new OmniBufException($"omnibuf: SchemaMeta field number {fnum64} out of range");
            }

            uint fnum = (uint)fnum64;
            var wt = (OmniBufWireType)(tag & 0x0Fu);
            if (fnum == 0)
            {
                throw new OmniBufException("omnibuf: Field #0 is not allowed inside SchemaMeta");
            }

            switch (fnum)
            {
                case 1:
                    {
                        if (wt != OmniBufWireType.String)
                        {
                            throw new OmniBufException($"omnibuf: SchemaMeta field #1 (type_name) requires String, got {wt}");
                        }

                        meta.TypeName = OmniBufDecoder.ReadString(data, ref pos);
                        break;
                    }

                case 2:
                    {
                        if (wt != OmniBufWireType.Map)
                        {
                            throw new OmniBufException($"omnibuf: SchemaMeta field #2 (fields) requires Map, got {wt}");
                        }

                        if (depth + 1 > maxDepth)
                        {
                            throw new OmniBufException($"omnibuf: SchemaMeta fields map depth {depth + 1} exceeds maximum {maxDepth}");
                        }

                        ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 3);
                        if (!OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType keyWt, out OmniBufWireType valWt, out int count, out int p)
                            || keyWt != OmniBufWireType.Uint
                            || valWt != OmniBufWireType.String)
                        {
                            throw new OmniBufException("omnibuf: invalid SchemaMeta fields map header");
                        }

                        for (int i = 0; i < count; i++)
                        {
                            ulong k = OmniBufDecoder.ReadVarint(body, ref p);
                            if (k == 0 || k > OmniBufConstants.MaxFieldNumber)
                            {
                                throw new OmniBufException($"omnibuf: invalid SchemaMeta field key {k}");
                            }

                            string name = OmniBufDecoder.ReadString(body, ref p);
                            meta.Fields[(uint)k] = name;
                        }

                        if (p != body.Length)
                        {
                            throw new OmniBufException("omnibuf: trailing bytes in SchemaMeta fields map");
                        }

                        break;
                    }

                case 3:
                    {
                        if (wt != OmniBufWireType.Map)
                        {
                            throw new OmniBufException($"omnibuf: SchemaMeta field #3 (children) requires Map, got {wt}");
                        }

                        if (depth + 1 > maxDepth)
                        {
                            throw new OmniBufException($"omnibuf: SchemaMeta children map depth {depth + 1} exceeds maximum {maxDepth}");
                        }

                        ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 3);
                        if (!OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType keyWt, out OmniBufWireType valWt, out int count, out int p)
                            || keyWt != OmniBufWireType.Uint
                            || valWt != OmniBufWireType.SubMsg)
                        {
                            throw new OmniBufException("omnibuf: invalid SchemaMeta children map header");
                        }

                        var mapChildren = new Dictionary<uint, SchemaMeta>(Math.Min(count, 256));
                        for (int i = 0; i < count; i++)
                        {
                            ulong k = OmniBufDecoder.ReadVarint(body, ref p);
                            if (k == 0 || k > OmniBufConstants.MaxFieldNumber)
                            {
                                throw new OmniBufException($"omnibuf: invalid SchemaMeta child key {k}");
                            }

                            ReadOnlySpan<byte> childBytes = OmniBufDecoder.ReadLenSlice(body, ref p, 0);
                            SchemaMeta childMeta = Decode(childBytes, depth + 2, maxDepth);
                            mapChildren[(uint)k] = childMeta;
                        }

                        if (p != body.Length)
                        {
                            throw new OmniBufException("omnibuf: trailing bytes in SchemaMeta children map");
                        }

                        foreach (KeyValuePair<uint, SchemaMeta> kv in mapChildren)
                        {
                            meta.Children.TryGetValue(kv.Key, out SchemaMeta? existing);
                            meta.Children[kv.Key] = Merge(existing, kv.Value)!;
                        }

                        break;
                    }

                default:
                    OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                    break;
            }
        }

        return meta;
    }

    public static SchemaMeta? Merge(SchemaMeta? baseMeta, SchemaMeta? overlay)
    {
        if (overlay is null)
        {
            return baseMeta;
        }

        if (baseMeta is null)
        {
            return Clone(overlay);
        }

        var outMeta = Clone(baseMeta);
        if (!string.IsNullOrEmpty(overlay.TypeName))
        {
            outMeta.TypeName = overlay.TypeName;
        }

        foreach (KeyValuePair<uint, string> kv in overlay.Fields)
        {
            outMeta.Fields[kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<uint, SchemaMeta> kv in overlay.Children)
        {
            outMeta.Children.TryGetValue(kv.Key, out SchemaMeta? existing);
            outMeta.Children[kv.Key] = Merge(existing, kv.Value)!;
        }

        return outMeta;
    }

    private static SchemaMeta Clone(SchemaMeta src)
    {
        var copy = new SchemaMeta
        {
            TypeName = src.TypeName,
            Fields = new Dictionary<uint, string>(src.Fields),
            Children = new Dictionary<uint, SchemaMeta>(src.Children.Count),
        };
        foreach (KeyValuePair<uint, SchemaMeta> kv in src.Children)
        {
            copy.Children[kv.Key] = Clone(kv.Value);
        }

        return copy;
    }
}
