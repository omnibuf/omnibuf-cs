using System.Buffers;
using System.Text.Json;
using Google.Protobuf;
using Omnibuf.Bench;
using Omnibuf.Conformance;
using OmniBuf;
using Xunit;

namespace OmniBuf.Tests;

public sealed class PluginOpaqueAndBlindTests
{
    [Fact]
    public void OpaquePeerUpdate_HeaderHopReadsAndGrowShrinkSplicingPreserveUntouchedFields()
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
        peer.Tags.AddRange(new[] { "tier1", "ebgp", "eu-central", "ixp-de-cix" });
        peer.Prefixes.AddRange(new uint[] { 167772160, 172032000, 3232235520 });
        peer.Neighbors.Add(new Neighbor { PeerAsn = 3356, Router = "ae0.fra1.level3.net", LatencyUs = 850, Established = true });

        byte[] originalWire = peer.MarshalOmniBuf();
        byte[] snapshot = (byte[])originalWire.Clone();

        var opaque = new OpaquePeerUpdate(originalWire);
        Assert.Equal(64512u, opaque.Asn);
        Assert.Equal("Frankfurt", opaque.GetLocationCity());
        Assert.Equal("FRA-Equinix-FR5", opaque.GetLocationDatacenter());
        Assert.Equal(9876543210UL, opaque.Sequence);

        // Grow splice: longer router_id and expanded 9-byte sequence
        opaque.RouterId = "edge-rtr-fra01-expanded-chassis-09.core.backbone.global.example.net";
        opaque.Sequence = ulong.MaxValue;
        byte[] grownWire = opaque.MarshalOmniBuf();
        Assert.Equal(snapshot, originalWire); // Source packet is never mutated

        PeerUpdate grownDecoded = PeerUpdate.ParseOmniBuf(grownWire);
        Assert.Equal("edge-rtr-fra01-expanded-chassis-09.core.backbone.global.example.net", grownDecoded.RouterId);
        Assert.Equal(ulong.MaxValue, grownDecoded.Sequence);
        Assert.Equal("Frankfurt", grownDecoded.Location.City);
        Assert.Single(grownDecoded.Neighbors);

        // Shrink splice: tiny router_id and 1-byte sequence
        var opaqueShrink = new OpaquePeerUpdate(grownWire)
        {
            RouterId = "r1",
            Sequence = 1UL,
        };
        byte[] shrunkWire = opaqueShrink.MarshalOmniBuf();
        Assert.True(shrunkWire.Length < grownWire.Length);

