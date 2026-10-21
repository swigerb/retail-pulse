using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RetailPulse.Api.Budget;

/// <summary>
/// Enforces the APIM tool-document contract on the exact message sequence sent to the
/// gateway. The original transcript is left intact so canonical tool results remain
/// available to response processing and chart reconstruction.
/// </summary>
internal sealed class GatewayToolDocumentBudgetChatClient : DelegatingChatClient
{
    internal const int MaxToolDocuments = 5;
    internal const int MaxGatewayAggregateChars = 9_500;

    private static readonly string _budgetNotice = JsonSerializer.Serialize(new
    {
        budget_notice =
            "One or more tool results were withheld from this model turn to stay within the gateway safety limit. "
            + "Use only the results present; do not claim omitted data is complete."
    });

    private readonly ToolResultBudgetOptions _options;
    private readonly ILogger<GatewayToolDocumentBudgetChatClient> _logger;

    public GatewayToolDocumentBudgetChatClient(
        IChatClient innerClient,
        ToolResultBudgetOptions options,
        ILogger<GatewayToolDocumentBudgetChatClient> logger)
        : base(innerClient)
    {
        _options = options;
        _logger = logger;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> bounded = PrepareMessages(messages, out BoundaryMetrics metrics);
        LogIfChanged(metrics);
        return InnerClient.GetResponseAsync(bounded, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> bounded = PrepareMessages(messages, out BoundaryMetrics metrics);
        LogIfChanged(metrics);

        await foreach (ChatResponseUpdate update in InnerClient
            .GetStreamingResponseAsync(bounded, options, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    internal IReadOnlyList<ChatMessage> PrepareMessages(
        IEnumerable<ChatMessage> messages,
        out BoundaryMetrics metrics)
    {
        List<ChatMessage> original = messages is List<ChatMessage> list ? list : [.. messages];
        List<ResultLocation> results = FindResults(original);
        int originalChars = results.Sum(r => r.Text.Length);
        int originalDocuments = results.Count;
        int maxChars = Math.Clamp(
            _options.MaxCumulativeChars,
            _budgetNotice.Length,
            MaxGatewayAggregateChars);

        if (originalDocuments <= MaxToolDocuments && originalChars <= maxChars)
        {
            metrics = new BoundaryMetrics(
                originalDocuments,
                originalDocuments,
                originalChars,
                originalChars,
                OmittedDocuments: 0);
            return original;
        }

        var bounded = original.Select(CloneMessageWithIndependentContents).ToList();
        int substantiveDocumentLimit = MaxToolDocuments - 1;
        int substantiveCharLimit = maxChars - _budgetNotice.Length;
        int returnedDocuments = 0;
        int returnedChars = 0;
        int omittedDocuments = 0;
        bool noticePlaced = false;

        foreach (ResultLocation result in results)
        {
            bool canKeep = returnedDocuments < substantiveDocumentLimit
                && returnedChars + result.Text.Length <= substantiveCharLimit;

            if (canKeep)
            {
                returnedDocuments++;
                returnedChars += result.Text.Length;
                continue;
            }

            omittedDocuments++;
            FunctionResultContent replacement;
            if (!noticePlaced)
            {
                replacement = new FunctionResultContent(result.Content.CallId, _budgetNotice);
                returnedDocuments++;
                returnedChars += _budgetNotice.Length;
                noticePlaced = true;
            }
            else
            {
                // APIM ignores blank tool documents. The first omitted result carries the
                // truthful notice; later protocol-required results remain blank rather than
                // fabricating data or repeating notices until the aggregate limit is exceeded.
                replacement = new FunctionResultContent(result.Content.CallId, "");
            }

            ChatMessage clonedMessage = bounded[result.MessageIndex];
            clonedMessage.RawRepresentation = null;
            clonedMessage.Contents[result.ContentIndex] = replacement;
        }

        metrics = new BoundaryMetrics(
            originalDocuments,
            returnedDocuments,
            originalChars,
            returnedChars,
            omittedDocuments);
        return bounded;
    }

    private static List<ResultLocation> FindResults(IReadOnlyList<ChatMessage> messages)
    {
        var results = new List<ResultLocation>();
        for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            ChatMessage message = messages[messageIndex];
            for (int contentIndex = 0; contentIndex < message.Contents.Count; contentIndex++)
            {
                if (message.Contents[contentIndex] is not FunctionResultContent result)
                    continue;

                string text = ResultText(result.Result);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                results.Add(new ResultLocation(messageIndex, contentIndex, result, text));
            }
        }

        return results;
    }

    internal static string ResultText(object? result) =>
        result switch
        {
            null => "null",
            string text => text,
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(result)
        };

    private static ChatMessage CloneMessageWithIndependentContents(ChatMessage message)
    {
        ChatMessage clone = message.Clone();
        clone.Contents = [.. message.Contents];
        return clone;
    }

    private void LogIfChanged(BoundaryMetrics metrics)
    {
        if (metrics.OmittedDocuments == 0)
            return;

        _logger.LogWarning(
            "Gateway tool-document boundary applied originalDocuments={OriginalDocuments} "
            + "returnedDocuments={ReturnedDocuments} originalChars={OriginalChars} "
            + "returnedChars={ReturnedChars} omittedDocuments={OmittedDocuments}",
            metrics.OriginalDocuments,
            metrics.ReturnedDocuments,
            metrics.OriginalChars,
            metrics.ReturnedChars,
            metrics.OmittedDocuments);
    }

    private sealed record ResultLocation(
        int MessageIndex,
        int ContentIndex,
        FunctionResultContent Content,
        string Text);

    internal sealed record BoundaryMetrics(
        int OriginalDocuments,
        int ReturnedDocuments,
        int OriginalChars,
        int ReturnedChars,
        int OmittedDocuments);
}
