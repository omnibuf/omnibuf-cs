using System.Buffers.Binary;
using System.Text;
using Google.Protobuf;
using Omnibuf.Bench;
using Omnibuf.Conformance;

namespace OmniBuf.ConformanceRunner;

public static class Program
{
    private const uint MaxFrameBytes = 16 * 1024 * 1024;
    private const uint WireFormatJson = 2;

    private const uint TierPlugin = 1;
    private const uint TierProtobufTypes = 2;
    private const uint TierAnnotatedStruct = 3;
    private const uint TierBlind = 4;

    private static readonly uint[] PeerGrowPrefixes = [100000u, 200000u, 300000u, 400000u, 500000u, 600000u];
    private static readonly string[] PeerShrinkTags = ["t"];
    private static readonly uint[] PeerShrinkPrefixes = [1u];
    private static readonly uint[] AllTypesGrowUint32Arr = [100000u, 200000u, 300000u, 400000u, 500000u];
    private static readonly string[] AllTypesShrinkStringArr = ["a"];
    private static readonly uint[] AllTypesShrinkUint32Arr = [7u];

    public static int Main()
    {
        using Stream stdin = Console.OpenStandardInput();
        using Stream stdout = Console.OpenStandardOutput();
        Span<byte> lenBuf = stackalloc byte[4];

        while (true)
        {
            int headerRead = ReadExactOrEof(stdin, lenBuf);
            if (headerRead == 0)
            {
                return 0;
            }

            if (headerRead < 4)
            {
                Console.Error.WriteLine("conformance-cs: truncated 4-byte frame length header");
                return 1;
            }

            uint frameLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
            if (frameLen > MaxFrameBytes)
            {
                Console.Error.WriteLine($"conformance-cs: frame size {frameLen} exceeds maximum {MaxFrameBytes}");
                return 1;
            }

            var reqBytes = new byte[frameLen];
            if (frameLen > 0)
            {
                int bodyRead = ReadExactOrEof(stdin, reqBytes);
                if (bodyRead < (int)frameLen)
                {
                    Console.Error.WriteLine("conformance-cs: unexpected EOF reading request body");
                    return 1;
                }
            }

            ConformanceRequestFrame req = OmniBufSerializer.Unmarshal<ConformanceRequestFrame>(reqBytes);
            ConformanceResponseFrame resp = HandleRequest(req);
            byte[] respBytes = OmniBufSerializer.Marshal(resp);

            BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)respBytes.Length);
            stdout.Write(lenBuf);
            if (respBytes.Length > 0)
            {
                stdout.Write(respBytes, 0, respBytes.Length);
            }

