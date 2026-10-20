using System.ClientModel.Primitives;
using System.Net.Http;
using RetailPulse.Contracts.Gateway;

namespace RetailPulse.Api.OpenAI.Gateway;

/// <summary>
/// Pipeline policy on the shared Azure OpenAI client options. Reads only allow-listed
/// <c>x-rp-gw-*</c> response headers, and only when the client is configured for an APIM
/// gateway; it never touches request/response bodies, so prompts and completions cannot leak
/// into evidence. Transport failures are recorded as a fixed error class (never exception text)
/// and rethrown unchanged. Capture problems never fail the call.
/// </summary>
public sealed class GatewayEvidencePolicy(GatewayMode mode) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        try
        {
            ProcessNext(message, pipeline, currentIndex);
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
            throw;
        }

        Capture(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        try
        {
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
            throw;
        }

        Capture(message);
    }

    internal static string ClassifyTransportError(Exception ex) => (ex.InnerException is { } inner && ex is not (TimeoutException or OperationCanceledException) ? inner : ex) switch
    {
        TimeoutException => "timeout",
        OperationCanceledException => "canceled",
        HttpRequestException => "network",
        System.Net.Sockets.SocketException => "network",
        IOException => "network",
        _ => "other",
    };

    private static void RecordFailure(Exception ex) =>
        GatewayEvidenceCollector.Record(
            GatewayCallEvidence.Empty(GatewayEvidenceStatus.Failed, transportError: ClassifyTransportError(ex)));

    private void Capture(PipelineMessage message)
    {
        try
        {
            if (message.Response is not { } response)
            {
                GatewayEvidenceCollector.Record(GatewayCallEvidence.Empty(
                    mode == GatewayMode.Apim ? GatewayEvidenceStatus.Unavailable : GatewayEvidenceStatus.Bypassed));
                return;
            }

            GatewayEvidenceCollector.Record(mode == GatewayMode.Apim
                ? GatewayEvidenceHeaders.Parse(response)
                : GatewayCallEvidence.Empty(GatewayEvidenceStatus.Bypassed, response.Status));
        }
        catch (Exception)
        {
            // Evidence is best-effort and must never affect the LLM call.
        }
    }
}
