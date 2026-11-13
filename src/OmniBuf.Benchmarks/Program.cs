using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using MessagePack;
using Omnibuf.Bench;
using OmniBuf;
using OmniBuf.ConformanceRunner;

namespace OmniBuf.Benchmarks;

public static class Program
{
    public static int Main(string[] args)
    {
        string outPath = ResolveOutputPath(args);

        PeerUpdate protoPeer = CreateSampleProtoPeerUpdate();
        AnnotatedPeerUpdate annotatedPeer = CreateSampleAnnotatedPeerUpdate();
        MpPeerUpdate mpPeer = CreateSampleMessagePackPeerUpdate();
        JsonPeerUpdate jsonPeer = CreateSampleJsonPeerUpdate();

        byte[] omnibufWire = protoPeer.MarshalOmniBuf();
        byte[] annotatedWire = OmniBufSerializer.Marshal(annotatedPeer);
        byte[] protobufWire = protoPeer.ToByteArray();
        byte[] msgpackWire = MessagePackSerializer.Serialize(mpPeer);
        byte[] jsonWire = JsonSerializer.SerializeToUtf8Bytes(jsonPeer, WorkloadJsonContext.Default.JsonPeerUpdate);

        if (omnibufWire.Length != 366)
        {
            throw new InvalidOperationException($"Expected OmniBuf wire size 366B, got {omnibufWire.Length}B");
        }

        if (!annotatedWire.AsSpan().SequenceEqual(omnibufWire))
        {
            throw new InvalidOperationException("AnnotatedPeerUpdate wire bytes diverged from generated PeerUpdate wire bytes");
        }

        if (protobufWire.Length != 341)
        {
            throw new InvalidOperationException($"Expected Google.Protobuf wire size 341B, got {protobufWire.Length}B");
        }

        VerifyRoundTrips(omnibufWire, protobufWire, msgpackWire, jsonWire);

        string runtimeDesc = $".NET {Environment.Version}";
        string platform = GetNormalizedPlatform();
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string cpu = DetectCpuModel();

        Console.WriteLine($"OmniBuf C# Benchmark Suite ({runtimeDesc}, {platform}/{arch}, {cpu})");
        Console.WriteLine($"Wire sizes: OmniBuf={omnibufWire.Length}B, Google.Protobuf={protobufWire.Length}B, MessagePack={msgpackWire.Length}B, JSON={jsonWire.Length}B");
        Console.WriteLine();

        int sink = 0;

        var encodeCategory = new BenchCategory
        {
            Id = "encode",
            Title = "Full Message Encode Speed",
            Unit = "ns",
            Results = MeasureCategory(new[]
            {
                new ContenderSpec(
                    "OmniBuf (Generated)",
                    WireBytes: omnibufWire.Length,
                    Highlight: true,
                    Baseline: true,
                    Action: () =>
                    {
                        byte[] b = protoPeer.MarshalOmniBuf();
                        sink ^= b.Length;
                    }),
                new ContenderSpec(
                    "OmniBuf ([OmniBufField])",
                    WireBytes: annotatedWire.Length,
                    Highlight: true,
                    Baseline: false,
                    Action: () =>
                    {
                        byte[] b = OmniBufSerializer.Marshal(annotatedPeer);
                        sink ^= b.Length;
                    }),
                new ContenderSpec(
                    "Google.Protobuf",
                    WireBytes: protobufWire.Length,
                    Highlight: false,
                    Baseline: false,
                    Action: () =>
                    {
                        byte[] b = protoPeer.ToByteArray();
                        sink ^= b.Length;
                    }),
                new ContenderSpec(
                    "MessagePack-CSharp",
                    WireBytes: msgpackWire.Length,
                    Highlight: false,
                    Baseline: false,
                    Action: () =>
                    {
                        byte[] b = MessagePackSerializer.Serialize(mpPeer);
                        sink ^= b.Length;
                    }),
                new ContenderSpec(
                    "System.Text.Json",
                    WireBytes: jsonWire.Length,
                    Highlight: false,
                    Baseline: false,
                    Action: () =>
                    {
                        byte[] b = JsonSerializer.SerializeToUtf8Bytes(jsonPeer, WorkloadJsonContext.Default.JsonPeerUpdate);
                        sink ^= b.Length;
                    }),
            }),
        };
        PrintCategory(encodeCategory);

        var decodeCategory = new BenchCategory
        {
            Id = "decode",
            Title = "Full Message Decode Speed",
            Unit = "ns",
            Results = MeasureCategory(new[]
            {
                new ContenderSpec(
                    "OmniBuf (Generated)",
                    WireBytes: null,
                    Highlight: true,
                    Baseline: true,
                    Action: () =>
                    {
                        PeerUpdate m = PeerUpdate.ParseOmniBuf(omnibufWire);
                        sink ^= (int)m.Asn;
                    }),
                new ContenderSpec(
                    "OmniBuf ([OmniBufField])",
                    WireBytes: null,
                    Highlight: true,
                    Baseline: false,
                    Action: () =>
                    {
                        AnnotatedPeerUpdate m = OmniBufSerializer.Unmarshal<AnnotatedPeerUpdate>(omnibufWire);
                        sink ^= (int)m.Asn;
                    }),
                new ContenderSpec(
                    "Google.Protobuf",
                    WireBytes: null,
                    Highlight: false,
                    Baseline: false,
                    Action: () =>
                    {
                        PeerUpdate m = PeerUpdate.Parser.ParseFrom(protobufWire);
                        sink ^= (int)m.Asn;
                    }),
                new ContenderSpec(
                    "MessagePack-CSharp",
                    WireBytes: null,
                    Highlight: false,
                    Baseline: false,
                    Action: () =>
                    {
                        MpPeerUpdate m = MessagePackSerializer.Deserialize<MpPeerUpdate>(msgpackWire);
                        sink ^= (int)m.Asn;
                    }),
                new ContenderSpec(
                    "System.Text.Json",
                    WireBytes: null,
                    Highlight: false,
                    Baseline: false,
                    Action: () =>
                    {
                        JsonPeerUpdate m = JsonSerializer.Deserialize(jsonWire, WorkloadJsonContext.Default.JsonPeerUpdate)!;
                        sink ^= (int)m.Asn;
                    }),
            }),
        };
        PrintCategory(decodeCategory);

        var lookupCategory = new BenchCategory
        {
            Id = "lookup_city",
            Title = "Read a Single Nested Field (peer.location.city)",
            Unit = "ns",
            Results = MeasureCategory(new[]
            {
                new ContenderSpec(
                    "OmniBuf (Generated)",
                    WireBytes: null,
                    Highlight: true,
                    Baseline: true,
                    Action: () =>
                    {
                        string city = new OpaquePeerUpdate(omnibufWire).GetLocationCity();
                        sink ^= city.Length;
                    }),
                new ContenderSpec(
                    "Google.Protobuf",
                    WireBytes: null,
                    Highlight: false,
                    Baseline: false,
                    Action: () =>
                    {
                        string city = PeerUpdate.Parser.ParseFrom(protobufWire).Location.City;
                        sink ^= city.Length;
                    }),
                new ContenderSpec(
                    "MessagePack-CSharp",
                    WireBytes: null,
                    Highlight: false,
                    Baseline: false,
                    Action: () =>
                    {
                        string city = MessagePackSerializer.Deserialize<MpPeerUpdate>(msgpackWire).Location!.City;
                        sink ^= city.Length;
                    }),
                new ContenderSpec(
                    "System.Text.Json",
                    WireBytes: null,
                    Highlight: false,
                    Baseline: false,
                    Action: () =>
                    {
                        string city = JsonSerializer.Deserialize(jsonWire, WorkloadJsonContext.Default.JsonPeerUpdate)!.Location!.City;
                        sink ^= city.Length;
                    }),
            }),
        };
        PrintCategory(lookupCategory);

        ulong seqCounter = 9876543211UL;
        var mutateCategory = new BenchCategory
        {
            Id = "mutate_sequence",
            Title = "Selective Single-Field Mutation (peer.sequence)",
            Unit = "ns",
            Results = MeasureCategory(new[]
            {
                new ContenderSpec(
                    "OmniBuf (Generated)",
                    WireBytes: null,
                    Highlight: true,
                    Baseline: true,
                    Action: () =>
                    {
                        var op = new OpaquePeerUpdate(omnibufWire);
                        op.Sequence = ++seqCounter;
                        byte[] outBytes = op.MarshalOmniBuf();
                        sink ^= outBytes.Length;
                    }),
                new ContenderSpec(
                    "Google.Protobuf",
                    WireBytes: null,
                    Highlight: false,
                    Baseline: false,
                    Action: () =>
                    {
                        PeerUpdate m = PeerUpdate.Parser.ParseFrom(protobufWire);
                        m.Sequence = ++seqCounter;
                        byte[] outBytes = m.ToByteArray();
                        sink ^= outBytes.Length;
                    }),
                new ContenderSpec(
                    "MessagePack-CSharp",
                    WireBytes: null,
                    Highlight: false,
                    Baseline: false,
                    Action: () =>
                    {
                        MpPeerUpdate m = MessagePackSerializer.Deserialize<MpPeerUpdate>(msgpackWire);
                        m.Sequence = ++seqCounter;
                        byte[] outBytes = MessagePackSerializer.Serialize(m);
                        sink ^= outBytes.Length;
                    }),
                new ContenderSpec(
                    "System.Text.Json",
                    WireBytes: null,
                    Highlight: false,
                    Baseline: false,
                    Action: () =>
                    {
                        JsonPeerUpdate m = JsonSerializer.Deserialize(jsonWire, WorkloadJsonContext.Default.JsonPeerUpdate)!;
                        m.Sequence = ++seqCounter;
                        byte[] outBytes = JsonSerializer.SerializeToUtf8Bytes(m, WorkloadJsonContext.Default.JsonPeerUpdate);
                        sink ^= outBytes.Length;
                    }),
            }),
        };
        PrintCategory(mutateCategory);

        if (sink == int.MinValue)
        {
            Console.WriteLine(sink);
        }

        var report = new BenchReport
        {
            Language = "csharp",
            Runtime = runtimeDesc,
            Platform = platform,
            Arch = arch,
            Cpu = cpu,
            Workload = "PeerUpdate",
            Categories = new List<BenchCategory>
            {
                encodeCategory,
                decodeCategory,
                lookupCategory,
                mutateCategory,
            },
        };

        string? dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string jsonOut = JsonSerializer.Serialize(report, ReportJsonContext.Default.BenchReport) + "\n";
        File.WriteAllText(outPath, jsonOut);
        Console.WriteLine($"Wrote benchmark report to {outPath}");
        return 0;
    }

