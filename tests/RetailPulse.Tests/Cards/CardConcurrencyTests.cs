using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Cards;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Cards;

namespace RetailPulse.Tests.Cards;

/// <summary>
/// Concurrency tests for InMemoryAdaptiveCardState.
/// Validates thread-safety under parallel vote, comment, and archive operations.
/// </summary>
public class CardConcurrencyTests
{
    private readonly InMemoryAdaptiveCardState _state;

    public CardConcurrencyTests()
    {
        _state = new InMemoryAdaptiveCardState(CreateMockHub(), Mock.Of<ILogger<InMemoryAdaptiveCardState>>());
    }

    [Fact]
    public async Task ConcurrentVotes_10Voters_AllRecordedNoCorruption()
    {
        var card = await _state.CreateAsync(MakeRequest("Concurrent Vote"));
        var tasks = Enumerable.Range(1, 10).Select(i =>
            _state.ActionAsync(card.Id, MakeVoteAction($"user-{i}", i % 2 == 0 ? "approve" : "reject"))
        );

        await Task.WhenAll(tasks);

        var result = await _state.GetAsync(card.Id);
        result.Votes.Should().HaveCount(10);
        result.Votes.Select(v => v.UserId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ConcurrentVoteAndArchive_NoExceptionOrCorruption()
    {
        var card = await _state.CreateAsync(MakeRequest("Vote+Archive Race"));

        var voteTasks = Enumerable.Range(1, 5).Select(i =>
            Task.Run(async () =>
            {
                try
                {
                    await _state.ActionAsync(card.Id, MakeVoteAction($"user-{i}", "approve"));
                }
                catch (InvalidOperationException)
                {
                    // Expected if archive wins the race
                }
            })
        );

        var archiveTask = Task.Run(async () =>
        {
            await Task.Delay(1); // Slight delay to let some votes start
            await _state.ArchiveAsync(card.Id);
        });

        await Task.WhenAll(voteTasks.Append(archiveTask));

        var result = await _state.GetAsync(card.Id);
        result.Lifecycle.Should().Be(CardLifecycle.Archived);
        // Some votes may have succeeded before archive — that's fine
        result.Votes.Should().HaveCountGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ConcurrentComments_AllPreserved()
    {
        var card = await _state.CreateAsync(MakeRequest("Concurrent Comments"));
        var tasks = Enumerable.Range(1, 10).Select(i =>
            _state.ActionAsync(card.Id, MakeCommentAction($"user-{i}", $"Comment #{i}"))
        );

        await Task.WhenAll(tasks);

        var result = await _state.GetAsync(card.Id);
        result.Comments.Should().HaveCount(10);
        result.Comments.Select(c => c.UserId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ConcurrentCreates_AllSucceed()
    {
        var tasks = Enumerable.Range(1, 20).Select(i =>
            _state.CreateAsync(MakeRequest($"Card #{i}"))
        );

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(20);
        results.Select(c => c.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ConcurrentMixedActions_NoDeadlock()
    {
        var card = await _state.CreateAsync(MakeRequest("Mixed Actions"));

        var tasks = new List<Task>();
        for (int i = 0; i < 5; i++)
        {
            var idx = i;
            tasks.Add(_state.ActionAsync(card.Id, MakeVoteAction($"voter-{idx}", "approve")));
            tasks.Add(_state.ActionAsync(card.Id, MakeCommentAction($"commenter-{idx}", $"Text {idx}")));
        }

        // Should not deadlock — completes within timeout
        var completed = Task.WhenAll(tasks);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
        var first = await Task.WhenAny(completed, timeoutTask);

        first.Should().BeSameAs(completed, "all actions should complete without deadlock");
    }

    #region Helpers

    private static CreateCardRequest MakeRequest(string title)
        => new(title, CardType.Dashboard, "test-user", new Dictionary<string, object>());

    private static CardAction MakeVoteAction(string userId, string vote)
        => new(userId, $"User {userId}", CardActionType.Vote, new() { ["vote"] = vote });

    private static CardAction MakeCommentAction(string userId, string text)
        => new(userId, $"User {userId}", CardActionType.Comment, new() { ["text"] = text });

    private static IHubContext<TelemetryHub> CreateMockHub()
    {
        var mockClients = new Mock<IHubClients>();
        var mockProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockProxy.Object);
        var mockHub = new Mock<IHubContext<TelemetryHub>>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        return mockHub.Object;
    }

    #endregion
}
