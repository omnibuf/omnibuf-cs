using OmniBuf;
using Xunit;

namespace OmniBuf.Tests;

public sealed class WirePrimitivesTests
{
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

        // Default MaxNestingDepth (32) rejects depth 80, while custom maxDepth=90 validates cleanly.
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
}
