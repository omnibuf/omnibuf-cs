# OmniBuf for C# (.NET 8)

Idiomatic .NET 8 implementation of the OmniBuf wire format. Built around `ReadOnlySpan<byte>`, `Span<byte>`, `BitOperations.TrailingZeroCount`, and `BinaryPrimitives`.

OmniBuf pairs self-delimiting 16-type wire headers with branch-light TZCNT Prefix-Varints and single-pass forward container backpatching. Decoders hop across top-level and nested headers in O(1) per skipped field. No intermediate object graph is needed for selective reads or single-field splicing.

## Supported Tiers

`omnibuf-cs` implements all four OmniBuf runtime tiers and passes 584/584 cross-language conformance cases (`0 skipped, 0 failed`):

- **Generated Plugin Codecs**: `protoc-gen-omnibuf-cs` emits direct partial-class `MarshalOmniBuf` / `ParseOmniBuf` methods alongside `Opaque*` lazy views (`OpaquePeerUpdate`, `OpaqueLocation`).
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
view.Sequence = 9876543211UL;
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
byte[] bytes = OmniBufSerializer.Marshal(sample, new MarshalOptions { SelfDescribing = true });

SensorSample restored = OmniBufSerializer.Unmarshal<SensorSample>(bytes);
string blindJson = OmniBufBlind.DecodeBlindJson(bytes);
```

### Standard `Google.Protobuf.IMessage` Reflection

```csharp
using Omnibuf.Bench;
using OmniBuf;

byte[] wire = OmniBufProtobuf.Marshal(peer, new MarshalOptions { Deterministic = true });
PeerUpdate copy = OmniBufProtobuf.Unmarshal<PeerUpdate>(wire);
```

## Benchmarks

Measured on Apple M1 Pro (`darwin/arm64`, `.NET 8.0.29`, Release build with RyuJIT Dynamic PGO warmup) using the canonical 11-field `PeerUpdate` workload (`just bench`, output saved to `testdata/benchmarks.json`).

| Category | OmniBuf Generated | OmniBuf Attributes | Google.Protobuf | MessagePack-CSharp | System.Text.Json |
| --- | ---: | ---: | ---: | ---: | ---: |
| Wire Size | 366 B | 366 B | 341 B | 626 B | 803 B |
| Full Encode | **310 ns** | 453 ns | 629 ns | 589 ns | 1,103 ns |
| Full Decode | **706 ns** | 794 ns | 834 ns | 1,220 ns | 3,167 ns |
| Read `peer.location.city` | **140 ns** | n/a | 835 ns | 1,210 ns | 3,120 ns |
| Mutate `peer.sequence` | **128 ns** | n/a | 1,486 ns | 1,811 ns | 4,331 ns |

## Development Commands

`mise.toml` pins `dotnet`, `buf`, `just`, and `protobuf`. Run `mise trust` once in the repository root.

```bash
just proto        # Build protoc-gen-omnibuf-cs and run buf generate
just build        # Build OmniBuf.sln in Release mode
just test         # Run xUnit tests
just conformance  # Run all 584 cross-language conformance cases
just bench        # Run PeerUpdate benchmarks and update testdata/benchmarks.json
just fmt          # Format .proto files (buf format) and C# sources (dotnet format)
just ci           # Full CI gate: fmt-check, buf lint, -warnaserror build, tests, conformance
```

## License

MIT
