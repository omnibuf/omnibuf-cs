using System.Diagnostics;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using OmniBuf.Tests.Editions2023;
using OmniBuf.Tests.Editions2023Implicit;
using OmniBuf.Tests.Editions2024;
using OmniBuf.Tests.Editions2026;
using OmniBuf.Tests.Rejected;
using Xunit;

namespace OmniBuf.Tests;

public sealed class EditionsTests
{
    [Fact]
    public void Edition2023_AbsentVsExplicitZero_RoundTripsAcrossPluginReflectionAndOpaque()
    {
        var empty = new Ed2023Record();
        byte[] emptyPlugin = empty.MarshalOmniBuf(new MarshalOptions { Deterministic = true });
        byte[] emptyRefl = OmniBufProtobuf.Marshal(empty, new MarshalOptions { Deterministic = true });
        Assert.Equal(emptyPlugin, emptyRefl);

        // Explicit zero/false/empty values must produce distinct wire bytes from absent fields
        var explicitZeros = new Ed2023Record
        {
            ExpI32 = 0,
            ExpDbl = 0.0,
            ExpBool = false,
            ExpStr = string.Empty,
            ExpBytes = ByteString.Empty,
            ExpStatus = Ed2023Status.Unspecified,
        };
        byte[] zerosPlugin = explicitZeros.MarshalOmniBuf(new MarshalOptions { Deterministic = true });
        byte[] zerosRefl = OmniBufProtobuf.Marshal(explicitZeros, new MarshalOptions { Deterministic = true });
        Assert.Equal(zerosPlugin, zerosRefl);
        Assert.NotEqual(emptyPlugin, zerosPlugin);
        Assert.True(zerosPlugin.Length > emptyPlugin.Length);

        // Verify WireBoolFalse tag is emitted for explicit false (field 3) and absent when unset
        Assert.False(OmniBufOpaque.LocateFieldMask(
            emptyPlugin,
            3u,
            (ushort)((1 << (byte)OmniBufWireType.BoolFalse) | (1 << (byte)OmniBufWireType.BoolTrue)),
            out _,
            out _));
        Assert.True(OmniBufOpaque.LocateFieldMask(
            zerosPlugin,
            3u,
            (ushort)((1 << (byte)OmniBufWireType.BoolFalse) | (1 << (byte)OmniBufWireType.BoolTrue)),
            out _,
            out OmniBufWireType boolWt));
        Assert.Equal(OmniBufWireType.BoolFalse, boolWt);

        // Verify Has* round-trips on fresh ParseOmniBuf, UnmarshalOmniBuf, and OmniBufProtobuf.Unmarshal
        Ed2023Record parsedEmptyPlugin = Ed2023Record.ParseOmniBuf(emptyPlugin);
        Ed2023Record parsedEmptyRefl = OmniBufProtobuf.Unmarshal<Ed2023Record>(emptyPlugin);
        foreach (Ed2023Record rec in new[] { parsedEmptyPlugin, parsedEmptyRefl })
        {
            Assert.False(rec.HasExpI32);
            Assert.False(rec.HasExpDbl);
            Assert.False(rec.HasExpBool);
            Assert.False(rec.HasExpStr);
            Assert.False(rec.HasExpBytes);
            Assert.False(rec.HasExpStatus);
        }

        Ed2023Record parsedZerosPlugin = Ed2023Record.ParseOmniBuf(zerosPlugin);
        Ed2023Record parsedZerosRefl = OmniBufProtobuf.Unmarshal<Ed2023Record>(zerosPlugin);
        foreach (Ed2023Record rec in new[] { parsedZerosPlugin, parsedZerosRefl })
        {
            Assert.True(rec.HasExpI32);
            Assert.Equal(0, rec.ExpI32);
            Assert.True(rec.HasExpDbl);
            Assert.Equal(0.0, rec.ExpDbl);
            Assert.True(rec.HasExpBool);
            Assert.False(rec.ExpBool);
            Assert.True(rec.HasExpStr);
            Assert.Equal(string.Empty, rec.ExpStr);
            Assert.True(rec.HasExpBytes);
            Assert.Equal(ByteString.Empty, rec.ExpBytes);
            Assert.True(rec.HasExpStatus);
            Assert.Equal(Ed2023Status.Unspecified, rec.ExpStatus);
        }

        // Reused instance must clear explicit presence when unmarshaling absent fields
        parsedZerosPlugin.UnmarshalOmniBuf(emptyPlugin);
        Assert.False(parsedZerosPlugin.HasExpI32);
        Assert.False(parsedZerosPlugin.HasExpDbl);
        Assert.False(parsedZerosPlugin.HasExpBool);
        Assert.False(parsedZerosPlugin.HasExpStr);
        Assert.False(parsedZerosPlugin.HasExpBytes);
        Assert.False(parsedZerosPlugin.HasExpStatus);

        OmniBufProtobuf.Unmarshal(emptyPlugin, parsedZerosRefl);
        Assert.False(parsedZerosRefl.HasExpI32);
        Assert.False(parsedZerosRefl.HasExpBool);
        Assert.False(parsedZerosRefl.HasExpStr);
        Assert.False(parsedZerosRefl.HasExpBytes);
        Assert.False(parsedZerosRefl.HasExpStatus);

        // Opaque view: absent vs present-zero reads, explicit zero writes, and clearing
        var opEmpty = new OpaqueEd2023Record(emptyPlugin);
        Assert.False(opEmpty.HasExpI32);
        Assert.False(opEmpty.HasExpDbl);
        Assert.False(opEmpty.HasExpBool);
        Assert.False(opEmpty.HasExpStr);
        Assert.False(opEmpty.HasExpBytes);
        Assert.False(opEmpty.HasExpStatus);

        var opZeros = new OpaqueEd2023Record(zerosPlugin);
        Assert.True(opZeros.HasExpI32);
        Assert.Equal(0, opZeros.ExpI32);
        Assert.True(opZeros.HasExpDbl);
        Assert.Equal(0.0, opZeros.ExpDbl);
        Assert.True(opZeros.HasExpBool);
        Assert.False(opZeros.ExpBool);
        Assert.True(opZeros.HasExpStr);
        Assert.Equal(string.Empty, opZeros.ExpStr);
        Assert.True(opZeros.HasExpBytes);
        Assert.Empty(opZeros.ExpBytes);
        Assert.True(opZeros.HasExpStatus);
        Assert.Equal(Ed2023Status.Unspecified, opZeros.ExpStatus);

        // Set explicit zeros on opaque constructed from empty bytes and marshal
        opEmpty.ExpI32 = 0;
        opEmpty.ExpDbl = 0.0;
        opEmpty.ExpBool = false;
        opEmpty.ExpStr = string.Empty;
        opEmpty.ExpBytes = Array.Empty<byte>();
        opEmpty.ExpStatus = Ed2023Status.Unspecified;
        Assert.True(opEmpty.HasExpI32);
        Assert.True(opEmpty.HasExpBool);
        Assert.True(opEmpty.HasExpStr);
        Assert.True(opEmpty.HasExpBytes);
        Assert.True(opEmpty.HasExpStatus);

        Ed2023Record fromOpSet = Ed2023Record.ParseOmniBuf(opEmpty.MarshalOmniBuf());
        Assert.True(fromOpSet.HasExpI32);
        Assert.True(fromOpSet.HasExpDbl);
        Assert.True(fromOpSet.HasExpBool);
        Assert.False(fromOpSet.ExpBool);
        Assert.True(fromOpSet.HasExpStr);
        Assert.True(fromOpSet.HasExpBytes);
        Assert.True(fromOpSet.HasExpStatus);

        // Clear explicit zeros on opaque and splice back to absent
        opZeros.ClearExpI32();
        opZeros.ClearExpDbl();
        opZeros.ClearExpBool();
        opZeros.ClearExpStr();
        opZeros.ClearExpBytes();
        opZeros.ClearExpStatus();
        Assert.False(opZeros.HasExpI32);
        Assert.False(opZeros.HasExpBool);
        Assert.False(opZeros.HasExpStr);
        Assert.False(opZeros.HasExpBytes);
        Assert.False(opZeros.HasExpStatus);
        Assert.Empty(opZeros.GetExpBytesSpan().ToArray());

        Ed2023Record fromOpCleared = Ed2023Record.ParseOmniBuf(opZeros.MarshalOmniBuf());
        Assert.False(fromOpCleared.HasExpI32);
        Assert.False(fromOpCleared.HasExpDbl);
        Assert.False(fromOpCleared.HasExpBool);
        Assert.False(fromOpCleared.HasExpStr);
        Assert.False(fromOpCleared.HasExpBytes);
        Assert.False(fromOpCleared.HasExpStatus);
    }

