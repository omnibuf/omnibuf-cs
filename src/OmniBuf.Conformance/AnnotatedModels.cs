using OmniBuf;

namespace OmniBuf.ConformanceRunner;

[OmniBufMessage(TypeName = "ConformanceRequest")]
public sealed class ConformanceRequestFrame
{
    [OmniBufField(1, Name = "test_name")]
    public string TestName { get; set; } = string.Empty;

    [OmniBufField(2, Name = "message_type")]
    public string MessageType { get; set; } = string.Empty;

    [OmniBufField(3, Name = "tier")]
    public uint Tier { get; set; }

    [OmniBufField(4, Name = "requested_output_format")]
    public uint RequestedOutputFormat { get; set; }

    [OmniBufField(5, Name = "omnibuf_payload")]
    public byte[] OmniBufPayload { get; set; } = Array.Empty<byte>();

    [OmniBufField(6, Name = "json_payload")]
    public string JsonPayload { get; set; } = string.Empty;

    [OmniBufField(7, Name = "self_describing")]
    public bool SelfDescribing { get; set; }

    [OmniBufField(8, Name = "test_opaque_splice")]
    public bool TestOpaqueSplice { get; set; }

    [OmniBufField(9, Name = "test_opaque_splice_shrink")]
    public bool TestOpaqueSpliceShrink { get; set; }
}

[OmniBufMessage(TypeName = "ConformanceResponse")]
public sealed class ConformanceResponseFrame
{
    [OmniBufField(1, Name = "parse_error")]
    public string ParseError { get; set; } = string.Empty;

    [OmniBufField(2, Name = "serialize_error")]
    public string SerializeError { get; set; } = string.Empty;

    [OmniBufField(3, Name = "runtime_error")]
    public string RuntimeError { get; set; } = string.Empty;

    [OmniBufField(4, Name = "skipped")]
    public string Skipped { get; set; } = string.Empty;

    [OmniBufField(5, Name = "omnibuf_payload")]
    public byte[] OmniBufPayload { get; set; } = Array.Empty<byte>();

    [OmniBufField(6, Name = "json_payload")]
    public string JsonPayload { get; set; } = string.Empty;
}

[OmniBufMessage(TypeName = "Location")]
public sealed class AnnotatedLocation
{
    [OmniBufField(1, Name = "region")]
    public string Region { get; set; } = string.Empty;

    [OmniBufField(2, Name = "datacenter")]
    public string Datacenter { get; set; } = string.Empty;

    [OmniBufField(3, Name = "city")]
    public string City { get; set; } = string.Empty;

    [OmniBufField(4, Name = "rack")]
    public uint Rack { get; set; }

    [OmniBufField(5, Name = "latitude")]
    public float Latitude { get; set; }

    [OmniBufField(6, Name = "longitude")]
    public float Longitude { get; set; }
}

[OmniBufMessage(TypeName = "Neighbor")]
public sealed class AnnotatedNeighbor
{
    [OmniBufField(1, Name = "peer_asn")]
    public uint PeerAsn { get; set; }

    [OmniBufField(2, Name = "router")]
    public string Router { get; set; } = string.Empty;

    [OmniBufField(3, Name = "latency_us")]
    public uint LatencyUs { get; set; }

    [OmniBufField(4, Name = "established")]
    public bool Established { get; set; }
}

[OmniBufMessage(TypeName = "PeerUpdate")]
public sealed class AnnotatedPeerUpdate
{
    [OmniBufField(1, Name = "asn")]
    public uint Asn { get; set; }

    [OmniBufField(2, Name = "router_id")]
    public string RouterId { get; set; } = string.Empty;

    [OmniBufField(3, Name = "active")]
    public bool Active { get; set; }

    [OmniBufField(4, Name = "uptime_sec")]
    public ulong UptimeSec { get; set; }

    [OmniBufField(5, Name = "rssi_dbm", Wire = OmniBufWireType.Sint)]
    public int RssiDbm { get; set; }

    [OmniBufField(6, Name = "cpu_load")]
    public double CpuLoad { get; set; }

    [OmniBufField(7, Name = "tags")]
    public List<string> Tags { get; set; } = new();

    [OmniBufField(8, Name = "prefixes")]
    public List<uint> Prefixes { get; set; } = new();

    [OmniBufField(9, Name = "location")]
    public AnnotatedLocation? Location { get; set; }

    [OmniBufField(10, Name = "neighbors")]
    public List<AnnotatedNeighbor> Neighbors { get; set; } = new();

    [OmniBufField(11, Name = "sequence")]
    public ulong Sequence { get; set; }
}

[OmniBufMessage(TypeName = "AllTypesSubMsg")]
public sealed record AnnotatedAllTypesSubMsg(
    [property: OmniBufField(1, Name = "id")] uint Id,
    [property: OmniBufField(2, Name = "label")] string Label,
    [property: OmniBufField(3, Name = "flag")] bool Flag,
    [property: OmniBufField(4, Name = "score", Wire = OmniBufWireType.Sint)] long Score);

