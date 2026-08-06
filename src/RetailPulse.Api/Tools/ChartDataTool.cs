using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Tools;

public class ChartDataTool
{
    private static readonly JsonSerializerOptions _inputOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<ChartDataTool> _logger;

    public ChartDataTool(ILogger<ChartDataTool> logger)
    {
        _logger = logger;
    }

    [Description("Create a chart visualization by providing structured chart data. Provide JSON matching the ChartSpec schema: {\"type\":\"line|bar|groupedBar|stackedBar|horizontalBar|pie|donut|gauge|table\",\"title\":\"...\",\"xAxisTitle\":\"...\",\"yAxisTitle\":\"...\",\"data\":[{\"legend\":\"Series name\",\"color\":\"#hex\",\"values\":[{\"x\":\"Category\",\"y\":123.4}]}]}. For a multi-series comparison, emit one entry in \"data\" per series, and give every series the same set of \"x\" category labels so they align. Every point needs a finite numeric \"y\". An empty or valueless chart is rejected, not rendered.")]
    public Task<string> CreateChart(
        [Description("JSON string matching the ChartSpec schema")] string chartSpecJson = "")
    {
        if (string.IsNullOrWhiteSpace(chartSpecJson))
        {
            return Task.FromResult(JsonSerializer.Serialize(new { error = "The chartSpecJson parameter is required. Please provide a valid JSON string matching the ChartSpec schema." }));
        }

        // Happy path: strict parse of a well-formed, complete payload.
        try
        {
            ChartSpec? spec = JsonSerializer.Deserialize<ChartSpec>(chartSpecJson, _inputOptions);
            if (spec == null)
            {
                return Task.FromResult(JsonSerializer.Serialize(new { error = "Invalid chart specification" }));
            }

            // A strictly-valid payload is only usable if it actually has a bindable
            // datapoint. Prefer it when renderable; otherwise fall back to the normalizer,
            // which recovers realistic non-canonical schemas (top-level series, Chart.js
            // labels/series, CanvasJS dataPoints) that bind to empty Data under strict
            // deserialization. Either way the result must pass the renderable invariant —
            // an empty/valueless chart is returned as a diagnostic, never as success.
            if (ChartSpecValidator.IsRenderable(spec))
            {
                return Task.FromResult(RenderableSuccessOrError(spec, recovered: false));
            }

            if (ChartSpecNormalizer.TryNormalize(chartSpecJson, out ChartSpec? enriched)
                && ChartSpecValidator.IsRenderable(enriched))
            {
                _logger.LogInformation(
                    "Enriched chart spec from non-canonical schema: {Type} - {Title} with {SeriesCount} series",
                    enriched!.Type, enriched.Title, enriched.Data.Count);

                return Task.FromResult(RenderableSuccessOrError(enriched, recovered: true));
            }

            return Task.FromResult(NoRenderableDataError());
        }
        catch (JsonException ex)
        {
            // Model output was malformed or truncated mid-stream. Attempt a
            // conservative recovery of the valid leading prefix before giving up.
            _logger.LogWarning(ex, "Invalid chart spec JSON — attempting recovery of a truncated payload");
            return Task.FromResult(TryRecover(chartSpecJson, ex));
        }
    }

    /// <summary>
    /// Recovers a usable chart from a malformed/truncated payload. Strips markdown
    /// fences, repairs a valid JSON prefix by trimming incomplete trailing tokens and
    /// balancing open containers, then salvages only complete series/datapoints.
    /// Returns a structured error when nothing usable can be recovered.
    /// </summary>
    private string TryRecover(string raw, JsonException originalError)
    {
        string cleaned = StripMarkdownFences(raw);

        // The payload may be well-formed but use a non-canonical, model-invented
        // schema (e.g. Chart.js-style data:{labels,series}). Normalize that shape
        // before falling back to truncation repair.
        if (ChartSpecNormalizer.TryNormalize(cleaned, out ChartSpec? normalized) && ChartSpecValidator.IsRenderable(normalized))
        {
            _logger.LogInformation(
                "Normalized non-canonical chart spec: {Type} - {Title} with {SeriesCount} series",
                normalized!.Type, normalized.Title, normalized.Data.Count);

            return RenderableSuccessOrError(normalized, recovered: true);
        }

        string? repaired = RepairTruncatedJson(cleaned);
        if (repaired is not null)
        {
            try
            {
                LenientChartSpec? lenient = JsonSerializer.Deserialize<LenientChartSpec>(repaired, _inputOptions);
                ChartSpec? salvaged = BuildUsableChart(lenient);
                if (salvaged is not null && ChartSpecValidator.IsRenderable(salvaged))
                {
                    _logger.LogInformation(
                        "Recovered truncated chart spec: {Type} - {Title} with {SeriesCount} series",
                        salvaged.Type, salvaged.Title, salvaged.Data.Count);

                    return RenderableSuccessOrError(salvaged, recovered: true);
                }
            }
            catch (JsonException)
            {
                // Repaired prefix still not deserializable — fall through to error.
            }
        }

        return JsonSerializer.Serialize(new
        {
            error = "Invalid JSON format",
            message = originalError.Message,
            recovered = false
        });
    }

