# OmniBuf for C# (.NET 8)

[![CI](https://github.com/omnibuf/omnibuf-cs/actions/workflows/ci.yml/badge.svg)](https://github.com/omnibuf/omnibuf-cs/actions/workflows/ci.yml)
[![Wire Spec: v0.1](https://img.shields.io/badge/spec-v0.1-111111?style=flat&labelColor=111111&color=FF4F18)](https://omnibuf.com/spec)
[![Website](https://img.shields.io/badge/website-omnibuf.com-111111?style=flat&labelColor=111111&color=D4FF33)](https://omnibuf.com/cs)

Idiomatic .NET 8 implementation of **[OmniBuf Wire Specification v0.1](https://omnibuf.com/spec)**. Built around `ReadOnlySpan<byte>`, `Span<byte>`, `BitOperations.TrailingZeroCount`, and `BinaryPrimitives`.

OmniBuf pairs self-delimiting 16-type wire headers with branch-light TZCNT Prefix-Varints and single-pass forward container backpatching. Decoders hop across top-level and nested headers in O(1) per skipped field. Selective reads and binary field splices avoid materializing unrelated fields.

## Supported Tiers

`omnibuf-cs` implements all four OmniBuf runtime tiers and passes 758/758 cross-language conformance cases (`0 skipped, 0 failed`):

- **Generated Plugin Codecs**: `protoc-gen-omnibuf-cs` emits direct partial-class `MarshalOmniBuf` / `ParseOmniBuf` methods alongside `Opaque*` lazy views (`OpaquePeerUpdate`, `OpaqueLocation`) with both O(1) `MarshalOmniBuf` splicing and fail-closed `TryMarshal` / `MarshalValidated`.
- **Standard Protobuf Reflection**: `OmniBufProtobuf.Marshal` and `OmniBufProtobuf.Unmarshal<T>` operate directly on `Google.Protobuf.IMessage` types produced by `protoc_builtin: csharp`.
- **Attribute-Annotated C# Types**: `[OmniBufMessage]` and `[OmniBufField]` serialize mutable classes, value structs, and immutable C# positional records via compiled expression trees in `OmniBufSerializer`.
- **Schema-Less Blind Inspection**: `OmniBufBlind.DecodeBlind` and `OmniBufBlind.DecodeBlindJson` inspect arbitrary packets using wire tags and optional embedded `Field #0` (`SchemaMeta`) metadata.

## Code Generation with Buf

`omnibuf-cs` uses [Buf](https://buf.build/) v2 (`buf.yaml` and `buf.gen.yaml`) for schema linting, formatting, and code generation.

```yaml
# buf.gen.yaml
version: v2
clean: true
plugins:
  - protoc_builtin: csharp
    out: src/OmniBuf.Conformance/Generated
  - local: ./src/ProtocGenOmniBufCs/bin/Release/net8.0/protoc-gen-omnibuf-cs
    out: src/OmniBuf.Conformance/Generated
```

Build the plugin and regenerate C# bindings in one step:

```bash
just proto
```

Under the hood, `just proto` compiles `ProtocGenOmniBufCs` in Release mode and runs `buf generate`.

## Quick Start

### Generated Codec and Opaque Views

```csharp
using Omnibuf.Bench;
using OmniBuf;

var peer = new PeerUpdate
{
    Asn = 64512,
    RouterId = "edge-rtr-fra01.core.backbone.net",
    Active = true,
    Sequence = 9876543210UL,
    Location = new Location { City = "Frankfurt", Rack = 42 },
};

// Single-pass forward encode
byte[] packet = peer.MarshalOmniBuf();

// Direct nested header-hop read
var view = new OpaquePeerUpdate(packet);
string city = view.GetLocationCity(); // "Frankfurt"

// Selective field splice
view.Sequence = view.Sequence + 1UL;
byte[] updated = view.MarshalOmniBuf();

// Full materialize
PeerUpdate decoded = PeerUpdate.ParseOmniBuf(updated);
```

### Attribute-Annotated C# Classes and Records

```csharp
using OmniBuf;

[OmniBufMessage(TypeName = "SensorSample")]
public sealed record SensorSample(
    [property: OmniBufField(1, Name = "sensor_id")] string SensorId,
    [property: OmniBufField(2, Name = "celsius")] double Celsius,
    [property: OmniBufField(3, Name = "seq")] ulong Seq);

var sample = new SensorSample("rack-42-inlet", 21.75, 1042UL);
byte[] bytes = OmniBufSerializer.Marshal(sample, new MarshalOptions(SelfDescribing: true));

SensorSample restored = OmniBufSerializer.Unmarshal<SensorSample>(bytes);
string blindJson = OmniBufBlind.DecodeBlindJson(bytes);
```

### Standard `Google.Protobuf.IMessage` Reflection

```csharp
using Omnibuf.Bench;
using OmniBuf;

byte[] wire = OmniBufProtobuf.Marshal(peer, new MarshalOptions(Deterministic: true));
PeerUpdate copy = OmniBufProtobuf.Unmarshal<PeerUpdate>(wire);
```

## Benchmarks

`src/OmniBuf.Benchmarks` (`just bench`, output saved to `testdata/benchmarks.json`) measures six categories on the 11-field `PeerUpdate` BGP workload in Release mode after RyuJIT Dynamic PGO warmup:

1. **Full Message Encode Speed**: Serializes all 11 fields to a fresh `byte[]` (366 B OmniBuf, 341 B Protobuf).
2. **Full Message Decode Speed**: Deserializes a fresh instance and folds all 11 fields into a non-inlined checksum sink.
3. **Read a Single Nested Field**: Extracts `peer.location.city` from the raw packet.
4. **Selective Single-Field Mutation**: Verifies `peer.location.city`, increments `peer.sequence` from the input packet, and emits an updated `byte[]`.
5. **Large Embedded Message Encode**: Encodes a packet where `location.datacenter` (20 KiB) and `neighbors[0].router` (18 KiB) exceed the 2-byte 16,383 B length reservation and trigger forward container shifts.
6. **Embedded Message Growth**: Expands `peer.location.datacenter` past 16 KiB ahead of 32 trailing `Neighbor` entries.

| Category | OmniBuf Generated | OmniBuf Attributes | Google.Protobuf | MessagePack-CSharp | System.Text.Json |
| --- | ---: | ---: | ---: | ---: | ---: |
| Wire Size | 366 B | 366 B | 341 B | 626 B | 803 B |
| Full Message Encode Speed | **308 ns** | 478 ns | 655 ns | 607 ns | 1,122 ns |
| Full Message Decode Speed | **743 ns** | 896 ns | 900 ns | 1,300 ns | 3,279 ns |
| Read a Single Nested Field | **144 ns** | n/a | 820 ns | 1,200 ns | 3,169 ns |
| Selective Single-Field Mutation | **365 ns** | n/a | 1,499 ns | 1,845 ns | 4,370 ns |
| Large Embedded Message Encode | 6,649 ns | 6,946 ns | 26,575 ns | **6,186 ns** | 9,047 ns |
| Embedded Message Growth | **6,629 ns** | n/a | 19,480 ns | 9,000 ns | 20,479 ns |

## Development Commands

`mise.toml` pins `dotnet`, `buf`, `just`, and `protobuf`. Run `mise trust` once in the repository root.

```bash
just proto        # Build protoc-gen-omnibuf-cs and run buf generate
just build        # Build OmniBuf.sln in Release mode
just test         # Run xUnit tests
just conformance  # Run all 758 cross-language conformance cases
just bench        # Run PeerUpdate benchmarks and update testdata/benchmarks.json
just fmt          # Format .proto files (buf format) and C# sources (dotnet format)
just ci           # Full CI gate: fmt-check, buf lint, -warnaserror build, tests, conformance
```

## License

Released under the [Apache 2.0 License](./LICENSE).

## Reflection and deployment

The attribute serializer discovers properties and constructors through reflection
and compiles expression delegates at runtime. Trimming and NativeAOT deployments
must preserve the required metadata and validate their published application.
Generated codecs provide explicit field access and avoid the attribute
serializer's reflection requirements. Property accessors and constructors are
trusted application code and can execute during serialization.