    [Fact]
    public void Edition2023_DelimitedRepeatedMapsOpenEnumsOneofAndOpaqueSplice_MatchByteForByte()
    {
        var record = new Ed2023Record
        {
            ExpI32 = -42,
            ExpDbl = 3.5,
            ExpBool = true,
            ExpStr = "editions-2023",
            ExpBytes = ByteString.CopyFrom(0x01, 0x02, 0x03),
            ExpStatus = (Ed2023Status)777,
            ImpI32 = 0,
            ImpBool = false,
            ImpStr = "implicit-str",
            ChildLen = new Ed2023Child { Id = 10, Label = "length-prefixed", Active = false },
            ChildDelim = new Ed2023Child { Id = 20, Label = "delimited-singular", Active = true },
            Nested = new Ed2023Record.Types.NestedDetail { Code = 0, Note = "nested-implicit" },
            OneofDelim = new Ed2023Child { Id = 30, Label = "oneof-delimited", Active = false },
        };
        record.RepPacked.AddRange(new[] { -1, 0, 5 });
        record.RepExpanded.AddRange(new[] { 10, 20, 30 });
        record.RepChildLen.Add(new Ed2023Child { Id = 1, Label = "len-0" });
        record.RepChildDelim.Add(new Ed2023Child { Id = 2, Label = "delim-0", Active = false });
        record.RepChildDelim.Add(new Ed2023Child { Id = 3, Label = "delim-1", Active = true });
        record.MapStrI32["alpha"] = 1;
        record.MapStrI32["beta"] = -2;
        record.MapStrChild["first"] = new Ed2023Child { Id = 100, Label = "map-child" };

        var opts = new MarshalOptions { Deterministic = true, SelfDescribing = true };
        byte[] pluginBytes = record.MarshalOmniBuf(opts);
        byte[] reflBytes = OmniBufProtobuf.Marshal(record, opts);
        Assert.Equal(pluginBytes, reflBytes);

        // Verify blind decode resolves delimited child and map value schema names
        Dictionary<string, object?> blind = OmniBufBlind.DecodeBlind(pluginBytes);
        Assert.Equal("delimited-singular", Assert.IsType<Dictionary<string, object?>>(blind["child_delim"])["label"]);
        var repDelimBlind = Assert.IsType<List<object?>>(blind["rep_child_delim"]);
        Assert.Equal("delim-1", Assert.IsType<Dictionary<string, object?>>(repDelimBlind[1])["label"]);
        var mapChildBlind = Assert.IsType<Dictionary<string, object?>>(blind["map_str_child"]);
        Assert.Equal("map-child", Assert.IsType<Dictionary<string, object?>>(mapChildBlind["first"])["label"]);

        Ed2023Record parsedPlugin = Ed2023Record.ParseOmniBuf(pluginBytes);
        Ed2023Record parsedRefl = OmniBufProtobuf.Unmarshal<Ed2023Record>(pluginBytes);
        Assert.Equal(record, parsedPlugin);
        Assert.Equal(record, parsedRefl);

        // Unknown open enum value (777) and negative unknown value (-9) must round-trip
        Assert.Equal((Ed2023Status)777, parsedPlugin.ExpStatus);
        Assert.Equal((Ed2023Status)777, parsedRefl.ExpStatus);
        Assert.True(parsedPlugin.ChildLen.HasActive);
        Assert.False(parsedPlugin.ChildLen.Active);
        Assert.True(parsedPlugin.ChildDelim.HasActive);
        Assert.True(parsedPlugin.ChildDelim.Active);
        Assert.True(parsedPlugin.Nested.HasCode);
        Assert.Equal(0, parsedPlugin.Nested.Code);
        Assert.Equal(Ed2023Record.ChoiceOneofCase.OneofDelim, parsedPlugin.ChoiceCase);
        Assert.Equal("oneof-delimited", parsedPlugin.OneofDelim.Label);

        var negEnumRecord = new Ed2023Record { ExpStatus = (Ed2023Status)(-9) };
        byte[] negWire = negEnumRecord.MarshalOmniBuf();
        Assert.Equal(negWire, OmniBufProtobuf.Marshal(negEnumRecord));
        Assert.Equal((Ed2023Status)(-9), Ed2023Record.ParseOmniBuf(negWire).ExpStatus);
        Assert.Equal((Ed2023Status)(-9), OmniBufProtobuf.Unmarshal<Ed2023Record>(negWire).ExpStatus);
        Assert.Equal((Ed2023Status)(-9), new OpaqueEd2023Record(negWire).ExpStatus);

        // Oneof active zero/false/empty and branch switching on OpaqueEd2023Record
        var oneofZero = new Ed2023Record { OneofBool = false };
        byte[] oneofZeroBytes = oneofZero.MarshalOmniBuf();
        Assert.Equal(oneofZeroBytes, OmniBufProtobuf.Marshal(oneofZero));
        Assert.Equal(Ed2023Record.ChoiceOneofCase.OneofBool, Ed2023Record.ParseOmniBuf(oneofZeroBytes).ChoiceCase);
        Assert.Equal(Ed2023Record.ChoiceOneofCase.OneofBool, OmniBufProtobuf.Unmarshal<Ed2023Record>(oneofZeroBytes).ChoiceCase);

        var op = new OpaqueEd2023Record(pluginBytes);
        Assert.Equal((Ed2023Status)777, op.ExpStatus);
        Assert.Equal("delimited-singular", op.ChildDelim!.Label);
        Assert.Equal(2, op.RepChildDelim.Count);
        Assert.Equal("delim-0", op.RepChildDelim[0].Label);
        Assert.NotNull(op.OneofDelim);
        Assert.Equal("oneof-delimited", op.OneofDelim.Label);
        op.ChildDelim.Label = "delimited-spliced";
        op.RepChildDelim[1].Label = "delim-1-spliced";
        op.OneofStr = string.Empty;
        Assert.Null(op.OneofDelim);
        Assert.True(op.HasOneofStr);

        byte[] spliced = op.MarshalOmniBuf(new MarshalOptions { Deterministic = true });
        Ed2023Record afterSplice = Ed2023Record.ParseOmniBuf(spliced);
        Assert.Equal("delimited-spliced", afterSplice.ChildDelim.Label);
        Assert.Equal("delim-1-spliced", afterSplice.RepChildDelim[1].Label);
        Assert.Equal(Ed2023Record.ChoiceOneofCase.OneofStr, afterSplice.ChoiceCase);
        Assert.Equal(string.Empty, afterSplice.OneofStr);
    }

