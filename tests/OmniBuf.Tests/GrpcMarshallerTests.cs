using System.Buffers;
using Grpc.Core;
using OmniBuf;
using OmniBuf.Grpc;
using Xunit;

namespace OmniBuf.Tests;

public sealed class GrpcMarshallerTests
{
    [OmniBufMessage(TypeName = "PingRequest")]
    private sealed record PingRequest(
        [property: OmniBufField(1, Name = "node")] string Node,
        [property: OmniBufField(2, Name = "seq")] ulong Seq,
        [property: OmniBufField(3, Name = "ok")] bool Ok);

    [OmniBufMessage(TypeName = "PingResponse")]
    private sealed record PingResponse(
        [property: OmniBufField(1, Name = "echo_node")] string EchoNode,
        [property: OmniBufField(2, Name = "ack_seq")] ulong AckSeq);

    private sealed class BufferSerializationContext : SerializationContext
    {
        private readonly ArrayBufferWriter<byte> _writer = new();

        public bool Completed { get; private set; }

        public byte[] WrittenBytes => _writer.WrittenSpan.ToArray();

        public override IBufferWriter<byte> GetBufferWriter() => _writer;

        public override void Complete() => Completed = true;

        public override void Complete(byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            _writer.Write(payload);
            Completed = true;
        }
    }

    private sealed class SequenceDeserializationContext : DeserializationContext
    {
        private readonly ReadOnlySequence<byte> _sequence;

        public SequenceDeserializationContext(ReadOnlySequence<byte> sequence)
        {
            _sequence = sequence;
        }

        public override int PayloadLength => checked((int)_sequence.Length);

        public override byte[] PayloadAsNewBuffer() => _sequence.ToArray();

        public override ReadOnlySequence<byte> PayloadAsReadOnlySequence() => _sequence;
    }

