using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Security.Anonymous;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Unit tests for the Anonymous-mode capability building blocks: fail-closed option validation,
/// the server-signed session token, the normalized principal, the read-only/write-tool policy,
/// and the billable-use circuit breaker. These are the safeguards a security reviewer must be
/// able to see enforced without standing up an HTTP host.
/// </summary>
public sealed class AnonymousCapabilityTests
{
    private const string HostedKey = "unit-test-signing-key-0123456789ABCDEF-32b";

    private static IHostEnvironment Env(string name) => new TestEnv { EnvironmentName = name };

    private static IConfiguration Config(params (string, string?)[] entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries.ToDictionary(e => e.Item1, e => e.Item2)).Build();

    // ── Options: fail-closed hosted validation ────────────────────────────────

    [Fact]
    public void Options_Development_AllowsEphemeralKeyAndLenientLimits()
    {
        var options = AnonymousAuthOptions.FromConfiguration(
            Config(("Authentication:Mode", "Anonymous")), Env("Development"));
        options.HostedGuardrailsEnforced.Should().BeFalse();
        options.HasConfiguredSigningKey.Should().BeFalse("Development generates an ephemeral key");
    }

    [Fact]
    public void Options_HostedWithoutAllowHosted_Throws()
    {
        Action act = () => AnonymousAuthOptions.FromConfiguration(
            Config(("Authentication:Mode", "Anonymous")), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*AllowHosted*");
    }

    [Fact]
    public void Options_HostedWithShortKey_Throws()
    {
        Action act = () => AnonymousAuthOptions.FromConfiguration(
            Config(
                ("Authentication:Mode", "Anonymous"),
                ("Anonymous:AllowHosted", "true"),
                ("Anonymous:SigningKey", "too-short"),
                ("Anonymous:Limits:DailyMaxRequests", "10"),
                ("Anonymous:Limits:DailyMaxTokens", "1000"),
                ("Anonymous:Limits:DailyMaxCostUsd", "1")),
            Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least 32 bytes*");
    }

    [Fact]
    public void Options_HostedWithZeroCostCeiling_Throws()
    {
        Action act = () => AnonymousAuthOptions.FromConfiguration(
            Config(
                ("Authentication:Mode", "Anonymous"),
                ("Anonymous:AllowHosted", "true"),
                ("Anonymous:SigningKey", HostedKey),
                ("Anonymous:Limits:DailyMaxRequests", "10"),
                ("Anonymous:Limits:DailyMaxTokens", "1000"),
                ("Anonymous:Limits:DailyMaxCostUsd", "0")),
            Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*DailyMaxCostUsd*");
    }

    [Fact]
    public void Options_HostedFullyConfigured_Succeeds()
    {
        AnonymousAuthOptions options = HostedOptions();

        options.HostedGuardrailsEnforced.Should().BeTrue();
        options.HasConfiguredSigningKey.Should().BeTrue();
        options.Role.Should().Be("RetailPulse.Anonymous");
        options.Scope.Should().Be("chat_limited");
    }

    // ── Session token: server-minted identity, no PII, short expiry ────────────

    [Fact]
    public void TokenService_MintsRandomSubject_WithConstrainedClaims()
    {
        AnonymousAuthOptions options = HostedOptions();
        var keyProvider = new AnonymousSigningKeyProvider(options);
        var svc = new AnonymousSessionTokenService(options, keyProvider);

        AnonymousSession a = svc.CreateSession();
        AnonymousSession b = svc.CreateSession();

        a.Subject.Should().StartWith("anon-");
        a.Subject.Should().NotBe(b.Subject, "each session gets a fresh random subject");
        a.ExpiresInSeconds.Should().Be(options.SessionTokenTtlSeconds);

        var handler = new JsonWebTokenHandler();
        JsonWebToken jwt = handler.ReadJsonWebToken(a.Token);
        jwt.GetClaim("provider").Value.Should().Be("Anonymous");
        jwt.GetClaim("roles").Value.Should().Be("RetailPulse.Anonymous");
        jwt.GetClaim("scp").Value.Should().Be("chat_limited");
        jwt.Subject.Should().Be(a.Subject);
        jwt.Claims.Should().NotContain(c => c.Type == "name" || c.Type == "email" || c.Type == "preferred_username",
            "anonymous tokens must carry no PII");
    }

    // ── Principal normalizer: subject from token, provider pinned ──────────────

    [Fact]
    public void Normalizer_ProjectsProviderSubjectRolesScopes()
    {
        ClaimsPrincipal principal = AnonymousPrincipal("anon-abc", role: "RetailPulse.Anonymous", scope: "chat_limited");
        var normalizer = new AnonymousPrincipalNormalizer();

        NormalizedPrincipal np = normalizer.Normalize(principal);

        np.Provider.Should().Be("Anonymous");
        np.Subject.Should().Be("anon-abc");
        np.Roles.Should().Contain("RetailPulse.Anonymous");
        np.Scopes.Should().Contain("chat_limited");
        np.DisplayName.Should().BeNull("anonymous principals expose no display name");
    }

    [Fact]
    public void Normalizer_RejectsNonAnonymousProvider()
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(JwtRegisteredClaimNames.Sub, "anon-x"));
        identity.AddClaim(new Claim("provider", "Entra"));
        var principal = new ClaimsPrincipal(identity);

        Action act = () => new AnonymousPrincipalNormalizer().Normalize(principal);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not 'Anonymous'*");
    }

    // ── Capability policy: deny-by-default allowlist ──────────────────────────

    [Theory]
    // Mutations / admin / observability / memory / cards / approvals / guardrail logs — all denied.
    [InlineData("POST", "/api/approvals/7/respond")]
    [InlineData("DELETE", "/api/memory/42")]
    [InlineData("POST", "/api/cards")]
    [InlineData("PUT", "/api/guardrails/config")]
    [InlineData("POST", "/api/admin/cache/invalidate")]
    // Alternate billable LLM paths + broad GET reads removed from the Anonymous surface — denied.
    [InlineData("POST", "/api/chat/stream")]
    [InlineData("POST", "/api/knowledge/search")]
    [InlineData("POST", "/api/council/convene")]
    [InlineData("POST", "/api/escalate")]
    [InlineData("GET", "/api/scorecard")]
    [InlineData("GET", "/api/margin/anything")]
    [InlineData("GET", "/api/sessions")]
    [InlineData("GET", "/api/audit")]
    [InlineData("GET", "/api/traces")]
    [InlineData("GET", "/api/dead-letter")]
    [InlineData("GET", "/api/memory")]
    [InlineData("GET", "/api/cards")]
    [InlineData("GET", "/api/approvals")]
    [InlineData("GET", "/api/info")]
    [InlineData("GET", "/api/chat")]
    public void CapabilityPolicy_DeniesEverythingOutsideAllowlist(string method, string path)
    {
        AnonymousCapabilityPolicy.IsBlocked(method, path).Should().BeTrue();
        AnonymousCapabilityPolicy.IsAllowed(method, path).Should().BeFalse();
    }

    [Theory]
    // The entire authenticated + bootstrap surface: chat POST, bootstrap POST, the two hubs, preflight.
    [InlineData("POST", "/api/chat")]
    [InlineData("POST", "/api/auth/anonymous/session")]
    [InlineData("OPTIONS", "/api/chat")]
    [InlineData("GET", "/hubs/telemetry")]
    [InlineData("POST", "/hubs/telemetry/negotiate")]
    [InlineData("GET", "/hubs/streaming")]
    [InlineData("POST", "/hubs/streaming/negotiate")]
    public void CapabilityPolicy_AllowsOnlyTheMinimalSurface(string method, string path)
    {
        AnonymousCapabilityPolicy.IsAllowed(method, path).Should().BeTrue();
        AnonymousCapabilityPolicy.IsBlocked(method, path).Should().BeFalse();
    }

    [Theory]
    [InlineData("POST", "/api/chat", true)]
    [InlineData("GET", "/hubs/telemetry", false)]
    [InlineData("POST", "/api/auth/anonymous/session", false)]
    [InlineData("OPTIONS", "/api/chat", false)]
    [InlineData("POST", "/api/chat/stream", false)]
    public void CapabilityPolicy_OnlyChatPostIsBudgeted(string method, string path, bool budgeted)
        => AnonymousCapabilityPolicy.IsBudgetedModelRoute(method, path).Should().Be(budgeted);

    // ── Tool filter: write-capable tools removed for anonymous ─────────────────

    [Fact]
    public void ToolFilter_DropsWriteTools_ForAnonymousPrincipal()
    {
        AITool approval = AIFunctionFactory.Create(() => "approved", "RequestApproval");
        AITool readOnly = AIFunctionFactory.Create(() => "chart", "GenerateChart");
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = AnonymousPrincipal("anon-1") }
        };
        var policy = new AnonymousChatPolicy(accessor, HostedOptions());

