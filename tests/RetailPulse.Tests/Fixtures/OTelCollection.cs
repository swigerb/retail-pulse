namespace RetailPulse.Tests.Fixtures;

/// <summary>
/// Serialization boundary for any test that either installs a process-wide
/// <see cref="System.Diagnostics.ActivityListener"/> against the
/// <c>RetailPulse.Agent</c> <see cref="System.Diagnostics.ActivitySource"/>
/// (e.g., <c>OTelRoutingSpanTests</c>) or emits activities on that source with
/// tag values that could satisfy another OTel test's filter
/// (e.g., <c>MafPrimitivesCharacterizationTests</c> exercising the real
/// <see cref="RetailPulse.Api.Agents.Routing.RetailOpsRouter"/> or
/// <see cref="RetailPulse.Api.Agents.AgentExecutionPipeline"/>).
/// <para>
/// The <see cref="System.Diagnostics.ActivityListener"/> API is process-wide:
/// once a listener is registered it captures every matching
/// <see cref="System.Diagnostics.Activity"/> from every thread until it is
/// disposed. That means a listener installed by one xUnit test class can
/// receive activities emitted by a different test class running in parallel,
/// contaminating <c>LastOrDefault</c>-style span assertions that assume the
/// captured list only contains the current test's activities.
/// </para>
/// <para>
/// Joining the <c>OTel</c> collection prevents that: xUnit runs every test in
/// this collection strictly serially, and <see cref="DisableParallelization"/>
/// also blocks the collection from running in parallel with any other test
/// collection, so no unrelated test can emit RetailPulse.Agent activities into
/// the OTel window.
/// </para>
/// </summary>
[CollectionDefinition("OTel", DisableParallelization = true)]
public sealed class OTelCollection
{
}
