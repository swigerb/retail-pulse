using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using Xunit;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Composition-root regression coverage for issue #74 Publix failure #2:
/// the DI factory in <see cref="RoutingServiceExtensions.AddAgentRouting"/>
/// constructed the <see cref="AgentExecutionPipeline"/> WITHOUT resolving
/// <see cref="TenantConfiguration"/>, so <c>_tenant</c> was null at runtime,
/// the portfolio-ranking roster was null, and Kroger's coverage-enforcement
/// branch in <c>EnforceChartFulfillment</c> was silently SKIPPED in production.
/// The enforcement code was correct — the wiring was not.
///
/// A unit test on <c>EnforceChartFulfillment</c> alone CANNOT catch this class of
/// defect. The point is that the pipeline must be resolved through the SAME
/// registration path the app uses, and the wired tenant roster must be present.
/// </summary>
public sealed partial class TenantRosterPipelineCompositionTests
{
    private static readonly string[] TenantBrands =
    [
        "Ridgeline Bourbon", "Harvest Table", "FreshMart", "Sierra Gold Tequila",
        "Summit Vodka", "Aspen Rye", "Cascade Gin", "Northshore Rum",
        "Meadowbrook Wine", "Valleyfield Beer", "Ironwood Whiskey", "Blackpine Scotch",
    ];

    [Fact]
    public void PipelineFactory_ResolvedFromRealServices_HasTenantRosterWired()
    {
        ServiceProvider provider = BuildProvider();

        using IServiceScope scope = provider.CreateScope();
        IAgentExecutionPipeline pipeline = scope.ServiceProvider.GetRequiredService<IAgentExecutionPipeline>();

        pipeline.Should().BeOfType<AgentExecutionPipeline>();
        var concrete = (AgentExecutionPipeline)pipeline;

        concrete.HasTenantRoster.Should().BeTrue(
            "the production DI wiring MUST resolve TenantConfiguration and pass it to " +
            "AgentExecutionPipeline — otherwise portfolio-ranking coverage enforcement " +
            "(issue #74) is silently disabled and horizontalBar rankings will ship with " +
            "a partial roster");
    }