    private sealed record ContenderSpec(
        string Name,
        long? WireBytes,
        bool Highlight,
        bool Baseline,
        Action Action);

    private static List<BenchRow> MeasureCategory(IReadOnlyList<ContenderSpec> specs)
    {
        var rows = new List<BenchRow>(specs.Count);
        foreach (ContenderSpec spec in specs)
        {
            (double nsPerOp, long bytesPerOp) = RunTimedBenchmark(spec.Action);
            long roundedNs = Math.Max(1L, (long)Math.Round(nsPerOp));
            long opsPerSec = (long)Math.Round(1_000_000_000.0 / nsPerOp);
            rows.Add(new BenchRow
            {
                Name = spec.Name,
                NsPerOp = roundedNs,
                OpsPerSec = opsPerSec,
                BytesPerOp = bytesPerOp > 0 ? bytesPerOp : null,
                WireBytes = spec.WireBytes,
                Highlight = spec.Highlight ? true : null,
                Baseline = spec.Baseline ? true : null,
            });
        }

        rows.Sort((a, b) => a.NsPerOp.CompareTo(b.NsPerOp));
        return rows;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (double NsPerOp, long BytesPerOp) RunTimedBenchmark(Action action)
    {
        double ticksToNs = 1_000_000_000.0 / Stopwatch.Frequency;

        // Phase 1: RyuJIT Tier-0 -> Tier-1 Dynamic PGO warmup (>= 400ms).
        long warmupTicks1 = (long)(Stopwatch.Frequency * 0.40);
        long start = Stopwatch.GetTimestamp();
        int batch = 256;
        while (Stopwatch.GetTimestamp() - start < warmupTicks1)
        {
            for (int i = 0; i < batch; i++)
            {
                action();
            }
        }

        // Allow background Tier-1 Dynamic PGO recompilation thread to install optimized code.
        Thread.Sleep(25);

        // Phase 2: Post-tier-up warmup + batch calibration (~100ms).
        long warmupTicks2 = (long)(Stopwatch.Frequency * 0.10);
        long calStart = Stopwatch.GetTimestamp();
        long calOps = 0;
        while (Stopwatch.GetTimestamp() - calStart < warmupTicks2)
        {
            for (int i = 0; i < batch; i++)
            {
                action();
            }

            calOps += batch;
        }

        long calElapsed = Math.Max(1L, Stopwatch.GetTimestamp() - calStart);
        double estOpsPerTrial = calOps * ((Stopwatch.Frequency * 0.18) / calElapsed);
        int trialIterations = (int)Math.Clamp(estOpsPerTrial, 1_000, 5_000_000);

        // Measure exact per-operation allocation on the current thread after JIT tier-up.
        action();
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        const int allocProbes = 64;
        for (int i = 0; i < allocProbes; i++)
        {
            action();
        }

        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        long bytesPerOp = Math.Max(0L, (long)Math.Round((allocAfter - allocBefore) / (double)allocProbes));

        // Phase 3: 5 timed trials after clean GC, taking median ns/op.
        const int trials = 5;
        var trialNs = new double[trials];
        for (int t = 0; t < trials; t++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < trialIterations; i++)
            {
                action();
            }

            long t1 = Stopwatch.GetTimestamp();
            trialNs[t] = ((t1 - t0) * ticksToNs) / trialIterations;
        }

        Array.Sort(trialNs);
        return (trialNs[trials / 2], bytesPerOp);
    }

