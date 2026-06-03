using System.Reflection;
using FluentAssertions;
using RetailPulse.Api.Endpoints;

namespace RetailPulse.Tests.Endpoints;

public class ChatEndpointsTests
{
    [Fact]
    public void CreateAuditEntryId_ReturnsUnique32CharacterIds()
    {
        MethodInfo? method = typeof(ChatEndpoints).GetMethod(
            "CreateAuditEntryId",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull("chat audit entries need a dedicated unique ID generator");

        string first = method.Invoke(null, null).Should().BeOfType<string>().Subject;
        string second = method.Invoke(null, null).Should().BeOfType<string>().Subject;

        first.Should().HaveLength(32);
        second.Should().HaveLength(32);
        first.Should().NotBe(second, "each audit entry must get its own primary key even within one session");
    }
}
