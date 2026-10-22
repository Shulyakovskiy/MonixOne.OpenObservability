using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Refit;
using Sentry;
using Sentry.AspNetCore;
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
        error.GetProperty("value").GetString().ShouldBe("Transport error");
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, SocketError.ConnectionRefused)]
    [InlineData(HttpRequestError.NameResolutionError, SocketError.HostNotFound)]
    public async Task RefitTransportFailure_ShowsTypedCauseInTheTitleWithoutRequestData(
        HttpRequestError reason, SocketError socketError)
    {
        var transport = new RecordingTransport();
        var builder = CreateBuilder(transport);
        builder.AddOpenObservability();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<SentryTelemetryTests>>();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://private-user:private-password@service.test:7575/private-path?token=private-query");
        request.Options.Set(new HttpRequestOptionsKey<string>(HttpRequestMessageOptions.RelativePathTemplate), "/session/{id}");
        var error = new ApiRequestException(request, request.Method, new RefitSettings(),
            new HttpRequestException(reason, "private-message", new SocketException((int)socketError)));

        logger.LogEvent(LogLevel.Error, "profile.exchange.failed", error, "Profile exchange failed");
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        var payload = transport.Payloads.Where(value => value.Contains("profile.exchange.failed")).ShouldHaveSingleItem();
        payload.ShouldNotContain("private-");
        using var entry = JsonDocument.Parse(payload.Split('\n')[2]);
        var summary = $"Transport error: {socketError} ({reason})";
        var errors = entry.RootElement.GetProperty("exception").GetProperty("values").EnumerateArray().ToArray();
        errors[^1].GetProperty("type").GetString().ShouldBe(typeof(ApiRequestException).FullName);
        errors[^1].GetProperty("value").GetString().ShouldBe(summary);
        foreach (var inner in errors[..^1])
            inner.GetProperty("value").GetString().ShouldBe("[omitted: exception_message]");
        var tags = entry.RootElement.GetProperty("tags");
        tags.GetProperty("failure_summary").GetString().ShouldBe(summary);
        tags.GetProperty("dependency_address").GetString().ShouldBe("service.test");
        tags.GetProperty("dependency_port").GetString().ShouldBe("7575");
        tags.TryGetProperty("upstream_status_code", out _).ShouldBeFalse();
        entry.RootElement.GetProperty("extra").GetProperty("failure_summary").GetString().ShouldBe(summary);
    }

    [Fact]
    public async Task RefitFailure_ExportsSafeDependencyAndBusinessContext()
    {
        var transport = new RecordingTransport();
        var builder = CreateBuilder(transport);
        builder.AddOpenObservability();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<SentryTelemetryTests>>();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://private-user:private-password@service.test:8443/users/private-id?token=private-query");
        request.Options.Set(new HttpRequestOptionsKey<string>(HttpRequestMessageOptions.RelativePathTemplate), "/users/{id}");
        request.Options.Set(new HttpRequestOptionsKey<Type>(HttpRequestMessageOptions.InterfaceType), typeof(SentryTelemetryTests));
        request.Headers.Add("Authorization", "Bearer private-authorization");
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"access_token\":\"private-body\"}") };
        var failure = await ApiException.Create("private-refit-message", request, request.Method, response, new RefitSettings(),
            new JsonException("private-json", "$.private-path", 2, 3));

        using (logger.BeginOperation("profile.exchange"))
            logger.LogEvent(LogLevel.Error, "profile.exchange.failed", failure,
                "Operation failed with {error_code}", "invalid_response");
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        var payload = transport.Payloads.Where(value => value.Contains("profile.exchange.failed")).ShouldHaveSingleItem();
        payload.ShouldNotContain("private-");
        payload.ShouldContain("profile.exchange");
        using var entry = JsonDocument.Parse(payload.Split('\n')[2]);
        var tags = entry.RootElement.GetProperty("tags");
        tags.GetProperty("dependency_address").GetString().ShouldBe("service.test");
        tags.GetProperty("dependency_port").GetString().ShouldBe("8443");
        tags.GetProperty("dependency_http_route").GetString().ShouldBe("/users/{id}");
        tags.GetProperty("upstream_status_code").GetString().ShouldBe("200");
        tags.GetProperty("failure_kind").GetString().ShouldBe("response_deserialization");
        tags.GetProperty("error_code").GetString().ShouldBe("invalid_response");
        entry.RootElement.GetProperty("exception").GetProperty("values").EnumerateArray().Last()
            .GetProperty("value").GetString().ShouldBe(
                "HTTP response JSON error (HTTP 200)");
    }

    [Fact]
    public async Task CanceledRequest_KeepsItsProvenCancellationSourceInSentryDiagnostics()
    {
        var transport = new RecordingTransport();
        var builder = CreateBuilder(transport);
        builder.AddOpenObservability();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<SentryTelemetryTests>>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var accessor = host.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { RequestAborted = cancellation.Token };
        try
        {
            logger.LogError(new OperationCanceledException("private-message", cancellation.Token), "Request canceled");
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

            var payload = transport.Payloads.Where(value => value.Contains("Request canceled")).ShouldHaveSingleItem();
            payload.ShouldNotContain("private-");
            using var entry = JsonDocument.Parse(payload.Split('\n')[2]);
            entry.RootElement.GetProperty("tags").GetProperty("cancellation_source").GetString().ShouldBe("request_aborted");
            entry.RootElement.GetProperty("extra").GetProperty("cancellation_source").GetString().ShouldBe("request_aborted");
            entry.RootElement.GetProperty("exception").GetProperty("values").EnumerateArray().Last()
                .GetProperty("value").GetString().ShouldBe("Canceled: request_aborted");
        }
        finally { accessor.HttpContext = null; }
    }

    [Fact]
    public async Task Breadcrumbs_AreBoundedAndContainOnlyApprovedStructuredFields()
    {
        var transport = new RecordingTransport();
        var builder = CreateBuilder(transport);
        builder.AddOpenObservability();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<SentryTelemetryTests>>();
        using var scope = SentrySdk.PushScope();
        for (var attempt = 0; attempt < 35; attempt++)
            logger.LogEvent(LogLevel.Information, "dependency.request.completed",
                "private-message {retry_attempt} {dependency_address} {duration_ms} {payload} {RequestBody}",
                attempt, "service.test", 12.5, "private-payload", "private-body");
        logger.LogInformation("private-unstructured {payload}", "private-value");
        SentrySdk.AddBreadcrumb("private-sdk-message", data: new Dictionary<string, string>
        {
            ["url"] = "https://service.test/private-path?token=private-query",
            ["Authorization"] = "private-authorization"
        });
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var breadcrumb = Breadcrumb.FromJson(JsonSerializer.SerializeToElement(new
        {
            timestamp,
            message = "private-message",
            data = new Dictionary<string, string>
            {
                ["operation"] = "profile.update",
                ["error_code"] = "version_conflict",
                ["payload"] = "private-data",
                ["dependency_http_route"] = new string('x', 257)
            }
        }));
        SentrySdk.AddBreadcrumb(breadcrumb);

        using (logger.BeginOperation("profile.update"))
            logger.LogError("Capture breadcrumb check");
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        var payload = transport.Payloads.Where(value => value.Contains("Capture breadcrumb check")).ShouldHaveSingleItem();
        payload.ShouldNotContain("private-");
        using var entry = JsonDocument.Parse(payload.Split('\n')[2]);
        var crumbs = entry.RootElement.GetProperty("breadcrumbs").EnumerateArray().ToArray();
        crumbs.Length.ShouldBe(30);
        var first = crumbs[0].GetProperty("data");
        first.GetProperty("retry_attempt").GetString().ShouldBe("6");
        first.GetProperty("duration_ms").GetString().ShouldBe("12.5");
        first.GetProperty("dependency_address").GetString().ShouldBe("service.test");
        var last = crumbs[^1].GetProperty("data");
        crumbs[^1].GetProperty("timestamp").GetDateTimeOffset().ShouldBe(timestamp);
        last.GetProperty("operation").GetString().ShouldBe("profile.update");
        last.GetProperty("error_code").GetString().ShouldBe("version_conflict");
        last.TryGetProperty("dependency_http_route", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ParallelRequests_KeepSafeRequestContextAndBreadcrumbsInTheirOwnScopes()
    {
        var transport = new RecordingTransport();
        var builder = CreateBuilder(transport);
        builder.AddOpenObservability();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<SentryTelemetryTests>>();
        var arrived = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new CorrelationMiddleware(async context =>
        {
            var index = (int)context.Items["index"]!;
            logger.LogEvent(LogLevel.Information, "operation.updated", "Step {operation} {payload}",
                $"step.{index}", "private-step-data");
            if (Interlocked.Increment(ref arrived) == 2)
                ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (index == 0)
                logger.LogError(CreateFailure(), "Parallel request {request_id}", index);
            else
                SentrySdk.CaptureException(CreateFailure());
        }, host.Services.GetRequiredService<ILogger<CorrelationMiddleware>>());

        await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            var context = new DefaultHttpContext { RequestServices = host.Services };
            context.Items["index"] = index;
            context.Request.Method = index == 0 ? "POST" : "PATCH";
            context.Request.Path = "/users/private-id";
            context.Request.QueryString = new QueryString("?token=private-query");
            context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
                RoutePatternFactory.Parse($"/users{index}/{{id}}"), 0, EndpointMetadataCollection.Empty, "users"));
            await middleware.Invoke(context);
        }, TestContext.Current.CancellationToken)));
        logger.LogError("After requests");
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        var entries = transport.Payloads.Select(payload => JsonDocument.Parse(payload.Split('\n')[2])).ToArray();
        try
        {
            entries.Length.ShouldBe(3);
            foreach (var entry in entries.Where(entry => entry.RootElement.TryGetProperty("exception", out _)))
            {
                var root = entry.RootElement;
                var index = root.GetProperty("request").GetProperty("method").GetString() == "POST" ? 0 : 1;
                root.GetProperty("request").GetProperty("other").GetProperty("route").GetString().ShouldBe($"/users{index}/{{id}}");
                var tags = root.GetProperty("tags");
                tags.GetProperty("trace_id").GetString()!.Length.ShouldBe(32);
                tags.GetProperty("span_id").GetString()!.Length.ShouldBe(16);
                var crumbs = root.GetProperty("breadcrumbs").EnumerateArray().ToArray();
                crumbs.ShouldHaveSingleItem().GetProperty("data").GetProperty("operation").GetString().ShouldBe($"step.{index}");
                root.GetRawText().ShouldNotContain("private-");
            }
            var after = entries.Single(entry => entry.RootElement.TryGetProperty("logentry", out var log)
                && log.GetProperty("message").GetString() == "After requests");
            after.RootElement.GetProperty("request").EnumerateObject().ShouldBeEmpty();
            after.RootElement.TryGetProperty("breadcrumbs", out _).ShouldBeFalse();
        }
        finally
        {
            foreach (var entry in entries)
                entry.Dispose();
        }
    }

    [Fact]
    public async Task UnhandledHttpFailure_PreservesSafeContextInTheOfficialAspNetCoreCapture()
    {
        var transport = new RecordingTransport();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Logging:ConsoleEnabled"] = "false",
            ["Sentry:Dsn"] = "https://public@example.test/1",
            ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Critical"
        });
        builder.Services.Configure<SentryAspNetCoreOptions>(options =>
        {
            options.Transport = transport;
            options.AutoSessionTracking = false;
        });
        builder.AddOpenObservability();
        await using var server = builder.Build();
        server.UseRouting();
        server.UseOpenObservability();
        server.MapGet("/users/{id}", async context =>
        {
            await Task.Yield();
            context.RequestServices.GetRequiredService<ILogger<SentryTelemetryTests>>()
                .LogEvent(LogLevel.Information, "operation.updated", "Step {operation} {payload}",
                    "profile.read", "private-step");
            throw new InvalidOperationException("private-exception");
        });
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient();

        using var response = await client.GetAsync(server.Urls.Single() + "/users/private-id?token=private-query",
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        var payload = transport.Payloads.Where(value => value.Contains("SentryMiddleware.UnhandledException")).ShouldHaveSingleItem();
        payload.ShouldNotContain("private-");
        using var entry = JsonDocument.Parse(payload.Split('\n')[2]);
        var root = entry.RootElement;
        root.GetProperty("request").GetProperty("method").GetString().ShouldBe("GET");
        root.GetProperty("request").GetProperty("other").GetProperty("route").GetString().ShouldBe("/users/{id}");
        root.GetProperty("breadcrumbs").EnumerateArray().Any(crumb =>
            crumb.GetProperty("data").TryGetProperty("operation", out var operation) && operation.GetString() == "profile.read")
            .ShouldBeTrue();
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