    private static void PrintCategory(BenchCategory category)
    {
        Console.WriteLine($"=== {category.Title} ({category.Id}) ===");
        foreach (BenchRow r in category.Results)
        {
            string wireInfo = r.WireBytes is { } wb ? $", wire={wb}B" : string.Empty;
            string allocInfo = r.BytesPerOp is { } bp ? $", alloc={bp}B/op" : ", alloc=0B/op";
            Console.WriteLine($"  {r.Name,-26} {r.NsPerOp,6} ns/op  ({r.OpsPerSec,10:N0} ops/s{allocInfo}{wireInfo})");
        }

        Console.WriteLine();
    }

    private static void VerifyRoundTrips(byte[] omnibufWire, byte[] protobufWire, byte[] msgpackWire, byte[] jsonWire)
    {
        PeerUpdate decodedGen = PeerUpdate.ParseOmniBuf(omnibufWire);
        AnnotatedPeerUpdate decodedAnn = OmniBufSerializer.Unmarshal<AnnotatedPeerUpdate>(omnibufWire);
        string city = new OpaquePeerUpdate(omnibufWire).GetLocationCity();
        if (decodedGen.Asn != 64512 || decodedAnn.Asn != 64512 || city != "Frankfurt")
        {
            throw new InvalidOperationException("OmniBuf round-trip verification failed");
        }

        var opaque = new OpaquePeerUpdate(omnibufWire) { Sequence = 9876543211UL };
        byte[] spliced = opaque.MarshalOmniBuf();
        PeerUpdate splicedDecoded = PeerUpdate.ParseOmniBuf(spliced);
        if (splicedDecoded.Sequence != 9876543211UL || splicedDecoded.Location.City != "Frankfurt")
        {
            throw new InvalidOperationException("OpaquePeerUpdate splice verification failed");
        }

        if (PeerUpdate.Parser.ParseFrom(protobufWire).Location.City != "Frankfurt")
        {
            throw new InvalidOperationException("Google.Protobuf round-trip verification failed");
        }

        if (MessagePackSerializer.Deserialize<MpPeerUpdate>(msgpackWire).Location?.City != "Frankfurt")
        {
            throw new InvalidOperationException("MessagePack-CSharp round-trip verification failed");
        }

        if (JsonSerializer.Deserialize(jsonWire, WorkloadJsonContext.Default.JsonPeerUpdate)?.Location?.City != "Frankfurt")
        {
            throw new InvalidOperationException("System.Text.Json round-trip verification failed");
        }
    }

