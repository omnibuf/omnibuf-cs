using Omnibuf.Bench;
using OmniBuf;
using Xunit;

namespace OmniBuf.Tests;

public sealed class WirePrimitivesTests
{
    [OmniBufMessage(TypeName = "LimitTestPoco")]
    private sealed class LimitTestPoco
    {
        [OmniBufField(1, Name = "items")]
        public List<uint> Items { get; set; } = new();

        [OmniBufField(2, Name = "child")]
        public LimitTestPoco? Child { get; set; }

        [OmniBufField(3, Name = "labels")]
        public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(1UL, 1)]
    [InlineData(127UL, 1)]
    [InlineData(128UL, 2)]
    [InlineData(16383UL, 2)]
    [InlineData(16384UL, 4)]
    [InlineData(536_870_911UL, 4)]
    [InlineData(536_870_912UL, 9)]
    [InlineData(ulong.MaxValue, 9)]
    public void PrefixVarint_Boundaries_RoundTripWithExpectedWidth(ulong value, int expectedBytes)
    {
        var enc = new OmniBufEncoder(16);
        enc.WriteVarint(value);
        ReadOnlySpan<byte> wire = enc.WrittenSpan;
        Assert.Equal(expectedBytes, wire.Length);

        bool ok = OmniBufDecoder.TryDecodeVarint(wire, out ulong decoded, out int bytesRead);
        Assert.True(ok);
        Assert.Equal(value, decoded);
        Assert.Equal(expectedBytes, bytesRead);
    }

    [Theory]
    [InlineData((byte)0x08)]
    [InlineData((byte)0x10)]
    [InlineData((byte)0x80)]
    [InlineData((byte)0xF8)]
    public void PrefixVarint_RejectsReservedNonZeroLow3ZeroMarkers(byte marker)
    {
        byte[] buf = new byte[9];
        buf[0] = marker;

        Assert.False(OmniBufDecoder.TryDecodeVarint(buf, out _, out _));

        Assert.Throws<OmniBufException>(() =>
        {
            int pos = 0;
            OmniBufDecoder.ReadVarint(buf, ref pos);
        });
    }

