using System.Collections;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Omnibuf.Bench;
using Omnibuf.Conformance;
using OmniBuf;
using Xunit;

namespace OmniBuf.Tests;

public sealed class AuditRegressionsTests
{
    // --- CS-1 models ---
    [OmniBufMessage(TypeName = "SampleRecord")]
    private sealed record SampleRecord(
        [property: OmniBufField(1)] uint Id,
        [property: OmniBufField(2)] string Name);

    // --- CS-2a models ---
    [OmniBufMessage(TypeName = "ReadOnlyListCollectionExprModel")]
    private sealed class ReadOnlyListCollectionExprModel
    {
        [OmniBufField(1)]
        public IReadOnlyList<string> Tags { get; set; } = [];
    }

    // --- CS-2b models ---
    private sealed class PureReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly T[] _items;

        public PureReadOnlyList(params T[] items)
        {
            _items = items;
        }

        public int Count => _items.Length;

        public T this[int index] => _items[index];

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class PureReadOnlyDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _inner;

        public PureReadOnlyDictionary(Dictionary<TKey, TValue> inner)
        {
            _inner = inner;
        }

        public int Count => _inner.Count;

        public IEnumerable<TKey> Keys => _inner.Keys;

        public IEnumerable<TValue> Values => _inner.Values;

        public TValue this[TKey key] => _inner[key];

        public bool ContainsKey(TKey key) => _inner.ContainsKey(key);

        public bool TryGetValue(TKey key, out TValue value) => _inner.TryGetValue(key, out value!);

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [OmniBufMessage(TypeName = "PureReadOnlyContainersModel")]
    private sealed class PureReadOnlyContainersModel
    {
        [OmniBufField(1)]
        public IReadOnlyList<string> Tags { get; set; } = new List<string>();

        [OmniBufField(2)]
        public IReadOnlyDictionary<string, uint> Scores { get; set; } = new Dictionary<string, uint>(StringComparer.Ordinal);
    }

    // --- CS-5a & CS-5b models ---
    [OmniBufMessage(TypeName = "CtorWithDefault")]
    private sealed class CtorWithDefault
    {
        public CtorWithDefault([OmniBufField(1)] string name, int localVersion = 1)
        {
            Name = name;
            LocalVersion = localVersion;
        }

        public string Name { get; }

        public int LocalVersion { get; }
    }

    [OmniBufMessage(TypeName = "HybridCtorModel")]
    private sealed class HybridCtorModel
    {
        public HybridCtorModel([OmniBufField(1)] uint id)
        {
            Id = id;
        }

        public uint Id { get; }

        [OmniBufField(2)]
        public string Label { get; set; } = string.Empty;
    }

    // --- CS-8b dynamic Protobuf message in a file defining a FieldOptions extension ---
    private static readonly FileDescriptor Cs8FieldOptionsFileDescriptor = BuildCs8FieldOptionsFileDescriptor();