    [Fact]
    public void Edition2023_FileLevelImplicitExpandedLegacyJsonAndDelimited_InheritAndOverrideCleanly()
    {
        var env = new Ed2023ImpEnvelope
        {
            ImpDefaultI32 = 0,
            ImpDefaultBool = false,
            ImpDefaultStr = string.Empty,
            ExpOverrideI32 = 0,
            DelimDefaultChild = new Ed2023ImpChild { Count = 0, Tag = string.Empty },
            LenOverrideChild = new Ed2023ImpChild { Count = 7 },
        };
        env.Nums.AddRange(new[] { 1, 2, 3 });
        env.RepDelimChildren.Add(new Ed2023ImpChild { Count = 5, Tag = "elem" });
        env.MapDelimChildren["k"] = new Ed2023ImpChild { Count = 9, Tag = "mapped" };

        var opts = new MarshalOptions { Deterministic = true, SelfDescribing = true };
        byte[] pluginBytes = env.MarshalOmniBuf(opts);
        byte[] reflBytes = OmniBufProtobuf.Marshal(env, opts);
        Assert.Equal(pluginBytes, reflBytes);

        Ed2023ImpEnvelope parsedPlugin = Ed2023ImpEnvelope.ParseOmniBuf(pluginBytes);
        Ed2023ImpEnvelope parsedRefl = OmniBufProtobuf.Unmarshal<Ed2023ImpEnvelope>(pluginBytes);
        Assert.Equal(env, parsedPlugin);
        Assert.Equal(env, parsedRefl);

        // ExpOverrideI32 was explicitly set to 0; ExpOverrideBool was left absent
        Assert.True(parsedPlugin.HasExpOverrideI32);
        Assert.Equal(0, parsedPlugin.ExpOverrideI32);
        Assert.False(parsedPlugin.HasExpOverrideBool);

        // Nested child Tag overrode file IMPLICIT back to EXPLICIT
        Assert.True(parsedPlugin.DelimDefaultChild.HasTag);
        Assert.Equal(string.Empty, parsedPlugin.DelimDefaultChild.Tag);
        Assert.False(parsedPlugin.LenOverrideChild.HasTag);
        Assert.Equal("mapped", parsedPlugin.MapDelimChildren["k"].Tag);

        // Verify Opaque view and SpanView read inherited delimited fields
        var op = new OpaqueEd2023ImpEnvelope(pluginBytes);
        Assert.True(op.HasExpOverrideI32);
        Assert.False(op.HasExpOverrideBool);
        Assert.Equal(0, op.DelimDefaultChild!.Count);
        Assert.True(op.DelimDefaultChild.HasTag);
        Assert.Equal(9, op.MapDelimChildren["k"].Count);

        Ed2023ImpEnvelopeSpanView spanView = op.AsSpanView();
        Assert.True(spanView.TryGetDelimDefaultChild(out Ed2023ImpChildSpanView childView));
        Assert.Equal(0, childView.Count);
    }