    [Fact]
    public void PrefixVarint_AcceptsNonMinimalEncodings()
    {
        // Value 5 encoded in 2-byte Prefix-Varint: (5 << 2) | 0b10 = 22 (0x16, 0x00)
        byte[] twoByteFive = { 0x16, 0x00 };
        Assert.True(OmniBufDecoder.TryDecodeVarint(twoByteFive, out ulong v2, out int n2));
        Assert.Equal(5UL, v2);
        Assert.Equal(2, n2);

        // Value 5 encoded in 9-byte Prefix-Varint: 0x00 followed by 8-byte LE 5
        byte[] nineByteFive = { 0x00, 0x05, 0, 0, 0, 0, 0, 0, 0 };
        Assert.True(OmniBufDecoder.TryDecodeVarint(nineByteFive, out ulong v9, out int n9));
        Assert.Equal(5UL, v9);
        Assert.Equal(9, n9);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1L)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void ZigZag64_RoundTripsExtremes(long value)
    {
        ulong encoded = OmniBufConstants.ZigZagEncode64(value);
        long decoded = OmniBufConstants.ZigZagDecode64(encoded);
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void ReadSint32AndUint32_TruncatePerSpec11_1_WhileCheckedVariantsRejectOverflow()
    {
        var sintEnc = new OmniBufEncoder(16);
        long sintOverflow = (long)int.MaxValue + 1L;
        sintEnc.WriteSintValue(sintOverflow);
        byte[] sintBytes = sintEnc.ToArray();

        var decSint = new OmniBufDecoder(sintBytes);
        Assert.Equal(unchecked((int)sintOverflow), decSint.ReadSint32());

        Assert.Throws<OmniBufException>(() =>
        {
            var dec = new OmniBufDecoder(sintBytes);
            dec.ReadSint32Checked();
        });

        var uintEnc = new OmniBufEncoder(16);
        ulong uintOverflow = (ulong)uint.MaxValue + 5UL;
        uintEnc.WriteVarint(uintOverflow);
        byte[] uintBytes = uintEnc.ToArray();

        var decUint = new OmniBufDecoder(uintBytes);
        Assert.Equal(unchecked((uint)uintOverflow), decUint.ReadUint32());

        Assert.Throws<OmniBufException>(() =>
        {
            var dec = new OmniBufDecoder(uintBytes);
            dec.ReadUint32Checked();
        });
    }

    [Fact]
    public void Encoder_BackpatchesContainerPromotionBeyond16383Bytes()
    {
        string largePayload = new('x', 20_000);
        var enc = new OmniBufEncoder(64);
        enc.BeginSubMsgField(1);
        enc.WriteStringField(2, largePayload);
        enc.EndSubMsg();

        byte[] packet = enc.ToArray();
        int pos = 0;
        (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(packet, ref pos);
        Assert.Equal(1u, fnum);
        Assert.Equal(OmniBufWireType.SubMsg, wt);

        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(packet, ref pos, 0);
        Assert.Equal(packet.Length, pos);

        int subPos = 0;
        (uint innerFnum, OmniBufWireType innerWt) = OmniBufDecoder.ReadTag(sub, ref subPos);
        Assert.Equal(2u, innerFnum);
        Assert.Equal(OmniBufWireType.String, innerWt);
        string decoded = OmniBufDecoder.ReadString(sub, ref subPos);
        Assert.Equal(largePayload, decoded);
    }

    [Fact]
    public void Encoder_DynamicallyGrowsContainerStackBeyond64Levels()
    {
        const int depth = 80;
        var enc = new OmniBufEncoder(128, maxDepth: 90);
        for (int i = 0; i < depth; i++)
        {
            enc.BeginSubMsgField(1);
        }

        enc.WriteUintField(2, 42);

        for (int i = 0; i < depth; i++)
        {
            enc.EndSubMsg();
        }

        byte[] packet = enc.ToArray();
        Assert.True(packet.Length > depth * 2);

        // Default MaxNestingDepth (32) rejects depth 80, while custom maxDepth=90 validates cleanly
        Assert.Throws<OmniBufException>(() =>
        {
            int pos = 0;
            (uint _, OmniBufWireType wt) = OmniBufDecoder.ReadTag(packet, ref pos);
            OmniBufDecoder.ValidateValue(packet, ref pos, wt, 0, OmniBufConstants.MaxNestingDepth);
        });

        int p2 = 0;
        (uint rootFnum, OmniBufWireType rootWt) = OmniBufDecoder.ReadTag(packet, ref p2);
        Assert.Equal(1u, rootFnum);
        OmniBufDecoder.ValidateValue(packet, ref p2, rootWt, 0, maxDepth: 90);
        Assert.Equal(packet.Length, p2);
    }

    [Fact]
    public void Encoder_BeginContainer_EnforcesMaxNestingDepth()
    {
        var enc = new OmniBufEncoder();
        for (int i = 0; i < OmniBufConstants.MaxNestingDepth; i++)
        {
            enc.BeginContainer();
        }

        Assert.Throws<OmniBufException>(() => enc.BeginContainer());
        Assert.Throws<OmniBufException>(() => enc.BeginContainer(sizeHint: 32_768));

        for (int i = 0; i < OmniBufConstants.MaxNestingDepth; i++)
        {
            enc.EndContainer();
        }

        Assert.Equal(0, enc.ContainerDepth);
    }

    [Fact]
    public void Encoder_ThreadLocalReentrancyGuardAndLargeBufferShrink()
    {
        OmniBufEncoder first = OmniBufEncoder.RentThreadLocal(out bool rentedFirst);
        Assert.True(rentedFirst);
        try
        {
            first.WriteUintField(1u, 42UL);

            // Nested rent while first is active must allocate an independent encoder
            OmniBufEncoder nested = OmniBufEncoder.RentThreadLocal(out bool rentedNested);
            Assert.False(rentedNested);
            Assert.NotSame(first, nested);
            nested.WriteStringField(2u, "nested");
            OmniBufEncoder.ReturnThreadLocal(nested, rentedNested);

            Assert.True(first.Length > 0);

            // Force capacity above 65,536 bytes and verify ReturnThreadLocal shrinks the retained buffer
            first.WriteStringField(3u, new string('z', 80_000));
            Assert.True(first.Capacity > 65_536);
        }
        finally
        {
            OmniBufEncoder.ReturnThreadLocal(first, rentedFirst);
        }

        OmniBufEncoder afterReturn = OmniBufEncoder.RentThreadLocal(out bool rentedAfter);
        try
        {
            Assert.True(rentedAfter);
            Assert.Same(first, afterReturn);
            Assert.Equal(512, afterReturn.Capacity);
        }
        finally
        {
            OmniBufEncoder.ReturnThreadLocal(afterReturn, rentedAfter);
        }
    }

    [Fact]
    public void Encoder_WriteNonAsciiStringTail_PreservesAsciiPrefixOnBufferGrow()
    {
        // Initial capacity 32; write 16 bytes so remaining space is 16.
        // String has 10 ASCII chars + 3 3-byte UTF-8 chars (13 chars total).
        // minNeeded = 1 + 13 = 14 <= 16 (no grow before Ascii.FromUtf16).
        // worstCaseNeeded = 9 + 10 + 9 = 28 > 16 (forces grow in WriteNonAsciiStringTail).
        var enc = new OmniBufEncoder(32);
        enc.WriteFixed64Value(0x0102030405060708UL);
        enc.WriteFixed64Value(0x1112131415161718UL);
        const string expected = "abcdefghij\u20AC\u20AC\u20AC";
        enc.WriteStringValue(expected);

        byte[] buf = enc.ToArray();
        int pos = 16;
        string actual = OmniBufDecoder.ReadString(buf, ref pos);
        Assert.Equal(expected, actual);
        Assert.Equal(buf.Length, pos);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xC0, 0x80 })] // overlong NUL
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })] // UTF-16 high surrogate U+D800
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 })] // > U+10FFFF
    [InlineData(new byte[] { 0xE2, 0x82 })] // truncated 3-byte sequence
    public void StrictUtf8_RejectsMalformedSequences(byte[] invalidUtf8)
    {
        var enc = new OmniBufEncoder(32);
        enc.WriteTag(1, OmniBufWireType.String);
        enc.WriteBytesValue(invalidUtf8);
        byte[] packet = enc.ToArray();

        Assert.Throws<OmniBufException>(() =>
        {
            int pos = 0;
            (_, OmniBufWireType wt) = OmniBufDecoder.ReadTag(packet, ref pos);
            OmniBufDecoder.ValidateValue(packet, ref pos, wt, 0, OmniBufConstants.MaxNestingDepth);
        });
    }

    [Theory]
    [InlineData((byte)0x02)]
    [InlineData((byte)0xFF)]
    public void ReadBoolElement_RejectsNonCanonicalPaddingBits(byte badBoolByte)
    {
        byte[] data = { badBoolByte };
        Assert.Throws<OmniBufException>(() =>
        {
            int pos = 0;
            OmniBufDecoder.ReadBoolElement(data, ref pos);
        });
    }

    [Fact]
    public void EmptyContainers_RejectTrailingBytes()
    {
        // WireArray with ElemType=Uint (0), Count=0 (encoded as 0x01), plus 1 trailing garbage byte 0xAA
        var arrEnc = new OmniBufEncoder(16);
        arrEnc.WriteTag(1, OmniBufWireType.Array);
        arrEnc.WriteBytesValue(new byte[] { (byte)OmniBufWireType.Uint, 0x01, 0xAA });
        byte[] badArr = arrEnc.ToArray();

        Assert.Throws<OmniBufException>(() =>
        {
            int pos = 0;
            (_, OmniBufWireType wt) = OmniBufDecoder.ReadTag(badArr, ref pos);
            OmniBufDecoder.ValidateValue(badArr, ref pos, wt, 0, OmniBufConstants.MaxNestingDepth);
        });

        // WireMap with KeyType=String (8), ValType=Uint (0), Count=0 (0x01), plus trailing byte 0xBB
        var mapEnc = new OmniBufEncoder(16);
        mapEnc.WriteTag(1, OmniBufWireType.Map);
        mapEnc.WriteBytesValue(new byte[] { (byte)OmniBufWireType.String, (byte)OmniBufWireType.Uint, 0x01, 0xBB });
        byte[] badMap = mapEnc.ToArray();

        Assert.Throws<OmniBufException>(() =>
        {
            int pos = 0;
            (_, OmniBufWireType wt) = OmniBufDecoder.ReadTag(badMap, ref pos);
            OmniBufDecoder.ValidateValue(badMap, ref pos, wt, 0, OmniBufConstants.MaxNestingDepth);
        });
    }

    [Fact]
    public void WireExtended_SkipsAny1ByteSubtypeAndRejectsZeroBytePayload()
    {
        foreach (byte subType in new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFF })
        {
            var enc = new OmniBufEncoder(32);
            enc.WriteTag(5, OmniBufWireType.Extended);
            enc.WriteBytesValue(new byte[] { subType, 0x11, 0x22 });
            byte[] okPacket = enc.ToArray();

            int pos = 0;
            (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(okPacket, ref pos);
            Assert.Equal(5u, fnum);
            Assert.Equal(OmniBufWireType.Extended, wt);
            OmniBufDecoder.ValidateValue(okPacket, ref pos, wt, 0, OmniBufConstants.MaxNestingDepth);
            Assert.Equal(okPacket.Length, pos);
        }

        // Zero-length WireExtended lacks required 1-byte SubType header
        var badEnc = new OmniBufEncoder(16);
        badEnc.WriteTag(5, OmniBufWireType.Extended);
        badEnc.WriteBytesValue(ReadOnlySpan<byte>.Empty);
        byte[] badPacket = badEnc.ToArray();

        Assert.Throws<OmniBufException>(() =>
        {
            int pos = 0;
            (_, OmniBufWireType wt) = OmniBufDecoder.ReadTag(badPacket, ref pos);
            OmniBufDecoder.ValidateValue(badPacket, ref pos, wt, 0, OmniBufConstants.MaxNestingDepth);
        });
    }

    [Fact]
    public void MarshalOptions_DefaultIsDeterministicAndNonSelfDescribing()
    {
        MarshalOptions def = default;
        Assert.True(def.Deterministic);
        Assert.False(def.SelfDescribing);

        var constructed = new MarshalOptions();
        Assert.True(constructed.Deterministic);
        Assert.False(constructed.SelfDescribing);
    }

    [Fact]
    public void UnmarshalOptions_DefaultsAndExplicitZero()
    {
        UnmarshalOptions def = default;
        Assert.Equal(UnmarshalOptions.DefaultMaxMessageBytes, def.MaxMessageBytes);
        Assert.Equal(UnmarshalOptions.DefaultMaxCollectionElements, def.MaxCollectionElements);
        Assert.Equal(UnmarshalOptions.DefaultMaxNestingDepth, def.MaxNestingDepth);
        Assert.Equal(def, new UnmarshalOptions());

        var zeroDepth = new UnmarshalOptions { MaxNestingDepth = 0 };
        Assert.Equal(0, zeroDepth.MaxNestingDepth);
        Assert.Equal(UnmarshalOptions.DefaultMaxMessageBytes, zeroDepth.MaxMessageBytes);
        Assert.Equal(UnmarshalOptions.DefaultMaxCollectionElements, zeroDepth.MaxCollectionElements);

        var custom = new UnmarshalOptions(MaxMessageBytes: 128, MaxCollectionElements: 5, MaxNestingDepth: 2);
        Assert.Equal(128, custom.MaxMessageBytes);
        Assert.Equal(5, custom.MaxCollectionElements);
        Assert.Equal(2, custom.MaxNestingDepth);
    }

    [Fact]
    public void UnmarshalOptions_EnforcesMessageBytesCollectionElementsAndDepthAcrossDecoders()
    {
        var poco = new LimitTestPoco
        {
            Items = new List<uint> { 1, 2, 3, 4 },
            Child = new LimitTestPoco
            {
                Child = new LimitTestPoco { Items = new List<uint> { 9 } },
            },
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["a"] = "1",
                ["b"] = "2",
                ["c"] = "3",
            },
        };
        byte[] packet = OmniBufSerializer.Marshal(poco, new MarshalOptions(SelfDescribing: true));

        // 1. MaxMessageBytes
        var tinyBytes = new UnmarshalOptions { MaxMessageBytes = packet.Length - 1 };
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Unmarshal<LimitTestPoco>(packet, tinyBytes));
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Unmarshal(packet, new LimitTestPoco(), tinyBytes));
        Assert.Throws<OmniBufException>(() => OmniBufDecoder.ValidateMessage(packet, tinyBytes));
        Assert.Throws<OmniBufException>(() => OmniBufBlind.DecodeBlind(packet, tinyBytes));
        Assert.Throws<OmniBufException>(() => OmniBufBlind.DecodeBlindJson(packet, tinyBytes));
        Assert.Throws<OmniBufException>(() => SchemaMeta.ReadField0(packet, tinyBytes));
        Assert.Throws<OmniBufException>(() => _ = new OmniBufDecoder(packet, tinyBytes));

        // 2. MaxCollectionElements
        var tinyCol = new UnmarshalOptions { MaxCollectionElements = 2 };
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Unmarshal<LimitTestPoco>(packet, tinyCol));
        Assert.Throws<OmniBufException>(() => OmniBufDecoder.ValidateMessage(packet, tinyCol));
        Assert.Throws<OmniBufException>(() => OmniBufBlind.DecodeBlind(packet, tinyCol));
        Assert.Throws<OmniBufException>(() => SchemaMeta.ReadField0(packet, tinyCol));

        // 3. MaxNestingDepth
        var tinyDepth = new UnmarshalOptions { MaxNestingDepth = 1 };
        Assert.Throws<OmniBufException>(() => OmniBufSerializer.Unmarshal<LimitTestPoco>(packet, tinyDepth));
        Assert.Throws<OmniBufException>(() => OmniBufDecoder.ValidateMessage(packet, tinyDepth));
        Assert.Throws<OmniBufException>(() => OmniBufBlind.DecodeBlind(packet, tinyDepth));

        // 4. OmniBufProtobuf limits
        var peer = new PeerUpdate();
        peer.Tags.Add("a");
        peer.Tags.Add("b");
        peer.Tags.Add("c");
        byte[] peerBytes = OmniBufProtobuf.Marshal(peer);
        Assert.Throws<OmniBufException>(() => OmniBufProtobuf.Unmarshal<PeerUpdate>(peerBytes, new UnmarshalOptions { MaxMessageBytes = 1 }));
        Assert.Throws<OmniBufException>(() => OmniBufProtobuf.Unmarshal<PeerUpdate>(peerBytes, new UnmarshalOptions { MaxCollectionElements = 2 }));
        Assert.Throws<OmniBufException>(() => OmniBufProtobuf.Unmarshal<PeerUpdate>(peerBytes, new UnmarshalOptions { MaxNestingDepth = 0 }));

        // Sufficient limits succeed
        var generous = new UnmarshalOptions(MaxMessageBytes: 4096, MaxCollectionElements: 10, MaxNestingDepth: 16);
        LimitTestPoco roundTripped = OmniBufSerializer.Unmarshal<LimitTestPoco>(packet, generous);
        Assert.Equal(4, roundTripped.Items.Count);
        PeerUpdate peerOut = OmniBufProtobuf.Unmarshal<PeerUpdate>(peerBytes, generous);
        Assert.Equal(3, peerOut.Tags.Count);
    }

    [Fact]
    public void Wrappers_ReadElem_EnforcesThreadedDepth()
    {
        var enc = new OmniBufEncoder();
        enc.BeginContainer();
        OmniBufWrappers.WriteInt32Body(enc, 42);
        enc.EndContainer();
        byte[] elemBytes = enc.ToArray();

        int pos = 0;
        Assert.Equal(42, OmniBufWrappers.ReadElemInt32(elemBytes, ref pos, depth: 5, maxDepth: 10));

        int pos2 = 0;
        Assert.Throws<OmniBufException>(() => OmniBufWrappers.ReadElemInt32(elemBytes, ref pos2, depth: 11, maxDepth: 10));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(127)]
    public void ReadContainer_DecodesCanonicalOneByteOddLengthSubMsg(int bodyLen)
    {
        byte[] body;
        string expectedRegion;
        if (bodyLen == 1)
        {
            // Field 7, WireType BoolTrue ((7 << 4) | 3 = 115 -> 1-byte varint 0xE7): valid 1-byte unknown field
            body = [0xE7];
            expectedRegion = string.Empty;
        }
        else
        {
            // Location.Region (field 1, String, tag 0x35) + 1-byte string len + payload = bodyLen
            int strLen = bodyLen - 2;
            expectedRegion = new string('r', strLen);
            body = new byte[bodyLen];
            body[0] = 0x35;
            body[1] = (byte)((strLen << 1) | 1);
            System.Text.Encoding.UTF8.GetBytes(expectedRegion, body.AsSpan(2));
        }

        // PeerUpdate: field 9 (Location, SubMsg, tag [0x72, 0x02]) + canonical 1-byte odd len + body
        // + trailing field 11 (Sequence = 42, Uint, tag [0xC2, 0x02], value 0x55).
        byte[] packet = new byte[2 + 1 + bodyLen + 3];
        packet[0] = 0x72;
        packet[1] = 0x02;
        packet[2] = (byte)((bodyLen << 1) | 1);
        body.CopyTo(packet.AsSpan(3));
        packet[3 + bodyLen] = 0xC2;
        packet[4 + bodyLen] = 0x02;
        packet[5 + bodyLen] = (byte)((42 << 1) | 1);

        PeerUpdate parsed = PeerUpdate.ParseOmniBuf(packet);
        Assert.NotNull(parsed.Location);
        Assert.Equal(expectedRegion, parsed.Location.Region);
        Assert.Equal(42UL, parsed.Sequence);
    }

    [Fact]
    public void ReadContainer_RejectsCorruptOneByteSubMsgPayload()
    {
        // PeerUpdate.Location (tag [0x72, 0x02]) with canonical 1-byte len=1 (0x03) and corrupt 1-byte body 0x00
        byte[] corrupt = [0x72, 0x02, 0x03, 0x00];
        Assert.Throws<OmniBufException>(() => PeerUpdate.ParseOmniBuf(corrupt));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(127)]
    public void ReadContainer_DecodesCanonicalOneByteOddLengthWireArray(int bodyLen)
    {
        // PeerUpdate.Prefixes (field 8, WireArray<Uint>, tag [0x36, 0x02]).
        // Array body: elemType (Uint = 0x00) + 1-byte count + count * 1-byte uint values = bodyLen.
        int count = bodyLen - 2;
        byte[] body = new byte[bodyLen];
        body[0] = (byte)OmniBufWireType.Uint;
        body[1] = (byte)((count << 1) | 1);
        for (int i = 0; i < count; i++)
        {
            uint v = (uint)(i % 120);
            body[2 + i] = (byte)((v << 1) | 1);
        }

        // Append trailing field 11 (Sequence = 99) to verify exact container advancement
        byte[] packet = new byte[2 + 1 + bodyLen + 3];
        packet[0] = 0x36;
        packet[1] = 0x02;
        packet[2] = (byte)((bodyLen << 1) | 1);
        body.CopyTo(packet.AsSpan(3));
        packet[3 + bodyLen] = 0xC2;
        packet[4 + bodyLen] = 0x02;
        packet[5 + bodyLen] = (byte)((99 << 1) | 1);

        PeerUpdate parsed = PeerUpdate.ParseOmniBuf(packet);
        Assert.Equal(count, parsed.Prefixes.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal((uint)(i % 120), parsed.Prefixes[i]);
        }

        Assert.Equal(99UL, parsed.Sequence);
    }

    [Fact]
    public void ReadContainer_RejectsOneByteLengthWireArrayBelowMinBodyLen()
    {
        // PeerUpdate.Prefixes with canonical 1-byte len=1 (0x03) and 1-byte body [0x00] (< minBodyLen 2)
        byte[] invalid = [0x36, 0x02, 0x03, 0x00];
        Assert.Throws<OmniBufException>(() => PeerUpdate.ParseOmniBuf(invalid));
    }

    [Fact]
    public void Decoder_RejectsNegativePosInMatchTag2AndTryFastSkipValue()
    {
        byte[] data = [0x72, 0x02, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

        Assert.False(OmniBufDecoder.MatchTag2(data, -1, 0x0272));
        Assert.False(OmniBufDecoder.MatchTag2(data, int.MinValue, 0x0272));
        Assert.False(OmniBufDecoder.MatchTag2(ReadOnlySpan<byte>.Empty, -1, 0x0272));

        OmniBufWireType[] fixedTypes =
        [
            OmniBufWireType.Fixed32,
            OmniBufWireType.Sfixed32,
            OmniBufWireType.Float32,
            OmniBufWireType.Fixed64,
            OmniBufWireType.Sfixed64,
            OmniBufWireType.Float64,
        ];

        foreach (OmniBufWireType wt in fixedTypes)
        {
            int negOne = -1;
            Assert.False(OmniBufDecoder.TryFastSkipValue(data, ref negOne, wt));
            Assert.Equal(-1, negOne);

            int minPos = int.MinValue;
            Assert.False(OmniBufDecoder.TryFastSkipValue(data, ref minPos, wt));
            Assert.Equal(int.MinValue, minPos);
        }
    }
}
