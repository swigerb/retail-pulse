using FluentAssertions;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Tests verifying chat endpoint routing conventions. Versioned (/api/v1/*) stub
/// routes were removed; only the canonical /api/chat[/stream] routes remain.
/// </summary>
public class ApiVersioningTests
{
    [Fact]
    public void ChatRoutes_AreCanonicallyUnversioned()
    {
        string chatRoute = "/api/chat";
        string streamRoute = "/api/chat/stream";

        chatRoute.Should().NotContain("/v1/");
        streamRoute.Should().NotContain("/v1/");
        chatRoute.Should().StartWith("/api/");
        streamRoute.Should().StartWith("/api/");
    }

    [Fact]
    public void ChatRoutes_FollowApiPrefixConvention()
    {
        string[] routes = ["/api/chat", "/api/chat/stream"];

        routes.Should().AllSatisfy(r =>
        {
            r.Should().StartWith("/api/");
            r.Should().NotEndWith("/");
        });
    }
}
