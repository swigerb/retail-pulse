using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Api.Models;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts.Routing;
using RetailPulse.Tests.Fixtures;

namespace RetailPulse.Tests.Planning;

/// <summary>
/// Deterministic tests for <see cref="PlanBuilder"/> — the stubbed IChatClient
/// returns fixed JSON so we can pin the planner shape without any live model
/// call. Every unusable path is a required terminal outcome for #93.
/// </summary>
public sealed class PlanBuilderTests
{
    private static AgentDefinition PlannerDef() => new()
    {
        Key = "planner",
        Name = "Plan-First Orchestrator",
        Model = "gpt-5.4-mini",
        SystemPrompt = "You are the planner.",
        Temperature = 0.1,
    };

    private static PlanPersistenceOptions Options() => new()
    {
        MaxStepCount = 5,
        MinDetectedIntentsForPlan = 2,
    };

    private static IReadOnlyList<ISpecialistAgent> Roster() =>
    [
        AgentTestFixtures.CreateMockSpecialist("scorecard", ["scorecard"], "Scorecard"),
        AgentTestFixtures.CreateMockSpecialist("demand-forecasting", ["demand"], "Demand"),
        AgentTestFixtures.CreateMockSpecialist("competitive-intel", ["competitive"], "Competitive"),
    ];

    private static PlanBuilder MakeBuilder(string plannerJson)
    {
        IChatClient client = AgentTestFixtures.CreateMockChatClient(plannerJson);
        return new PlanBuilder(
            client,
            PlannerDef(),
            Options(),
            NullLogger<PlanBuilder>.Instance,
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task Multi_domain_plan_returns_ordered_distinct_specialists()
    {
        const string json = /*lang=json,strict*/ @"{
            ""steps"": [
                { ""specialist_key"": ""scorecard"", ""intent"": ""scorecard"", ""action"": ""summarize scorecard"" },
                { ""specialist_key"": ""demand-forecasting"", ""intent"": ""demand"", ""action"": ""forecast demand"" },
                { ""specialist_key"": ""competitive-intel"", ""intent"": ""competitive"", ""action"": ""check competitors"" }
            ]
        }";

        PlanBuilder builder = MakeBuilder(json);
        PlanBuildResult result = await builder.BuildAsync(
            "How is brand X performing and what should we do next?",
            Roster(),
            ["scorecard", "demand", "competitive"],
            CancellationToken.None);

        result.IsUnusable.Should().BeFalse();
        result.Steps.Should().HaveCount(3);
        result.Steps.Select(s => s.SpecialistKey).Should().BeEquivalentTo(
            ["scorecard", "demand-forecasting", "competitive-intel"],
            opts => opts.WithStrictOrdering());
        result.Steps.Select(s => s.SpecialistKey).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public async Task Empty_json_is_unusable()
    {
        PlanBuilder builder = MakeBuilder("");
        PlanBuildResult result = await builder.BuildAsync(
            "How is brand X performing?", Roster(), ["scorecard"], CancellationToken.None);

        result.IsUnusable.Should().BeTrue();
        result.Steps.Should().BeEmpty();
        result.UnusableReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Unknown_specialist_key_is_unusable()
    {
        const string json = /*lang=json,strict*/ @"{ ""steps"": [{ ""specialist_key"": ""not-a-real-agent"", ""intent"": ""x"", ""action"": ""y"" }] }";
        PlanBuilder builder = MakeBuilder(json);

        PlanBuildResult result = await builder.BuildAsync(
            "anything", Roster(), ["scorecard"], CancellationToken.None);

        result.IsUnusable.Should().BeTrue();
        result.Steps.Should().BeEmpty();
        result.UnusableReason.Should().Contain("not-a-real-agent");
    }

    [Fact]
    public async Task Empty_step_list_with_reason_is_unusable_with_that_reason()
    {
        const string json = /*lang=json,strict*/ @"{ ""steps"": [], ""reason"": ""single-domain, planner declined"" }";
        PlanBuilder builder = MakeBuilder(json);

        PlanBuildResult result = await builder.BuildAsync(
            "anything", Roster(), ["scorecard"], CancellationToken.None);

        result.IsUnusable.Should().BeTrue();
        result.UnusableReason.Should().Contain("planner declined");
    }

    [Fact]
    public async Task Step_count_over_max_is_unusable()
    {
        // 6 steps -> exceeds MaxStepCount=5
        const string json = /*lang=json,strict*/ @"{ ""steps"": [
            { ""specialist_key"": ""scorecard"", ""action"": ""a"" },
            { ""specialist_key"": ""scorecard"", ""action"": ""a"" },
            { ""specialist_key"": ""scorecard"", ""action"": ""a"" },
            { ""specialist_key"": ""scorecard"", ""action"": ""a"" },
            { ""specialist_key"": ""scorecard"", ""action"": ""a"" },
            { ""specialist_key"": ""scorecard"", ""action"": ""a"" }
        ] }";
        PlanBuilder builder = MakeBuilder(json);

        PlanBuildResult result = await builder.BuildAsync(
            "anything", Roster(), ["scorecard"], CancellationToken.None);

        result.IsUnusable.Should().BeTrue();
        result.UnusableReason.Should().Contain("step cap");
    }

    [Fact]
    public async Task Malformed_json_is_unusable()
    {
        PlanBuilder builder = MakeBuilder("{ not valid json");
        PlanBuildResult result = await builder.BuildAsync(
            "anything", Roster(), ["scorecard"], CancellationToken.None);

        result.IsUnusable.Should().BeTrue();
        result.UnusableReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Empty_roster_is_unusable_without_calling_planner()
    {
        // If we somehow reached the builder with an empty roster, it should
        // shortcut to unusable without producing steps.
        var mock = new Mock<IChatClient>();
        var builder = new PlanBuilder(
            mock.Object,
            PlannerDef(),
            Options(),
            NullLogger<PlanBuilder>.Instance,
            NullLoggerFactory.Instance);

        PlanBuildResult result = await builder.BuildAsync(
            "anything", [], ["scorecard"], CancellationToken.None);

        result.IsUnusable.Should().BeTrue();
        mock.Verify(
            m => m.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
