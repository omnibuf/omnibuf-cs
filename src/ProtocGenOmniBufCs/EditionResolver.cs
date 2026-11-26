using Google.Protobuf.Reflection;

namespace OmniBuf.ProtocGen;

internal static class EditionResolver
{
    internal const int MinimumSupportedEdition = 1000;
    internal const int MaximumSupportedEdition = 1002;

    internal static bool IsEditionsFile(FileDescriptorProto file) =>
        string.Equals(file.Syntax, "editions", StringComparison.Ordinal);

    internal static bool IsSupportedEdition(Edition edition) =>
        (int)edition is >= MinimumSupportedEdition and <= MaximumSupportedEdition;

    internal static FeatureSet CreateEditionBaseline() =>
        new()
        {
            FieldPresence = FeatureSet.Types.FieldPresence.Explicit,
            EnumType = FeatureSet.Types.EnumType.Open,
            RepeatedFieldEncoding = FeatureSet.Types.RepeatedFieldEncoding.Packed,
            Utf8Validation = FeatureSet.Types.Utf8Validation.Verify,
            MessageEncoding = FeatureSet.Types.MessageEncoding.LengthPrefixed,
            JsonFormat = FeatureSet.Types.JsonFormat.Allow,
        };

    internal static Dictionary<string, bool> BuildClosedEnumMap(IReadOnlyList<FileDescriptorProto> files)
    {
        var closedEnums = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (FileDescriptorProto file in files)
        {
            string protoPrefix = string.IsNullOrEmpty(file.Package) ? string.Empty : "." + file.Package;
            bool isProto3 = string.Equals(file.Syntax, "proto3", StringComparison.Ordinal);
            bool isEditions = IsEditionsFile(file);
            FeatureSet? fileFeatures = isEditions ? MergeFeatures(CreateEditionBaseline(), file.Options?.Features) : null;

            foreach (EnumDescriptorProto en in file.EnumType)
            {
                string fullName = $"{protoPrefix}.{en.Name}";
                closedEnums[fullName] = IsEnumClosed(isProto3, isEditions, fileFeatures, en);
            }

            foreach (DescriptorProto msg in file.MessageType)
            {
                CollectMessageEnums(closedEnums, msg, protoPrefix, isProto3, isEditions, fileFeatures);
            }
        }

        return closedEnums;
    }

    internal static string? ValidateEditionFile(
        FileDescriptorProto file,
        IReadOnlyDictionary<string, bool> closedEnums)
    {
        if (!IsEditionsFile(file))
        {
            return null;
        }

        if (!IsSupportedEdition(file.Edition))
        {
            return $"omnibuf: unsupported protobuf edition {file.Edition} in {file.Name}";
        }

        FeatureSet fileFeatures = MergeFeatures(CreateEditionBaseline(), file.Options?.Features);
        string protoPrefix = string.IsNullOrEmpty(file.Package) ? string.Empty : "." + file.Package;

        foreach (EnumDescriptorProto en in file.EnumType)
        {
            FeatureSet enumFeatures = MergeFeatures(fileFeatures, en.Options?.Features);
            if (enumFeatures.EnumType != FeatureSet.Types.EnumType.Open)
            {
                string enumFullName = $"{protoPrefix}.{en.Name}".TrimStart('.');
                return $"omnibuf: editions CLOSED enum {enumFullName} is not supported";
            }
        }

        foreach (DescriptorProto msg in file.MessageType)
        {
            string? err = ValidateEditionMessage($"{protoPrefix}.{msg.Name}", msg, fileFeatures, closedEnums);
            if (err is not null)
            {
                return err;
            }
        }

        return null;
    }