    private static string ResolveOutputPath(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] is "--out" or "-o")
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        string cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "OmniBuf.sln");
        if (File.Exists(cwdCandidate))
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "testdata", "benchmarks.json");
        }

        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OmniBuf.sln")))
            {
                return Path.Combine(dir.FullName, "testdata", "benchmarks.json");
            }

            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine("testdata", "benchmarks.json"));
    }

    private static string GetNormalizedPlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "darwin";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        return Environment.OSVersion.Platform.ToString().ToLowerInvariant();
    }

    private static string DetectCpuModel()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("sysctl", "-n machdep.cpu.brand_string")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                using Process? p = Process.Start(psi);
                if (p is not null)
                {
                    string s = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(2000);
                    if (!string.IsNullOrEmpty(s))
                    {
                        return s;
                    }
                }
            }
            else if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = line.IndexOf(':');
                        if (idx >= 0)
                        {
                            return line[(idx + 1)..].Trim();
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore probe failure and fall back to architecture label.
        }

        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static PeerUpdate CreateSampleProtoPeerUpdate()
    {
        var p = new PeerUpdate
        {
            Asn = 64512,
            RouterId = "edge-rtr-fra01.core.backbone.net",
            Active = true,
            UptimeSec = 1849203,
            RssiDbm = -62,
            CpuLoad = 0.42875,
            Location = new Location
            {
                Region = "eu-central-1",
                Datacenter = "FRA-Equinix-FR5",
                City = "Frankfurt",
                Rack = 42,
                Latitude = 50.1109f,
                Longitude = 8.6821f,
            },
            Sequence = 9876543210UL,
        };
        p.Tags.AddRange(new[] { "tier1", "ebgp", "eu-central", "ixp-de-cix" });
        p.Prefixes.AddRange(new uint[] { 167772160, 172032000, 3232235520, 2886729728, 134744072, 134743040 });
        p.Neighbors.Add(new Neighbor { PeerAsn = 3356, Router = "ae0.fra1.level3.net", LatencyUs = 850, Established = true });
        p.Neighbors.Add(new Neighbor { PeerAsn = 1299, Router = "ffm-b5-link.telia.net", LatencyUs = 920, Established = true });
        p.Neighbors.Add(new Neighbor { PeerAsn = 174, Router = "be2040.ccr41.fra03.atlas.cogentco.com", LatencyUs = 1120, Established = true });
        p.Neighbors.Add(new Neighbor { PeerAsn = 2914, Router = "ae-12.r24.frnkge08.de.bb.gin.ntt.net", LatencyUs = 980, Established = false });
        return p;
    }

    private static AnnotatedPeerUpdate CreateSampleAnnotatedPeerUpdate() => new()
    {
        Asn = 64512,
        RouterId = "edge-rtr-fra01.core.backbone.net",
        Active = true,
        UptimeSec = 1849203,
        RssiDbm = -62,
        CpuLoad = 0.42875,
        Tags = new List<string> { "tier1", "ebgp", "eu-central", "ixp-de-cix" },
        Prefixes = new List<uint> { 167772160, 172032000, 3232235520, 2886729728, 134744072, 134743040 },
        Location = new AnnotatedLocation
        {
            Region = "eu-central-1",
            Datacenter = "FRA-Equinix-FR5",
            City = "Frankfurt",
            Rack = 42,
            Latitude = 50.1109f,
            Longitude = 8.6821f,
        },
        Neighbors = new List<AnnotatedNeighbor>
        {
            new() { PeerAsn = 3356, Router = "ae0.fra1.level3.net", LatencyUs = 850, Established = true },
            new() { PeerAsn = 1299, Router = "ffm-b5-link.telia.net", LatencyUs = 920, Established = true },
            new() { PeerAsn = 174, Router = "be2040.ccr41.fra03.atlas.cogentco.com", LatencyUs = 1120, Established = true },
            new() { PeerAsn = 2914, Router = "ae-12.r24.frnkge08.de.bb.gin.ntt.net", LatencyUs = 980, Established = false },
        },
        Sequence = 9876543210UL,
    };

    private static MpPeerUpdate CreateSampleMessagePackPeerUpdate() => new()
    {
        Asn = 64512,
        RouterId = "edge-rtr-fra01.core.backbone.net",
        Active = true,
        UptimeSec = 1849203,
        RssiDbm = -62,
        CpuLoad = 0.42875,
        Tags = new List<string> { "tier1", "ebgp", "eu-central", "ixp-de-cix" },
        Prefixes = new List<uint> { 167772160, 172032000, 3232235520, 2886729728, 134744072, 134743040 },
        Location = new MpLocation
        {
            Region = "eu-central-1",
            Datacenter = "FRA-Equinix-FR5",
            City = "Frankfurt",
            Rack = 42,
            Latitude = 50.1109f,
            Longitude = 8.6821f,
        },
        Neighbors = new List<MpNeighbor>
        {
            new() { PeerAsn = 3356, Router = "ae0.fra1.level3.net", LatencyUs = 850, Established = true },
            new() { PeerAsn = 1299, Router = "ffm-b5-link.telia.net", LatencyUs = 920, Established = true },
            new() { PeerAsn = 174, Router = "be2040.ccr41.fra03.atlas.cogentco.com", LatencyUs = 1120, Established = true },
            new() { PeerAsn = 2914, Router = "ae-12.r24.frnkge08.de.bb.gin.ntt.net", LatencyUs = 980, Established = false },
        },
        Sequence = 9876543210UL,
    };

    private static JsonPeerUpdate CreateSampleJsonPeerUpdate() => new()
    {
        Asn = 64512,
        RouterId = "edge-rtr-fra01.core.backbone.net",
        Active = true,
        UptimeSec = 1849203,
        RssiDbm = -62,
        CpuLoad = 0.42875,
        Tags = new List<string> { "tier1", "ebgp", "eu-central", "ixp-de-cix" },
        Prefixes = new List<uint> { 167772160, 172032000, 3232235520, 2886729728, 134744072, 134743040 },
        Location = new JsonLocation
        {
            Region = "eu-central-1",
            Datacenter = "FRA-Equinix-FR5",
            City = "Frankfurt",
            Rack = 42,
            Latitude = 50.1109f,
            Longitude = 8.6821f,
        },
        Neighbors = new List<JsonNeighbor>
        {
            new() { PeerAsn = 3356, Router = "ae0.fra1.level3.net", LatencyUs = 850, Established = true },
            new() { PeerAsn = 1299, Router = "ffm-b5-link.telia.net", LatencyUs = 920, Established = true },
            new() { PeerAsn = 174, Router = "be2040.ccr41.fra03.atlas.cogentco.com", LatencyUs = 1120, Established = true },
            new() { PeerAsn = 2914, Router = "ae-12.r24.frnkge08.de.bb.gin.ntt.net", LatencyUs = 980, Established = false },
        },
        Sequence = 9876543210UL,
    };
}

