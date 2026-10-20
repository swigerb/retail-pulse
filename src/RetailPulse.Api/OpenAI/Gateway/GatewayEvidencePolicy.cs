using System.ClientModel.Primitives;

namespace RetailPulse.Api.OpenAI.Gateway;

/// <summary>
/// Pipeline policy on the shared Azure OpenAI client options. Reads only allow-listed
/// <c>x-rp-gw-*</c> response headers; it never touches request/response bodies, so
/// prompts and completions cannot leak into evidence. Failures to parse never fail the call.
/// </summary>
public sealed class GatewayEvidencePolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        Capture(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        Capture(message);
    }

    private static void Capture(PipelineMessage message)
    {
        try
        {
            if (message.Response is { } response)
            {
                GatewayEvidenceCollector.Record(GatewayEvidenceHeaders.Parse(response));
            }
        }
        catch (Exception)
        {
            // Evidence is best-effort and must never affect the LLM call.
        }
    }
}
