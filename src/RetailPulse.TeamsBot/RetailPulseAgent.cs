using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using RetailPulse.Contracts;
using RetailPulse.TeamsBot.Auth;
using RetailPulse.TeamsBot.Cards;
using RetailPulse.TeamsBot.Services;

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
        IActivity activity = turnContext.Activity;
        string userMessage = activity.Text?.Trim() ?? string.Empty;
        string conversationId = activity.Conversation?.Id ?? Guid.NewGuid().ToString();

        // Check if this is an Action.Submit from a card (before input validation)
        if (activity.Value != null)
        {
            await HandleCardActionAsync(turnContext, conversationId, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(userMessage) || userMessage.Length > 4096)
        {
            await turnContext.SendActivityAsync("Please provide a message (max 4096 characters).", cancellationToken: cancellationToken);
            return;
        }

        _logger.LogInformation("User message from {ConversationId}: {Message}", conversationId, userMessage);

        // Extract user identity via SSO
        UserContext? userContext = null;
        using (IServiceScope scope = _serviceProvider.CreateScope())
        {
            TeamsSsoHandler ssoHandler = scope.ServiceProvider.GetRequiredService<TeamsSsoHandler>();
            UserIdentity? userIdentity = await ssoHandler.ExtractUserIdentityAsync(activity);

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
            Attachment welcomeCard = _cardBuilder.BuildWelcomeCard(isReset: true, userContext);
            await turnContext.SendActivityAsync(MessageFactory.Attachment(welcomeCard), cancellationToken);
            return;
        }

        // Get or create session for this conversation
        string sessionId = _sessionManager.GetOrCreateSessionId(conversationId);

        await _telemetryClient.StartCollectingAsync(sessionId, cancellationToken);

        try
        {
            HttpClient client = _httpClientFactory.CreateClient("RetailPulseApi");
            var chatRequest = new ChatRequest(userMessage, sessionId, userContext);
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/chat", chatRequest, cancellationToken);
            response.EnsureSuccessStatusCode();
            ChatResponse chatResponse = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken) ?? throw new InvalidOperationException("Received null response from API");
            await _telemetryClient.WaitForSpansAsync(_telemetryWaitMs, cancellationToken);
            List<AgentSpan> signalRSpans = _telemetryClient.GetSpans(sessionId, clearAfterRead: true);
            List<AgentSpan> allSpans = signalRSpans.Count != 0 ? signalRSpans : chatResponse.Spans;
            _sessionManager.StoreSpans(sessionId, allSpans);

            Attachment card = _cardBuilder.BuildChatResponseCard(chatResponse.Reply, allSpans, chatResponse.Charts, sessionId);
            await turnContext.SendActivityAsync(MessageFactory.Attachment(card), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message for session {SessionId}", sessionId);
            // Don't surface raw exception text to end users — could leak internal
            // endpoints, prompts, or stack traces. Show a generic message.
            Attachment errorCard = _cardBuilder.BuildErrorCard("Something went wrong while processing your request. Please try again in a moment.");
            await turnContext.SendActivityAsync(MessageFactory.Attachment(errorCard), cancellationToken);
        }
    }

    private async Task HandleCardActionAsync(ITurnContext turnContext, string conversationId, CancellationToken cancellationToken)
    {
        try
        {
            Dictionary<string, object>? actionData = JsonSerializer.Deserialize<Dictionary<string, object>>(
                turnContext.Activity.Value?.ToString() ?? "{}",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (actionData == null || !actionData.TryGetValue("action", out object? actionObj))
            {
                _logger.LogWarning("Card action missing 'action' field");
                return;
            }

            string? actionType = actionObj?.ToString();
            _logger.LogInformation("Handling card action: {ActionType}", actionType);

            switch (actionType)
            {
                case "detailed_telemetry":
                    if (actionData.TryGetValue("sessionId", out object? sessionIdObj))
                    {
                        string? sessionId = sessionIdObj?.ToString();
                        if (!string.IsNullOrEmpty(sessionId))
                        {
                            List<AgentSpan>? spans = _sessionManager.GetSpans(sessionId);
                            if (spans != null && spans.Count != 0)
                            {
                                Attachment detailedCard = _cardBuilder.BuildDetailedTelemetryCard(spans);
                                await turnContext.SendActivityAsync(MessageFactory.Attachment(detailedCard), cancellationToken);
                            }
                            else
                            {
                                Attachment errorCard = _cardBuilder.BuildErrorCard("Telemetry data not found for this session.");
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
            Attachment errorCard = _cardBuilder.BuildErrorCard("Failed to process action.");
            await turnContext.SendActivityAsync(MessageFactory.Attachment(errorCard), cancellationToken);
        }
    }

    private async Task HandleMembersAddedAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        IActivity activity = turnContext.Activity;
        IList<ChannelAccount> membersAdded = activity.MembersAdded;

        if (membersAdded != null)
        {
            foreach (ChannelAccount member in membersAdded)
            {
                if (member.Id != activity.Recipient?.Id)
                {
                    var userContext = new UserContext(
                        member.Id ?? "unknown",
                        member.Name ?? "Unknown User",
                        string.Empty
                    );
                    Attachment welcomeCard = _cardBuilder.BuildWelcomeCard(isReset: false, userContext);
                    await turnContext.SendActivityAsync(MessageFactory.Attachment(welcomeCard), cancellationToken);
                    return;
                }
            }
        }
    }
}
