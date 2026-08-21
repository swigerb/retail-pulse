using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetailPulse.Api.Agents.Routing;

namespace RetailPulse.Benchmarks;

/// <summary>
/// Deterministic p50/p95 baseline harness for the single-specialist fast path
/// consumed by issue #95. Invoked via <c>dotnet run -c Release --project
/// tests/RetailPulse.Benchmarks -- baseline</c> (see <see cref="Program"/>).
///
/// The measured code path is <c>RetailOpsRouter.TryKeywordClassify</c> for the
/// reference prompt "How is Sierra Gold Tequila performing in the Northeast?".
/// The prompt hits the <c>BrandPerformingRegex</c> single-brand-lookup fast path
/// and returns the General intent without any network, LLM, or MCP call — so
/// repeated timings are stable and represent decision-layer overhead only.
///
/// Writes a compact JSON artifact under
/// <c>tests/RetailPulse.Benchmarks/baselines/hybrid-fast-path-baseline.json</c>
/// containing commit SHA, coarse environment identifier (framework, OS platform,
/// process architecture, processor count) sufficient for before/after matching,
/// sample count, p50, and p95 in nanoseconds. No secrets, usernames, or machine
/// names are captured.
/// </summary>
internal static class HybridFastPathBaselineRunner
{
    /// <summary>Reference prompt from issue #95. Do not change without re-baselining.</summary>
    internal const string ReferencePrompt =
        "How is Sierra Gold Tequila performing in the Northeast?";

    /// <summary>Warmup iterations before timed samples. Keeps JIT + regex-init cost out.</summary>
    private const int WarmupIterations = 2_000;

    /// <summary>Timed sample count. Small enough to run in &lt;1s, large enough for stable p95.</summary>
    private const int SampleCount = 20_000;

    private static readonly MethodInfo TryKeywordClassifyMethod =
        typeof(RetailOpsRouter).GetMethod("TryKeywordClassify", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// Creates a <see cref="RetailOpsRouter"/> without invoking its constructor so
    /// benchmarks can reach the private <c>TryKeywordClassify</c> instance method
    /// without wiring the full DI graph (IChatClient, specialists, metrics, etc.).
    /// The method only reads the message argument and static regexes — no instance
    /// state is touched — so an uninitialized instance is safe here.
    /// </summary>
    internal static RetailOpsRouter CreateRouterUninitialized()
        => (RetailOpsRouter)RuntimeHelpers.GetUninitializedObject(typeof(RetailOpsRouter));

    public static int Run(string[] args)
    {
        string outputPath = ResolveOutputPath(args);
        RetailOpsRouter router = CreateRouterUninitialized();

        // Sanity check: the reference prompt must hit the fast path. If someone
        // moves the router logic and this stops returning a classification, the
        // baseline is meaningless — fail loudly instead of writing zeros.
        object? sanity = TryKeywordClassifyMethod.Invoke(router, [ReferencePrompt]);
        if (sanity is null)
        {
            Console.Error.WriteLine(
                "[baseline] Reference prompt no longer hits the keyword fast path — refusing to record a misleading baseline.");
            return 2;
        }

        // Warmup
        for (int i = 0; i < WarmupIterations; i++)
        {
            _ = TryKeywordClassifyMethod.Invoke(router, [ReferencePrompt]);
        }

        long[] samples = new long[SampleCount];
        var sw = new Stopwatch();
        for (int i = 0; i < SampleCount; i++)
        {
            sw.Restart();
            _ = TryKeywordClassifyMethod.Invoke(router, [ReferencePrompt]);
            sw.Stop();
            samples[i] = sw.ElapsedTicks;
        }

        Array.Sort(samples);
        double ticksToNanos = 1_000_000_000.0 / Stopwatch.Frequency;
        double p50Ns = samples[(int)(SampleCount * 0.50)] * ticksToNanos;
        double p95Ns = samples[(int)(SampleCount * 0.95)] * ticksToNanos;

        var artifact = new BaselineArtifact
        {
            Issue = "#95",
            Scenario = "hybrid-execution-fast-path",
            Prompt = ReferencePrompt,
            MeasuredCodePath = "RetailPulse.Api.Agents.Routing.RetailOpsRouter.TryKeywordClassify",
            CommitSha = ResolveCommitSha(),
            Environment = new EnvironmentIdentifier
            {
                Framework = RuntimeInformation.FrameworkDescription,
                OsPlatform = ResolveOsPlatform(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                Configuration = ResolveBuildConfiguration(),
            },
            Method = new MethodDescriptor
            {
                WarmupIterations = WarmupIterations,
                SampleCount = SampleCount,
                TimerFrequencyHz = Stopwatch.Frequency,
                Deterministic = true,
            },
            Results = new ResultBlock
            {
                Units = "nanoseconds",
                P50 = Math.Round(p50Ns, 2),
                P95 = Math.Round(p95Ns, 2),
            },
            RecordedAtUtc = DateTime.UtcNow.ToString("O"),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string json = JsonSerializer.Serialize(artifact, s_jsonOptions);
        File.WriteAllText(outputPath, json + Environment.NewLine);

        Console.WriteLine("[baseline] Wrote " + outputPath);
        Console.WriteLine($"[baseline] samples={SampleCount}  p50={artifact.Results.P50} ns  p95={artifact.Results.P95} ns  commit={artifact.CommitSha}");
        return 0;
    }

    private static string ResolveOutputPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[i + 1]);
        }
        string projectDir = ResolveProjectDirectory();
        return Path.Combine(projectDir, "baselines", "hybrid-fast-path-baseline.json");
    }

