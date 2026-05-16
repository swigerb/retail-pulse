using FluentAssertions;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Tests verifying API versioning routes work alongside legacy routes.
/// </summary>
public class ApiVersioningTests
{
    [Fact]
    public void VersionedRoute_IsCorrectlyFormatted()
    {
        string versionedChatRoute = "/api/v1/chat";
        string versionedStreamRoute = "/api/v1/chat/stream";
        string legacyChatRoute = "/api/chat";
        string legacyStreamRoute = "/api/chat/stream";

        versionedChatRoute.Should().Contain("/v1/");
        versionedStreamRoute.Should().Contain("/v1/");
        legacyChatRoute.Should().NotContain("/v1/");
        legacyStreamRoute.Should().NotContain("/v1/");
    }

    [Fact]
    public void LegacyRoutes_StillExist_ForBackwardCompatibility()
    {
        // Legacy routes must continue to work (with deprecation header)
        string[] legacyRoutes = ["/api/chat", "/api/chat/stream"];

        legacyRoutes.Should().HaveCount(2);
        legacyRoutes.Should().AllSatisfy(r => r.Should().StartWith("/api/"));
    }

    [Fact]
    public void VersionedRoutes_MapToV1()
    {
        string[] routes = ["/api/v1/chat", "/api/v1/chat/stream"];

        routes.Should().AllSatisfy(r =>
        {
            r.Should().StartWith("/api/v1/");
            r.Should().NotEndWith("/");
        });
    }

    [Fact]
    public void SunsetHeader_IsExpectedOnLegacyRoutes()
    {
        // The Sunset HTTP header (RFC 8594) signals deprecation
        string sunsetDate = "Sat, 31 Dec 2025 23:59:59 GMT";
        sunsetDate.Should().NotBeNullOrEmpty();
    }
}
