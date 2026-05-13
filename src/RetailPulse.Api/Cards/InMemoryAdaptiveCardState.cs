using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Cards;
using RetailPulse.Contracts.Consensus;

namespace RetailPulse.Api.Cards;

/// <summary>
/// In-memory collaborative card state with ConcurrentDictionary for thread-safety.
/// State transitions: Active → Voting → Decided → Archived.
/// Escalation triggers on split vote (50/50) or explicit escalation action.
/// Real-time sync via SignalR on every action.
/// </summary>
public class InMemoryAdaptiveCardState : IAdaptiveCardState
{
    private readonly ConcurrentDictionary<string, CardState> _cards = new();
    private readonly IHubContext<TelemetryHub> _hub;
    private readonly ILogger<InMemoryAdaptiveCardState> _logger;

    public InMemoryAdaptiveCardState(
        IHubContext<TelemetryHub> hub,
        ILogger<InMemoryAdaptiveCardState> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task<AdaptiveCard> CreateAsync(CreateCardRequest request, CancellationToken ct = default)
    {
        var id = $"card-{Guid.NewGuid():N}";
        var lifecycle = request.Type == CardType.Voting ? CardLifecycle.Voting : CardLifecycle.Active;

        var state = new CardState
        {
            Id = id,
            Title = request.Title,
            Type = request.Type,
            Lifecycle = lifecycle,
            CreatedBy = request.CreatedBy,
            CreatedAt = DateTime.UtcNow,
            Data = new Dictionary<string, object>(request.Data)
        };

        if (!_cards.TryAdd(id, state))
            throw new InvalidOperationException("Failed to create card — ID collision.");

        var card = state.ToAdaptiveCard();

        await _hub.Clients.All.SendAsync("card:created", card, ct);

        _logger.LogInformation("Card created: {CardId} ({Type}) by {CreatedBy}", id, request.Type, request.CreatedBy);
        return card;
    }

    public Task<AdaptiveCard> GetAsync(string cardId, CancellationToken ct = default)
    {
        if (!_cards.TryGetValue(cardId, out var state))
            throw new KeyNotFoundException($"Card '{cardId}' not found.");

        return Task.FromResult(state.ToAdaptiveCard());
    }

    public async Task<AdaptiveCard> ActionAsync(string cardId, CardAction action, CancellationToken ct = default)
    {
        if (!_cards.TryGetValue(cardId, out var state))
            throw new KeyNotFoundException($"Card '{cardId}' not found.");

        AdaptiveCard updatedCard;
        CardLifecycle? oldLifecycle = null;
        CardLifecycle? newLifecycle = null;

        // Lock on the individual card state for atomic action processing
        lock (state)
        {
            if (state.Lifecycle == CardLifecycle.Archived)
                throw new InvalidOperationException("Cannot perform actions on archived cards.");

            oldLifecycle = state.Lifecycle;

            switch (action.Type)
            {
                case CardActionType.Vote:
                    ProcessVote(state, action);
                    break;
                case CardActionType.Comment:
                    ProcessComment(state, action);
                    break;
                case CardActionType.DrillDown:
                    ProcessDrillDown(state, action);
                    break;
                case CardActionType.Escalate:
                    ProcessEscalation(state, action);
                    break;
                default:
                    throw new ArgumentException($"Unknown action type: {action.Type}");
            }

            newLifecycle = state.Lifecycle;
            updatedCard = state.ToAdaptiveCard();
        }

        // Send SignalR events outside the lock
        await _hub.Clients.All.SendAsync("card:action", new { cardId, action, updatedCard }, ct);

        if (oldLifecycle != newLifecycle)
        {
            await _hub.Clients.All.SendAsync("card:lifecycle", new
            {
                cardId,
                oldState = oldLifecycle.ToString(),
                newState = newLifecycle.ToString()
            }, ct);
        }

        _logger.LogInformation("Card action: {CardId} {ActionType} by {UserId}", cardId, action.Type, action.UserId);
        return updatedCard;
    }

    public Task<IReadOnlyList<AdaptiveCard>> GetActiveAsync(CancellationToken ct = default)
    {
        var active = _cards.Values
            .Where(s => s.Lifecycle != CardLifecycle.Archived)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.ToAdaptiveCard())
            .ToList();

        return Task.FromResult<IReadOnlyList<AdaptiveCard>>(active);
    }

    /// <summary>
    /// List cards with optional type and lifecycle filters.
    /// </summary>
    public Task<IReadOnlyList<AdaptiveCard>> ListAsync(
        CardType? type = null,
        CardLifecycle? lifecycle = null,
        CancellationToken ct = default)
    {
        IEnumerable<CardState> results = _cards.Values;

        if (type.HasValue)
            results = results.Where(s => s.Type == type.Value);

        if (lifecycle.HasValue)
            results = results.Where(s => s.Lifecycle == lifecycle.Value);

        var list = results
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.ToAdaptiveCard())
            .ToList();

