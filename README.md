# omnibuf-cs

[![OmniBuf Spec: v1 DRAFT-1](https://img.shields.io/badge/OmniBuf%20Spec-v1%20DRAFT--1-FF4F18)](https://omnibuf.com/spec)

.NET 8 implementation of the [OmniBuf v1 DRAFT-1 wire format](https://omnibuf.com/spec). Documentation and benchmarks live at [omnibuf.com/cs](https://omnibuf.com/cs).

## Install

```bash
dotnet add package OmniBuf
```

## Quick usage

```csharp
using OmniBuf;

[OmniBufMessage(TypeName = "SensorSample")]
public sealed record SensorSample(
    [property: OmniBufField(1, Name = "sensor_id")] string SensorId,
    [property: OmniBufField(2, Name = "celsius")] double Celsius,
    [property: OmniBufField(3, Name = "seq")] ulong Seq);

var sample = new SensorSample("rack-42-inlet", 21.75, 1042UL);
byte[] wire = OmniBufSerializer.Marshal(sample, new MarshalOptions(SelfDescribing: true));
SensorSample decoded = OmniBufSerializer.Unmarshal<SensorSample>(wire);
```

## Details

- Targets .NET 8+. Built on `Span<byte>`, `ReadOnlySpan<byte>`, `BitOperations.TrailingZeroCount`, and `BinaryPrimitives`.
- `OmniBufSerializer` serializes `[OmniBufMessage]` and `[OmniBufField]` classes, structs, and records via compiled expression trees, with `MarshalOptions` and `UnmarshalOptions` for deterministic encoding, self-describing headers, and decode limits.
- `OmniBufProtobuf` encodes and decodes `Google.Protobuf.IMessage` types directly.
- `protoc-gen-omnibuf-cs` emits partial-class `MarshalOmniBuf` / `ParseOmniBuf` methods and lazy `Opaque*` views with in-place field splicing.
- `OmniBufBlind.DecodeBlind` and `OmniBufBlind.DecodeBlindJson` inspect packets from wire tags and optional `Field #0` schema metadata.
- `OmniBufSerializer` relies on reflection and runtime expression compilation. Trimming and NativeAOT deployments must preserve metadata for annotated message types, whereas generated codecs use direct field access.

## License

Apache-2.0. See [LICENSE](./LICENSE).