    /// <summary>
    /// Serializes a success result only when the chart survives the renderable
    /// invariant (recognized type + title + at least one finite datapoint), returning
    /// the cleaned chart. Otherwise returns a structured diagnostic so an empty or
    /// valueless chart is never emitted as <c>status:"success"</c> and can never
    /// render as a blank card.
    /// </summary>
    private string RenderableSuccessOrError(ChartSpec? chart, bool recovered)
    {
        if (ChartSpecValidator.TryGetRenderable(chart, out ChartSpec? renderable) && renderable is not null)
        {
            _logger.LogInformation(
                "Chart created: {Type} - {Title} with {SeriesCount} series (recovered={Recovered})",
                renderable.Type, renderable.Title, renderable.Data.Count, recovered);

            return JsonSerializer.Serialize(new { status = "success", chart = renderable, recovered });
        }

        return NoRenderableDataError();
    }

    private string NoRenderableDataError()
    {
        _logger.LogWarning("Chart spec rejected: no renderable data (a recognized type, a title, and at least one finite datapoint are required)");

        return JsonSerializer.Serialize(new
        {
            error = "Chart has no renderable data",
            message = "The chart specification did not contain any bindable series. A renderable chart "
                + "needs a recognized type, a title, and at least one series with a finite numeric value. "
                + "Re-issue CreateChart using the canonical schema: data:[{legend, values:[{x, y}]}] with a "
                + "numeric y per point and a shared x label per category across every series.",
            recovered = false
        });
    }

    /// <summary>
    /// Removes surrounding markdown code fences (```json ... ```), tolerating a
    /// missing/truncated closing fence.
    /// </summary>
    private static string StripMarkdownFences(string input)
    {
        string s = input.Trim();

        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = s.IndexOf('\n');
            s = firstNewline >= 0 ? s[(firstNewline + 1)..] : s.TrimStart('`');
        }

        if (s.EndsWith("```", StringComparison.Ordinal))
        {
            s = s[..^3];
        }