        return Task.FromResult<IReadOnlyList<AdaptiveCard>>(list);
    }

    /// <summary>
    /// Creates a Voting card from a council verdict, mapping agent votes to card votes.
    /// </summary>
    public async Task<AdaptiveCard> CreateFromVerdictAsync(CouncilVerdict verdict, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>
        {
            ["brand"] = verdict.Brand,
            ["region"] = verdict.Region ?? "All Regions",
            ["overall_rating"] = verdict.OverallRating.ToString(),
            ["synthesis"] = verdict.Synthesis,
            ["is_unanimous"] = verdict.IsUnanimous,
            ["disagreements"] = verdict.Disagreements,
            ["action_items"] = verdict.ActionItems,
            ["expected_voters"] = verdict.Votes.Length
        };

        var request = new CreateCardRequest(
            $"Council Verdict: {verdict.Brand} — {verdict.OverallRating}",
            CardType.Voting,
            "council-orchestrator",
            data);

        var card = await CreateAsync(request, ct);

        // Map council agent votes to card votes
        if (_cards.TryGetValue(card.Id, out var state))
        {
            lock (state)
            {
                foreach (var agentVote in verdict.Votes)
                {
                    state.Votes.Add(new CardVote(
                        agentVote.AgentId,
                        agentVote.AgentName,
                        agentVote.Rating.ToString(),
                        DateTime.UtcNow));
                }
            }
        }

        _logger.LogInformation(
            "Voting card created from council verdict: {CardId} brand={Brand} rating={Rating}",
            card.Id, verdict.Brand, verdict.OverallRating);

        return _cards.TryGetValue(card.Id, out var updated)
            ? updated.ToAdaptiveCard()
            : card;
    }

    public async Task ArchiveAsync(string cardId, CancellationToken ct = default)
    {
        if (!_cards.TryGetValue(cardId, out var state))
            throw new KeyNotFoundException($"Card '{cardId}' not found.");

        var oldState = state.Lifecycle;

        lock (state)
        {
            state.Lifecycle = CardLifecycle.Archived;
        }

        await _hub.Clients.All.SendAsync("card:lifecycle", new
        {
            cardId,
            oldState = oldState.ToString(),
            newState = CardLifecycle.Archived.ToString()
        }, ct);

        _logger.LogInformation("Card archived: {CardId}", cardId);
    }

    private static void ProcessVote(CardState state, CardAction action)
    {
        var voteValue = action.Params.GetValueOrDefault("vote", "approve");

        // Replace existing vote from the same user
        state.Votes.RemoveAll(v => v.UserId == action.UserId);
        state.Votes.Add(new CardVote(action.UserId, action.UserName, voteValue, DateTime.UtcNow));

        // Transition to Voting if still Active
        if (state.Lifecycle == CardLifecycle.Active)
            state.Lifecycle = CardLifecycle.Voting;

        if (state.Votes.Count >= 2)
        {
            var groups = state.Votes.GroupBy(v => v.Vote).ToList();

            // Check for split vote (50/50) → escalation
            if (groups.Count == 2)
            {
                var counts = groups.Select(g => g.Count()).OrderBy(c => c).ToList();
                if (counts[0] == counts[1])
                {
                    state.EscalationReason = "Split vote detected (50/50) — escalated for manager review.";
                }
            }

            // If there's a clear majority AND no prior escalation, mark as decided
            // Once escalated, only explicit escalation action or archive can resolve
            if (state.EscalationReason == null)
            {
                var majority = groups.OrderByDescending(g => g.Count()).First();
                if (majority.Count() > state.Votes.Count / 2.0)
                {
                    state.Lifecycle = CardLifecycle.Decided;
                }
            }
        }
    }

    private static void ProcessComment(CardState state, CardAction action)
    {
        var text = action.Params.GetValueOrDefault("text", "");
        if (!string.IsNullOrWhiteSpace(text))
        {
            state.Comments.Add(new CardComment(action.UserId, action.UserName, text, DateTime.UtcNow));
        }
    }

    private static void ProcessDrillDown(CardState state, CardAction action)
    {
        var field = action.Params.GetValueOrDefault("field", "detail");
        state.Data[$"drilldown:{field}"] = $"Drill-down requested by {action.UserName} at {DateTime.UtcNow:O}";
    }

    private static void ProcessEscalation(CardState state, CardAction action)
    {
        var reason = action.Params.GetValueOrDefault("reason", "Manual escalation requested.");
        state.EscalationReason = reason;
        state.Lifecycle = CardLifecycle.Decided;
    }

    /// <summary>
    /// Mutable internal state — exposed as immutable AdaptiveCard via ToAdaptiveCard().
    /// </summary>
    private sealed class CardState
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required CardType Type { get; init; }
        public CardLifecycle Lifecycle { get; set; }
        public required string CreatedBy { get; init; }
        public required DateTime CreatedAt { get; init; }
        public List<CardVote> Votes { get; } = [];
        public List<CardComment> Comments { get; } = [];
        public Dictionary<string, object> Data { get; init; } = [];
        public string? EscalationReason { get; set; }

        public AdaptiveCard ToAdaptiveCard() => new(
            Id, Title, Type, Lifecycle, CreatedBy, CreatedAt,
            Votes.ToList().AsReadOnly(),
            Comments.ToList().AsReadOnly(),
            new Dictionary<string, object>(Data),
            EscalationReason);
    }
}
