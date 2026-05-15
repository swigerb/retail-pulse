using System.Text.RegularExpressions;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Validation;

/// <summary>
/// Validates ChatRequest input before the expensive LLM pipeline runs.
/// Returns structured field-level errors for 400 responses.
/// </summary>
public static partial class ChatRequestValidator
{
    public const int MaxMessageLength = 4000;

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

        return new ValidationResult(errors.Count == 0, errors);
    }
}

public record ValidationResult(bool IsValid, Dictionary<string, string[]> Errors);
