using System.Text.RegularExpressions;
using FluentAssertions;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Rejection finding #6 — streaming parity. Both <c>/api/chat</c> and
/// <c>/api/chat/stream</c> must call the shared
/// <c>GuardrailsMiddleware.CheckInputAsync</c> / <c>FilterOutputAsync</c>
/// seam so the four Content Safety paths behave identically in both modes.
///
/// Full end-to-end integration coverage of ChatEndpoints would require the
/// test host to boot with a real Agents pipeline (out of scope — the
/// <c>src/RetailPulse.Api/Agents</c> tree is locked under issue #89).
/// Instead this test is a static assertion against the endpoint file so
/// any future edit that removes a guardrail call site fails the build.
/// </summary>
public class ContentSafetyStreamingParityTests
{
    [Fact]
    public void BothChatEndpoints_InvokeGuardrailsMiddleware()
    {
        string path = LocateChatEndpointsFile();
        string source = File.ReadAllText(path);

        MatchCollection postMatches = Regex.Matches(source,
            @"MapPost\(""(?<route>/api/chat(?:/stream)?)""",
            RegexOptions.Multiline);

        postMatches.Should().HaveCount(2, "chat and streaming chat are both registered");
        HashSet<string> routes = postMatches.Select(m => m.Groups["route"].Value).ToHashSet();
        routes.Should().BeEquivalentTo(new[] { "/api/chat", "/api/chat/stream" });

        int checkInputCalls = Regex.Matches(source, @"\.CheckInputAsync\(").Count;
        int filterOutputCalls = Regex.Matches(source, @"\.FilterOutputAsync\(").Count;

        checkInputCalls.Should().BeGreaterThanOrEqualTo(2,
            "each endpoint (chat, stream) must call CheckInputAsync on the shared middleware");
        filterOutputCalls.Should().BeGreaterThanOrEqualTo(2,
            "each endpoint (chat, stream) must call FilterOutputAsync on the shared middleware");
    }

    private static string LocateChatEndpointsFile()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("repository root with a 'src' folder must be reachable from test binaries");
        string path = Path.Combine(dir!.FullName, "src", "RetailPulse.Api", "Endpoints", "ChatEndpoints.cs");
        File.Exists(path).Should().BeTrue($"expected ChatEndpoints.cs at {path}");
        return path;
    }
}
