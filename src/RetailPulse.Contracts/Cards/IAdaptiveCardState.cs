namespace RetailPulse.Contracts.Cards;

/// <summary>
/// Manages multi-user collaborative card state — voting, comments, drill-down, escalation.
/// Thread-safe for concurrent access from multiple SignalR clients.
/// </summary>
public interface IAdaptiveCardState
{
    Task<AdaptiveCard> CreateAsync(CreateCardRequest request, CancellationToken ct = default);
    Task<AdaptiveCard> GetAsync(string cardId, CancellationToken ct = default);
    Task<AdaptiveCard> ActionAsync(string cardId, CardAction action, CancellationToken ct = default);
    Task<IReadOnlyList<AdaptiveCard>> GetActiveAsync(CancellationToken ct = default);
    Task ArchiveAsync(string cardId, CancellationToken ct = default);
}

public record AdaptiveCard(
    string Id, string Title, CardType Type, CardLifecycle Lifecycle,
    string CreatedBy, DateTime CreatedAt,
    IReadOnlyList<CardVote> Votes, IReadOnlyList<CardComment> Comments,
    Dictionary<string, object> Data, string? EscalationReason);

public enum CardType { Voting, DrillDown, Dashboard, Briefing }
public enum CardLifecycle { Active, Voting, Decided, Archived }

public record CardVote(string UserId, string UserName, string Vote, DateTime Timestamp);
public record CardComment(string UserId, string UserName, string Text, DateTime Timestamp);
public record CardAction(string UserId, string UserName, CardActionType Type, Dictionary<string, string> Params);
public enum CardActionType { Vote, Comment, DrillDown, Escalate }

public record CreateCardRequest(string Title, CardType Type, string CreatedBy, Dictionary<string, object> Data);
