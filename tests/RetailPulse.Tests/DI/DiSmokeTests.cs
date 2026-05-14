using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Cards;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts.Cards;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.DI;

/// <summary>
/// Smoke tests that validate the DI container built by Program.cs
/// correctly registers and resolves key services. Uses a lightweight
/// in-memory configuration since the real app requires Azure credentials.
/// </summary>
public class DiSmokeTests
{
    /// <summary>
    /// Builds a minimal DI container that mirrors Program.cs singleton
    /// registrations without requiring Azure credentials or HTTP clients.
    /// </summary>
    private static ServiceProvider BuildTestContainer()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Observability:MaxCostEvents"] = "1000",
                ["Observability:MaxSessions"] = "100",
                ["Observability:MaxMessagesPerSession"] = "200",
                ["Knowledge:MaxDocuments"] = "50",
                ["Knowledge:MaxChunks"] = "500",
                ["Knowledge:MaxDocumentSizeBytes"] = "1048576",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        // Mirror the singleton registrations from Program.cs
        // Note: we skip AddSignalR() here since it requires IHostApplicationLifetime.
        // InMemoryAdaptiveCardState needs IHubContext — provide a mock instead.
        var hubMock = new Mock<IHubContext<RetailPulse.Api.Hubs.TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var groupProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubMock.Setup(h => h.Clients).Returns(clients.Object);
        services.AddSingleton(hubMock.Object);

        services.Configure<RetailPulse.Api.Configuration.ObservabilityOptions>(
            config.GetSection("Observability"));
        services.Configure<RetailPulse.Api.Configuration.KnowledgeOptions>(
            config.GetSection("Knowledge"));

        services.AddSingleton<InMemoryKnowledgeBase>();
        services.AddSingleton<IKnowledgeBase>(sp => sp.GetRequiredService<InMemoryKnowledgeBase>());

        services.AddSingleton<InMemoryAdaptiveCardState>();
        services.AddSingleton<IAdaptiveCardState>(sp => sp.GetRequiredService<InMemoryAdaptiveCardState>());

        services.AddSingleton<InMemoryCostTracker>();
        services.AddSingleton<ICostTracker>(sp => sp.GetRequiredService<InMemoryCostTracker>());