        PeerUpdate shrunkDecoded = PeerUpdate.ParseOmniBuf(shrunkWire);
        Assert.Equal("r1", shrunkDecoded.RouterId);
        Assert.Equal(1UL, shrunkDecoded.Sequence);
        Assert.Equal("Frankfurt", shrunkDecoded.Location.City);
    }

    [Fact]
    public void SpliceFields_PreservesAliasedPrefixWithoutMutatingSourceBackingBuffer()
    {
        var peer = new PeerUpdate
        {
            Asn = 64512,
            RouterId = "rtr-a",
            Sequence = 7UL,
        };
        byte[] packet = peer.MarshalOmniBuf();
        byte[] prefixHeader = { 0xCA, 0xFE, 0xBA, 0xBE };

        byte[] sharedBuffer = new byte[prefixHeader.Length + packet.Length];
        prefixHeader.CopyTo(sharedBuffer, 0);
        packet.CopyTo(sharedBuffer, prefixHeader.Length);
        byte[] backup = (byte[])sharedBuffer.Clone();

        var prefixSeg = new ArraySegment<byte>(sharedBuffer, 0, prefixHeader.Length);
        var packetSeg = new ArraySegment<byte>(sharedBuffer, prefixHeader.Length, packet.Length);

        byte[] spliced = OmniBufOpaque.SpliceFields(
            prefixSeg,
            packetSeg,
            dirtyMaskLow: 1UL << 11,
            dirtyOverflow: null,
            (enc, fnum) =>
            {
                if (fnum == 11u)
                {
                    enc.WriteUintField(11u, 123456789UL);
                }
            });

        // Backing sharedBuffer was not mutated in place
        Assert.Equal(backup, sharedBuffer);

        // Prefix bytes preserved at start of output
        Assert.Equal(prefixHeader, spliced[..prefixHeader.Length]);

        PeerUpdate decoded = PeerUpdate.ParseOmniBuf(spliced.AsSpan(prefixHeader.Length));
        Assert.Equal(64512u, decoded.Asn);
        Assert.Equal("rtr-a", decoded.RouterId);
        Assert.Equal(123456789UL, decoded.Sequence);
    }

    [Fact]
    public void DecodeBlindJson_SupportsTrailingField0ExactNumbersNegativeZeroAndBase64()
    {
        var msg = new TestAllTypes
        {
            F2Uint64 = ulong.MaxValue,
            F4Sint64 = long.MinValue,
            F14Float64 = -0.0,
            F13Float32 = 1.5f,
            F7Bytes = ByteString.CopyFrom(1, 2, 3, 4),
            F5Bool = true,
            F6String = "hello",
        };

        // Write body first, then trailing Field #0 to verify two-pass Field #0 discovery
        var enc = new OmniBufEncoder(256);
        byte[] bodyOnly = msg.MarshalOmniBuf();
        enc.WriteRawBytes(bodyOnly);
        SchemaMeta meta = TestAllTypes.BuildOmniBufSchemaMeta()!;
        SchemaMeta.WriteField0(enc, meta, deterministic: true);
        byte[] trailingField0Packet = enc.ToArray();

        string json = OmniBufBlind.DecodeBlindJson(trailingField0Packet, fallbackOnNonFinite: false);
        Assert.Contains("\"f2_uint64\":18446744073709551615", json);
        Assert.Contains("\"f4_sint64\":-9223372036854775808", json);
        Assert.Contains("\"f14_float64\":-0", json);
        Assert.Contains("\"f7_bytes\":\"AQIDBA==\"", json);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("hello", doc.RootElement.GetProperty("f6_string").GetString());
        Assert.True(doc.RootElement.GetProperty("f5_bool").GetBoolean());
    }

    [Fact]
    public void DecodeBlindJson_FallsBackToEmptyJsonObjectOnNonFiniteFloatsWhenConfigured()
    {
        var msg = new TestAllTypes { F14Float64 = double.NaN };
        byte[] wire = msg.MarshalOmniBuf(new MarshalOptions { SelfDescribing = true });

        string fallbackJson = OmniBufBlind.DecodeBlindJson(wire, fallbackOnNonFinite: true);
        Assert.Equal("{}", fallbackJson);

        Assert.Throws<OmniBufException>(() =>
            OmniBufBlind.DecodeBlindJson(wire, fallbackOnNonFinite: false));
    }

    [Fact]
    public void DecodeBlind_RejectsFieldNameCollisionAndConcatenatedWireShapeMismatch()
    {
        // 1. Field #0 mapping two distinct field numbers (#1 and #2) to the same name "dup" while both appear on wire
        var collisionMeta = new SchemaMeta
        {
            TypeName = "BadCollision",
            Fields = new Dictionary<uint, string>
            {
                [1u] = "dup",
                [2u] = "dup",
            },
        };
        var enc1 = new OmniBufEncoder(64);
        SchemaMeta.WriteField0(enc1, collisionMeta, deterministic: true);
        enc1.WriteUintField(1u, 10);
        enc1.WriteUintField(2u, 20);
        byte[] collisionPacket = enc1.ToArray();

        Assert.Throws<OmniBufException>(() => OmniBufBlind.DecodeBlind(collisionPacket));

        // 2. Concatenated field #1 changing wire shape from scalar Uint to SubMsg
        var enc2 = new OmniBufEncoder(64);
        enc2.WriteUintField(1u, 100);
        enc2.BeginSubMsgField(1u);
        enc2.WriteUintField(1u, 200);
        enc2.EndSubMsg();
        byte[] shapeChangePacket = enc2.ToArray();

        Assert.Throws<OmniBufException>(() => OmniBufBlind.DecodeBlind(shapeChangePacket));
    }

    [Fact]
    public void SpanViewAndBufferWriter_ZeroCopySliceReadsAndStreamingWritesMatchGeneratedCodec()
    {
        var peer = new PeerUpdate
        {
            Asn = 64512u,
            RouterId = "edge-rtr-ams02",
            Active = true,
            Sequence = 42424242UL,
            Location = new Location
            {
                Region = "eu-west-1",
                City = "Amsterdam",
                Rack = 9u,
            },
        };

        var writer = new ArrayBufferWriter<byte>();
        peer.MarshalOmniBuf(writer);
        ReadOnlySpan<byte> wire = writer.WrittenSpan;
        Assert.Equal(peer.MarshalOmniBuf(), wire.ToArray());

        var view = new PeerUpdateSpanView(wire);
        Assert.Equal(64512u, view.Asn);
        Assert.Equal("edge-rtr-ams02", view.RouterId);
        Assert.Equal("edge-rtr-ams02"u8.ToArray(), view.GetRouterIdUtf8Span().ToArray());
        Assert.True(view.Active);
        Assert.Equal(42424242UL, view.Sequence);
        Assert.True(view.TryGetLocation(out LocationSpanView locView));
        Assert.Equal("Amsterdam", locView.City);
        Assert.Equal(9u, locView.Rack);

        var msg = new TestAllTypes
        {
            F6String = "span-str",
            F7Bytes = ByteString.CopyFrom(0xDE, 0xAD, 0xBE, 0xEF),
        };
        var msgWriter = new ArrayBufferWriter<byte>();
        msg.MarshalOmniBuf(msgWriter);
        var msgView = new TestAllTypesSpanView(msgWriter.WrittenSpan);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, msgView.F7Bytes.ToArray());
        Assert.Equal("span-str", msgView.F6String);

        var opaqueMsg = new OpaqueTestAllTypes(msgWriter.WrittenMemory.ToArray());
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, opaqueMsg.GetF7BytesSpan().ToArray());
    }
}