[MessagePackObject]
public sealed class MpLocation
{
    [Key("region")]
    public string Region { get; set; } = string.Empty;

    [Key("datacenter")]
    public string Datacenter { get; set; } = string.Empty;

    [Key("city")]
    public string City { get; set; } = string.Empty;

    [Key("rack")]
    public uint Rack { get; set; }

    [Key("latitude")]
    public float Latitude { get; set; }

    [Key("longitude")]
    public float Longitude { get; set; }
}

[MessagePackObject]
public sealed class MpNeighbor
{
    [Key("peer_asn")]
    public uint PeerAsn { get; set; }

    [Key("router")]
    public string Router { get; set; } = string.Empty;

    [Key("latency_us")]
    public uint LatencyUs { get; set; }

    [Key("established")]
    public bool Established { get; set; }
}

[MessagePackObject]
public sealed class MpPeerUpdate
{
    [Key("asn")]
    public uint Asn { get; set; }

    [Key("router_id")]
    public string RouterId { get; set; } = string.Empty;

    [Key("active")]
    public bool Active { get; set; }

    [Key("uptime_sec")]
    public ulong UptimeSec { get; set; }

    [Key("rssi_dbm")]
    public int RssiDbm { get; set; }

    [Key("cpu_load")]
    public double CpuLoad { get; set; }

