using System.Buffers;
using Google.Protobuf;
using Omnibuf.Bench;
using Omnibuf.Conformance;
using OmniBuf;
using Xunit;

namespace OmniBuf.Tests;

public sealed class OpaqueTests
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

    [Fact]
    public void OpaqueFailClosed_ThrowsOnCorruptFramingAndRejectsUntouchedCorruptSubMsgInTryMarshal()
    {
        // Truncated outer varint header must throw OmniBufException on Opaque scalar read
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
    public void OpaqueTreeNode_SplicingAcrossMaxNestingDepth_FailsValidation()
    {
        // Build a valid child ProtoTreeNode chain of depth 30 (<= MaxNestingDepth 32)
        ProtoTreeNode childChain = BuildProtoTreeChain(edges: 30);
        byte[] childBytes = childChain.MarshalOmniBuf();
        _ = ProtoTreeNode.ParseOmniBuf(childBytes);

        // Build a parent ProtoTreeNode chain of depth 4 and splice childBytes as Primary at depth 5 -> total depth 35 (> 32)
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
    public void OpaqueOneof_OnConcatenatedPackets_EnforcesExclusivityForOverriddenBranch()
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
    public void OpaqueOneof_ClearInactiveMemberDoesNotDesyncActiveSibling()
    {
        var orig = new OuterEnvelope { CInt = 99 };
        byte[] wire = orig.MarshalOmniBuf();

        var opaque = new OpaqueOuterEnvelope(wire);
        Assert.True(opaque.HasCInt);
        Assert.Equal(99, opaque.CInt);

        opaque.ClearCStr();
        Assert.True(opaque.HasCInt);
        Assert.Equal(99, opaque.CInt);
        Assert.False(opaque.HasCStr);

        OuterEnvelope roundTripped = OuterEnvelope.ParseOmniBuf(opaque.MarshalOmniBuf());
        Assert.Equal(OuterEnvelope.ChoiceOneofCase.CInt, roundTripped.ChoiceCase);
        Assert.Equal(99, roundTripped.CInt);

        opaque.ClearCInt();
        Assert.False(opaque.HasCInt);
        Assert.False(opaque.HasCStr);
        OuterEnvelope cleared = OuterEnvelope.ParseOmniBuf(opaque.MarshalOmniBuf());
        Assert.Equal(OuterEnvelope.ChoiceOneofCase.None, cleared.ChoiceCase);
    }

    [Fact]
    public void OmniBufOpaque_LocateFieldAndGetter_RejectCorruptField0Header()
    {
        // Construct Field #0 (0x19) with outer length 2 bytes (TZCNT varint 0x05) containing a truncated
        // inner varint [0x35, 0x02] inside SchemaMeta, followed by valid field #1 = 42.
        var enc = new OmniBufEncoder(32);
        enc.WriteRawByte(OmniBufConstants.Field0SubMsgTag);
        enc.WriteBytesValue(new byte[] { 0x35, 0x02 });
        enc.WriteUintField(1u, 42UL);
        byte[] pkt = enc.ToArray();

        // Full parser properly rejects the corrupt Field #0
        Assert.Throws<OmniBufException>(() => PeerUpdate.ParseOmniBuf(pkt));

        // Opaque read helpers must not silently skip corrupt Field #0 and return field #1 = 42
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
    public void OpaqueSubMsgCollectionSetter_HandlesNullElement()
    {
        var opaque = new OpaquePeerUpdate(Array.Empty<byte>());
        opaque.SetNeighbors(new Neighbor[] { null! });

        byte[] wire = opaque.MarshalOmniBuf();
        PeerUpdate parsed = PeerUpdate.ParseOmniBuf(wire);
        Assert.Single(parsed.Neighbors);
    }

    [Fact]
    public void OpaqueHelpersAndWrappers_EnforceMaxNestingDepth()
    {
        // Build a wrapper SubMsg payload containing an unknown SubMsg nested 31 levels deep.
        // At depth 2 (element inside WireArray/WireMap), depth 2 + 31 = 33 > 32 must throw.
        byte[] nested31 = Array.Empty<byte>();
        for (int i = 0; i < 31; i++)
        {
            var e = new OmniBufEncoder(nested31.Length + 8);
            e.BeginSubMsgField(99u);
            e.WriteRawBytes(nested31);
            e.EndSubMsg();
            nested31 = e.ToArray();
        }

        var elemEnc = new OmniBufEncoder(nested31.Length + 8);
        elemEnc.WriteBytesValue(nested31);
        byte[] elemBytes = elemEnc.ToArray();

        Assert.Throws<OmniBufException>(() =>
        {
            int p = 0;
            OmniBufWrappers.ReadElemInt32(elemBytes, ref p);
        });

        // Build a packet with Field #0 SchemaMeta nested 32 levels deep (valid at depth 0, invalid at depth 2)
        SchemaMeta meta = new() { TypeName = "leaf" };
        for (int i = 0; i < 15; i++)
        {
            meta = new SchemaMeta
            {
                TypeName = $"n{i}",
                Children = new Dictionary<uint, SchemaMeta> { [1u] = meta },
            };
        }

        // Each SchemaMeta child is +2 depth (WireMap + WireSubMsg); 15 children = depth 31 at root (depth 0),
        // so at depth 1 it reaches depth 32, and at depth 2 it reaches 33 > 32.
        var pktEnc = new OmniBufEncoder(512);
        SchemaMeta.WriteField0(pktEnc, meta, deterministic: false);
        pktEnc.WriteStringField(2u, "ok");
        byte[] pkt = pktEnc.ToArray();

        Assert.Throws<OmniBufException>(() => OmniBufOpaque.LocateFieldMask(pkt, 2u, 0xFFFF, out _, out _, depth: 2));
        Assert.Throws<OmniBufException>(() =>
        {
            var outEnc = new OmniBufEncoder(64);
            OmniBufOpaque.SpliceFieldsTo(outEnc, pkt, 1UL << 3, null, 0, static (_, _, _) => { }, depth: 2);
        });
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
}
