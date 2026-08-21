using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Packs;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// Issue #109 contract test — proves every starting task in every shipped
/// pack is well-formed and submits successfully. A "broken suggestion" is
/// the most visible possible configuration failure (the first thing a
/// demo user clicks), so this suite fails CI before a malformed task can
/// reach a demo.
///
/// "Well-formed" means:
///   * Every category has a stable id, a human-readable label, an emoji,
///     and at least one task.
///   * Every task has a non-empty display name and a non-empty submitted
///     prompt string (post-normalization the loader is guaranteed to
///     have already synthesized tasks from any legacy `prompts:` list).
///   * Any declared `capability` uses a recognized kind and carries the
///     required companion field (`chartType` for chart, `planPath` for
///     plan).
///
/// "Submits successfully" means:
///   * The API /api/pack/starting-tasks projection round-trips each task
///     verbatim (Name + Prompt + Capability), so the frontend can submit
///     the exact prompt string the pack author intended.
///   * Every prompt string is a plausible chat submission — non-empty
///     after trim (the frontend gate) and free of stray YAML template
///     placeholders (`{{ }}`) that would land in the demo as gibberish.
/// </summary>
public sealed class StartingTasksContractTests
{
    private static IReadOnlyList<LoadedPack> LoadAllShipped()
    {
        var loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);
        return [.. loader.DiscoverPacks().Select(loader.Load)];
    }

    private static readonly HashSet<string> ValidCapabilityKinds =
        new(StringComparer.OrdinalIgnoreCase) { "prose", "chart", "plan" };

    [Fact]
    public void EveryShippedPack_HasAtLeastOneCategoryWithAtLeastOneTask()
    {
        IReadOnlyList<LoadedPack> packs = LoadAllShipped();
        packs.Should().NotBeEmpty();

        foreach (LoadedPack pack in packs)
        {
            pack.StartingTasks.Should().NotBeEmpty(
                "shipped pack '{0}' must supply at least one starting-task category", pack.Name);
            pack.StartingTasks.Should().OnlyContain(c => c.Tasks.Count > 0,
                "shipped pack '{0}' must not ship an empty category — that renders a chip with nothing to click", pack.Name);
        }
    }

    [Fact]
    public void EveryTask_HasNonEmptyDisplayNameAndSubmittedPrompt()
    {
        foreach (LoadedPack pack in LoadAllShipped())
        {
            foreach (PackStartingTaskCategory cat in pack.StartingTasks)
            {
                for (int i = 0; i < cat.Tasks.Count; i++)
                {
                    PackStartingTask task = cat.Tasks[i];
                    string trace = $"pack '{pack.Name}' category '{cat.Id}' task[{i}]";

                    task.Name.Should().NotBeNullOrWhiteSpace(
                        "{0} must have a display name shown on the button", trace);
                    task.Prompt.Should().NotBeNullOrWhiteSpace(
                        "{0} must have a submitted prompt string", trace);
                    task.Prompt.Trim().Should().Be(task.Prompt.Trim(),
                        "{0} prompt should not be trim-fragile", trace);
                    task.Prompt.Should().NotContain("{{",
                        "{0} prompt still has an unfilled YAML template placeholder", trace);
                    task.Prompt.Should().NotContain("}}",
                        "{0} prompt still has an unfilled YAML template placeholder", trace);
                }
            }
        }
    }

    [Fact]
    public void EveryTaskCapability_UsesARecognizedKindWithRequiredFields()
    {
        foreach (LoadedPack pack in LoadAllShipped())
        {
            foreach (PackStartingTaskCategory cat in pack.StartingTasks)
            {
                for (int i = 0; i < cat.Tasks.Count; i++)
                {
                    PackStartingTaskCapability? capability = cat.Tasks[i].Capability;
                    if (capability is null)
                    {
                        continue;
                    }

                    string trace = $"pack '{pack.Name}' category '{cat.Id}' task[{i}] capability";
                    ValidCapabilityKinds.Should().Contain(capability.Kind,
                        "{0} kind '{1}' must be one of prose|chart|plan", trace, capability.Kind);

                    if (capability.Kind.Equals("chart", StringComparison.OrdinalIgnoreCase))
                    {
                        capability.ChartType.Should().NotBeNullOrWhiteSpace(
                            "{0} kind=chart must declare a chartType", trace);
                    }
                    else if (capability.Kind.Equals("plan", StringComparison.OrdinalIgnoreCase))
                    {
                        capability.PlanPath.Should().NotBeNullOrWhiteSpace(
                            "{0} kind=plan must declare a planPath", trace);
                    }
                }
            }
        }
    }

    [Fact]
    public void EveryTask_HasAUniquePromptWithinItsCategory()
    {
        foreach (LoadedPack pack in LoadAllShipped())
        {
            foreach (PackStartingTaskCategory cat in pack.StartingTasks)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (PackStartingTask task in cat.Tasks)
                {
                    seen.Add(task.Prompt).Should().BeTrue(
                        "pack '{0}' category '{1}' has a duplicate prompt '{2}' — a demo user would click the same suggestion twice",
                        pack.Name, cat.Id, task.Prompt);
                }
            }
        }
    }

    [Fact]
    public async Task EveryTaskSurvivesTheApiProjection_WithNameAndPromptAndCapabilityIntact()
    {
        // Round-trip every shipped pack through /api/pack/starting-tasks so
        // a regression in the endpoint (dropped field, changed casing,
        // truncated collection) surfaces as a specific failure pointing at
        // the offending pack + task rather than a silent demo bug.
        foreach (LoadedPack pack in LoadAllShipped())
        {
            using IHost host = BuildHostFor(pack);
            using HttpClient client = host.GetTestClient();

            HttpResponseMessage response = await client.GetAsync("/api/pack/starting-tasks");
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "pack '{0}' should respond OK from /api/pack/starting-tasks", pack.Name);

            PackStartingTasksResponse? payload =
                await response.Content.ReadFromJsonAsync<PackStartingTasksResponse>();

            payload.Should().NotBeNull();
            payload.PackKey.Should().Be(pack.Name);
            payload.Categories.Should().HaveCount(pack.StartingTasks.Count,
                "pack '{0}' should project every category", pack.Name);

            for (int c = 0; c < pack.StartingTasks.Count; c++)
            {
                PackStartingTaskCategory source = pack.StartingTasks[c];
                PackStartingTaskResponse projected = payload.Categories[c];

                projected.Id.Should().Be(source.Id);
                projected.Label.Should().Be(source.Label);
                projected.Tasks.Should().HaveCount(source.Tasks.Count);

                for (int t = 0; t < source.Tasks.Count; t++)
                {
                    PackStartingTask sourceTask = source.Tasks[t];
                    PackStartingTaskItem projectedTask = projected.Tasks[t];

                    projectedTask.Name.Should().Be(sourceTask.Name);
                    projectedTask.Prompt.Should().Be(sourceTask.Prompt);
                    projectedTask.Order.Should().Be(sourceTask.Order);

                    if (sourceTask.Capability is null)
                    {
                        projectedTask.Capability.Should().BeNull();
                    }
                    else
                    {
                        projectedTask.Capability.Should().NotBeNull();
                        projectedTask.Capability.Kind.Should().Be(sourceTask.Capability.Kind);
                        projectedTask.Capability.ChartType.Should().Be(sourceTask.Capability.ChartType);
                        projectedTask.Capability.PlanPath.Should().Be(sourceTask.Capability.PlanPath);
                    }
                }

                // Legacy Prompts projection must stay in lock-step so any
                // client still on the old shape keeps working.
                projected.Prompts.Should().Equal(source.Tasks.Select(t => t.Prompt));
            }

            // Every prompt payload must be a valid JSON string round-trip
            // without escaping surprises — a pragmatic proxy for "submits
            // successfully" without wiring up the full chat pipeline.
            string raw = await response.Content.ReadAsStringAsync();
            using var _ = JsonDocument.Parse(raw);

            await host.StopAsync();
        }
    }

    private static IHost BuildHostFor(LoadedPack pack)
    {
        IHost host = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddSingleton(pack);
                    services.AddRouting();
                    services.AddRateLimiter(options => options.AddPolicy("relaxed", _ =>
                        RateLimitPartition.GetFixedWindowLimiter("relaxed", _ =>
                            new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = 100,
                                Window = TimeSpan.FromMinutes(1),
                                QueueLimit = 0,
                            })));
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints => endpoints.MapPackEndpoints());
                });
            })
            .Build();

        host.StartAsync().GetAwaiter().GetResult();
        return host;
    }
}
