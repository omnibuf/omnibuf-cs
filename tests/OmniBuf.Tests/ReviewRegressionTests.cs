using Google.Protobuf;
using Google.Protobuf.Reflection;
using Omnibuf.Bench;
using Omnibuf.Conformance;
using OmniBuf;
using OmniBuf.ConformanceRunner;
using Xunit;

namespace OmniBuf.Tests;

public sealed class ReviewRegressionTests
{
    private enum SamplePriority
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Negative = -3,
    }

    [OmniBufMessage(TypeName = "AnnotatedAdvancedSample")]
    private sealed class AnnotatedAdvancedSample
    {
        [OmniBufField(1, Name = "priority")]
        public SamplePriority Priority { get; set; }

        [OmniBufField(2, Name = "opt_count")]
        public int? OptCount { get; set; }

        [OmniBufField(3, Name = "opt_priority")]
        public SamplePriority? OptPriority { get; set; }

        [OmniBufField(4, Name = "matrix")]
        public List<List<int>> Matrix { get; set; } = new();

        [OmniBufField(5, Name = "grouped_tags")]
        public Dictionary<string, List<uint>> GroupedTags { get; set; } = new(StringComparer.Ordinal);

        [OmniBufField(6, Name = "fixed_items", Wire = OmniBufWireType.Fixed32)]
        public List<uint> FixedItems { get; set; } = new();

        [OmniBufField(7, Name = "fixed_map", KeyWire = OmniBufWireType.Sfixed32, ValueWire = OmniBufWireType.Fixed64)]
        public Dictionary<int, ulong> FixedMap { get; set; } = new();

        [OmniBufField(65, Name = "high_tag_note")]
        public string HighTagNote { get; set; } = string.Empty;
    }

    [OmniBufMessage(TypeName = "InvalidMapWirePoco")]
    private sealed class InvalidMapWirePoco
    {
        [OmniBufField(1, Wire = OmniBufWireType.Fixed64)]
        public Dictionary<int, ulong> BadMap { get; set; } = new();
    }

    private sealed class UnannotatedPoco
    {
        public int Value { get; set; }
    }

    [OmniBufMessage(TypeName = "EmptyAnnotatedPoco")]
    private sealed class EmptyAnnotatedPoco
    {
        public int IgnoredProperty { get; set; }
    }

    [Fact]
    public void MarshalOptions_DefaultIsDeterministicAndNonSelfDescribing()
    {
        MarshalOptions def = default;
        Assert.True(def.Deterministic);
        Assert.False(def.SelfDescribing);

        var constructed = new MarshalOptions();
        Assert.True(constructed.Deterministic);
        Assert.False(constructed.SelfDescribing);
    }

    [Fact]
    public void Encoder_ThreadLocalReentrancyGuardAndLargeBufferShrink()
    {
        OmniBufEncoder first = OmniBufEncoder.RentThreadLocal(out bool rentedFirst);
        Assert.True(rentedFirst);
        try
        {
            first.WriteUintField(1u, 42UL);

            // Nested rent while first is active must allocate an independent encoder rather than corrupting first
            OmniBufEncoder nested = OmniBufEncoder.RentThreadLocal(out bool rentedNested);
            Assert.False(rentedNested);
            Assert.NotSame(first, nested);
            nested.WriteStringField(2u, "nested");
            OmniBufEncoder.ReturnThreadLocal(nested, rentedNested);

            Assert.True(first.Length > 0);

            // Force capacity above 65,536 bytes and verify ReturnThreadLocal shrinks the retained buffer
            first.WriteStringField(3u, new string('z', 80_000));
            Assert.True(first.Capacity > 65_536);
        }
        finally
        {
            OmniBufEncoder.ReturnThreadLocal(first, rentedFirst);
        }

        OmniBufEncoder afterReturn = OmniBufEncoder.RentThreadLocal(out bool rentedAfter);
        try
        {
            Assert.True(rentedAfter);
            Assert.Same(first, afterReturn);
            Assert.Equal(512, afterReturn.Capacity);
        }
        finally
        {
            OmniBufEncoder.ReturnThreadLocal(afterReturn, rentedAfter);
        }
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

        // Mutate nested Opaque children in-place (singular, repeated, and map value) without re-assigning parent property
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
    public void OpaqueFailClosed_ThrowsOnCorruptFramingAndRejectsUntouchedCorruptSubMsgInTryMarshal()
    {
        // Truncated outer varint header must throw OmniBufException on Opaque scalar read rather than returning 0
        byte[] truncatedPacket = { 0x12 }; // 2-byte varint marker with missing second byte
        var badOpaque = new OpaquePeerUpdate(truncatedPacket);
        Assert.Throws<OmniBufException>(() => _ = badOpaque.Asn);
        Assert.False(badOpaque.TryMarshal(out _, out string? errTrunc));
        Assert.NotNull(errTrunc);

        // Packet with valid outer framing for field #9 (Location SubMsg) containing invalid inner UTF-8 string
        var innerCorrupt = new OmniBufEncoder(32);
        innerCorrupt.WriteTag(1u, OmniBufWireType.String);
        innerCorrupt.WriteBytesValue(new byte[] { 0xFF, 0xFE }); // invalid UTF-8 inside Location.region

        var outer = new OmniBufEncoder(64);
        outer.WriteUintField(1u, 64512UL);
        outer.WriteTag(9u, OmniBufWireType.SubMsg);
        outer.WriteBytesValue(innerCorrupt.WrittenSpan);
        outer.WriteUintField(11u, 5UL);
        byte[] outerBytes = outer.ToArray();

        var op = new OpaquePeerUpdate(outerBytes)
        {
            Sequence = 6UL,
        };

        Assert.False(op.TryMarshal(out _, out string? innerErr));
        Assert.NotNull(innerErr);
        Assert.Throws<OmniBufException>(() => op.MarshalValidated());
    }

    [Fact]
    public void AnnotatedSerializer_SupportsEnumsNullablesNestedCollectionsExplicitWireAndRejectsInvalidRootTypes()
    {
        var sample = new AnnotatedAdvancedSample
        {
            Priority = SamplePriority.Negative,
            OptCount = 0, // explicit zero nullable must be emitted on wire
            OptPriority = SamplePriority.Low, // explicit zero enum nullable must be emitted on wire
            Matrix = new List<List<int>>
            {
                new() { -1, 0, 2 },
                new() { 10, 20 },
            },
            GroupedTags = new Dictionary<string, List<uint>>(StringComparer.Ordinal)
            {
                ["alpha"] = new List<uint> { 1u, 2u },
                ["beta"] = new List<uint>(),
            },
            FixedItems = new List<uint> { 0x11223344u, 0xAABBCCDDu },
            FixedMap = new Dictionary<int, ulong> { [-5] = 0x0102030405060708UL },
            HighTagNote = "beyond-64",
        };

        byte[] wire = OmniBufSerializer.Marshal(sample, new MarshalOptions { SelfDescribing = true, Deterministic = true });
        AnnotatedAdvancedSample decoded = OmniBufSerializer.Unmarshal<AnnotatedAdvancedSample>(wire);

        Assert.Equal(SamplePriority.Negative, decoded.Priority);
        Assert.Equal(0, decoded.OptCount);
        Assert.Equal(SamplePriority.Low, decoded.OptPriority);
        Assert.Equal(2, decoded.Matrix.Count);
        Assert.Equal(new[] { -1, 0, 2 }, decoded.Matrix[0]);
        Assert.Equal(new uint[] { 1u, 2u }, decoded.GroupedTags["alpha"]);
        Assert.Empty(decoded.GroupedTags["beta"]);
        Assert.Equal(new uint[] { 0x11223344u, 0xAABBCCDDu }, decoded.FixedItems);
        Assert.Equal(0x0102030405060708UL, decoded.FixedMap[-5]);
        Assert.Equal("beyond-64", decoded.HighTagNote);

        // Nullables left null remain null after round-trip
        var nullSample = new AnnotatedAdvancedSample { OptCount = null, OptPriority = null };
        AnnotatedAdvancedSample decodedNulls = OmniBufSerializer.Unmarshal<AnnotatedAdvancedSample>(OmniBufSerializer.Marshal(nullSample));
        Assert.Null(decodedNulls.OptCount);
        Assert.Null(decodedNulls.OptPriority);

        // Primitives, strings, collections, and POCOs without [OmniBufField]/[OmniBufMessage] must throw OmniBufException,
        // while an intentional empty [OmniBufMessage] encodes to 0 bytes.
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Marshal(123));
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Marshal("raw"));
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Marshal(new List<int> { 1, 2 }));
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Marshal(new UnannotatedPoco { Value = 1 }));
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Marshal(new InvalidMapWirePoco()));
        Assert.Empty(OmniBufSerializer.Marshal(new EmptyAnnotatedPoco { IgnoredProperty = 1 }));
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

        // Runtime reference cycle in AnnotatedTreeNode must throw OmniBufException rather than overflowing the stack
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
    public void BlindDecoder_RejectsConflictingNestedArrayElementTypesAndTruncatedFirstSubMsgSegment()
    {
        // 1. Concatenated array<array<uint>> followed by array<array<string>> on field #1 must throw OmniBufException
        var nestedConflict = new OmniBufEncoder(64);
        nestedConflict.BeginArrayField(1u, OmniBufWireType.Array, 1);
        nestedConflict.BeginContainer();
        nestedConflict.WriteRawByte((byte)OmniBufWireType.Uint);
        nestedConflict.WriteVarint(1UL);
        nestedConflict.WriteUintValue(7UL);
        nestedConflict.EndContainer();
        nestedConflict.EndArray();

        nestedConflict.BeginArrayField(1u, OmniBufWireType.Array, 1);
        nestedConflict.BeginContainer();
        nestedConflict.WriteRawByte((byte)OmniBufWireType.String);
        nestedConflict.WriteVarint(1UL);
        nestedConflict.WriteStringValue("bad");
        nestedConflict.EndContainer();
        nestedConflict.EndArray();

        byte[] nestedConflictBytes = nestedConflict.ToArray();
        Assert.Throws<OmniBufException>(() => OmniBufBlind.DecodeBlind(nestedConflictBytes));

        // 2. Truncated first WireSubMsg segment followed by second WireSubMsg segment that accidentally completes a varint
        var segSplit = new OmniBufEncoder(64);
        segSplit.WriteTag(1u, OmniBufWireType.SubMsg);
        segSplit.WriteBytesValue(new byte[] { 0x09, 0x12 }); // Tag(field=1, Uint) + incomplete 2-byte varint first byte
        segSplit.WriteTag(1u, OmniBufWireType.SubMsg);
        segSplit.WriteBytesValue(new byte[] { 0x00 }); // Would complete varint if concatenated blindly without per-segment validation
        byte[] segSplitBytes = segSplit.ToArray();

        Assert.Throws<OmniBufException>(() => OmniBufBlind.DecodeBlind(segSplitBytes));
        Assert.Throws<OmniBufException>(() => OmniBufOpaque.TryReadSubMsgBytes(segSplitBytes, 1u, out _));
    }

    [Fact]
    public void SchemaMeta_DuplicateChildKeyInSameMapReplacesWhileSeparateBlocksMerge_AndEmptyNameClearsInheritedName()
    {
        // Build Field #0 with two Field #3 blocks:
        // Block 1 has duplicate key 1 (first maps field 1->"stale", second maps field 1->"replaced", field 2->"kept")
        // Block 2 merges onto key 1 and sets field 1->"" (empty name clears inherited "replaced" back to "field_1")
        var f0 = new OmniBufEncoder(128);
        f0.WriteRawByte(OmniBufConstants.Field0SubMsgTag);
        f0.BeginContainer();

        // First Field #3 map with count=2 and duplicate key 1u
        f0.BeginMapField(3u, OmniBufWireType.Uint, OmniBufWireType.SubMsg, 2);
        f0.WriteUintValue(1u);
        f0.BeginContainer();
        f0.BeginMapField(2u, OmniBufWireType.Uint, OmniBufWireType.String, 2);
        f0.WriteUintValue(1u);
        f0.WriteStringValue("stale");
        f0.WriteUintValue(3u);
        f0.WriteStringValue("leaked_from_duplicate");
        f0.EndMap();
        f0.EndContainer();

        f0.WriteUintValue(1u);
        f0.BeginContainer();
        f0.BeginMapField(2u, OmniBufWireType.Uint, OmniBufWireType.String, 2);
        f0.WriteUintValue(1u);
        f0.WriteStringValue("replaced");
        f0.WriteUintValue(2u);
        f0.WriteStringValue("kept");
        f0.EndMap();
        f0.EndContainer();
        f0.EndMap();

        // Second Field #3 map block merges into child 1u and clears field 1 name with ""
        f0.BeginMapField(3u, OmniBufWireType.Uint, OmniBufWireType.SubMsg, 1);
        f0.WriteUintValue(1u);
        f0.BeginContainer();
        f0.BeginMapField(2u, OmniBufWireType.Uint, OmniBufWireType.String, 1);
        f0.WriteUintValue(1u);
        f0.WriteStringValue(string.Empty);
        f0.EndMap();
        f0.EndContainer();
        f0.EndMap();

        f0.EndContainer();

        // SubMsg on field #1 containing inner fields #1, #2, #3
        f0.BeginSubMsgField(1u);
        f0.WriteUintField(1u, 10UL);
        f0.WriteUintField(2u, 20UL);
        f0.WriteUintField(3u, 30UL);
        f0.EndSubMsg();

        Dictionary<string, object?> decoded = OmniBufBlind.DecodeBlind(f0.ToArray());
        var child = Assert.IsType<Dictionary<string, object?>>(decoded["field_1"]);

        // Field 1 was cleared by "" in block 2 -> falls back to "field_1"
        Assert.Equal(10UL, child["field_1"]);
        // Field 2 from the replacement entry in block 1 was preserved across block 2 merge
        Assert.Equal(20UL, child["kept"]);
        // Field 3 from the overwritten first duplicate key in block 1 must NOT leak
        Assert.False(child.ContainsKey("leaked_from_duplicate"));
        Assert.Equal(30UL, child["field_3"]);
    }

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

    [OmniBufMessage(TypeName = "LimitTestPoco")]
    private sealed class LimitTestPoco
    {
        [OmniBufField(1, Name = "items")]
        public List<uint> Items { get; set; } = new();

        [OmniBufField(2, Name = "child")]
        public LimitTestPoco? Child { get; set; }

        [OmniBufField(3, Name = "labels")]
        public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);
    }

    [Fact]
    public void UnmarshalOptions_DefaultsAndExplicitZero()
    {
        UnmarshalOptions def = default;
        Assert.Equal(UnmarshalOptions.DefaultMaxMessageBytes, def.MaxMessageBytes);
        Assert.Equal(UnmarshalOptions.DefaultMaxCollectionElements, def.MaxCollectionElements);
        Assert.Equal(UnmarshalOptions.DefaultMaxNestingDepth, def.MaxNestingDepth);
        Assert.Equal(def, new UnmarshalOptions());

        var zeroDepth = new UnmarshalOptions { MaxNestingDepth = 0 };
        Assert.Equal(0, zeroDepth.MaxNestingDepth);
        Assert.Equal(UnmarshalOptions.DefaultMaxMessageBytes, zeroDepth.MaxMessageBytes);
        Assert.Equal(UnmarshalOptions.DefaultMaxCollectionElements, zeroDepth.MaxCollectionElements);

        var custom = new UnmarshalOptions(MaxMessageBytes: 128, MaxCollectionElements: 5, MaxNestingDepth: 2);
        Assert.Equal(128, custom.MaxMessageBytes);
        Assert.Equal(5, custom.MaxCollectionElements);
        Assert.Equal(2, custom.MaxNestingDepth);
    }

    [Fact]
    public void UnmarshalOptions_EnforcesMessageBytesCollectionElementsAndDepthAcrossDecoders()
    {
        var poco = new LimitTestPoco
        {
            Items = new List<uint> { 1, 2, 3, 4 },
            Child = new LimitTestPoco
            {
                Child = new LimitTestPoco { Items = new List<uint> { 9 } },
            },
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["a"] = "1",
                ["b"] = "2",
                ["c"] = "3",
            },
        };
        byte[] packet = OmniBufSerializer.Marshal(poco, new MarshalOptions(SelfDescribing: true));

        // 1. MaxMessageBytes
        var tinyBytes = new UnmarshalOptions { MaxMessageBytes = packet.Length - 1 };
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Unmarshal<LimitTestPoco>(packet, tinyBytes));
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Unmarshal(packet, new LimitTestPoco(), tinyBytes));
        Assert.Throws<OmniBufException>(() => OmniBufDecoder.ValidateMessage(packet, tinyBytes));
        Assert.Throws<OmniBufException>(() => OmniBufBlind.DecodeBlind(packet, tinyBytes));
        Assert.Throws<OmniBufException>(() => OmniBufBlind.DecodeBlindJson(packet, tinyBytes));
        Assert.Throws<OmniBufException>(() => SchemaMeta.ReadField0(packet, tinyBytes));
        Assert.Throws<OmniBufException>(() => _ = new OmniBufDecoder(packet, tinyBytes));

        // 2. MaxCollectionElements
        var tinyCol = new UnmarshalOptions { MaxCollectionElements = 2 };
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Unmarshal<LimitTestPoco>(packet, tinyCol));
        Assert.Throws<OmniBufException>(() => OmniBufDecoder.ValidateMessage(packet, tinyCol));
        Assert.Throws<OmniBufException>(() => OmniBufBlind.DecodeBlind(packet, tinyCol));
        Assert.Throws<OmniBufException>(() => SchemaMeta.ReadField0(packet, tinyCol));

        // 3. MaxNestingDepth
        var tinyDepth = new UnmarshalOptions { MaxNestingDepth = 1 };
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Unmarshal<LimitTestPoco>(packet, tinyDepth));
        Assert.Throws<OmniBufException>(() => OmniBufDecoder.ValidateMessage(packet, tinyDepth));
        Assert.Throws<OmniBufException>(() => OmniBufBlind.DecodeBlind(packet, tinyDepth));

        // 4. OmniBufProtobuf limits
        var peer = new PeerUpdate();
        peer.Tags.Add("a");
        peer.Tags.Add("b");
        peer.Tags.Add("c");
        byte[] peerBytes = OmniBufProtobuf.Marshal(peer);
        Assert.Throws<OmniBufException>(() => OmniBufProtobuf.Unmarshal<PeerUpdate>(peerBytes, new UnmarshalOptions { MaxMessageBytes = 1 }));
        Assert.Throws<OmniBufException>(() => OmniBufProtobuf.Unmarshal<PeerUpdate>(peerBytes, new UnmarshalOptions { MaxCollectionElements = 2 }));
        Assert.Throws<OmniBufException>(() => OmniBufProtobuf.Unmarshal<PeerUpdate>(peerBytes, new UnmarshalOptions { MaxNestingDepth = 0 }));

        // Sufficient limits succeed
        var generous = new UnmarshalOptions(MaxMessageBytes: 4096, MaxCollectionElements: 10, MaxNestingDepth: 16);
        LimitTestPoco roundTripped = OmniBufSerializer.Unmarshal<LimitTestPoco>(packet, generous);
        Assert.Equal(4, roundTripped.Items.Count);
        PeerUpdate peerOut = OmniBufProtobuf.Unmarshal<PeerUpdate>(peerBytes, generous);
        Assert.Equal(3, peerOut.Tags.Count);
    }

    [Fact]
    public void Encoder_BeginContainer_EnforcesMaxNestingDepth()
    {
        var enc = new OmniBufEncoder();
        for (int i = 0; i < OmniBufConstants.MaxNestingDepth; i++)
        {
            enc.BeginContainer();
        }

        Assert.Throws<OmniBufException>(() => enc.BeginContainer());
        Assert.Throws<OmniBufException>(() => enc.BeginContainer(sizeHint: 32_768));

        for (int i = 0; i < OmniBufConstants.MaxNestingDepth; i++)
        {
            enc.EndContainer();
        }

        Assert.Equal(0, enc.ContainerDepth);
    }

    [Fact]
    public void Opaque_TryReadStringSpan_ValidatesUtf8ZeroCopyAndMergesSubMsgs()
    {
        var enc = new OmniBufEncoder();
        enc.WriteStringField(1u, "hello-utf8-\u00E9");
        enc.BeginSubMsgField(2u);
        enc.WriteUintField(1u, 10UL);
        enc.EndSubMsg();
        enc.BeginSubMsgField(2u);
        enc.WriteUintField(2u, 20UL);
        enc.EndSubMsg();
        byte[] validPacket = enc.ToArray();

        Assert.True(OmniBufOpaque.TryReadStringSpan(validPacket, 1u, out ReadOnlySpan<byte> utf8Span));
        Assert.Equal("hello-utf8-\u00E9", System.Text.Encoding.UTF8.GetString(utf8Span));

        Assert.True(OmniBufOpaque.TryReadSubMsgSpan(validPacket, 2u, out ReadOnlySpan<byte> mergedSub));
        Assert.True(OmniBufOpaque.TryReadUint(mergedSub, 1u, out ulong v1));
        Assert.True(OmniBufOpaque.TryReadUint(mergedSub, 2u, out ulong v2));
        Assert.Equal(10UL, v1);
        Assert.Equal(20UL, v2);

        // Craft invalid UTF-8 in a WireString field
        var badEnc = new OmniBufEncoder();
        badEnc.WriteTag(1u, OmniBufWireType.String);
        badEnc.WriteBytesValue(new byte[] { 0xFF, 0xFE });
        byte[] badPacket = badEnc.ToArray();

        Assert.Throws<OmniBufException>(() => OmniBufOpaque.TryReadStringSpan(badPacket, 1u, out _));
    }

    [Fact]
    public void Wrappers_ReadElem_EnforcesThreadedDepth()
    {
        var enc = new OmniBufEncoder();
        enc.BeginContainer();
        OmniBufWrappers.WriteInt32Body(enc, 42);
        enc.EndContainer();
        byte[] elemBytes = enc.ToArray();

        int pos = 0;
        Assert.Equal(42, OmniBufWrappers.ReadElemInt32(elemBytes, ref pos, depth: 5, maxDepth: 10));

        int pos2 = 0;
        Assert.Throws<OmniBufException>(() => OmniBufWrappers.ReadElemInt32(elemBytes, ref pos2, depth: 11, maxDepth: 10));
    }
}
