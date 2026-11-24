using System.Collections;
using OmniBuf;
using OmniBuf.ConformanceRunner;
using Xunit;

namespace OmniBuf.Tests;

public sealed class SerializerTests
{
    private enum SamplePriority
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Negative = -3,
    }

    [OmniBufMessage(TypeName = "TelemetryRecord")]
    private sealed record TelemetryRecord(
        [property: OmniBufField(1, Name = "sensor_id")] string SensorId,
        [property: OmniBufField(2, Name = "temp_c")] double TempC,
        [property: OmniBufField(3, Name = "seq", Wire = OmniBufWireType.Fixed64)] ulong Seq,
        [property: OmniBufField(4, Name = "online")] bool Online);

    [OmniBufMessage(TypeName = "CompactPoint")]
    private struct CompactPoint
    {
        [OmniBufField(1, Name = "x", Wire = OmniBufWireType.Sfixed32)]
        public int X { get; set; }

        [OmniBufField(2, Name = "y", Wire = OmniBufWireType.Sfixed32)]
        public int Y { get; set; }
    }

    [OmniBufMessage(TypeName = "OmitEmptySample")]
    private sealed class OmitEmptySample
    {
        [OmniBufField(1, Name = "id", OmitEmpty = true)]
        public uint Id { get; set; }

        [OmniBufField(2, Name = "label", OmitEmpty = true)]
        public string Label { get; set; } = string.Empty;

        [OmniBufField(3, Name = "items", OmitEmpty = true)]
        public List<uint> Items { get; set; } = new();
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

    [OmniBufMessage(TypeName = "PositionalRecordSample")]
    private sealed record PositionalRecordSample(
        [property: OmniBufField(1)] uint Id,
        [property: OmniBufField(2)] string Name);

    [OmniBufMessage(TypeName = "ReadOnlyListCollectionExprModel")]
    private sealed class ReadOnlyListCollectionExprModel
    {
        [OmniBufField(1)]
        public IReadOnlyList<string> Tags { get; set; } = [];
    }

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

    [OmniBufMessage(TypeName = "DepthChainNode")]
    private sealed class DepthChainNode
    {
        [OmniBufField(1)]
        public DepthChainNode? Next { get; set; }

        [OmniBufField(2, OmitEmpty = true)]
        public List<DepthChainNode?> Children { get; set; } = new();
    }

    [OmniBufMessage(TypeName = "InnerMutableStruct")]
    private struct InnerMutableStruct
    {
        [OmniBufField(1)]
        public uint A { get; set; }

        [OmniBufField(2)]
        public string B { get; set; }
    }

    [OmniBufMessage(TypeName = "OuterWithStructRecord")]
    private sealed record OuterWithStructRecord(
        [property: OmniBufField(1)] InnerMutableStruct Inner);

    [OmniBufMessage(TypeName = "NestedStringArrayModel")]
    private sealed class NestedStringArrayModel
    {
        [OmniBufField(1)]
        public string[][] Matrix { get; set; } = Array.Empty<string[]>();
    }

    [Fact]
    public void AnnotatedSerializer_RoundTripsPositionalRecordAndSelfDescribingField0()
    {
        var original = new TelemetryRecord("fra-sensor-9", -14.25, 0x0102030405060708UL, true);
        byte[] wire = OmniBufSerializer.Marshal(original, new MarshalOptions { SelfDescribing = true, Deterministic = true });

        TelemetryRecord decoded = OmniBufSerializer.Unmarshal<TelemetryRecord>(wire);
        Assert.Equal(original, decoded);

        Dictionary<string, object?>? blind = OmniBufBlind.DecodeBlind(wire);
        Assert.NotNull(blind);
        Assert.Equal("fra-sensor-9", blind!["sensor_id"]);
        Assert.Equal(-14.25, blind["temp_c"]);
        Assert.Equal(0x0102030405060708UL, blind["seq"]);
        Assert.Equal(true, blind["online"]);
    }

    [Fact]
    public void AnnotatedSerializer_RoundTripsValueStruct()
    {
        var pt = new CompactPoint { X = -120, Y = 450 };
        byte[] wire = OmniBufSerializer.Marshal(pt);
        CompactPoint decoded = OmniBufSerializer.Unmarshal<CompactPoint>(wire);
        Assert.Equal(-120, decoded.X);
        Assert.Equal(450, decoded.Y);
    }

    [Fact]
    public void AnnotatedSerializer_OmitEmptyDropsZeroFieldsAndResetsReusedInstance()
    {
        var emptySample = new OmitEmptySample();
        byte[] emptyWire = OmniBufSerializer.Marshal(emptySample);
        Assert.Empty(emptyWire);

        var target = new OmitEmptySample
        {
            Id = 99,
            Label = "stale",
            Items = new List<uint> { 1, 2, 3 },
        };
        OmniBufSerializer.Unmarshal(emptyWire, target);
        Assert.Equal(0u, target.Id);
        Assert.Equal(string.Empty, target.Label);
        Assert.Empty(target.Items);
    }

    [Fact]
    public void AnnotatedSerializer_ConcatenatedPacketsMergePositionalRecordSubMsg()
    {
        var first = new AnnotatedTestAllTypes
        {
            F8SubMsg = new AnnotatedAllTypesSubMsg(42, "alpha", true, -100L),
            F9StringArr = new List<string> { "one" },
        };
        var second = new AnnotatedTestAllTypes
        {
            F8SubMsg = new AnnotatedAllTypesSubMsg(42, "beta", true, 500L),
            F9StringArr = new List<string> { "two" },
        };

        byte[] b1 = OmniBufSerializer.Marshal(first);
        byte[] b2 = OmniBufSerializer.Marshal(second);
        byte[] concat = new byte[b1.Length + b2.Length];
        b1.CopyTo(concat, 0);
        b2.CopyTo(concat.AsSpan(b1.Length));

        AnnotatedTestAllTypes merged = OmniBufSerializer.Unmarshal<AnnotatedTestAllTypes>(concat);
        Assert.NotNull(merged.F8SubMsg);
        Assert.Equal(42u, merged.F8SubMsg!.Id);
        Assert.Equal("beta", merged.F8SubMsg.Label);
        Assert.Equal(500L, merged.F8SubMsg.Score);
        Assert.Equal(new[] { "one", "two" }, merged.F9StringArr);
    }

    [Fact]
    public void AnnotatedSerializer_HandlesRecursiveTreeNodeAndDeterministicMapOrder()
    {
        var root = new AnnotatedTreeNode
        {
            Id = 1,
            Name = "root",
            Weight = 10,
            Children = new List<AnnotatedTreeNode>
            {
                new() { Id = 2, Name = "left", Weight = -5 },
                new()
                {
                    Id = 3,
                    Name = "right",
                    Weight = 15,
                    Children = new List<AnnotatedTreeNode>
                    {
                        new() { Id = 4, Name = "right-leaf", Weight = 20 },
                    },
                },
            },
        };

        byte[] wire = OmniBufSerializer.Marshal(root, new MarshalOptions { SelfDescribing = true, Deterministic = true });
        AnnotatedTreeNode roundTripped = OmniBufSerializer.Unmarshal<AnnotatedTreeNode>(wire);
        Assert.Equal("root", roundTripped.Name);
        Assert.Equal(2, roundTripped.Children.Count);
        Assert.Equal("right-leaf", roundTripped.Children[1].Children[0].Name);

        var m1 = new AnnotatedTestAllTypes();
        m1.F12MapStrUint["z"] = 1;
        m1.F12MapStrUint["a"] = 2;
        m1.F12MapStrUint["m"] = 3;

        var m2 = new AnnotatedTestAllTypes();
        m2.F12MapStrUint["a"] = 2;
        m2.F12MapStrUint["m"] = 3;
        m2.F12MapStrUint["z"] = 1;

        byte[] det1 = OmniBufSerializer.Marshal(m1, new MarshalOptions { Deterministic = true });
        byte[] det2 = OmniBufSerializer.Marshal(m2, new MarshalOptions { Deterministic = true });
        Assert.Equal(det1, det2);
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
    public void AnnotatedSerializer_InPlaceUnmarshalIntoPositionalRecord_ThrowsOmniBufException()
    {
        byte[] bytes = OmniBufSerializer.Marshal(new PositionalRecordSample(42u, "updated"));
        var existingRecord = new PositionalRecordSample(1u, "initial");

        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Unmarshal(bytes, existingRecord));
    }

    [Fact]
    public void AnnotatedSerializer_UnmarshalIntoReadOnlyListInitializedWithEmptyCollectionExpression_Succeeds()
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
    public void AnnotatedSerializer_MarshalCustomPureReadOnlyListAndDictionary_PreservesAllElements()
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
    public void AnnotatedSerializer_UnmarshalCtorWithUnannotatedDefaultValueTypeParam_UsesDefaultValue()
    {
        var src = new CtorWithDefault("hello");
        byte[] bytes = OmniBufSerializer.Marshal(src);

        CtorWithDefault decoded = OmniBufSerializer.Unmarshal<CtorWithDefault>(bytes);
        Assert.Equal("hello", decoded.Name);
        Assert.Equal(1, decoded.LocalVersion);
    }

    [Fact]
    public void AnnotatedSerializer_UnmarshalClassWithParameterizedCtorAndMutableProperty_PreservesMutableProperty()
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
    public void AnnotatedSerializer_DeterministicStringMap_SortsSupplementalAstralUtf8AfterBmpFullwidth()
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
    public void AnnotatedSerializer_DeterministicInt32MapWithUintKeyWire_SortsByUnsignedWireValue()
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
    public void AnnotatedSerializer_MarshalNullSubMsgElementAtDepth31_ThrowsOmniBufException()
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

    [Fact]
    public void AnnotatedSerializer_DepthLimitExceeded_LeavesNoUnclosedEncoderScope()
    {
        // Depth limit throws before BeginSubMsgField at depth 32, so ContainerDepth never exceeds MaxNestingDepth
        DepthChainNode root = new();
        for (int i = 0; i < 33; i++)
        {
            root = new DepthChainNode { Next = root };
        }

        var callerEnc = new OmniBufEncoder(64);
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Marshal(callerEnc, root));
        Assert.Equal(OmniBufConstants.MaxNestingDepth, callerEnc.ContainerDepth);
    }

    [Fact]
    public void AnnotatedSerializer_NonNullableStructSubMsg_MergesAcrossConcatenatedOccurrences()
    {
        var enc = new OmniBufEncoder(64);
        enc.BeginSubMsgField(1u);
        enc.WriteUintField(1u, 42UL);
        enc.EndSubMsg();
        enc.BeginSubMsgField(1u);
        enc.WriteStringField(2u, "merged");
        enc.EndSubMsg();

        OuterWithStructRecord parsed = OmniBufSerializer.Unmarshal<OuterWithStructRecord>(enc.ToArray());
        Assert.Equal(42u, parsed.Inner.A);
        Assert.Equal("merged", parsed.Inner.B);
    }

    [Fact]
    public void AnnotatedSerializer_NestedVariableWidthArray_RejectsTruncatedCountWithoutPreAllocation()
    {
        var enc = new OmniBufEncoder(32);
        enc.BeginArrayField(1u, OmniBufWireType.Array, 1);
        enc.BeginArrayElement(OmniBufWireType.String, 100_000_000);
        enc.WriteStringValue("only-one");
        enc.EndArray();
        enc.EndArray();
        byte[] malformed = enc.ToArray();

        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Unmarshal<NestedStringArrayModel>(malformed));
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
