using Google.Protobuf.Reflection;
using Xunit;

namespace OmniBuf.Tests;

public sealed class PluginTests
{
    [Fact]
    public void ProtocGenOmniBufCs_RejectsGroupsExtensionsCustomDefaultsAndSupportsProto2ZeroDefaultOptional()
    {
        // 1. Group field must return a clean generator error
        var groupFile = new FileDescriptorProto
        {
            Name = "bad_group.proto",
            Package = "testpkg",
            Syntax = "proto2",
        };
        var groupMsg = new DescriptorProto { Name = "BadGroupMsg" };
        groupMsg.Field.Add(new FieldDescriptorProto
        {
            Name = "grp",
            Number = 1,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Group,
            TypeName = ".testpkg.BadGroupMsg.Grp",
        });
        groupFile.MessageType.Add(groupMsg);

        (string? groupErr, _) = OmniBuf.ProtocGen.Program.GenerateCode(new[] { "bad_group.proto" }, new[] { groupFile });
        Assert.NotNull(groupErr);
        Assert.Contains("group", groupErr, StringComparison.OrdinalIgnoreCase);

        // 2. Extension range must return a clean generator error
        var extFile = new FileDescriptorProto
        {
            Name = "bad_ext.proto",
            Package = "testpkg",
            Syntax = "proto2",
        };
        var extMsg = new DescriptorProto { Name = "ExtendableMsg" };
        extMsg.ExtensionRange.Add(new DescriptorProto.Types.ExtensionRange { Start = 100, End = 200 });
        extFile.MessageType.Add(extMsg);

        (string? extErr, _) = OmniBuf.ProtocGen.Program.GenerateCode(new[] { "bad_ext.proto" }, new[] { extFile });
        Assert.NotNull(extErr);
        Assert.Contains("extension", extErr, StringComparison.OrdinalIgnoreCase);

        // 3. Non-zero proto2 default value must return a clean generator error
        var customDefFile = new FileDescriptorProto
        {
            Name = "bad_default.proto",
            Package = "testpkg",
            Syntax = "proto2",
        };
        var customDefMsg = new DescriptorProto { Name = "CustomDefaultMsg" };
        customDefMsg.Field.Add(new FieldDescriptorProto
        {
            Name = "retries",
            Number = 1,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Int32,
            DefaultValue = "3",
        });
        customDefFile.MessageType.Add(customDefMsg);

        (string? defErr, _) = OmniBuf.ProtocGen.Program.GenerateCode(new[] { "bad_default.proto" }, new[] { customDefFile });
        Assert.NotNull(defErr);
        Assert.Contains("default", defErr, StringComparison.OrdinalIgnoreCase);

        // 4. Valid proto2 message with explicit zero default succeeds
        var validProto2File = new FileDescriptorProto
        {
            Name = "ok_proto2.proto",
            Package = "testpkg",
            Syntax = "proto2",
        };
        var validProto2Msg = new DescriptorProto { Name = "ValidProto2Msg" };
        validProto2Msg.Field.Add(new FieldDescriptorProto
        {
            Name = "count",
            Number = 1,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Int32,
            DefaultValue = "0",
        });
        validProto2File.MessageType.Add(validProto2Msg);

        (string? okErr, List<(string Name, string Content)> files) =
            OmniBuf.ProtocGen.Program.GenerateCode(new[] { "ok_proto2.proto" }, new[] { validProto2File });
        Assert.Null(okErr);
        Assert.Single(files);
        Assert.Contains("this.HasCount", files[0].Content, StringComparison.Ordinal);
        Assert.Contains("System.Threading.Volatile.Read(ref _fieldIndex)", files[0].Content, StringComparison.Ordinal);
        Assert.Contains("System.Threading.Volatile.Write(ref _fieldIndex, built)", files[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtocGenOmniBufCs_AllowsFileDefiningFieldOptionsExtension()
    {
        FileDescriptorProto descriptorFile = FileDescriptorProto.Descriptor.File.ToProto();
        var targetFile = new FileDescriptorProto
        {
            Name = "field_options_ext.proto",
            Package = "omnibuf.test",
            Syntax = "proto3",
        };
        targetFile.Dependency.Add("google/protobuf/descriptor.proto");

        var msgProto = new DescriptorProto { Name = "PlainExtensionFileMessage" };
        msgProto.Field.Add(new FieldDescriptorProto
        {
            Name = "id",
            JsonName = "id",
            Number = 1,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Uint32,
        });
        targetFile.MessageType.Add(msgProto);

        targetFile.Extension.Add(new FieldDescriptorProto
        {
            Name = "custom_note",
            JsonName = "customNote",
            Number = 50001,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.String,
            Extendee = ".google.protobuf.FieldOptions",
        });

        (string? err, List<(string Name, string Content)> files) = OmniBuf.ProtocGen.Program.GenerateCode(
            new[] { targetFile.Name },
            new[] { descriptorFile, targetFile });

        Assert.Null(err);
        Assert.Single(files);
    }

    [Fact]
    public void ProtocGenOmniBufCs_RejectsUnsupportedEditionsAndTopLevelExtensions()
    {
        foreach (Edition badEdition in new[] { Edition.Unknown, (Edition)1003, (Edition)9999 })
        {
            var editionsFile = new FileDescriptorProto
            {
                Name = $"editions_{(int)badEdition}.proto",
                Package = "omnibuf.editions",
                Syntax = "editions",
                Edition = badEdition,
            };
            editionsFile.MessageType.Add(new DescriptorProto { Name = "EditionsMsg" });

            (string? edErr, _) = OmniBuf.ProtocGen.Program.GenerateCode(
                new[] { editionsFile.Name },
                new[] { editionsFile });
            Assert.NotNull(edErr);
            Assert.Contains("unsupported protobuf edition", edErr, StringComparison.OrdinalIgnoreCase);
        }

        var extFile = new FileDescriptorProto
        {
            Name = "top_ext_test.proto",
            Package = "omnibuf.ext",
            Syntax = "proto2",
        };
        var baseMsg = new DescriptorProto { Name = "BaseMsg" };
        extFile.MessageType.Add(baseMsg);
        extFile.Extension.Add(new FieldDescriptorProto
        {
            Name = "extra",
            Number = 100,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Uint32,
            Extendee = ".omnibuf.ext.BaseMsg",
        });

        (string? extErr, _) = OmniBuf.ProtocGen.Program.GenerateCode(
            new[] { extFile.Name },
            new[] { extFile });
        Assert.NotNull(extErr);
        Assert.Contains("extension", extErr, StringComparison.OrdinalIgnoreCase);
    }
}
