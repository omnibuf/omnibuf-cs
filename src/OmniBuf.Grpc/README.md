# OmniBuf.Grpc

[![CI](https://github.com/omnibuf/omnibuf-cs/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/omnibuf/omnibuf-cs/actions/workflows/ci.yml?query=branch%3Amain)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](https://github.com/omnibuf/omnibuf-cs/blob/main/LICENSE)
[![Website](https://img.shields.io/badge/website-omnibuf.com-111111?style=flat&labelColor=111111&color=D4FF33)](https://omnibuf.com/cs)

OmniBuf `Marshaller<T>` factory for `Grpc.Core.Api`, `Grpc.Net.Client`, and `Grpc.AspNetCore`.

## Install

```bash
dotnet add package OmniBuf.Grpc
```

## Usage

```csharp
using Grpc.Core;
using OmniBuf;
using OmniBuf.Grpc;

var requestMarshaller = OmniBufGrpcMarshaller.Create<GetUserRequest>();
var responseMarshaller = OmniBufGrpcMarshaller.Create<GetUserResponse>();

var method = new Method<GetUserRequest, GetUserResponse>(
    MethodType.Unary,
    serviceName: "acme.v1.UserService",
    name: "GetUser",
    requestMarshaller: requestMarshaller,
    responseMarshaller: responseMarshaller);
```

Call `OmniBufGrpcMarshaller.CreateSelfDescribing<T>()` to include Field #0 schema metadata inline (`application/grpc+x-omnibuf`).

## License

Apache-2.0. See [LICENSE](https://github.com/omnibuf/omnibuf-cs/blob/main/LICENSE).