    private static FileDescriptorProto BuildCs8FileDescriptorProto()
    {
        var fileProto = new FileDescriptorProto
        {
            Name = "audit_field_options_ext.proto",
            Package = "omnibuf.audit",
            Syntax = "proto3",
        };
        fileProto.Dependency.Add("google/protobuf/descriptor.proto");

        var msgProto = new DescriptorProto { Name = "PlainAuditMsg" };
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
            Name = "audit_note",
            JsonName = "auditNote",
            Number = 50001,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.String,
            Extendee = ".google.protobuf.FieldOptions",
        });

        return fileProto;
    }

    private static FileDescriptor BuildCs8FieldOptionsFileDescriptor()
    {
        FileDescriptorProto fileProto = BuildCs8FileDescriptorProto();
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
                        typeof(PlainAuditMsg),
                        PlainAuditMsg.Parser,
                        new[] { nameof(PlainAuditMsg.Id) },
                        null,
                        null,
                        null,
                        null),
                }));
    }

    private sealed class PlainAuditMsg : IMessage<PlainAuditMsg>
    {
        public static MessageParser<PlainAuditMsg> Parser { get; } = new(() => new PlainAuditMsg());

        public uint Id { get; set; }

        public MessageDescriptor Descriptor => Cs8FieldOptionsFileDescriptor.MessageTypes[0];

        public int CalculateSize() => Id == 0 ? 0 : CodedOutputStream.ComputeTagSize(1) + CodedOutputStream.ComputeUInt32Size(Id);

        public PlainAuditMsg Clone() => new() { Id = Id };

        public bool Equals(PlainAuditMsg? other) => other is not null && Id == other.Id;

        public override bool Equals(object? obj) => obj is PlainAuditMsg other && Equals(other);

        public override int GetHashCode() => Id.GetHashCode();

        public void MergeFrom(PlainAuditMsg message)
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

    // --- CS-13 models ---
    [OmniBufMessage(TypeName = "Utf8StringMapModel")]
    private sealed class Utf8StringMapModel
    {
        [OmniBufField(1)]
        public Dictionary<string, ulong> Map { get; set; } = new(StringComparer.Ordinal);
    }

    [OmniBufMessage(TypeName = "UintKeyWireIntMapModel")]
    private sealed class UintKeyWireIntMapModel
    {
        [OmniBufField(1, KeyWire = OmniBufWireType.Uint)]
        public Dictionary<int, string> Map { get; set; } = new();
    }

    // --- CS-14 models ---
    [OmniBufMessage(TypeName = "DepthChainNode")]
    private sealed class DepthChainNode
    {
        [OmniBufField(1)]
        public DepthChainNode? Next { get; set; }

        [OmniBufField(2, OmitEmpty = true)]
        public List<DepthChainNode?> Children { get; set; } = new();
    }

    [Fact]
    public void CS1_InPlaceUnmarshalIntoPositionalRecord_ThrowsOmniBufExceptionInsteadOfNullReferenceException()
    {
        byte[] bytes = OmniBufSerializer.Marshal(new SampleRecord(42u, "updated"));
        var existingRecord = new SampleRecord(1u, "initial");

        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Unmarshal(bytes, existingRecord));
    }

    [Fact]
    public void CS2a_UnmarshalIntoReadOnlyListInitializedWithEmptyCollectionExpression_Succeeds()
    {
        var src = new ReadOnlyListCollectionExprModel
        {
            Tags = ["a", "b"],
        };
        byte[] bytes = OmniBufSerializer.Marshal(src);

        ReadOnlyListCollectionExprModel decoded = OmniBufSerializer.Unmarshal<ReadOnlyListCollectionExprModel>(bytes);
        Assert.Equal(["a", "b"], decoded.Tags);
    }

    [Fact]
    public void CS2b_MarshalCustomPureReadOnlyListAndDictionary_PreservesAllElements()
    {
        var src = new PureReadOnlyContainersModel
        {
            Tags = new PureReadOnlyList<string>("x", "y"),
            Scores = new PureReadOnlyDictionary<string, uint>(new Dictionary<string, uint>(StringComparer.Ordinal)
            {
                ["k1"] = 10u,
                ["k2"] = 20u,
            }),
        };

        byte[] bytes = OmniBufSerializer.Marshal(src);
        PureReadOnlyContainersModel decoded = OmniBufSerializer.Unmarshal<PureReadOnlyContainersModel>(bytes);

        Assert.Equal(2, decoded.Tags.Count);
        Assert.Equal(["x", "y"], decoded.Tags);
        Assert.Equal(2, decoded.Scores.Count);
    }

    [Fact]
    public void CS3_OpaqueTreeNodeSplicingAcrossMaxNestingDepth_FailsValidationAtDepth35()
    {
        // Build a valid child ProtoTreeNode chain of depth 30 (<= MaxNestingDepth 32).
        ProtoTreeNode childChain = BuildProtoTreeChain(edges: 30);
        byte[] childBytes = childChain.MarshalOmniBuf();
        _ = ProtoTreeNode.ParseOmniBuf(childBytes);

        // Build a parent ProtoTreeNode chain of depth 4 and splice childBytes as Primary at depth 5 -> total depth 35 (> 32).
        ProtoTreeNode parentChain = BuildProtoTreeChain(edges: 4);
        var rootOpaque = new OpaqueProtoTreeNode(parentChain.MarshalOmniBuf());
        OpaqueProtoTreeNode cursor = rootOpaque;
        for (int i = 0; i < 4; i++)
        {
            cursor = cursor.Primary!;
        }

        cursor.Primary = new OpaqueProtoTreeNode(childBytes);

        bool marshaledOk = rootOpaque.TryMarshal(out _, out _);
        Assert.False(marshaledOk);
    }

    [Fact]
    public void CS5a_UnmarshalCtorWithUnannotatedDefaultValueTypeParam_DoesNotThrowNullReferenceException()
    {
        var src = new CtorWithDefault("hello");
        byte[] bytes = OmniBufSerializer.Marshal(src);

        CtorWithDefault decoded = OmniBufSerializer.Unmarshal<CtorWithDefault>(bytes);
        Assert.Equal("hello", decoded.Name);
        Assert.Equal(1, decoded.LocalVersion);
    }

    [Fact]
    public void CS5b_UnmarshalClassWithParameterizedCtorAndMutableFieldProperty_PreservesMutableProperty()
    {
        var src = new HybridCtorModel(7u)
        {
            Label = "kept",
        };
        byte[] bytes = OmniBufSerializer.Marshal(src);

        HybridCtorModel decoded = OmniBufSerializer.Unmarshal<HybridCtorModel>(bytes);
        Assert.Equal(7u, decoded.Id);
        Assert.Equal("kept", decoded.Label);
    }

    [Fact]
    public void CS6_OpaqueOneofOnConcatenatedPackets_EnforcesExclusivityForOverriddenBranch()
    {
        byte[] pkt1 = new OuterEnvelope { CInt = 99 }.MarshalOmniBuf();
        byte[] pkt2 = new OuterEnvelope { CStr = "branch-b" }.MarshalOmniBuf();

        byte[] concat = new byte[pkt1.Length + pkt2.Length];
        pkt1.CopyTo(concat, 0);
        pkt2.CopyTo(concat.AsSpan(pkt1.Length));

        OuterEnvelope parsed = OuterEnvelope.ParseOmniBuf(concat);
        Assert.Equal(OuterEnvelope.ChoiceOneofCase.CStr, parsed.ChoiceCase);
        Assert.False(parsed.HasCInt);
        Assert.Equal(0, parsed.CInt);

        var opaque = new OpaqueOuterEnvelope(concat);
        Assert.True(opaque.HasCStr);
        Assert.Equal("branch-b", opaque.CStr);
        Assert.False(opaque.HasCInt);
        Assert.Equal(0, opaque.CInt);
    }

    [Fact]
    public void CS7_OmniBufOpaqueLocateFieldAndGetter_RejectCorruptField0Header()
    {
        // Construct Field #0 (0x19) with outer length 2 bytes (TZCNT varint 0x05) containing a truncated
        // inner varint [0x35, 0x02] inside SchemaMeta, followed by valid field #1 = 42.
        var enc = new OmniBufEncoder(32);
        enc.WriteRawByte(OmniBufConstants.Field0SubMsgTag);
        enc.WriteBytesValue(new byte[] { 0x35, 0x02 });
        enc.WriteUintField(1u, 42UL);
        byte[] pkt = enc.ToArray();

        // Full parser properly rejects the corrupt Field #0.
        Assert.Throws<OmniBufException>(() => PeerUpdate.ParseOmniBuf(pkt));

        // Opaque read helpers must not silently skip corrupt Field #0 and return field #1 = 42.
        bool located;
        try
        {
            located = OmniBufOpaque.LocateField(pkt, 1u, out _, out _);
        }
        catch (OmniBufException)
        {
            located = false;
        }

        uint asn;
        try
        {
            asn = new OpaquePeerUpdate(pkt).Asn;
        }
        catch (OmniBufException)
        {
            asn = 0u;
        }

        Assert.False(located);
        Assert.Equal(0u, asn);
    }

    [Fact]
    public void CS8a_OpaqueSubMsgCollectionSetter_HandlesNullElementLikeEncodeOmniBufBody()
    {
        var opaque = new OpaquePeerUpdate(Array.Empty<byte>());
        opaque.SetNeighbors(new Neighbor[] { null! });

        byte[] wire = opaque.MarshalOmniBuf();
        PeerUpdate parsed = PeerUpdate.ParseOmniBuf(wire);
        Assert.Single(parsed.Neighbors);
    }

    [Fact]
    public void CS8b_OmniBufProtobuf_AllowsMessageInFileDefiningFieldOptionsExtension()
    {
        Assert.True(Cs8FieldOptionsFileDescriptor.Extensions.UnorderedExtensions.Count > 0);

        var msg = new PlainAuditMsg { Id = 77u };
        byte[] wire = OmniBufProtobuf.Marshal(msg);
        PlainAuditMsg roundTripped = OmniBufProtobuf.Unmarshal<PlainAuditMsg>(wire);
        Assert.Equal(77u, roundTripped.Id);
    }

    [Fact]
    public void CS8c_ProtocGenOmniBufCs_AllowsFileDefiningFieldOptionsExtension()
    {
        FileDescriptorProto descriptorFile = FileDescriptorProto.Descriptor.File.ToProto();
        FileDescriptorProto targetFile = BuildCs8FileDescriptorProto();

        (string? err, List<(string Name, string Content)> files) = OmniBuf.ProtocGen.Program.GenerateCode(
            new[] { targetFile.Name },
            new[] { descriptorFile, targetFile });

        Assert.Null(err);
        Assert.Single(files);
    }

    [Fact]
    public void CS13a_DeterministicStringMap_SortsSupplementalAstralUtf8AfterBmpFullwidth()
    {
        // UTF-8 byte order:
        //   "\uE000_pua" -> EE 80 80 ...
        //   "！_ff01"    -> EF BC 81 ...
        //   "🚀_1f680"   -> F0 9F 9A 80 ...
        // UTF-16 StringComparer.Ordinal wrongly sorts surrogate pair "🚀_1f680" (0xD83D) before 0xE000 and 0xFF01.
        var model = new Utf8StringMapModel
        {
            Map = new Dictionary<string, ulong>(StringComparer.Ordinal)
            {
                ["\uE000_pua"] = 1UL,
                ["！_ff01"] = 2UL,
                ["🚀_1f680"] = 3UL,
            },
        };

        byte[] wire = OmniBufSerializer.Marshal(model, new MarshalOptions { Deterministic = true });
        List<string> keysOnWire = ReadStringUintMapKeysInWireOrder(wire);

        Assert.Equal(new[] { "\uE000_pua", "！_ff01", "🚀_1f680" }, keysOnWire);
    }

    [Fact]
    public void CS13b_DeterministicInt32MapWithUintKeyWire_SortsByUnsignedWireValue()
    {
        var model = new UintKeyWireIntMapModel
        {
            Map = new Dictionary<int, string>
            {
                [1] = "one",
                [-1] = "max_uint",
            },
        };

        byte[] wire = OmniBufSerializer.Marshal(model, new MarshalOptions { Deterministic = true });
        List<ulong> keysOnWire = ReadUintStringMapKeysInWireOrder(wire);

        Assert.Equal(2, keysOnWire.Count);
        Assert.True(keysOnWire[0] < keysOnWire[1], $"Expected unsigned key 1 before {keysOnWire[0]}, got [{keysOnWire[0]}, {keysOnWire[1]}]");
    }

    [Fact]
    public void CS14_MarshalNullSubMsgElementAtDepth31_ThrowsOmniBufExceptionInsteadOfEmittingDepth33Packet()
    {
        var leafAtDepth31 = new DepthChainNode
        {
            Children = new List<DepthChainNode?> { null },
        };

        DepthChainNode root = leafAtDepth31;
        for (int i = 0; i < 31; i++)
        {
            root = new DepthChainNode { Next = root };
        }

        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Marshal(root));
    }

    private static ProtoTreeNode BuildProtoTreeChain(int edges)
    {
        var node = new ProtoTreeNode { Id = 1u, Name = "leaf" };
        for (int i = 0; i < edges; i++)
        {
            node = new ProtoTreeNode
            {
                Id = (uint)(i + 2),
                Name = "n",
                Primary = node,
            };
        }

        return node;
    }

    private static List<string> ReadStringUintMapKeysInWireOrder(ReadOnlySpan<byte> packet)
    {
        int pos = 0;
        (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(packet, ref pos);
        Assert.Equal(1u, fnum);
        Assert.Equal(OmniBufWireType.Map, wt);

        ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(packet, ref pos, 3);
        Assert.True(OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType kWt, out OmniBufWireType vWt, out int count, out int p));
        Assert.Equal(OmniBufWireType.String, kWt);
        Assert.Equal(OmniBufWireType.Uint, vWt);

        var keys = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            keys.Add(OmniBufDecoder.ReadString(body, ref p));
            _ = OmniBufDecoder.ReadVarint(body, ref p);
        }

        return keys;
    }

    private static List<ulong> ReadUintStringMapKeysInWireOrder(ReadOnlySpan<byte> packet)
    {
        int pos = 0;
        (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(packet, ref pos);
        Assert.Equal(1u, fnum);
        Assert.Equal(OmniBufWireType.Map, wt);

        ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(packet, ref pos, 3);
        Assert.True(OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType kWt, out OmniBufWireType vWt, out int count, out int p));
        Assert.Equal(OmniBufWireType.Uint, kWt);
        Assert.Equal(OmniBufWireType.String, vWt);

        var keys = new List<ulong>(count);
        for (int i = 0; i < count; i++)
        {
            keys.Add(OmniBufDecoder.ReadVarint(body, ref p));
            _ = OmniBufDecoder.ReadString(body, ref p);
        }

        return keys;
    }
}
