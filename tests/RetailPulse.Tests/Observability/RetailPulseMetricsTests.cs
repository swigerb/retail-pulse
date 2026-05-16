using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RetailPulse.Api.Telemetry;

namespace RetailPulse.Tests.Observability;

public class RetailPulseMetricsTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly RetailPulseMetrics _metrics;
    private readonly List<(string Name, object? Value)> _recordedSimple = [];

    public RetailPulseMetricsTests()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        ServiceProvider provider = services.BuildServiceProvider();
        IMeterFactory meterFactory = provider.GetRequiredService<IMeterFactory>();
        _metrics = new RetailPulseMetrics(meterFactory);

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RetailPulseMetrics.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) => _recordedSimple.Add((instrument.Name, measurement)));
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) => _recordedSimple.Add((instrument.Name, measurement)));
        _listener.Start();
    }

    [Fact]
    public void RecordIntentClassification_EmitsCounter()
    {
        _metrics.RecordIntentClassification("demand_forecasting", fastPathHit: true);
        _listener.RecordObservableInstruments();

        _recordedSimple.Should().Contain(r =>
            r.Name == "retailpulse.intent_classification_total" && (long)r.Value! == 1);
    }

    [Fact]
    public void RecordCacheHit_EmitsCounter()
    {
        _metrics.RecordCacheHit();
        _listener.RecordObservableInstruments();

        _recordedSimple.Should().Contain(r => r.Name == "retailpulse.cache_hit_total");
    }

    [Fact]
    public void RecordCacheMiss_EmitsCounter()
    {
        _metrics.RecordCacheMiss();
        _listener.RecordObservableInstruments();

        _recordedSimple.Should().Contain(r => r.Name == "retailpulse.cache_miss_total");
    }

    [Fact]
    public void RecordError_EmitsCounterWithCategory()
    {
        _metrics.RecordError("timeout");
        _listener.RecordObservableInstruments();

        _recordedSimple.Should().Contain(r => r.Name == "retailpulse.error_total");
    }

    [Fact]
    public void RecordToolCallDuration_EmitsHistogram()
    {
        _metrics.RecordToolCallDuration("get_depletion_stats", 150.5);
        _listener.RecordObservableInstruments();

        _recordedSimple.Should().Contain(r =>
            r.Name == "retailpulse.tool_call_duration_ms" && (double)r.Value! == 150.5);
    }

    [Fact]
    public void RecordAgentExecutionDuration_EmitsHistogram()
    {
        _metrics.RecordAgentExecutionDuration("demand-forecast", 2500.0);
        _listener.RecordObservableInstruments();

        _recordedSimple.Should().Contain(r =>
            r.Name == "retailpulse.agent_execution_duration_ms" && (double)r.Value! == 2500.0);
    }

    [Fact]
    public void RecordRoutingDuration_EmitsHistogram()
    {
        _metrics.RecordRoutingDuration(42.0);
        _listener.RecordObservableInstruments();

        _recordedSimple.Should().Contain(r =>
            r.Name == "retailpulse.routing_duration_ms" && (double)r.Value! == 42.0);
    }

    [Fact]
    public void RecordRequest_EmitsCounterAndHistogram()
    {
        _metrics.RecordRequest(1200.0, isError: false);
        _listener.RecordObservableInstruments();

        _recordedSimple.Should().Contain(r => r.Name == "retailpulse.request_total");
        _recordedSimple.Should().Contain(r => r.Name == "retailpulse.request_duration_ms");
    }

    public void Dispose() => _listener.Dispose();
}