    private static string ResolveProjectDirectory()
    {
        // AppContext.BaseDirectory in Release is
        // tests/RetailPulse.Benchmarks/bin/Release/net10.0/. Walk up to the .csproj.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.Benchmarks.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string ResolveCommitSha()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = ResolveProjectDirectory(),
            };
            using var p = Process.Start(psi);
            if (p is null) return "unknown";
            string sha = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2_000);
            return string.IsNullOrEmpty(sha) ? "unknown" : sha;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[baseline] Could not resolve git HEAD: " + ex.Message);
            return "unknown";
        }
    }

    private static string ResolveOsPlatform()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
            : "unknown";
    }

    private static string ResolveBuildConfiguration() =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private sealed class BaselineArtifact
    {
        [JsonPropertyName("issue")] public string Issue { get; set; } = "";
        [JsonPropertyName("scenario")] public string Scenario { get; set; } = "";
        [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
        [JsonPropertyName("measured_code_path")] public string MeasuredCodePath { get; set; } = "";
        [JsonPropertyName("commit_sha")] public string CommitSha { get; set; } = "";
        [JsonPropertyName("environment")] public EnvironmentIdentifier Environment { get; set; } = new();
        [JsonPropertyName("method")] public MethodDescriptor Method { get; set; } = new();
        [JsonPropertyName("results")] public ResultBlock Results { get; set; } = new();
        [JsonPropertyName("recorded_at_utc")] public string RecordedAtUtc { get; set; } = "";
    }

    private sealed class EnvironmentIdentifier
    {
        [JsonPropertyName("framework")] public string Framework { get; set; } = "";
        [JsonPropertyName("os_platform")] public string OsPlatform { get; set; } = "";
        [JsonPropertyName("process_architecture")] public string ProcessArchitecture { get; set; } = "";
        [JsonPropertyName("processor_count")] public int ProcessorCount { get; set; }
        [JsonPropertyName("configuration")] public string Configuration { get; set; } = "";
    }

    private sealed class MethodDescriptor
    {
        [JsonPropertyName("warmup_iterations")] public int WarmupIterations { get; set; }
        [JsonPropertyName("sample_count")] public int SampleCount { get; set; }
        [JsonPropertyName("timer_frequency_hz")] public long TimerFrequencyHz { get; set; }
        [JsonPropertyName("deterministic")] public bool Deterministic { get; set; }
    }

    private sealed class ResultBlock
    {
        [JsonPropertyName("units")] public string Units { get; set; } = "";
        [JsonPropertyName("p50")] public double P50 { get; set; }
        [JsonPropertyName("p95")] public double P95 { get; set; }
    }
}
