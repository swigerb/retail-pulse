using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Durable state store for plan review and clarification suspensions (#94).
/// Writes a genuine Microsoft.Agents.AI.Workflows checkpoint through
/// <see cref="ICheckpointStore{TStoreObject}.CreateCheckpointAsync"/> — the
/// framework's public JSON-serialized checkpoint API, not a bespoke JSON
/// marker written next to it. Reads walk the same store through
/// <see cref="CheckpointManager.GetLatestCheckpointAsync"/> and
/// <see cref="ICheckpointStore{TStoreObject}.RetrieveCheckpointAsync"/> so
/// a process created AFTER a suspension sees exactly what the process that
/// wrote the checkpoint wrote.
///
/// <para>
/// One session id per plan (<c>plan-review::{planId}</c>). Every round /
/// clarification suspension appends a new checkpoint to that session; the
/// latest wins on resume. The framework's own checkpoint parent-chain is
/// unused — reviews are strictly linear (round N supersedes round N-1) so a
/// flat sequence of orphan checkpoints under one session id keeps the file
/// layout simple and audit-friendly.
/// </para>
/// </summary>
public sealed class PlanReviewCheckpointService
{
    private readonly ICheckpointStore<JsonElement> _store;
    private readonly CheckpointManager _manager;
    private readonly ILogger<PlanReviewCheckpointService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public PlanReviewCheckpointService(
        ICheckpointStore<JsonElement> store,
        CheckpointManager manager,
        ILogger<PlanReviewCheckpointService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Deterministic session id used for every checkpoint tied to a single
    /// plan. Exposed so tests can assert the same session id the coordinator
    /// used to write the checkpoint is the one the resume path reads.
    /// </summary>
    public static string SessionIdFor(string planId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        return $"plan-review::{planId}";
    }

    /// <summary>
    /// Save a new checkpoint through the framework store. Returns the
    /// framework-issued <see cref="CheckpointInfo"/> so callers can persist
    /// it into the approval row's audit record (round → checkpoint id).
    /// </summary>
    public async ValueTask<CheckpointInfo> SaveAsync(
        PlanReviewCheckpointState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ct.ThrowIfCancellationRequested();

        JsonElement value = JsonSerializer.SerializeToElement(state, _jsonOptions);
        string sessionId = SessionIdFor(state.PlanId);
        CheckpointInfo info = await _store
            .CreateCheckpointAsync(sessionId, value, parent: null!)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Plan {PlanId} checkpoint saved (kind={Kind}, round={Round}, checkpointId={CheckpointId}).",
            state.PlanId, state.Kind, state.RoundNumber, info.CheckpointId);

        return info;
    }

    /// <summary>
    /// Load the most recent checkpoint for a plan. Returns null when no
    /// checkpoint exists (e.g., review is disabled, or the store lost the
    /// data). The resume path treats null as a hard signal to terminate the
    /// plan honestly instead of guessing at state.
    /// </summary>
    public async ValueTask<PlanReviewCheckpointState?> LoadLatestAsync(
        string planId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ct.ThrowIfCancellationRequested();

        string sessionId = SessionIdFor(planId);
        CheckpointInfo? latest = await _manager
            .GetLatestCheckpointAsync(sessionId, ct)
            .ConfigureAwait(false);
        if (latest is null)
        {
            _logger.LogDebug(
                "Plan {PlanId} checkpoint miss — no framework checkpoint present at session {SessionId}.",
                planId, sessionId);
            return null;
        }

        JsonElement value = await _store
            .RetrieveCheckpointAsync(sessionId, latest)
            .ConfigureAwait(false);

        PlanReviewCheckpointState? state =
            JsonSerializer.Deserialize<PlanReviewCheckpointState>(value.GetRawText(), _jsonOptions);
        if (state is null)
        {
            _logger.LogWarning(
                "Plan {PlanId} checkpoint at {SessionId} deserialised to null — treating as absent.",
                planId, sessionId);
        }
        return state;
    }
}
