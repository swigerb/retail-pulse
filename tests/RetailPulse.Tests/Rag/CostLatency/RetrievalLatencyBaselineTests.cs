using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Rag.FoundryIQ;
using RetailPulse.Contracts.Rag;
using RetailPulse.Tests.Rag.Baselines;
using RetailPulse.Tests.Rag.FoundryIQ;
using Xunit.Abstractions;

namespace RetailPulse.Tests.Rag.CostLatency;

/// <summary>
/// Issue #107 cost/latency baseline. Records retrieval latency per provider
/// against a fixed query set so operators can spot regressions during
/// promotion. This is an INFORMATIONAL baseline - it never fails on
/// performance; the honest number is what matters.
///
/// Local providers (InMemory + Foundry IQ via <see cref="FakeFoundryIQClient"/>)
/// are measured every run. Cloud latencies are captured by the corresponding
/// live-conformance suites when configured; those are the numbers to trust
/// for a Wave-5 promotion decision.
/// </summary>
public sealed class RetrievalLatencyBaselineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task InMemoryProvider_RetrievalLatencyBaseline_IsRecorded()
    {
        InMemoryKnowledgeBase kb = new(
            NullLoggerFactory.Instance.CreateLogger<InMemoryKnowledgeBase>(),
            Options.Create(new KnowledgeOptions()));
        foreach ((string title, string source, string content) in PreWave5BaselineFixture.Corpus)
        {
            await kb.IngestDocumentAsync(title, content, source);
        }

        List<double> latencies = [];
        foreach (string query in PreWave5BaselineFixture.Queries)
        {
            // Warmup - JIT + first-touch cache
            _ = await kb.SearchAsync(query, topK: 5);
            long start = Stopwatch.GetTimestamp();
            _ = await kb.SearchAsync(query, topK: 5);
            latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        double p50 = Percentile(latencies, 0.50);
        double p95 = Percentile(latencies, 0.95);
        double max = latencies.Max();

        output.WriteLine("InMemory retrieval latency baseline (BM25, 5 docs, 6 queries, no warmup)");
        output.WriteLine($"  p50 : {p50:F3} ms");
        output.WriteLine($"  p95 : {p95:F3} ms");
        output.WriteLine($"  max : {max:F3} ms");

        // Non-assertive - the baseline is informational so operators can spot
        // a 100x regression against the number in the PR review log.
    }

    [Fact]
    public async Task FoundryIQProvider_RetrievalLatencyBaseline_IsRecorded()
    {
        var fake = new FakeFoundryIQClient();
        fake.Stores["vs_latency"] = new FoundryIQVectorStoreInfo("vs_latency", "latency", "Completed");
        fake.AgentsByName["retail-pulse-foundry-iq-retrieval"] =
            new FoundryIQAgentInfo("asst_latency", "retail-pulse-foundry-iq-retrieval");
        fake.NextSearchHits.Add(new FoundryIQSearchHit(
            FileId: "doc",
            FileName: "Latency",
            Score: 0.9,
            Chunk: "Retail category management and merchandising execution baseline chunk."));

        var options = new FoundryIQOptions
        {
            ProjectEndpoint = "https://foundry.example/api/projects/p",
            VectorStoreId = "vs_latency",
            RetrievalAgentName = "retail-pulse-foundry-iq-retrieval",
            Model = "gpt-5.4-mini",
            RequestTimeoutMs = 5_000,
            PollIntervalMs = 50,
            MaxResults = 5,
        };
        var resolver = new FoundryIQVectorStoreResolver(fake, options, NullLogger<FoundryIQVectorStoreResolver>.Instance);
        var agentProvider = new FoundryIQRetrievalAgentProvider(fake, resolver, options, NullLogger<FoundryIQRetrievalAgentProvider>.Instance);
        var kb = new FoundryIQKnowledgeBase(
            fake, resolver, agentProvider, options,
            new KnowledgeOptions(),
            new RecordingCostTracker(),
            NullLogger<FoundryIQKnowledgeBase>.Instance);

        List<double> latencies = [];
        foreach (string query in PreWave5BaselineFixture.Queries)
        {
            _ = await kb.SearchAsync(query, topK: 5);
            long start = Stopwatch.GetTimestamp();
            _ = await kb.SearchAsync(query, topK: 5);
            latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        double p50 = Percentile(latencies, 0.50);
        double p95 = Percentile(latencies, 0.95);
        output.WriteLine("FoundryIQ (fake client) retrieval latency baseline (6 queries)");
        output.WriteLine($"  p50 : {p50:F3} ms");
        output.WriteLine($"  p95 : {p95:F3} ms");
        output.WriteLine("Note: fake-client latency dominates by test infra, not Foundry service. See FoundryIQLiveConformanceTests for the cloud baseline.");
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        List<double> ordered = [.. values.OrderBy(v => v)];
        double rank = p * (ordered.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        return lo == hi ? ordered[lo] : ordered[lo] + ((ordered[hi] - ordered[lo]) * (rank - lo));
    }
}