    private sealed class ByteSegment : ReadOnlySequenceSegment<byte>
    {
        public ByteSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public ByteSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new ByteSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length,
            };
            Next = next;
            return next;
        }
    }

    [Fact]
    public void Constants_AndHelpers_ExposeExpectedCodecNamesAndContentTypes()
    {
        Assert.Equal("omnibuf", OmniBufGrpcMarshaller.CodecName);
        Assert.Equal("x-omnibuf", OmniBufGrpcMarshaller.SelfDescribingCodecName);
        Assert.Equal("application/grpc+omnibuf", OmniBufGrpcMarshaller.ContentType);
        Assert.Equal("application/grpc+x-omnibuf", OmniBufGrpcMarshaller.SelfDescribingContentType);

        Assert.Equal("omnibuf", OmniBufGrpcMarshaller.GetCodecName(MarshalOptions.Default));
        Assert.Equal("application/grpc+omnibuf", OmniBufGrpcMarshaller.GetContentType(MarshalOptions.Default));
        Assert.Equal("x-omnibuf", OmniBufGrpcMarshaller.GetCodecName(new MarshalOptions(SelfDescribing: true)));
        Assert.Equal("application/grpc+x-omnibuf", OmniBufGrpcMarshaller.GetContentType(new MarshalOptions(SelfDescribing: true)));
    }

    [Fact]
    public void Method_UnaryRpcRoundTrip_ExecutesContextualSerializerAndDeserializer()
    {
        var method = new Method<PingRequest, PingResponse>(
            MethodType.Unary,
            serviceName: "omnibuf.test.PingService",
            name: "Ping",
            requestMarshaller: OmniBufGrpcMarshaller.Create<PingRequest>(),
            responseMarshaller: OmniBufGrpcMarshaller.Create<PingResponse>());

        Assert.Equal("/omnibuf.test.PingService/Ping", method.FullName);

        var request = new PingRequest("edge-fra-01", 42UL, true);
        var reqSerCtx = new BufferSerializationContext();
        method.RequestMarshaller.ContextualSerializer(request, reqSerCtx);
        Assert.True(reqSerCtx.Completed);

        var reqDeserCtx = new SequenceDeserializationContext(new ReadOnlySequence<byte>(reqSerCtx.WrittenBytes));
        PingRequest serverRequest = method.RequestMarshaller.ContextualDeserializer(reqDeserCtx);
        Assert.Equal(request, serverRequest);

        var response = new PingResponse(serverRequest.Node, serverRequest.Seq + 1);
        var respSerCtx = new BufferSerializationContext();
        method.ResponseMarshaller.ContextualSerializer(response, respSerCtx);
        Assert.True(respSerCtx.Completed);

        var respDeserCtx = new SequenceDeserializationContext(new ReadOnlySequence<byte>(respSerCtx.WrittenBytes));
        PingResponse clientResponse = method.ResponseMarshaller.ContextualDeserializer(respDeserCtx);
        Assert.Equal(response, clientResponse);
    }

    [Fact]
    public void MultiSegmentSequence_DeserializesCorrectly()
    {
        Marshaller<PingRequest> marshaller = OmniBufGrpcMarshaller.Create<PingRequest>();
        var original = new PingRequest("edge-nrt-03", 99UL, true);

        var serCtx = new BufferSerializationContext();
        marshaller.ContextualSerializer(original, serCtx);
        byte[] wire = serCtx.WrittenBytes;
        int split = wire.Length / 2;

        var first = new ByteSegment(wire.AsMemory(0, split));
        ByteSegment last = first.Append(wire.AsMemory(split));
        var multiSegment = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        Assert.False(multiSegment.IsSingleSegment);

        PingRequest decoded = marshaller.ContextualDeserializer(new SequenceDeserializationContext(multiSegment));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void SelfDescribingMarshaller_EmitsField0AndRoundTrips()
    {
        Marshaller<PingRequest> compact = OmniBufGrpcMarshaller.Create<PingRequest>();
        Marshaller<PingRequest> selfDescribing = OmniBufGrpcMarshaller.CreateSelfDescribing<PingRequest>();
        var original = new PingRequest("edge-iad-02", 9001UL, false);

        var compactCtx = new BufferSerializationContext();
        compact.ContextualSerializer(original, compactCtx);

        var selfDescCtx = new BufferSerializationContext();
        selfDescribing.ContextualSerializer(original, selfDescCtx);
        Assert.True(selfDescCtx.WrittenBytes.Length > compactCtx.WrittenBytes.Length);

        PingRequest decoded = selfDescribing.ContextualDeserializer(
            new SequenceDeserializationContext(new ReadOnlySequence<byte>(selfDescCtx.WrittenBytes)));
        Assert.Equal(original, decoded);

        Dictionary<string, object?> blind = OmniBufBlind.DecodeBlind(selfDescCtx.WrittenBytes);
        Assert.Equal("edge-iad-02", blind["node"]);
        Assert.Equal(9001UL, blind["seq"]);
    }

    [Fact]
    public void UnmarshalOptions_EnforcedAndNullArgumentsRejected()
    {
        Marshaller<PingRequest> limited = OmniBufGrpcMarshaller.Create<PingRequest>(
            MarshalOptions.Default,
            new UnmarshalOptions(MaxMessageBytes: 4));

        var serCtx = new BufferSerializationContext();
        limited.ContextualSerializer(new PingRequest("payload-larger-than-four-bytes", 1UL, true), serCtx);

        var deserCtx = new SequenceDeserializationContext(new ReadOnlySequence<byte>(serCtx.WrittenBytes));
        Assert.Throws<OmniBufException>(() => limited.ContextualDeserializer(deserCtx));
        Assert.Throws<ArgumentNullException>(() => limited.ContextualSerializer(null!, serCtx));
        Assert.Throws<ArgumentNullException>(() => limited.ContextualSerializer(new PingRequest("a", 1UL, true), null!));
        Assert.Throws<ArgumentNullException>(() => limited.ContextualDeserializer(null!));
    }
}
