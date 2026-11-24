using Google.Protobuf;
using Google.Protobuf.Reflection;
using Omnibuf.Bench;
using Omnibuf.Conformance;
using OmniBuf;
using OmniBuf.ConformanceRunner;
using Xunit;

namespace OmniBuf.Tests;

public sealed class ProtobufTests
{
    private static readonly FileDescriptor FieldOptionsExtensionFileDescriptor = BuildFieldOptionsExtensionFileDescriptor();

    private static FileDescriptorProto BuildFieldOptionsExtensionFileDescriptorProto()
    {
        var fileProto = new FileDescriptorProto
        {
            Name = "field_options_ext.proto",
            Package = "omnibuf.test",
            Syntax = "proto3",
        };
        fileProto.Dependency.Add("google/protobuf/descriptor.proto");

        var msgProto = new DescriptorProto { Name = "PlainExtensionFileMessage" };
        msgProto.Field.Add(new FieldDescriptorProto
        {
            Name = "id",
            JsonName = "id",
            Number = 1,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Uint32,
        });
        fileProto.MessageType.Add(msgProto);

        fileProto.Extension.Add(new FieldDescriptorProto
        {
            Name = "custom_note",
            JsonName = "customNote",
            Number = 50001,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.String,
            Extendee = ".google.protobuf.FieldOptions",
        });

        return fileProto;
    }

    private static FileDescriptor BuildFieldOptionsExtensionFileDescriptor()
    {
        FileDescriptorProto fileProto = BuildFieldOptionsExtensionFileDescriptorProto();
        var ext = new Extension<FieldOptions, string>(50001, FieldCodec.ForString(400010u, string.Empty));
        return FileDescriptor.FromGeneratedCode(
            fileProto.ToByteArray(),
            new[] { FileDescriptorProto.Descriptor.File },
            new GeneratedClrTypeInfo(
                null,
                new Extension[] { ext },
                new[]
                {
                    new GeneratedClrTypeInfo(
                        typeof(PlainExtensionFileMessage),
                        PlainExtensionFileMessage.Parser,
                        new[] { nameof(PlainExtensionFileMessage.Id) },
                        null,
                        null,
                        null,
                        null),
                }));
    }

    private sealed class PlainExtensionFileMessage : IMessage<PlainExtensionFileMessage>
    {
        public static MessageParser<PlainExtensionFileMessage> Parser { get; } = new(() => new PlainExtensionFileMessage());

        public uint Id { get; set; }

        public MessageDescriptor Descriptor => FieldOptionsExtensionFileDescriptor.MessageTypes[0];

        public int CalculateSize() => Id == 0 ? 0 : CodedOutputStream.ComputeTagSize(1) + CodedOutputStream.ComputeUInt32Size(Id);

        public PlainExtensionFileMessage Clone() => new() { Id = Id };

        public bool Equals(PlainExtensionFileMessage? other) => other is not null && Id == other.Id;

        public override bool Equals(object? obj) => obj is PlainExtensionFileMessage other && Equals(other);

        public override int GetHashCode() => Id.GetHashCode();

        public void MergeFrom(PlainExtensionFileMessage message)
        {
            if (message.Id != 0)
            {
                Id = message.Id;
            }
        }

        public void MergeFrom(CodedInputStream input)
        {
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (tag == 8u)
                {
                    Id = input.ReadUInt32();
                }
                else
                {
                    input.SkipLastField();
                }
            }
        }

