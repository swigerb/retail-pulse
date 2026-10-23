namespace RetailPulse.Tests.Fixtures;

/// <summary>
/// The real API child and the in-process HTTPS dependency must both be scheduled
/// within the unchanged production safety deadlines. Run this collection apart
/// from other collections so full-suite load cannot starve the loopback server.
/// This isolates test scheduling; it does not guarantee availability under host-wide load.
/// </summary>
[CollectionDefinition("ContentSafetyStartup", DisableParallelization = true)]
public sealed class ContentSafetyStartupCollection
{
}
