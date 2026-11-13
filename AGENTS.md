# AGENTS.md

Repository instructions for AI coding agents working in `omnibuf-cs`.

## Toolchain Setup

Tool versions live in `mise.toml` (`dotnet = "8.0.423"`, `buf = "1.75.0"`, `just = "1.58.0"`, `protobuf = "35.1"`). Load the environment shims before invoking CLI commands:

```bash
export PATH="$HOME/.local/share/mise/shims:/opt/homebrew/bin:/usr/local/bin:$PATH"
eval "$(mise env)"
```

Use `buf` v2 (`buf lint`, `buf format`, `buf generate`) for every Protobuf workflow. Raw `protoc` invocations are not used in build scripts or documentation.

## Repository Layout

- `src/OmniBuf`: Core .NET 8 runtime library (`OmniBufEncoder`, `OmniBufDecoder`, `OmniBufOpaque`, `SchemaMeta`, `OmniBufBlind`, `OmniBufSerializer`, `OmniBufProtobuf`).
- `src/ProtocGenOmniBufCs`: `protoc-gen-omnibuf-cs` plugin invoked through `buf.gen.yaml`. Emits partial-class static codecs and `Opaque*` header-hop views.
- `src/OmniBuf.Conformance`: Stdin/stdout length-prefixed binary conformance adapter covering all four OmniBuf tiers.
- `src/OmniBuf.Benchmarks`: Reproducible `PeerUpdate` benchmark runner writing structured metrics to `testdata/benchmarks.json`.
- `tests/OmniBuf.Tests`: xUnit test suite covering wire invariants, strict UTF-8 validation, container backpatching, attribute serialization, reflection codecs, splicing, and blind JSON decoding.
- `proto/`: Shared `.proto` definitions managed by `buf.yaml`.

## Wire Format Invariants

Every change to `src/OmniBuf` or `src/ProtocGenOmniBufCs` must preserve these wire rules:

1. TZCNT Prefix-Varints reject reserved markers where `(b & 0x07) == 0 && b != 0`. Non-minimal encodings remain valid.
2. Strings enforce strict UTF-8 validation via `System.Text.Unicode.Utf8.IsValid`. Surrogates and overlong byte sequences throw `OmniBufException`.
3. `WireArray` and `WireMap` headers enforce `MaxNestingDepth = 32` (`depth + 1` at the container header, `depth + 2` for inner `SubMsg` elements) and reject trailing bytes even when `Count == 0`.
4. `WireBool` inside arrays and maps requires canonical `0x00` or `0x01` element bytes.
5. `WireExtended` (`15`) carries a mandatory 1-byte subtype header (`0x00..0xFF`); zero-byte payloads fail validation.
6. `Field #0` (`SchemaMeta`) requires `WireSubMsg` and is discovered in a preliminary scan pass during `DecodeBlind` so trailing metadata still resolves earlier fields.

## Required Verification Gate

Run `just ci` after every code or schema edit:

```bash
just proto
just fmt
just ci
```

`just ci` executes `buf format -d --exit-code`, `dotnet format --verify-no-changes`, `buf lint`, `dotnet build -c Release -warnaserror`, the xUnit suite, and the full 584-case cross-language conformance runner (`0 skipped, 0 failed`).

## Documentation & Copy Style

- Never use em-dashes anywhere in C# comments, XML docs, commit messages, or Markdown files.
- Keep inline code comments short and omit trailing periods on 2-3 word comments.
- Mix crisp 5-9 word sentences with detailed technical explanations.
