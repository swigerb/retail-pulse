using Azure.AI.Agents.Persistent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Rag.FoundryIQ;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Rag.FoundryIQ;

public sealed class FoundryIQServiceCollectionExtensionsTests
{
    [Fact]
    public void BlankEndpoint_NoRegistrationsAdded()
    {
        IServiceCollection services = BuildBaseServices();
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        services.AddFoundryIQKnowledgeProvider(config);

        using ServiceProvider sp = services.BuildServiceProvider();
        sp.GetService<FoundryIQOptions>().Should().BeNull();
        sp.GetService<PersistentAgentsClient>().Should().BeNull();
        sp.GetService<FoundryClientAccessor>().Should().BeNull();
        sp.GetService<IKnowledgeProviderContribution>().Should().BeNull();
    }

    [Fact]
    public void EnabledPath_MaterializesKnowledgeBase_AndContributesFactory()
    {
        IServiceCollection services = BuildBaseServices();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:FoundryIQ:ProjectEndpoint"] = "https://foundry.example/api/projects/p",
                ["Knowledge:FoundryIQ:VectorStoreId"] = "vs_direct",
                ["Knowledge:FoundryIQ:Model"] = "gpt-5.4-mini",
            })
            .Build();

        services.AddFoundryIQKnowledgeProvider(config);

        using ServiceProvider sp = services.BuildServiceProvider();
        sp.GetService<FoundryIQOptions>().Should().NotBeNull().And.Subject.As<FoundryIQOptions>()
            .IsConfigured.Should().BeTrue();
        sp.GetService<FoundryClientAccessor>().Should().NotBeNull();
        sp.GetServices<IKnowledgeProviderContribution>().Should().ContainSingle(
            c => c is FoundryIQProviderContribution);
    }

    [Fact]
    public void EnabledPath_MissingModel_FailsFastWithActionableMessage()
    {
        IServiceCollection services = BuildBaseServices();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:FoundryIQ:ProjectEndpoint"] = "https://foundry.example/api/projects/p",
                ["Knowledge:FoundryIQ:VectorStoreId"] = "vs_direct",
                // Model deliberately omitted.
            })
            .Build();

        Action act = () => services.AddFoundryIQKnowledgeProvider(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Knowledge:FoundryIQ:Model*");
    }

    [Fact]
    public void FoundryClientAccessor_CanonicalisesTrailingSlashes()
    {
        IServiceCollection services = BuildBaseServices();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:FoundryIQ:ProjectEndpoint"] = "https://foundry.example/api/projects/p",
                ["Knowledge:FoundryIQ:VectorStoreId"] = "vs_direct",
                ["Knowledge:FoundryIQ:Model"] = "gpt-5.4-mini",
            })
            .Build();

        services.AddFoundryIQKnowledgeProvider(config);

        using ServiceProvider sp = services.BuildServiceProvider();
        FoundryClientAccessor accessor = sp.GetRequiredService<FoundryClientAccessor>();

        var existing = new PersistentAgentsClient(
            "https://foundry.example/api/projects/p",
            new Azure.Identity.DefaultAzureCredential());
        PersistentAgentsClient first = accessor.Register(
            "https://foundry.example/api/projects/p", existing);
        PersistentAgentsClient second = accessor.Register(
            "https://foundry.example/api/projects/p/", existing);

        first.Should().BeSameAs(existing);
        second.Should().BeSameAs(existing,
            "endpoint keys must canonicalise on the trailing slash so registered clients are reused");
        accessor.EndpointCount.Should().Be(1);
    }

    private static IServiceCollection BuildBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<KnowledgeOptions>(_ => { });
        services.Configure<ObservabilityOptions>(o =>
        {
            o.MaxCostEvents = 100;
            o.CostEventTtlHours = 24;
        });
        services.AddSingleton<ICostTracker>(sp => new RecordingCostTracker());
        return services;
    }
}
