using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RetailPulse.Api.OpenAI.Gateway;

namespace RetailPulse.Tests.OpenAI;

public sealed class GatewayEvidenceSpoofingTests
{
    [Fact]
    public async Task InboundXRpHeaders_AreStripped_OtherHeadersKept()
    {
        var builder = new ApplicationBuilder(new ServiceProvider0());
        builder.UseStripInboundGatewayHeaders();
        var app = builder.Build();
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-RP-GW-Quota"] = "allowed";
        ctx.Request.Headers["x-rp-anything"] = "1";
        ctx.Request.Headers["x-correlation-id"] = "keep";

        await app(ctx);

        ctx.Request.Headers.Keys.Should().NotContain(k => k.StartsWith("x-rp-", StringComparison.OrdinalIgnoreCase));
        ctx.Request.Headers["x-correlation-id"].ToString().Should().Be("keep");
    }

    private sealed class ServiceProvider0 : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