    [Fact]
    public void PipelineConstructor_RejectsNullTenant()
    {
        // Defense-in-depth: even if some future refactor removes the DI resolve,
        // the pipeline itself must refuse to construct without a tenant.
        Action act = () => _ = new AgentExecutionPipeline(
            new NoopChatClient(),
            hubContext: TestHubContext.Create(),
            streamingHubContext: null,
            streamingFeature: null,
            configuration: new ConfigurationBuilder().Build(),
            logger: new Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentExecutionPipeline>(),
            metrics: null,
            anonymousChatPolicy: Api.Auth.NoOpAnonymousChatPolicy.Instance,
            tenant: null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("tenant");
    }

    /// <summary>
    /// Source-grep guard: no PRODUCTION call site (anything under <c>src\</c>)
    /// may construct <see cref="AgentExecutionPipeline"/> without threading in a
    /// tenant argument. This blocks the class of silent-null bug that shipped as
    /// Publix failure #2 — even if a future PR reintroduces the omission, this
    /// test catches it before deploy.
    /// </summary>
    [Fact]
    public void NoProductionCallSite_OmitsTenantArgument()
    {
        DirectoryInfo srcRoot = LocateRepoRoot().CreateSubdirectory("src");
        srcRoot.Exists.Should().BeTrue();

        // Split "new AgentExecutionPipeline(" invocations across possibly-multi-line arg lists.
        // We check that every invocation site's argument list mentions either the
        // parameter name "tenant" (explicit named argument) or a resolved-from-SP
        // TenantConfiguration expression. The compat 5-arg ctor is production-safe
        // because it internally supplies a default TenantConfiguration.
        Regex invocation = MyRegex();
        List<string> offenders = [];

        foreach (FileInfo cs in srcRoot.EnumerateFiles("*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(cs.FullName);
            foreach (Match m in invocation.Matches(text))
            {
                string args = ExtractBalancedArgList(text, m.Index + m.Length - 1);
                int commaCount = TopLevelCommaCount(args);
                // 5-arg legacy compat ctor (chatClient, hubContext, configuration, logger, metrics)
                // supplies its own TenantConfiguration internally — safe.
                if (commaCount <= 4)
                    continue;

                bool mentionsTenant = args.Contains("tenant", StringComparison.OrdinalIgnoreCase)
                    || args.Contains("TenantConfiguration", StringComparison.Ordinal);
                if (!mentionsTenant)
                    offenders.Add($"{cs.FullName}: {args[..Math.Min(200, args.Length)]}");
            }
        }

        offenders.Should().BeEmpty(
            "every production construction of AgentExecutionPipeline must thread in a " +
            "TenantConfiguration — the exact wiring defect behind Publix failure #2 (issue #74)");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddHttpContextAccessor();

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Anonymous:Enabled"] = "false",
            ["Anonymous:SigningKey"] = "tenant-roster-composition-signing-key-0123456789",
        }).Build();
        services.AddSingleton(config);

        services.AddSingleton<IChatClient>(new NoopChatClient());

        // The exact tenant registration Program.cs performs at startup.
        var tenant = new TenantConfiguration
        {
            Company = "TestCo",
            BrandsList = [.. TenantBrands.Select(b => new BrandConfig { Name = b, Category = "spirits" })],
        };
        services.AddSingleton(tenant);
        services.AddSingleton<ITenantProvider>(new StaticTenantProvider(tenant));

        // Anonymous chat policy fallback — RoutingServiceExtensions uses TryAdd for
        // this and the real production wiring provides one, so we register the no-op.
        services.AddSingleton<Api.Auth.IAnonymousChatPolicy>(Api.Auth.NoOpAnonymousChatPolicy.Instance);

        services.AddAgentRouting(
            promptConfig: new PromptConfiguration
            {
                Agents = new Dictionary<string, AgentDefinition>
                {
                    ["router"] = new AgentDefinition
                    {
                        Name = "router",
                        SystemPrompt = "route",
                        Key = "router",
                        Role = "orchestration",
                    },
                    ["general"] = new AgentDefinition
                    {
                        Name = "General",
                        SystemPrompt = "gen",
                        Key = "general",
                        Role = "specialist",
                        Intents = { "general/fallback" },
                    },
                },
            },
            toolRegistry: new Api.Agents.Tools.AgentToolRegistry(),
            orchestrationIntents: []);

        return services.BuildServiceProvider();
    }

    private static DirectoryInfo LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !dir.EnumerateFiles("RetailPulse.slnx").Any())
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir;
    }

    private static string ExtractBalancedArgList(string src, int openParenIndex)
    {
        int depth = 0;
        var sb = new System.Text.StringBuilder();
        for (int i = openParenIndex; i < src.Length; i++)
        {
            char c = src[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0) break;
            }
            else if (depth >= 1)
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static int TopLevelCommaCount(string args)
    {
        int depth = 0;
        int n = 0;
        foreach (char c in args)
        {
            if (c is '(' or '[' or '{' or '<') depth++;
            else if (c is ')' or ']' or '}' or '>') depth--;
            else if (c == ',' && depth == 0) n++;
        }
        return n;
    }

    private sealed class StaticTenantProvider(TenantConfiguration tenant) : ITenantProvider
    {
        public TenantConfiguration GetTenant() => tenant;
    }

    private sealed class NoopChatClient : IChatClient
    {
        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static class TestHubContext
    {
        public static Microsoft.AspNetCore.SignalR.IHubContext<Api.Hubs.TelemetryHub> Create()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSignalR();
            using ServiceProvider sp = services.BuildServiceProvider();
            return sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Api.Hubs.TelemetryHub>>();
        }
    }

    [GeneratedRegex(@"new\s+AgentExecutionPipeline\s*\(", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
