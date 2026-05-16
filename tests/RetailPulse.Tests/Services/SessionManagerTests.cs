using FluentAssertions;
using RetailPulse.Contracts;
using RetailPulse.TeamsBot.Services;

namespace RetailPulse.Tests.Services;

public class SessionManagerTests
{
    [Fact]
    public void GetOrCreateSessionId_NewConversation_CreatesNewSessionId()
    {
        var sessionManager = new SessionManager();
        string conversationId = "test-conversation-1";

        string sessionId = sessionManager.GetOrCreateSessionId(conversationId);

        sessionId.Should().NotBeNullOrEmpty();
        Guid.TryParse(sessionId, out _).Should().BeTrue("Session ID should be a valid GUID");
    }

    [Fact]
    public void GetOrCreateSessionId_SameConversation_ReturnsSameSessionId()
    {
        var sessionManager = new SessionManager();
        string conversationId = "test-conversation-2";

        string sessionId1 = sessionManager.GetOrCreateSessionId(conversationId);
        string sessionId2 = sessionManager.GetOrCreateSessionId(conversationId);

        sessionId1.Should().Be(sessionId2);
    }

    [Fact]
    public void GetOrCreateSessionId_DifferentConversations_ReturnsDifferentSessionIds()
    {
        var sessionManager = new SessionManager();
        string conversationId1 = "test-conversation-3";
        string conversationId2 = "test-conversation-4";

        string sessionId1 = sessionManager.GetOrCreateSessionId(conversationId1);
        string sessionId2 = sessionManager.GetOrCreateSessionId(conversationId2);

        sessionId1.Should().NotBe(sessionId2);
    }

    [Fact]
    public void StoreSpans_AndGetSpans_ReturnsStoredSpans()
    {
        var sessionManager = new SessionManager();
        string sessionId = "test-session-1";
        var spans = new List<AgentSpan>
        {
            new("Test Span 1", "thought", "Testing", 100, DateTimeOffset.UtcNow),
            new("Test Span 2", "tool_call", "Tool execution", 200, DateTimeOffset.UtcNow)
        };

        sessionManager.StoreSpans(sessionId, spans);
        List<AgentSpan>? retrievedSpans = sessionManager.GetSpans(sessionId);

        retrievedSpans.Should().NotBeNull();
        retrievedSpans.Should().HaveCount(2);
        retrievedSpans[0].Name.Should().Be("Test Span 1");
        retrievedSpans[1].Name.Should().Be("Test Span 2");
    }

    [Fact]
    public void GetSpans_NonExistentSession_ReturnsNull()
    {
        var sessionManager = new SessionManager();
        string sessionId = "non-existent-session";

        List<AgentSpan>? retrievedSpans = sessionManager.GetSpans(sessionId);

        retrievedSpans.Should().BeNull();
    }

    [Fact]
    public void StoreSpans_OverwritesExistingSpans()
    {
        var sessionManager = new SessionManager();
        string sessionId = "test-session-2";
        var spans1 = new List<AgentSpan>
        {
            new("Original Span", "thought", "Original", 100, DateTimeOffset.UtcNow)
        };
        var spans2 = new List<AgentSpan>
        {
            new("Updated Span", "response", "Updated", 200, DateTimeOffset.UtcNow)
        };

        sessionManager.StoreSpans(sessionId, spans1);
        sessionManager.StoreSpans(sessionId, spans2);
        List<AgentSpan>? retrievedSpans = sessionManager.GetSpans(sessionId);

        retrievedSpans.Should().HaveCount(1);
        retrievedSpans[0].Name.Should().Be("Updated Span");
    }

    [Fact]
    public void ClearSession_RemovesSessionAndSpans()
    {
        var sessionManager = new SessionManager();
        string conversationId = "test-conversation-5";
        string sessionId = sessionManager.GetOrCreateSessionId(conversationId);
        var spans = new List<AgentSpan>
        {
            new("Test Span", "thought", "Testing", 100, DateTimeOffset.UtcNow)
        };
        sessionManager.StoreSpans(sessionId, spans);

        sessionManager.ClearSession(conversationId);
        string newSessionId = sessionManager.GetOrCreateSessionId(conversationId);
        List<AgentSpan>? retrievedSpans = sessionManager.GetSpans(sessionId);

        newSessionId.Should().NotBe(sessionId, "New session should be created after clearing");
        retrievedSpans.Should().BeNull("Spans should be cleared");
    }

    [Fact]
    public void ClearSession_NonExistentConversation_DoesNotThrow()
    {
        var sessionManager = new SessionManager();
        string conversationId = "non-existent-conversation";

        Action act = () => sessionManager.ClearSession(conversationId);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task SessionManager_ConcurrentAccess_HandlesMultipleConversations()
    {
        var sessionManager = new SessionManager();
        var tasks = new List<Task<string>>();

        for (int i = 0; i < 10; i++)
        {
            string conversationId = $"conversation-{i}";
            Task<string> task = Task.Run(() => sessionManager.GetOrCreateSessionId(conversationId));
            tasks.Add(task);
        }

        await Task.WhenAll(tasks.ToArray());
        var sessionIds = tasks.Select(t => t.Result).ToList();

        sessionIds.Should().HaveCount(10);
        sessionIds.Should().OnlyHaveUniqueItems("Each conversation should have a unique session ID");
    }
}