            stdout.Flush();
        }
    }

    private static int ReadExactOrEof(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer.Slice(total));
            if (n == 0)
            {
                return total;
            }

            total += n;
        }

        return total;
    }

    public static ConformanceResponseFrame HandleRequest(ConformanceRequestFrame req)
    {
        try
        {
            return req.Tier switch
            {
                TierBlind => HandleBlindTier(req),
                TierPlugin => HandlePluginTier(req),
                TierProtobufTypes => HandleProtobufTypesTier(req),
                TierAnnotatedStruct => HandleAnnotatedStructTier(req),
                _ => new ConformanceResponseFrame { RuntimeError = $"unsupported tier: {req.Tier}" },
            };
        }
        catch (OmniBufException ex)
        {
            return new ConformanceResponseFrame { ParseError = ex.Message };
        }
        catch (Exception ex)
        {
            return new ConformanceResponseFrame { RuntimeError = ex.Message };
        }
    }

    private static ConformanceResponseFrame HandleBlindTier(ConformanceRequestFrame req)
    {
        try
        {
            if (req.RequestedOutputFormat == WireFormatJson)
            {
                string json = OmniBufBlind.DecodeBlindJson(req.OmniBufPayload, fallbackOnNonFinite: true);
                return new ConformanceResponseFrame { JsonPayload = json };
            }

            _ = OmniBufBlind.DecodeBlind(req.OmniBufPayload);
            return new ConformanceResponseFrame();
        }
        catch (OmniBufException ex)
        {
            return new ConformanceResponseFrame { ParseError = ex.Message };
        }
    }

    private static ConformanceResponseFrame HandlePluginTier(ConformanceRequestFrame req)
    {
        var opts = new MarshalOptions(SelfDescribing: req.SelfDescribing, Deterministic: true);

        switch (req.MessageType)
        {
            case "omnibuf.bench.PeerUpdate":
                {
                    if (req.TestOpaqueSplice || req.TestOpaqueSpliceShrink)
                    {
                        var probe = new OpaquePeerUpdate(req.OmniBufPayload);
                        try
                        {
                            probe.Sequence = probe.Sequence + 1UL;
                        }
                        catch (OmniBufException ex)
                        {
                            return new ConformanceResponseFrame { ParseError = ex.Message };
                        }

                        if (!probe.TryMarshal(out _, out string? probeErr))
                        {
                            return new ConformanceResponseFrame { ParseError = probeErr ?? "opaque PeerUpdate splice validation failed" };
                        }

                        string? aliasErr = CheckSpliceAliasedPrefix(req.OmniBufPayload);
                        if (aliasErr is not null)
                        {
                            return new ConformanceResponseFrame { RuntimeError = aliasErr };
                        }
                    }

                    // Pre-populate stale values to verify UnmarshalOmniBuf clears absent fields and sub-messages.
                    var msg = new PeerUpdate
                    {
                        Asn = 999999u,
                        RouterId = "stale-router",
                        Active = true,
                        Location = new Location { Region = "stale", City = "stale-city", Rack = 999u },
                        Sequence = 999999UL,
                    };
                    msg.Tags.Add("stale-tag");
                    msg.Prefixes.Add(999u);
                    msg.Neighbors.Add(new Neighbor { PeerAsn = 999u, Router = "stale-neighbor" });

                    try
                    {
                        msg.UnmarshalOmniBuf(req.OmniBufPayload);
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { ParseError = ex.Message };
                    }

                    var op = new OpaquePeerUpdate(req.OmniBufPayload);
                    string opCity = op.GetLocationCity();
                    string wantCity = msg.Location?.City ?? string.Empty;
                    OpaqueLocation? opLoc = op.Location;
                    uint opLatBits = BitConverter.SingleToUInt32Bits(opLoc?.Latitude ?? 0f);
                    uint wantLatBits = BitConverter.SingleToUInt32Bits(msg.Location?.Latitude ?? 0f);
                    uint opLonBits = BitConverter.SingleToUInt32Bits(opLoc?.Longitude ?? 0f);
                    uint wantLonBits = BitConverter.SingleToUInt32Bits(msg.Location?.Longitude ?? 0f);
                    if (op.Asn != msg.Asn
                        || op.RouterId != msg.RouterId
                        || op.Active != msg.Active
                        || op.UptimeSec != msg.UptimeSec
                        || op.RssiDbm != msg.RssiDbm
                        || BitConverter.DoubleToUInt64Bits(op.CpuLoad) != BitConverter.DoubleToUInt64Bits(msg.CpuLoad)
                        || op.Tags.Count != msg.Tags.Count
                        || op.Prefixes.Count != msg.Prefixes.Count
                        || opCity != wantCity
                        || opLatBits != wantLatBits
                        || opLonBits != wantLonBits
                        || op.Neighbors.Count != msg.Neighbors.Count
                        || op.Sequence != msg.Sequence)
                    {
                        return new ConformanceResponseFrame { ParseError = "opaque PeerUpdate getter mismatch with decoded message" };
                    }

                    if (req.TestOpaqueSplice)
                    {
                        op.Asn = 1_000_000u;
                        op.RouterId = RepeatString("edge-rtr-fra01-", 20);
                        op.Active = !op.Active;
                        op.UptimeSec = ulong.MaxValue;
                        op.RssiDbm = -1_000_000;
                        op.CpuLoad = 99.875;
                        op.SetTags(new[] { "tag-grow-a", "tag-grow-b", RepeatString("long-tag-", 20) });
                        op.SetPrefixes(PeerGrowPrefixes);
                        op.SetLocation(new Location
                        {
                            Region = "eu-central-grown",
                            Datacenter = RepeatString("fra-dc-", 25),
                            City = "Berlin",
                            Rack = 999999u,
                            Latitude = 52.52f,
                            Longitude = 13.405f,
                        });
                        op.Sequence += 1UL;
                        return op.TryMarshal(out byte[] spliced, out string? spliceErr)
                            ? new ConformanceResponseFrame { OmniBufPayload = spliced }
                            : new ConformanceResponseFrame { ParseError = spliceErr ?? "opaque PeerUpdate splice failed" };
                    }

                    if (req.TestOpaqueSpliceShrink)
                    {
                        op.Asn = 1u;
                        op.RouterId = "r";
                        op.Active = false;
                        op.UptimeSec = 5UL;
                        op.RssiDbm = -1;
                        op.CpuLoad = 0.5;
                        op.SetTags(PeerShrinkTags);
                        op.SetPrefixes(PeerShrinkPrefixes);
                        op.SetLocation(new Location
                        {
                            Region = "r",
                            Datacenter = "d",
                            City = "c",
                            Rack = 1u,
                            Latitude = 1.0f,
                            Longitude = 2.0f,
                        });
                        op.Sequence = 2UL;
                        return op.TryMarshal(out byte[] shrunk, out string? shrinkErr)
                            ? new ConformanceResponseFrame { OmniBufPayload = shrunk }
                            : new ConformanceResponseFrame { ParseError = shrinkErr ?? "opaque PeerUpdate shrink splice failed" };
                    }

                    try
                    {
                        return new ConformanceResponseFrame { OmniBufPayload = msg.MarshalOmniBuf(opts) };
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { SerializeError = ex.Message };
                    }
                }

            case "omnibuf.conformance.TestAllTypes":
                {
                    if (req.TestOpaqueSplice || req.TestOpaqueSpliceShrink)
                    {
                        var probe = new OpaqueTestAllTypes(req.OmniBufPayload);
                        try
                        {
                            probe.F1Uint32 = probe.F1Uint32 + 1u;
                        }
                        catch (OmniBufException ex)
                        {
                            return new ConformanceResponseFrame { ParseError = ex.Message };
                        }

                        if (!probe.TryMarshal(out _, out string? probeErr))
                        {
                            return new ConformanceResponseFrame { ParseError = probeErr ?? "opaque TestAllTypes splice validation failed" };
                        }

                        string? aliasErr = CheckSpliceAliasedPrefix(req.OmniBufPayload);
                        if (aliasErr is not null)
                        {
                            return new ConformanceResponseFrame { RuntimeError = aliasErr };
                        }
                    }

                    var msg = new TestAllTypes
                    {
                        F1Uint32 = 999999u,
                        F5Bool = true,
                        F6String = "stale-str",
                        F7Bytes = ByteString.CopyFrom(0xff),
                        F8SubMsg = new AllTypesSubMsg { Id = 999u, Label = "stale-sub", Flag = true, Score = -999L },
                        F20OverflowStr = "stale-overflow",
                    };
                    msg.F9StringArr.Add("stale");
                    msg.F10Uint32Arr.Add(999u);
                    msg.F11SubMsgArr.Add(new AllTypesSubMsg { Id = 999u, Label = "stale-elem" });
                    msg.F12MapStrUint["stale"] = 999UL;

                    try
                    {
                        msg.UnmarshalOmniBuf(req.OmniBufPayload);
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { ParseError = ex.Message };
                    }

                    var op = new OpaqueTestAllTypes(req.OmniBufPayload);
                    AllTypesSubMsg wantSub = msg.F8SubMsg ?? new AllTypesSubMsg();
                    OpaqueAllTypesSubMsg? opSub = op.F8SubMsg;
                    uint opSubId = opSub?.Id ?? 0u;
                    string opSubLabel = opSub?.Label ?? string.Empty;
                    bool opSubFlag = opSub?.Flag ?? false;
                    long opSubScore = opSub?.Score ?? 0L;

                    if (op.F1Uint32 != msg.F1Uint32
                        || op.F2Uint64 != msg.F2Uint64
                        || op.F3Sint32 != msg.F3Sint32
                        || op.F4Sint64 != msg.F4Sint64
                        || op.F5Bool != msg.F5Bool
                        || op.F6String != msg.F6String
                        || !op.F7Bytes.AsSpan().SequenceEqual(msg.F7Bytes.Span)
                        || opSubId != wantSub.Id
                        || opSubLabel != wantSub.Label
                        || opSubFlag != wantSub.Flag
                        || opSubScore != wantSub.Score
                        || op.F9StringArr.Count != msg.F9StringArr.Count
                        || op.F10Uint32Arr.Count != msg.F10Uint32Arr.Count
                        || op.F11SubMsgArr.Count != msg.F11SubMsgArr.Count
                        || op.F12MapStrUint.Count != msg.F12MapStrUint.Count
                        || BitConverter.SingleToUInt32Bits(op.F13Float32) != BitConverter.SingleToUInt32Bits(msg.F13Float32)
                        || BitConverter.DoubleToUInt64Bits(op.F14Float64) != BitConverter.DoubleToUInt64Bits(msg.F14Float64)
                        || op.F15Fixed32 != msg.F15Fixed32
                        || op.F16Fixed64 != msg.F16Fixed64
                        || op.F20OverflowStr != msg.F20OverflowStr)
                    {
                        return new ConformanceResponseFrame { ParseError = "opaque TestAllTypes getter mismatch with decoded message" };
                    }

                    if (req.TestOpaqueSplice)
                    {
                        op.F1Uint32 = 1_000_000u;
                        op.F2Uint64 = ulong.MaxValue;
                        op.F3Sint32 = -1_000_000;
                        op.F4Sint64 = long.MinValue;
                        op.F5Bool = !op.F5Bool;
                        op.F6String = RepeatString("grown-str-", 30);
                        op.F7Bytes = RepeatBytes(new byte[] { 0xab, 0xcd }, 130);
                        op.SetF8SubMsg(new AllTypesSubMsg
                        {
                            Id = 888888u,
                            Label = RepeatString("sub-grown-", 20),
                            Flag = true,
                            Score = -999999999L,
                        });
                        op.SetF9StringArr(new[] { "arr-grow-1", "arr-grow-2", RepeatString("arr-grow-3-", 15) });
                        op.SetF10Uint32Arr(AllTypesGrowUint32Arr);
                        op.SetF11SubMsgArr(new[]
                        {
                            new AllTypesSubMsg { Id = 100001u, Label = RepeatString("elem0-", 25), Flag = true, Score = -500000L },
                            new AllTypesSubMsg { Id = 100002u, Label = "elem1-grown", Flag = false, Score = 500000L },
                        });
                        op.SetF12MapStrUint(new Dictionary<string, ulong>(StringComparer.Ordinal)
                        {
                            ["k1"] = 1000000UL,
                            ["k2"] = ulong.MaxValue,
                            ["k3_long_key"] = 424242UL,
                        });
                        op.F13Float32 = -1234.5f;
                        op.F14Float64 = 3.141592653589793d;
                        op.F15Fixed32 = 0xdeadbeefu;
                        op.F16Fixed64 = 0xcafebabedeadbeefUL;
                        op.F20OverflowStr = RepeatString("overflow20-", 20);
                        return op.TryMarshal(out byte[] spliced, out string? spliceErr)
                            ? new ConformanceResponseFrame { OmniBufPayload = spliced }
                            : new ConformanceResponseFrame { ParseError = spliceErr ?? "opaque TestAllTypes splice failed" };
                    }

                    if (req.TestOpaqueSpliceShrink)
                    {
                        op.F1Uint32 = 1u;
                        op.F2Uint64 = 2UL;
                        op.F3Sint32 = -1;
                        op.F4Sint64 = -2L;
                        op.F5Bool = false;
                        op.F6String = "s";
                        op.F7Bytes = new byte[] { 0x01 };
                        op.SetF8SubMsg(new AllTypesSubMsg { Id = 1u, Label = "x", Flag = false, Score = -1L });
                        op.SetF9StringArr(AllTypesShrinkStringArr);
                        op.SetF10Uint32Arr(AllTypesShrinkUint32Arr);
                        op.SetF11SubMsgArr(new[]
                        {
                            new AllTypesSubMsg { Id = 1u, Label = "e", Flag = true, Score = 1L },
                        });
                        op.SetF12MapStrUint(new Dictionary<string, ulong>(StringComparer.Ordinal) { ["k"] = 1UL });
                        op.F13Float32 = 0.5f;
                        op.F14Float64 = -0.25d;
                        op.F15Fixed32 = 7u;
                        op.F16Fixed64 = 9UL;
                        op.F20OverflowStr = "z";
                        return op.TryMarshal(out byte[] shrunk, out string? shrinkErr)
                            ? new ConformanceResponseFrame { OmniBufPayload = shrunk }
                            : new ConformanceResponseFrame { ParseError = shrinkErr ?? "opaque TestAllTypes shrink splice failed" };
                    }

                    try
                    {
                        return new ConformanceResponseFrame { OmniBufPayload = msg.MarshalOmniBuf(opts) };
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { SerializeError = ex.Message };
                    }
                }

            case "omnibuf.conformance.TestEditions2023":
                return HandlePluginEditions2023(req, opts);

            case "omnibuf.conformance.TestEditions2024":
                return HandlePluginEditions2024(req, opts);

            case "omnibuf.conformance.TestEditions2026":
                return HandlePluginEditions2026(req, opts);

            default:
                return new ConformanceResponseFrame { Skipped = "plugin tier generated for PeerUpdate, TestAllTypes, and TestEditions2023/2024/2026" };
        }
    }

    private static ConformanceResponseFrame HandlePluginEditions2023(ConformanceRequestFrame req, MarshalOptions opts)
    {
        var msg = new TestEditions2023
        {
            ExplicitInt = 999,
            ExplicitBool = true,
            ExplicitString = "stale",
            ExplicitBytes = ByteString.CopyFrom(0xff),
            ExplicitDouble = 9.5,
            Status = Editions2023Status.Active,
            ImplicitCount = 999,
            ImplicitFlag = true,
            Child = new Editions2023Child { Id = 999, Note = "stale-child" },
            DelimitedChild = new Editions2023Child { Id = 888, Note = "stale-delim" },
            ChoiceText = "stale-choice",
        };
        msg.PackedVals.Add(999);
        msg.ExpandedVals.Add(888);
        msg.Scores["stale"] = 999;

        try
        {
            msg.UnmarshalOmniBuf(req.OmniBufPayload);
        }
        catch (OmniBufException ex)
        {
            return new ConformanceResponseFrame { ParseError = ex.Message };
        }

        var op = new OpaqueTestEditions2023(req.OmniBufPayload);
        if (op.HasExplicitInt != msg.HasExplicitInt
            || op.ExplicitInt != msg.ExplicitInt
            || op.HasExplicitBool != msg.HasExplicitBool
            || op.ExplicitBool != msg.ExplicitBool
            || op.HasExplicitString != msg.HasExplicitString
            || op.ExplicitString != msg.ExplicitString
            || op.HasExplicitBytes != msg.HasExplicitBytes
            || !op.ExplicitBytes.AsSpan().SequenceEqual(msg.ExplicitBytes.Span)
            || op.HasExplicitDouble != msg.HasExplicitDouble
            || BitConverter.DoubleToUInt64Bits(op.ExplicitDouble) != BitConverter.DoubleToUInt64Bits(msg.ExplicitDouble)
            || op.HasStatus != msg.HasStatus
            || op.Status != msg.Status
            || op.ImplicitCount != msg.ImplicitCount
            || op.ImplicitFlag != msg.ImplicitFlag
            || !ChildMatches2023(op.Child, msg.Child)
            || !ChildMatches2023(op.DelimitedChild, msg.DelimitedChild)
            || !op.PackedVals.SequenceEqual(msg.PackedVals)
            || !op.ExpandedVals.SequenceEqual(msg.ExpandedVals)
            || !MapMatches(op.Scores, msg.Scores)
            || (msg.ChoiceCase == TestEditions2023.ChoiceOneofCase.ChoiceInt) != op.HasChoiceInt
            || (op.HasChoiceInt && op.ChoiceInt != msg.ChoiceInt)
            || (msg.ChoiceCase == TestEditions2023.ChoiceOneofCase.ChoiceText) != op.HasChoiceText
            || (op.HasChoiceText && op.ChoiceText != msg.ChoiceText)
            || (msg.ChoiceCase == TestEditions2023.ChoiceOneofCase.ChoiceFlag) != op.HasChoiceFlag
            || (op.HasChoiceFlag && op.ChoiceFlag != msg.ChoiceFlag)
            || (msg.ChoiceCase == TestEditions2023.ChoiceOneofCase.ChoiceChild) != (op.ChoiceChild is not null)
            || (op.ChoiceChild is not null && !ChildMatches2023(op.ChoiceChild, msg.ChoiceChild)))
        {
            return new ConformanceResponseFrame { ParseError = "opaque TestEditions2023 getter mismatch with decoded message" };
        }

        try
        {
            return new ConformanceResponseFrame { OmniBufPayload = msg.MarshalOmniBuf(opts) };
        }
        catch (OmniBufException ex)
        {
            return new ConformanceResponseFrame { SerializeError = ex.Message };
        }
    }

    private static ConformanceResponseFrame HandlePluginEditions2024(ConformanceRequestFrame req, MarshalOptions opts)
    {
        var msg = new TestEditions2024
        {
            ExplicitInt = 999,
            ExplicitBool = true,
            ExplicitString = "stale",
            ExplicitBytes = ByteString.CopyFrom(0xff),
            ExplicitDouble = 9.5,
            Status = Editions2024Status.Active,
            ImplicitCount = 999,
            ImplicitFlag = true,
            Child = new Editions2024Child { Id = 999, Note = "stale-child" },
            DelimitedChild = new Editions2024Child { Id = 888, Note = "stale-delim" },
            ChoiceText = "stale-choice",
        };
        msg.PackedVals.Add(999);
        msg.ExpandedVals.Add(888);
        msg.Scores["stale"] = 999;

        try
        {
            msg.UnmarshalOmniBuf(req.OmniBufPayload);
        }
        catch (OmniBufException ex)
        {
            return new ConformanceResponseFrame { ParseError = ex.Message };
        }

        var op = new OpaqueTestEditions2024(req.OmniBufPayload);
        if (op.HasExplicitInt != msg.HasExplicitInt
            || op.ExplicitInt != msg.ExplicitInt
            || op.HasExplicitBool != msg.HasExplicitBool
            || op.ExplicitBool != msg.ExplicitBool
            || op.HasExplicitString != msg.HasExplicitString
            || op.ExplicitString != msg.ExplicitString
            || op.HasExplicitBytes != msg.HasExplicitBytes
            || !op.ExplicitBytes.AsSpan().SequenceEqual(msg.ExplicitBytes.Span)
            || op.HasExplicitDouble != msg.HasExplicitDouble
            || BitConverter.DoubleToUInt64Bits(op.ExplicitDouble) != BitConverter.DoubleToUInt64Bits(msg.ExplicitDouble)
            || op.HasStatus != msg.HasStatus
            || op.Status != msg.Status
            || op.ImplicitCount != msg.ImplicitCount
            || op.ImplicitFlag != msg.ImplicitFlag
            || !ChildMatches2024(op.Child, msg.Child)
            || !ChildMatches2024(op.DelimitedChild, msg.DelimitedChild)
            || !op.PackedVals.SequenceEqual(msg.PackedVals)
            || !op.ExpandedVals.SequenceEqual(msg.ExpandedVals)
            || !MapMatches(op.Scores, msg.Scores)
            || (msg.ChoiceCase == TestEditions2024.ChoiceOneofCase.ChoiceInt) != op.HasChoiceInt
            || (op.HasChoiceInt && op.ChoiceInt != msg.ChoiceInt)
            || (msg.ChoiceCase == TestEditions2024.ChoiceOneofCase.ChoiceText) != op.HasChoiceText
            || (op.HasChoiceText && op.ChoiceText != msg.ChoiceText)
            || (msg.ChoiceCase == TestEditions2024.ChoiceOneofCase.ChoiceFlag) != op.HasChoiceFlag
            || (op.HasChoiceFlag && op.ChoiceFlag != msg.ChoiceFlag)
            || (msg.ChoiceCase == TestEditions2024.ChoiceOneofCase.ChoiceChild) != (op.ChoiceChild is not null)
            || (op.ChoiceChild is not null && !ChildMatches2024(op.ChoiceChild, msg.ChoiceChild)))
        {
            return new ConformanceResponseFrame { ParseError = "opaque TestEditions2024 getter mismatch with decoded message" };
        }

        try
        {
            return new ConformanceResponseFrame { OmniBufPayload = msg.MarshalOmniBuf(opts) };
        }
        catch (OmniBufException ex)
        {
            return new ConformanceResponseFrame { SerializeError = ex.Message };
        }
    }

    private static ConformanceResponseFrame HandlePluginEditions2026(ConformanceRequestFrame req, MarshalOptions opts)
    {
        var msg = new TestEditions2026
        {
            ExplicitInt = 999,
            ExplicitBool = true,
            ExplicitString = "stale",
            ExplicitBytes = ByteString.CopyFrom(0xff),
            ExplicitDouble = 9.5,
            Status = Editions2026Status.Active,
            ImplicitCount = 999,
            ImplicitFlag = true,
            Child = new Editions2026Child { Id = 999, Note = "stale-child" },
            DelimitedChild = new Editions2026Child { Id = 888, Note = "stale-delim" },
            ChoiceText = "stale-choice",
        };
        msg.PackedVals.Add(999);
        msg.ExpandedVals.Add(888);
        msg.Scores["stale"] = 999;

        try
        {
            msg.UnmarshalOmniBuf(req.OmniBufPayload);
        }
        catch (OmniBufException ex)
        {
            return new ConformanceResponseFrame { ParseError = ex.Message };
        }

        var op = new OpaqueTestEditions2026(req.OmniBufPayload);
        if (op.HasExplicitInt != msg.HasExplicitInt
            || op.ExplicitInt != msg.ExplicitInt
            || op.HasExplicitBool != msg.HasExplicitBool
            || op.ExplicitBool != msg.ExplicitBool
            || op.HasExplicitString != msg.HasExplicitString
            || op.ExplicitString != msg.ExplicitString
            || op.HasExplicitBytes != msg.HasExplicitBytes
            || !op.ExplicitBytes.AsSpan().SequenceEqual(msg.ExplicitBytes.Span)
            || op.HasExplicitDouble != msg.HasExplicitDouble
            || BitConverter.DoubleToUInt64Bits(op.ExplicitDouble) != BitConverter.DoubleToUInt64Bits(msg.ExplicitDouble)
            || op.HasStatus != msg.HasStatus
            || op.Status != msg.Status
            || op.ImplicitCount != msg.ImplicitCount
            || op.ImplicitFlag != msg.ImplicitFlag
            || !ChildMatches2026(op.Child, msg.Child)
            || !ChildMatches2026(op.DelimitedChild, msg.DelimitedChild)
            || !op.PackedVals.SequenceEqual(msg.PackedVals)
            || !op.ExpandedVals.SequenceEqual(msg.ExpandedVals)
            || !MapMatches(op.Scores, msg.Scores)
            || (msg.ChoiceCase == TestEditions2026.ChoiceOneofCase.ChoiceInt) != op.HasChoiceInt
            || (op.HasChoiceInt && op.ChoiceInt != msg.ChoiceInt)
            || (msg.ChoiceCase == TestEditions2026.ChoiceOneofCase.ChoiceText) != op.HasChoiceText
            || (op.HasChoiceText && op.ChoiceText != msg.ChoiceText)
            || (msg.ChoiceCase == TestEditions2026.ChoiceOneofCase.ChoiceFlag) != op.HasChoiceFlag
            || (op.HasChoiceFlag && op.ChoiceFlag != msg.ChoiceFlag)
            || (msg.ChoiceCase == TestEditions2026.ChoiceOneofCase.ChoiceChild) != (op.ChoiceChild is not null)
            || (op.ChoiceChild is not null && !ChildMatches2026(op.ChoiceChild, msg.ChoiceChild)))
        {
            return new ConformanceResponseFrame { ParseError = "opaque TestEditions2026 getter mismatch with decoded message" };
        }

        try
        {
            return new ConformanceResponseFrame { OmniBufPayload = msg.MarshalOmniBuf(opts) };
        }
        catch (OmniBufException ex)
        {
            return new ConformanceResponseFrame { SerializeError = ex.Message };
        }
    }

    private static bool ChildMatches2023(OpaqueEditions2023Child? op, Editions2023Child? msg)
    {
        if (op is null || msg is null)
        {
            return op is null && msg is null;
        }

        return op.HasId == msg.HasId
            && op.Id == msg.Id
            && op.HasNote == msg.HasNote
            && op.Note == msg.Note;
    }

    private static bool ChildMatches2024(OpaqueEditions2024Child? op, Editions2024Child? msg)
    {
        if (op is null || msg is null)
        {
            return op is null && msg is null;
        }

        return op.HasId == msg.HasId
            && op.Id == msg.Id
            && op.HasNote == msg.HasNote
            && op.Note == msg.Note;
    }

    private static bool ChildMatches2026(OpaqueEditions2026Child? op, Editions2026Child? msg)
    {
        if (op is null || msg is null)
        {
            return op is null && msg is null;
        }

        return op.HasId == msg.HasId
            && op.Id == msg.Id
            && op.HasNote == msg.HasNote
            && op.Note == msg.Note;
    }

    private static bool MapMatches(IReadOnlyDictionary<string, int> op, IDictionary<string, int> msg)
    {
        if (op.Count != msg.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, int> kv in msg)
        {
            if (!op.TryGetValue(kv.Key, out int val) || val != kv.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static string? CheckSpliceAliasedPrefix(byte[] packet)
    {
        static void AppendDirty(OmniBufEncoder enc, uint fnum)
        {
            enc.WriteUintField(fnum, 1UL << 40);
        }

        byte[] spliced = OmniBufOpaque.SpliceFields(packet, 1UL << 1, null, AppendDirty);
        foreach (int prefixLen in new[] { 0, 3, 32 })
        {
            var prefix = new byte[prefixLen];
            Array.Fill(prefix, (byte)0xa5);

            var storage = new byte[prefixLen + packet.Length + 512];
            Buffer.BlockCopy(prefix, 0, storage, 0, prefixLen);
            Buffer.BlockCopy(packet, 0, storage, prefixLen, packet.Length);

            var want = new byte[prefixLen + spliced.Length];
            Buffer.BlockCopy(prefix, 0, want, 0, prefixLen);
            Buffer.BlockCopy(spliced, 0, want, prefixLen, spliced.Length);

            byte[] got = OmniBufOpaque.SpliceFields(
                new ArraySegment<byte>(storage, 0, prefixLen),
                new ArraySegment<byte>(storage, prefixLen, packet.Length),
                1UL << 1,
                null,
                AppendDirty);

            if (!got.AsSpan().SequenceEqual(want))
            {
                return $"opaque splice with {prefixLen}-byte aliased prefix changed prefix or output";
            }

            if (!storage.AsSpan(prefixLen, packet.Length).SequenceEqual(packet))
            {
                return $"opaque splice with {prefixLen}-byte aliased prefix modified source packet";
            }
        }

        return null;
    }

    private static ConformanceResponseFrame HandleProtobufTypesTier(ConformanceRequestFrame req)
    {
        var opts = new MarshalOptions(SelfDescribing: req.SelfDescribing, Deterministic: true);
        switch (req.MessageType)
        {
            case "omnibuf.bench.PeerUpdate":
                {
                    var msg = new PeerUpdate
                    {
                        Asn = 999u,
                        RouterId = "stale",
                        Active = true,
                        Location = new Location { City = "stale" },
                    };
                    msg.Tags.Add("stale");

                    try
                    {
                        OmniBufProtobuf.Unmarshal(req.OmniBufPayload, msg);
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { ParseError = ex.Message };
                    }

                    try
                    {
                        return new ConformanceResponseFrame { OmniBufPayload = OmniBufProtobuf.Marshal(msg, opts) };
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { SerializeError = ex.Message };
                    }
                }

            case "omnibuf.conformance.TestAllTypes":
                {
                    var msg = new TestAllTypes
                    {
                        F1Uint32 = 999u,
                        F5Bool = true,
                        F6String = "stale",
                        F8SubMsg = new AllTypesSubMsg { Id = 999u, Label = "stale" },
                    };
                    msg.F9StringArr.Add("stale");
                    msg.F12MapStrUint["stale"] = 999UL;

                    try
                    {
                        OmniBufProtobuf.Unmarshal(req.OmniBufPayload, msg);
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { ParseError = ex.Message };
                    }

                    try
                    {
                        return new ConformanceResponseFrame { OmniBufPayload = OmniBufProtobuf.Marshal(msg, opts) };
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { SerializeError = ex.Message };
                    }
                }

            case "omnibuf.conformance.TestEditions2023":
                {
                    var msg = new TestEditions2023
                    {
                        ExplicitInt = 999,
                        ExplicitBool = true,
                        ExplicitString = "stale",
                        ExplicitBytes = ByteString.CopyFrom(0xff),
                        ExplicitDouble = 9.5,
                        Status = Editions2023Status.Active,
                        ImplicitCount = 999,
                        ImplicitFlag = true,
                        Child = new Editions2023Child { Id = 999, Note = "stale-child" },
                        DelimitedChild = new Editions2023Child { Id = 888, Note = "stale-delim" },
                        ChoiceText = "stale-choice",
                    };
                    msg.PackedVals.Add(999);
                    msg.ExpandedVals.Add(888);
                    msg.Scores["stale"] = 999;

                    try
                    {
                        OmniBufProtobuf.Unmarshal(req.OmniBufPayload, msg);
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { ParseError = ex.Message };
                    }

                    try
                    {
                        return new ConformanceResponseFrame { OmniBufPayload = OmniBufProtobuf.Marshal(msg, opts) };
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { SerializeError = ex.Message };
                    }
                }

            case "omnibuf.conformance.TestEditions2024":
                {
                    var msg = new TestEditions2024
                    {
                        ExplicitInt = 999,
                        ExplicitBool = true,
                        ExplicitString = "stale",
                        ExplicitBytes = ByteString.CopyFrom(0xff),
                        ExplicitDouble = 9.5,
                        Status = Editions2024Status.Active,
                        ImplicitCount = 999,
                        ImplicitFlag = true,
                        Child = new Editions2024Child { Id = 999, Note = "stale-child" },
                        DelimitedChild = new Editions2024Child { Id = 888, Note = "stale-delim" },
                        ChoiceText = "stale-choice",
                    };
                    msg.PackedVals.Add(999);
                    msg.ExpandedVals.Add(888);
                    msg.Scores["stale"] = 999;

                    try
                    {
                        OmniBufProtobuf.Unmarshal(req.OmniBufPayload, msg);
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { ParseError = ex.Message };
                    }

                    try
                    {
                        return new ConformanceResponseFrame { OmniBufPayload = OmniBufProtobuf.Marshal(msg, opts) };
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { SerializeError = ex.Message };
                    }
                }

            case "omnibuf.conformance.TestEditions2026":
                {
                    var msg = new TestEditions2026
                    {
                        ExplicitInt = 999,
                        ExplicitBool = true,
                        ExplicitString = "stale",
                        ExplicitBytes = ByteString.CopyFrom(0xff),
                        ExplicitDouble = 9.5,
                        Status = Editions2026Status.Active,
                        ImplicitCount = 999,
                        ImplicitFlag = true,
                        Child = new Editions2026Child { Id = 999, Note = "stale-child" },
                        DelimitedChild = new Editions2026Child { Id = 888, Note = "stale-delim" },
                        ChoiceText = "stale-choice",
                    };
                    msg.PackedVals.Add(999);
                    msg.ExpandedVals.Add(888);
                    msg.Scores["stale"] = 999;

                    try
                    {
                        OmniBufProtobuf.Unmarshal(req.OmniBufPayload, msg);
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { ParseError = ex.Message };
                    }

                    try
                    {
                        return new ConformanceResponseFrame { OmniBufPayload = OmniBufProtobuf.Marshal(msg, opts) };
                    }
                    catch (OmniBufException ex)
                    {
                        return new ConformanceResponseFrame { SerializeError = ex.Message };
                    }
                }

            default:
                return new ConformanceResponseFrame { Skipped = "protobuf types tier exercises PeerUpdate, TestAllTypes, and TestEditions2023/2024/2026" };
        }
    }

    private static ConformanceResponseFrame HandleAnnotatedStructTier(ConformanceRequestFrame req)
    {
        var opts = new MarshalOptions(SelfDescribing: req.SelfDescribing, Deterministic: true);
        try
        {
            switch (req.MessageType)
            {
                case "omnibuf.bench.PeerUpdate":
                    {
                        var msg = new AnnotatedPeerUpdate
                        {
                            Asn = 999u,
                            RouterId = "stale",
                            Active = true,
                            Tags = new List<string> { "stale" },
                            Location = new AnnotatedLocation { City = "stale" },
                        };
                        OmniBufSerializer.Unmarshal(req.OmniBufPayload, msg);
                        return new ConformanceResponseFrame { OmniBufPayload = OmniBufSerializer.Marshal(msg, opts) };
                    }

                case "omnibuf.conformance.TreeNode":
                    {
                        AnnotatedTreeNode msg = OmniBufSerializer.Unmarshal<AnnotatedTreeNode>(req.OmniBufPayload);
                        return new ConformanceResponseFrame { OmniBufPayload = OmniBufSerializer.Marshal(msg, opts) };
                    }

                case "omnibuf.conformance.NestedCluster":
                    {
                        AnnotatedNestedCluster msg = OmniBufSerializer.Unmarshal<AnnotatedNestedCluster>(req.OmniBufPayload);
                        return new ConformanceResponseFrame { OmniBufPayload = OmniBufSerializer.Marshal(msg, opts) };
                    }

                case "omnibuf.conformance.TestAllTypes":
                    {
                        var msg = new AnnotatedTestAllTypes
                        {
                            F1Uint32 = 999u,
                            F5Bool = true,
                            F6String = "stale",
                            F8SubMsg = new AnnotatedAllTypesSubMsg(999u, "stale", true, -1L),
                            F9StringArr = new List<string> { "stale" },
                            F12MapStrUint = new Dictionary<string, ulong> { ["stale"] = 999UL },
                        };
                        OmniBufSerializer.Unmarshal(req.OmniBufPayload, msg);
                        return new ConformanceResponseFrame { OmniBufPayload = OmniBufSerializer.Marshal(msg, opts) };
                    }

                default:
                    return new ConformanceResponseFrame { Skipped = $"unsupported annotated struct type: {req.MessageType}" };
            }
        }
        catch (OmniBufException ex)
        {
            return new ConformanceResponseFrame { ParseError = ex.Message };
        }
    }

    private static string RepeatString(string part, int count)
    {
        var sb = new StringBuilder(part.Length * count);
        for (int i = 0; i < count; i++)
        {
            sb.Append(part);
        }

        return sb.ToString();
    }

    private static byte[] RepeatBytes(ReadOnlySpan<byte> pattern, int count)
    {
        var result = new byte[pattern.Length * count];
        for (int i = 0; i < count; i++)
        {
            pattern.CopyTo(result.AsSpan(i * pattern.Length));
        }

        return result;
    }
}
