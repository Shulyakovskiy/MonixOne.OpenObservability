using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Sentry;
using Sentry.Protocol;
using Shouldly;
using Xunit;

namespace MonixOne.OpenObservability.Tests;

public sealed class TelemetryRegistrationTests
{
    [Fact]
    public async Task SingleRegistration_CollectsBuiltInRuntimeMetricsWithoutInstrumentationAdapters()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Observability:Logging:ConsoleEnabled"] = "false", ["Sentry:Dsn"] = "", ["SENTRY_DSN"] = "" });
        builder.AddOpenObservability();
        using var exporter = new MetricExporter();
        using var reader = new BaseExportingMetricReader(exporter);
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(reader));
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<MeterProvider>().ForceFlush().ShouldBeTrue();

        exporter.Names.ShouldContain(name => name.StartsWith("dotnet.gc.", StringComparison.Ordinal));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void SentryUnhandledEvent_OmitsRawRequestUserExceptionMessageAndDataInSerializedEnvelope()
    {
        var failure = new InvalidOperationException("private-exception-message");
        failure.Data["payload"] = "private-exception-data";
        failure.Data["sentry:tag:http_route"] = "private-route";
        var entry = new SentryEvent(failure)
        {
            Request = new SentryRequest
            {
                Method = "POST",
                Url = "https://service.test/private-path?token=private-query",
                Data = "private-body",
                Headers = { ["Authorization"] = "private-header" }
            },
            User = new SentryUser { Email = "private-email", IpAddress = "private-ip" },
            SentryExceptions = [new SentryException { Type = nameof(InvalidOperationException), Value = failure.Message }]
        };
        entry.SetExtra("payload", "private-extra");
        entry.SetTag("http_route", "private-route");

        var sanitized = SentryConfiguration.SanitizeEvent(entry);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            sanitized.WriteTo(writer, logger: null);
        var json = Encoding.UTF8.GetString(stream.ToArray());

        json.ShouldNotContain("private-");
        json.ShouldContain("InvalidOperationException");
        json.ShouldContain("exception_root_type");
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("request").GetProperty("method").GetString().ShouldBe("POST");
        document.RootElement.GetProperty("request").TryGetProperty("other", out _).ShouldBeFalse();
    }

    private sealed class MetricExporter : BaseExporter<Metric>
    {
        internal List<string> Names { get; } = [];
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
                Names.Add(metric.Name);
            return ExportResult.Success;
        }
    }
}
