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
        var conversationId = "test-conversation-1";

        var sessionId = sessionManager.GetOrCreateSessionId(conversationId);

        sessionId.Should().NotBeNullOrEmpty();
        Guid.TryParse(sessionId, out _).Should().BeTrue("Session ID should be a valid GUID");
    }

    [Fact]
    public void GetOrCreateSessionId_SameConversation_ReturnsSameSessionId()
    {
        var sessionManager = new SessionManager();
        var conversationId = "test-conversation-2";

        var sessionId1 = sessionManager.GetOrCreateSessionId(conversationId);
        var sessionId2 = sessionManager.GetOrCreateSessionId(conversationId);

        sessionId1.Should().Be(sessionId2);
    }

    [Fact]
    public void GetOrCreateSessionId_DifferentConversations_ReturnsDifferentSessionIds()
    {
        var sessionManager = new SessionManager();
        var conversationId1 = "test-conversation-3";
        var conversationId2 = "test-conversation-4";

        var sessionId1 = sessionManager.GetOrCreateSessionId(conversationId1);
        var sessionId2 = sessionManager.GetOrCreateSessionId(conversationId2);

        sessionId1.Should().NotBe(sessionId2);
    }

    [Fact]
    public void StoreSpans_AndGetSpans_ReturnsStoredSpans()
    {
        var sessionManager = new SessionManager();
        var sessionId = "test-session-1";
        var spans = new List<AgentSpan>
        {
            new AgentSpan("Test Span 1", "thought", "Testing", 100, DateTimeOffset.UtcNow),
            new AgentSpan("Test Span 2", "tool_call", "Tool execution", 200, DateTimeOffset.UtcNow)
        };

        sessionManager.StoreSpans(sessionId, spans);
        var retrievedSpans = sessionManager.GetSpans(sessionId);

        retrievedSpans.Should().NotBeNull();
        retrievedSpans.Should().HaveCount(2);
        retrievedSpans![0].Name.Should().Be("Test Span 1");
        retrievedSpans[1].Name.Should().Be("Test Span 2");
    }

    [Fact]
    public void GetSpans_NonExistentSession_ReturnsNull()
    {
        var sessionManager = new SessionManager();
        var sessionId = "non-existent-session";

        var retrievedSpans = sessionManager.GetSpans(sessionId);

        retrievedSpans.Should().BeNull();
    }

    [Fact]
    public void StoreSpans_OverwritesExistingSpans()
    {
        var sessionManager = new SessionManager();
        var sessionId = "test-session-2";
        var spans1 = new List<AgentSpan>
        {
            new AgentSpan("Original Span", "thought", "Original", 100, DateTimeOffset.UtcNow)
        };
        var spans2 = new List<AgentSpan>
        {
            new AgentSpan("Updated Span", "response", "Updated", 200, DateTimeOffset.UtcNow)
        };

        sessionManager.StoreSpans(sessionId, spans1);
        sessionManager.StoreSpans(sessionId, spans2);
        var retrievedSpans = sessionManager.GetSpans(sessionId);

        retrievedSpans.Should().HaveCount(1);
        retrievedSpans![0].Name.Should().Be("Updated Span");
    }

    [Fact]
    public void ClearSession_RemovesSessionAndSpans()
    {
        var sessionManager = new SessionManager();
        var conversationId = "test-conversation-5";
        var sessionId = sessionManager.GetOrCreateSessionId(conversationId);
        var spans = new List<AgentSpan>
        {
            new AgentSpan("Test Span", "thought", "Testing", 100, DateTimeOffset.UtcNow)
        };
        sessionManager.StoreSpans(sessionId, spans);

        sessionManager.ClearSession(conversationId);
        var newSessionId = sessionManager.GetOrCreateSessionId(conversationId);
        var retrievedSpans = sessionManager.GetSpans(sessionId);

        newSessionId.Should().NotBe(sessionId, "New session should be created after clearing");
        retrievedSpans.Should().BeNull("Spans should be cleared");
    }

    [Fact]
    public void ClearSession_NonExistentConversation_DoesNotThrow()
    {
        var sessionManager = new SessionManager();
        var conversationId = "non-existent-conversation";

        var act = () => sessionManager.ClearSession(conversationId);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task SessionManager_ConcurrentAccess_HandlesMultipleConversations()
    {
        var sessionManager = new SessionManager();
        var tasks = new List<Task<string>>();

        for (int i = 0; i < 10; i++)
        {
            var conversationId = $"conversation-{i}";
            var task = Task.Run(() => sessionManager.GetOrCreateSessionId(conversationId));
            tasks.Add(task);
        }

        await Task.WhenAll(tasks.ToArray());
        var sessionIds = tasks.Select(t => t.Result).ToList();

        sessionIds.Should().HaveCount(10);
        sessionIds.Should().OnlyHaveUniqueItems("Each conversation should have a unique session ID");
    }
}