    [Key("tags")]
    public List<string> Tags { get; set; } = new();

    [Key("prefixes")]
    public List<uint> Prefixes { get; set; } = new();

    [Key("location")]
    public MpLocation? Location { get; set; }

    [Key("neighbors")]
    public List<MpNeighbor> Neighbors { get; set; } = new();

    [Key("sequence")]
    public ulong Sequence { get; set; }
}

public sealed class JsonLocation
{
    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("datacenter")]
    public string Datacenter { get; set; } = string.Empty;

    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    [JsonPropertyName("rack")]
    public uint Rack { get; set; }

    [JsonPropertyName("latitude")]
    public float Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public float Longitude { get; set; }
}

public sealed class JsonNeighbor
{
    [JsonPropertyName("peer_asn")]
    public uint PeerAsn { get; set; }

    [JsonPropertyName("router")]
    public string Router { get; set; } = string.Empty;

    [JsonPropertyName("latency_us")]
    public uint LatencyUs { get; set; }

    [JsonPropertyName("established")]
    public bool Established { get; set; }
}

public sealed class JsonPeerUpdate
{
    [JsonPropertyName("asn")]
    public uint Asn { get; set; }

    [JsonPropertyName("router_id")]
    public string RouterId { get; set; } = string.Empty;

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("uptime_sec")]
    public ulong UptimeSec { get; set; }

    [JsonPropertyName("rssi_dbm")]
    public int RssiDbm { get; set; }

    [JsonPropertyName("cpu_load")]
    public double CpuLoad { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("prefixes")]
    public List<uint> Prefixes { get; set; } = new();

    [JsonPropertyName("location")]
    public JsonLocation? Location { get; set; }

    [JsonPropertyName("neighbors")]
    public List<JsonNeighbor> Neighbors { get; set; } = new();

    [JsonPropertyName("sequence")]
    public ulong Sequence { get; set; }
}