    internal static string? ValidateEditionMessageInFile(
        FileDescriptorProto file,
        string targetMessageFullName,
        IReadOnlyDictionary<string, bool> closedEnums)
    {
        if (!IsEditionsFile(file))
        {
            return null;
        }

        if (!IsSupportedEdition(file.Edition))
        {
            return $"omnibuf: unsupported protobuf edition {file.Edition} in {file.Name}";
        }

        FeatureSet fileFeatures = MergeFeatures(CreateEditionBaseline(), file.Options?.Features);
        string protoPrefix = string.IsNullOrEmpty(file.Package) ? string.Empty : "." + file.Package;

        foreach (DescriptorProto msg in file.MessageType)
        {
            if (TryFindMessageFeatures(
                    $"{protoPrefix}.{msg.Name}",
                    msg,
                    fileFeatures,
                    targetMessageFullName,
                    out DescriptorProto? foundMsg,
                    out FeatureSet? parentFeatures))
            {
                return ValidateEditionMessage(targetMessageFullName, foundMsg!, parentFeatures!, closedEnums);
            }
        }

        return null;
    }

    internal static FileDescriptorProto NormalizeForCodeGen(FileDescriptorProto source)
    {
        if (!IsEditionsFile(source) || !IsSupportedEdition(source.Edition))
        {
            return source;
        }

        FileDescriptorProto file = source.Clone();
        FeatureSet fileFeatures = MergeFeatures(CreateEditionBaseline(), file.Options?.Features);
        foreach (DescriptorProto message in file.MessageType)
        {
            NormalizeMessage(message, fileFeatures);
        }

        file.Syntax = "proto3";
        return file;
    }

    private static bool TryFindMessageFeatures(
        string currentFullName,
        DescriptorProto msg,
        FeatureSet parentFeatures,
        string targetFullName,
        out DescriptorProto? foundMsg,
        out FeatureSet? foundParentFeatures)
    {
        if (string.Equals(currentFullName, targetFullName, StringComparison.Ordinal))
        {
            foundMsg = msg;
            foundParentFeatures = parentFeatures;
            return true;
        }

        FeatureSet msgFeatures = MergeFeatures(parentFeatures, msg.Options?.Features);
        foreach (DescriptorProto nested in msg.NestedType)
        {
            if (TryFindMessageFeatures(
                    $"{currentFullName}.{nested.Name}",
                    nested,
                    msgFeatures,
                    targetFullName,
                    out foundMsg,
                    out foundParentFeatures))
            {
                return true;
            }
        }

        foundMsg = null;
        foundParentFeatures = null;
        return false;
    }

    private static string? ValidateEditionMessage(
        string protoFullName,
        DescriptorProto message,
        FeatureSet parentFeatures,
        IReadOnlyDictionary<string, bool> closedEnums)
    {
        FeatureSet msgFeatures = MergeFeatures(parentFeatures, message.Options?.Features);
        string displayMsgName = protoFullName.TrimStart('.');

        foreach (EnumDescriptorProto en in message.EnumType)
        {
            FeatureSet enumFeatures = MergeFeatures(msgFeatures, en.Options?.Features);
            if (enumFeatures.EnumType != FeatureSet.Types.EnumType.Open)
            {
                return $"omnibuf: editions CLOSED enum {displayMsgName}.{en.Name} is not supported";
            }
        }

        var mapEntryParentOverrides = new Dictionary<string, FeatureSet>(StringComparer.Ordinal);
        foreach (FieldDescriptorProto field in message.Field)
        {
            FeatureSet fieldFeatures = ResolveFieldFeatures(message, field, msgFeatures);

            if (fieldFeatures.FieldPresence is not (FeatureSet.Types.FieldPresence.Explicit or FeatureSet.Types.FieldPresence.Implicit)
                || field.Label == FieldDescriptorProto.Types.Label.Required)
            {
                return $"omnibuf: editions LEGACY_REQUIRED field presence on {displayMsgName}.{field.Name} is not supported";
            }

            if (field.Type == FieldDescriptorProto.Types.Type.String
                && fieldFeatures.Utf8Validation != FeatureSet.Types.Utf8Validation.Verify)
            {
                return $"omnibuf: editions UTF-8 validation NONE on {displayMsgName}.{field.Name} is not supported";
            }

            if (field.Type == FieldDescriptorProto.Types.Type.Enum
                && closedEnums.TryGetValue(field.TypeName, out bool isClosed)
                && isClosed)
            {
                return $"omnibuf: editions CLOSED enum {field.TypeName.TrimStart('.')} referenced by {displayMsgName}.{field.Name} is not supported";
            }

            if (field.Label == FieldDescriptorProto.Types.Label.Repeated
                && field.Type == FieldDescriptorProto.Types.Type.Message
                && field.Options?.Features is not null)
            {
                mapEntryParentOverrides[field.TypeName] = fieldFeatures;
            }
        }

        foreach (DescriptorProto nested in message.NestedType)
        {
            string nestedFullName = $"{protoFullName}.{nested.Name}";
            FeatureSet nestedParentFeatures = nested.Options?.MapEntry == true
                && mapEntryParentOverrides.TryGetValue(nestedFullName, out FeatureSet? mapOverride)
                ? mapOverride
                : msgFeatures;
            string? err = ValidateEditionMessage(nestedFullName, nested, nestedParentFeatures, closedEnums);
            if (err is not null)
            {
                return err;
            }
        }

        return null;
    }

