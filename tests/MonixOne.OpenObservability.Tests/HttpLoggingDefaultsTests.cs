using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace MonixOne.OpenObservability.Tests;

[Collection("HTTP telemetry")]
public sealed class HttpLoggingDefaultsTests
{
    [Theory]
    [InlineData("GET", 200)]
    [InlineData("POST", 201)]
    [InlineData("PUT", 204)]
    [InlineData("PATCH", 400)]
    [InlineData("DELETE", 401)]
    [InlineData("OPTIONS", 403)]
    [InlineData("GET", 404)]
    [InlineData("HEAD", 500)]
    public async Task Incoming_AllMethodsAndStatuses_AreExportedDespiteBroadFrameworkWarning(
        string method, int statusCode)
    {
        using var exporter = new RecordingExporter();
        var builder = CreateBuilder();
        builder.Services.AddOpenTelemetry().WithLogging(logging =>
            logging.AddProcessor(new SimpleLogRecordExportProcessor(exporter)));
        using var host = builder.Build();

        await InvokeRequest(host.Services, method, "/users/private-id", statusCode);

        var entry = exporter.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Attributes.ShouldContain(field => field.Key == "event_name" && Equals(field.Value, "http.request.completed"));
        entry.Attributes.ShouldContain(field => field.Key == "http_method" && Equals(field.Value, method));
        entry.Attributes.ShouldContain(field => field.Key == "http_status_code" && Equals(field.Value, statusCode));
        entry.Attributes.ShouldContain(field => field.Key == "http_route" && Equals(field.Value, "/users/{id}"));
        entry.Attributes.Single(field => field.Key == "trace_id").Value.ShouldBeOfType<string>().Length.ShouldBe(32);
        entry.Message.ShouldNotContain("private-id");
        entry.Message.ShouldNotContain("private-query");
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/health/live")]
    [InlineData("/api/health/ready")]
    public async Task Incoming_HealthRequests_AreExcluded(string path)
    {
        using var exporter = new RecordingExporter();
        var builder = CreateBuilder();
        builder.Services.AddOpenTelemetry().WithLogging(logging =>
            logging.AddProcessor(new SimpleLogRecordExportProcessor(exporter)));
        using var host = builder.Build();

        await InvokeRequest(host.Services, "GET", path, 200);

        exporter.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Incoming_ExplicitProviderCategoryDisable_IsRespected()
    {
        using var exporter = new RecordingExporter();
        var builder = CreateBuilder();
        builder.Configuration["Logging:OpenTelemetry:LogLevel:Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"] = "None";
        builder.Services.AddOpenTelemetry().WithLogging(logging =>
            logging.AddProcessor(new SimpleLogRecordExportProcessor(exporter)));
        using var host = builder.Build();

        await InvokeRequest(host.Services, "GET", "/users/private-id", 200);

        exporter.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task ErrorHandler_FinalStatusIsSeparateFromExceptionTimeSnapshot()
    {
        using var exporter = new RecordingExporter(includeErrors: true);
        var builder = CreateBuilder();
        builder.Services.AddOpenTelemetry().WithLogging(logging =>
            logging.AddProcessor(new SimpleLogRecordExportProcessor(exporter)));
        using var host = builder.Build();
        var context = new DefaultHttpContext { RequestServices = host.Services };
        context.Request.Method = "POST";
        context.Request.Path = "/users/private-id";
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/users/{id}"), 0, EndpointMetadataCollection.Empty, "users"));
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        try
        {
            var pipeline = new ApplicationBuilder(host.Services);
            pipeline.UseOpenObservability();
            pipeline.Use(next => async http =>
            {
                try { await next(http); }
                catch (HttpRequestException exception)
                {
                    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("incident.test")
                        .LogError(exception, "Dependency failed");
                    http.Response.StatusCode = 502;
                }
            });
            pipeline.Run(_ => throw new HttpRequestException("private-error", null, HttpStatusCode.ServiceUnavailable));

            await pipeline.Build()(context);

            var error = exporter.Entries.Where(entry => entry.Attributes.Any(field => field.Key == "failure_kind"))
                .ShouldHaveSingleItem();
            error.Attributes.ShouldContain(field => field.Key == "upstream_status_code" && Equals(field.Value, 503));
            error.Attributes.ShouldContain(field => field.Key == "http_response_status_code_at_log" && Equals(field.Value, 200));
            error.Attributes.ShouldNotContain(field => field.Key == "http_status_code");
            var completion = exporter.Entries.Where(entry => entry.Attributes.Any(field =>
                field.Key == "event_name" && Equals(field.Value, "http.request.completed"))).ShouldHaveSingleItem();
            completion.Attributes.ShouldContain(field => field.Key == "http_status_code" && Equals(field.Value, 502));
            completion.Attributes.Single(field => field.Key == "trace_id").Value
                .ShouldBe(error.Attributes.Single(field => field.Key == "trace_id").Value);
        }
        finally { host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = null; }
    }

    [Fact]
    public async Task OtlpHttp_AllSignals_UseTransportOutsideApplicationHttpLogging()
    {
        var requests = new ConcurrentQueue<(string Path, string ContentType, byte[] Body)>();
        var receiverBuilder = WebApplication.CreateBuilder();
        receiverBuilder.Logging.ClearProviders();
        receiverBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var receiver = receiverBuilder.Build();
        receiver.Run(async context =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            requests.Enqueue((context.Request.Path.Value ?? "", context.Request.ContentType ?? "", body.ToArray()));
            context.Response.ContentType = "application/x-protobuf";
        });
        await receiver.StartAsync(TestContext.Current.CancellationToken);
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Observability:Logging:Enabled"] = "true",
            ["Observability:Logging:OtlpEndpoint"] = receiver.Urls.Single() + "/api/default",
            ["Observability:Logging:OtlpProtocol"] = "http/protobuf"
        });
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource("observability.test"))
            .WithMetrics(metrics => metrics.AddMeter("observability.test"));
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        using var source = new ActivitySource("observability.test");
        using (var activity = source.StartActivity("visible-operation"))
            activity.ShouldNotBeNull();
        using var meter = new Meter("observability.test");
        meter.CreateCounter<long>("test_counter").Add(1);
        await InvokeRequest(host.Services, "POST", "/users/private-id", 500);
        host.Services.GetRequiredService<LoggerProvider>().ForceFlush(5000).ShouldBeTrue();
        host.Services.GetRequiredService<TracerProvider>().ForceFlush(5000).ShouldBeTrue();
        host.Services.GetRequiredService<MeterProvider>().ForceFlush(5000).ShouldBeTrue();

