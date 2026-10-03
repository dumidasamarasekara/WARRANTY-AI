using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Warranty.UnitTests.Api;

public sealed class ServiceDefaultsTests
{
    [Fact]
    public void Every_warranty_source_is_traced_and_every_warranty_meter_is_collected()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = "Warranty.UnitTests.Host" });

        // Metrics are collected only when an exporter reads them; nothing is listening at this endpoint
        // and the test never exports.
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4317";
        builder.AddServiceDefaults();
        using var host = builder.Build();
        host.Services.GetRequiredService<TracerProvider>();
        host.Services.GetRequiredService<MeterProvider>();

        Extensions.WarrantyTelemetrySources.ShouldBe(
            ["Warranty.AI.Harness", "Warranty.AI.Gateway", "Warranty.Knowledge", "Warranty.Tools", "Warranty.Api"]);
        foreach (var name in Extensions.WarrantyTelemetrySources)
        {
            using var source = new ActivitySource(name);
            source.HasListeners().ShouldBeTrue(name);

            using var meter = new Meter(name);
            meter.CreateCounter<long>("warranty.test.count").Enabled.ShouldBeTrue(name);
        }
    }
}
