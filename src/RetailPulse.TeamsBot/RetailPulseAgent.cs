using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using RetailPulse.Contracts;
using RetailPulse.TeamsBot.Cards;
using RetailPulse.TeamsBot.Services;
using RetailPulse.TeamsBot.Auth;
using System.Text.Json;

namespace RetailPulse.TeamsBot;

public class RetailPulseAgent : AgentApplication
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelemetrySignalRClient _telemetryClient;
    private readonly SessionManager _sessionManager;
    private readonly AdaptiveCardBuilder _cardBuilder;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RetailPulseAgent> _logger;
    private readonly int _telemetryWaitMs;

    public RetailPulseAgent(
        AgentApplicationOptions options,
        IHttpClientFactory httpClientFactory,
        TelemetrySignalRClient telemetryClient,
        SessionManager sessionManager,
        AdaptiveCardBuilder cardBuilder,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<RetailPulseAgent> logger) : base(options)
    {
        _httpClientFactory = httpClientFactory;
        _telemetryClient = telemetryClient;
        _sessionManager = sessionManager;
        _cardBuilder = cardBuilder;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _telemetryWaitMs = Math.Max(0, configuration.GetValue<int?>("TeamsBot:TelemetryWaitMs") ?? 500);

        // Register route handlers
        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, HandleMembersAddedAsync);
        OnActivity(ActivityTypes.Message, HandleMessageAsync);
    }

    private async Task HandleMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        var activity = turnContext.Activity;
        var userMessage = activity.Text?.Trim() ?? string.Empty;
        var conversationId = activity.Conversation?.Id ?? Guid.NewGuid().ToString();

        _logger.LogInformation("User message from {ConversationId}: {Message}", conversationId, userMessage);

        // Check if this is an Action.Submit from a card
        if (activity.Value != null)
        {
            await HandleCardActionAsync(turnContext, conversationId, cancellationToken);
            return;
        }

        // Extract user identity via SSO
        UserContext? userContext = null;
        using (var scope = _serviceProvider.CreateScope())
        {
            var ssoHandler = scope.ServiceProvider.GetRequiredService<TeamsSsoHandler>();
            var userIdentity = await ssoHandler.ExtractUserIdentityAsync(activity);

            if (userIdentity != null)
            {
                _logger.LogInformation("Authenticated user: {DisplayName} ({Email})",
                    userIdentity.DisplayName, userIdentity.Email);
                userContext = new UserContext(userIdentity.ObjectId, userIdentity.DisplayName, userIdentity.Email);
            }
            else
            {
                userContext = new UserContext(
                    activity.From?.Id ?? "unknown",
                    activity.From?.Name ?? "Unknown User",
                    string.Empty
                );
                _logger.LogWarning("SSO not available, using fallback user context");
            }
        }

        // Handle "new chat" command
        if (userMessage.Equals("new chat", StringComparison.OrdinalIgnoreCase) ||
            userMessage.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            _sessionManager.ClearSession(conversationId);
            var welcomeCard = _cardBuilder.BuildWelcomeCard(isReset: true, userContext);
            await turnContext.SendActivityAsync(MessageFactory.Attachment(welcomeCard), cancellationToken);
            return;
        }

        // Get or create session for this conversation
        var sessionId = _sessionManager.GetOrCreateSessionId(conversationId);

        await _telemetryClient.StartCollectingAsync(sessionId, cancellationToken);

        try
        {
            var client = _httpClientFactory.CreateClient("RetailPulseApi");
            var chatRequest = new ChatRequest(userMessage, sessionId, userContext);
            var response = await client.PostAsJsonAsync("/api/chat", chatRequest, cancellationToken);
            response.EnsureSuccessStatusCode();
            var chatResponse = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken);

            if (chatResponse == null)
                throw new InvalidOperationException("Received null response from API");

            await _telemetryClient.WaitForSpansAsync(_telemetryWaitMs, cancellationToken);
            var signalRSpans = _telemetryClient.GetSpans(sessionId, clearAfterRead: true);
            var allSpans = signalRSpans.Any() ? signalRSpans : chatResponse.Spans;
            _sessionManager.StoreSpans(sessionId, allSpans);

            var card = _cardBuilder.BuildChatResponseCard(chatResponse.Reply, allSpans, chatResponse.Charts, sessionId);
            await turnContext.SendActivityAsync(MessageFactory.Attachment(card), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message for session {SessionId}", sessionId);
            // Don't surface raw exception text to end users — could leak internal
            // endpoints, prompts, or stack traces. Show a generic message.
            var errorCard = _cardBuilder.BuildErrorCard("Something went wrong while processing your request. Please try again in a moment.");
            await turnContext.SendActivityAsync(MessageFactory.Attachment(errorCard), cancellationToken);
        }
    }

    private async Task HandleCardActionAsync(ITurnContext turnContext, string conversationId, CancellationToken cancellationToken)
    {
        try
        {
            var actionData = JsonSerializer.Deserialize<Dictionary<string, object>>(
                turnContext.Activity.Value?.ToString() ?? "{}",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (actionData == null || !actionData.TryGetValue("action", out var actionObj))
            {
                _logger.LogWarning("Card action missing 'action' field");
                return;
            }

            var actionType = actionObj?.ToString();
            _logger.LogInformation("Handling card action: {ActionType}", actionType);

            switch (actionType)
            {
                case "detailed_telemetry":
                    if (actionData.TryGetValue("sessionId", out var sessionIdObj))
                    {
                        var sessionId = sessionIdObj?.ToString();
                        if (!string.IsNullOrEmpty(sessionId))
                        {
                            var spans = _sessionManager.GetSpans(sessionId);
                            if (spans != null && spans.Any())
                            {
                                var detailedCard = _cardBuilder.BuildDetailedTelemetryCard(spans);
                                await turnContext.SendActivityAsync(MessageFactory.Attachment(detailedCard), cancellationToken);
                            }
                            else
                            {
                                var errorCard = _cardBuilder.BuildErrorCard("Telemetry data not found for this session.");
                                await turnContext.SendActivityAsync(MessageFactory.Attachment(errorCard), cancellationToken);
                            }
                        }
                    }
                    break;

                case "retry":
                    // Retry is no longer offered as a card action — error cards now
                    // ask users to retype their question instead. Kept here as a
                    // no-op so old cards still in conversation history don't error.
                    break;

                default:
                    _logger.LogWarning("Unknown card action: {ActionType}", actionType);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling card action");
            var errorCard = _cardBuilder.BuildErrorCard("Failed to process action.");
            await turnContext.SendActivityAsync(MessageFactory.Attachment(errorCard), cancellationToken);
        }
    }

    private async Task HandleMembersAddedAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        var activity = turnContext.Activity;
        var membersAdded = activity.MembersAdded;

        if (membersAdded != null)
        {
            foreach (var member in membersAdded)
            {
                if (member.Id != activity.Recipient?.Id)
                {
                    var userContext = new UserContext(
                        member.Id ?? "unknown",
                        member.Name ?? "Unknown User",
                        string.Empty
                    );
                    var welcomeCard = _cardBuilder.BuildWelcomeCard(isReset: false, userContext);
                    await turnContext.SendActivityAsync(MessageFactory.Attachment(welcomeCard), cancellationToken);
                    return;
                }
            }
        }
    }
}
