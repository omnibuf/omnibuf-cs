using System.Text.Json;
using Google.Protobuf;
using Omnibuf.Conformance;
using OmniBuf;
using Xunit;

namespace OmniBuf.Tests;

public sealed class BlindTests
{
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
        segSplit.WriteBytesValue(new byte[] { 0x00 }); // Would complete varint if concatenated blindly
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
}
