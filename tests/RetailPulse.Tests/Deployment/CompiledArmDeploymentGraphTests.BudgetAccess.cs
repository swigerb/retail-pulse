using System.Text.Json;

namespace RetailPulse.Tests.Deployment;

public sealed partial class CompiledArmDeploymentGraphTests
{
    internal JsonElement? CompileForBudgetTests() => TryCompileMainBicep();
}
