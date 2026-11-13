using Google.Protobuf;
using Omnibuf.Bench;
using Omnibuf.Conformance;
using OmniBuf;
using OmniBuf.ConformanceRunner;
using Xunit;

namespace OmniBuf.Tests;

public sealed class SerializerAndProtobufTests
{
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
}