[OmniBufMessage(TypeName = "TestAllTypes")]
public sealed class AnnotatedTestAllTypes
{
    [OmniBufField(1, Name = "f1_uint32")]
    public uint F1Uint32 { get; set; }

    [OmniBufField(2, Name = "f2_uint64")]
    public ulong F2Uint64 { get; set; }

    [OmniBufField(3, Name = "f3_sint32", Wire = OmniBufWireType.Sint)]
    public int F3Sint32 { get; set; }

    [OmniBufField(4, Name = "f4_sint64", Wire = OmniBufWireType.Sint)]
    public long F4Sint64 { get; set; }

    [OmniBufField(5, Name = "f5_bool")]
    public bool F5Bool { get; set; }

    [OmniBufField(6, Name = "f6_string")]
    public string F6String { get; set; } = string.Empty;

    [OmniBufField(7, Name = "f7_bytes")]
    public byte[] F7Bytes { get; set; } = Array.Empty<byte>();

    [OmniBufField(8, Name = "f8_sub_msg")]
    public AnnotatedAllTypesSubMsg? F8SubMsg { get; set; }

    [OmniBufField(9, Name = "f9_string_arr")]
    public List<string> F9StringArr { get; set; } = new();

    [OmniBufField(10, Name = "f10_uint32_arr")]
    public List<uint> F10Uint32Arr { get; set; } = new();

    [OmniBufField(11, Name = "f11_sub_msg_arr")]
    public List<AnnotatedAllTypesSubMsg> F11SubMsgArr { get; set; } = new();

    [OmniBufField(12, Name = "f12_map_str_uint")]
    public Dictionary<string, ulong> F12MapStrUint { get; set; } = new();

    [OmniBufField(13, Name = "f13_float32")]
    public float F13Float32 { get; set; }

    [OmniBufField(14, Name = "f14_float64")]
    public double F14Float64 { get; set; }

    [OmniBufField(15, Name = "f15_fixed32", Wire = OmniBufWireType.Fixed32)]
    public uint F15Fixed32 { get; set; }

    [OmniBufField(16, Name = "f16_fixed64", Wire = OmniBufWireType.Fixed64)]
    public ulong F16Fixed64 { get; set; }

    [OmniBufField(20, Name = "f20_overflow_str")]
    public string F20OverflowStr { get; set; } = string.Empty;
}

[OmniBufMessage(TypeName = "TreeNode")]
public sealed class AnnotatedTreeNode
{
    [OmniBufField(1, Name = "id")]
    public uint Id { get; set; }

    [OmniBufField(2, Name = "name")]
    public string Name { get; set; } = string.Empty;

    [OmniBufField(3, Name = "weight", Wire = OmniBufWireType.Sint)]
    public long Weight { get; set; }

    [OmniBufField(4, Name = "primary")]
    public AnnotatedTreeNode? Primary { get; set; }

    [OmniBufField(5, Name = "children")]
    public List<AnnotatedTreeNode> Children { get; set; } = new();

    [OmniBufField(6, Name = "branches")]
    public List<AnnotatedTreeNode> Branches { get; set; } = new();

    [OmniBufField(7, Name = "labels")]
    public Dictionary<string, ulong> Labels { get; set; } = new();
}

[OmniBufMessage(TypeName = "NestedNIC")]
public sealed record AnnotatedNestedNic(
    [property: OmniBufField(1, Name = "if_index")] uint IfIndex,
    [property: OmniBufField(2, Name = "mac")] string Mac,
    [property: OmniBufField(3, Name = "ips")] List<uint> Ips);

[OmniBufMessage(TypeName = "NestedServer")]
public sealed record AnnotatedNestedServer(
    [property: OmniBufField(1, Name = "server_id")] uint ServerId,
    [property: OmniBufField(2, Name = "hostname")] string Hostname,
    [property: OmniBufField(3, Name = "nics")] List<AnnotatedNestedNic> Nics);

[OmniBufMessage(TypeName = "NestedRack")]
public sealed class AnnotatedNestedRack
{
    [OmniBufField(1, Name = "rack_id")]
    public uint RackId { get; set; }

    [OmniBufField(2, Name = "zone")]
    public string Zone { get; set; } = string.Empty;

    [OmniBufField(3, Name = "servers")]
    public List<AnnotatedNestedServer> Servers { get; set; } = new();
}

[OmniBufMessage(TypeName = "NestedCluster")]
public sealed class AnnotatedNestedCluster
{
    [OmniBufField(1, Name = "cluster_name")]
    public string ClusterName { get; set; } = string.Empty;

    [OmniBufField(2, Name = "racks")]
    public List<AnnotatedNestedRack> Racks { get; set; } = new();

    [OmniBufField(3, Name = "epoch")]
    public ulong Epoch { get; set; }
}
