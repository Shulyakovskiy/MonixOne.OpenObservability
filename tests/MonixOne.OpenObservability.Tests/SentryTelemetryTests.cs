using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.Extensions.Logging;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Shouldly;
using Xunit;

namespace MonixOne.OpenObservability.Tests;

[Collection("Sentry SDK")]
public sealed class SentryTelemetryTests
{
    [Fact]
    public async Task OrdinaryLogError_UsesOfficialSentryProvider_WithSafeDiagnosticsAndNoManualCapture()
    {
        var transport = new RecordingTransport();
        var builder = CreateBuilder(transport);
        builder.AddOpenObservability();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<SentryTelemetryTests>>();

        logger.LogError(CreateFailure(),
            "Operation failed for {request_id}", "visible-request");
        logger.LogError("Plain operation failed for {request_id}", "visible-plain-error");
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        var payload = transport.Payloads.Where(value => value.Contains("visible-request")).ShouldHaveSingleItem();
        payload.ShouldNotContain("private-");
        payload.ShouldContain("exception_root_type");
        payload.ShouldContain("transport");
        var eventJson = payload.Split('\n')[2];
        using var entry = JsonDocument.Parse(eventJson);
        var error = entry.RootElement.GetProperty("exception").GetProperty("values").EnumerateArray().Single();
        error.GetProperty("type").GetString().ShouldBe(typeof(HttpRequestException).FullName);
        error.GetProperty("stacktrace").GetProperty("frames").GetArrayLength().ShouldBeGreaterThan(0);
        var plainPayload = transport.Payloads.Where(value => value.Contains("visible-plain-error")).ShouldHaveSingleItem();
        using var plain = JsonDocument.Parse(plainPayload.Split('\n')[2]);
        plain.RootElement.TryGetProperty("exception", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task SentryMinimumEventLevel_IsHonoredForAutomaticExceptionDiagnostics()
    {
        var transport = new RecordingTransport();
        var builder = CreateBuilder(transport);
        builder.Services.Configure<SentryLoggingOptions>(options => options.MinimumEventLevel = LogLevel.Critical);
        builder.AddOpenObservability();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<SentryTelemetryTests>>();

        logger.LogError(CreateFailure(), "Ignored operation");
        logger.LogCritical(CreateFailure(), "Critical operation");
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        transport.Payloads.Where(value => value.Contains("Ignored operation")).ShouldBeEmpty();
        transport.Payloads.Where(value => value.Contains("Critical operation")).ShouldHaveSingleItem()
            .ShouldNotContain("private-");
    }

    [Fact]
    public async Task StandardDsnAlias_WorksWithAnEmptyJsonDsn()
    {
        var transport = new RecordingTransport();
        var builder = CreateBuilder(transport);
        builder.Configuration["Sentry:Dsn"] = "";
        builder.Configuration["SENTRY_DSN"] = "https://public@example.test/1";
        builder.AddOpenObservability();
        using var host = builder.Build();

        host.Services.GetRequiredService<ILogger<SentryTelemetryTests>>()
            .LogError(CreateFailure(), "Alias operation");
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        transport.Payloads.Where(value => value.Contains("Alias operation")).ShouldHaveSingleItem()
            .ShouldNotContain("private-");
    }

    [Fact]
    public async Task ParallelExceptionLogs_KeepEachNativeExceptionWithItsOwnRequest()
    {
        var transport = new RecordingTransport();
        var builder = CreateBuilder(transport);
        builder.AddOpenObservability();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<SentryTelemetryTests>>();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(index => Task.Run(() =>
        {
            Exception error = index % 2 == 0 ? new ArgumentException("private-argument")
                : new InvalidOperationException("private-operation");
            logger.LogError(error, "Parallel operation {request_id}", index);
        }, TestContext.Current.CancellationToken)));
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        var entries = transport.Payloads.Where(value => value.Contains("Parallel operation")).ToArray();
        entries.Length.ShouldBe(10);
        foreach (var payload in entries)
        {
            payload.ShouldNotContain("private-");
            using var entry = JsonDocument.Parse(payload.Split('\n')[2]);
            var requestId = int.Parse(entry.RootElement.GetProperty("tags").GetProperty("request_id").GetString()!);
            var type = entry.RootElement.GetProperty("exception").GetProperty("values")[0].GetProperty("type").GetString();
            type.ShouldBe(requestId % 2 == 0 ? typeof(ArgumentException).FullName : typeof(InvalidOperationException).FullName);
        }
    }

    [Fact]
    public async Task DirectSdkException_ClearsExceptionDataFromTheSerializedEvent()
    {
        var transport = new RecordingTransport();
        var builder = CreateBuilder(transport);
        builder.AddOpenObservability();
        using var host = builder.Build();
        _ = host.Services.GetRequiredService<ILogger<SentryTelemetryTests>>();

        SentrySdk.CaptureException(CreateFailure());
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        var payload = transport.Payloads.ShouldHaveSingleItem();
        payload.ShouldNotContain("private-");
        using var entry = JsonDocument.Parse(payload.Split('\n')[2]);
        var error = entry.RootElement.GetProperty("exception").GetProperty("values")[0];
        error.GetProperty("type").GetString().ShouldBe(typeof(HttpRequestException).FullName);
        error.GetProperty("stacktrace").GetProperty("frames").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task HttpTransaction_DoesNotExportRawDescriptionsOrUrlData()
    {
        var transport = new RecordingTransport();
        var builder = CreateBuilder(transport);
        builder.Services.Configure<SentryLoggingOptions>(options => options.TracesSampleRate = 1);
        builder.AddOpenObservability();
        using var host = builder.Build();
        _ = host.Services.GetRequiredService<ILogger<SentryTelemetryTests>>();
        var transaction = SentrySdk.StartTransaction("visible-operation", "task");
        var span = transaction.StartChild("http.client", "GET https://example.test/private-path?token=private-query");
        span.SetData("url", "https://example.test/private-path?token=private-query");
        span.SetTag("http.url", "https://example.test/private-path");
        span.Finish();
        transaction.Finish();
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        var payload = transport.Payloads.Where(value => value.Contains("\"type\":\"transaction\"")).ShouldHaveSingleItem();
        payload.ShouldNotContain("private-");
        payload.ShouldContain("http.client");
    }

    private static HostApplicationBuilder CreateBuilder(RecordingTransport transport)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Logging:ConsoleEnabled"] = "false",
            ["Sentry:Dsn"] = "https://public@example.test/1"
        });
        builder.Services.Configure<SentryLoggingOptions>(options =>
        {
            options.Transport = transport;
            options.AutoSessionTracking = false;
        });
        return builder;
    }

    private static Exception CreateFailure()
    {
        try
        { throw new HttpRequestException("private-exception-message"); }
        catch (HttpRequestException exception)
        {
            exception.Data["payload"] = "private-exception-data";
            exception.Data["sentry:tag:payload"] = "private-exception-tag";
            exception.Data["sentry:context:payload"] = new { access_token = "private-exception-context" };
            return exception;
        }
    }

    private sealed class RecordingTransport : ITransport
    {
        internal ConcurrentQueue<string> Payloads { get; } = new();
        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, logger: null, cancellationToken);
            Payloads.Enqueue(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }
}

[CollectionDefinition("Sentry SDK", DisableParallelization = true)]
public sealed class SentrySdkCollection;