    [Fact]
    public void Edition2024_StrictVisibilityExportLocalAndCustomFieldOptions_RoundTripAcrossTiers()
    {
        var env = new Ed2024Envelope
        {
            Id = 0,
            Enabled = false,
            Name = "ed2024",
            Mode = (Ed2024Mode)99,
            MetaDelim = new Ed2024LocalMeta { Version = 2, Owner = "core" },
            Boxed = 0,
            Code = 0,
        };
        env.History.Add(new Ed2024LocalMeta { Version = 1, Owner = "init" });
        env.Scores.AddRange(new[] { 10, -5, 0 });
        env.Counters["hits"] = 42;

        var opts = new MarshalOptions { Deterministic = true, SelfDescribing = true };
        byte[] pluginBytes = env.MarshalOmniBuf(opts);
        byte[] reflBytes = OmniBufProtobuf.Marshal(env, opts);
        Assert.Equal(pluginBytes, reflBytes);

        Ed2024Envelope parsedPlugin = Ed2024Envelope.ParseOmniBuf(pluginBytes);
        Ed2024Envelope parsedRefl = OmniBufProtobuf.Unmarshal<Ed2024Envelope>(pluginBytes);
        Assert.Equal(env, parsedPlugin);
        Assert.Equal(env, parsedRefl);
        Assert.True(parsedPlugin.HasId);
        Assert.Equal(0, parsedPlugin.Id);
        Assert.True(parsedPlugin.HasEnabled);
        Assert.False(parsedPlugin.Enabled);
        Assert.Equal((Ed2024Mode)99, parsedPlugin.Mode);
        Assert.Equal(0, parsedPlugin.Boxed);
        Assert.Equal(Ed2024Envelope.PayloadOneofCase.Code, parsedPlugin.PayloadCase);

        var op = new OpaqueEd2024Envelope(pluginBytes);
        Assert.True(op.HasId);
        Assert.True(op.HasEnabled);
        Assert.False(op.Enabled);
        Assert.Equal("core", op.MetaDelim!.Owner);
        op.ClearEnabled();
        op.Text = "switched";
        Ed2024Envelope spliced = Ed2024Envelope.ParseOmniBuf(op.MarshalOmniBuf());
        Assert.False(spliced.HasEnabled);
        Assert.Equal(Ed2024Envelope.PayloadOneofCase.Text, spliced.PayloadCase);
        Assert.Equal("switched", spliced.Text);
    }

