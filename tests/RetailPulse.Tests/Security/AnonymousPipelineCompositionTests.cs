using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Models;
using RetailPulse.Api.Security;
using RetailPulse.Contracts;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Composition (integration-style) regression for the blocking finding that the DI factory built
/// the <see cref="AgentExecutionPipeline"/> WITHOUT an <see cref="IAnonymousChatPolicy"/>, so the
/// write-tool filter and output-token cap never ran at runtime even though the isolated unit tests
/// on <see cref="AnonymousChatPolicy"/> passed.
///
/// Unlike the unit tests that <c>new</c> a policy directly, these resolve
/// <see cref="IAgentExecutionPipeline"/> through the SAME production registration path
/// (<see cref="RoutingServiceExtensions.AddAgentRouting"/>, plus
/// <see cref="ProviderNeutralAuthentication.AddProviderNeutralAuthentication"/> for the Anonymous
/// composition) and then drive a real execution with a capturing <see cref="IChatClient"/> and real
/// tools. They assert on the <see cref="ChatOptions"/> the pipeline actually handed to the model —
/// the exact surface that was silently unconstrained before the fix.
/// </summary>
public sealed class AnonymousPipelineCompositionTests
{
    private const string SigningKeyText = "anon-pipeline-composition-signing-key-0123456789";
    private const int ConfiguredCap = 333;

    [Fact]
    public async Task AnonymousComposition_ResolvesRealPolicy_StripsWriteTools_AndCapsOutput()
    {
        var chatClient = new CapturingChatClient();
        ServiceProvider provider = BuildProvider(chatClient, anonymousMode: true);

        // The production Anonymous wiring must win over the no-op fallback.
        provider.GetRequiredService<IAnonymousChatPolicy>().Should().BeOfType<AnonymousChatPolicy>(
            "AddAnonymousMode registers the constrained policy and AddAgentRouting's TryAdd must not override it");

        // Put an authenticated Anonymous principal on the ambient HttpContext the policy reads from.
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { User = AnonymousPrincipal("anon-subject") };

        await ExecuteAsync(provider);

        chatClient.LastOptions.Should().NotBeNull("the pipeline must have called the model");
        IReadOnlyList<string> toolNames = ToolNames(chatClient.LastOptions);

        toolNames.Should().NotContain(
            "RequestApproval",
            "the write-capable tool must be stripped for an anonymous principal at runtime — the exact bypass this fixes");
        toolNames.Should().Contain("GetSalesData", "read-only tools remain available to anonymous sessions");
        chatClient.LastOptions?.MaxOutputTokens.Should().Be(
            ConfiguredCap, "the configured Anonymous output cap must be applied by the composed pipeline");
    }

    [Fact]
    public async Task NonAnonymousComposition_UsesNoOpPolicy_RetainsTools_AndDoesNotCap()
    {
        var chatClient = new CapturingChatClient();

        // Entra/Development composition: no Anonymous wiring, so AddAgentRouting's TryAdd supplies
        // the provider-neutral no-op — never a null dependency.
        ServiceProvider provider = BuildProvider(chatClient, anonymousMode: false);

        provider.GetRequiredService<IAnonymousChatPolicy>().Should().BeOfType<NoOpAnonymousChatPolicy>(
            "non-Anonymous modes get the explicit provider-neutral no-op, not a null");

        await ExecuteAsync(provider);

        chatClient.LastOptions.Should().NotBeNull();
        IReadOnlyList<string> toolNames = ToolNames(chatClient.LastOptions);

        toolNames.Should().Contain("RequestApproval", "the no-op policy never strips tools");
        toolNames.Should().Contain("GetSalesData");
        chatClient.LastOptions?.MaxOutputTokens.Should().BeNull("the no-op policy never caps output tokens");
    }

    // ── composition helpers ─────────────────────────────────────────────────────

    private static ServiceProvider BuildProvider(CapturingChatClient chatClient, bool anonymousMode)
    {
        var settings = new Dictionary<string, string?>();
        if (anonymousMode)
        {
            settings["Authentication:Mode"] = "Anonymous";
            settings["Anonymous:AllowHosted"] = "true";
            settings["Anonymous:SigningKey"] = SigningKeyText;
            settings["Anonymous:MaxOutputTokens"] = ConfiguredCap.ToString();
            settings["Anonymous:Limits:DailyMaxRequests"] = "500";
            settings["Anonymous:Limits:DailyMaxTokens"] = "1000000";
            settings["Anonymous:Limits:DailyMaxCostUsd"] = "100";
        }

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddHttpContextAccessor();
        services.AddSingleton(config);
        services.AddSingleton(new TenantConfiguration());

        // The capturing model client the composed pipeline will call.
        services.AddSingleton<IChatClient>(chatClient);

        if (anonymousMode)
        {
            // Production Anonymous wiring: registers the real AnonymousChatPolicy (+ options + accessor).
            services.AddProviderNeutralAuthentication(config, new TestEnv("Production"));
        }

        // Production pipeline registration. This is the factory that previously omitted the policy.
        services.AddAgentRouting(
            promptConfig: RouterOnlyConfig(),
            toolRegistry: new Api.Agents.Tools.AgentToolRegistry(),
            orchestrationIntents: []);

        return services.BuildServiceProvider();
    }

    private static async Task ExecuteAsync(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();
        IAgentExecutionPipeline pipeline = scope.ServiceProvider.GetRequiredService<IAgentExecutionPipeline>();

        var context = new AgentExecutionContext
        {
            AgentName = "General",
            SystemPrompt = "You are a test agent.",
            Temperature = 0.5f,
            ModelName = "gpt-4o",
            Request = new ChatRequest("What are today's sales?", SessionId: "compose-1"),
            Tools = Tools(),
        };

        await pipeline.ExecuteAsync(context);
    }

    private static IReadOnlyList<AITool> Tools() =>
    [
        AIFunctionFactory.Create(() => "approved", "RequestApproval"),
        AIFunctionFactory.Create(() => "sales", "GetSalesData"),
    ];

    private static IReadOnlyList<string> ToolNames(ChatOptions? options) =>
        [.. (options?.Tools ?? []).OfType<AIFunction>().Select(t => t.Name)];

    private static PromptConfiguration RouterOnlyConfig() => new()
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
    };

    private static ClaimsPrincipal AnonymousPrincipal(string subject)
    {
        var identity = new ClaimsIdentity("anonymous-session");
        identity.AddClaim(new Claim(JwtRegisteredClaimNames.Sub, subject));
        identity.AddClaim(new Claim("provider", "Anonymous"));
        identity.AddClaim(new Claim("roles", "RetailPulse.Anonymous"));
        identity.AddClaim(new Claim("scp", "chat_limited"));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>Records the <see cref="ChatOptions"/> the pipeline handed to the model.</summary>
    private sealed class CapturingChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public Task<MeaiChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new MeaiChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Streaming is not exercised by this composition test.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class TestEnv : IHostEnvironment
    {
        public TestEnv(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "RetailPulse.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
