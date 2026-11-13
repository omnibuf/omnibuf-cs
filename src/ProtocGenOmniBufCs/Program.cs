using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace OmniBuf.ProtocGen;

public static class Program
{
    private const ulong FeatureProto3Optional = 1UL;

    public static int Main()
    {
        try
        {
            byte[] inputBytes;
            using (Stream stdin = Console.OpenStandardInput())
            using (var ms = new MemoryStream())
            {
                stdin.CopyTo(ms);
                inputBytes = ms.ToArray();
            }

            (List<string> filesToGenerate, List<FileDescriptorProto> protoFiles) = ParseRequest(inputBytes);
            var symbolTable = BuildSymbolTable(protoFiles);
            var generatedFiles = new List<(string Name, string Content)>();

            var targetSet = new HashSet<string>(filesToGenerate, StringComparer.Ordinal);
            foreach (FileDescriptorProto file in protoFiles)
            {
                if (!targetSet.Contains(file.Name))
                {
                    continue;
                }

                string fileContent = GenerateFile(file, symbolTable);
                string outputName = ComputeOutputFileName(file.Name);
                generatedFiles.Add((outputName, fileContent));
            }

            byte[] responseBytes = EncodeResponse(error: null, generatedFiles);
            using Stream stdout = Console.OpenStandardOutput();
            stdout.Write(responseBytes, 0, responseBytes.Length);
            stdout.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            byte[] errBytes = EncodeResponse(error: ex.ToString(), Array.Empty<(string, string)>());
            using Stream stdout = Console.OpenStandardOutput();
            stdout.Write(errBytes, 0, errBytes.Length);
            stdout.Flush();
            return 0;
        }
    }

    private static (List<string> FilesToGenerate, List<FileDescriptorProto> ProtoFiles) ParseRequest(byte[] bytes)
    {
        var filesToGenerate = new List<string>();
        var protoFiles = new List<FileDescriptorProto>();
        var cis = new CodedInputStream(bytes);
        uint tag;
        while ((tag = cis.ReadTag()) != 0)
        {
            int fieldNum = WireFormat.GetTagFieldNumber(tag);
            switch (fieldNum)
            {
                case 1:
                    filesToGenerate.Add(cis.ReadString());
                    break;
                case 15:
                    {
                        ByteString bs = cis.ReadBytes();
                        protoFiles.Add(FileDescriptorProto.Parser.ParseFrom(bs));
                        break;
                    }
                default:
                    cis.SkipLastField();
                    break;
            }
        }

        return (filesToGenerate, protoFiles);
    }

    private static byte[] EncodeResponse(string? error, IReadOnlyList<(string Name, string Content)> files)
    {
        using var ms = new MemoryStream();
        using (var cos = new CodedOutputStream(ms, leaveOpen: true))
        {
            if (!string.IsNullOrEmpty(error))
            {
                cos.WriteTag(1, WireFormat.WireType.LengthDelimited);
                cos.WriteString(error);
            }

            cos.WriteTag(2, WireFormat.WireType.Varint);
            cos.WriteUInt64(FeatureProto3Optional);

            foreach ((string name, string content) in files)
            {
                using var fileMs = new MemoryStream();
                using (var fileCos = new CodedOutputStream(fileMs, leaveOpen: true))
                {
                    fileCos.WriteTag(1, WireFormat.WireType.LengthDelimited);
                    fileCos.WriteString(name);
                    fileCos.WriteTag(15, WireFormat.WireType.LengthDelimited);
                    fileCos.WriteString(content);
                }

                byte[] filePayload = fileMs.ToArray();
                cos.WriteTag(15, WireFormat.WireType.LengthDelimited);
                cos.WriteBytes(ByteString.CopyFrom(filePayload));
            }
        }

        return ms.ToArray();
    }

    private sealed class TypeSymbol
    {
        public required string ProtoFullName { get; init; }
        public required string CSharpFullName { get; init; }
        public required string CSharpOpaqueFullName { get; init; }
        public required string SimpleName { get; init; }
        public DescriptorProto? Message { get; init; }
        public EnumDescriptorProto? Enum { get; init; }
        public bool IsMapEntry => Message?.Options?.MapEntry == true;
    }

    private static Dictionary<string, TypeSymbol> BuildSymbolTable(List<FileDescriptorProto> files)
    {
        var table = new Dictionary<string, TypeSymbol>(StringComparer.Ordinal);
        foreach (FileDescriptorProto file in files)
        {
            string ns = GetCSharpNamespace(file);
            string protoPrefix = string.IsNullOrEmpty(file.Package) ? string.Empty : "." + file.Package;
            string csPrefix = string.IsNullOrEmpty(ns) ? "global::" : $"global::{ns}.";

            foreach (EnumDescriptorProto en in file.EnumType)
            {
                string protoName = $"{protoPrefix}.{en.Name}";
                table[protoName] = new TypeSymbol
                {
                    ProtoFullName = protoName,
                    CSharpFullName = csPrefix + en.Name,
                    CSharpOpaqueFullName = csPrefix + en.Name,
                    SimpleName = en.Name,
                    Enum = en,
                };
            }

            foreach (DescriptorProto msg in file.MessageType)
            {
                RegisterMessageSymbols(table, msg, protoPrefix, csPrefix, isNested: false);
            }
        }

        return table;
    }

    private static void RegisterMessageSymbols(
        Dictionary<string, TypeSymbol> table,
        DescriptorProto msg,
        string protoParent,
        string csParent,
        bool isNested)
    {
        string protoName = $"{protoParent}.{msg.Name}";
        string csName = isNested ? $"{csParent}Types.{msg.Name}" : $"{csParent}{msg.Name}";
        string csOpaqueName = isNested ? $"{csParent}Types.Opaque{msg.Name}" : $"{csParent}Opaque{msg.Name}";

        table[protoName] = new TypeSymbol
        {
            ProtoFullName = protoName,
            CSharpFullName = csName,
            CSharpOpaqueFullName = csOpaqueName,
            SimpleName = msg.Name,
            Message = msg,
        };

        foreach (EnumDescriptorProto en in msg.EnumType)
        {
            string enProto = $"{protoName}.{en.Name}";
            table[enProto] = new TypeSymbol
            {
                ProtoFullName = enProto,
                CSharpFullName = $"{csName}.Types.{en.Name}",
                CSharpOpaqueFullName = $"{csName}.Types.{en.Name}",
                SimpleName = en.Name,
                Enum = en,
            };
        }

        foreach (DescriptorProto nested in msg.NestedType)
        {
            RegisterMessageSymbols(table, nested, protoName, csName + ".", isNested: true);
        }
    }

    private static string ComputeOutputFileName(string protoFileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(protoFileName);
        string pascal = ToPascalCase(baseName);
        return $"{pascal}.OmniBuf.g.cs";
    }

    private static string GetCSharpNamespace(FileDescriptorProto file)
    {
        if (file.Options is not null && !string.IsNullOrEmpty(file.Options.CsharpNamespace))
        {
            return file.Options.CsharpNamespace;
        }

        if (string.IsNullOrEmpty(file.Package))
        {
            return string.Empty;
        }

        string[] parts = file.Package.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = ToPascalCase(parts[i]);
        }

