using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders.Testing;
using Moq;
using RetailPulse.Api.Auth;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Tests for DevelopmentAuthHandler — the auto-succeed authentication handler
/// used in Development environments.
/// </summary>
public class DevelopmentAuthHandlerTests
{
    private static async Task<DevelopmentAuthHandler> CreateAndInitializeHandler()
    {
        var options = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Setup(o => o.Get(DevelopmentAuthHandler.SchemeName))
            .Returns(new AuthenticationSchemeOptions());

        var loggerFactory = Mock.Of<ILoggerFactory>(
            f => f.CreateLogger(It.IsAny<string>()) == Mock.Of<ILogger>());

        var handler = new DevelopmentAuthHandler(
            options.Object,
            loggerFactory,
            new UrlTestEncoder());

        var scheme = new AuthenticationScheme(
            DevelopmentAuthHandler.SchemeName,
            DevelopmentAuthHandler.SchemeName,
            typeof(DevelopmentAuthHandler));

        var context = new DefaultHttpContext();
        await handler.InitializeAsync(scheme, context);

        return handler;
    }

    [Fact]
    public async Task SchemeName_IsDevelopment()
    {
        DevelopmentAuthHandler.SchemeName.Should().Be("Development");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Authenticate_AlwaysSucceeds()
    {
        var handler = await CreateAndInitializeHandler();

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        result.Failure.Should().BeNull();
    }

    [Fact]
    public async Task Authenticate_ProducesSyntheticIdentity()
    {
        var handler = await CreateAndInitializeHandler();

        var result = await handler.AuthenticateAsync();

        result.Principal.Should().NotBeNull();
        result.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
        result.Principal.Identity.AuthenticationType.Should().Be("Development");
    }

    [Fact]
    public async Task Authenticate_ContainsDevUserClaims()
    {
        var handler = await CreateAndInitializeHandler();

        var result = await handler.AuthenticateAsync();

        var claims = result.Principal!.Claims.ToList();
        claims.Should().Contain(c => c.Type == ClaimTypes.NameIdentifier && c.Value == "dev-user");
        claims.Should().Contain(c => c.Type == ClaimTypes.Name && c.Value == "Development User");
        claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "Admin");
        claims.Should().Contain(c => c.Type == "oid");
    }

    [Fact]
    public async Task Authenticate_TicketHasCorrectScheme()
    {
        var handler = await CreateAndInitializeHandler();

        var result = await handler.AuthenticateAsync();

        result.Ticket.Should().NotBeNull();
        result.Ticket.AuthenticationScheme.Should().Be(DevelopmentAuthHandler.SchemeName);
    }
}
