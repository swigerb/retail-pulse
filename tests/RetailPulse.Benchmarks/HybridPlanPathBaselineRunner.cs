using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Benchmarks;

/// <summary>
/// Wave 2 QA sweep (#97) companion baseline for the plan-first decision.
/// Measures the pure decision-layer overhead of
/// <see cref="HybridExecutionDecider.Decide"/> selecting Plan for a
/// multi-domain <see cref="RoutingDecision"/>. This mirrors the fast-path
/// baseline runner in <see cref="HybridFastPathBaselineRunner"/> so
/// before/after comparisons across Wave 2 have paired artifacts for both
/// paths.
///
/// Live plan-execution latency (planner → executor → synthesis) is
/// intentionally NOT measured here: the executor's real cost is
/// LLM-bound and cannot be reproduced deterministically from a benchmark
/// harness. The load-test project's
/// <c>PlanPathChatEndpointScenario</c> covers that live number when a
/// running API is available.
///
/// Invoke with:
///   dotnet run -c Release --project tests/RetailPulse.Benchmarks -- baseline-plan
///   dotnet run -c Release --project tests/RetailPulse.Benchmarks -- baseline-plan --out &lt;path&gt;
/// </summary>
internal static class HybridPlanPathBaselineRunner
{
    /// <summary>
    /// Reference multi-domain prompt — hits <c>MinDetectedIntentsForPlan</c>
    /// so <see cref="HybridExecutionDecider.Decide"/> returns Plan. Do not
    /// change without re-baselining.
    /// </summary>
    internal const string ReferencePrompt =
        "Compare Q4 demand for Apex Grill in the Southwest with current inventory health and outstanding shipments.";

    private const int WarmupIterations = 5_000;
    private const int SampleCount = 50_000;

    public static int Run(string[] args)
    {
        string outputPath = ResolveOutputPath(args);

        // Multi-domain decision: two distinct intents forces the plan branch
        // (>= MinDetectedIntentsForPlan = 2) regardless of Confidence.
        var decision = new RoutingDecision(
            AgentKey: "demand-forecasting",
            Intent: AgentIntent.DemandForecasting,
            Confidence: 0.85,
            DetectedIntents: [AgentIntent.DemandForecasting, AgentIntent.SupplyShipments]);
        var context = new HybridExecutionContext(
            AnonymousCaller: false,
            PlannerAvailable: true,
            ForcedPath: null);
        var options = new PlanPersistenceOptions();

        HybridExecutionResult sanity = HybridExecutionDecider.Decide(decision, ReferencePrompt, context, options);
        if (!string.Equals(sanity.Path, ExecutionPath.Plan, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[baseline-plan] Reference decision no longer resolves to Plan (got '{sanity.Path}') — refusing to record a misleading baseline.");
            return 2;
        }

        for (int i = 0; i < WarmupIterations; i++)
        {
            _ = HybridExecutionDecider.Decide(decision, ReferencePrompt, context, options);
        }

        long[] samples = new long[SampleCount];
        var sw = new Stopwatch();
        for (int i = 0; i < SampleCount; i++)
        {
            sw.Restart();
            _ = HybridExecutionDecider.Decide(decision, ReferencePrompt, context, options);
            sw.Stop();
            samples[i] = sw.ElapsedTicks;
        }

        Array.Sort(samples);
        double ticksToNanos = 1_000_000_000.0 / Stopwatch.Frequency;
        double p50Ns = samples[(int)(SampleCount * 0.50)] * ticksToNanos;
        double p95Ns = samples[(int)(SampleCount * 0.95)] * ticksToNanos;

        var artifact = new BaselineArtifact
        {
            Issue = "#97",
            Scenario = "hybrid-execution-plan-path",
            Prompt = ReferencePrompt,
            MeasuredCodePath = "RetailPulse.Api.Agents.Routing.HybridExecutionDecider.Decide",
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
                Notes = "Decision-layer overhead only. Live plan-execution latency is covered by the load-test scenario.",
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

        Console.WriteLine("[baseline-plan] Wrote " + outputPath);
        Console.WriteLine($"[baseline-plan] samples={SampleCount}  p50={artifact.Results.P50} ns  p95={artifact.Results.P95} ns  commit={artifact.CommitSha}");
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
        return Path.Combine(projectDir, "baselines", "hybrid-plan-path-baseline.json");
    }

    private static string ResolveProjectDirectory()
    {
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
            Console.Error.WriteLine("[baseline-plan] Could not resolve git HEAD: " + ex.Message);
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
        [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    }

    private sealed class ResultBlock
    {
        [JsonPropertyName("units")] public string Units { get; set; } = "";
        [JsonPropertyName("p50")] public double P50 { get; set; }
        [JsonPropertyName("p95")] public double P95 { get; set; }
    }
}
