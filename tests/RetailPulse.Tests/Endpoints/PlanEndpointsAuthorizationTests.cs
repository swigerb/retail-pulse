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
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Persistence;
using RetailPulse.Api.Security.Anonymous;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Tests.Endpoints;

/// <summary>
/// Contract-level HTTP tests for the <c>/api/plans</c> surface introduced by
/// issue #93. Guards two invariants the endpoint layer owns and no unit test
/// covers:
/// <list type="number">
///   <item>Anonymous callers are refused with a 403 whose body carries the
///     stable <c>plan_persistence_unavailable</c> code the UI branches on.</item>
///   <item>Cross-subject reads collapse into a 404 (never a 200) and cross-subject
///     lists are empty — so a probe cannot enumerate another user's plan ids.</item>
/// </list>
/// The host mirrors <see cref="ObservabilityExportEndpointTests"/>: real
/// endpoint mapping, stub auth handler, no-op rate limiter policies, and an
/// in-memory <see cref="IPlanStore"/> fake so behaviour is deterministic.
/// </summary>
public sealed class PlanEndpointsAuthorizationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task Anonymous_principal_is_refused_with_plan_persistence_unavailable_code()
    {
        await using PlanEndpointsHost host = await PlanEndpointsHost.CreateAsync(new StubAuthConfig
        {
            AsAnonymousProvider = true,
            Subject = "anon-session-xyz",
        });

        HttpResponseMessage listResponse = await host.Client.GetAsync("/api/plans");
        HttpResponseMessage getResponse = await host.Client.GetAsync("/api/plans/plan-alice-1");
        HttpResponseMessage deleteResponse = await host.Client.DeleteAsync("/api/plans/plan-alice-1");

        foreach (HttpResponseMessage response in new[] { listResponse, getResponse, deleteResponse })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("code").GetString()
                .Should().Be("plan_persistence_unavailable",
                    "the UI branches on this exact code to hide the plans surface");
        }
    }

    [Fact]
    public async Task Cross_subject_get_is_404_and_list_is_empty_so_ids_cannot_be_probed()
    {
        // Seed a plan owned by alice, then hit every read as bob. The store's
        // subject filter is the source of truth for ownership; the endpoints
        // must not leak the plan id or its detail across subjects.
        await using PlanEndpointsHost host = await PlanEndpointsHost.CreateAsync(new StubAuthConfig
        {
            AsAnonymousProvider = false,
            Subject = "bob-oid",
        });

        host.Store.Seed(new PlanSummaryDto(
            PlanId: "plan-alice-1",
            SessionId: "s-a",
            TenantId: "Contoso",
            Request: "alice's plan",
            Status: PlanStatus.Completed,
            StepCount: 2,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow),
            ownerSubject: "alice-oid");

        HttpResponseMessage listResponse = await host.Client.GetAsync("/api/plans");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        List<PlanSummaryDto>? list = await listResponse.Content.ReadFromJsonAsync<List<PlanSummaryDto>>(JsonOptions);
        list.Should().NotBeNull().And.BeEmpty(
            "bob must not see any of alice's plans in the list response");

        HttpResponseMessage getResponse = await host.Client.GetAsync("/api/plans/plan-alice-1");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "cross-subject read must be indistinguishable from an unknown id");

        HttpResponseMessage deleteResponse = await host.Client.DeleteAsync("/api/plans/plan-alice-1");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "cross-subject delete must collapse into a 404 for the same reason");

        // Alice's plan is still there — bob's calls did not silently delete it.
        host.Store.CountFor("alice-oid").Should().Be(1);
    }

    [Fact]
    public async Task Owner_can_list_and_fetch_their_own_plan()
    {
        await using PlanEndpointsHost host = await PlanEndpointsHost.CreateAsync(new StubAuthConfig
        {
            AsAnonymousProvider = false,
            Subject = "alice-oid",
        });

        host.Store.Seed(new PlanSummaryDto(
            PlanId: "plan-alice-1",
            SessionId: "s-a",
            TenantId: "Contoso",
            Request: "alice's plan",
            Status: PlanStatus.Completed,
            StepCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow),
            ownerSubject: "alice-oid");

        HttpResponseMessage listResponse = await host.Client.GetAsync("/api/plans");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        List<PlanSummaryDto>? list = await listResponse.Content.ReadFromJsonAsync<List<PlanSummaryDto>>(JsonOptions);
        list.Should().ContainSingle(p => p.PlanId == "plan-alice-1");

        HttpResponseMessage getResponse = await host.Client.GetAsync("/api/plans/plan-alice-1");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        PlanDetailDto? detail = await getResponse.Content.ReadFromJsonAsync<PlanDetailDto>(JsonOptions);
        detail!.PlanId.Should().Be("plan-alice-1");
    }

    private sealed class PlanEndpointsHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required InMemoryPlanStore Store { get; init; }

        public static async Task<PlanEndpointsHost> CreateAsync(StubAuthConfig auth)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            builder.Services.AddSingleton(auth);
            builder.Services
                .AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, StubAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddRateLimiter(options =>
            {
                options.AddPolicy("relaxed", _ => RateLimitPartition.GetNoLimiter("all"));
                options.AddPolicy("moderate", _ => RateLimitPartition.GetNoLimiter("all"));
            });

            var store = new InMemoryPlanStore();
            builder.Services.AddSingleton<IPlanStore>(store);

            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapPlanEndpoints();
            await app.StartAsync();

            return new PlanEndpointsHost
            {
                App = app,
                Client = app.GetTestClient(),
                Store = store,
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
            List<Claim> claims;
            if (cfg.AsAnonymousProvider)
            {
                // Match AnonymousCapabilityPolicy.IsAnonymousPrincipal: an authenticated
                // principal with provider=Anonymous. UserIdentity.Resolve picks the sub.
                claims =
                [
                    new Claim(AnonymousCapabilityPolicy.ProviderClaimType, AnonymousCapabilityPolicy.ProviderName),
                    new Claim("sub", cfg.Subject),
                ];
            }
            else
            {
                claims = [new Claim("oid", cfg.Subject)];
            }

            var identity = new ClaimsIdentity(claims, "Test");
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed record PlanRow(PlanSummaryDto Summary, PlanDetailDto Detail);

    private sealed class InMemoryPlanStore : IPlanStore
    {
        // subject -> planId -> row
        private readonly Dictionary<string, Dictionary<string, PlanRow>> _byOwner
            = new(StringComparer.Ordinal);

        public void Seed(PlanSummaryDto summary, string ownerSubject)
        {
            if (!_byOwner.TryGetValue(ownerSubject, out Dictionary<string, PlanRow>? bucket))
            {
                bucket = new Dictionary<string, PlanRow>(StringComparer.Ordinal);
                _byOwner[ownerSubject] = bucket;
            }

            var detail = new PlanDetailDto(
                PlanId: summary.PlanId,
                SessionId: summary.SessionId,
                TenantId: summary.TenantId,
                Request: summary.Request,
                Status: summary.Status,
                DetectedIntents: [],
                FailureReason: null,
                TotalInputTokens: null,
                TotalOutputTokens: null,
                TotalTokens: null,
                TotalDurationMs: null,
                CreatedAt: summary.CreatedAt,
                UpdatedAt: summary.UpdatedAt,
                Steps: []);
            bucket[summary.PlanId] = new PlanRow(summary, detail);
        }

        public int CountFor(string ownerSubject) =>
            _byOwner.TryGetValue(ownerSubject, out Dictionary<string, PlanRow>? bucket)
                ? bucket.Count
                : 0;

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

        public Task<bool> DeletePlanAsync(string subject, string planId, CancellationToken ct = default)
            => _byOwner.TryGetValue(subject, out Dictionary<string, PlanRow>? bucket)
                ? Task.FromResult(bucket.Remove(planId))
                : Task.FromResult(false);

        public Task<PlanCleanupResult> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default)
            => Task.FromResult(new PlanCleanupResult(0, 0));
    }
}