public sealed class BenchRow
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("nsPerOp")]
    public required long NsPerOp { get; init; }

    [JsonPropertyName("opsPerSec")]
    public required long OpsPerSec { get; init; }

    [JsonPropertyName("bytesPerOp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BytesPerOp { get; init; }

    [JsonPropertyName("wireBytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WireBytes { get; init; }

    [JsonPropertyName("highlight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Highlight { get; init; }

    [JsonPropertyName("baseline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Baseline { get; init; }
}

public sealed class BenchCategory
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("unit")]
    public required string Unit { get; init; }

    [JsonPropertyName("results")]
    public required List<BenchRow> Results { get; init; }
}

public sealed class BenchReport
{
    [JsonPropertyName("language")]
    public required string Language { get; init; }

    [JsonPropertyName("runtime")]
    public required string Runtime { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("arch")]
    public required string Arch { get; init; }

    [JsonPropertyName("cpu")]
    public required string Cpu { get; init; }

    [JsonPropertyName("workload")]
    public required string Workload { get; init; }

    [JsonPropertyName("categories")]
    public required List<BenchCategory> Categories { get; init; }
}

[JsonSerializable(typeof(JsonPeerUpdate))]
internal sealed partial class WorkloadJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BenchReport))]
internal sealed partial class ReportJsonContext : JsonSerializerContext
{
}
