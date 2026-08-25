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
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Persistence;
using RetailPulse.Api.Security.Anonymous;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Tests.Endpoints;

/// <summary>
/// HTTP contract for issue #92's user-initiated execution control surface:
/// cancel + reconcile. Covers:
/// <list type="bullet">
///   <item>Cancel actually triggers cancellation on the registered CTS and
///     an in-flight fake tool ceases its work (not merely HTTP 204).</item>
///   <item>Cross-subject cancels collapse to 404 so live sessions/plans
///     cannot be probed.</item>
///   <item>Reconcile filters cross-subject reads to 404 and honours
///     <c>afterStepIndex</c>.</item>
/// </list>
/// </summary>
public sealed class ExecutionControlEndpointsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task CancelChat_Owner_TriggersCancellation_OnInFlightTool()
    {
        await using ControlHost host = await ControlHost.CreateAsync(new StubAuthConfig
        {
            Subject = "alice-oid",
        });

        using var cts = new CancellationTokenSource();
        using IDisposable handle = host.Registry.Register(
            ExecutionCancellationRegistry.ChatScope, "session-1", "alice-oid", cts);

        int toolIterations = 0;
        bool toolCancelled = false;
        var firstIteration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var toolTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    int n = Interlocked.Increment(ref toolIterations);
                    if (n == 1) firstIteration.TrySetResult();
                    await Task.Delay(10, cts.Token);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                toolCancelled = true;
            }
        });

        // Wait deterministically for the tool to actually enter its loop rather
        // than sleeping a fixed budget. Under CPU contention from the full suite
        // a fixed Task.Delay races the thread-pool scheduler and observes zero
        // iterations, causing the same spurious "beforeCount=0" failure #152
        // eliminated for the Hubs registry test.
        await firstIteration.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int beforeCount = Volatile.Read(ref toolIterations);
        beforeCount.Should().BeGreaterThan(0);

        HttpResponseMessage response = await host.Client.PostAsync("/api/chat/session-1/cancel", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await toolTask.WaitAsync(TimeSpan.FromSeconds(5));
        toolCancelled.Should().BeTrue("cancellation must reach the fake tool, not just return HTTP");

        int settledCount = Volatile.Read(ref toolIterations);
        Volatile.Read(ref toolIterations).Should().Be(settledCount,
            "the tool loop must stop iterating after cancel arrives");
    }

    [Fact]
    public async Task CancelChat_ForeignSubject_ReturnsNotFound_AndDoesNotCancel()
    {
        await using ControlHost host = await ControlHost.CreateAsync(new StubAuthConfig
        {
            Subject = "attacker-oid",
        });

        using var cts = new CancellationTokenSource();
        using IDisposable handle = host.Registry.Register(
            ExecutionCancellationRegistry.ChatScope, "victim-session", "victim-oid", cts);

        HttpResponseMessage response = await host.Client.PostAsync("/api/chat/victim-session/cancel", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        cts.IsCancellationRequested.Should().BeFalse(
            "a foreign subject must never cancel another user's in-flight run");
    }

    [Fact]
    public async Task CancelPlan_AnonymousCaller_IsRefused()
    {
        await using ControlHost host = await ControlHost.CreateAsync(new StubAuthConfig
        {
            AsAnonymousProvider = true,
            Subject = "anon-sub-1",
        });

        HttpResponseMessage response = await host.Client.PostAsync("/api/plans/some-plan/cancel", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("code").GetString().Should().Be("plan_cancel_unavailable");
    }

    [Fact]
    public async Task ReconcilePlan_Owner_ReturnsStepsAfterCursor()
    {
        await using ControlHost host = await ControlHost.CreateAsync(new StubAuthConfig
        {
            Subject = "alice-oid",
        });

        host.PlanStore.Seed("alice-oid", new PlanDetailDto(
            PlanId: "plan-1",
            SessionId: "s-1",
            TenantId: "Contoso",
            Request: "multi-step",
            Status: PlanStatus.Running,
            DetectedIntents: ["a", "b"],
            FailureReason: null,
            TotalInputTokens: null,
            TotalOutputTokens: null,
            TotalTokens: null,
            TotalDurationMs: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Steps:
            [
                new PlanStepRecordDto("s0", "plan-1", 0, "kA", "iA", "act0", PlanStepStatus.Completed, "r0", null, 1, 2, 3, 5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new PlanStepRecordDto("s1", "plan-1", 1, "kB", "iB", "act1", PlanStepStatus.Completed, "r1", null, 1, 2, 3, 5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new PlanStepRecordDto("s2", "plan-1", 2, "kC", "iC", "act2", PlanStepStatus.Running, null, null, 0, 0, 0, 0, DateTimeOffset.UtcNow, null),
            ]));

        HttpResponseMessage response = await host.Client.GetAsync("/api/plans/plan-1/reconcile?afterStepIndex=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("planId").GetString().Should().Be("plan-1");
        doc.RootElement.GetProperty("status").GetString().Should().Be(PlanStatus.Running);
        doc.RootElement.GetProperty("afterStepIndex").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("totalStepCount").GetInt32().Should().Be(3);

        JsonElement steps = doc.RootElement.GetProperty("steps");
        steps.GetArrayLength().Should().Be(1, "only steps with index > 1 should be returned");
        steps[0].GetProperty("stepIndex").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ReconcilePlan_CrossSubject_Returns404()
    {
        await using ControlHost host = await ControlHost.CreateAsync(new StubAuthConfig
        {
            Subject = "attacker-oid",
        });

        host.PlanStore.Seed("victim-oid", new PlanDetailDto(
            PlanId: "plan-victim",
            SessionId: "s-v",
            TenantId: "Contoso",
            Request: "victim",
            Status: PlanStatus.Completed,
            DetectedIntents: [],
            FailureReason: null,
            TotalInputTokens: null,
            TotalOutputTokens: null,
            TotalTokens: null,
            TotalDurationMs: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Steps: []));

        HttpResponseMessage response = await host.Client.GetAsync("/api/plans/plan-victim/reconcile");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReconcilePlan_AnonymousCaller_IsRefused()
    {
        await using ControlHost host = await ControlHost.CreateAsync(new StubAuthConfig
        {
            AsAnonymousProvider = true,
            Subject = "anon-sub-2",
        });

        HttpResponseMessage response = await host.Client.GetAsync("/api/plans/anything/reconcile");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("code").GetString().Should().Be("plan_reconcile_unavailable");
    }

    private sealed class ControlHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required IExecutionCancellationRegistry Registry { get; init; }
        public required InMemoryPlanStoreForReconcile PlanStore { get; init; }

        public static async Task<ControlHost> CreateAsync(StubAuthConfig auth)
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

            var registry = new ExecutionCancellationRegistry();
            var planStore = new InMemoryPlanStoreForReconcile();
            builder.Services.AddSingleton<IExecutionCancellationRegistry>(registry);
            builder.Services.AddSingleton<IPlanStore>(planStore);

            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapExecutionControlEndpoints();
            app.MapPlanReconciliationEndpoint();
            await app.StartAsync();

            return new ControlHost
            {
                App = app,
                Client = app.GetTestClient(),
                Registry = registry,
                PlanStore = planStore,
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

    private sealed class InMemoryPlanStoreForReconcile : IPlanStore
    {
        private readonly Dictionary<string, Dictionary<string, PlanDetailDto>> _byOwner
            = new(StringComparer.Ordinal);

        public void Seed(string owner, PlanDetailDto detail)
        {
            if (!_byOwner.TryGetValue(owner, out Dictionary<string, PlanDetailDto>? bucket))
            {
                bucket = new(StringComparer.Ordinal);
                _byOwner[owner] = bucket;
            }
            bucket[detail.PlanId] = detail;
        }

        public Task CreatePlanAsync(PlanWrite plan, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePlanStatusAsync(PlanStatusUpdate update, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateStepAsync(PlanStepUpdate update, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PlanSummaryDto>> ListPlansForSubjectAsync(string subject, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlanSummaryDto>>([]);

        public Task<PlanDetailDto?> GetPlanAsync(string subject, string planId, CancellationToken ct = default)
            => _byOwner.TryGetValue(subject, out Dictionary<string, PlanDetailDto>? bucket)
                && bucket.TryGetValue(planId, out PlanDetailDto? detail)
                    ? Task.FromResult<PlanDetailDto?>(detail)
                    : Task.FromResult<PlanDetailDto?>(null);

        public Task<bool> DeletePlanAsync(string subject, string planId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<PlanCleanupResult> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default)
            => Task.FromResult(new PlanCleanupResult(0, 0));
    }
}
