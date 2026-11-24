using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Grpc.Core;

namespace OmniBuf.Grpc;

/// <summary>
/// Factory for <see cref="Marshaller{T}"/> instances backed by <see cref="OmniBufSerializer"/>.
/// Lives in the detached <c>OmniBuf.Grpc</c> assembly so core <c>OmniBuf</c> has zero gRPC dependencies.
/// </summary>
public static class OmniBufGrpcMarshaller
{
    private const string TrimWarningMessage =
        "OmniBufGrpcMarshaller delegates to OmniBufSerializer, which uses reflection to inspect [OmniBufField] properties and constructors.";

    private const string AotWarningMessage =
        "OmniBufGrpcMarshaller delegates to OmniBufSerializer, which compiles expression trees and dynamic generic methods at runtime.";

    /// <summary>
    /// Standard gRPC codec name for compact OmniBuf payloads.
    /// </summary>
    public const string CodecName = "omnibuf";

    /// <summary>
    /// Codec name for self-describing OmniBuf payloads carrying Field #0 schema metadata.
    /// </summary>
    public const string SelfDescribingCodecName = "x-omnibuf";

    /// <summary>
    /// gRPC HTTP/2 content-type header value for compact OmniBuf payloads.
    /// </summary>
    public const string ContentType = "application/grpc+omnibuf";

    /// <summary>
    /// gRPC HTTP/2 content-type header value for self-describing OmniBuf payloads.
    /// </summary>
    public const string SelfDescribingContentType = "application/grpc+x-omnibuf";

    /// <summary>
    /// Returns the gRPC codec name matching <paramref name="marshalOptions"/>.
    /// </summary>
    public static string GetCodecName(MarshalOptions marshalOptions) =>
        marshalOptions.SelfDescribing ? SelfDescribingCodecName : CodecName;

    /// <summary>
    /// Returns the gRPC content-type header value matching <paramref name="marshalOptions"/>.
    /// </summary>
    public static string GetContentType(MarshalOptions marshalOptions) =>
        marshalOptions.SelfDescribing ? SelfDescribingContentType : ContentType;

    /// <summary>
    /// Creates a contextual <see cref="Marshaller{T}"/> using the provided OmniBuf
    /// <paramref name="marshalOptions"/> and <paramref name="unmarshalOptions"/>.
    /// </summary>
    [RequiresUnreferencedCode(TrimWarningMessage)]
    [RequiresDynamicCode(AotWarningMessage)]
    public static Marshaller<T> Create<T>(
        MarshalOptions marshalOptions = default,
        UnmarshalOptions unmarshalOptions = default)
        where T : notnull
    {
        return new Marshaller<T>(
            (message, context) => Serialize(message, context, marshalOptions),
            context => Deserialize<T>(context, unmarshalOptions));
    }

    /// <summary>
    /// Creates a contextual <see cref="Marshaller{T}"/> configured for self-describing
    /// Field #0 encoding (<see cref="SelfDescribingCodecName"/>).
    /// </summary>
    [RequiresUnreferencedCode(TrimWarningMessage)]
    [RequiresDynamicCode(AotWarningMessage)]
    public static Marshaller<T> CreateSelfDescribing<T>(UnmarshalOptions unmarshalOptions = default)
        where T : notnull =>
        Create<T>(new MarshalOptions(SelfDescribing: true), unmarshalOptions);

    [RequiresUnreferencedCode(TrimWarningMessage)]
    [RequiresDynamicCode(AotWarningMessage)]
    private static void Serialize<T>(T message, SerializationContext context, MarshalOptions options)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        OmniBufSerializer.Marshal(context.GetBufferWriter(), message, options);
        context.Complete();
    }

    [RequiresUnreferencedCode(TrimWarningMessage)]
    [RequiresDynamicCode(AotWarningMessage)]
    private static T Deserialize<T>(DeserializationContext context, UnmarshalOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ReadOnlySequence<byte> sequence = context.PayloadAsReadOnlySequence();
        if (sequence.IsSingleSegment)
        {
            return OmniBufSerializer.Unmarshal<T>(sequence.FirstSpan, options);
        }

        byte[] buffer = context.PayloadAsNewBuffer();
        return OmniBufSerializer.Unmarshal<T>(buffer.AsSpan(), options);
    }
}
