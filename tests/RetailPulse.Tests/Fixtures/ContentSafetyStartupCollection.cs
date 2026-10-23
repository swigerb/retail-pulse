namespace RetailPulse.Tests.Fixtures;

/// <summary>
/// Process-backed startup fixtures depend on child processes and loopback servers
/// reaching readiness within unchanged deadlines. Run them apart from other
/// collections to isolate xUnit scheduling; this does not guarantee availability
/// under host-wide load.
/// </summary>
[CollectionDefinition("ContentSafetyStartup", DisableParallelization = true)]
public sealed class ContentSafetyStartupCollection
{
}