    private static void NormalizeMessage(DescriptorProto message, FeatureSet parentFeatures)
    {
        FeatureSet msgFeatures = MergeFeatures(parentFeatures, message.Options?.Features);
        bool isMapEntry = message.Options?.MapEntry == true;

        foreach (FieldDescriptorProto field in message.Field)
        {
            FeatureSet resolved = ResolveFieldFeatures(message, field, msgFeatures);
            if (field.Type == FieldDescriptorProto.Types.Type.Group)
            {
                field.Type = FieldDescriptorProto.Types.Type.Message;
            }

            if (!field.HasOneofIndex
                && field.Label != FieldDescriptorProto.Types.Label.Repeated
                && field.Type != FieldDescriptorProto.Types.Type.Message
                && !isMapEntry)
            {
                field.Proto3Optional = resolved.FieldPresence == FeatureSet.Types.FieldPresence.Explicit;
            }
        }

        foreach (DescriptorProto nested in message.NestedType)
        {
            NormalizeMessage(nested, msgFeatures);
        }
    }

    private static void CollectMessageEnums(
        Dictionary<string, bool> closedEnums,
        DescriptorProto msg,
        string protoParent,
        bool isProto3,
        bool isEditions,
        FeatureSet? parentFeatures)
    {
        string protoName = $"{protoParent}.{msg.Name}";
        FeatureSet? msgFeatures = isEditions ? MergeFeatures(parentFeatures!, msg.Options?.Features) : null;

        foreach (EnumDescriptorProto en in msg.EnumType)
        {
            string fullName = $"{protoName}.{en.Name}";
            closedEnums[fullName] = IsEnumClosed(isProto3, isEditions, msgFeatures, en);
        }

        foreach (DescriptorProto nested in msg.NestedType)
        {
            CollectMessageEnums(closedEnums, nested, protoName, isProto3, isEditions, msgFeatures);
        }
    }

    private static bool IsEnumClosed(bool isProto3, bool isEditions, FeatureSet? parentFeatures, EnumDescriptorProto en)
    {
        if (isProto3)
        {
            return false;
        }

        if (isEditions)
        {
            FeatureSet resolved = MergeFeatures(parentFeatures!, en.Options?.Features);
            return resolved.EnumType != FeatureSet.Types.EnumType.Open;
        }

        return true;
    }

    private static FeatureSet ResolveFieldFeatures(DescriptorProto message, FieldDescriptorProto field, FeatureSet msgFeatures)
    {
        FeatureSet resolved = msgFeatures.Clone();
        if (field.HasOneofIndex
            && field.OneofIndex >= 0
            && field.OneofIndex < message.OneofDecl.Count
            && message.OneofDecl[field.OneofIndex].Options?.Features is { } oneofFeatures)
        {
            resolved.MergeFrom(oneofFeatures);
        }

        if (field.Options?.Features is { } fieldFeatures)
        {
            resolved.MergeFrom(fieldFeatures);
        }

        return resolved;
    }

    private static FeatureSet MergeFeatures(FeatureSet parent, FeatureSet? child)
    {
        FeatureSet merged = parent.Clone();
        if (child is not null)
        {
            merged.MergeFrom(child);
        }

        return merged;
    }
}
