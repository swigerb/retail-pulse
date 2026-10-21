using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Endpoints;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.OpenAI.Gateway;

namespace RetailPulse.Tests.OpenAI;

public sealed partial class RegulatedRuntimeBehaviorTests
{
    private const string FullId = "11111111-2222-3333-4444-555555555555";

    private static IConfiguration Config(string? enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [RegulatedRetailProfile.ConfigKey] = enabled }).Build();

    private static bool IsKestrelSafe(string v) => v.All(c => c is >= ' ' and <= '~');

    [Theory]
    [InlineData("héllo-wörld-ünïcode-ID-1234")]
    [InlineData("日本語日本語日本語日本語日本語")]
    [InlineData("abc\u0001\u0002def\u007fghijkl")]
    [InlineData("short")]
    [InlineData(FullId)]
    public void Masker_OutputIsAsciiOnly_AndHidesMiddle(string input)
    {
        string masked = ClientIdentifierMasker.Mask(input);
        IsKestrelSafe(masked).Should().BeTrue();
        masked.Should().NotBe(input);
    }

    [Fact]
    public async Task Pipeline_Regulated_MasksHeaderAndErrorBody_KeepsFullIdServerSide()
    {
        var ctx = new DefaultHttpContext { TraceIdentifier = FullId };
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Headers["X-Correlation-ID"] = FullId;
        IConfiguration config = Config("true");

        RequestDelegate pipeline = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("boom"),
            NullLogger<ExceptionHandlingMiddleware>.Instance, config).InvokeAsync;
        await new CorrelationIdMiddleware(pipeline, NullLogger<CorrelationIdMiddleware>.Instance, config).InvokeAsync(ctx);

        string header = ctx.Response.Headers["X-Correlation-ID"]!;
        IsKestrelSafe(header).Should().BeTrue();
        header.Should().NotBe(FullId);
        ctx.Items["CorrelationId"].Should().Be(FullId);

        ctx.Response.Body.Position = 0;
        string body = new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEnd();
        body.Should().NotContain(FullId);
        JsonDocument.Parse(body).RootElement.GetProperty("correlationId").GetString()
            .Should().Be(ClientIdentifierMasker.Mask(FullId));
    }

    [Fact]
    public async Task Pipeline_NormalMode_KeepsFullIds()
    {
        var ctx = new DefaultHttpContext { TraceIdentifier = FullId };
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Headers["X-Correlation-ID"] = FullId;
        IConfiguration config = Config(null);

        RequestDelegate pipeline = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("boom"),
            NullLogger<ExceptionHandlingMiddleware>.Instance, config).InvokeAsync;
        await new CorrelationIdMiddleware(pipeline, NullLogger<CorrelationIdMiddleware>.Instance, config).InvokeAsync(ctx);

        ctx.Response.Headers["X-Correlation-ID"].ToString().Should().Be(FullId);
        ctx.Response.Body.Position = 0;
        new StreamReader(ctx.Response.Body).ReadToEnd().Should().Contain(FullId);
    }

    [Theory]
    [InlineData("What is the pharmacy refill count?")]
    [InlineData("Show recall status for lot 42")]
    [InlineData("Patient identity lookup")]
    [InlineData("clinical health summary")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("update the price of SKU 1")]
    [InlineData("What are top sellers by region?")]
    public void ChatEndpoint_CacheBypass_AppliesToAllInputs_WhenRegulated_AndNeverInNormalMode(string message)
    {
        ChatEndpoints.ShouldBypassSensitiveCache(HttpFor("true"), message).Should().BeTrue();
        ChatEndpoints.ShouldBypassSensitiveCache(HttpFor(null), message).Should().BeFalse();
    }

    [Fact]
    public void ChatEndpoint_NoConfiguration_IsNormalMode() =>
        ChatEndpoints.ShouldBypassSensitiveCache(new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() }, "x")
            .Should().BeFalse();

    [Fact]
    public void ChatEndpoint_GatesBothCacheLookupAndStore()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "src", "RetailPulse.Api", "Endpoints", "ChatEndpoints.cs")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new FileNotFoundException("ChatEndpoints.cs");
        }

        string src = File.ReadAllText(Path.Combine(dir, "src", "RetailPulse.Api", "Endpoints", "ChatEndpoints.cs"));
        MyRegex().Count(src).Should().Be(2);
        src.IndexOf("!sensitiveBypass", StringComparison.Ordinal).Should().BeLessThan(src.IndexOf("responseCache.GetAsync", StringComparison.Ordinal));
        src.LastIndexOf("!sensitiveBypass", StringComparison.Ordinal).Should().BeLessThan(src.IndexOf("responseCache.SetAsync", StringComparison.Ordinal));
    }

    private static HttpContext HttpFor(string? flag) => new DefaultHttpContext
    {
        RequestServices = new ServiceCollection().AddSingleton(Config(flag)).BuildServiceProvider()
    };
    [System.Text.RegularExpressions.GeneratedRegex(@"!sensitiveBypass")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