        return s.Trim();
    }

    /// <summary>
    /// Scans a possibly-truncated JSON string and returns the longest valid prefix
    /// that can be safely closed. Incomplete trailing tokens, dangling properties, and
    /// partial datapoints are trimmed; open containers are balanced. Returns null when
    /// no complete value could be recovered. Never fabricates datapoint values.
    /// </summary>
    private static string? RepairTruncatedJson(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var stack = new List<Frame>();
        int safeLen = -1;
        List<char>? safeClosers = null;

        void RecordSafePoint(int lenExclusive)
        {
            safeLen = lenExclusive;
            var closers = new List<char>(stack.Count);
            for (int k = stack.Count - 1; k >= 0; k--)
            {
                closers.Add(stack[k].Type == '{' ? '}' : ']');
            }
            safeClosers = closers;
        }

        void MarkValueCompleted()
        {
            if (stack.Count > 0 && stack[^1].Type == '{')
            {
                stack[^1].ExpectValue = false;
            }
        }

        string? Build()
        {
            if (safeLen <= 0 || safeClosers is null)
            {
                return null;
            }
            var sb = new StringBuilder(input[..safeLen]);
            foreach (char cl in safeClosers)
            {
                sb.Append(cl);
            }
            return sb.ToString();
        }

        bool IsValuePosition() =>
            stack.Count == 0
            || stack[^1].Type == '['
            || (stack[^1].Type == '{' && stack[^1].ExpectValue);

        int i = 0;
        int n = input.Length;
        while (i < n)
        {
            char c = input[i];

            if (c is ' ' or '\t' or '\r' or '\n')
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '{':
                case '[':
                    if (stack.Count > 0 && stack[^1].Type == '{' && !stack[^1].ExpectValue)
                    {
                        return Build(); // key expected but got a container start
                    }
                    stack.Add(new Frame { Type = c, ExpectValue = c == '[' });
                    i++;
                    break;

                case '}':
                    if (stack.Count == 0 || stack[^1].Type != '{')
                    {
                        return Build();
                    }
                    stack.RemoveAt(stack.Count - 1);
                    MarkValueCompleted();
                    i++;
                    RecordSafePoint(i);
                    break;

                case ']':
                    if (stack.Count == 0 || stack[^1].Type != '[')
                    {
                        return Build();
                    }
                    stack.RemoveAt(stack.Count - 1);
                    MarkValueCompleted();
                    i++;
                    RecordSafePoint(i);
                    break;

                case ',':
                    if (stack.Count == 0)
                    {
                        return Build();
                    }
                    stack[^1].ExpectValue = stack[^1].Type == '[';
                    i++;
                    break;

                case ':':
                    if (stack.Count == 0 || stack[^1].Type != '{')
                    {
                        return Build();
                    }
                    stack[^1].ExpectValue = true;
                    i++;
                    break;

                case '"':
                    {
                        bool isValue = IsValuePosition();
                        int end = ScanString(input, i);
                        if (end < 0)
                        {
                            return Build(); // unterminated string — trim it
                        }
                        i = end;
                        if (isValue)
                        {
                            MarkValueCompleted();
                            RecordSafePoint(i);
                        }
                        break;
                    }

                default:
                    {
                        if (!IsValuePosition())
                        {
                            return Build(); // non-string where an object key is required
                        }
                        int end = ScanScalar(input, i);
                        if (end < 0)
                        {
                            return Build(); // scalar ran to end of input — possibly truncated
                        }
                        string token = input[i..end];
                        if (!IsValidScalar(token))
                        {
                            return Build();
                        }
                        i = end;
                        MarkValueCompleted();
                        RecordSafePoint(i);
                        break;
                    }
            }
        }

        return Build();
    }

    private static int ScanString(string s, int start)
    {
        int i = start + 1; // skip opening quote
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\')
            {
                i += 2; // skip escape and the escaped char
                continue;
            }
            if (c == '"')
            {
                return i + 1;
            }
            i++;
        }
        return -1; // unterminated (truncated)
    }

    private static int ScanScalar(string s, int start)
    {
        int i = start;
        while (i < s.Length)
        {
            char c = s[i];
            if (c is ',' or '}' or ']' or ' ' or '\t' or '\r' or '\n')
            {
                return i; // terminator — scalar is complete
            }
            i++;
        }
        return -1; // reached end of input without a terminator
    }

    private static bool IsValidScalar(string token) =>
        token is "true" or "false" or "null"
        || double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Validates the recovered chart shape and salvages only complete series and
    /// datapoints. Requires type + title and at least one usable datapoint; returns
    /// null (reject) otherwise. Incomplete datapoints (missing x or y) are dropped.
    /// </summary>
    private static ChartSpec? BuildUsableChart(LenientChartSpec? lenient)
    {
        if (lenient is null
            || string.IsNullOrWhiteSpace(lenient.Type)
            || string.IsNullOrWhiteSpace(lenient.Title))
        {
            return null;
        }

        var series = new List<ChartSeries>();
        foreach (LenientSeries? s in lenient.Data ?? [])
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Legend))
            {
                continue; // series truncated before its legend — cannot bind
            }

            var points = new List<ChartDataPoint>();
            foreach (LenientPoint? p in s.Values ?? [])
            {
                if (p is null || p.X is null || p.Y is null)
                {
                    continue; // never fabricate incomplete datapoints
                }
                points.Add(new ChartDataPoint { X = p.X, Y = p.Y.Value });
            }

            if (points.Count > 0)
            {
                series.Add(new ChartSeries { Legend = s.Legend, Color = s.Color, Values = points });
            }
        }

        if (series.Count == 0)
        {
            return null; // no usable chart data
        }

        return new ChartSpec
        {
            Type = lenient.Type,
            Title = lenient.Title,
            XAxisTitle = lenient.XAxisTitle,
            YAxisTitle = lenient.YAxisTitle,
            Data = series
        };
    }

    private sealed class Frame
    {
        public char Type;        // '{' or '['
        public bool ExpectValue; // objects: true after ':'; arrays: always true
    }

    // Tolerant mirror of ChartSpec used only on the recovery path so that
    // incomplete leading elements can be filtered instead of throwing.
    private sealed class LenientChartSpec
    {
        public string? Type { get; set; }
        public string? Title { get; set; }
        public string? XAxisTitle { get; set; }
        public string? YAxisTitle { get; set; }
        public List<LenientSeries>? Data { get; set; }
    }

    private sealed class LenientSeries
    {
        public string? Legend { get; set; }
        public string? Color { get; set; }
        public List<LenientPoint>? Values { get; set; }
    }

    private sealed class LenientPoint
    {
        public string? X { get; set; }
        public double? Y { get; set; }
    }
}