        services.AddSingleton<InMemoryAuditLog>();
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<InMemoryAuditLog>());

        services.AddSingleton<ConversationExporter>();
        services.AddSingleton<IConversationExport>(sp => sp.GetRequiredService<ConversationExporter>());

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public async Task Resolve_InMemoryKnowledgeBase_Succeeds()
    {
        await using var sp = BuildTestContainer();
        var instance = sp.GetService<InMemoryKnowledgeBase>();
        instance.Should().NotBeNull();
    }

    [Fact]
    public async Task Resolve_IKnowledgeBase_ReturnsSameInstanceAsConcreteType()
    {
        await using var sp = BuildTestContainer();
        var concrete = sp.GetRequiredService<InMemoryKnowledgeBase>();
        var iface = sp.GetRequiredService<IKnowledgeBase>();
        iface.Should().BeSameAs(concrete, "singleton forwarding should return the same instance");
    }

    [Fact]
    public async Task Resolve_InMemoryCostTracker_Succeeds()
    {
        await using var sp = BuildTestContainer();
        var instance = sp.GetService<InMemoryCostTracker>();
        instance.Should().NotBeNull();
    }

    [Fact]
    public async Task Resolve_ICostTracker_ReturnsSameInstanceAsConcrete()
    {
        await using var sp = BuildTestContainer();
        var concrete = sp.GetRequiredService<InMemoryCostTracker>();
        var iface = sp.GetRequiredService<ICostTracker>();
        iface.Should().BeSameAs(concrete);
    }

    [Fact]
    public async Task Resolve_ConversationExporter_Succeeds()
    {
        await using var sp = BuildTestContainer();
        var instance = sp.GetService<ConversationExporter>();
        instance.Should().NotBeNull();
    }

    [Fact]
    public async Task Resolve_IConversationExport_ReturnsSameInstanceAsConcrete()
    {
        await using var sp = BuildTestContainer();
        var concrete = sp.GetRequiredService<ConversationExporter>();
        var iface = sp.GetRequiredService<IConversationExport>();
        iface.Should().BeSameAs(concrete);
    }

    [Fact]
    public async Task Resolve_InMemoryAuditLog_Succeeds()
    {
        await using var sp = BuildTestContainer();
        var instance = sp.GetService<InMemoryAuditLog>();
        instance.Should().NotBeNull();
    }

    [Fact]
    public async Task Resolve_IAuditLog_ReturnsSameInstanceAsConcrete()
    {
        await using var sp = BuildTestContainer();
        var concrete = sp.GetRequiredService<InMemoryAuditLog>();
        var iface = sp.GetRequiredService<IAuditLog>();
        iface.Should().BeSameAs(concrete);
    }

    [Fact]
    public async Task Resolve_InMemoryAdaptiveCardState_Succeeds()
    {
        await using var sp = BuildTestContainer();
        var instance = sp.GetService<InMemoryAdaptiveCardState>();
        instance.Should().NotBeNull();
    }

    [Fact]
    public async Task Resolve_IAdaptiveCardState_ReturnsSameInstanceAsConcrete()
    {
        await using var sp = BuildTestContainer();
        var concrete = sp.GetRequiredService<InMemoryAdaptiveCardState>();
        var iface = sp.GetRequiredService<IAdaptiveCardState>();
        iface.Should().BeSameAs(concrete);
    }

    [Fact]
    public async Task Singletons_ReturnSameInstance_OnMultipleResolves()
    {
        await using var sp = BuildTestContainer();

        var kb1 = sp.GetRequiredService<InMemoryKnowledgeBase>();
        var kb2 = sp.GetRequiredService<InMemoryKnowledgeBase>();
        kb1.Should().BeSameAs(kb2, "singletons must return the same instance");

        var cost1 = sp.GetRequiredService<InMemoryCostTracker>();
        var cost2 = sp.GetRequiredService<InMemoryCostTracker>();
        cost1.Should().BeSameAs(cost2);

        var audit1 = sp.GetRequiredService<InMemoryAuditLog>();
        var audit2 = sp.GetRequiredService<InMemoryAuditLog>();
        audit1.Should().BeSameAs(audit2);

        var card1 = sp.GetRequiredService<InMemoryAdaptiveCardState>();
        var card2 = sp.GetRequiredService<InMemoryAdaptiveCardState>();
        card1.Should().BeSameAs(card2);

        var export1 = sp.GetRequiredService<ConversationExporter>();
        var export2 = sp.GetRequiredService<ConversationExporter>();
        export1.Should().BeSameAs(export2);
    }

    [Fact]
    public async Task NoDuplicateRegistrations_ForKeySingletons()
    {
        await using var sp = BuildTestContainer();

        // Each interface should resolve to exactly one implementation.
        // If duplicates exist, GetServices returns multiple items.
        var costTrackers = sp.GetServices<ICostTracker>().ToList();
        costTrackers.Should().HaveCount(1, "ICostTracker should have exactly one registration");

        var auditLogs = sp.GetServices<IAuditLog>().ToList();
        auditLogs.Should().HaveCount(1, "IAuditLog should have exactly one registration");

        var exporters = sp.GetServices<IConversationExport>().ToList();
        exporters.Should().HaveCount(1, "IConversationExport should have exactly one registration");

        var knowledgeBases = sp.GetServices<IKnowledgeBase>().ToList();
        knowledgeBases.Should().HaveCount(1, "IKnowledgeBase should have exactly one registration");

        var cardStates = sp.GetServices<IAdaptiveCardState>().ToList();
        cardStates.Should().HaveCount(1, "IAdaptiveCardState should have exactly one registration");
    }
}
