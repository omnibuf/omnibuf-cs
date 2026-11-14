CONFORMANCE_RUNNER := env_var_or_default("CONFORMANCE_RUNNER", "../omnibuf-go/bin/conformance-runner")

# List available recipes
default:
    @just --list

# Build protoc-gen-omnibuf-cs and generate C# bindings via Buf
proto:
    dotnet build src/ProtocGenOmniBufCs/ProtocGenOmniBufCs.csproj -c Release -v minimal
    buf generate

# Alias for proto code generation
generate: proto

# Build the entire .NET 8 solution in Release configuration
build:
    dotnet build OmniBuf.sln -c Release

# Run the xUnit test suite in Release configuration
test:
    dotnet test OmniBuf.sln -c Release

# Build the conformance adapter and run the 758-case cross-language conformance runner
conformance runner=CONFORMANCE_RUNNER:
    dotnet build src/OmniBuf.Conformance/OmniBuf.Conformance.csproj -c Release -v minimal
    "{{runner}}" ./src/OmniBuf.Conformance/bin/Release/net8.0/OmniBuf.Conformance

# Run the PeerUpdate benchmark suite in Release mode and write testdata/benchmarks.json
bench:
    dotnet run --project src/OmniBuf.Benchmarks/OmniBuf.Benchmarks.csproj -c Release

# Format Protobuf schemas and C# source files
fmt:
    buf format -w
    dotnet format OmniBuf.sln

# Verify Protobuf and C# formatting with zero changes permitted
fmt-check:
    buf format -d --exit-code
    dotnet format OmniBuf.sln --verify-no-changes

# Lint Protobuf schemas with Buf and compile C# with warnings treated as errors
lint:
    buf lint
    dotnet build OmniBuf.sln -c Release -warnaserror

# Full CI gate: formatting, Buf + compiler lint, xUnit tests, and 758/758 conformance
ci: fmt-check lint test conformance
