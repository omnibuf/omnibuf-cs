using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace OmniBuf.ProtocGen;

public static class Program
{
    private const ulong FeatureProto3Optional = 1UL;
    private const int MaxFieldNumber = 16_777_215;

    private static readonly HashSet<string> ReservedMemberNames = new(StringComparer.Ordinal)
    {
        "Types",
        "Descriptor",
        "Parser",
        "Equals",
        "ToString",
        "GetHashCode",
        "Clone",
        "CalculateSize",
        "MergeFrom",
        "WriteTo",
    };

    private sealed record WrapperInfo(
        string ShortName,
        string MethodSuffix,
        string ElemCsType,
        string ScalarCsType,
        string DefaultExpr,
        bool IsValueType);

    private static readonly Dictionary<string, WrapperInfo> WrapperTypes = new(StringComparer.Ordinal)
    {
        [".google.protobuf.DoubleValue"] = new("DoubleValue", "Double", "double?", "double", "0d", true),
        [".google.protobuf.FloatValue"] = new("FloatValue", "Float", "float?", "float", "0f", true),
        [".google.protobuf.Int64Value"] = new("Int64Value", "Int64", "long?", "long", "0L", true),
        [".google.protobuf.UInt64Value"] = new("UInt64Value", "UInt64", "ulong?", "ulong", "0UL", true),
        [".google.protobuf.Int32Value"] = new("Int32Value", "Int32", "int?", "int", "0", true),
        [".google.protobuf.UInt32Value"] = new("UInt32Value", "UInt32", "uint?", "uint", "0u", true),
        [".google.protobuf.BoolValue"] = new("BoolValue", "Bool", "bool?", "bool", "false", true),
        [".google.protobuf.StringValue"] = new("StringValue", "String", "string", "string", "string.Empty", false),
        [".google.protobuf.BytesValue"] = new("BytesValue", "Bytes", "ByteString", "ByteString", "ByteString.Empty", false),
    };

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
            (string? error, List<(string Name, string Content)> generatedFiles) = GenerateCode(filesToGenerate, protoFiles);

            byte[] responseBytes = EncodeResponse(error, generatedFiles);
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

    public static (string? Error, List<(string Name, string Content)> Files) GenerateCode(
        IReadOnlyList<string> filesToGenerate,
        IReadOnlyList<FileDescriptorProto> protoFiles)
    {
        Dictionary<string, TypeSymbol> symbolTable = BuildSymbolTable(protoFiles);
        var generatedFiles = new List<(string Name, string Content)>();
        var targetSet = new HashSet<string>(filesToGenerate, StringComparer.Ordinal);
        var validatedMessages = new HashSet<string>(StringComparer.Ordinal);

        foreach (FileDescriptorProto file in protoFiles)
        {
            if (!targetSet.Contains(file.Name))
            {
                continue;
            }

            string? validationError = ValidateFile(file, symbolTable, validatedMessages);
            if (validationError is not null)
            {
                return (validationError, new List<(string Name, string Content)>());
            }

            string fileContent = GenerateFile(file, symbolTable);
            string outputName = ComputeOutputFileName(file.Name);
            generatedFiles.Add((outputName, fileContent));
        }

        return (null, generatedFiles);
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
        public required bool IsProto2 { get; init; }
        public DescriptorProto? Message { get; init; }
        public EnumDescriptorProto? Enum { get; init; }
        public bool IsMapEntry => Message?.Options?.MapEntry == true;
    }

    private static bool IsProto2File(FileDescriptorProto file) =>
        !string.Equals(file.Syntax, "proto3", StringComparison.Ordinal);

    private static Dictionary<string, TypeSymbol> BuildSymbolTable(IReadOnlyList<FileDescriptorProto> files)
    {
        var table = new Dictionary<string, TypeSymbol>(StringComparer.Ordinal);
        foreach (FileDescriptorProto file in files)
        {
            bool isProto2 = IsProto2File(file);
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
                    IsProto2 = isProto2,
                    Enum = en,
                };
            }

