# OmniBuf.Grpc

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
