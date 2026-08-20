using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Rag;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag;

/// <summary>
/// Deterministic resolution + fail-fast tests for
/// <see cref="KnowledgeProviderSelector"/>. Covers the documented defaults,
/// unknown-value rejection, and the "recognized-but-unregistered" case where
/// a cloud mode is selected without the corresponding module wired up
/// (issues #103 / #104).
/// </summary>
public sealed class KnowledgeProviderSelectorTests
{
    private static KnowledgeProviderSelector CreateSelector(
        string? mode,
        string? degradation,
        KnowledgeProviderRegistry? registry = null)
    {
        IOptions<KnowledgeProviderOptions> options = Options.Create(new KnowledgeProviderOptions
        {
            Mode = mode,
            Degradation = degradation,
        });
        return new KnowledgeProviderSelector(options, registry ?? new KnowledgeProviderRegistry());
    }

    private static InMemoryKnowledgeBase CreateInMemory() => new(
        NullLoggerFactory.Instance.CreateLogger<InMemoryKnowledgeBase>(),
        Options.Create(new KnowledgeOptions()));

    // ── Mode resolution ────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveMode_MissingOrBlank_DefaultsToInMemory(string? raw)
    {
        KnowledgeProviderSelector selector = CreateSelector(mode: raw, degradation: null);

        selector.ResolveMode().Should().Be(KnowledgeProviderMode.InMemory);
    }

    [Theory]
    [InlineData("InMemory", KnowledgeProviderMode.InMemory)]
    [InlineData("inmemory", KnowledgeProviderMode.InMemory)]
    [InlineData("  InMemory  ", KnowledgeProviderMode.InMemory)]
    [InlineData("AzureAISearch", KnowledgeProviderMode.AzureAISearch)]
    [InlineData("azureaisearch", KnowledgeProviderMode.AzureAISearch)]
    [InlineData("FoundryIQ", KnowledgeProviderMode.FoundryIQ)]
    public void ResolveMode_RecognizedName_ReturnsMatchingEnum(string raw, KnowledgeProviderMode expected)
    {
        KnowledgeProviderSelector selector = CreateSelector(mode: raw, degradation: null);

        selector.ResolveMode().Should().Be(expected);
    }

    [Theory]
    [InlineData("Sqlite")]                // typo / not an enum
    [InlineData("AzureAiSearch__typo")]   // close but wrong
    [InlineData("!!invalid!!")]           // punctuation garbage
    public void ResolveMode_UnknownName_ThrowsWithActionableMessage(string raw)
    {
        KnowledgeProviderSelector selector = CreateSelector(mode: raw, degradation: null);

        Action act = () => selector.ResolveMode();

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain(raw, "the operator's exact input is echoed back so they can fix it")
                .And.Contain("InMemory", "valid options must be listed in the error")
                .And.Contain("AzureAISearch")
                .And.Contain("FoundryIQ");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("999")]
    public void ResolveMode_BareNumber_Rejected(string raw)
    {
        // A mode is a documented, readable name — never an integer. This
        // prevents an accidental integer overriding config from silently
        // selecting a cloud provider.
        KnowledgeProviderSelector selector = CreateSelector(mode: raw, degradation: null);

        Action act = () => selector.ResolveMode();

        act.Should().Throw<InvalidOperationException>();
    }

    // ── Degradation resolution ─────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveDegradation_MissingOrBlank_DefaultsToFailLoud(string? raw)
    {
        // FailLoud is the safer default: a configured cloud provider that
        // silently dropped back would mask real outages. The operator opts
        // into FallbackToInMemory deliberately.
        KnowledgeProviderSelector selector = CreateSelector(mode: null, degradation: raw);

        selector.ResolveDegradation().Should().Be(KnowledgeDegradationMode.FailLoud);
    }

    [Theory]
    [InlineData("FailLoud", KnowledgeDegradationMode.FailLoud)]
    [InlineData("failloud", KnowledgeDegradationMode.FailLoud)]
    [InlineData("FallbackToInMemory", KnowledgeDegradationMode.FallbackToInMemory)]
    [InlineData("fallbacktoinmemory", KnowledgeDegradationMode.FallbackToInMemory)]
    public void ResolveDegradation_RecognizedName_ReturnsMatchingEnum(string raw, KnowledgeDegradationMode expected)
    {
        KnowledgeProviderSelector selector = CreateSelector(mode: null, degradation: raw);

        selector.ResolveDegradation().Should().Be(expected);
    }

    [Fact]
    public void ResolveDegradation_UnknownName_ThrowsWithActionableMessage()
    {
        KnowledgeProviderSelector selector = CreateSelector(mode: null, degradation: "SilentlyReturnEmpty");

        Action act = () => selector.ResolveDegradation();

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("SilentlyReturnEmpty")
                .And.Contain("FailLoud")
                .And.Contain("FallbackToInMemory")
                .And.Contain("NEVER silently returns",
                    "the error message documents the empty-result invariant");
    }

    // ── Provider materialization ──────────────────────────────────────

    [Fact]
    public void CreatePrimary_InMemoryRegistered_ResolvesInMemoryInstance()
    {
        var registry = new KnowledgeProviderRegistry();
        InMemoryKnowledgeBase concrete = CreateInMemory();
        registry.Register(KnowledgeProviderMode.InMemory, _ => concrete);

        KnowledgeProviderSelector selector = CreateSelector(mode: "InMemory", degradation: null, registry: registry);

        ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        IKnowledgeBase primary = selector.CreatePrimary(services);

        primary.Should().BeSameAs(concrete);
        primary.GetCapabilities().ProviderName.Should().Be(InMemoryKnowledgeBase.ProviderName);
    }

    [Fact]
    public void CreatePrimary_RecognizedModeNotRegistered_ThrowsWithActionableMessage()
    {
        // AzureAISearch is a known enum value but its factory is only wired
        // up by issue #103. Selecting it without registration must fail
        // startup with an actionable message — never silently degrade.
        var registry = new KnowledgeProviderRegistry();
        registry.Register(KnowledgeProviderMode.InMemory, _ => CreateInMemory());

        KnowledgeProviderSelector selector = CreateSelector(mode: "AzureAISearch", degradation: null, registry: registry);
        ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        Action act = () => selector.CreatePrimary(services);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("AzureAISearch")
                .And.Contain("not registered")
                .And.Contain("InMemory", "the message lists which modes ARE registered");
    }

    [Fact]
    public void ResolveMode_BeforeCreatePrimary_UnknownMode_ThrowsBeforeRegistryLookup()
    {
        // The mode parser rejects unknown names before the registry is
        // consulted, so the operator sees "not a recognized mode" rather
        // than "not registered" for a typo.
        var registry = new KnowledgeProviderRegistry();
        registry.Register(KnowledgeProviderMode.InMemory, _ => CreateInMemory());

        KnowledgeProviderSelector selector = CreateSelector(mode: "TotallyMadeUp", degradation: null, registry: registry);
        ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        Action act = () => selector.CreatePrimary(services);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("not a recognized knowledge provider mode");
    }
}