        public void WriteTo(CodedOutputStream output)
        {
            if (Id != 0)
            {
                output.WriteRawTag(8);
                output.WriteUInt32(Id);
            }
        }
    }

    [Fact]
    public void OmniBufProtobuf_MatchesPluginWireBytesOnPeerUpdateAndTestAllTypes()
    {
        var peer = new PeerUpdate
        {
            Asn = 64512,
            RouterId = "edge-rtr-fra01.core.backbone.net",
            Active = true,
            UptimeSec = 1849203,
            RssiDbm = -62,
            CpuLoad = 0.42875,
            Location = new Location
            {
                Region = "eu-central-1",
                Datacenter = "FRA-Equinix-FR5",
                City = "Frankfurt",
                Rack = 42,
                Latitude = 50.1109f,
                Longitude = 8.6821f,
            },
            Sequence = 9876543210UL,
        };
        peer.Tags.AddRange(new[] { "tier1", "ebgp" });
        peer.Prefixes.AddRange(new uint[] { 167772160, 172032000 });
        peer.Neighbors.Add(new Neighbor { PeerAsn = 3356, Router = "ae0.fra1.level3.net", LatencyUs = 850, Established = true });

        byte[] pluginBytes = peer.MarshalOmniBuf(new MarshalOptions { Deterministic = true });
        byte[] reflectionBytes = OmniBufProtobuf.Marshal(peer, new MarshalOptions { Deterministic = true });
        Assert.Equal(pluginBytes, reflectionBytes);

        var decodedViaReflection = OmniBufProtobuf.Unmarshal<PeerUpdate>(pluginBytes);
        Assert.Equal(peer, decodedViaReflection);

        var allTypes = new TestAllTypes
        {
            F14Float64 = -0.0,
            F3Sint32 = -12345,
            F2Uint64 = ulong.MaxValue,
            F7Bytes = ByteString.CopyFrom(0xDE, 0xAD, 0xBE, 0xEF),
            F15Fixed32 = 0x11223344u,
            F16Fixed64 = 0x0102030405060708UL,
            F20OverflowStr = "picked",
        };
        allTypes.F12MapStrUint["beta"] = 20;
        allTypes.F12MapStrUint["alpha"] = 10;

        byte[] pbReflWire = OmniBufProtobuf.Marshal(allTypes, new MarshalOptions { Deterministic = true, SelfDescribing = true });
        var decodedAll = OmniBufProtobuf.Unmarshal<TestAllTypes>(pbReflWire);
        Assert.Equal(0x11223344u, decodedAll.F15Fixed32);
        Assert.Equal("picked", decodedAll.F20OverflowStr);
        Assert.Equal(ulong.MaxValue, decodedAll.F2Uint64);
    }

    [Fact]
    public void AllTwelveMapKeyTypes_Wrappers_NestedTypes_AndCollisions_MatchAcrossPluginReflectionAndOpaque()
    {
        var env = new OuterEnvelope
        {
            WDouble = 0.0,
            WFloat = -0.0f,
            WInt64 = 0L,
            WUint64 = 0UL,
            WInt32 = 0,
            WUint32 = 0u,
            WBool = false,
            WString = string.Empty,
            WBytes = ByteString.Empty,
            Types_ = "types-val",
            Descriptor_ = 0,
            Parser_ = 77u,
            Equals_ = true,
            ToString_ = "str-val",
            GetHashCode_ = 999UL,
            OuterEnvelope_ = "self-named",
            Clone_ = "cloned-arm",
            SingleInner = new OuterEnvelope.Types.InnerPayload
            {
                Code = 10u,
                Detail = "initial",
                Mode = OuterEnvelope.Types.NestedMode.Fast,
            },
            F63Boundary = 630u,
            F64Boundary = 640u,
            F65High = "sixty-five",
            F90Sub = new OuterEnvelope.Types.InnerPayload
            {
                Code = 90u,
                Detail = "ninety",
                Mode = OuterEnvelope.Types.NestedMode.Strict,
            },
            F130Opt = 0,
        };

        env.MapI32Str[-10] = "neg";
        env.MapI32Str[20] = "pos";
        env.MapI64U32[-5000000000L] = 42u;
        env.MapU32Enum[7u] = AdvancedStatus.Paused;
        env.MapU64Msg[100UL] = new OuterEnvelope.Types.InnerPayload
        {
            Code = 1u,
            Detail = "map-child",
            Mode = OuterEnvelope.Types.NestedMode.Fast,
        };
        env.MapS32S64[-99] = -1234567890123L;
        env.MapS64Bool[-1L] = true;
        env.MapF32Flt[0x12345678u] = 3.25f;
        env.MapF64Dbl[0x0102030405060708UL] = -6.5;
        env.MapSf32Bytes[-42] = ByteString.CopyFrom(0xAA, 0xBB);
        env.MapSf64F64[-9999999999L] = 0xCAFEBABECAFEBABEUL;
        env.MapBoolStr[false] = "no";
        env.MapBoolStr[true] = "yes";
        env.MapStrWrap["zero"] = 0;
        env.MapStrWrap["pos"] = 123;

        env.RepStatus.Add(AdvancedStatus.Active);
        env.RepStatus.Add(AdvancedStatus.Paused);
        env.RepBool.Add(true);
        env.RepBool.Add(false);
        env.RepSint64.Add(-77L);
        env.RepDouble.Add(2.5);
        env.RepBytes.Add(ByteString.CopyFrom(1, 2));
        env.RepI32Wrap.Add(0);
        env.RepI32Wrap.Add(55);
        env.RepStrWrap.Add(string.Empty);
        env.RepStrWrap.Add("wrapped");
        env.RepInner.Add(new OuterEnvelope.Types.InnerPayload { Code = 5u, Detail = "item0" });

        byte[] pluginBytes = env.MarshalOmniBuf(new MarshalOptions { SelfDescribing = true, Deterministic = true });
        byte[] reflBytes = OmniBufProtobuf.Marshal(env, new MarshalOptions { SelfDescribing = true, Deterministic = true });
        Assert.Equal(pluginBytes, reflBytes);

        OuterEnvelope parsedPlugin = OuterEnvelope.ParseOmniBuf(pluginBytes);
        OuterEnvelope parsedRefl = OmniBufProtobuf.Unmarshal<OuterEnvelope>(pluginBytes);
        Assert.Equal(env, parsedPlugin);
        Assert.Equal(env, parsedRefl);

        // Zero-valued wrappers must remain non-null after round-trip
        Assert.Equal(0.0, parsedPlugin.WDouble);
        Assert.Equal(0, parsedPlugin.WInt32);
        Assert.Equal(false, parsedPlugin.WBool);
        Assert.Equal(string.Empty, parsedPlugin.WString);
        Assert.Equal(ByteString.Empty, parsedPlugin.WBytes);
        Assert.True(parsedPlugin.HasDescriptor_);
        Assert.Equal(0, parsedPlugin.Descriptor_);
        Assert.True(parsedPlugin.HasF130Opt);
        Assert.Equal(0, parsedPlugin.F130Opt);
        Assert.Equal(OuterEnvelope.CollisionChoiceOneofCase.Clone_, parsedPlugin.CollisionChoiceCase);

        // Verify OpaqueOuterEnvelope reads all map key types, wrappers, >64 tags, and propagates child mutations
        var opaque = new OpaqueOuterEnvelope(pluginBytes);
        Assert.Equal("neg", opaque.MapI32Str[-10]);
        Assert.Equal(42u, opaque.MapI64U32[-5000000000L]);
        Assert.Equal(AdvancedStatus.Paused, opaque.MapU32Enum[7u]);
        Assert.Equal("map-child", opaque.MapU64Msg[100UL].Detail);
        Assert.Equal(-1234567890123L, opaque.MapS32S64[-99]);
        Assert.True(opaque.MapS64Bool[-1L]);
        Assert.Equal(3.25f, opaque.MapF32Flt[0x12345678u]);
        Assert.Equal(-6.5, opaque.MapF64Dbl[0x0102030405060708UL]);
        Assert.Equal(ByteString.CopyFrom(0xAA, 0xBB), opaque.MapSf32Bytes[-42]);
        Assert.Equal(0xCAFEBABECAFEBABEUL, opaque.MapSf64F64[-9999999999L]);
        Assert.Equal("no", opaque.MapBoolStr[false]);
        Assert.Equal("yes", opaque.MapBoolStr[true]);
        Assert.Equal(0, opaque.MapStrWrap["zero"]);
        Assert.Equal(123, opaque.MapStrWrap["pos"]);
        Assert.Equal(0, opaque.WInt32);
        Assert.Equal(string.Empty, opaque.WString);
        Assert.True(opaque.HasF130Opt);
        Assert.Equal(0, opaque.F130Opt);
        Assert.Equal("sixty-five", opaque.F65High);

        // Mutate nested Opaque children in-place (singular, repeated, and map value)
        opaque.SingleInner!.Detail = "mutated-singular";
        opaque.RepInner[0].Detail = "mutated-list-elem";
        opaque.MapU64Msg[100UL].Detail = "mutated-map-val";
        opaque.F65High = "mutated-high-65";

        byte[] splicedSelfDesc = opaque.MarshalOmniBuf(new MarshalOptions { SelfDescribing = true, Deterministic = true });
        OuterEnvelope afterSplice = OuterEnvelope.ParseOmniBuf(splicedSelfDesc);
        Assert.Equal("mutated-singular", afterSplice.SingleInner.Detail);
        Assert.Equal("mutated-list-elem", afterSplice.RepInner[0].Detail);
        Assert.Equal("mutated-map-val", afterSplice.MapU64Msg[100UL].Detail);
        Assert.Equal("mutated-high-65", afterSplice.F65High);
        Assert.Equal(640u, afterSplice.F64Boundary);
    }

    [Fact]
    public void OneofAndOptionalZeroValues_PreservePresenceAndResetOnInstanceReuse()
    {
        // Zero-valued oneof arms (c_int = 0, c_str = "", c_bool = false) must retain their active ChoiceCase
        var zeroInt = new OuterEnvelope { CInt = 0 };
        byte[] zeroIntBytes = zeroInt.MarshalOmniBuf();
        Assert.NotEmpty(zeroIntBytes);
        Assert.Equal(zeroIntBytes, OmniBufProtobuf.Marshal(zeroInt));

        OuterEnvelope parsedZeroInt = OuterEnvelope.ParseOmniBuf(zeroIntBytes);
        Assert.Equal(OuterEnvelope.ChoiceOneofCase.CInt, parsedZeroInt.ChoiceCase);
        Assert.Equal(0, parsedZeroInt.CInt);

        var zeroStr = new OuterEnvelope { CStr = string.Empty };
        OuterEnvelope parsedZeroStr = OmniBufProtobuf.Unmarshal<OuterEnvelope>(zeroStr.MarshalOmniBuf());
        Assert.Equal(OuterEnvelope.ChoiceOneofCase.CStr, parsedZeroStr.ChoiceCase);
        Assert.Equal(string.Empty, parsedZeroStr.CStr);

        var zeroBool = new OuterEnvelope { CBool = false };
        OuterEnvelope parsedZeroBool = OuterEnvelope.ParseOmniBuf(OmniBufProtobuf.Marshal(zeroBool));
        Assert.Equal(OuterEnvelope.ChoiceOneofCase.CBool, parsedZeroBool.ChoiceCase);
        Assert.False(parsedZeroBool.CBool);

        // Unmarshaling an empty packet into a reused instance must clear previously active oneof and optional fields
        var reused = new OuterEnvelope
        {
            CInt = 42,
            OptInt = 0,
            OptBool = false,
            OptStr = string.Empty,
            OptStatus = AdvancedStatus.Unspecified,
        };
        byte[] withExplicitZeros = reused.MarshalOmniBuf();
        var checkOpt = OuterEnvelope.ParseOmniBuf(withExplicitZeros);
        Assert.True(checkOpt.HasOptInt);
        Assert.True(checkOpt.HasOptBool);
        Assert.True(checkOpt.HasOptStr);
        Assert.True(checkOpt.HasOptStatus);

        reused.UnmarshalOmniBuf(ReadOnlySpan<byte>.Empty);
        Assert.Equal(OuterEnvelope.ChoiceOneofCase.None, reused.ChoiceCase);
        Assert.False(reused.HasOptInt);
        Assert.False(reused.HasOptBool);
        Assert.False(reused.HasOptStr);
        Assert.False(reused.HasOptStatus);

        // Opaque oneof sibling clearing: switching from CInt to CStr drops the CInt wire field
        var op = new OpaqueOuterEnvelope(zeroIntBytes);
        Assert.True(op.HasCInt);
        Assert.False(op.HasCStr);
        op.CStr = string.Empty;
        Assert.False(op.HasCInt);
        Assert.True(op.HasCStr);
        OuterEnvelope switched = OuterEnvelope.ParseOmniBuf(op.MarshalOmniBuf());
        Assert.Equal(OuterEnvelope.ChoiceOneofCase.CStr, switched.ChoiceCase);
    }

    [Fact]
    public void WideMessageBeyond64Fields_RoundTripsCleanlyInPluginReflectionAndOpaque()
    {
        var wide = new WideMessage
        {
            F1 = 11u,
            F63 = 63u,
            F64 = 64u,
            F65 = "tag-65",
        };
        wide.F66.Add(100u);
        wide.F66.Add(200u);

        byte[] bytes = wide.MarshalOmniBuf();
        Assert.Equal(bytes, OmniBufProtobuf.Marshal(wide));

        WideMessage parsed = WideMessage.ParseOmniBuf(bytes);
        Assert.Equal(wide, parsed);

        var op = new OpaqueWideMessage(bytes);
        Assert.Equal(64u, op.F64);
        Assert.Equal("tag-65", op.F65);
        Assert.Equal(new uint[] { 100u, 200u }, op.F66);

        op.F64 = 6400u;
        op.F65 = "updated-65";
        WideMessage spliced = WideMessage.ParseOmniBuf(op.MarshalOmniBuf());
        Assert.Equal(11u, spliced.F1);
        Assert.Equal(63u, spliced.F63);
        Assert.Equal(6400u, spliced.F64);
        Assert.Equal("updated-65", spliced.F65);
        Assert.Equal(new uint[] { 100u, 200u }, spliced.F66);
    }

    [Fact]
    public void RecursiveSelfDescribingAndCycleDetection_PreserveNestedNamesAndRejectRuntimeCycles()
    {
        // 1. Annotated recursive TreeNode with SelfDescribing: true must preserve nested child field names in DecodeBlind
        var annotatedRoot = new AnnotatedTreeNode
        {
            Id = 1u,
            Name = "root",
            Weight = 5,
            Children = new List<AnnotatedTreeNode>
            {
                new()
                {
                    Id = 2u,
                    Name = "child",
                    Weight = -3,
                    Children = new List<AnnotatedTreeNode>
                    {
                        new() { Id = 3u, Name = "grandchild", Weight = 9 },
                    },
                },
            },
        };

        byte[] annotatedWire = OmniBufSerializer.Marshal(annotatedRoot, new MarshalOptions { SelfDescribing = true, Deterministic = true });
        Dictionary<string, object?> blindAnnotated = OmniBufBlind.DecodeBlind(annotatedWire);
        Assert.Equal("root", blindAnnotated["name"]);
        var level1List = Assert.IsType<List<object?>>(blindAnnotated["children"]);
        var level1Map = Assert.IsType<Dictionary<string, object?>>(level1List[0]);
        Assert.Equal("child", level1Map["name"]);
        var level2List = Assert.IsType<List<object?>>(level1Map["children"]);
        var level2Map = Assert.IsType<Dictionary<string, object?>>(level2List[0]);
        Assert.Equal("grandchild", level2Map["name"]);

        // Runtime reference cycle in AnnotatedTreeNode must throw OmniBufException
        var cyclicNode = new AnnotatedTreeNode { Id = 1u, Name = "loop" };
        cyclicNode.Children.Add(cyclicNode);
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Marshal(cyclicNode, new MarshalOptions { SelfDescribing = true }));
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Marshal(cyclicNode, new MarshalOptions { SelfDescribing = false }));

        // 2. Protobuf recursive ProtoTreeNode across Plugin, OmniBufProtobuf, and OpaqueProtoTreeNode
        var protoRoot = new ProtoTreeNode
        {
            Id = 10u,
            Name = "p-root",
            Primary = new ProtoTreeNode
            {
                Id = 11u,
                Name = "p-primary",
            },
        };
        protoRoot.Children.Add(new ProtoTreeNode { Id = 12u, Name = "p-child" });
        protoRoot.Branches["main"] = new ProtoTreeNode { Id = 13u, Name = "p-branch" };

        byte[] protoSelfDesc = protoRoot.MarshalOmniBuf(new MarshalOptions { SelfDescribing = true, Deterministic = true });
        byte[] reflSelfDesc = OmniBufProtobuf.Marshal(protoRoot, new MarshalOptions { SelfDescribing = true, Deterministic = true });
        Assert.Equal(protoSelfDesc, reflSelfDesc);

        Dictionary<string, object?> blindProto = OmniBufBlind.DecodeBlind(protoSelfDesc);
        Assert.Equal("p-root", blindProto["name"]);
        var primaryBlind = Assert.IsType<Dictionary<string, object?>>(blindProto["primary"]);
        Assert.Equal("p-primary", primaryBlind["name"]);
        var branchesBlind = Assert.IsType<Dictionary<string, object?>>(blindProto["branches"]);
        var mainBranchBlind = Assert.IsType<Dictionary<string, object?>>(branchesBlind["main"]);
        Assert.Equal("p-branch", mainBranchBlind["name"]);

        var opTree = new OpaqueProtoTreeNode(protoSelfDesc) { Name = "p-root-edited" };
        byte[] opSelfDesc = opTree.MarshalOmniBuf(new MarshalOptions { SelfDescribing = true, Deterministic = true });
        Dictionary<string, object?> blindOp = OmniBufBlind.DecodeBlind(opSelfDesc);
        Assert.Equal("p-root-edited", blindOp["name"]);
        Assert.Equal("p-primary", Assert.IsType<Dictionary<string, object?>>(blindOp["primary"])["name"]);

        // Runtime reference cycle in ProtoTreeNode must throw OmniBufException
        var cyclicProto = new ProtoTreeNode { Id = 1u, Name = "self" };
        cyclicProto.Primary = cyclicProto;
        Assert.Throws<OmniBufException>(() => cyclicProto.MarshalOmniBuf(new MarshalOptions { SelfDescribing = true }));
        Assert.Throws<OmniBufException>(() => OmniBufProtobuf.Marshal(cyclicProto, new MarshalOptions { SelfDescribing = true }));
    }

    [Fact]
    public void OmniBufProtobuf_AllowsMessageInFileDefiningFieldOptionsExtension()
    {
        Assert.True(FieldOptionsExtensionFileDescriptor.Extensions.UnorderedExtensions.Count > 0);

        var msg = new PlainExtensionFileMessage { Id = 77u };
        byte[] wire = OmniBufProtobuf.Marshal(msg);
        PlainExtensionFileMessage roundTripped = OmniBufProtobuf.Unmarshal<PlainExtensionFileMessage>(wire);
        Assert.Equal(77u, roundTripped.Id);
    }
}