        AITool[] filtered = [.. policy.FilterTools([approval, readOnly])];

        filtered.Should().ContainSingle().Which.Should().BeSameAs(readOnly);
        policy.MaxOutputTokens.Should().Be(HostedOptions().MaxOutputTokens);
    }

    [Fact]
    public void ToolFilter_IsPassthrough_ForNonAnonymousPrincipal()
    {
        AITool approval = AIFunctionFactory.Create(() => "approved", "RequestApproval");
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() }; // unauthenticated
        var policy = new AnonymousChatPolicy(accessor, HostedOptions());

        AITool[] filtered = [.. policy.FilterTools([approval])];

        filtered.Should().ContainSingle().Which.Should().BeSameAs(approval);
        policy.MaxOutputTokens.Should().BeNull();
    }

    // ── Circuit breaker: request/token/cost ceilings, cache-truthful ───────────

    [Fact]
    public void Budget_TripsWhenRequestCeilingReached_FailClosed()
    {
        AnonymousUsageBudget budget = new(HostedOptions(dailyRequests: 2));

        budget.TryBeginRequest(out _).Should().BeTrue();
        budget.TryBeginRequest(out _).Should().BeTrue();
        budget.TryBeginRequest(out string? reason).Should().BeFalse("the 3rd request exceeds the daily ceiling");
        reason.Should().Contain("request ceiling");
    }

    [Fact]
    public void Budget_TripsWhenTokenCeilingReached()
    {
        AnonymousUsageBudget budget = new(HostedOptions(dailyTokens: 100));

        budget.TryBeginRequest(out _).Should().BeTrue();
        budget.RecordUsage(tokens: 150, costUsd: 0m);

        budget.TryBeginRequest(out string? reason).Should().BeFalse();
        reason.Should().Contain("token ceiling");
    }

    [Fact]
    public void Budget_CacheHitDoesNotAdvanceTokenOrCostCeiling()
    {
        AnonymousAuthOptions options = HostedOptions(dailyTokens: 100, dailyCost: 100m);
        AnonymousUsageBudget budget = new(options);
        var tracker = new AnonymousBudgetCostTracker(new NullCostTracker(), budget, Config());

        // A cache hit is recorded truthfully as zero tokens / cache-flagged: it must not advance
        // the token or cost ceiling. The request slot is charged elsewhere (guard middleware).
        tracker.TrackUsageAsync(new UsageEvent("cache", "cache", 0, 0, null, DateTime.UtcNow, CacheHit: true))
            .GetAwaiter().GetResult();

        AnonymousBudgetSnapshot snap = budget.Snapshot();
        snap.Tokens.Should().Be(0);
        snap.CostUsd.Should().Be(0m);
        snap.BreakerTripped.Should().BeFalse();
    }

    // ── Rate limiter: fixed-window per key ─────────────────────────────────────

    [Fact]
    public void RateLimiter_DeniesBeyondPermitInWindow()
    {
        var limiter = new AnonymousRateLimiter();

        limiter.TryAcquire("k", 2).Should().BeTrue();
        limiter.TryAcquire("k", 2).Should().BeTrue();
        limiter.TryAcquire("k", 2).Should().BeFalse();
        limiter.TryAcquire("other", 2).Should().BeTrue("a different key has its own window");
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static AnonymousAuthOptions HostedOptions(
        int dailyRequests = 500, long dailyTokens = 200_000, decimal dailyCost = 5.00m) =>
        AnonymousAuthOptions.FromConfiguration(
            Config(
                ("Authentication:Mode", "Anonymous"),
                ("Anonymous:AllowHosted", "true"),
                ("Anonymous:SigningKey", HostedKey),
                ("Anonymous:Limits:DailyMaxRequests", dailyRequests.ToString()),
                ("Anonymous:Limits:DailyMaxTokens", dailyTokens.ToString()),
                ("Anonymous:Limits:DailyMaxCostUsd", dailyCost.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            Env("Production"));

    private static ClaimsPrincipal AnonymousPrincipal(
        string subject, string role = "RetailPulse.Anonymous", string scope = "chat_limited")
    {
        var identity = new ClaimsIdentity("anonymous-session");
        identity.AddClaim(new Claim(JwtRegisteredClaimNames.Sub, subject));
        identity.AddClaim(new Claim("provider", "Anonymous"));
        identity.AddClaim(new Claim("roles", role));
        identity.AddClaim(new Claim("scp", scope));
        return new ClaimsPrincipal(identity);
    }

    private sealed class TestEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "RetailPulse.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class NullCostTracker : ICostTracker
    {
        public Task TrackUsageAsync(UsageEvent usage, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CostSummary> GetSummaryAsync(CostPeriod period, CancellationToken ct = default) =>
            Task.FromResult(new CostSummary(0, 0m, 0, period));
        public Task<IReadOnlyList<AgentCostBreakdown>> GetByAgentAsync(CostPeriod period, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AgentCostBreakdown>>([]);
        public Task<CostTrend> GetTrendAsync(int days = 7, CancellationToken ct = default) =>
            Task.FromResult(new CostTrend([]));
    }
}
