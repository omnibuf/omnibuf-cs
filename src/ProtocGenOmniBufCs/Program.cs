using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace OmniBuf.ProtocGen;

public static class Program
{
    private const ulong FeatureProto3Optional = 1UL;
    private const ulong FeatureSupportsEditions = 2UL;
    private const int MaxFieldNumber = 536_870_911;

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
        Dictionary<string, bool> closedEnums = EditionResolver.BuildClosedEnumMap(protoFiles);
        var normalizedFiles = new List<FileDescriptorProto>(protoFiles.Count);
        var originalByName = new Dictionary<string, FileDescriptorProto>(protoFiles.Count, StringComparer.Ordinal);
        foreach (FileDescriptorProto rawFile in protoFiles)
        {
            originalByName[rawFile.Name] = rawFile;
            normalizedFiles.Add(EditionResolver.NormalizeForCodeGen(rawFile));
        }

        Dictionary<string, TypeSymbol> symbolTable = BuildSymbolTable(normalizedFiles, originalByName);
        var generatedFiles = new List<(string Name, string Content)>();
        var targetSet = new HashSet<string>(filesToGenerate, StringComparer.Ordinal);
        var validatedMessages = new HashSet<string>(StringComparer.Ordinal);
        var usedOutputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (FileDescriptorProto file in normalizedFiles)
        {
            if (!targetSet.Contains(file.Name))
            {
                continue;
            }

            FileDescriptorProto originalFile = originalByName[file.Name];
            string? validationError = ValidateFile(originalFile, file, symbolTable, closedEnums, validatedMessages);
            if (validationError is not null)
            {
                return (validationError, new List<(string Name, string Content)>());
            }

            string fileContent = GenerateFile(file, symbolTable);
            string outputName = ComputeOutputFileName(file.Name, usedOutputNames);
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
            cos.WriteUInt64(FeatureProto3Optional | FeatureSupportsEditions);
            cos.WriteTag(3, WireFormat.WireType.Varint);
            cos.WriteInt32(EditionResolver.MinimumSupportedEdition);
            cos.WriteTag(4, WireFormat.WireType.Varint);
            cos.WriteInt32(EditionResolver.MaximumSupportedEdition);

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
        public required string CSharpSpanViewFullName { get; init; }
        public required string SimpleName { get; init; }
        public required bool IsProto2 { get; init; }
        public required FileDescriptorProto SourceFile { get; init; }
        public DescriptorProto? Message { get; init; }
        public EnumDescriptorProto? Enum { get; init; }
        public bool IsMapEntry => Message?.Options?.MapEntry == true;
    }

    private static bool IsProto2File(FileDescriptorProto file) =>
        !string.Equals(file.Syntax, "proto3", StringComparison.Ordinal);

    private static Dictionary<string, TypeSymbol> BuildSymbolTable(
        IReadOnlyList<FileDescriptorProto> files,
        IReadOnlyDictionary<string, FileDescriptorProto> originalByName)
    {
        var table = new Dictionary<string, TypeSymbol>(StringComparer.Ordinal);
        foreach (FileDescriptorProto file in files)
        {
            FileDescriptorProto sourceFile = originalByName[file.Name];
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
                    CSharpSpanViewFullName = csPrefix + en.Name,
                    SimpleName = en.Name,
                    IsProto2 = isProto2,
                    SourceFile = sourceFile,
                    Enum = en,
                };
            }

            foreach (DescriptorProto msg in file.MessageType)
            {
                RegisterMessageSymbols(table, msg, protoPrefix, csPrefix, isNested: false, isProto2: isProto2, sourceFile: sourceFile);
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
        bool isProto2,
        FileDescriptorProto sourceFile)
    {
        string protoName = $"{protoParent}.{msg.Name}";
        string csName = isNested ? $"{csParent}Types.{msg.Name}" : $"{csParent}{msg.Name}";
        string csOpaqueName = isNested ? $"{csParent}Types.Opaque{msg.Name}" : $"{csParent}Opaque{msg.Name}";
        string csSpanViewName = isNested ? $"{csParent}Types.{msg.Name}SpanView" : $"{csParent}{msg.Name}SpanView";

        table[protoName] = new TypeSymbol
        {
            ProtoFullName = protoName,
            CSharpFullName = csName,
            CSharpOpaqueFullName = csOpaqueName,
            CSharpSpanViewFullName = csSpanViewName,
            SimpleName = msg.Name,
            IsProto2 = isProto2,
            SourceFile = sourceFile,
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
                CSharpSpanViewFullName = $"{csName}.Types.{en.Name}",
                SimpleName = en.Name,
                IsProto2 = isProto2,
                SourceFile = sourceFile,
                Enum = en,
            };
        }

