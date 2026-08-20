using System.Net;
using System.Net.Http.Json;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Programmable <see cref="HttpMessageHandler"/> used by the Prompt Shields
/// and resilience integration tests. Records every request it sees and returns
/// canned responses (or delays, or exceptions) supplied by the test.
/// </summary>
internal sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Responder { get; set; }

    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        Requests.Add(await CloneAsync(request, cancellationToken).ConfigureAwait(false));
        if (Responder is null)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    userPromptAnalysis = new { attackDetected = false },
                    documentsAnalysis = Array.Empty<object>()
                })
            };
        }
        return await Responder(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Convenience helper for tests that want to inspect the captured Authorization header.</summary>
    public string? LastAuthorizationHeader =>
        Requests.LastOrDefault()?.Headers.Authorization?.ToString();

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage source, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (KeyValuePair<string, IEnumerable<string>> header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (source.Content is not null)
        {
            string body = await source.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            clone.Content = new StringContent(body);
            foreach (KeyValuePair<string, IEnumerable<string>> header in source.Content.Headers)
            {
                clone.Content.Headers.Remove(header.Key);
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        return clone;
    }
}