            foreach (DescriptorProto msg in file.MessageType)
            {
                RegisterMessageSymbols(table, msg, protoPrefix, csPrefix, isNested: false, isProto2: isProto2);
            }
        }

        return table;
    }

    private static void RegisterMessageSymbols(
        Dictionary<string, TypeSymbol> table,
        DescriptorProto msg,
        string protoParent,
        string csParent,
        bool isNested,
        bool isProto2)
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
            IsProto2 = isProto2,
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
                IsProto2 = isProto2,
                Enum = en,
            };
        }

        foreach (DescriptorProto nested in msg.NestedType)
        {
            RegisterMessageSymbols(table, nested, protoName, csName + ".", isNested: true, isProto2: isProto2);
        }
    }

    private static string? ValidateFile(
        FileDescriptorProto file,
        Dictionary<string, TypeSymbol> symbols,
        HashSet<string> validatedMessages)
    {
        if (file.Extension.Count > 0)
        {
            return $"omnibuf: Protobuf extensions in {file.Name} are not supported";
        }

        string protoPrefix = string.IsNullOrEmpty(file.Package) ? string.Empty : "." + file.Package;
        foreach (DescriptorProto msg in file.MessageType)
        {
            string? err = ValidateMessage($"{protoPrefix}.{msg.Name}", msg, symbols, validatedMessages);
            if (err is not null)
            {
                return err;
            }
        }

        return null;
    }

    private static string? ValidateMessage(
        string protoFullName,
        DescriptorProto msg,
        Dictionary<string, TypeSymbol> symbols,
        HashSet<string> validatedMessages)
    {
        if (!validatedMessages.Add(protoFullName))
        {
            return null;
        }

        if (msg.Extension.Count > 0 || msg.ExtensionRange.Count > 0)
        {
            return $"omnibuf: Protobuf extensions on {protoFullName.TrimStart('.')} are not supported";
        }

        foreach (FieldDescriptorProto f in msg.Field)
        {
            if (f.Number <= 0 || f.Number > MaxFieldNumber)
            {
                return $"omnibuf: invalid field number {f.Number} on {protoFullName.TrimStart('.')}.{f.Name}";
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Group)
            {
                return $"omnibuf: Protobuf group field {protoFullName.TrimStart('.')}.{f.Name} is not supported";
            }

            if (f.HasDefaultValue && !IsZeroDefaultValue(f, symbols))
            {
                return $"omnibuf: custom non-zero default value '{f.DefaultValue}' on {protoFullName.TrimStart('.')}.{f.Name} is not supported";
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Message
                && !WrapperTypes.ContainsKey(f.TypeName)
                && symbols.TryGetValue(f.TypeName, out TypeSymbol? refSym)
                && refSym.Message is not null)
            {
                string? childErr = ValidateMessage(refSym.ProtoFullName, refSym.Message, symbols, validatedMessages);
                if (childErr is not null)
                {
                    return childErr;
                }
            }
        }

        foreach (DescriptorProto nested in msg.NestedType)
        {
            string? nestedErr = ValidateMessage($"{protoFullName}.{nested.Name}", nested, symbols, validatedMessages);
            if (nestedErr is not null)
            {
                return nestedErr;
            }
        }

        return null;
    }

    private static bool IsZeroDefaultValue(FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols)
    {
        string dv = f.DefaultValue;
        if (string.IsNullOrEmpty(dv))
        {
            return true;
        }

        switch (f.Type)
        {
            case FieldDescriptorProto.Types.Type.Bool:
                return dv == "false";

            case FieldDescriptorProto.Types.Type.String:
            case FieldDescriptorProto.Types.Type.Bytes:
                return dv.Length == 0;

            case FieldDescriptorProto.Types.Type.Enum:
                if (symbols.TryGetValue(f.TypeName, out TypeSymbol? enumSym) && enumSym.Enum is not null)
                {
                    EnumValueDescriptorProto? ev = enumSym.Enum.Value.FirstOrDefault(v => v.Name == dv);
                    return ev is not null && ev.Number == 0;
                }

                return false;

            case FieldDescriptorProto.Types.Type.Float:
            case FieldDescriptorProto.Types.Type.Double:
                return double.TryParse(dv, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    && d == 0d
                    && !double.IsNegative(d);

            case FieldDescriptorProto.Types.Type.Uint32:
            case FieldDescriptorProto.Types.Type.Uint64:
            case FieldDescriptorProto.Types.Type.Fixed32:
            case FieldDescriptorProto.Types.Type.Fixed64:
                return ulong.TryParse(dv, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong u) && u == 0UL;

            default:
                return long.TryParse(dv, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i) && i == 0L;
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
        if (prop == enclosingMsgName || ReservedMemberNames.Contains(prop))
        {
            prop += "_";
        }

        return prop;
    }

    private static bool IsRealOneof(FieldDescriptorProto f) => f.HasOneofIndex && !f.Proto3Optional;

    private static bool HasExplicitScalarPresence(FieldDescriptorProto f, bool isProto2) =>
        !IsRealOneof(f)
        && f.Label != FieldDescriptorProto.Types.Label.Repeated
        && f.Type != FieldDescriptorProto.Types.Type.Message
        && (f.Proto3Optional || isProto2);

    private static List<(int Index, string PascalName, List<FieldDescriptorProto> Fields)> GetRealOneofs(DescriptorProto msg)
    {
        var result = new List<(int Index, string PascalName, List<FieldDescriptorProto> Fields)>();
        for (int idx = 0; idx < msg.OneofDecl.Count; idx++)
        {
            List<FieldDescriptorProto> members = msg.Field
                .Where(f => f.HasOneofIndex && f.OneofIndex == idx && !f.Proto3Optional)
                .OrderBy(f => f.Number)
                .ToList();
            if (members.Count > 0)
            {
                result.Add((idx, ToPascalCase(msg.OneofDecl[idx].Name), members));
            }
        }

        return result;
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

        bool isProto2 = IsProto2File(file);
        string protoPrefix = string.IsNullOrEmpty(file.Package) ? string.Empty : "." + file.Package;
        foreach (DescriptorProto msg in file.MessageType)
        {
            if (msg.Options?.MapEntry == true)
            {
                continue;
            }

            EmitMessageCodecAndOpaque(sb, msg, $"{protoPrefix}.{msg.Name}", isProto2, symbols, string.Empty);
        }

        return sb.ToString();
    }

    private static void EmitMessageCodecAndOpaque(
        StringBuilder sb,
        DescriptorProto msg,
        string protoFullName,
        bool isProto2,
        Dictionary<string, TypeSymbol> symbols,
        string indent)
    {
        TypeSymbol msgSym = symbols[protoFullName];
        List<FieldDescriptorProto> orderedFields = msg.Field.OrderBy(f => f.Number).ToList();
        List<DescriptorProto> nestedMessages = msg.NestedType.Where(n => n.Options?.MapEntry != true).ToList();

        sb.AppendLine($"{indent}public sealed partial class {msg.Name}");
        sb.AppendLine($"{indent}{{");

        EmitSchemaMetaBuilders(sb, msg, protoFullName, isProto2, orderedFields, symbols, indent + "    ");
        EmitMarshalMethods(sb, msg, isProto2, orderedFields, symbols, indent + "    ");
        EmitUnmarshalMethods(sb, msgSym, msg, isProto2, orderedFields, symbols, indent + "    ");

        if (nestedMessages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{indent}    public static partial class Types");
            sb.AppendLine($"{indent}    {{");
            foreach (DescriptorProto nested in nestedMessages)
            {
                EmitMessageCodecAndOpaque(sb, nested, $"{protoFullName}.{nested.Name}", isProto2, symbols, indent + "        ");
            }

            sb.AppendLine($"{indent}    }}");
        }

        sb.AppendLine($"{indent}}}");
        sb.AppendLine();

        EmitOpaqueClass(sb, msgSym, msg, isProto2, orderedFields, symbols, indent);
    }

    private static void EmitSchemaMetaBuilders(
        StringBuilder sb,
        DescriptorProto msg,
        string protoFullName,
        bool isProto2,
        List<FieldDescriptorProto> fields,
        Dictionary<string, TypeSymbol> symbols,
        string indent)
    {
        sb.AppendLine($"{indent}public static SchemaMeta? BuildOmniBufSchemaMeta(int depth = 1, HashSet<string>? active = null) =>");
        sb.AppendLine($"{indent}    BuildOmniBufSchemaMeta(depth, active, out _);");
        sb.AppendLine();
        sb.AppendLine($"{indent}public static SchemaMeta? BuildOmniBufSchemaMeta(int depth, HashSet<string>? active, out bool cyclic)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    cyclic = false;");
        sb.AppendLine($"{indent}    if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        return null;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    active ??= new HashSet<string>(StringComparer.Ordinal);");
        sb.AppendLine($"{indent}    if (!active.Add(\"{protoFullName}\"))");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        cyclic = true;");
        sb.AppendLine($"{indent}        return null;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    try");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        var meta = new SchemaMeta");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            TypeName = \"{msg.Name}\",");
        sb.AppendLine($"{indent}            Fields = new Dictionary<uint, string>({fields.Count}),");
        sb.AppendLine($"{indent}        }};");

        foreach (FieldDescriptorProto f in fields)
        {
            uint fnum = (uint)f.Number;
            sb.AppendLine($"{indent}        meta.Fields[{fnum}u] = \"{f.Name}\";");

            if (TryResolveChildSchemaTarget(f, symbols, out TypeSymbol? childMsgSym, out WrapperInfo? childWrapper))
            {
                if (childWrapper is not null)
                {
                    sb.AppendLine($"{indent}        if (OmniBufWrappers.BuildSchemaMeta(\"{childWrapper.ShortName}\", depth + 2) is {{ }} wrap_{fnum})");
                    sb.AppendLine($"{indent}        {{");
                    sb.AppendLine($"{indent}            meta.Children[{fnum}u] = wrap_{fnum};");
                    sb.AppendLine($"{indent}        }}");
                }
                else if (childMsgSym is not null)
                {
                    sb.AppendLine($"{indent}        SchemaMeta? child_{fnum} = {childMsgSym.CSharpFullName}.BuildOmniBufSchemaMeta(depth + 2, active, out bool cyc_{fnum});");
                    sb.AppendLine($"{indent}        cyclic |= cyc_{fnum};");
                    sb.AppendLine($"{indent}        if (child_{fnum} is not null)");
                    sb.AppendLine($"{indent}        {{");
                    sb.AppendLine($"{indent}            meta.Children[{fnum}u] = child_{fnum};");
                    sb.AppendLine($"{indent}        }}");
                }
            }
        }

        sb.AppendLine($"{indent}        return meta;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    finally");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        active.Remove(\"{protoFullName}\");");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();

        // Instance-driven SchemaMeta builder used when static schema has recursive message cycles (for example, TreeNode).
        sb.AppendLine($"{indent}internal SchemaMeta? BuildOmniBufValueSchemaMeta(int depth = 1, HashSet<object>? active = null)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        return null;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    active ??= new HashSet<object>(ReferenceEqualityComparer.Instance);");
        sb.AppendLine($"{indent}    if (!active.Add(this))");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        return null;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    try");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        var meta = new SchemaMeta");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            TypeName = \"{msg.Name}\",");
        sb.AppendLine($"{indent}            Fields = new Dictionary<uint, string>({fields.Count}),");
        sb.AppendLine($"{indent}        }};");

        bool hasChildMetaFields = fields.Any(f => TryResolveChildSchemaTarget(f, symbols, out _, out _));
        if (hasChildMetaFields)
        {
            foreach (FieldDescriptorProto f in fields)
            {
                uint fnum = (uint)f.Number;
                sb.AppendLine($"{indent}        meta.Fields[{fnum}u] = \"{f.Name}\";");
            }

            sb.AppendLine($"{indent}        if (depth + 3 <= OmniBufConstants.MaxNestingDepth)");
            sb.AppendLine($"{indent}        {{");
            foreach (FieldDescriptorProto f in fields)
            {
                uint fnum = (uint)f.Number;
                string prop = GetPropertyName(f, msg.Name);
                if (!TryResolveChildSchemaTarget(f, symbols, out TypeSymbol? childMsgSym, out WrapperInfo? childWrapper))
                {
                    continue;
                }

                if (IsMapField(f, symbols, out _, out _))
                {
                    if (childWrapper is not null)
                    {
                        sb.AppendLine($"{indent}            if (this.{prop}.Count > 0 && OmniBufWrappers.BuildSchemaMeta(\"{childWrapper.ShortName}\", depth + 2) is {{ }} mw_{fnum})");
                        sb.AppendLine($"{indent}            {{");
                        sb.AppendLine($"{indent}                meta.Children[{fnum}u] = mw_{fnum};");
                        sb.AppendLine($"{indent}            }}");
                    }
                    else if (childMsgSym is not null)
                    {
                        sb.AppendLine($"{indent}            if (this.{prop}.Count > 0)");
                        sb.AppendLine($"{indent}            {{");
                        sb.AppendLine($"{indent}                SchemaMeta? mm_{fnum} = null;");
                        sb.AppendLine($"{indent}                foreach (var kv in this.{prop})");
                        sb.AppendLine($"{indent}                {{");
                        sb.AppendLine($"{indent}                    if (kv.Value is not null)");
                        sb.AppendLine($"{indent}                    {{");
                        sb.AppendLine($"{indent}                        mm_{fnum} = SchemaMeta.Merge(mm_{fnum}, kv.Value.BuildOmniBufValueSchemaMeta(depth + 2, active));");
                        sb.AppendLine($"{indent}                    }}");
                        sb.AppendLine($"{indent}                }}");
                        sb.AppendLine($"{indent}                if (mm_{fnum} is not null)");
                        sb.AppendLine($"{indent}                {{");
                        sb.AppendLine($"{indent}                    meta.Children[{fnum}u] = mm_{fnum};");
                        sb.AppendLine($"{indent}                }}");
                        sb.AppendLine($"{indent}            }}");
                    }
                }
                else if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
                {
                    if (childWrapper is not null)
                    {
                        sb.AppendLine($"{indent}            if (this.{prop}.Count > 0 && OmniBufWrappers.BuildSchemaMeta(\"{childWrapper.ShortName}\", depth + 2) is {{ }} rw_{fnum})");
                        sb.AppendLine($"{indent}            {{");
                        sb.AppendLine($"{indent}                meta.Children[{fnum}u] = rw_{fnum};");
                        sb.AppendLine($"{indent}            }}");
                    }
                    else if (childMsgSym is not null)
                    {
                        sb.AppendLine($"{indent}            if (this.{prop}.Count > 0)");
                        sb.AppendLine($"{indent}            {{");
                        sb.AppendLine($"{indent}                SchemaMeta? rm_{fnum} = null;");
                        sb.AppendLine($"{indent}                for (int i = 0; i < this.{prop}.Count; i++)");
                        sb.AppendLine($"{indent}                {{");
                        sb.AppendLine($"{indent}                    if (this.{prop}[i] is not null)");
                        sb.AppendLine($"{indent}                    {{");
                        sb.AppendLine($"{indent}                        rm_{fnum} = SchemaMeta.Merge(rm_{fnum}, this.{prop}[i].BuildOmniBufValueSchemaMeta(depth + 2, active));");
                        sb.AppendLine($"{indent}                    }}");
                        sb.AppendLine($"{indent}                }}");
                        sb.AppendLine($"{indent}                if (rm_{fnum} is not null)");
                        sb.AppendLine($"{indent}                {{");
                        sb.AppendLine($"{indent}                    meta.Children[{fnum}u] = rm_{fnum};");
                        sb.AppendLine($"{indent}                }}");
                        sb.AppendLine($"{indent}            }}");
                    }
                }
                else if (childWrapper is not null)
                {
                    string cond = IsRealOneof(f)
                        ? $"this.{ToPascalCase(msg.OneofDecl[f.OneofIndex].Name)}Case == {ToPascalCase(msg.OneofDecl[f.OneofIndex].Name)}OneofCase.{prop}"
                        : (childWrapper.IsValueType ? $"this.{prop}.HasValue" : $"this.{prop} is not null");
                    sb.AppendLine($"{indent}            if ({cond} && OmniBufWrappers.BuildSchemaMeta(\"{childWrapper.ShortName}\", depth + 2) is {{ }} sw_{fnum})");
                    sb.AppendLine($"{indent}            {{");
                    sb.AppendLine($"{indent}                meta.Children[{fnum}u] = sw_{fnum};");
                    sb.AppendLine($"{indent}            }}");
                }
                else if (childMsgSym is not null)
                {
                    sb.AppendLine($"{indent}            if (this.{prop} is not null && this.{prop}.BuildOmniBufValueSchemaMeta(depth + 2, active) is {{ }} sm_{fnum})");
                    sb.AppendLine($"{indent}            {{");
                    sb.AppendLine($"{indent}                meta.Children[{fnum}u] = sm_{fnum};");
                    sb.AppendLine($"{indent}            }}");
                }
            }

            sb.AppendLine($"{indent}        }}");
        }
        else
        {
            foreach (FieldDescriptorProto f in fields)
            {
                uint fnum = (uint)f.Number;
                sb.AppendLine($"{indent}        meta.Fields[{fnum}u] = \"{f.Name}\";");
            }
        }

        _ = isProto2;
        sb.AppendLine($"{indent}        return meta;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    finally");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        active.Remove(this);");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
    }

    private static bool TryResolveChildSchemaTarget(
        FieldDescriptorProto f,
        Dictionary<string, TypeSymbol> symbols,
        out TypeSymbol? childMsgSym,
        out WrapperInfo? wrapper)
    {
        childMsgSym = null;
        wrapper = null;
        if (f.Type != FieldDescriptorProto.Types.Type.Message)
        {
            return false;
        }

        if (WrapperTypes.TryGetValue(f.TypeName, out wrapper))
        {
            return true;
        }

        if (!symbols.TryGetValue(f.TypeName, out TypeSymbol? sym))
        {
            return false;
        }

        if (sym.IsMapEntry)
        {
            FieldDescriptorProto valField = sym.Message!.Field.First(x => x.Number == 2);
            if (valField.Type != FieldDescriptorProto.Types.Type.Message)
            {
                return false;
            }

            if (WrapperTypes.TryGetValue(valField.TypeName, out wrapper))
            {
                return true;
            }

            if (symbols.TryGetValue(valField.TypeName, out childMsgSym))
            {
                return true;
            }

            return false;
        }

        childMsgSym = sym;
        return true;
    }

    private static void EmitMarshalMethods(
        StringBuilder sb,
        DescriptorProto msg,
        bool isProto2,
        List<FieldDescriptorProto> fields,
        Dictionary<string, TypeSymbol> symbols,
        string indent)
    {
        sb.AppendLine($"{indent}public byte[] MarshalOmniBuf(MarshalOptions options = default)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    OmniBufEncoder encoder = OmniBufEncoder.RentThreadLocal(out bool rented);");
        sb.AppendLine($"{indent}    try");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        MarshalOmniBuf(encoder, options);");
        sb.AppendLine($"{indent}        return encoder.ToArray();");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    finally");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        OmniBufEncoder.ReturnThreadLocal(encoder, rented);");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
        sb.AppendLine($"{indent}public void MarshalOmniBuf(OmniBufEncoder encoder, MarshalOptions options = default)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    if (options.SelfDescribing)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        SchemaMeta? meta = BuildOmniBufSchemaMeta(1, null, out bool cyclic);");
        sb.AppendLine($"{indent}        if (cyclic)");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            meta = BuildOmniBufValueSchemaMeta(1, null);");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}        if (meta is not null)");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            SchemaMeta.WriteField0(encoder, meta, options.Deterministic);");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    EncodeOmniBufBody(encoder, options.Deterministic, 0);");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
        sb.AppendLine($"{indent}internal void EncodeOmniBufBody(OmniBufEncoder encoder, bool deterministic, int depth = 0)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    if (depth > OmniBufConstants.MaxNestingDepth)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        throw new OmniBufException($\"omnibuf: encode nesting depth {{depth}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
        sb.AppendLine($"{indent}    }}");

        foreach (FieldDescriptorProto f in fields)
        {
            uint fnum = (uint)f.Number;
            string prop = GetPropertyName(f, msg.Name);

            if (IsMapField(f, symbols, out FieldDescriptorProto? keyField, out FieldDescriptorProto? valField))
            {
                string keyWt = WireTypeEnumName(keyField!.Type);
                string valWt = WireTypeEnumName(valField!.Type);
                string keyCs = CSharpScalarType(keyField, symbols);
                sb.AppendLine($"{indent}    if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        throw new OmniBufException($\"omnibuf: WireMap encode depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}    encoder.BeginMapField({fnum}u, OmniBufWireType.{keyWt}, OmniBufWireType.{valWt}, this.{prop}.Count);");
                sb.AppendLine($"{indent}    if (this.{prop}.Count > 0)");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        if (deterministic && this.{prop}.Count > 1)");
                sb.AppendLine($"{indent}        {{");
                sb.AppendLine($"{indent}            var keys_{fnum} = new List<{keyCs}>(this.{prop}.Keys);");
                if (keyField.Type == FieldDescriptorProto.Types.Type.String)
                {
                    sb.AppendLine($"{indent}            keys_{fnum}.Sort(StringComparer.Ordinal);");
                }
                else
                {
                    sb.AppendLine($"{indent}            keys_{fnum}.Sort();");
                }

                sb.AppendLine($"{indent}            for (int i = 0; i < keys_{fnum}.Count; i++)");
                sb.AppendLine($"{indent}            {{");
                sb.AppendLine($"{indent}                var k = keys_{fnum}[i];");
                sb.AppendLine($"{indent}                var v = this.{prop}[k];");
                sb.AppendLine($"{indent}                {EmitWriteRawValue("encoder", keyField, "k")};");
                EmitMapValEncode(sb, indent + "                ", valField, "v", "depth + 2");
                sb.AppendLine($"{indent}            }}");
                sb.AppendLine($"{indent}        }}");
                sb.AppendLine($"{indent}        else");
                sb.AppendLine($"{indent}        {{");
                sb.AppendLine($"{indent}            foreach (var kv in this.{prop})");
                sb.AppendLine($"{indent}            {{");
                sb.AppendLine($"{indent}                {EmitWriteRawValue("encoder", keyField, "kv.Key")};");
                EmitMapValEncode(sb, indent + "                ", valField, "kv.Value", "depth + 2");
                sb.AppendLine($"{indent}            }}");
                sb.AppendLine($"{indent}        }}");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}    encoder.EndMap();");
                continue;
            }

            if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
            {
                string elemWt = WireTypeEnumName(f.Type);
                sb.AppendLine($"{indent}    if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        throw new OmniBufException($\"omnibuf: WireArray encode depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}    encoder.BeginArrayField({fnum}u, OmniBufWireType.{elemWt}, this.{prop}.Count);");
                sb.AppendLine($"{indent}    for (int i = 0; i < this.{prop}.Count; i++)");
                sb.AppendLine($"{indent}    {{");
                if (f.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    sb.AppendLine($"{indent}        if (depth + 2 > OmniBufConstants.MaxNestingDepth)");
                    sb.AppendLine($"{indent}        {{");
                    sb.AppendLine($"{indent}            throw new OmniBufException($\"omnibuf: encode nesting depth {{depth + 2}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                    sb.AppendLine($"{indent}        }}");
                    sb.AppendLine($"{indent}        encoder.BeginContainer();");
                    if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? rWrap))
                    {
                        if (rWrap.IsValueType)
                        {
                            sb.AppendLine($"{indent}        if (this.{prop}[i].HasValue)");
                            sb.AppendLine($"{indent}        {{");
                            sb.AppendLine($"{indent}            OmniBufWrappers.Write{rWrap.MethodSuffix}Body(encoder, this.{prop}[i]!.Value);");
                            sb.AppendLine($"{indent}        }}");
                        }
                        else
                        {
                            sb.AppendLine($"{indent}        if (this.{prop}[i] is not null)");
                            sb.AppendLine($"{indent}        {{");
                            sb.AppendLine($"{indent}            OmniBufWrappers.Write{rWrap.MethodSuffix}Body(encoder, this.{prop}[i]);");
                            sb.AppendLine($"{indent}        }}");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"{indent}        this.{prop}[i]?.EncodeOmniBufBody(encoder, deterministic, depth + 2);");
                    }

                    sb.AppendLine($"{indent}        encoder.EndContainer();");
                }
                else
                {
                    sb.AppendLine($"{indent}        {EmitWriteRawValue("encoder", f, $"this.{prop}[i]")};");
                }

                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}    encoder.EndArray();");
                continue;
            }

            if (IsRealOneof(f))
            {
                string oneofPascal = ToPascalCase(msg.OneofDecl[f.OneofIndex].Name);
                sb.AppendLine($"{indent}    if (this.{oneofPascal}Case == {oneofPascal}OneofCase.{prop})");
                sb.AppendLine($"{indent}    {{");
                if (f.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    sb.AppendLine($"{indent}        if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
                    sb.AppendLine($"{indent}        {{");
                    sb.AppendLine($"{indent}            throw new OmniBufException($\"omnibuf: encode nesting depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                    sb.AppendLine($"{indent}        }}");
                    sb.AppendLine($"{indent}        encoder.BeginSubMsgField({fnum}u);");
                    if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? oWrap))
                    {
                        string wVal = oWrap.IsValueType ? $"this.{prop}!.Value" : $"this.{prop}";
                        sb.AppendLine($"{indent}        OmniBufWrappers.Write{oWrap.MethodSuffix}Body(encoder, {wVal});");
                    }
                    else
                    {
                        sb.AppendLine($"{indent}        this.{prop}?.EncodeOmniBufBody(encoder, deterministic, depth + 1);");
                    }

                    sb.AppendLine($"{indent}        encoder.EndSubMsg();");
                }
                else
                {
                    sb.AppendLine($"{indent}        {EmitWriteField("encoder", fnum, f, $"this.{prop}")};");
                }

                sb.AppendLine($"{indent}    }}");
                continue;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? sWrap))
                {
                    string checkNull = sWrap.IsValueType ? $"this.{prop}.HasValue" : $"this.{prop} is not null";
                    string wVal = sWrap.IsValueType ? $"this.{prop}.Value" : $"this.{prop}";
                    sb.AppendLine($"{indent}    if ({checkNull})");
                    sb.AppendLine($"{indent}    {{");
                    sb.AppendLine($"{indent}        if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
                    sb.AppendLine($"{indent}        {{");
                    sb.AppendLine($"{indent}            throw new OmniBufException($\"omnibuf: encode nesting depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                    sb.AppendLine($"{indent}        }}");
                    sb.AppendLine($"{indent}        encoder.BeginSubMsgField({fnum}u);");
                    sb.AppendLine($"{indent}        OmniBufWrappers.Write{sWrap.MethodSuffix}Body(encoder, {wVal});");
                    sb.AppendLine($"{indent}        encoder.EndSubMsg();");
                    sb.AppendLine($"{indent}    }}");
                }
                else
                {
                    sb.AppendLine($"{indent}    if (this.{prop} is not null)");
                    sb.AppendLine($"{indent}    {{");
                    sb.AppendLine($"{indent}        if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
                    sb.AppendLine($"{indent}        {{");
                    sb.AppendLine($"{indent}            throw new OmniBufException($\"omnibuf: encode nesting depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                    sb.AppendLine($"{indent}        }}");
                    sb.AppendLine($"{indent}        encoder.BeginSubMsgField({fnum}u);");
                    sb.AppendLine($"{indent}        this.{prop}.EncodeOmniBufBody(encoder, deterministic, depth + 1);");
                    sb.AppendLine($"{indent}        encoder.EndSubMsg();");
                    sb.AppendLine($"{indent}    }}");
                }

                continue;
            }

            if (HasExplicitScalarPresence(f, isProto2))
            {
                sb.AppendLine($"{indent}    if (this.Has{prop})");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        {EmitWriteField("encoder", fnum, f, $"this.{prop}")};");
                sb.AppendLine($"{indent}    }}");
                continue;
            }

            sb.AppendLine($"{indent}    {EmitWriteField("encoder", fnum, f, $"this.{prop}")};");
        }

        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
    }

    private static void EmitMapValEncode(StringBuilder sb, string indent, FieldDescriptorProto valField, string expr, string childDepthExpr)
    {
        if (valField.Type == FieldDescriptorProto.Types.Type.Message)
        {
            sb.AppendLine($"{indent}if ({childDepthExpr} > OmniBufConstants.MaxNestingDepth)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    throw new OmniBufException($\"omnibuf: encode nesting depth {{{childDepthExpr}}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}encoder.BeginContainer();");
            if (WrapperTypes.TryGetValue(valField.TypeName, out WrapperInfo? mWrap))
            {
                if (mWrap.IsValueType)
                {
                    sb.AppendLine($"{indent}if ({expr}.HasValue)");
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    OmniBufWrappers.Write{mWrap.MethodSuffix}Body(encoder, {expr}.Value);");
                    sb.AppendLine($"{indent}}}");
                }
                else
                {
                    sb.AppendLine($"{indent}if ({expr} is not null)");
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    OmniBufWrappers.Write{mWrap.MethodSuffix}Body(encoder, {expr});");
                    sb.AppendLine($"{indent}}}");
                }
            }
            else
            {
                sb.AppendLine($"{indent}{expr}?.EncodeOmniBufBody(encoder, deterministic, {childDepthExpr});");
            }

            sb.AppendLine($"{indent}encoder.EndContainer();");
        }
        else
        {
            sb.AppendLine($"{indent}{EmitWriteRawValue("encoder", valField, expr)};");
        }
    }

    private static void EmitUnmarshalMethods(
        StringBuilder sb,
        TypeSymbol msgSym,
        DescriptorProto msg,
        bool isProto2,
        List<FieldDescriptorProto> fields,
        Dictionary<string, TypeSymbol> symbols,
        string indent)
    {
        List<(int Index, string PascalName, List<FieldDescriptorProto> Fields)> realOneofs = GetRealOneofs(msg);
        bool hasLowFields = fields.Any(f => f.Number < 64);
        bool hasHighFields = fields.Any(f => f.Number >= 64);

        sb.AppendLine($"{indent}public static {msgSym.CSharpFullName} ParseOmniBuf(ReadOnlySpan<byte> data)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    var msg = new {msgSym.CSharpFullName}();");
        sb.AppendLine($"{indent}    msg.UnmarshalOmniBuf(data);");
        sb.AppendLine($"{indent}    return msg;");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
        sb.AppendLine($"{indent}public void UnmarshalOmniBuf(ReadOnlySpan<byte> data)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    DecodeOmniBuf(data, 0, OmniBufConstants.MaxNestingDepth, merge: false);");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
        sb.AppendLine($"{indent}internal void DecodeOmniBuf(ReadOnlySpan<byte> data, int depth, int maxDepth, bool merge)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    if (depth > maxDepth)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        throw new OmniBufException($\"omnibuf: nesting depth {{depth}} exceeds maximum {{maxDepth}}\");");
        sb.AppendLine($"{indent}    }}");

        if (realOneofs.Count > 0)
        {
            sb.AppendLine($"{indent}    if (!merge)");
            sb.AppendLine($"{indent}    {{");
            foreach ((_, string oneofPascal, _) in realOneofs)
            {
                sb.AppendLine($"{indent}        this.Clear{oneofPascal}();");
            }

            sb.AppendLine($"{indent}    }}");
        }

        if (hasLowFields)
        {
            sb.AppendLine($"{indent}    ulong seenLow = 0;");
        }

        if (hasHighFields)
        {
            sb.AppendLine($"{indent}    HashSet<uint>? seenHigh = null;");
        }

        sb.AppendLine($"{indent}    int pos = 0;");
        sb.AppendLine($"{indent}    while (pos < data.Length)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);");
        sb.AppendLine($"{indent}        switch (fnum)");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            case 0:");
        sb.AppendLine($"{indent}                SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);");
        sb.AppendLine($"{indent}                break;");

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

            sb.AppendLine($"{indent}            case {fnum}u:");
            sb.AppendLine($"{indent}            {{");

            if (IsMapField(f, symbols, out FieldDescriptorProto? keyField, out FieldDescriptorProto? valField))
            {
                string keyWt = WireTypeEnumName(keyField!.Type);
                string valWt = WireTypeEnumName(valField!.Type);
                sb.AppendLine($"{indent}                if (wt != OmniBufWireType.Map)");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);");
                sb.AppendLine($"{indent}                    break;");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}                if (depth + 1 > maxDepth)");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    throw new OmniBufException($\"omnibuf: WireMap depth {{depth + 1}} exceeds maximum {{maxDepth}}\");");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}                bool alreadySeen = {seenCheckExpr} || merge;");
                sb.AppendLine($"{indent}                {markSeenStmt}");
                sb.AppendLine($"{indent}                ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 3);");
                sb.AppendLine($"{indent}                if (!OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType kWt, out OmniBufWireType vWt, out int count, out int p) || kWt != OmniBufWireType.{keyWt} || vWt != OmniBufWireType.{valWt})");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    throw new OmniBufException(\"omnibuf: invalid WireMap header on {msg.Name}.{f.Name}\");");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}                if (!alreadySeen)");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    this.{prop}.Clear();");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}                for (int i = 0; i < count; i++)");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    var k = {EmitReadRawValue("body", "p", keyField, symbols)};");
                if (valField.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    sb.AppendLine($"{indent}                    ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);");
                    if (WrapperTypes.TryGetValue(valField.TypeName, out WrapperInfo? mWrap))
                    {
                        sb.AppendLine($"{indent}                    var v = OmniBufWrappers.Decode{mWrap.MethodSuffix}(sub, {mWrap.DefaultExpr}, depth + 2, maxDepth);");
                    }
                    else
                    {
                        string valMsgCs = symbols[valField.TypeName].CSharpFullName;
                        sb.AppendLine($"{indent}                    var v = new {valMsgCs}();");
                        sb.AppendLine($"{indent}                    v.DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                    }
                }
                else
                {
                    sb.AppendLine($"{indent}                    var v = {EmitReadRawValue("body", "p", valField, symbols)};");
                }

                sb.AppendLine($"{indent}                    this.{prop}[k] = v;");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}                if (p != body.Length)");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    throw new OmniBufException(\"omnibuf: trailing bytes in WireMap\");");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}                break;");
                sb.AppendLine($"{indent}            }}");
                continue;
            }

            if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
            {
                string elemWt = WireTypeEnumName(f.Type);
                sb.AppendLine($"{indent}                if (wt != OmniBufWireType.Array)");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);");
                sb.AppendLine($"{indent}                    break;");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}                if (depth + 1 > maxDepth)");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    throw new OmniBufException($\"omnibuf: WireArray depth {{depth + 1}} exceeds maximum {{maxDepth}}\");");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}                bool alreadySeen = {seenCheckExpr} || merge;");
                sb.AppendLine($"{indent}                {markSeenStmt}");
                sb.AppendLine($"{indent}                ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 2);");
                sb.AppendLine($"{indent}                if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p) || elemWt != OmniBufWireType.{elemWt})");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    throw new OmniBufException(\"omnibuf: invalid WireArray header on {msg.Name}.{f.Name}\");");
                sb.AppendLine($"{indent}                }}");
                if (f.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? rWrap))
                    {
                        sb.AppendLine($"{indent}                if (!alreadySeen)");
                        sb.AppendLine($"{indent}                {{");
                        sb.AppendLine($"{indent}                    this.{prop}.Clear();");
                        sb.AppendLine($"{indent}                }}");
                        sb.AppendLine($"{indent}                for (int i = 0; i < count; i++)");
                        sb.AppendLine($"{indent}                {{");
                        sb.AppendLine($"{indent}                    ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);");
                        sb.AppendLine($"{indent}                    this.{prop}.Add(OmniBufWrappers.Decode{rWrap.MethodSuffix}(sub, {rWrap.DefaultExpr}, depth + 2, maxDepth));");
                        sb.AppendLine($"{indent}                }}");
                    }
                    else
                    {
                        string elemMsgCs = symbols[f.TypeName].CSharpFullName;
                        sb.AppendLine($"{indent}                if (!alreadySeen)");
                        sb.AppendLine($"{indent}                {{");
                        sb.AppendLine($"{indent}                    int prevCount = this.{prop}.Count;");
                        sb.AppendLine($"{indent}                    for (int i = 0; i < count; i++)");
                        sb.AppendLine($"{indent}                    {{");
                        sb.AppendLine($"{indent}                        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);");
                        sb.AppendLine($"{indent}                        if (i < prevCount)");
                        sb.AppendLine($"{indent}                        {{");
                        sb.AppendLine($"{indent}                            this.{prop}[i].DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                        sb.AppendLine($"{indent}                        }}");
                        sb.AppendLine($"{indent}                        else");
                        sb.AppendLine($"{indent}                        {{");
                        sb.AppendLine($"{indent}                            var elem = new {elemMsgCs}();");
                        sb.AppendLine($"{indent}                            elem.DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                        sb.AppendLine($"{indent}                            this.{prop}.Add(elem);");
                        sb.AppendLine($"{indent}                        }}");
                        sb.AppendLine($"{indent}                    }}");
                        sb.AppendLine($"{indent}                    for (int r = prevCount - 1; r >= count; r--)");
                        sb.AppendLine($"{indent}                    {{");
                        sb.AppendLine($"{indent}                        this.{prop}.RemoveAt(r);");
                        sb.AppendLine($"{indent}                    }}");
                        sb.AppendLine($"{indent}                }}");
                        sb.AppendLine($"{indent}                else");
                        sb.AppendLine($"{indent}                {{");
                        sb.AppendLine($"{indent}                    for (int i = 0; i < count; i++)");
                        sb.AppendLine($"{indent}                    {{");
                        sb.AppendLine($"{indent}                        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);");
                        sb.AppendLine($"{indent}                        var elem = new {elemMsgCs}();");
                        sb.AppendLine($"{indent}                        elem.DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                        sb.AppendLine($"{indent}                        this.{prop}.Add(elem);");
                        sb.AppendLine($"{indent}                    }}");
                        sb.AppendLine($"{indent}                }}");
                    }
                }
                else
                {
                    sb.AppendLine($"{indent}                if (!alreadySeen)");
                    sb.AppendLine($"{indent}                {{");
                    sb.AppendLine($"{indent}                    this.{prop}.Clear();");
                    sb.AppendLine($"{indent}                }}");
                    sb.AppendLine($"{indent}                for (int i = 0; i < count; i++)");
                    sb.AppendLine($"{indent}                {{");
                    sb.AppendLine($"{indent}                    this.{prop}.Add({EmitReadRawValue("body", "p", f, symbols)});");
                    sb.AppendLine($"{indent}                }}");
                }

                sb.AppendLine($"{indent}                if (p != body.Length)");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    throw new OmniBufException(\"omnibuf: trailing bytes in WireArray\");");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}                break;");
                sb.AppendLine($"{indent}            }}");
                continue;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Bool)
            {
                sb.AppendLine($"{indent}                if (wt != OmniBufWireType.BoolFalse && wt != OmniBufWireType.BoolTrue)");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);");
                sb.AppendLine($"{indent}                    break;");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}                {markSeenStmt}");
                sb.AppendLine($"{indent}                this.{prop} = wt == OmniBufWireType.BoolTrue;");
                sb.AppendLine($"{indent}                break;");
                sb.AppendLine($"{indent}            }}");
                continue;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                sb.AppendLine($"{indent}                if (wt != OmniBufWireType.SubMsg)");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);");
                sb.AppendLine($"{indent}                    break;");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}                bool alreadySeen = {seenCheckExpr} || merge;");
                sb.AppendLine($"{indent}                {markSeenStmt}");
                sb.AppendLine($"{indent}                ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);");
                if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? sWrap))
                {
                    if (IsRealOneof(f))
                    {
                        string oneofPascal = ToPascalCase(msg.OneofDecl[f.OneofIndex].Name);
                        string activeCheck = $"alreadySeen && this.{oneofPascal}Case == {oneofPascal}OneofCase.{prop}";
                        string prevExpr = sWrap.IsValueType
                            ? $"{activeCheck} && this.{prop}.HasValue ? this.{prop}.Value : {sWrap.DefaultExpr}"
                            : $"{activeCheck} && this.{prop} is not null ? this.{prop} : {sWrap.DefaultExpr}";
                        sb.AppendLine($"{indent}                {sWrap.ScalarCsType} prev = {prevExpr};");
                    }
                    else
                    {
                        string prevExpr = sWrap.IsValueType
                            ? $"alreadySeen && this.{prop}.HasValue ? this.{prop}.Value : {sWrap.DefaultExpr}"
                            : $"alreadySeen && this.{prop} is not null ? this.{prop} : {sWrap.DefaultExpr}";
                        sb.AppendLine($"{indent}                {sWrap.ScalarCsType} prev = {prevExpr};");
                    }

                    sb.AppendLine($"{indent}                this.{prop} = OmniBufWrappers.Decode{sWrap.MethodSuffix}(sub, prev, depth + 1, maxDepth);");
                }
                else
                {
                    string subCs = symbols[f.TypeName].CSharpFullName;
                    if (IsRealOneof(f))
                    {
                        string oneofPascal = ToPascalCase(msg.OneofDecl[f.OneofIndex].Name);
                        sb.AppendLine($"{indent}                if (this.{oneofPascal}Case != {oneofPascal}OneofCase.{prop} || this.{prop} is null)");
                        sb.AppendLine($"{indent}                {{");
                        sb.AppendLine($"{indent}                    this.{prop} = new {subCs}();");
                        sb.AppendLine($"{indent}                    this.{prop}.DecodeOmniBuf(sub, depth + 1, maxDepth, merge: false);");
                        sb.AppendLine($"{indent}                }}");
                        sb.AppendLine($"{indent}                else");
                        sb.AppendLine($"{indent}                {{");
                        sb.AppendLine($"{indent}                    this.{prop}.DecodeOmniBuf(sub, depth + 1, maxDepth, merge: alreadySeen);");
                        sb.AppendLine($"{indent}                }}");
                    }
                    else
                    {
                        sb.AppendLine($"{indent}                if (this.{prop} is null)");
                        sb.AppendLine($"{indent}                {{");
                        sb.AppendLine($"{indent}                    this.{prop} = new {subCs}();");
                        sb.AppendLine($"{indent}                }}");
                        sb.AppendLine($"{indent}                this.{prop}.DecodeOmniBuf(sub, depth + 1, maxDepth, merge: alreadySeen);");
                    }
                }

                sb.AppendLine($"{indent}                break;");
                sb.AppendLine($"{indent}            }}");
                continue;
            }

            string expWt = WireTypeEnumName(f.Type);
            sb.AppendLine($"{indent}                if (wt != OmniBufWireType.{expWt})");
            sb.AppendLine($"{indent}                {{");
            sb.AppendLine($"{indent}                    OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);");
            sb.AppendLine($"{indent}                    break;");
            sb.AppendLine($"{indent}                }}");
            sb.AppendLine($"{indent}                {markSeenStmt}");
            sb.AppendLine($"{indent}                this.{prop} = {EmitReadRawValue("data", "pos", f, symbols)};");
            sb.AppendLine($"{indent}                break;");
            sb.AppendLine($"{indent}            }}");
        }

        sb.AppendLine($"{indent}            default:");
        sb.AppendLine($"{indent}                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);");
        sb.AppendLine($"{indent}                break;");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}    }}");

        List<FieldDescriptorProto> nonOneofFields = fields.Where(f => !IsRealOneof(f)).ToList();
        if (nonOneofFields.Count > 0)
        {
            sb.AppendLine($"{indent}    if (!merge)");
            sb.AppendLine($"{indent}    {{");
            foreach (FieldDescriptorProto f in nonOneofFields)
            {
                uint fnum = (uint)f.Number;
                string prop = GetPropertyName(f, msg.Name);
                string notSeen = fnum < 64
                    ? $"((seenLow & (1UL << {(int)fnum})) == 0)"
                    : $"(seenHigh is null || !seenHigh.Contains({fnum}u))";
                sb.AppendLine($"{indent}        if ({notSeen})");
                sb.AppendLine($"{indent}        {{");
                if (IsMapField(f, symbols, out _, out _) || f.Label == FieldDescriptorProto.Types.Label.Repeated)
                {
                    sb.AppendLine($"{indent}            this.{prop}.Clear();");
                }
                else if (f.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    sb.AppendLine($"{indent}            this.{prop} = null;");
                }
                else if (HasExplicitScalarPresence(f, isProto2))
                {
                    sb.AppendLine($"{indent}            this.Clear{prop}();");
                }
                else
                {
                    sb.AppendLine($"{indent}            this.{prop} = {DefaultValueLiteral(f, symbols)};");
                }

                sb.AppendLine($"{indent}        }}");
            }

            sb.AppendLine($"{indent}    }}");
        }

        sb.AppendLine($"{indent}}}");
    }

    private static bool IsOpaqueSubMsgField(FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols) =>
        f.Type == FieldDescriptorProto.Types.Type.Message
        && !WrapperTypes.ContainsKey(f.TypeName)
        && !IsMapField(f, symbols, out _, out _)
        && f.Label != FieldDescriptorProto.Types.Label.Repeated;

    private static bool IsOpaqueSubMsgListField(FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols) =>
        f.Label == FieldDescriptorProto.Types.Label.Repeated
        && f.Type == FieldDescriptorProto.Types.Type.Message
        && !WrapperTypes.ContainsKey(f.TypeName)
        && !IsMapField(f, symbols, out _, out _);

    private static bool IsOpaqueSubMsgMapField(FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols) =>
        IsMapField(f, symbols, out _, out FieldDescriptorProto? vf)
        && vf!.Type == FieldDescriptorProto.Types.Type.Message
        && !WrapperTypes.ContainsKey(vf.TypeName);

    private static void EmitOpaqueClass(
        StringBuilder sb,
        TypeSymbol msgSym,
        DescriptorProto msg,
        bool isProto2,
        List<FieldDescriptorProto> fields,
        Dictionary<string, TypeSymbol> symbols,
        string indent)
    {
        string opaqueName = $"Opaque{msg.Name}";
        List<(int Index, string PascalName, List<FieldDescriptorProto> Fields)> realOneofs = GetRealOneofs(msg);

        sb.AppendLine($"{indent}public sealed class {opaqueName}");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    private readonly byte[] _raw;");
        sb.AppendLine($"{indent}    private ulong _dirtyMaskLow;");
        sb.AppendLine($"{indent}    private HashSet<uint>? _dirtyOverflow;");

        foreach (FieldDescriptorProto f in fields)
        {
            string prop = GetPropertyName(f, msg.Name);
            string fieldStorageType = OpaqueStorageType(f, symbols);
            sb.AppendLine($"{indent}    private {fieldStorageType} _dirty_{prop};");
            if (HasExplicitScalarPresence(f, isProto2) || IsRealOneof(f))
            {
                sb.AppendLine($"{indent}    private bool _present_{prop};");
            }

            if (IsOpaqueSubMsgField(f, symbols))
            {
                sb.AppendLine($"{indent}    private bool _hasCached_{prop};");
                sb.AppendLine($"{indent}    private {symbols[f.TypeName].CSharpOpaqueFullName}? _cached_{prop};");
            }
            else if (IsOpaqueSubMsgListField(f, symbols))
            {
                sb.AppendLine($"{indent}    private List<{symbols[f.TypeName].CSharpOpaqueFullName}>? _cached_{prop};");
            }
            else if (IsOpaqueSubMsgMapField(f, symbols))
            {
                _ = IsMapField(f, symbols, out FieldDescriptorProto? kf, out FieldDescriptorProto? vf);
                sb.AppendLine($"{indent}    private Dictionary<{CSharpScalarType(kf!, symbols)}, {symbols[vf!.TypeName].CSharpOpaqueFullName}>? _cached_{prop};");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"{indent}    public {opaqueName}(byte[] raw)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        _raw = raw ?? Array.Empty<byte>();");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public {opaqueName}(ReadOnlySpan<byte> raw)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        _raw = raw.ToArray();");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public ReadOnlySpan<byte> RawSpan => _raw;");
        sb.AppendLine();

        sb.AppendLine($"{indent}    internal bool HasDirtyFields");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        get");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            PropagateChildDirty();");
        sb.AppendLine($"{indent}            return _dirtyMaskLow != 0 || (_dirtyOverflow is not null && _dirtyOverflow.Count > 0);");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();

        if (fields.Count > 0)
        {
            sb.AppendLine($"{indent}    private bool IsDirty(uint fieldNum) => fieldNum < 64");
            sb.AppendLine($"{indent}        ? (_dirtyMaskLow & (1UL << (int)fieldNum)) != 0");
            sb.AppendLine($"{indent}        : (_dirtyOverflow is not null && _dirtyOverflow.Contains(fieldNum));");
            sb.AppendLine();
            sb.AppendLine($"{indent}    private void MarkDirty(uint fieldNum)");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        if (fieldNum < 64)");
            sb.AppendLine($"{indent}        {{");
            sb.AppendLine($"{indent}            _dirtyMaskLow |= (1UL << (int)fieldNum);");
            sb.AppendLine($"{indent}        }}");
            sb.AppendLine($"{indent}        else");
            sb.AppendLine($"{indent}        {{");
            sb.AppendLine($"{indent}            (_dirtyOverflow ??= new HashSet<uint>()).Add(fieldNum);");
            sb.AppendLine($"{indent}        }}");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
        }

        sb.AppendLine($"{indent}    private void PropagateChildDirty()");
        sb.AppendLine($"{indent}    {{");
        foreach (FieldDescriptorProto f in fields)
        {
            uint fnum = (uint)f.Number;
            string prop = GetPropertyName(f, msg.Name);
            if (IsOpaqueSubMsgField(f, symbols))
            {
                sb.AppendLine($"{indent}        if (!IsDirty({fnum}u) && _hasCached_{prop} && _cached_{prop} is not null && _cached_{prop}.HasDirtyFields)");
                sb.AppendLine($"{indent}        {{");
                sb.AppendLine($"{indent}            _dirty_{prop} = _cached_{prop};");
                if (IsRealOneof(f))
                {
                    sb.AppendLine($"{indent}            _present_{prop} = true;");
                    EmitClearOneofSiblings(sb, indent + "            ", msg, f);
                }

                sb.AppendLine($"{indent}            MarkDirty({fnum}u);");
                sb.AppendLine($"{indent}        }}");
            }
            else if (IsOpaqueSubMsgListField(f, symbols))
            {
                sb.AppendLine($"{indent}        if (!IsDirty({fnum}u) && _cached_{prop} is not null)");
                sb.AppendLine($"{indent}        {{");
                sb.AppendLine($"{indent}            for (int i = 0; i < _cached_{prop}.Count; i++)");
                sb.AppendLine($"{indent}            {{");
                sb.AppendLine($"{indent}                if (_cached_{prop}[i].HasDirtyFields)");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    _dirty_{prop} = _cached_{prop};");
                sb.AppendLine($"{indent}                    MarkDirty({fnum}u);");
                sb.AppendLine($"{indent}                    break;");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}            }}");
                sb.AppendLine($"{indent}        }}");
            }
            else if (IsOpaqueSubMsgMapField(f, symbols))
            {
                sb.AppendLine($"{indent}        if (!IsDirty({fnum}u) && _cached_{prop} is not null)");
                sb.AppendLine($"{indent}        {{");
                sb.AppendLine($"{indent}            foreach (var kv in _cached_{prop})");
                sb.AppendLine($"{indent}            {{");
                sb.AppendLine($"{indent}                if (kv.Value.HasDirtyFields)");
                sb.AppendLine($"{indent}                {{");
                sb.AppendLine($"{indent}                    _dirty_{prop} = _cached_{prop};");
                sb.AppendLine($"{indent}                    MarkDirty({fnum}u);");
                sb.AppendLine($"{indent}                    break;");
                sb.AppendLine($"{indent}                }}");
                sb.AppendLine($"{indent}            }}");
                sb.AppendLine($"{indent}        }}");
            }
        }

        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();

        foreach ((_, string oneofPascal, List<FieldDescriptorProto> members) in realOneofs)
        {
            sb.AppendLine($"{indent}    public void Clear{oneofPascal}()");
            sb.AppendLine($"{indent}    {{");
            foreach (FieldDescriptorProto mf in members)
            {
                string mProp = GetPropertyName(mf, msg.Name);
                sb.AppendLine($"{indent}        _present_{mProp} = false;");
                sb.AppendLine($"{indent}        MarkDirty({(uint)mf.Number}u);");
            }

            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
        }

        foreach (FieldDescriptorProto f in fields)
        {
            EmitOpaqueFieldMembers(sb, msg, f, isProto2, symbols, indent + "    ");
        }

        sb.AppendLine($"{indent}    public void ResetDirty()");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        _dirtyMaskLow = 0;");
        sb.AppendLine($"{indent}        _dirtyOverflow?.Clear();");
        foreach (FieldDescriptorProto f in fields)
        {
            string prop = GetPropertyName(f, msg.Name);
            if (IsOpaqueSubMsgField(f, symbols))
            {
                sb.AppendLine($"{indent}        _hasCached_{prop} = false;");
                sb.AppendLine($"{indent}        _cached_{prop} = null;");
            }
            else if (IsOpaqueSubMsgListField(f, symbols) || IsOpaqueSubMsgMapField(f, symbols))
            {
                sb.AppendLine($"{indent}        _cached_{prop} = null;");
            }
        }

        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();

        sb.AppendLine($"{indent}    public void MarshalOmniBuf(OmniBufEncoder encoder, MarshalOptions options = default)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        PropagateChildDirty();");
        sb.AppendLine($"{indent}        if (options.SelfDescribing)");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            SchemaMeta? meta = {msgSym.CSharpFullName}.BuildOmniBufSchemaMeta(1, null, out bool cyclic);");
        sb.AppendLine($"{indent}            if (cyclic)");
        sb.AppendLine($"{indent}            {{");
        sb.AppendLine($"{indent}                byte[] body = OmniBufOpaque.SpliceFields(_raw, _dirtyMaskLow, _dirtyOverflow, (Self: this, Deterministic: options.Deterministic), static (st, enc, fnum) => st.Self.AppendDirtyField(enc, fnum, st.Deterministic, validate: false), stripField0: true);");
        sb.AppendLine($"{indent}                meta = {msgSym.CSharpFullName}.ParseOmniBuf(body).BuildOmniBufValueSchemaMeta(1, null);");
        sb.AppendLine($"{indent}                if (meta is not null)");
        sb.AppendLine($"{indent}                {{");
        sb.AppendLine($"{indent}                    SchemaMeta.WriteField0(encoder, meta, options.Deterministic);");
        sb.AppendLine($"{indent}                }}");
        sb.AppendLine($"{indent}                encoder.WriteRawBytes(body);");
        sb.AppendLine($"{indent}                return;");
        sb.AppendLine($"{indent}            }}");
        sb.AppendLine($"{indent}            if (meta is not null)");
        sb.AppendLine($"{indent}            {{");
        sb.AppendLine($"{indent}                SchemaMeta.WriteField0(encoder, meta, options.Deterministic);");
        sb.AppendLine($"{indent}            }}");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}        OmniBufOpaque.SpliceFieldsTo(encoder, _raw, _dirtyMaskLow, _dirtyOverflow, (Self: this, Deterministic: options.Deterministic), static (st, enc, fnum) => st.Self.AppendDirtyField(enc, fnum, st.Deterministic, validate: false), stripField0: options.SelfDescribing);");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();

        sb.AppendLine($"{indent}    public byte[] MarshalOmniBuf(MarshalOptions options = default)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        PropagateChildDirty();");
        sb.AppendLine($"{indent}        if (!options.SelfDescribing)");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            return (_dirtyMaskLow == 0 && (_dirtyOverflow is null || _dirtyOverflow.Count == 0))");
        sb.AppendLine($"{indent}                ? (byte[])_raw.Clone()");
        sb.AppendLine($"{indent}                : OmniBufOpaque.SpliceFields(_raw, _dirtyMaskLow, _dirtyOverflow, (Self: this, Deterministic: options.Deterministic), static (st, enc, fnum) => st.Self.AppendDirtyField(enc, fnum, st.Deterministic, validate: false));");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}        OmniBufEncoder enc = OmniBufEncoder.RentThreadLocal(out bool rented);");
        sb.AppendLine($"{indent}        try");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            MarshalOmniBuf(enc, options);");
        sb.AppendLine($"{indent}            return enc.ToArray();");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}        finally");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            OmniBufEncoder.ReturnThreadLocal(enc, rented);");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();

        sb.AppendLine($"{indent}    public byte[] MarshalValidated(MarshalOptions options = default)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        PropagateChildDirty();");
        sb.AppendLine($"{indent}        if (!options.SelfDescribing)");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            return OmniBufOpaque.SpliceFieldsValidated(_raw, _dirtyMaskLow, _dirtyOverflow, (Self: this, Deterministic: options.Deterministic), static (st, enc, fnum) => st.Self.AppendDirtyField(enc, fnum, st.Deterministic, validate: true));");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}        OmniBufEncoder enc = OmniBufEncoder.RentThreadLocal(out bool rented);");
        sb.AppendLine($"{indent}        try");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            SchemaMeta? meta = {msgSym.CSharpFullName}.BuildOmniBufSchemaMeta(1, null, out bool cyclic);");
        sb.AppendLine($"{indent}            if (cyclic)");
        sb.AppendLine($"{indent}            {{");
        sb.AppendLine($"{indent}                byte[] body = OmniBufOpaque.SpliceFieldsValidated(_raw, _dirtyMaskLow, _dirtyOverflow, (Self: this, Deterministic: options.Deterministic), static (st, e, fnum) => st.Self.AppendDirtyField(e, fnum, st.Deterministic, validate: true), stripField0: true);");
        sb.AppendLine($"{indent}                meta = {msgSym.CSharpFullName}.ParseOmniBuf(body).BuildOmniBufValueSchemaMeta(1, null);");
        sb.AppendLine($"{indent}                if (meta is not null)");
        sb.AppendLine($"{indent}                {{");
        sb.AppendLine($"{indent}                    SchemaMeta.WriteField0(enc, meta, options.Deterministic);");
        sb.AppendLine($"{indent}                }}");
        sb.AppendLine($"{indent}                enc.WriteRawBytes(body);");
        sb.AppendLine($"{indent}            }}");
        sb.AppendLine($"{indent}            else");
        sb.AppendLine($"{indent}            {{");
        sb.AppendLine($"{indent}                if (meta is not null)");
        sb.AppendLine($"{indent}                {{");
        sb.AppendLine($"{indent}                    SchemaMeta.WriteField0(enc, meta, options.Deterministic);");
        sb.AppendLine($"{indent}                }}");
        sb.AppendLine($"{indent}                OmniBufOpaque.SpliceFieldsValidatedTo(enc, _raw, _dirtyMaskLow, _dirtyOverflow, (Self: this, Deterministic: options.Deterministic), static (st, e, fnum) => st.Self.AppendDirtyField(e, fnum, st.Deterministic, validate: true), stripField0: true);");
        sb.AppendLine($"{indent}            }}");
        sb.AppendLine($"{indent}            return enc.ToArray();");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}        finally");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            OmniBufEncoder.ReturnThreadLocal(enc, rented);");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();

        sb.AppendLine($"{indent}    public bool TryMarshal(out byte[] packet, out string? error, MarshalOptions options = default)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        try");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            packet = MarshalValidated(options);");
        sb.AppendLine($"{indent}            error = null;");
        sb.AppendLine($"{indent}            return true;");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}        catch (OmniBufException ex)");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            packet = Array.Empty<byte>();");
        sb.AppendLine($"{indent}            error = ex.Message;");
        sb.AppendLine($"{indent}            return false;");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();

        sb.AppendLine($"{indent}    private void AppendDirtyField(OmniBufEncoder encoder, uint fieldNum, bool deterministic, bool validate)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        _ = deterministic;");
        sb.AppendLine($"{indent}        _ = validate;");
        if (fields.Count > 0)
        {
            sb.AppendLine($"{indent}        switch (fieldNum)");
            sb.AppendLine($"{indent}        {{");
            foreach (FieldDescriptorProto f in fields)
            {
                uint fnum = (uint)f.Number;
                string prop = GetPropertyName(f, msg.Name);
                sb.AppendLine($"{indent}            case {fnum}u:");
                sb.AppendLine($"{indent}            {{");
                EmitOpaqueAppendDirtyCase(sb, indent + "                ", fnum, prop, f, isProto2, symbols);
                sb.AppendLine($"{indent}                break;");
                sb.AppendLine($"{indent}            }}");
            }

            sb.AppendLine($"{indent}        }}");
        }
        else
        {
            sb.AppendLine($"{indent}        _ = encoder;");
            sb.AppendLine($"{indent}        _ = fieldNum;");
        }

        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
    }

    private static void EmitClearOneofSiblings(
        StringBuilder sb,
        string indent,
        DescriptorProto msg,
        FieldDescriptorProto activeField)
    {
        int oneofIdx = activeField.OneofIndex;
        foreach (FieldDescriptorProto sibling in msg.Field)
        {
            if (sibling.Number != activeField.Number
                && sibling.HasOneofIndex
                && sibling.OneofIndex == oneofIdx
                && !sibling.Proto3Optional)
            {
                string sibProp = GetPropertyName(sibling, msg.Name);
                sb.AppendLine($"{indent}_present_{sibProp} = false;");
                sb.AppendLine($"{indent}MarkDirty({(uint)sibling.Number}u);");
            }
        }
    }

    private static void EmitOpaqueFieldMembers(
        StringBuilder sb,
        DescriptorProto msg,
        FieldDescriptorProto f,
        bool isProto2,
        Dictionary<string, TypeSymbol> symbols,
        string indent)
    {
        uint fnum = (uint)f.Number;
        string prop = GetPropertyName(f, msg.Name);

        if (IsMapField(f, symbols, out FieldDescriptorProto? keyField, out FieldDescriptorProto? valField))
        {
            string keyCs = CSharpScalarType(keyField!, symbols);
            string keyWt = WireTypeEnumName(keyField.Type);
            string valWt = WireTypeEnumName(valField!.Type);
            string keyReader = OpaqueElemReaderExpr(keyField, symbols);
            string comparerArg = keyField.Type == FieldDescriptorProto.Types.Type.String ? "StringComparer.Ordinal" : "null";
            string emptyDictCtorArgs = keyField.Type == FieldDescriptorProto.Types.Type.String ? "StringComparer.Ordinal" : string.Empty;
            string dictCtorArgs = keyField.Type == FieldDescriptorProto.Types.Type.String ? "value, StringComparer.Ordinal" : "value";

            if (valField.Type == FieldDescriptorProto.Types.Type.Message && !WrapperTypes.ContainsKey(valField.TypeName))
            {
                TypeSymbol valSym = symbols[valField.TypeName];
                string valOpaque = valSym.CSharpOpaqueFullName;
                string valMsg = valSym.CSharpFullName;
                string valReader = $"static (ReadOnlySpan<byte> b, ref int p) => new {valOpaque}(OmniBufOpaque.ReadElemBytes(b, ref p))";

                sb.AppendLine($"{indent}public IReadOnlyDictionary<{keyCs}, {valOpaque}> {prop}");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    get");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        if (IsDirty({fnum}u))");
                sb.AppendLine($"{indent}        {{");
                sb.AppendLine($"{indent}            return _dirty_{prop} ?? new Dictionary<{keyCs}, {valOpaque}>({emptyDictCtorArgs});");
                sb.AppendLine($"{indent}        }}");
                sb.AppendLine($"{indent}        _cached_{prop} ??= OmniBufOpaque.ReadMap(_raw, {fnum}u, OmniBufWireType.{keyWt}, OmniBufWireType.{valWt}, {keyReader}, {valReader}, {comparerArg});");
                sb.AppendLine($"{indent}        return _cached_{prop};");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}}}");
                sb.AppendLine($"{indent}public void Set{prop}(IDictionary<{keyCs}, {valMsg}> value)");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    var dict = new Dictionary<{keyCs}, {valOpaque}>({(keyField.Type == FieldDescriptorProto.Types.Type.String ? "value.Count, StringComparer.Ordinal" : "value.Count")});");
                sb.AppendLine($"{indent}    foreach (var kv in value)");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        dict[kv.Key] = new {valOpaque}(kv.Value.MarshalOmniBuf());");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}    _dirty_{prop} = dict;");
                sb.AppendLine($"{indent}    MarkDirty({fnum}u);");
                sb.AppendLine($"{indent}}}");
                sb.AppendLine($"{indent}public void Set{prop}(IDictionary<{keyCs}, {valOpaque}> value)");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    _dirty_{prop} = new Dictionary<{keyCs}, {valOpaque}>({dictCtorArgs});");
                sb.AppendLine($"{indent}    MarkDirty({fnum}u);");
                sb.AppendLine($"{indent}}}");
                sb.AppendLine();
                return;
            }

            string valCs = OpaqueCollectionElemType(valField, symbols);
            string valScalarReader = OpaqueElemReaderExpr(valField, symbols);
            sb.AppendLine($"{indent}public IReadOnlyDictionary<{keyCs}, {valCs}> {prop}");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    get => IsDirty({fnum}u)");
            sb.AppendLine($"{indent}        ? (_dirty_{prop} ?? new Dictionary<{keyCs}, {valCs}>({emptyDictCtorArgs}))");
            sb.AppendLine($"{indent}        : OmniBufOpaque.ReadMap(_raw, {fnum}u, OmniBufWireType.{keyWt}, OmniBufWireType.{valWt}, {keyReader}, {valScalarReader}, {comparerArg});");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}public void Set{prop}(IDictionary<{keyCs}, {valCs}> value)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    _dirty_{prop} = new Dictionary<{keyCs}, {valCs}>({dictCtorArgs});");
            sb.AppendLine($"{indent}    MarkDirty({fnum}u);");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine();
            return;
        }

        if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
        {
            if (f.Type == FieldDescriptorProto.Types.Type.Message && !WrapperTypes.ContainsKey(f.TypeName))
            {
                TypeSymbol sym = symbols[f.TypeName];
                sb.AppendLine($"{indent}public IReadOnlyList<{sym.CSharpOpaqueFullName}> {prop}");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    get");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        if (IsDirty({fnum}u))");
                sb.AppendLine($"{indent}        {{");
                sb.AppendLine($"{indent}            return (IReadOnlyList<{sym.CSharpOpaqueFullName}>?)_dirty_{prop} ?? Array.Empty<{sym.CSharpOpaqueFullName}>();");
                sb.AppendLine($"{indent}        }}");
                sb.AppendLine($"{indent}        if (_cached_{prop} is null)");
                sb.AppendLine($"{indent}        {{");
                sb.AppendLine($"{indent}            List<byte[]> payloads = OmniBufOpaque.ReadSubMsgArrayPayloads(_raw, {fnum}u);");
                sb.AppendLine($"{indent}            var list = new List<{sym.CSharpOpaqueFullName}>(payloads.Count);");
                sb.AppendLine($"{indent}            for (int i = 0; i < payloads.Count; i++)");
                sb.AppendLine($"{indent}            {{");
                sb.AppendLine($"{indent}                list.Add(new {sym.CSharpOpaqueFullName}(payloads[i]));");
                sb.AppendLine($"{indent}            }}");
                sb.AppendLine($"{indent}            _cached_{prop} = list;");
                sb.AppendLine($"{indent}        }}");
                sb.AppendLine($"{indent}        return _cached_{prop};");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}}}");
                sb.AppendLine($"{indent}public void Set{prop}(IEnumerable<{sym.CSharpFullName}> values)");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    var list = new List<{sym.CSharpOpaqueFullName}>();");
                sb.AppendLine($"{indent}    foreach (var item in values)");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        list.Add(new {sym.CSharpOpaqueFullName}(item.MarshalOmniBuf()));");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}    _dirty_{prop} = list;");
                sb.AppendLine($"{indent}    MarkDirty({fnum}u);");
                sb.AppendLine($"{indent}}}");
                sb.AppendLine();
                return;
            }

            string elemCs = OpaqueCollectionElemType(f, symbols);
            string elemWt = WireTypeEnumName(f.Type);
            string elemReader = OpaqueElemReaderExpr(f, symbols);
            sb.AppendLine($"{indent}public IReadOnlyList<{elemCs}> {prop}");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    get => IsDirty({fnum}u)");
            sb.AppendLine($"{indent}        ? ((IReadOnlyList<{elemCs}>?)_dirty_{prop} ?? Array.Empty<{elemCs}>())");
            sb.AppendLine($"{indent}        : OmniBufOpaque.ReadArray(_raw, {fnum}u, OmniBufWireType.{elemWt}, {elemReader});");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}public void Set{prop}(IEnumerable<{elemCs}> values)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    _dirty_{prop} = new List<{elemCs}>(values);");
            sb.AppendLine($"{indent}    MarkDirty({fnum}u);");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine();
            return;
        }

        if (f.Type == FieldDescriptorProto.Types.Type.Message)
        {
            if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? wrap))
            {
                string propType = wrap.IsValueType ? wrap.ElemCsType : (wrap.ElemCsType + "?");
                string readWrap = $"OmniBufOpaque.TryReadSubMsgBytes(_raw, {fnum}u, out byte[] sub) ? ({propType})OmniBufWrappers.Decode{wrap.MethodSuffix}(sub, {wrap.DefaultExpr}, 1, OmniBufConstants.MaxNestingDepth) : null";
                sb.AppendLine($"{indent}public {propType} {prop}");
                sb.AppendLine($"{indent}{{");
                if (IsRealOneof(f))
                {
                    sb.AppendLine($"{indent}    get => IsDirty({fnum}u) ? (_present_{prop} ? _dirty_{prop} : null) : ({readWrap});");
                    sb.AppendLine($"{indent}    set");
                    sb.AppendLine($"{indent}    {{");
                    sb.AppendLine($"{indent}        _dirty_{prop} = value;");
                    sb.AppendLine($"{indent}        _present_{prop} = value is not null;");
                    EmitClearOneofSiblings(sb, indent + "        ", msg, f);
                    sb.AppendLine($"{indent}        MarkDirty({fnum}u);");
                    sb.AppendLine($"{indent}    }}");
                }
                else
                {
                    sb.AppendLine($"{indent}    get => IsDirty({fnum}u) ? _dirty_{prop} : ({readWrap});");
                    sb.AppendLine($"{indent}    set");
                    sb.AppendLine($"{indent}    {{");
                    sb.AppendLine($"{indent}        _dirty_{prop} = value;");
                    sb.AppendLine($"{indent}        MarkDirty({fnum}u);");
                    sb.AppendLine($"{indent}    }}");
                }

                sb.AppendLine($"{indent}}}");
                sb.AppendLine();
                return;
            }

            TypeSymbol sym = symbols[f.TypeName];
            sb.AppendLine($"{indent}public {sym.CSharpOpaqueFullName}? {prop}");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    get");
            sb.AppendLine($"{indent}    {{");
            if (IsRealOneof(f))
            {
                sb.AppendLine($"{indent}        if (IsDirty({fnum}u)) return _present_{prop} ? _dirty_{prop} : null;");
            }
            else
            {
                sb.AppendLine($"{indent}        if (IsDirty({fnum}u)) return _dirty_{prop};");
            }

            sb.AppendLine($"{indent}        if (!_hasCached_{prop})");
            sb.AppendLine($"{indent}        {{");
            sb.AppendLine($"{indent}            _cached_{prop} = OmniBufOpaque.TryReadSubMsgBytes(_raw, {fnum}u, out byte[] sub) ? new {sym.CSharpOpaqueFullName}(sub) : null;");
            sb.AppendLine($"{indent}            _hasCached_{prop} = true;");
            sb.AppendLine($"{indent}        }}");
            sb.AppendLine($"{indent}        return _cached_{prop};");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}    set");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        _dirty_{prop} = value;");
            if (IsRealOneof(f))
            {
                sb.AppendLine($"{indent}        _present_{prop} = value is not null;");
                EmitClearOneofSiblings(sb, indent + "        ", msg, f);
            }

            sb.AppendLine($"{indent}        MarkDirty({fnum}u);");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}public void Set{prop}({sym.CSharpFullName}? value)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    this.{prop} = value is null ? null : new {sym.CSharpOpaqueFullName}(value.MarshalOmniBuf());");
            sb.AppendLine($"{indent}}}");

            if (sym.Message is not null)
            {
                foreach (FieldDescriptorProto cf in sym.Message.Field)
                {
                    if (cf.Label != FieldDescriptorProto.Types.Label.Repeated && cf.Type == FieldDescriptorProto.Types.Type.String)
                    {
                        string childProp = GetPropertyName(cf, sym.Message.Name);
                        sb.AppendLine($"{indent}public string Get{prop}{childProp}()");
                        sb.AppendLine($"{indent}{{");
                        sb.AppendLine($"{indent}    PropagateChildDirty();");
                        if (IsRealOneof(f))
                        {
                            sb.AppendLine($"{indent}    if (IsDirty({fnum}u)) return _present_{prop} ? (_dirty_{prop}?.{childProp} ?? string.Empty) : string.Empty;");
                        }
                        else
                        {
                            sb.AppendLine($"{indent}    if (IsDirty({fnum}u)) return _dirty_{prop}?.{childProp} ?? string.Empty;");
                        }

                        sb.AppendLine($"{indent}    return OmniBufOpaque.TryReadNestedString(_raw, {fnum}u, {(uint)cf.Number}u, out string s) ? s : string.Empty;");
                        sb.AppendLine($"{indent}}}");
                    }
                }
            }

            sb.AppendLine();
            return;
        }

        bool hasPresence = HasExplicitScalarPresence(f, isProto2) || IsRealOneof(f);

        if (f.Type == FieldDescriptorProto.Types.Type.Bytes)
        {
            sb.AppendLine($"{indent}public byte[] {prop}");
            sb.AppendLine($"{indent}{{");
            if (hasPresence)
            {
                sb.AppendLine($"{indent}    get => IsDirty({fnum}u) ? (_present_{prop} ? (_dirty_{prop} ?? Array.Empty<byte>()) : Array.Empty<byte>()) : (OmniBufOpaque.TryReadBytes(_raw, {fnum}u, out byte[] b) ? b : Array.Empty<byte>());");
                sb.AppendLine($"{indent}    set");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        _dirty_{prop} = value ?? Array.Empty<byte>();");
                sb.AppendLine($"{indent}        _present_{prop} = true;");
                if (IsRealOneof(f))
                {
                    EmitClearOneofSiblings(sb, indent + "        ", msg, f);
                }

                sb.AppendLine($"{indent}        MarkDirty({fnum}u);");
                sb.AppendLine($"{indent}    }}");
            }
            else
            {
                sb.AppendLine($"{indent}    get => IsDirty({fnum}u) ? (_dirty_{prop} ?? Array.Empty<byte>()) : (OmniBufOpaque.TryReadBytes(_raw, {fnum}u, out byte[] b) ? b : Array.Empty<byte>());");
                sb.AppendLine($"{indent}    set {{ _dirty_{prop} = value ?? Array.Empty<byte>(); MarkDirty({fnum}u); }}");
            }

            sb.AppendLine($"{indent}}}");
            if (hasPresence)
            {
                EmitOpaquePresenceHelpers(sb, indent, fnum, prop, f);
            }

            sb.AppendLine();
            return;
        }

        string csType = CSharpScalarType(f, symbols);
        string readExpr = EmitOpaqueScalarRead(fnum, f, symbols);
        string defaultExpr = f.Type == FieldDescriptorProto.Types.Type.String ? "string.Empty" : DefaultValueLiteral(f, symbols);
        sb.AppendLine($"{indent}public {csType} {prop}");
        sb.AppendLine($"{indent}{{");
        if (hasPresence)
        {
            sb.AppendLine($"{indent}    get => IsDirty({fnum}u) ? (_present_{prop} ? _dirty_{prop}! : {defaultExpr}) : ({readExpr});");
            sb.AppendLine($"{indent}    set");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        _dirty_{prop} = value;");
            sb.AppendLine($"{indent}        _present_{prop} = true;");
            if (IsRealOneof(f))
            {
                EmitClearOneofSiblings(sb, indent + "        ", msg, f);
            }

            sb.AppendLine($"{indent}        MarkDirty({fnum}u);");
            sb.AppendLine($"{indent}    }}");
        }
        else
        {
            sb.AppendLine($"{indent}    get => IsDirty({fnum}u) ? _dirty_{prop}! : ({readExpr});");
            sb.AppendLine($"{indent}    set {{ _dirty_{prop} = value; MarkDirty({fnum}u); }}");
        }

        sb.AppendLine($"{indent}}}");
        if (hasPresence)
        {
            EmitOpaquePresenceHelpers(sb, indent, fnum, prop, f);
        }

        sb.AppendLine();
    }

    private static void EmitOpaquePresenceHelpers(
        StringBuilder sb,
        string indent,
        uint fnum,
        string prop,
        FieldDescriptorProto f)
    {
        string maskExpr = f.Type == FieldDescriptorProto.Types.Type.Bool
            ? "(ushort)((1 << (byte)OmniBufWireType.BoolFalse) | (1 << (byte)OmniBufWireType.BoolTrue))"
            : $"(ushort)(1 << (byte)OmniBufWireType.{WireTypeEnumName(f.Type)})";
        sb.AppendLine($"{indent}public bool Has{prop} => IsDirty({fnum}u) ? _present_{prop} : OmniBufOpaque.LocateFieldMask(_raw, {fnum}u, {maskExpr}, out _, out _);");
        sb.AppendLine($"{indent}public void Clear{prop}()");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    _present_{prop} = false;");
        sb.AppendLine($"{indent}    MarkDirty({fnum}u);");
        sb.AppendLine($"{indent}}}");
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

    private static string OpaqueCollectionElemType(FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols)
    {
        if (f.Type == FieldDescriptorProto.Types.Type.Message && WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? wrap))
        {
            return wrap.ElemCsType;
        }

        return CSharpScalarType(f, symbols);
    }

    private static string OpaqueElemReaderExpr(FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols)
    {
        if (f.Type == FieldDescriptorProto.Types.Type.Message && WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? wrap))
        {
            return $"OmniBufWrappers.ReadElem{wrap.MethodSuffix}";
        }

        return f.Type switch
        {
            FieldDescriptorProto.Types.Type.Uint32 => "OmniBufOpaque.ReadElemUint32",
            FieldDescriptorProto.Types.Type.Uint64 => "OmniBufOpaque.ReadElemUint64",
            FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32 => "OmniBufOpaque.ReadElemSint32",
            FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 => "OmniBufOpaque.ReadElemSint64",
            FieldDescriptorProto.Types.Type.Enum => $"static (ReadOnlySpan<byte> b, ref int p) => ({symbols[f.TypeName].CSharpFullName})OmniBufOpaque.ReadElemSint32(b, ref p)",
            FieldDescriptorProto.Types.Type.Bool => "OmniBufOpaque.ReadElemBool",
            FieldDescriptorProto.Types.Type.Fixed32 => "OmniBufOpaque.ReadElemFixed32",
            FieldDescriptorProto.Types.Type.Fixed64 => "OmniBufOpaque.ReadElemFixed64",
            FieldDescriptorProto.Types.Type.Sfixed32 => "OmniBufOpaque.ReadElemSfixed32",
            FieldDescriptorProto.Types.Type.Sfixed64 => "OmniBufOpaque.ReadElemSfixed64",
            FieldDescriptorProto.Types.Type.Float => "OmniBufOpaque.ReadElemFloat32",
            FieldDescriptorProto.Types.Type.Double => "OmniBufOpaque.ReadElemFloat64",
            FieldDescriptorProto.Types.Type.String => "OmniBufOpaque.ReadElemString",
            FieldDescriptorProto.Types.Type.Bytes => "OmniBufOpaque.ReadElemByteString",
            _ => throw new InvalidOperationException($"Unsupported opaque element type {f.Type}"),
        };
    }

    private static void EmitOpaqueAppendDirtyCase(
        StringBuilder sb,
        string indent,
        uint fnum,
        string prop,
        FieldDescriptorProto f,
        bool isProto2,
        Dictionary<string, TypeSymbol> symbols)
    {
        if (IsMapField(f, symbols, out FieldDescriptorProto? keyField, out FieldDescriptorProto? valField))
        {
            string keyCs = CSharpScalarType(keyField!, symbols);
            string keyWt = WireTypeEnumName(keyField.Type);
            string valWt = WireTypeEnumName(valField!.Type);

            sb.AppendLine($"{indent}var map = _dirty_{prop};");
            sb.AppendLine($"{indent}int count = map?.Count ?? 0;");
            sb.AppendLine($"{indent}encoder.BeginMapField({fnum}u, OmniBufWireType.{keyWt}, OmniBufWireType.{valWt}, count);");
            sb.AppendLine($"{indent}if (map is not null && count > 0)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    if (deterministic && count > 1)");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        var keys = new List<{keyCs}>(map.Keys);");
            if (keyField.Type == FieldDescriptorProto.Types.Type.String)
            {
                sb.AppendLine($"{indent}        keys.Sort(StringComparer.Ordinal);");
            }
            else
            {
                sb.AppendLine($"{indent}        keys.Sort();");
            }

            sb.AppendLine($"{indent}        for (int i = 0; i < keys.Count; i++)");
            sb.AppendLine($"{indent}        {{");
            sb.AppendLine($"{indent}            var k = keys[i];");
            sb.AppendLine($"{indent}            var v = map[k];");
            sb.AppendLine($"{indent}            {EmitWriteRawValue("encoder", keyField, "k")};");
            EmitOpaqueMapValAppend(sb, indent + "            ", valField, "v");
            sb.AppendLine($"{indent}        }}");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}    else");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        foreach (var kv in map)");
            sb.AppendLine($"{indent}        {{");
            sb.AppendLine($"{indent}            {EmitWriteRawValue("encoder", keyField, "kv.Key")};");
            EmitOpaqueMapValAppend(sb, indent + "            ", valField, "kv.Value");
            sb.AppendLine($"{indent}        }}");
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
                if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? rWrap))
                {
                    sb.AppendLine($"{indent}        encoder.BeginContainer();");
                    if (rWrap.IsValueType)
                    {
                        sb.AppendLine($"{indent}        if (list[i].HasValue)");
                        sb.AppendLine($"{indent}        {{");
                        sb.AppendLine($"{indent}            OmniBufWrappers.Write{rWrap.MethodSuffix}Body(encoder, list[i]!.Value);");
                        sb.AppendLine($"{indent}        }}");
                    }
                    else
                    {
                        sb.AppendLine($"{indent}        if (list[i] is not null)");
                        sb.AppendLine($"{indent}        {{");
                        sb.AppendLine($"{indent}            OmniBufWrappers.Write{rWrap.MethodSuffix}Body(encoder, list[i]);");
                        sb.AppendLine($"{indent}        }}");
                    }

                    sb.AppendLine($"{indent}        encoder.EndContainer();");
                }
                else
                {
                    sb.AppendLine($"{indent}        byte[] elemBytes = validate ? list[i].MarshalValidated(new MarshalOptions(false, deterministic)) : list[i].MarshalOmniBuf(new MarshalOptions(false, deterministic));");
                    sb.AppendLine($"{indent}        encoder.WriteBytesValue(elemBytes);");
                }
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
            if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? sWrap))
            {
                string cond = IsRealOneof(f)
                    ? (sWrap.IsValueType ? $"_present_{prop} && _dirty_{prop}.HasValue" : $"_present_{prop} && _dirty_{prop} is not null")
                    : (sWrap.IsValueType ? $"_dirty_{prop}.HasValue" : $"_dirty_{prop} is not null");
                string valExpr = sWrap.IsValueType ? $"_dirty_{prop}!.Value" : $"_dirty_{prop}!";
                sb.AppendLine($"{indent}if ({cond})");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    encoder.BeginSubMsgField({fnum}u);");
                sb.AppendLine($"{indent}    OmniBufWrappers.Write{sWrap.MethodSuffix}Body(encoder, {valExpr});");
                sb.AppendLine($"{indent}    encoder.EndSubMsg();");
                sb.AppendLine($"{indent}}}");
                return;
            }

            string msgCond = IsRealOneof(f) ? $"_present_{prop} && _dirty_{prop} is not null" : $"_dirty_{prop} is not null";
            sb.AppendLine($"{indent}if ({msgCond})");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    byte[] subBytes = validate ? _dirty_{prop}!.MarshalValidated(new MarshalOptions(false, deterministic)) : _dirty_{prop}!.MarshalOmniBuf(new MarshalOptions(false, deterministic));");
            sb.AppendLine($"{indent}    encoder.WriteTag({fnum}u, OmniBufWireType.SubMsg);");
            sb.AppendLine($"{indent}    encoder.WriteBytesValue(subBytes);");
            sb.AppendLine($"{indent}}}");
            return;
        }

        bool hasPresence = HasExplicitScalarPresence(f, isProto2) || IsRealOneof(f);
        if (hasPresence)
        {
            sb.AppendLine($"{indent}if (_present_{prop})");
            sb.AppendLine($"{indent}{{");
            if (f.Type == FieldDescriptorProto.Types.Type.Bytes)
            {
                sb.AppendLine($"{indent}    encoder.WriteBytesField({fnum}u, _dirty_{prop} ?? Array.Empty<byte>());");
            }
            else
            {
                sb.AppendLine($"{indent}    {EmitWriteField("encoder", fnum, f, $"_dirty_{prop}!")};");
            }

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

    private static void EmitOpaqueMapValAppend(StringBuilder sb, string indent, FieldDescriptorProto valField, string valExpr)
    {
        if (valField.Type == FieldDescriptorProto.Types.Type.Message)
        {
            if (WrapperTypes.TryGetValue(valField.TypeName, out WrapperInfo? mWrap))
            {
                sb.AppendLine($"{indent}encoder.BeginContainer();");
                if (mWrap.IsValueType)
                {
                    sb.AppendLine($"{indent}if ({valExpr}.HasValue)");
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    OmniBufWrappers.Write{mWrap.MethodSuffix}Body(encoder, {valExpr}.Value);");
                    sb.AppendLine($"{indent}}}");
                }
                else
                {
                    sb.AppendLine($"{indent}if ({valExpr} is not null)");
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    OmniBufWrappers.Write{mWrap.MethodSuffix}Body(encoder, {valExpr});");
                    sb.AppendLine($"{indent}}}");
                }

                sb.AppendLine($"{indent}encoder.EndContainer();");
            }
            else
            {
                sb.AppendLine($"{indent}byte[] subBytes = validate ? {valExpr}.MarshalValidated(new MarshalOptions(false, deterministic)) : {valExpr}.MarshalOmniBuf(new MarshalOptions(false, deterministic));");
                sb.AppendLine($"{indent}encoder.WriteBytesValue(subBytes);");
            }
        }
        else
        {
            sb.AppendLine($"{indent}{EmitWriteRawValue("encoder", valField, valExpr)};");
        }
    }

    private static string OpaqueStorageType(FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols)
    {
        if (IsMapField(f, symbols, out FieldDescriptorProto? kf, out FieldDescriptorProto? vf))
        {
            string valType = (vf!.Type == FieldDescriptorProto.Types.Type.Message && !WrapperTypes.ContainsKey(vf.TypeName))
                ? symbols[vf.TypeName].CSharpOpaqueFullName
                : OpaqueCollectionElemType(vf, symbols);
            return $"Dictionary<{CSharpScalarType(kf!, symbols)}, {valType}>?";
        }

        if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
        {
            if (f.Type == FieldDescriptorProto.Types.Type.Message && !WrapperTypes.ContainsKey(f.TypeName))
            {
                return $"List<{symbols[f.TypeName].CSharpOpaqueFullName}>?";
            }

            return $"List<{OpaqueCollectionElemType(f, symbols)}>?";
        }

        if (f.Type == FieldDescriptorProto.Types.Type.Message)
        {
            if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? wrap))
            {
                return wrap.IsValueType ? wrap.ElemCsType : (wrap.ElemCsType + "?");
            }

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
        [NotNullWhen(true)] out FieldDescriptorProto? keyField,
        [NotNullWhen(true)] out FieldDescriptorProto? valField)
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