        foreach (DescriptorProto nested in msg.NestedType)
        {
            RegisterMessageSymbols(table, nested, protoName, csName + ".", isNested: true, isProto2: isProto2, sourceFile: sourceFile);
        }
    }

    private static bool IsDescriptorOptionsExtendee(string extendee) =>
        extendee is ".google.protobuf.FileOptions"
            or ".google.protobuf.MessageOptions"
            or ".google.protobuf.FieldOptions"
            or ".google.protobuf.OneofOptions"
            or ".google.protobuf.EnumOptions"
            or ".google.protobuf.EnumValueOptions"
            or ".google.protobuf.ServiceOptions"
            or ".google.protobuf.MethodOptions"
            or ".google.protobuf.ExtensionRangeOptions"
            or "google.protobuf.FileOptions"
            or "google.protobuf.MessageOptions"
            or "google.protobuf.FieldOptions"
            or "google.protobuf.OneofOptions"
            or "google.protobuf.EnumOptions"
            or "google.protobuf.EnumValueOptions"
            or "google.protobuf.ServiceOptions"
            or "google.protobuf.MethodOptions"
            or "google.protobuf.ExtensionRangeOptions";

    private static string? ValidateFile(
        FileDescriptorProto originalFile,
        FileDescriptorProto normalizedFile,
        Dictionary<string, TypeSymbol> symbols,
        IReadOnlyDictionary<string, bool> closedEnums,
        HashSet<string> validatedMessages)
    {
        string? editionErr = EditionResolver.ValidateEditionFile(originalFile, closedEnums);
        if (editionErr is not null)
        {
            return editionErr;
        }

        foreach (FieldDescriptorProto ext in originalFile.Extension)
        {
            if (!IsDescriptorOptionsExtendee(ext.Extendee))
            {
                return $"omnibuf: Protobuf extensions in {originalFile.Name} are not supported";
            }
        }

        string protoPrefix = string.IsNullOrEmpty(normalizedFile.Package) ? string.Empty : "." + normalizedFile.Package;
        foreach (DescriptorProto msg in normalizedFile.MessageType)
        {
            string? err = ValidateMessage($"{protoPrefix}.{msg.Name}", msg, originalFile, symbols, closedEnums, validatedMessages);
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
        FileDescriptorProto sourceFile,
        Dictionary<string, TypeSymbol> symbols,
        IReadOnlyDictionary<string, bool> closedEnums,
        HashSet<string> validatedMessages)
    {
        if (!validatedMessages.Add(protoFullName))
        {
            return null;
        }

        bool isEditions = EditionResolver.IsEditionsFile(sourceFile);

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

            if (f.Type == FieldDescriptorProto.Types.Type.Group && !isEditions)
            {
                return $"omnibuf: Protobuf group field {protoFullName.TrimStart('.')}.{f.Name} is not supported";
            }

            if (f.HasDefaultValue && !IsZeroDefaultValue(f, symbols))
            {
                return $"omnibuf: custom non-zero default value '{f.DefaultValue}' on {protoFullName.TrimStart('.')}.{f.Name} is not supported";
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Enum
                && symbols.TryGetValue(f.TypeName, out TypeSymbol? enumRefSym)
                && EditionResolver.IsEditionsFile(enumRefSym.SourceFile)
                && !EditionResolver.IsSupportedEdition(enumRefSym.SourceFile.Edition))
            {
                return $"omnibuf: unsupported protobuf edition {enumRefSym.SourceFile.Edition} in {enumRefSym.SourceFile.Name}";
            }

            if ((f.Type == FieldDescriptorProto.Types.Type.Message
                    || (isEditions && f.Type == FieldDescriptorProto.Types.Type.Group))
                && !WrapperTypes.ContainsKey(f.TypeName)
                && symbols.TryGetValue(f.TypeName, out TypeSymbol? refSym)
                && refSym.Message is not null)
            {
                if (!ReferenceEquals(refSym.SourceFile, sourceFile))
                {
                    string? refEditionErr = EditionResolver.ValidateEditionMessageInFile(
                        refSym.SourceFile,
                        refSym.ProtoFullName,
                        closedEnums);
                    if (refEditionErr is not null)
                    {
                        return refEditionErr;
                    }
                }

                string? childErr = ValidateMessage(
                    refSym.ProtoFullName,
                    refSym.Message,
                    refSym.SourceFile,
                    symbols,
                    closedEnums,
                    validatedMessages);
                if (childErr is not null)
                {
                    return childErr;
                }
            }
        }

        foreach (DescriptorProto nested in msg.NestedType)
        {
            string? nestedErr = ValidateMessage($"{protoFullName}.{nested.Name}", nested, sourceFile, symbols, closedEnums, validatedMessages);
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

    private static string ComputeOutputFileName(string protoFileName, HashSet<string> usedOutputNames)
    {
        string baseName = Path.GetFileNameWithoutExtension(protoFileName);
        string pascal = ToPascalCase(baseName);
        string candidate = $"{pascal}.OmniBuf.g.cs";
        if (usedOutputNames.Add(candidate))
        {
            return candidate;
        }

        string dirPrefix = Path.GetDirectoryName(protoFileName)?.Replace('\\', '/') ?? string.Empty;
        string qualified = string.IsNullOrEmpty(dirPrefix)
            ? pascal
            : ToPascalCase(dirPrefix.Replace('/', '_') + "_" + baseName);
        candidate = $"{qualified}.OmniBuf.g.cs";
        int suffix = 2;
        while (!usedOutputNames.Add(candidate))
        {
            candidate = $"{qualified}_{suffix}.OmniBuf.g.cs";
            suffix++;
        }

        return candidate;
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"//     source: {file.Name}");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591, CS0612, CS3021, CS8981");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Buffers;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using Google.Protobuf;");
        sb.AppendLine("using OmniBuf;");
        sb.AppendLine();

        string ns = GetCSharpNamespace(file);
        if (!string.IsNullOrEmpty(ns))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {ns};");
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

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public sealed partial class {msg.Name}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");

        EmitSchemaMetaBuilders(sb, msg, protoFullName, isProto2, orderedFields, symbols, indent + "    ");
        EmitMarshalMethods(sb, msg, isProto2, orderedFields, symbols, indent + "    ");
        EmitUnmarshalMethods(sb, msgSym, msg, isProto2, orderedFields, symbols, indent + "    ");

        if (nestedMessages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public static partial class Types");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            foreach (DescriptorProto nested in nestedMessages)
            {
                EmitMessageCodecAndOpaque(sb, nested, $"{protoFullName}.{nested.Name}", isProto2, symbols, indent + "        ");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        sb.AppendLine();

        EmitOpaqueClass(sb, msgSym, msg, isProto2, orderedFields, symbols, indent);
        EmitSpanViewStruct(sb, msgSym, msg, isProto2, orderedFields, symbols, indent);
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public static SchemaMeta? BuildOmniBufSchemaMeta(int depth = 1, HashSet<string>? active = null) =>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    BuildOmniBufSchemaMeta(depth, active, out _);");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public static SchemaMeta? BuildOmniBufSchemaMeta(int depth, HashSet<string>? active, out bool cyclic)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    cyclic = false;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return null;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    active ??= new HashSet<string>(StringComparer.Ordinal);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (!active.Add(\"{protoFullName}\"))");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        cyclic = true;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return null;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    try");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        var meta = new SchemaMeta");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            TypeName = \"{msg.Name}\",");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            Fields = new Dictionary<uint, string>({fields.Count}),");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }};");

        foreach (FieldDescriptorProto f in fields)
        {
            uint fnum = (uint)f.Number;
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        meta.Fields[{fnum}u] = \"{f.Name}\";");

            if (TryResolveChildSchemaTarget(f, symbols, out TypeSymbol? childMsgSym, out WrapperInfo? childWrapper))
            {
                if (childWrapper is not null)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (OmniBufWrappers.BuildSchemaMeta(\"{childWrapper.ShortName}\", depth + 2) is {{ }} wrap_{fnum})");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            meta.Children[{fnum}u] = wrap_{fnum};");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                }
                else if (childMsgSym is not null)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        SchemaMeta? child_{fnum} = {childMsgSym.CSharpFullName}.BuildOmniBufSchemaMeta(depth + 2, active, out bool cyc_{fnum});");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        cyclic |= cyc_{fnum};");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (child_{fnum} is not null)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            meta.Children[{fnum}u] = child_{fnum};");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                }
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return meta;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    finally");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        active.Remove(\"{protoFullName}\");");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        sb.AppendLine();

        // Instance-driven SchemaMeta builder used when static schema has recursive message cycles (for example, TreeNode).
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}internal SchemaMeta? BuildOmniBufValueSchemaMeta(int depth = 1, HashSet<object>? active = null)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return null;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    active ??= new HashSet<object>(ReferenceEqualityComparer.Instance);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (!active.Add(this))");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return null;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    try");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        var meta = new SchemaMeta");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            TypeName = \"{msg.Name}\",");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            Fields = new Dictionary<uint, string>({fields.Count}),");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }};");

        bool hasChildMetaFields = fields.Any(f => TryResolveChildSchemaTarget(f, symbols, out _, out _));
        if (hasChildMetaFields)
        {
            foreach (FieldDescriptorProto f in fields)
            {
                uint fnum = (uint)f.Number;
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        meta.Fields[{fnum}u] = \"{f.Name}\";");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (depth + 3 <= OmniBufConstants.MaxNestingDepth)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
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
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (this.{prop}.Count > 0 && OmniBufWrappers.BuildSchemaMeta(\"{childWrapper.ShortName}\", depth + 2) is {{ }} mw_{fnum})");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                meta.Children[{fnum}u] = mw_{fnum};");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                    }
                    else if (childMsgSym is not null)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (this.{prop}.Count > 0)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                SchemaMeta? mm_{fnum} = null;");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                foreach (var kv in this.{prop})");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    if (kv.Value is not null)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        mm_{fnum} = SchemaMeta.Merge(mm_{fnum}, kv.Value.BuildOmniBufValueSchemaMeta(depth + 2, active));");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (mm_{fnum} is not null)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    meta.Children[{fnum}u] = mm_{fnum};");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                    }
                }
                else if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
                {
                    if (childWrapper is not null)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (this.{prop}.Count > 0 && OmniBufWrappers.BuildSchemaMeta(\"{childWrapper.ShortName}\", depth + 2) is {{ }} rw_{fnum})");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                meta.Children[{fnum}u] = rw_{fnum};");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                    }
                    else if (childMsgSym is not null)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (this.{prop}.Count > 0)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                SchemaMeta? rm_{fnum} = null;");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                for (int i = 0; i < this.{prop}.Count; i++)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    if (this.{prop}[i] is not null)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        rm_{fnum} = SchemaMeta.Merge(rm_{fnum}, this.{prop}[i].BuildOmniBufValueSchemaMeta(depth + 2, active));");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (rm_{fnum} is not null)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    meta.Children[{fnum}u] = rm_{fnum};");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                    }
                }
                else if (childWrapper is not null)
                {
                    string cond = IsRealOneof(f)
                        ? $"this.{ToPascalCase(msg.OneofDecl[f.OneofIndex].Name)}Case == {ToPascalCase(msg.OneofDecl[f.OneofIndex].Name)}OneofCase.{prop}"
                        : (childWrapper.IsValueType ? $"this.{prop}.HasValue" : $"this.{prop} is not null");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if ({cond} && OmniBufWrappers.BuildSchemaMeta(\"{childWrapper.ShortName}\", depth + 2) is {{ }} sw_{fnum})");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                meta.Children[{fnum}u] = sw_{fnum};");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                }
                else if (childMsgSym is not null)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (this.{prop} is {{ }} sv_{fnum} && sv_{fnum}.BuildOmniBufValueSchemaMeta(depth + 2, active) is {{ }} sm_{fnum})");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                meta.Children[{fnum}u] = sm_{fnum};");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                }
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        }
        else
        {
            foreach (FieldDescriptorProto f in fields)
            {
                uint fnum = (uint)f.Number;
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        meta.Fields[{fnum}u] = \"{f.Name}\";");
            }
        }

        _ = isProto2;
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return meta;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    finally");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        active.Remove(this);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public byte[] MarshalOmniBuf(MarshalOptions options = default)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    OmniBufEncoder encoder = OmniBufEncoder.RentThreadLocal(out bool rented);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    try");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        MarshalOmniBuf(encoder, options);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return encoder.ToArray();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    finally");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        OmniBufEncoder.ReturnThreadLocal(encoder, rented);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public void MarshalOmniBuf(IBufferWriter<byte> writer, MarshalOptions options = default)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    ArgumentNullException.ThrowIfNull(writer);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    OmniBufEncoder encoder = OmniBufEncoder.RentThreadLocal(out bool rented);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    try");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        MarshalOmniBuf(encoder, options);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.CopyTo(writer);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    finally");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        OmniBufEncoder.ReturnThreadLocal(encoder, rented);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}internal int EstimateOmniBufMinSize(int depth = 0)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (depth > 8) return 0;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    int total = 0;");
        foreach (FieldDescriptorProto f in fields)
        {
            string prop = GetPropertyName(f, msg.Name);
            if (IsMapField(f, symbols, out _, out _))
            {
                continue;
            }

            if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
            {
                if (f.Type == FieldDescriptorProto.Types.Type.String)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    for (int i = 0; i < this.{prop}.Count; i++) total += 1 + (this.{prop}[i]?.Length ?? 0);");
                }
                else if (f.Type == FieldDescriptorProto.Types.Type.Bytes)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    for (int i = 0; i < this.{prop}.Count; i++) total += 1 + this.{prop}[i].Length;");
                }
                else if (f.Type == FieldDescriptorProto.Types.Type.Message && !WrapperTypes.ContainsKey(f.TypeName))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    for (int i = 0; i < this.{prop}.Count; i++) total += 2 + (this.{prop}[i]?.EstimateOmniBufMinSize(depth + 1) ?? 0);");
                }

                continue;
            }

            if (IsRealOneof(f))
            {
                string oneofPascal = ToPascalCase(msg.OneofDecl[f.OneofIndex].Name);
                if (f.Type == FieldDescriptorProto.Types.Type.String)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (this.{oneofPascal}Case == {oneofPascal}OneofCase.{prop}) total += this.{prop}?.Length ?? 0;");
                }
                else if (f.Type == FieldDescriptorProto.Types.Type.Bytes)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (this.{oneofPascal}Case == {oneofPascal}OneofCase.{prop}) total += this.{prop}.Length;");
                }
                else if (f.Type == FieldDescriptorProto.Types.Type.Message && !WrapperTypes.ContainsKey(f.TypeName))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (this.{oneofPascal}Case == {oneofPascal}OneofCase.{prop} && this.{prop} is {{ }} om_{f.Number}) total += 3 + om_{f.Number}.EstimateOmniBufMinSize(depth + 1);");
                }

                continue;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.String)
            {
                if (HasExplicitScalarPresence(f, isProto2))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (this.Has{prop}) total += this.{prop}?.Length ?? 0;");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    total += this.{prop}?.Length ?? 0;");
                }
            }
            else if (f.Type == FieldDescriptorProto.Types.Type.Bytes)
            {
                if (HasExplicitScalarPresence(f, isProto2))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (this.Has{prop}) total += this.{prop}.Length;");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    total += this.{prop}.Length;");
                }
            }
            else if (f.Type == FieldDescriptorProto.Types.Type.Message && !WrapperTypes.ContainsKey(f.TypeName))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (this.{prop} is {{ }} sm_{f.Number}) total += 3 + sm_{f.Number}.EstimateOmniBufMinSize(depth + 1);");
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    return total;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public void MarshalOmniBuf(OmniBufEncoder encoder, MarshalOptions options = default)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (options.SelfDescribing)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        SchemaMeta? meta = BuildOmniBufSchemaMeta(1, null, out bool cyclic);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (cyclic)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            meta = BuildOmniBufValueSchemaMeta(1, null);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (meta is not null)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            SchemaMeta.WriteField0(encoder, meta, options.Deterministic);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    EncodeOmniBufBody(encoder, options.Deterministic, 0);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}internal void EncodeOmniBufBody(OmniBufEncoder encoder, bool deterministic, int depth = 0)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (depth > OmniBufConstants.MaxNestingDepth)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        throw new OmniBufException($\"omnibuf: encode nesting depth {{depth}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");

        foreach (FieldDescriptorProto f in fields)
        {
            uint fnum = (uint)f.Number;
            string prop = GetPropertyName(f, msg.Name);

            if (IsMapField(f, symbols, out FieldDescriptorProto? keyField, out FieldDescriptorProto? valField))
            {
                string keyWt = WireTypeEnumName(keyField!.Type);
                string valWt = WireTypeEnumName(valField!.Type);
                string keyCs = CSharpScalarType(keyField, symbols);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        throw new OmniBufException($\"omnibuf: WireMap encode depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    encoder.BeginMapField({fnum}u, OmniBufWireType.{keyWt}, OmniBufWireType.{valWt}, this.{prop}.Count);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (this.{prop}.Count > 0)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (deterministic && this.{prop}.Count > 1)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            var keys_{fnum} = new List<{keyCs}>(this.{prop}.Keys);");
                if (keyField.Type == FieldDescriptorProto.Types.Type.String)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            keys_{fnum}.Sort(Utf8OrdinalStringComparer.Instance);");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            keys_{fnum}.Sort();");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            for (int i = 0; i < keys_{fnum}.Count; i++)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                var k = keys_{fnum}[i];");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                var v = this.{prop}[k];");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {EmitWriteRawValue("encoder", keyField, "k")};");
                EmitMapValEncode(sb, indent + "                ", valField, "v", "depth + 2");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        else");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            foreach (var kv in this.{prop})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {EmitWriteRawValue("encoder", keyField, "kv.Key")};");
                EmitMapValEncode(sb, indent + "                ", valField, "kv.Value", "depth + 2");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    encoder.EndMap();");
                continue;
            }

            if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
            {
                string elemWt = WireTypeEnumName(f.Type);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        throw new OmniBufException($\"omnibuf: WireArray encode depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                if (f.Type == FieldDescriptorProto.Types.Type.Message && !WrapperTypes.ContainsKey(f.TypeName))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    int arrMin_{fnum} = 2;");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    for (int i = 0; i < this.{prop}.Count; i++) arrMin_{fnum} += 2 + (this.{prop}[i]?.EstimateOmniBufMinSize(0) ?? 0);");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    encoder.BeginArrayField({fnum}u, OmniBufWireType.{elemWt}, this.{prop}.Count, arrMin_{fnum});");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    encoder.BeginArrayField({fnum}u, OmniBufWireType.{elemWt}, this.{prop}.Count);");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    for (int i = 0; i < this.{prop}.Count; i++)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                if (f.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (depth + 2 > OmniBufConstants.MaxNestingDepth)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            throw new OmniBufException($\"omnibuf: encode nesting depth {{depth + 2}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                    if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? rWrap))
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.BeginContainer();");
                        if (rWrap.IsValueType)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (this.{prop}[i].HasValue)");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            OmniBufWrappers.Write{rWrap.MethodSuffix}Body(encoder, this.{prop}[i]!.Value);");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                        }
                        else
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (this.{prop}[i] is not null)");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            OmniBufWrappers.Write{rWrap.MethodSuffix}Body(encoder, this.{prop}[i]);");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                        }
                    }
                    else
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.BeginContainer(this.{prop}[i]?.EstimateOmniBufMinSize(0) ?? 0);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        this.{prop}[i]?.EncodeOmniBufBody(encoder, deterministic, depth + 2);");
                    }

                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.EndContainer();");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {EmitWriteRawValue("encoder", f, $"this.{prop}[i]")};");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    encoder.EndArray();");
                continue;
            }

            if (IsRealOneof(f))
            {
                string oneofPascal = ToPascalCase(msg.OneofDecl[f.OneofIndex].Name);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (this.{oneofPascal}Case == {oneofPascal}OneofCase.{prop})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                if (f.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            throw new OmniBufException($\"omnibuf: encode nesting depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.BeginSubMsgField({fnum}u);");
                    if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? oWrap))
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (this.{prop} is {{ }} ow_{fnum})");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            OmniBufWrappers.Write{oWrap.MethodSuffix}Body(encoder, ow_{fnum});");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                    }
                    else
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        this.{prop}?.EncodeOmniBufBody(encoder, deterministic, depth + 1);");
                    }

                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.EndSubMsg();");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {EmitWriteField("encoder", fnum, f, $"this.{prop}")};");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                continue;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? sWrap))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (this.{prop} is {{ }} sw_{fnum})");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            throw new OmniBufException($\"omnibuf: encode nesting depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.BeginSubMsgField({fnum}u);");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        OmniBufWrappers.Write{sWrap.MethodSuffix}Body(encoder, sw_{fnum});");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.EndSubMsg();");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (this.{prop} is {{ }} msg_{fnum})");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            throw new OmniBufException($\"omnibuf: encode nesting depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.BeginSubMsgField({fnum}u, msg_{fnum}.EstimateOmniBufMinSize(0));");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        msg_{fnum}.EncodeOmniBufBody(encoder, deterministic, depth + 1);");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.EndSubMsg();");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                }

                continue;
            }

            if (HasExplicitScalarPresence(f, isProto2))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (this.Has{prop})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {EmitWriteField("encoder", fnum, f, $"this.{prop}")};");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {EmitWriteField("encoder", fnum, f, $"this.{prop}")};");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        sb.AppendLine();
    }

    private static void EmitMapValEncode(StringBuilder sb, string indent, FieldDescriptorProto valField, string expr, string childDepthExpr)
    {
        if (valField.Type == FieldDescriptorProto.Types.Type.Message)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if ({childDepthExpr} > OmniBufConstants.MaxNestingDepth)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    throw new OmniBufException($\"omnibuf: encode nesting depth {{{childDepthExpr}}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}encoder.BeginContainer();");
            if (WrapperTypes.TryGetValue(valField.TypeName, out WrapperInfo? mWrap))
            {
                if (mWrap.IsValueType)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if ({expr}.HasValue)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    OmniBufWrappers.Write{mWrap.MethodSuffix}Body(encoder, {expr}.Value);");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if ({expr} is not null)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    OmniBufWrappers.Write{mWrap.MethodSuffix}Body(encoder, {expr});");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                }
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{expr}?.EncodeOmniBufBody(encoder, deterministic, {childDepthExpr});");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}encoder.EndContainer();");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{EmitWriteRawValue("encoder", valField, expr)};");
        }
    }

    private static bool CanEmitFastSequential(DescriptorProto msg, Dictionary<string, TypeSymbol> symbols)
    {
        if (msg.Field.Count == 0 || msg.Field.Count > 16)
        {
            return false;
        }

        if (GetRealOneofs(msg).Count > 0)
        {
            return false;
        }

        foreach (FieldDescriptorProto f in msg.Field)
        {
            if (f.Number >= 64 || f.Number > 1023 || IsMapField(f, symbols, out _, out _))
            {
                return false;
            }
        }

        return true;
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
        bool emitFastSequential = CanEmitFastSequential(msg, symbols);

        if (emitFastSequential)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}private readonly struct OmniBufFreshDecoder : IOmniBufFreshDecoder<{msgSym.CSharpFullName}>");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public {msgSym.CSharpFullName} Decode(OmniBufSpanReader rd)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        var msg = new {msgSym.CSharpFullName}();");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        msg.DecodeOmniBufFresh(rd, 0, OmniBufConstants.MaxNestingDepth);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return msg;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public static {msgSym.CSharpFullName} ParseOmniBuf(ReadOnlySpan<byte> data)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (data.IsEmpty)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return new {msgSym.CSharpFullName}();");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    return OmniBufSpanReader.ParsePinned<{msgSym.CSharpFullName}, OmniBufFreshDecoder>(data);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public static {msgSym.CSharpFullName} ParseOmniBuf(ReadOnlySpan<byte> data)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    var msg = new {msgSym.CSharpFullName}();");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    msg.DecodeOmniBuf(data, 0, OmniBufConstants.MaxNestingDepth, merge: false);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    return msg;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public void UnmarshalOmniBuf(ReadOnlySpan<byte> data)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    DecodeOmniBuf(data, 0, OmniBufConstants.MaxNestingDepth, merge: false);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        sb.AppendLine();

        if (emitFastSequential)
        {
            bool needsSeenLow = fields.Any(f => f.Label == FieldDescriptorProto.Types.Label.Repeated || f.Type == FieldDescriptorProto.Types.Type.Message);
            bool isSmallLeaf = !needsSeenLow && fields.Count <= 8;
            if (isSmallLeaf)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}internal void DecodeOmniBufFresh(OmniBufSpanReader rd, int depth, int maxDepth)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (depth > maxDepth)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        OmniBufDecoder.ThrowMaxDepthExceeded(depth, maxDepth);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            if (needsSeenLow)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    ulong seenLow = 0;");
            }
            foreach (FieldDescriptorProto f in fields)
            {
                uint fnum = (uint)f.Number;
                string prop = GetPropertyName(f, msg.Name);
                if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
                {
                    ulong arrTag = ((ulong)fnum << 4) | 13UL;
                    string matchExpr = EmitTagMatchReaderExpr("rd", arrTag);
                    string elemWt = WireTypeEnumName(f.Type);
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if ({matchExpr})");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (depth + 1 > maxDepth) OmniBufDecoder.ThrowMaxDepthExceeded(depth + 1, maxDepth);");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        seenLow |= (1UL << {(int)fnum});");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        var body = rd.ReadContainer(2);");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (!body.TryParseTypedArrayHeader(OmniBufWireType.{elemWt}, out int count)) OmniBufDecoder.ThrowInvalidArrayHeader(\"{msg.Name}.{f.Name}\");");
                    if (f.Type == FieldDescriptorProto.Types.Type.Message)
                    {
                        if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? rWrap))
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (count > 0)");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            int targetCap = count <= 4096 ? count : 4096;");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (this.{prop}.Capacity < targetCap) this.{prop}.Capacity = targetCap;");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            for (int i = 0; i < count; i++)");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                ReadOnlySpan<byte> sub = body.ReadLenSpan(0);");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                this.{prop}.Add(OmniBufWrappers.Decode{rWrap.MethodSuffix}(sub, {rWrap.DefaultExpr}, depth + 2, maxDepth));");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                        }
                        else
                        {
                            TypeSymbol elemSym = symbols[f.TypeName];
                            string elemMsgCs = elemSym.CSharpFullName;
                            bool elemFast = elemSym.Message is not null && CanEmitFastSequential(elemSym.Message, symbols);
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (count > 0)");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (depth + 2 > maxDepth) OmniBufDecoder.ThrowMaxDepthExceeded(depth + 2, maxDepth);");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            int targetCap = count <= 4096 ? count : 4096;");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (this.{prop}.Capacity < targetCap) this.{prop}.Capacity = targetCap;");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            for (int i = 0; i < count; i++)");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                            if (elemFast)
                            {
                                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                var sub = body.ReadContainer(0);");
                                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                var elem = new {elemMsgCs}();");
                                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (!sub.IsAtEnd) elem.DecodeOmniBufFresh(sub, depth + 2, maxDepth);");
                                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                this.{prop}.Add(elem);");
                            }
                            else
                            {
                                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                ReadOnlySpan<byte> sub = body.ReadLenSpan(0);");
                                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                var elem = new {elemMsgCs}();");
                                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                elem.DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                this.{prop}.Add(elem);");
                            }

                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                        }
                    }
                    else if (f.Type == FieldDescriptorProto.Types.Type.String)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (count > 0) body.ReadStringArray(count, this.{prop});");
                    }
                    else if (f.Type == FieldDescriptorProto.Types.Type.Uint32)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (count > 0) body.ReadUint32Array(count, this.{prop});");
                    }
                    else
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (count > 0)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            int targetCap = count <= 4096 ? count : 4096;");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (this.{prop}.Capacity < targetCap) this.{prop}.Capacity = targetCap;");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            for (int i = 0; i < count; i++)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                this.{prop}.Add({EmitReaderReadRawValue("body", f, symbols)});");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                    }

                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (!body.IsAtEnd) OmniBufDecoder.ThrowTrailingArrayBytes();");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                }
                else if (f.Type == FieldDescriptorProto.Types.Type.Bool)
                {
                    ulong boolTrueTag = ((ulong)fnum << 4) | 3UL;
                    ulong boolFalseTag = ((ulong)fnum << 4) | 2UL;
                    string matchTrue = EmitTagMatchReaderExpr("rd", boolTrueTag);
                    string matchFalse = EmitTagMatchReaderExpr("rd", boolFalseTag);
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if ({matchTrue})");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        this.{prop} = true;");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    else if ({matchFalse})");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        this.{prop} = false;");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                }
                else if (f.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    ulong subMsgTag = ((ulong)fnum << 4) | 12UL;
                    string matchExpr = EmitTagMatchReaderExpr("rd", subMsgTag);
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if ({matchExpr})");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        seenLow |= (1UL << {(int)fnum});");
                    if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? sWrap))
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        ReadOnlySpan<byte> sub = rd.ReadLenSpan(0);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        this.{prop} = OmniBufWrappers.Decode{sWrap.MethodSuffix}(sub, {sWrap.DefaultExpr}, depth + 1, maxDepth);");
                    }
                    else
                    {
                        TypeSymbol subSym = symbols[f.TypeName];
                        string subCs = subSym.CSharpFullName;
                        bool subFast = subSym.Message is not null && CanEmitFastSequential(subSym.Message, symbols);
                        if (subFast)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (depth + 1 > maxDepth) OmniBufDecoder.ThrowMaxDepthExceeded(depth + 1, maxDepth);");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        var sub = rd.ReadContainer(0);");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        var subMsg = new {subCs}();");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        this.{prop} = subMsg;");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (!sub.IsAtEnd) subMsg.DecodeOmniBufFresh(sub, depth + 1, maxDepth);");
                        }
                        else
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        ReadOnlySpan<byte> sub = rd.ReadLenSpan(0);");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        var subMsg = new {subCs}();");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        this.{prop} = subMsg;");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        subMsg.DecodeOmniBuf(sub, depth + 1, maxDepth, merge: false);");
                        }
                    }

                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                }
                else
                {
                    ulong scalarTag = ((ulong)fnum << 4) | (ulong)WireTypeByte(f.Type);
                    string matchExpr = EmitTagMatchReaderExpr("rd", scalarTag);
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if ({matchExpr})");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        this.{prop} = {EmitReaderReadRawValue("rd", f, symbols)};");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                }
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (rd.IsAtEnd) return;");
            string seenArg = needsSeenLow ? "seenLow" : "0UL";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    DecodeOmniBufSlow(rd.RemainingSpan(), 0, {seenArg}, depth, maxDepth, merge: false, isFresh: true);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}internal void DecodeOmniBuf(ReadOnlySpan<byte> data, int depth, int maxDepth, bool merge)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (depth > maxDepth)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        OmniBufDecoder.ThrowMaxDepthExceeded(depth, maxDepth);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    DecodeOmniBufSlow(data, 0, 0UL, depth, maxDepth, merge, isFresh: false);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}private void DecodeOmniBufSlow(ReadOnlySpan<byte> data, int pos, ulong seenLow, int depth, int maxDepth, bool merge, bool isFresh)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}internal void DecodeOmniBuf(ReadOnlySpan<byte> data, int depth, int maxDepth, bool merge)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (depth > maxDepth)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        OmniBufDecoder.ThrowMaxDepthExceeded(depth, maxDepth);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");

            if (realOneofs.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (!merge)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                foreach ((_, string oneofPascal, _) in realOneofs)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        this.Clear{oneofPascal}();");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            }

            if (hasLowFields)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    ulong seenLow = 0;");
            }

            if (hasHighFields)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    HashSet<uint>? seenHigh = null;");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    int pos = 0;");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    while (pos < data.Length)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        ulong tag = OmniBufDecoder.ReadVarint(data, ref pos);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        switch (tag)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");

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

            if (IsMapField(f, symbols, out FieldDescriptorProto? keyField, out FieldDescriptorProto? valField))
            {
                string keyWt = WireTypeEnumName(keyField!.Type);
                string valWt = WireTypeEnumName(valField!.Type);
                ulong mapTag = ((ulong)fnum << 4) | 14UL;
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            case {mapTag}UL:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (depth + 1 > maxDepth)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    throw new OmniBufException($\"omnibuf: WireMap depth {{depth + 1}} exceeds maximum {{maxDepth}}\");");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                bool alreadySeen = {seenCheckExpr} || merge;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {markSeenStmt}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 3);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (!OmniBufDecoder.TryParseTypedMapHeader(body, OmniBufWireType.{keyWt}, OmniBufWireType.{valWt}, out int count, out int p))");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    throw new OmniBufException(\"omnibuf: invalid WireMap header on {msg.Name}.{f.Name}\");");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (!alreadySeen)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    this.{prop}.Clear();");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                for (int i = 0; i < count; i++)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    var k = {EmitReadRawValue("body", "p", keyField, symbols)};");
                if (valField.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);");
                    if (WrapperTypes.TryGetValue(valField.TypeName, out WrapperInfo? mWrap))
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    var v = OmniBufWrappers.Decode{mWrap.MethodSuffix}(sub, {mWrap.DefaultExpr}, depth + 2, maxDepth);");
                    }
                    else
                    {
                        string valMsgCs = symbols[valField.TypeName].CSharpFullName;
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    var v = new {valMsgCs}();");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    v.DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                    }
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    var v = {EmitReadRawValue("body", "p", valField, symbols)};");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    this.{prop}[k] = v;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (p != body.Length)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    throw new OmniBufException(\"omnibuf: trailing bytes in WireMap\");");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                break;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                continue;
            }

            if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
            {
                string elemWt = WireTypeEnumName(f.Type);
                ulong arrTag = ((ulong)fnum << 4) | 13UL;
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            case {arrTag}UL:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (depth + 1 > maxDepth)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    throw new OmniBufException($\"omnibuf: WireArray depth {{depth + 1}} exceeds maximum {{maxDepth}}\");");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                bool alreadySeen = {seenCheckExpr} || merge;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {markSeenStmt}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 2);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (!OmniBufDecoder.TryParseTypedArrayHeader(body, OmniBufWireType.{elemWt}, out int count, out int p))");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    throw new OmniBufException(\"omnibuf: invalid WireArray header on {msg.Name}.{f.Name}\");");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                if (f.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? rWrap))
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (!alreadySeen && this.{prop}.Count > 0)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    this.{prop}.Clear();");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (count > 0)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    int targetCap = this.{prop}.Count + (count <= 4096 ? count : 4096);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    if (this.{prop}.Capacity < targetCap) this.{prop}.Capacity = targetCap;");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    for (int i = 0; i < count; i++)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        this.{prop}.Add(OmniBufWrappers.Decode{rWrap.MethodSuffix}(sub, {rWrap.DefaultExpr}, depth + 2, maxDepth));");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                    }
                    else
                    {
                        string elemMsgCs = symbols[f.TypeName].CSharpFullName;
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                int prevCount = this.{prop}.Count;");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (!alreadySeen && prevCount > 0)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    for (int i = 0; i < count; i++)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        if (i < prevCount)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                            this.{prop}[i].DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        else");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                            var elem = new {elemMsgCs}();");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                            elem.DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                            this.{prop}.Add(elem);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    for (int r = prevCount - 1; r >= count; r--)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        this.{prop}.RemoveAt(r);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                else if (count > 0)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    int targetCap = prevCount + (count <= 4096 ? count : 4096);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    if (this.{prop}.Capacity < targetCap) this.{prop}.Capacity = targetCap;");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    for (int i = 0; i < count; i++)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        var elem = new {elemMsgCs}();");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        elem.DecodeOmniBuf(sub, depth + 2, maxDepth, merge: false);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        this.{prop}.Add(elem);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                    }
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (!alreadySeen && this.{prop}.Count > 0)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    this.{prop}.Clear();");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (count > 0)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    int targetCap = this.{prop}.Count + (count <= 4096 ? count : 4096);");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    if (this.{prop}.Capacity < targetCap) this.{prop}.Capacity = targetCap;");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    for (int i = 0; i < count; i++)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                        this.{prop}.Add({EmitReadRawValue("body", "p", f, symbols)});");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    }}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (p != body.Length)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    throw new OmniBufException(\"omnibuf: trailing bytes in WireArray\");");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                break;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                continue;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Bool)
            {
                ulong boolFalseTag = ((ulong)fnum << 4) | 2UL;
                ulong boolTrueTag = ((ulong)fnum << 4) | 3UL;
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            case {boolFalseTag}UL:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {markSeenStmt}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                this.{prop} = false;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                break;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            case {boolTrueTag}UL:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {markSeenStmt}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                this.{prop} = true;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                break;");
                continue;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                ulong subMsgTag = ((ulong)fnum << 4) | 12UL;
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            case {subMsgTag}UL:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                bool alreadySeen = {seenCheckExpr} || merge;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {markSeenStmt}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);");
                if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? sWrap))
                {
                    if (IsRealOneof(f))
                    {
                        string oneofPascal = ToPascalCase(msg.OneofDecl[f.OneofIndex].Name);
                        string activeCheck = $"alreadySeen && this.{oneofPascal}Case == {oneofPascal}OneofCase.{prop}";
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {sWrap.ScalarCsType} prev = {activeCheck} && this.{prop} is {{ }} prevVal_{fnum} ? prevVal_{fnum} : {sWrap.DefaultExpr};");
                    }
                    else
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {sWrap.ScalarCsType} prev = alreadySeen && this.{prop} is {{ }} prevVal_{fnum} ? prevVal_{fnum} : {sWrap.DefaultExpr};");
                    }

                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                this.{prop} = OmniBufWrappers.Decode{sWrap.MethodSuffix}(sub, prev, depth + 1, maxDepth);");
                }
                else
                {
                    string subCs = symbols[f.TypeName].CSharpFullName;
                    if (IsRealOneof(f))
                    {
                        string oneofPascal = ToPascalCase(msg.OneofDecl[f.OneofIndex].Name);
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (this.{oneofPascal}Case != {oneofPascal}OneofCase.{prop} || this.{prop} is not {{ }} existing_{fnum})");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    var subMsg_{fnum} = new {subCs}();");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    this.{prop} = subMsg_{fnum};");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    subMsg_{fnum}.DecodeOmniBuf(sub, depth + 1, maxDepth, merge: false);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                else");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    existing_{fnum}.DecodeOmniBuf(sub, depth + 1, maxDepth, merge: alreadySeen);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                    }
                    else
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                var subMsg_{fnum} = this.{prop};");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (subMsg_{fnum} is null)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    subMsg_{fnum} = new {subCs}();");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    this.{prop} = subMsg_{fnum};");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                subMsg_{fnum}.DecodeOmniBuf(sub, depth + 1, maxDepth, merge: alreadySeen);");
                    }
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                break;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                continue;
            }

            ulong scalarTag = ((ulong)fnum << 4) | (ulong)WireTypeByte(f.Type);
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            case {scalarTag}UL:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {markSeenStmt}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                this.{prop} = {EmitReadRawValue("data", "pos", f, symbols)};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                break;");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            default:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                OmniBufDecoder.SkipUnknownOrField0Tag(data, ref pos, tag, depth, maxDepth);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                break;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");

        List<FieldDescriptorProto> nonOneofFields = fields.Where(f => !IsRealOneof(f)).ToList();
        if (nonOneofFields.Count > 0)
        {
            ulong expectedLowMask = 0;
            bool hasNonOneofHigh = false;
            foreach (FieldDescriptorProto f in nonOneofFields)
            {
                if (f.Number < 64)
                {
                    expectedLowMask |= 1UL << f.Number;
                }
                else
                {
                    hasNonOneofHigh = true;
                }
            }

            string resetCond = emitFastSequential
                ? $"!merge && !isFresh && seenLow != 0x{expectedLowMask:X}UL"
                : (hasNonOneofHigh
                    ? "!merge"
                    : $"!merge && seenLow != 0x{expectedLowMask:X}UL");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if ({resetCond})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            foreach (FieldDescriptorProto f in nonOneofFields)
            {
                uint fnum = (uint)f.Number;
                string prop = GetPropertyName(f, msg.Name);
                string notSeen = fnum < 64
                    ? $"((seenLow & (1UL << {(int)fnum})) == 0)"
                    : $"(seenHigh is null || !seenHigh.Contains({fnum}u))";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if ({notSeen})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                if (IsMapField(f, symbols, out _, out _) || f.Label == FieldDescriptorProto.Types.Label.Repeated)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            this.{prop}.Clear();");
                }
                else if (f.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            this.{prop} = null;");
                }
                else if (HasExplicitScalarPresence(f, isProto2))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            this.Clear{prop}();");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            this.{prop} = {DefaultValueLiteral(f, symbols)};");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
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
        string spanViewName = $"{msg.Name}SpanView";
        List<(int Index, string PascalName, List<FieldDescriptorProto> Fields)> realOneofs = GetRealOneofs(msg);
        int maxField = fields.Count == 0 ? 0 : fields.Max(f => f.Number);
        int maxIndexedField = Math.Min(maxField, 64);

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public sealed class {opaqueName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private const int MaxIndexedField = {maxIndexedField};");
        foreach ((_, string oneofPascal, List<FieldDescriptorProto> members) in realOneofs)
        {
            string fNums = string.Join(", ", members.Select(m => $"{(uint)m.Number}u"));
            string fMasks = string.Join(", ", members.Select(OneofMemberWireMaskLiteral));
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private static readonly uint[] s_oneof_{oneofPascal}_fields = new uint[] {{ {fNums} }};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private static readonly ushort[] s_oneof_{oneofPascal}_masks = new ushort[] {{ {fMasks} }};");
        }

        if (fields.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private sealed class DirtyState");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        public HashSet<uint>? Overflow;");
            foreach (FieldDescriptorProto f in fields)
            {
                string prop = GetPropertyName(f, msg.Name);
                string fieldStorageType = OpaqueStorageType(f, symbols);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        public {fieldStorageType} f_{prop};");
                if (HasExplicitScalarPresence(f, isProto2) || IsRealOneof(f))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        public bool present_{prop};");
                }
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine();
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private readonly byte[] _raw;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private ulong _dirtyMaskLow;");
        if (fields.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private DirtyState? _dirty;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private long[]? _fieldIndex;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private byte _readCount;");
        }

        foreach (FieldDescriptorProto f in fields)
        {
            string prop = GetPropertyName(f, msg.Name);
            if (IsOpaqueSubMsgField(f, symbols))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private bool _hasCached_{prop};");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private {symbols[f.TypeName].CSharpOpaqueFullName}? _cached_{prop};");
            }
            else if (IsOpaqueSubMsgListField(f, symbols))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private List<{symbols[f.TypeName].CSharpOpaqueFullName}>? _cached_{prop};");
            }
            else if (IsOpaqueSubMsgMapField(f, symbols))
            {
                _ = IsMapField(f, symbols, out FieldDescriptorProto? kf, out FieldDescriptorProto? vf);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private Dictionary<{CSharpScalarType(kf!, symbols)}, {symbols[vf!.TypeName].CSharpOpaqueFullName}>? _cached_{prop};");
            }
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public {opaqueName}(byte[] raw)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _raw = raw ?? Array.Empty<byte>();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public {opaqueName}(ReadOnlySpan<byte> raw)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _raw = raw.ToArray();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public ReadOnlySpan<byte> RawSpan => _raw;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public {spanViewName} AsSpanView() => new(_raw);");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    internal bool HasDirtyFields");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        get");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            PropagateChildDirty();");
        if (fields.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            return _dirtyMaskLow != 0 || (_dirty?.Overflow is {{ Count: > 0 }});");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            return _dirtyMaskLow != 0;");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine();

        if (fields.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private DirtyState EnsureDirty() => _dirty ??= new DirtyState();");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private long[]? GetOrBuildIndex()");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        long[]? idx = System.Threading.Volatile.Read(ref _fieldIndex);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (idx is not null) return idx;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (System.Threading.Volatile.Read(ref _readCount) == 0) {{ System.Threading.Volatile.Write(ref _readCount, (byte)1); return null; }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        long[] built = OmniBufOpaque.BuildFieldIndex(_raw, MaxIndexedField);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        System.Threading.Volatile.Write(ref _fieldIndex, built);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return built;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private bool IsDirty(uint fieldNum) => fieldNum < 64");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        ? (_dirtyMaskLow & (1UL << (int)fieldNum)) != 0");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        : (_dirty?.Overflow is not null && _dirty.Overflow.Contains(fieldNum));");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private void MarkDirty(uint fieldNum)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (fieldNum < 64)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            _dirtyMaskLow |= (1UL << (int)fieldNum);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        else");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            (EnsureDirty().Overflow ??= new HashSet<uint>()).Add(fieldNum);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine();
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private void PropagateChildDirty()");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        foreach (FieldDescriptorProto f in fields)
        {
            uint fnum = (uint)f.Number;
            string prop = GetPropertyName(f, msg.Name);
            if (IsOpaqueSubMsgField(f, symbols))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (!IsDirty({fnum}u) && _hasCached_{prop} && _cached_{prop} is not null && _cached_{prop}.HasDirtyFields)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            DirtyState d = EnsureDirty();");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            d.f_{prop} = _cached_{prop};");
                if (IsRealOneof(f))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            d.present_{prop} = true;");
                    EmitClearOneofSiblings(sb, indent + "            ", msg, f);
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            MarkDirty({fnum}u);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
            }
            else if (IsOpaqueSubMsgListField(f, symbols))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (!IsDirty({fnum}u) && _cached_{prop} is not null)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            for (int i = 0; i < _cached_{prop}.Count; i++)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (_cached_{prop}[i].HasDirtyFields)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    EnsureDirty().f_{prop} = _cached_{prop};");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    MarkDirty({fnum}u);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    break;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
            }
            else if (IsOpaqueSubMsgMapField(f, symbols))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (!IsDirty({fnum}u) && _cached_{prop} is not null)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            foreach (var kv in _cached_{prop})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (kv.Value.HasDirtyFields)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    EnsureDirty().f_{prop} = _cached_{prop};");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    MarkDirty({fnum}u);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    break;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine();

        foreach ((_, string oneofPascal, List<FieldDescriptorProto> members) in realOneofs)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private uint GetActive{oneofPascal}Field()");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        PropagateChildDirty();");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (_dirty is {{ }} d)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
            foreach (FieldDescriptorProto mf in members)
            {
                uint mfNum = (uint)mf.Number;
                string mProp = GetPropertyName(mf, msg.Name);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (IsDirty({mfNum}u) && d.present_{mProp}) return {mfNum}u;");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        uint rawActive = OmniBufOpaque.LocateActiveOneof(_raw, s_oneof_{oneofPascal}_fields, s_oneof_{oneofPascal}_masks);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return rawActive == 0u || IsDirty(rawActive) ? 0u : rawActive;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public void Clear{oneofPascal}()");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        DirtyState d = EnsureDirty();");
            foreach (FieldDescriptorProto mf in members)
            {
                string mProp = GetPropertyName(mf, msg.Name);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        d.present_{mProp} = false;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        MarkDirty({(uint)mf.Number}u);");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine();
        }

        foreach (FieldDescriptorProto f in fields)
        {
            EmitOpaqueFieldMembers(sb, msg, f, isProto2, symbols, indent + "    ");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public void ResetDirty()");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _dirtyMaskLow = 0;");
        if (fields.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _dirty = null;");
        }

        foreach (FieldDescriptorProto f in fields)
        {
            string prop = GetPropertyName(f, msg.Name);
            if (IsOpaqueSubMsgField(f, symbols))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _hasCached_{prop} = false;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _cached_{prop} = null;");
            }
            else if (IsOpaqueSubMsgListField(f, symbols) || IsOpaqueSubMsgMapField(f, symbols))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _cached_{prop} = null;");
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine();

        string dirtyOverflowExpr = fields.Count > 0 ? "_dirty?.Overflow" : "null";

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    internal void SpliceBodyInto(OmniBufEncoder encoder, bool deterministic, bool validate, int depth)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (depth > OmniBufConstants.MaxNestingDepth)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            throw new OmniBufException($\"omnibuf: nesting depth {{depth}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        PropagateChildDirty();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (validate)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            OmniBufOpaque.SpliceFieldsValidatedTo(encoder, _raw, _dirtyMaskLow, {dirtyOverflowExpr}, (Self: this, Deterministic: deterministic, Depth: depth), static (st, e, fnum) => st.Self.AppendDirtyField(e, fnum, st.Deterministic, validate: true, st.Depth), stripField0: false, depth: depth, maxDepth: OmniBufConstants.MaxNestingDepth);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        else");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            OmniBufOpaque.SpliceFieldsTo(encoder, _raw, _dirtyMaskLow, {dirtyOverflowExpr}, (Self: this, Deterministic: deterministic, Depth: depth), static (st, e, fnum) => st.Self.AppendDirtyField(e, fnum, st.Deterministic, validate: false, st.Depth), stripField0: false, depth: depth, maxDepth: OmniBufConstants.MaxNestingDepth);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public void MarshalOmniBuf(IBufferWriter<byte> writer, MarshalOptions options = default)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        ArgumentNullException.ThrowIfNull(writer);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        OmniBufEncoder enc = OmniBufEncoder.RentThreadLocal(out bool rented);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        try");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            MarshalOmniBuf(enc, options);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            enc.CopyTo(writer);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        finally");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            OmniBufEncoder.ReturnThreadLocal(enc, rented);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public void MarshalOmniBuf(OmniBufEncoder encoder, MarshalOptions options = default)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        PropagateChildDirty();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (options.SelfDescribing)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            SchemaMeta? meta = {msgSym.CSharpFullName}.BuildOmniBufSchemaMeta(1, null, out bool cyclic);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (cyclic)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                byte[] body = OmniBufOpaque.SpliceFields(_raw, _dirtyMaskLow, {dirtyOverflowExpr}, (Self: this, Deterministic: options.Deterministic), static (st, enc, fnum) => st.Self.AppendDirtyField(enc, fnum, st.Deterministic, validate: false, 0), stripField0: true);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                meta = {msgSym.CSharpFullName}.ParseOmniBuf(body).BuildOmniBufValueSchemaMeta(1, null);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (meta is not null)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    SchemaMeta.WriteField0(encoder, meta, options.Deterministic);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                encoder.WriteRawBytes(body);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                return;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (meta is not null)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                SchemaMeta.WriteField0(encoder, meta, options.Deterministic);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        OmniBufOpaque.SpliceFieldsTo(encoder, _raw, _dirtyMaskLow, {dirtyOverflowExpr}, (Self: this, Deterministic: options.Deterministic), static (st, enc, fnum) => st.Self.AppendDirtyField(enc, fnum, st.Deterministic, validate: false, 0), stripField0: options.SelfDescribing);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public byte[] MarshalOmniBuf(MarshalOptions options = default)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        PropagateChildDirty();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (!options.SelfDescribing)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            return (_dirtyMaskLow == 0 && ({dirtyOverflowExpr} is null || {dirtyOverflowExpr}.Count == 0))");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                ? (byte[])_raw.Clone()");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                : OmniBufOpaque.SpliceFields(_raw, _dirtyMaskLow, {dirtyOverflowExpr}, (Self: this, Deterministic: options.Deterministic), static (st, enc, fnum) => st.Self.AppendDirtyField(enc, fnum, st.Deterministic, validate: false, 0));");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        OmniBufEncoder enc = OmniBufEncoder.RentThreadLocal(out bool rented);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        try");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            MarshalOmniBuf(enc, options);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            return enc.ToArray();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        finally");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            OmniBufEncoder.ReturnThreadLocal(enc, rented);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public byte[] MarshalValidated(MarshalOptions options = default)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        PropagateChildDirty();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        OmniBufEncoder enc = OmniBufEncoder.RentThreadLocal(out bool rented);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        try");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (!options.SelfDescribing)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                SpliceBodyInto(enc, options.Deterministic, validate: true, depth: 0);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                return enc.ToArray();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            SchemaMeta? meta = {msgSym.CSharpFullName}.BuildOmniBufSchemaMeta(1, null, out bool cyclic);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            if (cyclic)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                OmniBufOpaque.SpliceFieldsValidatedTo(enc, _raw, _dirtyMaskLow, {dirtyOverflowExpr}, (Self: this, Deterministic: options.Deterministic), static (st, e, fnum) => st.Self.AppendDirtyField(e, fnum, st.Deterministic, validate: true, 0), stripField0: true, depth: 0, maxDepth: OmniBufConstants.MaxNestingDepth);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                byte[] body = enc.ToArray();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                enc.Reset();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                meta = {msgSym.CSharpFullName}.ParseOmniBuf(body).BuildOmniBufValueSchemaMeta(1, null);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (meta is not null)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    SchemaMeta.WriteField0(enc, meta, options.Deterministic);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                enc.WriteRawBytes(body);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            else");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                if (meta is not null)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                    SchemaMeta.WriteField0(enc, meta, options.Deterministic);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                OmniBufOpaque.SpliceFieldsValidatedTo(enc, _raw, _dirtyMaskLow, {dirtyOverflowExpr}, (Self: this, Deterministic: options.Deterministic), static (st, e, fnum) => st.Self.AppendDirtyField(e, fnum, st.Deterministic, validate: true, 0), stripField0: true, depth: 0, maxDepth: OmniBufConstants.MaxNestingDepth);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            return enc.ToArray();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        finally");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            OmniBufEncoder.ReturnThreadLocal(enc, rented);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public bool TryMarshal(out byte[] packet, out string? error, MarshalOptions options = default)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        try");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            packet = MarshalValidated(options);");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            error = null;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            return true;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        catch (OmniBufException ex)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            packet = Array.Empty<byte>();");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            error = ex.Message;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            return false;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private void AppendDirtyField(OmniBufEncoder encoder, uint fieldNum, bool deterministic, bool validate, int depth)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _ = deterministic;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _ = validate;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _ = depth;");
        if (fields.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (_dirty is not {{ }} d) return;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        switch (fieldNum)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
            foreach (FieldDescriptorProto f in fields)
            {
                uint fnum = (uint)f.Number;
                string prop = GetPropertyName(f, msg.Name);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            case {fnum}u:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                EmitOpaqueAppendDirtyCase(sb, indent + "                ", fnum, prop, f, isProto2, symbols);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                break;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _ = encoder;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _ = fieldNum;");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        sb.AppendLine();
    }

    private static string OneofMemberWireMaskLiteral(FieldDescriptorProto f) => f.Type == FieldDescriptorProto.Types.Type.Bool
        ? "(ushort)((1 << (byte)OmniBufWireType.BoolFalse) | (1 << (byte)OmniBufWireType.BoolTrue))"
        : $"(ushort)(1 << (byte)OmniBufWireType.{WireTypeEnumName(f.Type)})";

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
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}d.present_{sibProp} = false;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}MarkDirty({(uint)sibling.Number}u);");
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
        string? oneofPascal = IsRealOneof(f) ? ToPascalCase(msg.OneofDecl[f.OneofIndex].Name) : null;

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

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public IReadOnlyDictionary<{keyCs}, {valOpaque}> {prop}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (IsDirty({fnum}u))");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            return _dirty?.f_{prop} ?? new Dictionary<{keyCs}, {valOpaque}>({emptyDictCtorArgs});");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _cached_{prop} ??= OmniBufOpaque.ReadMap(_raw, {fnum}u, OmniBufWireType.{keyWt}, OmniBufWireType.{valWt}, {keyReader}, {valReader}, {comparerArg});");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return _cached_{prop};");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public void Set{prop}(IDictionary<{keyCs}, {valMsg}?>? value)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    var dict = new Dictionary<{keyCs}, {valOpaque}>({(keyField.Type == FieldDescriptorProto.Types.Type.String ? "(value?.Count ?? 0), StringComparer.Ordinal" : "(value?.Count ?? 0)")});");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (value is not null)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        foreach (var kv in value)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            dict[kv.Key] = kv.Value is null ? new {valOpaque}(Array.Empty<byte>()) : new {valOpaque}(kv.Value.MarshalOmniBuf());");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    EnsureDirty().f_{prop} = dict;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    MarkDirty({fnum}u);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public void Set{prop}(IDictionary<{keyCs}, {valOpaque}>? value)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    EnsureDirty().f_{prop} = value is null ? new Dictionary<{keyCs}, {valOpaque}>({emptyDictCtorArgs}) : new Dictionary<{keyCs}, {valOpaque}>({dictCtorArgs});");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    MarkDirty({fnum}u);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                sb.AppendLine();
                return;
            }

            string valCs = OpaqueCollectionElemType(valField, symbols);
            string valScalarReader = OpaqueElemReaderExpr(valField, symbols);
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public IReadOnlyDictionary<{keyCs}, {valCs}> {prop}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get => IsDirty({fnum}u)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        ? (_dirty?.f_{prop} ?? new Dictionary<{keyCs}, {valCs}>({emptyDictCtorArgs}))");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        : OmniBufOpaque.ReadMap(_raw, {fnum}u, OmniBufWireType.{keyWt}, OmniBufWireType.{valWt}, {keyReader}, {valScalarReader}, {comparerArg});");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public void Set{prop}(IDictionary<{keyCs}, {valCs}>? value)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    EnsureDirty().f_{prop} = value is null ? new Dictionary<{keyCs}, {valCs}>({emptyDictCtorArgs}) : new Dictionary<{keyCs}, {valCs}>({dictCtorArgs});");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    MarkDirty({fnum}u);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine();
            return;
        }

        if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
        {
            if (f.Type == FieldDescriptorProto.Types.Type.Message && !WrapperTypes.ContainsKey(f.TypeName))
            {
                TypeSymbol sym = symbols[f.TypeName];
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public IReadOnlyList<{sym.CSharpOpaqueFullName}> {prop}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (IsDirty({fnum}u))");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            return (IReadOnlyList<{sym.CSharpOpaqueFullName}>?)_dirty?.f_{prop} ?? Array.Empty<{sym.CSharpOpaqueFullName}>();");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (_cached_{prop} is null)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            List<byte[]> payloads = OmniBufOpaque.ReadSubMsgArrayPayloads(_raw, {fnum}u);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            var list = new List<{sym.CSharpOpaqueFullName}>(payloads.Count);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            for (int i = 0; i < payloads.Count; i++)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}                list.Add(new {sym.CSharpOpaqueFullName}(payloads[i]));");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            _cached_{prop} = list;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return _cached_{prop};");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public void Set{prop}(IEnumerable<{sym.CSharpFullName}?>? values)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    var list = new List<{sym.CSharpOpaqueFullName}>();");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (values is not null)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        foreach (var item in values)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            list.Add(item is null ? new {sym.CSharpOpaqueFullName}(Array.Empty<byte>()) : new {sym.CSharpOpaqueFullName}(item.MarshalOmniBuf()));");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    EnsureDirty().f_{prop} = list;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    MarkDirty({fnum}u);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                sb.AppendLine();
                return;
            }

            string elemCs = OpaqueCollectionElemType(f, symbols);
            string elemWt = WireTypeEnumName(f.Type);
            string elemReader = OpaqueElemReaderExpr(f, symbols);
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public IReadOnlyList<{elemCs}> {prop}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get => IsDirty({fnum}u)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        ? ((IReadOnlyList<{elemCs}>?)_dirty?.f_{prop} ?? Array.Empty<{elemCs}>())");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        : OmniBufOpaque.ReadArray(_raw, {fnum}u, OmniBufWireType.{elemWt}, {elemReader});");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public void Set{prop}(IEnumerable<{elemCs}>? values)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    EnsureDirty().f_{prop} = values is null ? new List<{elemCs}>() : new List<{elemCs}>(values);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    MarkDirty({fnum}u);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine();
            return;
        }

        if (f.Type == FieldDescriptorProto.Types.Type.Message)
        {
            if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? wrap))
            {
                string propType = wrap.IsValueType ? wrap.ElemCsType : (wrap.ElemCsType + "?");
                string readWrap = $"OmniBufOpaque.TryReadSubMsgBytes(_raw, {fnum}u, out byte[] sub) ? ({propType})OmniBufWrappers.Decode{wrap.MethodSuffix}(sub, {wrap.DefaultExpr}, 1, OmniBufConstants.MaxNestingDepth) : null";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public {propType} {prop}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                if (oneofPascal is not null)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get => GetActive{oneofPascal}Field() != {fnum}u ? null : (IsDirty({fnum}u) ? _dirty!.f_{prop} : ({readWrap}));");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    set");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        DirtyState d = EnsureDirty();");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        d.f_{prop} = value;");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        d.present_{prop} = value is not null;");
                    EmitClearOneofSiblings(sb, indent + "        ", msg, f);
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        MarkDirty({fnum}u);");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get => IsDirty({fnum}u) ? _dirty!.f_{prop} : ({readWrap});");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    set");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        EnsureDirty().f_{prop} = value;");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        MarkDirty({fnum}u);");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                sb.AppendLine();
                return;
            }

            TypeSymbol sym = symbols[f.TypeName];
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public {sym.CSharpOpaqueFullName}? {prop}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            if (oneofPascal is not null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (GetActive{oneofPascal}Field() != {fnum}u) return null;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (IsDirty({fnum}u)) return _dirty!.f_{prop};");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (IsDirty({fnum}u)) return _dirty!.f_{prop};");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (!_hasCached_{prop})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            _cached_{prop} = OmniBufOpaque.TryReadSubMsgBytes(_raw, {fnum}u, out byte[] sub) ? new {sym.CSharpOpaqueFullName}(sub) : null;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            _hasCached_{prop} = true;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return _cached_{prop};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    set");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        DirtyState d = EnsureDirty();");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        d.f_{prop} = value;");
            if (oneofPascal is not null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        d.present_{prop} = value is not null;");
                EmitClearOneofSiblings(sb, indent + "        ", msg, f);
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        MarkDirty({fnum}u);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public void Set{prop}({sym.CSharpFullName}? value)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    this.{prop} = value is null ? null : new {sym.CSharpOpaqueFullName}(value.MarshalOmniBuf());");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");

            if (sym.Message is not null)
            {
                foreach (FieldDescriptorProto cf in sym.Message.Field)
                {
                    if (cf.Label != FieldDescriptorProto.Types.Label.Repeated && cf.Type == FieldDescriptorProto.Types.Type.String)
                    {
                        string childProp = GetPropertyName(cf, sym.Message.Name);
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public string Get{prop}{childProp}()");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    PropagateChildDirty();");
                        if (oneofPascal is not null)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (GetActive{oneofPascal}Field() != {fnum}u) return string.Empty;");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (IsDirty({fnum}u)) return _dirty!.f_{prop}?.{childProp} ?? string.Empty;");
                        }
                        else
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (IsDirty({fnum}u)) return _dirty!.f_{prop}?.{childProp} ?? string.Empty;");
                        }

                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    return OmniBufOpaque.TryReadNestedString(_raw, {fnum}u, {(uint)cf.Number}u, out string s) ? s : string.Empty;");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                    }
                }
            }

            sb.AppendLine();
            return;
        }

        bool hasPresence = HasExplicitScalarPresence(f, isProto2) || oneofPascal is not null;

        if (f.Type == FieldDescriptorProto.Types.Type.Bytes)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public byte[] {prop}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            if (oneofPascal is not null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get => GetActive{oneofPascal}Field() != {fnum}u ? Array.Empty<byte>() : (IsDirty({fnum}u) ? (_dirty!.f_{prop} ?? Array.Empty<byte>()) : (GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadBytesIndexed(idx, _raw, {fnum}u, out byte[] bi) ? bi : Array.Empty<byte>()) : (OmniBufOpaque.TryReadBytes(_raw, {fnum}u, out byte[] b) ? b : Array.Empty<byte>())));");
            }
            else if (hasPresence)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get => IsDirty({fnum}u) ? (_dirty!.present_{prop} ? (_dirty.f_{prop} ?? Array.Empty<byte>()) : Array.Empty<byte>()) : (GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadBytesIndexed(idx, _raw, {fnum}u, out byte[] bi) ? bi : Array.Empty<byte>()) : (OmniBufOpaque.TryReadBytes(_raw, {fnum}u, out byte[] b) ? b : Array.Empty<byte>()));");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get => IsDirty({fnum}u) ? (_dirty!.f_{prop} ?? Array.Empty<byte>()) : (GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadBytesIndexed(idx, _raw, {fnum}u, out byte[] bi) ? bi : Array.Empty<byte>()) : (OmniBufOpaque.TryReadBytes(_raw, {fnum}u, out byte[] b) ? b : Array.Empty<byte>()));");
            }

            if (hasPresence)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    set");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        DirtyState d = EnsureDirty();");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        d.f_{prop} = value ?? Array.Empty<byte>();");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        d.present_{prop} = true;");
                if (oneofPascal is not null)
                {
                    EmitClearOneofSiblings(sb, indent + "        ", msg, f);
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        MarkDirty({fnum}u);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    set {{ EnsureDirty().f_{prop} = value ?? Array.Empty<byte>(); MarkDirty({fnum}u); }}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public ReadOnlySpan<byte> Get{prop}Span() => {(oneofPascal is not null ? $"GetActive{oneofPascal}Field() != {fnum}u ? ReadOnlySpan<byte>.Empty : " : string.Empty)}(IsDirty({fnum}u) ? ({(hasPresence && oneofPascal is null ? $"_dirty!.present_{prop} ? (_dirty.f_{prop} ?? Array.Empty<byte>()) : Array.Empty<byte>()" : $"_dirty!.f_{prop} ?? Array.Empty<byte>()")}) : (OmniBufOpaque.TryReadBytesSpan(_raw, {fnum}u, out ReadOnlySpan<byte> sp) ? sp : ReadOnlySpan<byte>.Empty));");
            if (hasPresence)
            {
                EmitOpaquePresenceHelpers(sb, indent, fnum, prop, f, oneofPascal);
            }

            sb.AppendLine();
            return;
        }

        string csType = CSharpScalarType(f, symbols);
        string readExpr = EmitOpaqueScalarRead(fnum, f, symbols);
        string defaultExpr = f.Type == FieldDescriptorProto.Types.Type.String ? "string.Empty" : DefaultValueLiteral(f, symbols);
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public {csType} {prop}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
        if (oneofPascal is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get => GetActive{oneofPascal}Field() != {fnum}u ? {defaultExpr} : (IsDirty({fnum}u) ? _dirty!.f_{prop}! : ({readExpr}));");
        }
        else if (hasPresence)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get => IsDirty({fnum}u) ? (_dirty!.present_{prop} ? _dirty.f_{prop}! : {defaultExpr}) : ({readExpr});");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    get => IsDirty({fnum}u) ? _dirty!.f_{prop}! : ({readExpr});");
        }

        if (hasPresence)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    set");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        DirtyState d = EnsureDirty();");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        d.f_{prop} = {(f.Type == FieldDescriptorProto.Types.Type.String ? "value ?? string.Empty" : "value")};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        d.present_{prop} = true;");
            if (oneofPascal is not null)
            {
                EmitClearOneofSiblings(sb, indent + "        ", msg, f);
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        MarkDirty({fnum}u);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    set {{ EnsureDirty().f_{prop} = {(f.Type == FieldDescriptorProto.Types.Type.String ? "value ?? string.Empty" : "value")}; MarkDirty({fnum}u); }}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        if (hasPresence)
        {
            EmitOpaquePresenceHelpers(sb, indent, fnum, prop, f, oneofPascal);
        }

        sb.AppendLine();
    }

    private static void EmitOpaquePresenceHelpers(
        StringBuilder sb,
        string indent,
        uint fnum,
        string prop,
        FieldDescriptorProto f,
        string? oneofPascal)
    {
        if (oneofPascal is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public bool Has{prop} => GetActive{oneofPascal}Field() == {fnum}u;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public void Clear{prop}()");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (GetActive{oneofPascal}Field() == {fnum}u)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        Clear{oneofPascal}();");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        }
        else
        {
            string maskExpr = OneofMemberWireMaskLiteral(f);
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public bool Has{prop} => IsDirty({fnum}u) ? _dirty!.present_{prop} : OmniBufOpaque.LocateFieldMask(_raw, {fnum}u, {maskExpr}, out _, out _);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public void Clear{prop}()");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    EnsureDirty().present_{prop} = false;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    MarkDirty({fnum}u);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        }
    }

    private static string EmitOpaqueScalarRead(uint fnum, FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols) => f.Type switch
    {
        FieldDescriptorProto.Types.Type.Uint32 => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadUintIndexed(idx, _raw, {fnum}u, out ulong vi) ? (uint)vi : 0u) : (OmniBufOpaque.TryReadUint(_raw, {fnum}u, out ulong v) ? (uint)v : 0u)",
        FieldDescriptorProto.Types.Type.Uint64 => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadUintIndexed(idx, _raw, {fnum}u, out ulong vi) ? vi : 0UL) : (OmniBufOpaque.TryReadUint(_raw, {fnum}u, out ulong v) ? v : 0UL)",
        FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32 => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadSintIndexed(idx, _raw, {fnum}u, out long vi) ? (int)vi : 0) : (OmniBufOpaque.TryReadSint(_raw, {fnum}u, out long v) ? (int)v : 0)",
        FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadSintIndexed(idx, _raw, {fnum}u, out long vi) ? vi : 0L) : (OmniBufOpaque.TryReadSint(_raw, {fnum}u, out long v) ? v : 0L)",
        FieldDescriptorProto.Types.Type.Enum => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadSintIndexed(idx, _raw, {fnum}u, out long vi) ? ({symbols[f.TypeName].CSharpFullName})(int)vi : default) : (OmniBufOpaque.TryReadSint(_raw, {fnum}u, out long v) ? ({symbols[f.TypeName].CSharpFullName})(int)v : default)",
        FieldDescriptorProto.Types.Type.Bool => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadBoolIndexed(idx, _raw, {fnum}u, out bool vi) && vi) : (OmniBufOpaque.TryReadBool(_raw, {fnum}u, out bool v) && v)",
        FieldDescriptorProto.Types.Type.Fixed32 => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadFixed32Indexed(idx, _raw, {fnum}u, OmniBufWireType.Fixed32, out uint vi) ? vi : 0u) : (OmniBufOpaque.TryReadFixed32(_raw, {fnum}u, OmniBufWireType.Fixed32, out uint v) ? v : 0u)",
        FieldDescriptorProto.Types.Type.Sfixed32 => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadFixed32Indexed(idx, _raw, {fnum}u, OmniBufWireType.Sfixed32, out uint vi) ? (int)vi : 0) : (OmniBufOpaque.TryReadFixed32(_raw, {fnum}u, OmniBufWireType.Sfixed32, out uint v) ? (int)v : 0)",
        FieldDescriptorProto.Types.Type.Float => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadFixed32Indexed(idx, _raw, {fnum}u, OmniBufWireType.Float32, out uint vi) ? BitConverter.UInt32BitsToSingle(vi) : 0f) : (OmniBufOpaque.TryReadFixed32(_raw, {fnum}u, OmniBufWireType.Float32, out uint v) ? BitConverter.UInt32BitsToSingle(v) : 0f)",
        FieldDescriptorProto.Types.Type.Fixed64 => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadFixed64Indexed(idx, _raw, {fnum}u, OmniBufWireType.Fixed64, out ulong vi) ? vi : 0UL) : (OmniBufOpaque.TryReadFixed64(_raw, {fnum}u, OmniBufWireType.Fixed64, out ulong v) ? v : 0UL)",
        FieldDescriptorProto.Types.Type.Sfixed64 => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadFixed64Indexed(idx, _raw, {fnum}u, OmniBufWireType.Sfixed64, out ulong vi) ? (long)vi : 0L) : (OmniBufOpaque.TryReadFixed64(_raw, {fnum}u, OmniBufWireType.Sfixed64, out ulong v) ? (long)v : 0L)",
        FieldDescriptorProto.Types.Type.Double => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadFixed64Indexed(idx, _raw, {fnum}u, OmniBufWireType.Float64, out ulong vi) ? BitConverter.UInt64BitsToDouble(vi) : 0d) : (OmniBufOpaque.TryReadFixed64(_raw, {fnum}u, OmniBufWireType.Float64, out ulong v) ? BitConverter.UInt64BitsToDouble(v) : 0d)",
        FieldDescriptorProto.Types.Type.String => $"GetOrBuildIndex() is {{ }} idx ? (OmniBufOpaque.TryReadStringIndexed(idx, _raw, {fnum}u, out string vi) ? vi : string.Empty) : (OmniBufOpaque.TryReadString(_raw, {fnum}u, out string v) ? v : string.Empty)",
        _ => "default!",
    };

    private static void EmitSpanViewStruct(
        StringBuilder sb,
        TypeSymbol msgSym,
        DescriptorProto msg,
        bool isProto2,
        List<FieldDescriptorProto> fields,
        Dictionary<string, TypeSymbol> symbols,
        string indent)
    {
        _ = msgSym;
        _ = isProto2;
        string spanViewName = $"{msg.Name}SpanView";
        List<(int Index, string PascalName, List<FieldDescriptorProto> Fields)> realOneofs = GetRealOneofs(msg);

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}public readonly ref struct {spanViewName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
        foreach ((_, string oneofPascal, List<FieldDescriptorProto> members) in realOneofs)
        {
            string fNums = string.Join(", ", members.Select(m => $"{(uint)m.Number}u"));
            string fMasks = string.Join(", ", members.Select(OneofMemberWireMaskLiteral));
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private static readonly uint[] s_oneof_{oneofPascal}_fields = new uint[] {{ {fNums} }};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private static readonly ushort[] s_oneof_{oneofPascal}_masks = new ushort[] {{ {fMasks} }};");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    private readonly ReadOnlySpan<byte> _raw;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public {spanViewName}(ReadOnlySpan<byte> raw)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        _raw = raw;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public ReadOnlySpan<byte> RawSpan => _raw;");
        sb.AppendLine();

        foreach (FieldDescriptorProto f in fields)
        {
            if (IsMapField(f, symbols, out _, out _) || f.Label == FieldDescriptorProto.Types.Label.Repeated)
            {
                continue;
            }

            uint fnum = (uint)f.Number;
            string prop = GetPropertyName(f, msg.Name);
            string? oneofPascal = IsRealOneof(f) ? ToPascalCase(msg.OneofDecl[f.OneofIndex].Name) : null;
            string oneofGuard = oneofPascal is not null
                ? $"OmniBufOpaque.LocateActiveOneof(_raw, s_oneof_{oneofPascal}_fields, s_oneof_{oneofPascal}_masks) != {fnum}u"
                : string.Empty;

            if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                if (WrapperTypes.ContainsKey(f.TypeName))
                {
                    continue;
                }

                TypeSymbol subSym = symbols[f.TypeName];
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public bool TryGet{prop}(out {subSym.CSharpSpanViewFullName} view)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                if (oneofPascal is not null)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if ({oneofGuard}) {{ view = default; return false; }}");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (OmniBufOpaque.TryReadSubMsgSpan(_raw, {fnum}u, out ReadOnlySpan<byte> sub))");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            view = new {subSym.CSharpSpanViewFullName}(sub);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            return true;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        view = default;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        return false;");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                continue;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.Bytes)
            {
                string expr = $"OmniBufOpaque.TryReadBytesSpan(_raw, {fnum}u, out ReadOnlySpan<byte> b) ? b : ReadOnlySpan<byte>.Empty";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public ReadOnlySpan<byte> {prop} => {(oneofPascal is not null ? $"{oneofGuard} ? ReadOnlySpan<byte>.Empty : ({expr})" : expr)};");
                continue;
            }

            if (f.Type == FieldDescriptorProto.Types.Type.String)
            {
                string strExpr = $"OmniBufOpaque.TryReadString(_raw, {fnum}u, out string s) ? s : string.Empty";
                string spanExpr = $"OmniBufOpaque.TryReadStringSpan(_raw, {fnum}u, out ReadOnlySpan<byte> b) ? b : ReadOnlySpan<byte>.Empty";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public string {prop} => {(oneofPascal is not null ? $"{oneofGuard} ? string.Empty : ({strExpr})" : strExpr)};");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public ReadOnlySpan<byte> Get{prop}Utf8Span() => {(oneofPascal is not null ? $"{oneofGuard} ? ReadOnlySpan<byte>.Empty : ({spanExpr})" : spanExpr)};");
                continue;
            }

            string csType = CSharpScalarType(f, symbols);
            string defaultExpr = DefaultValueLiteral(f, symbols);
            string scalarExpr = f.Type switch
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
                _ => defaultExpr,
            };
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    public {csType} {prop} => {(oneofPascal is not null ? $"{oneofGuard} ? {defaultExpr} : ({scalarExpr})" : scalarExpr)};");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        sb.AppendLine();
    }

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

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    throw new OmniBufException($\"omnibuf: nesting depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}var map = d.f_{prop};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}int count = map?.Count ?? 0;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}encoder.BeginMapField({fnum}u, OmniBufWireType.{keyWt}, OmniBufWireType.{valWt}, count);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if (map is not null && count > 0)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (deterministic && count > 1)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        var keys = new List<{keyCs}>(map.Keys);");
            if (keyField.Type == FieldDescriptorProto.Types.Type.String)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        keys.Sort(Utf8OrdinalStringComparer.Instance);");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        keys.Sort();");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        for (int i = 0; i < keys.Count; i++)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            var k = keys[i];");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            var v = map[k];");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {EmitWriteRawValue("encoder", keyField, "k")};");
            EmitOpaqueMapValAppend(sb, indent + "            ", valField, "v");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    else");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        foreach (var kv in map)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            {EmitWriteRawValue("encoder", keyField, "kv.Key")};");
            EmitOpaqueMapValAppend(sb, indent + "            ", valField, "kv.Value");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}encoder.EndMap();");
            return;
        }

        if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
        {
            string elemWt = WireTypeEnumName(f.Type);
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    throw new OmniBufException($\"omnibuf: nesting depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}var list = d.f_{prop};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}int count = list?.Count ?? 0;");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}encoder.BeginArrayField({fnum}u, OmniBufWireType.{elemWt}, count);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if (list is not null)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    for (int i = 0; i < count; i++)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (depth + 2 > OmniBufConstants.MaxNestingDepth)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            throw new OmniBufException($\"omnibuf: nesting depth {{depth + 2}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? rWrap))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.BeginContainer();");
                    if (rWrap.IsValueType)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (list[i].HasValue)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            OmniBufWrappers.Write{rWrap.MethodSuffix}Body(encoder, list[i]!.Value);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                    }
                    else
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (list[i] is not null)");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            OmniBufWrappers.Write{rWrap.MethodSuffix}Body(encoder, list[i]);");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                    }

                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.EndContainer();");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.BeginContainer();");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        if (list[i] is not null)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}            list[i].SpliceBodyInto(encoder, deterministic, validate, depth + 2);");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        }}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        encoder.EndContainer();");
                }
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        {EmitWriteRawValue("encoder", f, "list[i]")};");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}encoder.EndArray();");
            return;
        }

        if (f.Type == FieldDescriptorProto.Types.Type.Message)
        {
            if (WrapperTypes.TryGetValue(f.TypeName, out WrapperInfo? sWrap))
            {
                string cond = IsRealOneof(f)
                    ? (sWrap.IsValueType ? $"d.present_{prop} && d.f_{prop}.HasValue" : $"d.present_{prop} && d.f_{prop} is not null")
                    : (sWrap.IsValueType ? $"d.f_{prop}.HasValue" : $"d.f_{prop} is not null");
                string valExpr = sWrap.IsValueType ? $"d.f_{prop}!.Value" : $"d.f_{prop}!";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if ({cond})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        throw new OmniBufException($\"omnibuf: nesting depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    encoder.BeginSubMsgField({fnum}u);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    OmniBufWrappers.Write{sWrap.MethodSuffix}Body(encoder, {valExpr});");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    encoder.EndSubMsg();");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                return;
            }

            string msgCond = IsRealOneof(f) ? $"d.present_{prop} && d.f_{prop} is not null" : $"d.f_{prop} is not null";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if ({msgCond})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    if (depth + 1 > OmniBufConstants.MaxNestingDepth)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}        throw new OmniBufException($\"omnibuf: nesting depth {{depth + 1}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    }}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    encoder.BeginSubMsgField({fnum}u);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    d.f_{prop}!.SpliceBodyInto(encoder, deterministic, validate, depth + 1);");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    encoder.EndSubMsg();");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            return;
        }

        bool hasPresence = HasExplicitScalarPresence(f, isProto2) || IsRealOneof(f);
        if (hasPresence)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if (d.present_{prop})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            if (f.Type == FieldDescriptorProto.Types.Type.Bytes)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    encoder.WriteBytesField({fnum}u, d.f_{prop} ?? Array.Empty<byte>());");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {EmitWriteField("encoder", fnum, f, $"d.f_{prop}!")};");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            return;
        }

        if (f.Type == FieldDescriptorProto.Types.Type.Bytes)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}encoder.WriteBytesField({fnum}u, d.f_{prop} ?? Array.Empty<byte>());");
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{EmitWriteField("encoder", fnum, f, $"d.f_{prop}!")};");
    }

    private static void EmitOpaqueMapValAppend(StringBuilder sb, string indent, FieldDescriptorProto valField, string valExpr)
    {
        if (valField.Type == FieldDescriptorProto.Types.Type.Message)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if (depth + 2 > OmniBufConstants.MaxNestingDepth)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    throw new OmniBufException($\"omnibuf: nesting depth {{depth + 2}} exceeds maximum {{OmniBufConstants.MaxNestingDepth}}\");");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
            if (WrapperTypes.TryGetValue(valField.TypeName, out WrapperInfo? mWrap))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}encoder.BeginContainer();");
                if (mWrap.IsValueType)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if ({valExpr}.HasValue)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    OmniBufWrappers.Write{mWrap.MethodSuffix}Body(encoder, {valExpr}.Value);");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if ({valExpr} is not null)");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    OmniBufWrappers.Write{mWrap.MethodSuffix}Body(encoder, {valExpr});");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}encoder.EndContainer();");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}encoder.BeginContainer();");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}if ({valExpr} is not null)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{{");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {valExpr}.SpliceBodyInto(encoder, deterministic, validate, depth + 2);");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}encoder.EndContainer();");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{EmitWriteRawValue("encoder", valField, valExpr)};");
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

    private static byte WireTypeByte(FieldDescriptorProto.Types.Type t) => t switch
    {
        FieldDescriptorProto.Types.Type.Uint32 or FieldDescriptorProto.Types.Type.Uint64 => 0,
        FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32
            or FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64
            or FieldDescriptorProto.Types.Type.Enum => 1,
        FieldDescriptorProto.Types.Type.Bool => 2,
        FieldDescriptorProto.Types.Type.Fixed32 => 4,
        FieldDescriptorProto.Types.Type.Fixed64 => 5,
        FieldDescriptorProto.Types.Type.Sfixed32 => 6,
        FieldDescriptorProto.Types.Type.Sfixed64 => 7,
        FieldDescriptorProto.Types.Type.Float => 8,
        FieldDescriptorProto.Types.Type.Double => 9,
        FieldDescriptorProto.Types.Type.String => 10,
        FieldDescriptorProto.Types.Type.Bytes => 11,
        FieldDescriptorProto.Types.Type.Message => 12,
        _ => throw new InvalidOperationException($"Unsupported proto type {t}"),
    };

    private static string EmitTagMatchReaderExpr(string rdVar, ulong tag)
    {
        if (tag <= 127UL)
        {
            byte enc1 = (byte)((tag << 1) | 1UL);
            return $"{rdVar}.MatchTag1(0x{enc1:X2})";
        }

        ushort enc2 = (ushort)((tag << 2) | 2UL);
        return $"{rdVar}.MatchTag2(0x{enc2:X4})";
    }

    private static string EmitReaderReadRawValue(string rdVar, FieldDescriptorProto f, Dictionary<string, TypeSymbol> symbols) => f.Type switch
    {
        FieldDescriptorProto.Types.Type.Uint32 => $"(uint){rdVar}.ReadVarint()",
        FieldDescriptorProto.Types.Type.Uint64 => $"{rdVar}.ReadVarint()",
        FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32 => $"(int)OmniBufConstants.ZigZagDecode64({rdVar}.ReadVarint())",
        FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 => $"OmniBufConstants.ZigZagDecode64({rdVar}.ReadVarint())",
        FieldDescriptorProto.Types.Type.Enum => $"({symbols[f.TypeName].CSharpFullName})(int)OmniBufConstants.ZigZagDecode64({rdVar}.ReadVarint())",
        FieldDescriptorProto.Types.Type.Bool => $"{rdVar}.ReadBoolElement()",
        FieldDescriptorProto.Types.Type.Fixed32 => $"{rdVar}.ReadFixed32()",
        FieldDescriptorProto.Types.Type.Fixed64 => $"{rdVar}.ReadFixed64()",
        FieldDescriptorProto.Types.Type.Sfixed32 => $"(int){rdVar}.ReadFixed32()",
        FieldDescriptorProto.Types.Type.Sfixed64 => $"(long){rdVar}.ReadFixed64()",
        FieldDescriptorProto.Types.Type.Float => $"BitConverter.UInt32BitsToSingle({rdVar}.ReadFixed32())",
        FieldDescriptorProto.Types.Type.Double => $"BitConverter.UInt64BitsToDouble({rdVar}.ReadFixed64())",
        FieldDescriptorProto.Types.Type.String => $"{rdVar}.ReadString()",
        FieldDescriptorProto.Types.Type.Bytes => $"ByteString.CopyFrom({rdVar}.ReadLenSpan(0))",
        _ => throw new InvalidOperationException($"Unsupported proto type {f.Type}"),
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
