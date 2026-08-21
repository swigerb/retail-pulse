using System.Text.RegularExpressions;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Validation;

/// <summary>
/// Validates ChatRequest input before the expensive LLM pipeline runs.
/// Returns structured field-level errors for 400 responses.
/// </summary>
public static partial class ChatRequestValidator
{
    public const int MaxMessageLength = 4000;

    /// <summary>Maximum number of prior conversation turns accepted with a chat request.</summary>
    public const int MaxHistoryMessages = 50;

    /// <summary>Maximum characters allowed in any single history entry's content.</summary>
    public const int MaxHistoryMessageLength = 4000;

    /// <summary>
    /// Maximum combined characters across all history entries. Bounds the total prompt a caller can
    /// assemble from the history array (a rough proxy for token count) independent of the per-entry
    /// and count caps, so an attacker cannot smuggle a huge context past the model.
    /// </summary>
    public const int MaxAggregateHistoryChars = 100_000;

    // SessionId must be alphanumeric/hyphens, 1-64 chars (GUID-like formats)
    [GeneratedRegex(@"^[a-zA-Z0-9\-]{1,64}$")]
    private static partial Regex SessionIdPattern();

    public static ValidationResult Validate(ChatRequest? request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request is null)
        {
            errors["request"] = ["Request body is required."];
            return new ValidationResult(false, errors);
        }

        // Message validation
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            errors["message"] = ["Field 'message' is required and cannot be empty."];
        }
        else if (request.Message.Length > MaxMessageLength)
        {
            errors["message"] = [$"Field 'message' must not exceed {MaxMessageLength} characters. Received: {request.Message.Length}."];
        }

        // SessionId format validation (if provided)
        if (request.SessionId is not null && !SessionIdPattern().IsMatch(request.SessionId))
        {
            errors["sessionId"] = ["Field 'sessionId' must be 1-64 alphanumeric characters or hyphens."];
        }

        // History bounds — count, per-entry size, and aggregate size are all bounded BEFORE the
        // model runs so an oversized or padded history array is rejected with 400, not billed.
        if (request.History is { Count: > 0 } history)
        {
            if (history.Count > MaxHistoryMessages)
            {
                errors["history"] = [$"Field 'history' must not exceed {MaxHistoryMessages} messages. Received: {history.Count}."];
            }

            long aggregate = 0;
            for (int i = 0; i < history.Count; i++)
            {
                int contentLength = history[i]?.Content?.Length ?? 0;
                aggregate += contentLength;

                if (contentLength > MaxHistoryMessageLength)
                {
                    errors[$"history[{i}]"] = [$"History message content must not exceed {MaxHistoryMessageLength} characters. Received: {contentLength}."];
                }
            }

            if (aggregate > MaxAggregateHistoryChars)
            {
                errors["history.aggregate"] = [$"Combined history content must not exceed {MaxAggregateHistoryChars} characters. Received: {aggregate}."];
            }
        }

        // Force-execution-path override (issue #95). Optional. Only user-forceable
        // values are accepted; council is a router-controlled destination and is
        // never a valid override. Unknown values fail closed with a 400 so a
        // silent typo can never quietly re-route through the fast path.
        if (request.ForceExecutionPath is not null
            && !ExecutionPath.IsForceable(request.ForceExecutionPath))
        {
            errors["forceExecutionPath"] =
            [
                $"Field 'forceExecutionPath' must be '{ExecutionPath.Fast}' or '{ExecutionPath.Plan}' when specified."
            ];
        }

        return new ValidationResult(errors.Count == 0, errors);
    }
}

public record ValidationResult(bool IsValid, Dictionary<string, string[]> Errors);