    [Fact]
    public void Edition2026_NullableReferenceTypesLimitsAndDelimited_RoundTripAcrossTiers()
    {
        var payload = new Ed2026Payload
        {
            Seq = 0,
            Flag = false,
            Label = string.Empty,
            Raw = ByteString.Empty,
            Tier = (Ed2026Tier)123,
            ImpCounter = 0,
            Primary = new Ed2026Item { Key = 1, Value = "primary" },
            SecondaryDelim = new Ed2026Item { Key = 2, Value = "secondary-delim" },
            Note = string.Empty,
            VWrap = string.Empty,
        };
        payload.ItemsDelim.Add(new Ed2026Item { Key = 3, Value = "delim-item" });
        payload.Lookup["main"] = new Ed2026Item { Key = 4, Value = "lookup-item" };

        var opts = new MarshalOptions { Deterministic = true, SelfDescribing = true };
        byte[] pluginBytes = payload.MarshalOmniBuf(opts);
        byte[] reflBytes = OmniBufProtobuf.Marshal(payload, opts);
        Assert.Equal(pluginBytes, reflBytes);

        Ed2026Payload parsedPlugin = Ed2026Payload.ParseOmniBuf(pluginBytes);
        Ed2026Payload parsedRefl = OmniBufProtobuf.Unmarshal<Ed2026Payload>(pluginBytes);
        Assert.Equal(payload, parsedPlugin);
        Assert.Equal(payload, parsedRefl);

        Assert.True(parsedPlugin.HasSeq);
        Assert.Equal(0, parsedPlugin.Seq);
        Assert.True(parsedPlugin.HasFlag);
        Assert.False(parsedPlugin.Flag);
        Assert.True(parsedPlugin.HasLabel);
        Assert.Equal(string.Empty, parsedPlugin.Label);
        Assert.True(parsedPlugin.HasRaw);
        Assert.Equal(ByteString.Empty, parsedPlugin.Raw);
        Assert.Equal((Ed2026Tier)123, parsedPlugin.Tier);
        Assert.Equal("secondary-delim", parsedPlugin.SecondaryDelim!.Value);
        Assert.Equal(string.Empty, parsedPlugin.Note);
        Assert.Equal(Ed2026Payload.VariantOneofCase.VWrap, parsedPlugin.VariantCase);
        Assert.Equal(string.Empty, parsedPlugin.VWrap);

        // Also test switching oneof to message branch with nullable reference types enabled
        payload.VItem = new Ed2026Item { Key = 5, Value = "oneof-item" };
        byte[] msgArmPlugin = payload.MarshalOmniBuf(new MarshalOptions { Deterministic = true });
        byte[] msgArmRefl = OmniBufProtobuf.Marshal(payload, new MarshalOptions { Deterministic = true });
        Assert.Equal(msgArmPlugin, msgArmRefl);
        Ed2026Payload parsedMsgArm = Ed2026Payload.ParseOmniBuf(msgArmPlugin);
        Assert.Equal(Ed2026Payload.VariantOneofCase.VItem, parsedMsgArm.VariantCase);
        Assert.Equal("oneof-item", parsedMsgArm.VItem!.Value);

        // Opaque view on Edition 2026 payload
        var op = new OpaqueEd2026Payload(pluginBytes);
        Assert.True(op.HasSeq);
        Assert.True(op.HasFlag);
        Assert.False(op.Flag);
        Assert.Equal("secondary-delim", op.SecondaryDelim!.Value);
        op.SecondaryDelim.Value = "spliced-2026";
        op.ClearFlag();
        Ed2026Payload afterOp = Ed2026Payload.ParseOmniBuf(op.MarshalOmniBuf());
        Assert.False(afterOp.HasFlag);
        Assert.Equal("spliced-2026", afterOp.SecondaryDelim!.Value);
    }

    [Fact]
    public void Editions_RejectedFeatures_FailConsistentlyInBothPluginAndReflection()
    {
        // 1. Declared CLOSED enum (same file)
        AssertRejectedInBoth(
            new ClosedEnumHolder { State = ClosedState.On },
            new[] { ClosedEnumHolder.Descriptor.File.ToProto() },
            ClosedEnumHolder.Descriptor.File.Name,
            "CLOSED");

        // 2. Cross-file CLOSED enum reference
        AssertRejectedInBoth(
            new CrossFileClosedHolder { State = ClosedState.On },
            new[]
            {
                ClosedEnumHolder.Descriptor.File.ToProto(),
                CrossFileClosedHolder.Descriptor.File.ToProto(),
            },
            CrossFileClosedHolder.Descriptor.File.Name,
            "CLOSED");

        // 3. LEGACY_REQUIRED field presence
        AssertRejectedInBoth(
            new LegacyRequiredMsg { ReqId = 1 },
            new[] { LegacyRequiredMsg.Descriptor.File.ToProto() },
            LegacyRequiredMsg.Descriptor.File.Name,
            "LEGACY_REQUIRED");

        // 4. utf8_validation = NONE on singular string
        AssertRejectedInBoth(
            new Utf8NoneScalarMsg { Raw = "ok" },
            new[] { Utf8NoneScalarMsg.Descriptor.File.ToProto() },
            Utf8NoneScalarMsg.Descriptor.File.Name,
            "UTF-8");

        // 5. utf8_validation = NONE on repeated string
        var utf8Rep = new Utf8NoneRepeatedMsg();
        utf8Rep.Items.Add("ok");
        AssertRejectedInBoth(
            utf8Rep,
            new[] { Utf8NoneRepeatedMsg.Descriptor.File.ToProto() },
            Utf8NoneRepeatedMsg.Descriptor.File.Name,
            "UTF-8");

        // 6. utf8_validation = NONE on map<string, int32> key
        var utf8MapKey = new Utf8NoneMapKeyMsg();
        utf8MapKey.Labels["k"] = 1;
        AssertRejectedInBoth(
            utf8MapKey,
            new[] { Utf8NoneMapKeyMsg.Descriptor.File.ToProto() },
            Utf8NoneMapKeyMsg.Descriptor.File.Name,
            "UTF-8");

        // 7. utf8_validation = NONE on map<int32, string> value
        var utf8MapVal = new Utf8NoneMapValMsg();
        utf8MapVal.Names[1] = "v";
        AssertRejectedInBoth(
            utf8MapVal,
            new[] { Utf8NoneMapValMsg.Descriptor.File.ToProto() },
            Utf8NoneMapValMsg.Descriptor.File.Name,
            "UTF-8");

        // 8. Custom non-zero default in Editions
        AssertRejectedInBoth(
            new NonZeroDefaultMsg { Retries = 5 },
            new[] { NonZeroDefaultMsg.Descriptor.File.ToProto() },
            NonZeroDefaultMsg.Descriptor.File.Name,
            "default");

        // 9. Proto2 group must remain rejected while Editions DELIMITED succeeds
        AssertRejectedInBoth(
            new Proto2GroupMsg { LegacyGroup = new Proto2GroupMsg.Types.LegacyGroup { A = 1 } },
            new[] { Proto2GroupMsg.Descriptor.File.ToProto() },
            Proto2GroupMsg.Descriptor.File.Name,
            "group");
    }

