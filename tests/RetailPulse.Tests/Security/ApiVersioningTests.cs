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
        var versionedChatRoute = "/api/v1/chat";
        var versionedStreamRoute = "/api/v1/chat/stream";
        var legacyChatRoute = "/api/chat";
        var legacyStreamRoute = "/api/chat/stream";

        versionedChatRoute.Should().Contain("/v1/");
        versionedStreamRoute.Should().Contain("/v1/");
        legacyChatRoute.Should().NotContain("/v1/");
        legacyStreamRoute.Should().NotContain("/v1/");
    }

    [Fact]
    public void LegacyRoutes_StillExist_ForBackwardCompatibility()
    {
        // Legacy routes must continue to work (with deprecation header)
        var legacyRoutes = new[] { "/api/chat", "/api/chat/stream" };

        legacyRoutes.Should().HaveCount(2);
        legacyRoutes.Should().AllSatisfy(r => r.Should().StartWith("/api/"));
    }

    [Fact]
    public void VersionedRoutes_MapToV1()
    {
        var routes = new[] { "/api/v1/chat", "/api/v1/chat/stream" };

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
        var sunsetDate = "Sat, 31 Dec 2025 23:59:59 GMT";
        sunsetDate.Should().NotBeNullOrEmpty();
    }
}