        var request = requests.Where(entry => entry.Path == "/api/default/v1/logs").ShouldHaveSingleItem();
        request.ContentType.ShouldBe("application/x-protobuf");
        request.Body.ShouldNotBeEmpty();
        // These string attributes are carried verbatim in the protobuf envelope.
        var payload = Encoding.UTF8.GetString(request.Body);
        payload.ShouldContain("http.request.completed");
        payload.ShouldContain("/users/{id}");
        payload.ShouldContain("trace_id");
        payload.ShouldNotContain("private-id");
        payload.ShouldNotContain("private-query");
        payload.ShouldNotContain("http.client.response.body");
        var traces = requests.Where(entry => entry.Path == "/api/default/v1/traces").ShouldHaveSingleItem();
        Encoding.UTF8.GetString(traces.Body).ShouldContain("visible-operation");
        var metrics = requests.Where(entry => entry.Path == "/api/default/v1/metrics").ShouldHaveSingleItem();
        Encoding.UTF8.GetString(metrics.Body).ShouldContain("test_counter");
        requests.Count.ShouldBe(3);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await receiver.StopAsync(TestContext.Current.CancellationToken);
    }

    private static HostApplicationBuilder CreateBuilder(Dictionary<string, string?>? overrides = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Logging:ConsoleEnabled"] = "false",
            ["Sentry:Dsn"] = "",
            ["SENTRY_DSN"] = "",
            ["Logging:LogLevel:Default"] = "Information",
            ["Logging:LogLevel:Microsoft"] = "Warning",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            ["Logging:LogLevel:System.Net.Http.HttpClient"] = "Warning"
        });
        if (overrides is not null)
            builder.Configuration.AddInMemoryCollection(overrides);
        builder.AddOpenObservability();
        return builder;
    }

    private static async Task InvokeRequest(IServiceProvider services, string method, string path, int statusCode)
    {
        using var responseBody = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString("?token=private-query");
        context.Response.Body = responseBody;
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/users/{id}"), 0, EndpointMetadataCollection.Empty, "users"));
        var pipeline = new ApplicationBuilder(services);
        pipeline.UseOpenObservability();
        pipeline.Run(http =>
        {
            http.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });
        await pipeline.Build()(context);
    }

    private sealed class RecordingExporter(bool includeErrors = false) : BaseExporter<LogRecord>
    {
        internal ConcurrentQueue<(LogLevel Level, string Message, KeyValuePair<string, object?>[] Attributes)> Entries { get; } = new();

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var entry in batch)
                if (entry.CategoryName == "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"
                    || includeErrors && entry.CategoryName == "incident.test")
                    Entries.Enqueue((entry.LogLevel, entry.FormattedMessage ?? "", entry.Attributes?.ToArray() ?? []));
            return ExportResult.Success;
        }
    }
}

// HTTP instrumentation is process-wide and also observes other tests' servers.
[CollectionDefinition("HTTP telemetry", DisableParallelization = true)]
public sealed class HttpTelemetryCollection;
