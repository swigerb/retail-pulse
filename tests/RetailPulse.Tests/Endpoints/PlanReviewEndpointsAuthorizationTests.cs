using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Persistence;
using RetailPulse.Api.Security.Anonymous;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Tests.Endpoints;

/// <summary>
/// Adversarial HTTP tests for the plan-review + clarification endpoints
/// (#94 B4). Proves subject B cannot decide subject A's plan review nor
/// answer their clarification. The endpoints MUST:
/// <list type="bullet">
///   <item>Return 404 for cross-subject probes so the ids cannot be
///     enumerated by an attacker.</item>
///   <item>Leave the approval row untouched — never invoke RespondAsync on a
///     row the caller does not own.</item>
/// </list>
/// </summary>
public sealed class PlanReviewEndpointsAuthorizationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Decision endpoint ────────────────────────────────────────────────

    [Fact]
    public async Task Cross_subject_decision_returns_404_and_row_stays_pending()
    {
        await using PlanReviewHost host = await PlanReviewHost.CreateAsync(
            subjectClaim: "bob-oid",
            aliceRow: new SeedRow
            {
                Subject = "alice-oid",
                PlanId = "plan-alice-1",
                Kind = ApprovalKind.PlanReview,
            });

        HttpResponseMessage resp = await host.Client.PostAsJsonAsync(
            $"/api/plans/plan-alice-1/reviews/{host.SeededRequestId}/decision",
            new { kind = "approve" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "cross-subject decide must collapse into a 404 — no probe surface.");

        // The row is still Pending — Bob's attempt did not resolve it.
        ApprovalResult after = await host.Gate.GetResultAsync(host.SeededRequestId);
        after.Decision.Should().Be(ApprovalDecision.Pending);
    }

    [Fact]
    public async Task Wrong_plan_id_returns_404_even_for_owner()
    {
        // Alice owns plan-alice-1 but tries to decide with a different plan
        // id in the URL. The endpoint must not accept mismatched plan/request
        // pairs.
        await using PlanReviewHost host = await PlanReviewHost.CreateAsync(
            subjectClaim: "alice-oid",
            aliceRow: new SeedRow
            {
                Subject = "alice-oid",
                PlanId = "plan-alice-1",
                Kind = ApprovalKind.PlanReview,
            });

        HttpResponseMessage resp = await host.Client.PostAsJsonAsync(
            $"/api/plans/plan-nonsense/reviews/{host.SeededRequestId}/decision",
            new { kind = "approve" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.Gate.GetResultAsync(host.SeededRequestId)).Decision.Should().Be(ApprovalDecision.Pending);
    }

    // ── Clarification endpoint ──────────────────────────────────────────

    [Fact]
    public async Task Cross_subject_clarification_answer_returns_404_and_row_stays_pending()
    {
        await using PlanReviewHost host = await PlanReviewHost.CreateAsync(
            subjectClaim: "bob-oid",
            aliceRow: new SeedRow
            {
                Subject = "alice-oid",
                PlanId = "plan-alice-1",
                Kind = ApprovalKind.Clarification,
            });

        HttpResponseMessage resp = await host.Client.PostAsJsonAsync(
            $"/api/plans/plan-alice-1/clarifications/{host.SeededRequestId}/answer",
            new { answer = "malicious answer" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.Gate.GetResultAsync(host.SeededRequestId)).Decision.Should().Be(ApprovalDecision.Pending);
    }

    [Fact]
    public async Task Anonymous_caller_is_refused_with_403_before_reaching_the_gate()
    {
        await using PlanReviewHost host = await PlanReviewHost.CreateAsync(
            subjectClaim: "anon-session",
            asAnonymous: true,
            aliceRow: new SeedRow
            {
                Subject = "alice-oid",
                PlanId = "plan-alice-1",
                Kind = ApprovalKind.PlanReview,
            });

        HttpResponseMessage resp = await host.Client.PostAsJsonAsync(
            $"/api/plans/plan-alice-1/reviews/{host.SeededRequestId}/decision",
            new { kind = "approve" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await host.Gate.GetResultAsync(host.SeededRequestId)).Decision.Should().Be(ApprovalDecision.Pending);
    }

    [Fact]
    public async Task Owner_decision_succeeds_and_flips_row_to_terminal()
    {
        await using PlanReviewHost host = await PlanReviewHost.CreateAsync(
            subjectClaim: "alice-oid",
            aliceRow: new SeedRow
            {
                Subject = "alice-oid",
                PlanId = "plan-alice-1",
                Kind = ApprovalKind.PlanReview,
            });

        HttpResponseMessage resp = await host.Client.PostAsJsonAsync(
            $"/api/plans/plan-alice-1/reviews/{host.SeededRequestId}/decision",
            new { kind = "approve" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        ApprovalResult after = await host.Gate.GetResultAsync(host.SeededRequestId);
        after.Decision.Should().Be(ApprovalDecision.Approved);
    }

    // ── Host + support types ─────────────────────────────────────────────

    private sealed class SeedRow
    {
        public required string Subject { get; init; }
        public required string PlanId { get; init; }
        public required string Kind { get; init; }
    }

    private sealed class PlanReviewHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required SqliteApprovalGate Gate { get; init; }
        public required string SeededRequestId { get; init; }

        public static async Task<PlanReviewHost> CreateAsync(
            string subjectClaim,
            SeedRow aliceRow,
            bool asAnonymous = false)
        {
            string dbPath = Path.Combine(Path.GetTempPath(), $"prv_ep_{Guid.NewGuid():N}.db");

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            var cfg = new StubAuthConfig
            {
                Subject = subjectClaim,
                AsAnonymousProvider = asAnonymous,
            };
            builder.Services.AddSingleton(cfg);
            builder.Services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, StubAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddRateLimiter(o =>
            {
                o.AddPolicy("relaxed", _ => RateLimitPartition.GetNoLimiter("all"));
                o.AddPolicy("moderate", _ => RateLimitPartition.GetNoLimiter("all"));
            });
            builder.Services.AddSignalR();

            SqliteApprovalGate gate = new(dbPath, NullLogger<SqliteApprovalGate>.Instance,
                TimeSpan.FromMinutes(30), TimeProvider.System);
            builder.Services.AddSingleton(gate);
            builder.Services.AddSingleton<IApprovalGate>(_ => gate);

            var plans = new InMemoryPlanStore();
            // Both Alice and Bob "own" a plan with the seeded id from their
            // own perspective so we can test cross-subject rejection at the
            // approval-row layer specifically (not the plan-ownership layer).
            plans.Seed(new PlanSummaryDto(
                PlanId: aliceRow.PlanId,
                SessionId: null,
                TenantId: "Contoso",
                Request: "alice's plan",
                Status: PlanStatus.AwaitingReview,
                StepCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
                ownerSubject: aliceRow.Subject);
            plans.Seed(new PlanSummaryDto(
                PlanId: aliceRow.PlanId,
                SessionId: null,
                TenantId: "Contoso",
                Request: "bob's view",
                Status: PlanStatus.AwaitingReview,
                StepCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
                ownerSubject: subjectClaim);
            builder.Services.AddSingleton<IPlanStore>(plans);

            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapPlanReviewEndpoints();
            await app.StartAsync();

            // Seed a Pending approval row owned by Alice.
            var ctx = new ApprovalContext(
                AgentId: "plan-review",
                UserId: aliceRow.Subject,
                Action: "review",
                Impact: "impact",
                Urgency: "medium",
                Reasoning: "why",
                SessionId: null, ConversationId: null,
                Kind: aliceRow.Kind,
                PlanId: aliceRow.PlanId,
                RoundNumber: 0,
                Payload: null);
            ApprovalRequest seededRow = await gate.RequestApprovalAsync(ctx);

            return new PlanReviewHost
            {
                App = app,
                Client = app.GetTestClient(),
                Gate = gate,
                SeededRequestId = seededRow.RequestId,
            };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private sealed class StubAuthConfig
    {
        public bool AsAnonymousProvider { get; init; }
        public string Subject { get; init; } = "tester-oid";
    }

    private sealed class StubAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        StubAuthConfig cfg) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            List<Claim> claims = cfg.AsAnonymousProvider
                ? [
                    new Claim(AnonymousCapabilityPolicy.ProviderClaimType, AnonymousCapabilityPolicy.ProviderName),
                    new Claim("sub", cfg.Subject),
                ]
                : [new Claim("oid", cfg.Subject)];

            var identity = new ClaimsIdentity(claims, "Test");
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed record PlanRow(PlanSummaryDto Summary, PlanDetailDto Detail);

    private sealed class InMemoryPlanStore : IPlanStore
    {
        private readonly Dictionary<string, Dictionary<string, PlanRow>> _byOwner
            = new(StringComparer.Ordinal);

        public void Seed(PlanSummaryDto summary, string ownerSubject)
        {
            if (!_byOwner.TryGetValue(ownerSubject, out Dictionary<string, PlanRow>? bucket))
            {
                bucket = new(StringComparer.Ordinal);
                _byOwner[ownerSubject] = bucket;
            }
            var detail = new PlanDetailDto(
                summary.PlanId, summary.SessionId, summary.TenantId, summary.Request,
                summary.Status, [], null, null, null, null, null,
                summary.CreatedAt, summary.UpdatedAt, []);
            bucket[summary.PlanId] = new PlanRow(summary, detail);
        }

        public Task CreatePlanAsync(PlanWrite plan, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePlanStatusAsync(PlanStatusUpdate update, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateStepAsync(PlanStepUpdate update, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PlanSummaryDto>> ListPlansForSubjectAsync(string subject, CancellationToken ct = default)
            => _byOwner.TryGetValue(subject, out Dictionary<string, PlanRow>? bucket)
                ? Task.FromResult<IReadOnlyList<PlanSummaryDto>>([.. bucket.Values.Select(v => v.Summary)])
                : Task.FromResult<IReadOnlyList<PlanSummaryDto>>([]);

        public Task<PlanDetailDto?> GetPlanAsync(string subject, string planId, CancellationToken ct = default)
            => _byOwner.TryGetValue(subject, out Dictionary<string, PlanRow>? bucket)
                && bucket.TryGetValue(planId, out PlanRow? row)
                ? Task.FromResult<PlanDetailDto?>(row.Detail)
                : Task.FromResult<PlanDetailDto?>(null);

        public Task<bool> DeletePlanAsync(string subject, string planId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<PlanCleanupResult> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default)
            => Task.FromResult(new PlanCleanupResult(0, 0));
    }
}