        return string.Join(".", parts);
    }

    private static string ToPascalCase(string input)
    {
        var sb = new StringBuilder(input.Length);
        bool capNext = true;
        foreach (char c in input)
        {
            if (c is >= 'a' and <= 'z')
            {
                sb.Append(capNext ? char.ToUpperInvariant(c) : c);
                capNext = false;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                sb.Append(c);
                capNext = false;
            }
            else if (c is >= '0' and <= '9')
            {
                sb.Append(c);
                capNext = true;
            }
            else
            {
                capNext = true;
            }
        }

        return sb.ToString();
    }

    private static string GetPropertyName(FieldDescriptorProto field, string enclosingMsgName)
    {
        string prop = ToPascalCase(field.Name);
        if (prop == enclosingMsgName || prop == "Types")
        {
            prop += "_";
        }

        return prop;
    }

    private static string GenerateFile(FileDescriptorProto file, Dictionary<string, TypeSymbol> symbols)
    {
        var sb = new StringBuilder(16384);
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("//     Generated by protoc-gen-omnibuf-cs. DO NOT EDIT!");
        sb.AppendLine($"//     source: {file.Name}");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591, CS0612, CS3021, CS8981");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using Google.Protobuf;");
        sb.AppendLine("using OmniBuf;");
        sb.AppendLine();

        string ns = GetCSharpNamespace(file);
        if (!string.IsNullOrEmpty(ns))
        {
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
        }

        foreach (DescriptorProto msg in file.MessageType)
        {
            if (msg.Options?.MapEntry == true)
            {
                continue;
            }

            EmitMessageCodecAndOpaque(sb, msg, symbols);
        }

        return sb.ToString();
    }

    private static void EmitMessageCodecAndOpaque(
        StringBuilder sb,
        DescriptorProto msg,
        Dictionary<string, TypeSymbol> symbols)
    {
        List<FieldDescriptorProto> orderedFields = msg.Field.OrderBy(f => f.Number).ToList();

        // 1. Partial class extending protoc --csharp_out message
        sb.AppendLine($"public sealed partial class {msg.Name}");
        sb.AppendLine("{");

        EmitSchemaMetaBuilder(sb, msg, orderedFields, symbols);
        EmitMarshalMethods(sb, orderedFields, symbols, msg.Name);
        EmitUnmarshalMethods(sb, msg, orderedFields, symbols);

        sb.AppendLine("}");
        sb.AppendLine();

        // 2. Opaque lazy header-hop view + zero-decode splicer
        EmitOpaqueClass(sb, msg, orderedFields, symbols);
    }

    private static void EmitSchemaMetaBuilder(
        StringBuilder sb,
        DescriptorProto msg,
        List<FieldDescriptorProto> fields,
        Dictionary<string, TypeSymbol> symbols)
    {
        sb.AppendLine("    public static SchemaMeta? BuildOmniBufSchemaMeta(int depth = 0, HashSet<string>? active = null)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (depth > OmniBufConstants.MaxNestingDepth)");
        sb.AppendLine("        {");
        sb.AppendLine("            return null;");
        sb.AppendLine("        }");
        sb.AppendLine("        active ??= new HashSet<string>(StringComparer.Ordinal);");
        sb.AppendLine($"        if (!active.Add(\"{msg.Name}\"))");
        sb.AppendLine("        {");
        sb.AppendLine("            return null;");
        sb.AppendLine("        }");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            var meta = new SchemaMeta");
        sb.AppendLine("            {");
        sb.AppendLine($"                TypeName = \"{msg.Name}\",");
        sb.AppendLine($"                Fields = new Dictionary<uint, string>({fields.Count}),");
        sb.AppendLine("            };");

        foreach (FieldDescriptorProto f in fields)
        {
            uint fnum = (uint)f.Number;
            sb.AppendLine($"            meta.Fields[{fnum}u] = \"{f.Name}\";");

            TypeSymbol? childMsgSym = ResolveChildMessageSymbol(f, symbols, out int depthDelta);
            if (childMsgSym is not null)
            {
                sb.AppendLine($"            if ({childMsgSym.CSharpFullName}.BuildOmniBufSchemaMeta(depth + {depthDelta}, active) is {{ }} child_{fnum})");
                sb.AppendLine("            {");
                sb.AppendLine($"                meta.Children[{fnum}u] = child_{fnum};");
                sb.AppendLine("            }");
            }
        }

        sb.AppendLine("            return meta;");
        sb.AppendLine("        }");
        sb.AppendLine("        finally");
        sb.AppendLine("        {");
        sb.AppendLine($"            active.Remove(\"{msg.Name}\");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static TypeSymbol? ResolveChildMessageSymbol(
        FieldDescriptorProto f,
        Dictionary<string, TypeSymbol> symbols,
        out int depthDelta)
    {
        depthDelta = 1;
        if (f.Type != FieldDescriptorProto.Types.Type.Message)
        {
            return null;
        }

        if (!symbols.TryGetValue(f.TypeName, out TypeSymbol? sym))
        {
            return null;
        }

        if (sym.IsMapEntry)
        {
            FieldDescriptorProto valField = sym.Message!.Field.First(x => x.Number == 2);
            if (valField.Type == FieldDescriptorProto.Types.Type.Message
                && symbols.TryGetValue(valField.TypeName, out TypeSymbol? mapValSym))
            {
                depthDelta = 2;
                return mapValSym;
            }

            return null;
        }

        if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
        {
            depthDelta = 2;
        }

        return sym;
    }

    private static void EmitMarshalMethods(
        StringBuilder sb,
        List<FieldDescriptorProto> fields,
        Dictionary<string, TypeSymbol> symbols,
        string msgName)
    {
        sb.AppendLine("    public byte[] MarshalOmniBuf(MarshalOptions options = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        OmniBufEncoder encoder = OmniBufEncoder.RentThreadLocal(out bool rented);");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            MarshalOmniBuf(encoder, options);");
        sb.AppendLine("            return encoder.ToArray();");
        sb.AppendLine("        }");
        sb.AppendLine("        finally");
        sb.AppendLine("        {");
        sb.AppendLine("            OmniBufEncoder.ReturnThreadLocal(encoder, rented);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public void MarshalOmniBuf(OmniBufEncoder encoder, MarshalOptions options = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (options.SelfDescribing)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (BuildOmniBufSchemaMeta() is { } meta)");
        sb.AppendLine("            {");
        sb.AppendLine("                SchemaMeta.WriteField0(encoder, meta, options.Deterministic);");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        EncodeOmniBufBody(encoder, options.Deterministic);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal void EncodeOmniBufBody(OmniBufEncoder encoder, bool deterministic)");
        sb.AppendLine("    {");

        foreach (FieldDescriptorProto f in fields)
        {
            uint fnum = (uint)f.Number;
            string prop = GetPropertyName(f, msgName);

            if (IsMapField(f, symbols, out FieldDescriptorProto? keyField, out FieldDescriptorProto? valField))
            {
                string keyWt = WireTypeEnumName(keyField!.Type);
                string valWt = WireTypeEnumName(valField!.Type);
                string keyCs = CSharpScalarType(keyField, symbols);
                sb.AppendLine($"        encoder.BeginMapField({fnum}u, OmniBufWireType.{keyWt}, OmniBufWireType.{valWt}, this.{prop}.Count);");
                sb.AppendLine($"        if (this.{prop}.Count > 0)");
                sb.AppendLine("        {");
                sb.AppendLine($"            if (deterministic && this.{prop}.Count > 1)");
                sb.AppendLine("            {");
                sb.AppendLine($"                var keys_{fnum} = new List<{keyCs}>(this.{prop}.Keys);");
                if (keyField.Type == FieldDescriptorProto.Types.Type.String)
                {
                    sb.AppendLine($"                keys_{fnum}.Sort(StringComparer.Ordinal);");
                }
                else
                {
                    sb.AppendLine($"                keys_{fnum}.Sort();");
                }

                sb.AppendLine($"                for (int i = 0; i < keys_{fnum}.Count; i++)");
                sb.AppendLine("                {");
                sb.AppendLine($"                    var k = keys_{fnum}[i];");
                sb.AppendLine($"                    var v = this.{prop}[k];");
                sb.AppendLine($"                    {EmitWriteRawValue("encoder", keyField, "k")};");
                EmitMapValEncode(sb, "                    ", valField, "v");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
                sb.AppendLine("            else");
                sb.AppendLine("            {");
                sb.AppendLine($"                foreach (var kv in this.{prop})");
                sb.AppendLine("                {");
                sb.AppendLine($"                    {EmitWriteRawValue("encoder", keyField, "kv.Key")};");
                EmitMapValEncode(sb, "                    ", valField, "kv.Value");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine("        encoder.EndMap();");
                continue;
            }

            if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
            {
                string elemWt = WireTypeEnumName(f.Type);
                sb.AppendLine($"        encoder.BeginArrayField({fnum}u, OmniBufWireType.{elemWt}, this.{prop}.Count);");
                sb.AppendLine($"        for (int i = 0; i < this.{prop}.Count; i++)");
                sb.AppendLine("        {");
                if (f.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    sb.AppendLine("            encoder.BeginContainer();");
                    sb.AppendLine($"            this.{prop}[i]?.EncodeOmniBufBody(encoder, deterministic);");
                    sb.AppendLine("            encoder.EndContainer();");
                }
                else
                {
                    sb.AppendLine($"            {EmitWriteRawValue("encoder", f, $"this.{prop}[i]")};");
                }

                sb.AppendLine("        }");
                sb.AppendLine("        encoder.EndArray();");
                continue;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                sb.AppendLine($"        if (this.{prop} is not null)");
                sb.AppendLine("        {");
                sb.AppendLine($"            encoder.BeginSubMsgField({fnum}u);");
                sb.AppendLine($"            this.{prop}.EncodeOmniBufBody(encoder, deterministic);");
                sb.AppendLine("            encoder.EndSubMsg();");
                sb.AppendLine("        }");
                continue;
            }

            if (f.Proto3Optional)
            {
                sb.AppendLine($"        if (this.Has{prop})");
                sb.AppendLine("        {");
                sb.AppendLine($"            {EmitWriteField("encoder", fnum, f, $"this.{prop}")};");
                sb.AppendLine("        }");
                continue;
            }

            sb.AppendLine($"        {EmitWriteField("encoder", fnum, f, $"this.{prop}")};");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void EmitMapValEncode(StringBuilder sb, string indent, FieldDescriptorProto valField, string expr)
    {
        if (valField.Type == FieldDescriptorProto.Types.Type.Message)
        {
            sb.AppendLine($"{indent}encoder.BeginContainer();");
            sb.AppendLine($"{indent}{expr}?.EncodeOmniBufBody(encoder, deterministic);");
            sb.AppendLine($"{indent}encoder.EndContainer();");
        }
        else
        {
            sb.AppendLine($"{indent}{EmitWriteRawValue("encoder", valField, expr)};");
        }
    }

    private static void EmitUnmarshalMethods(
        StringBuilder sb,
        DescriptorProto msg,
        List<FieldDescriptorProto> fields,
        Dictionary<string, TypeSymbol> symbols)
    {
        sb.AppendLine($"    public static {msg.Name} ParseOmniBuf(ReadOnlySpan<byte> data)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var msg = new {msg.Name}();");
        sb.AppendLine("        msg.UnmarshalOmniBuf(data);");
        sb.AppendLine("        return msg;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public void UnmarshalOmniBuf(ReadOnlySpan<byte> data)");
        sb.AppendLine("    {");
        sb.AppendLine("        DecodeOmniBuf(data, 0, OmniBufConstants.MaxNestingDepth, merge: false);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal void DecodeOmniBuf(ReadOnlySpan<byte> data, int depth, int maxDepth, bool merge)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (depth > maxDepth)");
        sb.AppendLine("        {");
        sb.AppendLine("            throw new OmniBufException($\"omnibuf: nesting depth {depth} exceeds maximum {maxDepth}\");");
        sb.AppendLine("        }");
        sb.AppendLine("        ulong seenLow = 0;");
        bool hasHighFields = fields.Any(f => f.Number >= 64);
        if (hasHighFields)
        {
            sb.AppendLine("        HashSet<uint>? seenHigh = null;");
        }

        sb.AppendLine("        int pos = 0;");
        sb.AppendLine("        while (pos < data.Length)");
        sb.AppendLine("        {");
        sb.AppendLine("            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);");
        sb.AppendLine("            switch (fnum)");
        sb.AppendLine("            {");
        sb.AppendLine("                case 0:");
        sb.AppendLine("                    SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);");
        sb.AppendLine("                    break;");

        foreach (FieldDescriptorProto f in fields)
        {
            uint fnum = (uint)f.Number;
            string prop = GetPropertyName(f, msg.Name);
            string seenCheckExpr = fnum < 64
                ? $"((seenLow & (1UL << {(int)fnum})) != 0)"
                : $"(seenHigh is not null && seenHigh.Contains({fnum}u))";
            string markSeenStmt = fnum < 64
                ? $"seenLow |= (1UL << {(int)fnum});"
                : $"(seenHigh ??= new HashSet<uint>()).Add({fnum}u);";

            sb.AppendLine($"                case {fnum}u:");
            sb.AppendLine("                {");

            if (IsMapField(f, symbols, out FieldDescriptorProto? keyField, out FieldDescriptorProto? valField))
            {
                string keyWt = WireTypeEnumName(keyField!.Type);
                string valWt = WireTypeEnumName(valField!.Type);
                sb.AppendLine("                    if (wt != OmniBufWireType.Map)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);");
                sb.AppendLine("                        break;");
                sb.AppendLine("                    }");
                sb.AppendLine("                    if (depth + 1 > maxDepth)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        throw new OmniBufException($\"omnibuf: WireMap depth {depth + 1} exceeds maximum {maxDepth}\");");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    bool alreadySeen = {seenCheckExpr} || merge;");
                sb.AppendLine($"                    {markSeenStmt}");
                sb.AppendLine("                    ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 3);");
                sb.AppendLine($"                    if (!OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType kWt, out OmniBufWireType vWt, out int count, out int p) || kWt != OmniBufWireType.{keyWt} || vWt != OmniBufWireType.{valWt})");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        throw new OmniBufException(\"omnibuf: invalid WireMap header on {msg.Name}.{f.Name}\");");
                sb.AppendLine("                    }");
                sb.AppendLine("                    if (!alreadySeen)");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        this.{prop}.Clear();");
                sb.AppendLine("                    }");
                sb.AppendLine("                    for (int i = 0; i < count; i++)");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        var k = {EmitReadRawValue("body", "p", keyField, symbols)};");
                if (valField.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    string valMsgCs = symbols[valField.TypeName].CSharpFullName;
                    sb.AppendLine("                        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);");
                    sb.AppendLine($"                        var v = new {valMsgCs}();");
                    sb.AppendLine("                        v.DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                }
                else
                {
                    sb.AppendLine($"                        var v = {EmitReadRawValue("body", "p", valField, symbols)};");
                }

                sb.AppendLine($"                        this.{prop}[k] = v;");
                sb.AppendLine("                    }");
                sb.AppendLine("                    if (p != body.Length)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        throw new OmniBufException(\"omnibuf: trailing bytes in WireMap\");");
                sb.AppendLine("                    }");
                sb.AppendLine("                    break;");
                sb.AppendLine("                }");
                continue;
            }

            if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
            {
                string elemWt = WireTypeEnumName(f.Type);
                sb.AppendLine("                    if (wt != OmniBufWireType.Array)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);");
                sb.AppendLine("                        break;");
                sb.AppendLine("                    }");
                sb.AppendLine("                    if (depth + 1 > maxDepth)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        throw new OmniBufException($\"omnibuf: WireArray depth {depth + 1} exceeds maximum {maxDepth}\");");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    bool alreadySeen = {seenCheckExpr} || merge;");
                sb.AppendLine($"                    {markSeenStmt}");
                sb.AppendLine("                    ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 2);");
                sb.AppendLine($"                    if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p) || elemWt != OmniBufWireType.{elemWt})");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        throw new OmniBufException(\"omnibuf: invalid WireArray header on {msg.Name}.{f.Name}\");");
                sb.AppendLine("                    }");
                if (f.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    string elemMsgCs = symbols[f.TypeName].CSharpFullName;
                    sb.AppendLine("                    if (!alreadySeen)");
                    sb.AppendLine("                    {");
                    sb.AppendLine($"                        int prevCount = this.{prop}.Count;");
                    sb.AppendLine("                        for (int i = 0; i < count; i++)");
                    sb.AppendLine("                        {");
                    sb.AppendLine("                            ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);");
                    sb.AppendLine("                            if (i < prevCount)");
                    sb.AppendLine("                            {");
                    sb.AppendLine($"                                this.{prop}[i].DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                    sb.AppendLine("                            }");
                    sb.AppendLine("                            else");
                    sb.AppendLine("                            {");
                    sb.AppendLine($"                                var elem = new {elemMsgCs}();");
                    sb.AppendLine("                                elem.DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                    sb.AppendLine($"                                this.{prop}.Add(elem);");
                    sb.AppendLine("                            }");
                    sb.AppendLine("                        }");
                    sb.AppendLine("                        for (int r = prevCount - 1; r >= count; r--)");
                    sb.AppendLine("                        {");
                    sb.AppendLine($"                            this.{prop}.RemoveAt(r);");
                    sb.AppendLine("                        }");
                    sb.AppendLine("                    }");
                    sb.AppendLine("                    else");
                    sb.AppendLine("                    {");
                    sb.AppendLine("                        for (int i = 0; i < count; i++)");
                    sb.AppendLine("                        {");
                    sb.AppendLine("                            ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);");
                    sb.AppendLine($"                            var elem = new {elemMsgCs}();");
                    sb.AppendLine("                            elem.DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                    sb.AppendLine($"                            this.{prop}.Add(elem);");
                    sb.AppendLine("                        }");
                    sb.AppendLine("                    }");
                }
                else
                {
                    sb.AppendLine("                    if (!alreadySeen)");
                    sb.AppendLine("                    {");
                    sb.AppendLine($"                        this.{prop}.Clear();");
                    sb.AppendLine("                    }");
                    sb.AppendLine("                    for (int i = 0; i < count; i++)");
                    sb.AppendLine("                    {");
                    sb.AppendLine($"                        this.{prop}.Add({EmitReadRawValue("body", "p", f, symbols)});");
                    sb.AppendLine("                    }");
                }
                sb.AppendLine("                    if (p != body.Length)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        throw new OmniBufException(\"omnibuf: trailing bytes in WireArray\");");
                sb.AppendLine("                    }");
                sb.AppendLine("                    break;");
                sb.AppendLine("                }");
                continue;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Bool)
            {
                sb.AppendLine("                    if (wt != OmniBufWireType.BoolFalse && wt != OmniBufWireType.BoolTrue)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);");
                sb.AppendLine("                        break;");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    {markSeenStmt}");
                sb.AppendLine($"                    this.{prop} = wt == OmniBufWireType.BoolTrue;");
                sb.AppendLine("                    break;");
                sb.AppendLine("                }");
                continue;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                string subCs = symbols[f.TypeName].CSharpFullName;
                sb.AppendLine("                    if (wt != OmniBufWireType.SubMsg)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);");
                sb.AppendLine("                        break;");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    bool alreadySeen = {seenCheckExpr} || merge;");
                sb.AppendLine($"                    {markSeenStmt}");
                sb.AppendLine("                    ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);");
                sb.AppendLine($"                    if (this.{prop} is null)");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        this.{prop} = new {subCs}();");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    this.{prop}.DecodeOmniBuf(sub, depth + 1, maxDepth, merge: alreadySeen);");
                sb.AppendLine("                    break;");
                sb.AppendLine("                }");
                continue;
            }

            string expWt = WireTypeEnumName(f.Type);
            sb.AppendLine($"                    if (wt != OmniBufWireType.{expWt})");
            sb.AppendLine("                    {");
            sb.AppendLine("                        OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);");
            sb.AppendLine("                        break;");
            sb.AppendLine("                    }");
            sb.AppendLine($"                    {markSeenStmt}");
            sb.AppendLine($"                    this.{prop} = {EmitReadRawValue("data", "pos", f, symbols)};");
            sb.AppendLine("                    break;");
            sb.AppendLine("                }");
        }

        sb.AppendLine("                default:");
        sb.AppendLine("                    OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);");
        sb.AppendLine("                    break;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");

        // Reset unseen fields on instance reuse when merge == false
        sb.AppendLine("        if (!merge)");
        sb.AppendLine("        {");
        foreach (FieldDescriptorProto f in fields)
        {
            uint fnum = (uint)f.Number;
            string prop = GetPropertyName(f, msg.Name);
            string notSeen = fnum < 64
                ? $"((seenLow & (1UL << {(int)fnum})) == 0)"
                : $"(seenHigh is null || !seenHigh.Contains({fnum}u))";
            sb.AppendLine($"            if ({notSeen})");
            sb.AppendLine("            {");
            if (IsMapField(f, symbols, out _, out _) || f.Label == FieldDescriptorProto.Types.Label.Repeated)
            {
                sb.AppendLine($"                this.{prop}.Clear();");
            }
            else if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                sb.AppendLine($"                this.{prop} = null;");
            }
            else if (f.Proto3Optional)
            {
                sb.AppendLine($"                this.Clear{prop}();");
            }
            else
            {
                sb.AppendLine($"                this.{prop} = {DefaultValueLiteral(f, symbols)};");
            }

            sb.AppendLine("            }");
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static void EmitOpaqueClass(
        StringBuilder sb,
        DescriptorProto msg,
        List<FieldDescriptorProto> fields,
        Dictionary<string, TypeSymbol> symbols)
    {
        string opaqueName = $"Opaque{msg.Name}";
        sb.AppendLine($"public sealed class {opaqueName}");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly byte[] _raw;");
        sb.AppendLine("    private ulong _dirtyMaskLow;");
        sb.AppendLine("    private HashSet<uint>? _dirtyOverflow;");

        foreach (FieldDescriptorProto f in fields)
        {
            string prop = GetPropertyName(f, msg.Name);
            string fieldStorageType = OpaqueStorageType(f, symbols);
            sb.AppendLine($"    private {fieldStorageType} _dirty_{prop};");
        }

        sb.AppendLine();
        sb.AppendLine($"    public {opaqueName}(byte[] raw)");
        sb.AppendLine("    {");
        sb.AppendLine("        _raw = raw ?? Array.Empty<byte>();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public {opaqueName}(ReadOnlySpan<byte> raw)");
        sb.AppendLine("    {");
        sb.AppendLine("        _raw = raw.ToArray();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public ReadOnlySpan<byte> RawSpan => _raw;");
        sb.AppendLine();
        sb.AppendLine("    private bool IsDirty(uint fieldNum) => fieldNum < 64");
        sb.AppendLine("        ? (_dirtyMaskLow & (1UL << (int)fieldNum)) != 0");
        sb.AppendLine("        : (_dirtyOverflow is not null && _dirtyOverflow.Contains(fieldNum));");
        sb.AppendLine();
        sb.AppendLine("    private void MarkDirty(uint fieldNum)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (fieldNum < 64)");
        sb.AppendLine("        {");
        sb.AppendLine("            _dirtyMaskLow |= (1UL << (int)fieldNum);");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");
        sb.AppendLine("            (_dirtyOverflow ??= new HashSet<uint>()).Add(fieldNum);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (FieldDescriptorProto f in fields)
        {
            EmitOpaqueFieldMembers(sb, msg, f, symbols);
        }

        // Emit Splice / MarshalOmniBuf
        sb.AppendLine("    public void ResetDirty()");
        sb.AppendLine("    {");
        sb.AppendLine("        _dirtyMaskLow = 0;");
        sb.AppendLine("        _dirtyOverflow?.Clear();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public void MarshalOmniBuf(OmniBufEncoder encoder, MarshalOptions options = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (options.SelfDescribing)");
        sb.AppendLine("        {");
        sb.AppendLine($"            if ({msg.Name}.BuildOmniBufSchemaMeta() is {{ }} meta)");
        sb.AppendLine("            {");
        sb.AppendLine("                SchemaMeta.WriteField0(encoder, meta, options.Deterministic);");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        OmniBufOpaque.SpliceFieldsTo(encoder, _raw, _dirtyMaskLow, _dirtyOverflow, this, static (self, enc, fnum) => self.AppendDirtyField(enc, fnum));");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public byte[] MarshalOmniBuf(MarshalOptions options = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (!options.SelfDescribing)");
        sb.AppendLine("        {");
        sb.AppendLine("            return (_dirtyMaskLow == 0 && (_dirtyOverflow is null || _dirtyOverflow.Count == 0))");
        sb.AppendLine("                ? (byte[])_raw.Clone()");
        sb.AppendLine("                : OmniBufOpaque.SpliceFields(_raw, _dirtyMaskLow, _dirtyOverflow, this, static (self, enc, fnum) => self.AppendDirtyField(enc, fnum));");
        sb.AppendLine("        }");
        sb.AppendLine("        OmniBufEncoder enc = OmniBufEncoder.RentThreadLocal(out bool rented);");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            MarshalOmniBuf(enc, options);");
        sb.AppendLine("            return enc.ToArray();");
        sb.AppendLine("        }");
        sb.AppendLine("        finally");
        sb.AppendLine("        {");
        sb.AppendLine("            OmniBufEncoder.ReturnThreadLocal(enc, rented);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void AppendDirtyField(OmniBufEncoder encoder, uint fieldNum)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (fieldNum)");
        sb.AppendLine("        {");
        foreach (FieldDescriptorProto f in fields)
        {
            uint fnum = (uint)f.Number;
            string prop = GetPropertyName(f, msg.Name);
            sb.AppendLine($"            case {fnum}u:");
            sb.AppendLine("            {");
            EmitOpaqueAppendDirtyCase(sb, "                ", fnum, prop, f, symbols);
            sb.AppendLine("                break;");
            sb.AppendLine("            }");
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitOpaqueFieldMembers(
        StringBuilder sb,
        DescriptorProto msg,
        FieldDescriptorProto f,
        Dictionary<string, TypeSymbol> symbols)
    {
        uint fnum = (uint)f.Number;
        string prop = GetPropertyName(f, msg.Name);

        if (IsMapField(f, symbols, out FieldDescriptorProto? keyField, out FieldDescriptorProto? valField)
            && keyField!.Type == FieldDescriptorProto.Types.Type.String
            && valField!.Type == FieldDescriptorProto.Types.Type.Uint64)
        {
            sb.AppendLine($"    public IReadOnlyDictionary<string, ulong> {prop}");
            sb.AppendLine("    {");
            sb.AppendLine($"        get => IsDirty({fnum}u) ? (_dirty_{prop} ?? new Dictionary<string, ulong>()) : OmniBufOpaque.ReadStringUint64Map(_raw, {fnum}u);");
            sb.AppendLine("    }");
            sb.AppendLine($"    public void Set{prop}(IDictionary<string, ulong> value)");
            sb.AppendLine("    {");
            sb.AppendLine($"        _dirty_{prop} = new Dictionary<string, ulong>(value, StringComparer.Ordinal);");
            sb.AppendLine($"        MarkDirty({fnum}u);");
            sb.AppendLine("    }");
            sb.AppendLine();
            return;
        }

        if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
        {
            if (f.Type == FieldDescriptorProto.Types.Type.String)
            {
                sb.AppendLine($"    public IReadOnlyList<string> {prop}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => IsDirty({fnum}u) ? ((IReadOnlyList<string>?)_dirty_{prop} ?? Array.Empty<string>()) : OmniBufOpaque.ReadStringArray(_raw, {fnum}u);");
                sb.AppendLine("    }");
                sb.AppendLine($"    public void Set{prop}(IEnumerable<string> values)");
                sb.AppendLine("    {");
                sb.AppendLine($"        _dirty_{prop} = new List<string>(values);");
                sb.AppendLine($"        MarkDirty({fnum}u);");
                sb.AppendLine("    }");
                sb.AppendLine();
                return;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Uint32)
            {
                sb.AppendLine($"    public IReadOnlyList<uint> {prop}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => IsDirty({fnum}u) ? ((IReadOnlyList<uint>?)_dirty_{prop} ?? Array.Empty<uint>()) : OmniBufOpaque.ReadUint32Array(_raw, {fnum}u);");
                sb.AppendLine("    }");
                sb.AppendLine($"    public void Set{prop}(IEnumerable<uint> values)");
                sb.AppendLine("    {");
                sb.AppendLine($"        _dirty_{prop} = new List<uint>(values);");
                sb.AppendLine($"        MarkDirty({fnum}u);");
                sb.AppendLine("    }");
                sb.AppendLine();
                return;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                TypeSymbol sym = symbols[f.TypeName];
                sb.AppendLine($"    public IReadOnlyList<{sym.CSharpOpaqueFullName}> {prop}");
                sb.AppendLine("    {");
                sb.AppendLine("        get");
                sb.AppendLine("        {");
                sb.AppendLine($"            if (IsDirty({fnum}u))");
                sb.AppendLine("            {");
                sb.AppendLine($"                return (IReadOnlyList<{sym.CSharpOpaqueFullName}>?)_dirty_{prop} ?? Array.Empty<{sym.CSharpOpaqueFullName}>();");
                sb.AppendLine("            }");
                sb.AppendLine($"            List<byte[]> payloads = OmniBufOpaque.ReadSubMsgArrayPayloads(_raw, {fnum}u);");
                sb.AppendLine($"            var list = new List<{sym.CSharpOpaqueFullName}>(payloads.Count);");
                sb.AppendLine("            for (int i = 0; i < payloads.Count; i++)");
                sb.AppendLine("            {");
                sb.AppendLine($"                list.Add(new {sym.CSharpOpaqueFullName}(payloads[i]));");
                sb.AppendLine("            }");
                sb.AppendLine("            return list;");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine($"    public void Set{prop}(IEnumerable<{sym.CSharpFullName}> values)");
                sb.AppendLine("    {");
                sb.AppendLine($"        var list = new List<{sym.CSharpOpaqueFullName}>();");
                sb.AppendLine("        foreach (var item in values)");
                sb.AppendLine("        {");
                sb.AppendLine($"            list.Add(new {sym.CSharpOpaqueFullName}(item.MarshalOmniBuf()));");
                sb.AppendLine("        }");
                sb.AppendLine($"        _dirty_{prop} = list;");
                sb.AppendLine($"        MarkDirty({fnum}u);");
                sb.AppendLine("    }");
                sb.AppendLine();
                return;
            }

            // Fallback repeated field via message decode
            string elemCs = CSharpScalarType(f, symbols);
            sb.AppendLine($"    public IReadOnlyList<{elemCs}> {prop}");
            sb.AppendLine("    {");
            sb.AppendLine("        get");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (IsDirty({fnum}u)) return (IReadOnlyList<{elemCs}>?)_dirty_{prop} ?? Array.Empty<{elemCs}>();");
            sb.AppendLine($"            return {msg.Name}.ParseOmniBuf(_raw).{prop};");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine($"    public void Set{prop}(IEnumerable<{elemCs}> values) {{ _dirty_{prop} = new List<{elemCs}>(values); MarkDirty({fnum}u); }}");
            sb.AppendLine();
            return;
        }

        if (f.Type == FieldDescriptorProto.Types.Type.Message)
        {
            TypeSymbol sym = symbols[f.TypeName];
            sb.AppendLine($"    public {sym.CSharpOpaqueFullName}? {prop}");
            sb.AppendLine("    {");
            sb.AppendLine("        get");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (IsDirty({fnum}u)) return _dirty_{prop};");
            sb.AppendLine($"            return OmniBufOpaque.TryReadSubMsgBytes(_raw, {fnum}u, out byte[] sub) ? new {sym.CSharpOpaqueFullName}(sub) : null;");
            sb.AppendLine("        }");
            sb.AppendLine("        set");
            sb.AppendLine("        {");
            sb.AppendLine($"            _dirty_{prop} = value;");
            sb.AppendLine($"            MarkDirty({fnum}u);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine($"    public void Set{prop}({sym.CSharpFullName}? value)");
            sb.AppendLine("    {");
            sb.AppendLine($"        _dirty_{prop} = value is null ? null : new {sym.CSharpOpaqueFullName}(value.MarshalOmniBuf());");
            sb.AppendLine($"        MarkDirty({fnum}u);");
            sb.AppendLine("    }");

            // Direct nested string header-hop helpers (e.g., GetLocationCity) with zero sub-message allocation!
            if (sym.Message is not null)
            {
                foreach (FieldDescriptorProto cf in sym.Message.Field)
                {
                    if (cf.Label != FieldDescriptorProto.Types.Label.Repeated && cf.Type == FieldDescriptorProto.Types.Type.String)
                    {
                        string childProp = GetPropertyName(cf, sym.Message.Name);
                        sb.AppendLine($"    public string Get{prop}{childProp}()");
                        sb.AppendLine("    {");
                        sb.AppendLine($"        if (IsDirty({fnum}u)) return _dirty_{prop}?.{childProp} ?? string.Empty;");
                        sb.AppendLine($"        return OmniBufOpaque.TryReadNestedString(_raw, {fnum}u, {(uint)cf.Number}u, out string s) ? s : string.Empty;");
                        sb.AppendLine("    }");
                    }
                }
            }

            sb.AppendLine();
            return;
        }

        if (f.Type == FieldDescriptorProto.Types.Type.Bytes)
        {
            sb.AppendLine($"    public byte[] {prop}");
            sb.AppendLine("    {");
            sb.AppendLine($"        get => IsDirty({fnum}u) ? (_dirty_{prop} ?? Array.Empty<byte>()) : (OmniBufOpaque.TryReadBytes(_raw, {fnum}u, out byte[] b) ? b : Array.Empty<byte>());");
            sb.AppendLine($"        set {{ _dirty_{prop} = value ?? Array.Empty<byte>(); MarkDirty({fnum}u); }}");
            sb.AppendLine("    }");
            sb.AppendLine();
            return;
        }

        string csType = CSharpScalarType(f, symbols);
        string readExpr = EmitOpaqueScalarRead(fnum, f, symbols);
        sb.AppendLine($"    public {csType} {prop}");
        sb.AppendLine("    {");
        sb.AppendLine($"        get => IsDirty({fnum}u) ? _dirty_{prop}! : ({readExpr});");
        sb.AppendLine($"        set {{ _dirty_{prop} = value; MarkDirty({fnum}u); }}");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static string EmitOpaqueScalarRead(uint fnum, FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols) => f.Type switch
    {
        FieldDescriptorProto.Types.Type.Uint32 => $"OmniBufOpaque.TryReadUint(_raw, {fnum}u, out ulong v) ? (uint)v : 0u",
        FieldDescriptorProto.Types.Type.Uint64 => $"OmniBufOpaque.TryReadUint(_raw, {fnum}u, out ulong v) ? v : 0UL",
        FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32 => $"OmniBufOpaque.TryReadSint(_raw, {fnum}u, out long v) ? (int)v : 0",
        FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 => $"OmniBufOpaque.TryReadSint(_raw, {fnum}u, out long v) ? v : 0L",
        FieldDescriptorProto.Types.Type.Enum => $"OmniBufOpaque.TryReadSint(_raw, {fnum}u, out long v) ? ({symbols[f.TypeName].CSharpFullName})(int)v : default",
        FieldDescriptorProto.Types.Type.Bool => $"OmniBufOpaque.TryReadBool(_raw, {fnum}u, out bool v) && v",
        FieldDescriptorProto.Types.Type.Fixed32 => $"OmniBufOpaque.TryReadFixed32(_raw, {fnum}u, OmniBufWireType.Fixed32, out uint v) ? v : 0u",
        FieldDescriptorProto.Types.Type.Sfixed32 => $"OmniBufOpaque.TryReadFixed32(_raw, {fnum}u, OmniBufWireType.Sfixed32, out uint v) ? (int)v : 0",
        FieldDescriptorProto.Types.Type.Float => $"OmniBufOpaque.TryReadFixed32(_raw, {fnum}u, OmniBufWireType.Float32, out uint v) ? BitConverter.UInt32BitsToSingle(v) : 0f",
        FieldDescriptorProto.Types.Type.Fixed64 => $"OmniBufOpaque.TryReadFixed64(_raw, {fnum}u, OmniBufWireType.Fixed64, out ulong v) ? v : 0UL",
        FieldDescriptorProto.Types.Type.Sfixed64 => $"OmniBufOpaque.TryReadFixed64(_raw, {fnum}u, OmniBufWireType.Sfixed64, out ulong v) ? (long)v : 0L",
        FieldDescriptorProto.Types.Type.Double => $"OmniBufOpaque.TryReadFixed64(_raw, {fnum}u, OmniBufWireType.Float64, out ulong v) ? BitConverter.UInt64BitsToDouble(v) : 0d",
        FieldDescriptorProto.Types.Type.String => $"OmniBufOpaque.TryReadString(_raw, {fnum}u, out string v) ? v : string.Empty",
        _ => "default!",
    };

    private static void EmitOpaqueAppendDirtyCase(
        StringBuilder sb,
        string indent,
        uint fnum,
        string prop,
        FieldDescriptorProto f,
        Dictionary<string, TypeSymbol> symbols)
    {
        if (IsMapField(f, symbols, out FieldDescriptorProto? keyField, out FieldDescriptorProto? valField)
            && keyField!.Type == FieldDescriptorProto.Types.Type.String
            && valField!.Type == FieldDescriptorProto.Types.Type.Uint64)
        {
            sb.AppendLine($"{indent}var map = _dirty_{prop};");
            sb.AppendLine($"{indent}int count = map?.Count ?? 0;");
            sb.AppendLine($"{indent}encoder.BeginMapField({fnum}u, OmniBufWireType.String, OmniBufWireType.Uint, count);");
            sb.AppendLine($"{indent}if (map is not null && count > 0)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    var keys = new List<string>(map.Keys);");
            sb.AppendLine($"{indent}    keys.Sort(StringComparer.Ordinal);");
            sb.AppendLine($"{indent}    foreach (string k in keys)");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        encoder.WriteStringValue(k);");
            sb.AppendLine($"{indent}        encoder.WriteUintValue(map[k]);");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}encoder.EndMap();");
            return;
        }

        if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
        {
            string elemWt = WireTypeEnumName(f.Type);
            sb.AppendLine($"{indent}var list = _dirty_{prop};");
            sb.AppendLine($"{indent}int count = list?.Count ?? 0;");
            sb.AppendLine($"{indent}encoder.BeginArrayField({fnum}u, OmniBufWireType.{elemWt}, count);");
            sb.AppendLine($"{indent}if (list is not null)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    for (int i = 0; i < count; i++)");
            sb.AppendLine($"{indent}    {{");
            if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                sb.AppendLine($"{indent}        byte[] elemBytes = list[i].MarshalOmniBuf();");
                sb.AppendLine($"{indent}        encoder.WriteBytesValue(elemBytes);");
            }
            else
            {
                sb.AppendLine($"{indent}        {EmitWriteRawValue("encoder", f, "list[i]")};");
            }

            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}encoder.EndArray();");
            return;
        }

        if (f.Type == FieldDescriptorProto.Types.Type.Message)
        {
            sb.AppendLine($"{indent}if (_dirty_{prop} is not null)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    byte[] subBytes = _dirty_{prop}.MarshalOmniBuf();");
            sb.AppendLine($"{indent}    encoder.WriteTag({fnum}u, OmniBufWireType.SubMsg);");
            sb.AppendLine($"{indent}    encoder.WriteBytesValue(subBytes);");
            sb.AppendLine($"{indent}}}");
            return;
        }

        if (f.Type == FieldDescriptorProto.Types.Type.Bytes)
        {
            sb.AppendLine($"{indent}encoder.WriteBytesField({fnum}u, _dirty_{prop} ?? Array.Empty<byte>());");
            return;
        }

        sb.AppendLine($"{indent}{EmitWriteField("encoder", fnum, f, $"_dirty_{prop}!")};");
    }

    private static string OpaqueStorageType(FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols)
    {
        if (IsMapField(f, symbols, out FieldDescriptorProto? kf, out FieldDescriptorProto? vf))
        {
            return $"Dictionary<{CSharpScalarType(kf!, symbols)}, {CSharpScalarType(vf!, symbols)}>?";
        }

        if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
        {
            if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                return $"List<{symbols[f.TypeName].CSharpOpaqueFullName}>?";
            }

            return $"List<{CSharpScalarType(f, symbols)}>?";
        }

        if (f.Type == FieldDescriptorProto.Types.Type.Message)
        {
            return $"{symbols[f.TypeName].CSharpOpaqueFullName}?";
        }

        if (f.Type == FieldDescriptorProto.Types.Type.Bytes)
        {
            return "byte[]?";
        }

        if (f.Type == FieldDescriptorProto.Types.Type.String)
        {
            return "string?";
        }

        return CSharpScalarType(f, symbols);
    }

    private static bool IsMapField(
        FieldDescriptorProto f,
        Dictionary<string, TypeSymbol> symbols,
        out FieldDescriptorProto? keyField,
        out FieldDescriptorProto? valField)
    {
        keyField = null;
        valField = null;
        if (f.Label == FieldDescriptorProto.Types.Label.Repeated
            && f.Type == FieldDescriptorProto.Types.Type.Message
            && symbols.TryGetValue(f.TypeName, out TypeSymbol? sym)
            && sym.IsMapEntry)
        {
            keyField = sym.Message!.Field.First(x => x.Number == 1);
            valField = sym.Message!.Field.First(x => x.Number == 2);
            return true;
        }

        return false;
    }

    private static string WireTypeEnumName(FieldDescriptorProto.Types.Type t) => t switch
    {
        FieldDescriptorProto.Types.Type.Uint32 or FieldDescriptorProto.Types.Type.Uint64 => "Uint",
        FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32
            or FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64
            or FieldDescriptorProto.Types.Type.Enum => "Sint",
        FieldDescriptorProto.Types.Type.Bool => "BoolFalse",
        FieldDescriptorProto.Types.Type.Fixed32 => "Fixed32",
        FieldDescriptorProto.Types.Type.Fixed64 => "Fixed64",
        FieldDescriptorProto.Types.Type.Sfixed32 => "Sfixed32",
        FieldDescriptorProto.Types.Type.Sfixed64 => "Sfixed64",
        FieldDescriptorProto.Types.Type.Float => "Float32",
        FieldDescriptorProto.Types.Type.Double => "Float64",
        FieldDescriptorProto.Types.Type.String => "String",
        FieldDescriptorProto.Types.Type.Bytes => "Bytes",
        FieldDescriptorProto.Types.Type.Message => "SubMsg",
        _ => throw new InvalidOperationException($"Unsupported proto type {t}"),
    };

    private static string CSharpScalarType(FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols) => f.Type switch
    {
        FieldDescriptorProto.Types.Type.Uint32 or FieldDescriptorProto.Types.Type.Fixed32 => "uint",
        FieldDescriptorProto.Types.Type.Uint64 or FieldDescriptorProto.Types.Type.Fixed64 => "ulong",
        FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32 or FieldDescriptorProto.Types.Type.Sfixed32 => "int",
        FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 or FieldDescriptorProto.Types.Type.Sfixed64 => "long",
        FieldDescriptorProto.Types.Type.Bool => "bool",
        FieldDescriptorProto.Types.Type.Float => "float",
        FieldDescriptorProto.Types.Type.Double => "double",
        FieldDescriptorProto.Types.Type.String => "string",
        FieldDescriptorProto.Types.Type.Bytes => "ByteString",
        FieldDescriptorProto.Types.Type.Enum => symbols[f.TypeName].CSharpFullName,
        FieldDescriptorProto.Types.Type.Message => symbols[f.TypeName].CSharpFullName,
        _ => throw new InvalidOperationException($"Unsupported proto type {f.Type}"),
    };

    private static string DefaultValueLiteral(FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols) => f.Type switch
    {
        FieldDescriptorProto.Types.Type.Uint32 or FieldDescriptorProto.Types.Type.Fixed32 => "0u",
        FieldDescriptorProto.Types.Type.Uint64 or FieldDescriptorProto.Types.Type.Fixed64 => "0UL",
        FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32 or FieldDescriptorProto.Types.Type.Sfixed32 => "0",
        FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 or FieldDescriptorProto.Types.Type.Sfixed64 => "0L",
        FieldDescriptorProto.Types.Type.Bool => "false",
        FieldDescriptorProto.Types.Type.Float => "0f",
        FieldDescriptorProto.Types.Type.Double => "0d",
        FieldDescriptorProto.Types.Type.String => "string.Empty",
        FieldDescriptorProto.Types.Type.Bytes => "ByteString.Empty",
        FieldDescriptorProto.Types.Type.Enum => $"default({symbols[f.TypeName].CSharpFullName})",
        _ => "default!",
    };

    private static string EmitWriteField(string enc, uint fnum, FieldDescriptorProto f, string valExpr) => f.Type switch
    {
        FieldDescriptorProto.Types.Type.Uint32 or FieldDescriptorProto.Types.Type.Uint64 => $"{enc}.WriteUintField({fnum}u, {valExpr})",
        FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32
            or FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 => $"{enc}.WriteSintField({fnum}u, {valExpr})",
        FieldDescriptorProto.Types.Type.Enum => $"{enc}.WriteSintField({fnum}u, (int){valExpr})",
        FieldDescriptorProto.Types.Type.Bool => $"{enc}.WriteBoolField({fnum}u, {valExpr})",
        FieldDescriptorProto.Types.Type.Fixed32 => $"{enc}.WriteFixed32Field({fnum}u, {valExpr})",
        FieldDescriptorProto.Types.Type.Fixed64 => $"{enc}.WriteFixed64Field({fnum}u, {valExpr})",
        FieldDescriptorProto.Types.Type.Sfixed32 => $"{enc}.WriteSfixed32Field({fnum}u, {valExpr})",
        FieldDescriptorProto.Types.Type.Sfixed64 => $"{enc}.WriteSfixed64Field({fnum}u, {valExpr})",
        FieldDescriptorProto.Types.Type.Float => $"{enc}.WriteFloat32Field({fnum}u, {valExpr})",
        FieldDescriptorProto.Types.Type.Double => $"{enc}.WriteFloat64Field({fnum}u, {valExpr})",
        FieldDescriptorProto.Types.Type.String => $"{enc}.WriteStringField({fnum}u, {valExpr})",
        FieldDescriptorProto.Types.Type.Bytes => $"{enc}.WriteBytesField({fnum}u, {valExpr}.Span)",
        _ => throw new InvalidOperationException($"Unsupported proto type {f.Type}"),
    };

    private static string EmitWriteRawValue(string enc, FieldDescriptorProto f, string valExpr) => f.Type switch
    {
        FieldDescriptorProto.Types.Type.Uint32 or FieldDescriptorProto.Types.Type.Uint64 => $"{enc}.WriteUintValue({valExpr})",
        FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32
            or FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 => $"{enc}.WriteSintValue({valExpr})",
        FieldDescriptorProto.Types.Type.Enum => $"{enc}.WriteSintValue((int){valExpr})",
        FieldDescriptorProto.Types.Type.Bool => $"{enc}.WriteBoolElement({valExpr})",
        FieldDescriptorProto.Types.Type.Fixed32 => $"{enc}.WriteFixed32Value({valExpr})",
        FieldDescriptorProto.Types.Type.Fixed64 => $"{enc}.WriteFixed64Value({valExpr})",
        FieldDescriptorProto.Types.Type.Sfixed32 => $"{enc}.WriteSfixed32Value({valExpr})",
        FieldDescriptorProto.Types.Type.Sfixed64 => $"{enc}.WriteSfixed64Value({valExpr})",
        FieldDescriptorProto.Types.Type.Float => $"{enc}.WriteFloat32Value({valExpr})",
        FieldDescriptorProto.Types.Type.Double => $"{enc}.WriteFloat64Value({valExpr})",
        FieldDescriptorProto.Types.Type.String => $"{enc}.WriteStringValue({valExpr})",
        FieldDescriptorProto.Types.Type.Bytes => $"{enc}.WriteBytesValue({valExpr}.Span)",
        _ => throw new InvalidOperationException($"Unsupported proto type {f.Type}"),
    };

    private static string EmitReadRawValue(string buf, string pos, FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols) => f.Type switch
    {
        FieldDescriptorProto.Types.Type.Uint32 => $"(uint)OmniBufDecoder.ReadVarint({buf}, ref {pos})",
        FieldDescriptorProto.Types.Type.Uint64 => $"OmniBufDecoder.ReadVarint({buf}, ref {pos})",
        FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32 => $"(int)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint({buf}, ref {pos}))",
        FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 => $"OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint({buf}, ref {pos}))",
        FieldDescriptorProto.Types.Type.Enum => $"({symbols[f.TypeName].CSharpFullName})(int)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint({buf}, ref {pos}))",
        FieldDescriptorProto.Types.Type.Bool => $"OmniBufDecoder.ReadBoolElement({buf}, ref {pos})",
        FieldDescriptorProto.Types.Type.Fixed32 => $"OmniBufDecoder.ReadFixed32({buf}, ref {pos})",
        FieldDescriptorProto.Types.Type.Fixed64 => $"OmniBufDecoder.ReadFixed64({buf}, ref {pos})",
        FieldDescriptorProto.Types.Type.Sfixed32 => $"(int)OmniBufDecoder.ReadFixed32({buf}, ref {pos})",
        FieldDescriptorProto.Types.Type.Sfixed64 => $"(long)OmniBufDecoder.ReadFixed64({buf}, ref {pos})",
        FieldDescriptorProto.Types.Type.Float => $"BitConverter.UInt32BitsToSingle(OmniBufDecoder.ReadFixed32({buf}, ref {pos}))",
        FieldDescriptorProto.Types.Type.Double => $"BitConverter.UInt64BitsToDouble(OmniBufDecoder.ReadFixed64({buf}, ref {pos}))",
        FieldDescriptorProto.Types.Type.String => $"OmniBufDecoder.ReadString({buf}, ref {pos})",
        FieldDescriptorProto.Types.Type.Bytes => $"ByteString.CopyFrom(OmniBufDecoder.ReadLenSlice({buf}, ref {pos}, 0))",
        _ => throw new InvalidOperationException($"Unsupported proto type {f.Type}"),
    };
}