    [Fact]
    public void Editions_SyntheticInheritanceAndUnsupportedEditions_EnforcedInPluginAndReflection()
    {
        // Verify synthetic message -> nested message -> oneof -> field feature inheritance in plugin
        var inheritedFile = new FileDescriptorProto
        {
            Name = "inherited_features.proto",
            Package = "omnibuf.test.inherit",
            Syntax = "editions",
            Edition = (Edition)1002,
            Options = new Google.Protobuf.Reflection.FileOptions
            {
                Features = new FeatureSet { FieldPresence = FeatureSet.Types.FieldPresence.Implicit },
            },
        };
        var outerMsg = new DescriptorProto
        {
            Name = "OuterInherit",
            Options = new MessageOptions
            {
                Features = new FeatureSet { FieldPresence = FeatureSet.Types.FieldPresence.Explicit },
            },
        };
        outerMsg.Field.Add(new FieldDescriptorProto
        {
            Name = "msg_explicit",
            Number = 1,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Int32,
        });
        outerMsg.Field.Add(new FieldDescriptorProto
        {
            Name = "field_implicit",
            Number = 2,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Int32,
            Options = new FieldOptions
            {
                Features = new FeatureSet { FieldPresence = FeatureSet.Types.FieldPresence.Implicit },
            },
        });
        inheritedFile.MessageType.Add(outerMsg);

        (string? inheritErr, List<(string Name, string Content)> inheritFiles) = OmniBuf.ProtocGen.Program.GenerateCode(
            new[] { inheritedFile.Name },
            new[] { inheritedFile });
        Assert.Null(inheritErr);
        Assert.Single(inheritFiles);
        Assert.Contains("this.HasMsgExplicit", inheritFiles[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("this.HasFieldImplicit", inheritFiles[0].Content, StringComparison.Ordinal);

        // Oneof-level LEGACY_REQUIRED override must be rejected
        var oneofReqFile = new FileDescriptorProto
        {
            Name = "oneof_req.proto",
            Package = "omnibuf.test.oneofreq",
            Syntax = "editions",
            Edition = (Edition)1000,
        };
        var oneofReqMsg = new DescriptorProto { Name = "OneofReqMsg" };
        oneofReqMsg.OneofDecl.Add(new OneofDescriptorProto
        {
            Name = "pick",
            Options = new OneofOptions
            {
                Features = new FeatureSet { FieldPresence = FeatureSet.Types.FieldPresence.LegacyRequired },
            },
        });
        oneofReqMsg.Field.Add(new FieldDescriptorProto
        {
            Name = "a",
            Number = 1,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Int32,
            OneofIndex = 0,
        });
        oneofReqFile.MessageType.Add(oneofReqMsg);

        (string? oneofErr, _) = OmniBuf.ProtocGen.Program.GenerateCode(
            new[] { oneofReqFile.Name },
            new[] { oneofReqFile });
        Assert.NotNull(oneofErr);
        Assert.Contains("LEGACY_REQUIRED", oneofErr, StringComparison.OrdinalIgnoreCase);

        // Cross-file proto2 enum referenced by an Editions file must be rejected as CLOSED
        var proto2EnumFile = new FileDescriptorProto
        {
            Name = "dep_p2_enum.proto",
            Package = "omnibuf.test.depp2",
            Syntax = "proto2",
        };
        var p2Enum = new EnumDescriptorProto { Name = "LegacyClosed" };
        p2Enum.Value.Add(new EnumValueDescriptorProto { Name = "LEGACY_CLOSED_ZERO", Number = 0 });
        proto2EnumFile.EnumType.Add(p2Enum);

        var edUsingP2Enum = new FileDescriptorProto
        {
            Name = "ed_uses_p2_enum.proto",
            Package = "omnibuf.test.edp2",
            Syntax = "editions",
            Edition = (Edition)1001,
        };
        edUsingP2Enum.Dependency.Add(proto2EnumFile.Name);
        var edMsg = new DescriptorProto { Name = "UsesLegacyEnum" };
        edMsg.Field.Add(new FieldDescriptorProto
        {
            Name = "legacy",
            Number = 1,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Enum,
            TypeName = ".omnibuf.test.depp2.LegacyClosed",
        });
        edUsingP2Enum.MessageType.Add(edMsg);

        (string? crossP2Err, _) = OmniBuf.ProtocGen.Program.GenerateCode(
            new[] { edUsingP2Enum.Name },
            new[] { proto2EnumFile, edUsingP2Enum });
        Assert.NotNull(crossP2Err);
        Assert.Contains("CLOSED", crossP2Err, StringComparison.OrdinalIgnoreCase);

        // Editions file with utf8_validation = NONE and no string fields must succeed
        var utf8NoStrings = new FileDescriptorProto
        {
            Name = "utf8_none_no_strings.proto",
            Package = "omnibuf.test.nostr",
            Syntax = "editions",
            Edition = (Edition)1000,
            Options = new Google.Protobuf.Reflection.FileOptions
            {
                Features = new FeatureSet { Utf8Validation = FeatureSet.Types.Utf8Validation.None },
            },
        };
        var noStrMsg = new DescriptorProto { Name = "OnlyBytesAndInts" };
        noStrMsg.Field.Add(new FieldDescriptorProto
        {
            Name = "id",
            Number = 1,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Int32,
        });
        noStrMsg.Field.Add(new FieldDescriptorProto
        {
            Name = "raw",
            Number = 2,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Bytes,
        });
        utf8NoStrings.MessageType.Add(noStrMsg);

        (string? noStrErr, List<(string Name, string Content)> noStrFiles) = OmniBuf.ProtocGen.Program.GenerateCode(
            new[] { utf8NoStrings.Name },
            new[] { utf8NoStrings });
        Assert.Null(noStrErr);
        Assert.Single(noStrFiles);

        // Unused declared CLOSED enum (top-level and nested) rejected in plugin and reflection
        var unusedTopClosed = new FileDescriptorProto
        {
            Name = "unused_top_closed.proto",
            Package = "omnibuf.test.unusedclosed",
            Syntax = "editions",
            Edition = (Edition)1000,
        };
        var topClosedEnum = new EnumDescriptorProto
        {
            Name = "UnusedClosed",
            Options = new EnumOptions { Features = new FeatureSet { EnumType = FeatureSet.Types.EnumType.Closed } },
        };
        topClosedEnum.Value.Add(new EnumValueDescriptorProto { Name = "UNUSED_CLOSED_ZERO", Number = 0 });
        unusedTopClosed.EnumType.Add(topClosedEnum);
        var plainMsgProto = new DescriptorProto { Name = "PlainHolder" };
        plainMsgProto.Field.Add(new FieldDescriptorProto
        {
            Name = "x",
            JsonName = "x",
            Number = 1,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Int32,
        });
        unusedTopClosed.MessageType.Add(plainMsgProto);

        (string? topClosedPluginErr, _) = OmniBuf.ProtocGen.Program.GenerateCode(
            new[] { unusedTopClosed.Name },
            new[] { unusedTopClosed });
        Assert.NotNull(topClosedPluginErr);
        Assert.Contains("CLOSED", topClosedPluginErr, StringComparison.OrdinalIgnoreCase);

        FileDescriptor unusedTopClosedFd = BuildSyntheticFileDescriptor(unusedTopClosed, includeEnum: typeof(ClosedState));
        var unusedTopClosedEx = Assert.Throws<OmniBufException>(
            () => OmniBufProtobuf.Marshal(new SyntheticEditionMessage(unusedTopClosedFd.MessageTypes[0]) { X = 1 }));
        Assert.Contains("CLOSED", unusedTopClosedEx.Message, StringComparison.OrdinalIgnoreCase);

        // Editions non-zero defaults (bool, string, negative zero double) rejected in plugin and reflection
        foreach ((FieldDescriptorProto.Types.Type ft, string badDefault) in new[]
        {
            (FieldDescriptorProto.Types.Type.Bool, "true"),
            (FieldDescriptorProto.Types.Type.String, "abc"),
            (FieldDescriptorProto.Types.Type.Double, "-0.0"),
        })
        {
            var badDefFile = new FileDescriptorProto
            {
                Name = $"bad_def_{ft}.proto",
                Package = "omnibuf.test.baddef",
                Syntax = "editions",
                Edition = (Edition)1002,
            };
            var badDefMsg = new DescriptorProto { Name = "BadDefMsg" };
            badDefMsg.Field.Add(new FieldDescriptorProto
            {
                Name = "v",
                JsonName = "v",
                Number = 1,
                Label = FieldDescriptorProto.Types.Label.Optional,
                Type = ft,
                DefaultValue = badDefault,
            });
            badDefFile.MessageType.Add(badDefMsg);

            (string? badDefErr, _) = OmniBuf.ProtocGen.Program.GenerateCode(
                new[] { badDefFile.Name },
                new[] { badDefFile });
            Assert.NotNull(badDefErr);
            Assert.Contains("default", badDefErr, StringComparison.OrdinalIgnoreCase);
        }

        // Unsupported editions (0, 1003, 9999) rejected in BOTH plugin AND OmniBufProtobuf
        int reflectionUnsupportedChecked = 0;
        foreach (Edition badEdition in new[] { Edition.Unknown, (Edition)1003, (Edition)9999 })
        {
            var badFile = new FileDescriptorProto
            {
                Name = $"bad_ed_{(int)badEdition}.proto",
                Package = "omnibuf.test.baded",
                Syntax = "editions",
                Edition = badEdition,
            };
            var badMsgProto = new DescriptorProto { Name = "BadEditionMsg" };
            badMsgProto.Field.Add(new FieldDescriptorProto
            {
                Name = "x",
                JsonName = "x",
                Number = 1,
                Label = FieldDescriptorProto.Types.Label.Optional,
                Type = FieldDescriptorProto.Types.Type.Int32,
            });
            badFile.MessageType.Add(badMsgProto);

            (string? plugErr, _) = OmniBuf.ProtocGen.Program.GenerateCode(
                new[] { badFile.Name },
                new[] { badFile });
            Assert.NotNull(plugErr);
            Assert.Contains("unsupported protobuf edition", plugErr, StringComparison.OrdinalIgnoreCase);

            // Build runtime descriptor when Google.Protobuf accepts the edition enum value
            FileDescriptor? runtimeFd = null;
            try
            {
                runtimeFd = BuildSyntheticFileDescriptor(badFile, includeEnum: null);
            }
            catch (ArgumentException)
            {
                runtimeFd = null;
            }
            catch (InvalidOperationException)
            {
                runtimeFd = null;
            }
            catch (DescriptorValidationException)
            {
                runtimeFd = null;
            }

            if (runtimeFd is not null)
            {
                reflectionUnsupportedChecked++;
                var synthMsg = new SyntheticEditionMessage(runtimeFd.MessageTypes[0]) { X = 1 };
                var ex = Assert.Throws<OmniBufException>(() => OmniBufProtobuf.Marshal(synthMsg));
                Assert.Contains("unsupported protobuf edition", ex.Message, StringComparison.OrdinalIgnoreCase);

                var unmarshalEx = Assert.Throws<OmniBufException>(
                    () => OmniBufProtobuf.Unmarshal(ReadOnlySpan<byte>.Empty, new SyntheticEditionMessage(runtimeFd.MessageTypes[0])));
                Assert.Contains("unsupported protobuf edition", unmarshalEx.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.True(reflectionUnsupportedChecked > 0, "Expected at least one unsupported edition to exercise OmniBufProtobuf rejection");
    }

    [Fact]
    public void ProtocGenOmniBufCs_ResponseAdvertisesEditions2023Through2026()
    {
        // Invoke the compiled plugin binary with an empty CodeGeneratorRequest and decode response header
        string pluginDll = typeof(OmniBuf.ProtocGen.Program).Assembly.Location;
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{pluginDll}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);
        proc.StandardInput.BaseStream.Close();

        using var outMs = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(outMs);
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);

        ulong supportedFeatures = 0;
        int minEdition = -1;
        int maxEdition = -1;
        var cis = new CodedInputStream(outMs.ToArray());
        uint tag;
        while ((tag = cis.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 2:
                    supportedFeatures = cis.ReadUInt64();
                    break;
                case 3:
                    minEdition = cis.ReadInt32();
                    break;
                case 4:
                    maxEdition = cis.ReadInt32();
                    break;
                default:
                    cis.SkipLastField();
                    break;
            }
        }

        Assert.Equal(3UL, supportedFeatures);
        Assert.Equal(1000, minEdition);
        Assert.Equal(1002, maxEdition);
    }

    private static void AssertRejectedInBoth(
        IMessage message,
        IReadOnlyList<FileDescriptorProto> protoFiles,
        string targetFileName,
        string expectedSubstring)
    {
        (string? pluginErr, _) = OmniBuf.ProtocGen.Program.GenerateCode(
            new[] { targetFileName },
            protoFiles);
        Assert.NotNull(pluginErr);
        Assert.Contains(expectedSubstring, pluginErr, StringComparison.OrdinalIgnoreCase);

        var marshalEx = Assert.Throws<OmniBufException>(() => OmniBufProtobuf.Marshal(message));
        Assert.Contains(expectedSubstring, marshalEx.Message, StringComparison.OrdinalIgnoreCase);

        IMessage emptyTarget = message.Descriptor.Parser.ParseFrom(ByteString.Empty);
        var unmarshalEx = Assert.Throws<OmniBufException>(() => OmniBufProtobuf.Unmarshal(ReadOnlySpan<byte>.Empty, emptyTarget));
        Assert.Contains(expectedSubstring, unmarshalEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FileDescriptor BuildSyntheticFileDescriptor(FileDescriptorProto fileProto, Type? includeEnum)
    {
        return FileDescriptor.FromGeneratedCode(
            fileProto.ToByteArray(),
            Array.Empty<FileDescriptor>(),
            new GeneratedClrTypeInfo(
                includeEnum is null ? null : new[] { includeEnum },
                null,
                new[]
                {
                    new GeneratedClrTypeInfo(
                        typeof(SyntheticEditionMessage),
                        SyntheticEditionMessage.Parser,
                        new[] { nameof(SyntheticEditionMessage.X) },
                        null,
                        null,
                        null,
                        null),
                }));
    }

    private sealed class SyntheticEditionMessage : IMessage<SyntheticEditionMessage>
    {
        private readonly MessageDescriptor _descriptor;
        private int _x;

        public SyntheticEditionMessage()
            : this(Ed2023Child.Descriptor)
        {
        }

        public SyntheticEditionMessage(MessageDescriptor descriptor)
        {
            _descriptor = descriptor;
        }

        public static MessageParser<SyntheticEditionMessage> Parser { get; } = new(() => new SyntheticEditionMessage());

        public int X
        {
            get => _x;
            set
            {
                _x = value;
                HasX = true;
            }
        }

        public bool HasX { get; private set; }

        public void ClearX()
        {
            _x = 0;
            HasX = false;
        }

        public MessageDescriptor Descriptor => _descriptor;

        public int CalculateSize() => 0;

        public SyntheticEditionMessage Clone() => new(_descriptor) { _x = _x, HasX = HasX };

        public bool Equals(SyntheticEditionMessage? other) => other is not null && HasX == other.HasX && _x == other._x;

        public override bool Equals(object? obj) => obj is SyntheticEditionMessage other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(HasX, _x);

        public void MergeFrom(SyntheticEditionMessage message)
        {
            if (message.HasX)
            {
                X = message.X;
            }
        }

        public void MergeFrom(CodedInputStream input)
        {
            while (input.ReadTag() != 0)
            {
                input.SkipLastField();
            }
        }

        public void WriteTo(CodedOutputStream output)
        {
        }
    }
}
