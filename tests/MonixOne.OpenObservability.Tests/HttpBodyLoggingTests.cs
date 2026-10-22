using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace MonixOne.OpenObservability.Tests;

public sealed class HttpBodyLoggingTests
{
    [Fact]
    public async Task SlowChunkedResponse_HeadersReturnBeforeBody_AndConsumedBodyIsMasked()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer(release.Task);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var logs = new Collector();
        using var exporter = new BodyExporter();
        using var host = CreateClientHost(logs, exporter);
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("slow");
        client.Timeout.ShouldBe(TimeSpan.FromSeconds(75));
        using var activity = new Activity("test-request").Start();
        var traceId = activity.TraceId.ToString();
        try
        {
            using var response = await client.GetAsync(server.Urls.Single(), HttpCompletionOption.ResponseHeadersRead,
                TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            activity.Stop();
            using var unrelatedActivity = new Activity("unrelated-reader").Start();
            unrelatedActivity.TraceId.ToString().ShouldNotBe(traceId);
            logs.Entries.Where(entry => entry.ContainsKey("ResponseBody")).ShouldBeEmpty();
            await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            var prefix = new byte[8];
            await stream.ReadExactlyAsync(prefix, TestContext.Current.CancellationToken);
            Encoding.UTF8.GetString(prefix).ShouldBe("{\"safe\":");
            await Task.Delay(1200, TestContext.Current.CancellationToken);
            release.SetResult();
            using var reader = new StreamReader(stream);
            var body = Encoding.UTF8.GetString(prefix) + await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            body.ShouldBe("{\"safe\":\"visible\",\"password\":\"private-password\"}");
            var entry = logs.Entries.Where(entry => entry.ContainsKey("ResponseBody")).ShouldHaveSingleItem();
            using var masked = System.Text.Json.JsonDocument.Parse((string)entry["ResponseBody"]!);
            masked.RootElement.GetProperty("safe").GetString().ShouldBe("visible");
            masked.RootElement.GetProperty("password").GetString().ShouldBe("pri***rd");
            entry["trace_id"].ShouldBe(traceId);
            exporter.TraceIds.ShouldHaveSingleItem().ShouldBe(traceId);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CanceledPartialRead_PropagatesCancellation_AndOmitsCapturedPrefix()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer(release.Task);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var logs = new Collector();
        using var host = CreateClientHost(logs);
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("slow");
        try
        {
            using var response = await client.GetAsync(server.Urls.Single(), HttpCompletionOption.ResponseHeadersRead,
                TestContext.Current.CancellationToken);
            await using (var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken))
            {
                await stream.ReadExactlyAsync(new byte[8], TestContext.Current.CancellationToken);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await stream.ReadExactlyAsync(new byte[32], cancellation.Token));
            }
            var entry = logs.Entries.Where(entry => entry.ContainsKey("ResponseBody")).ShouldHaveSingleItem();
            entry["ResponseBody"].ShouldBe("[omitted: incomplete_body]");
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task EmptyRead_DoesNotCompleteBodyLogBeforeThePayloadIsConsumed()
    {
        await using var server = CreateServer(Task.CompletedTask);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var logs = new Collector();
        using var host = CreateClientHost(logs);
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("slow");
        using var response = await client.GetAsync(server.Urls.Single(), HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        (await stream.ReadAsync(Memory<byte>.Empty, TestContext.Current.CancellationToken)).ShouldBe(0);
        logs.Entries.Where(entry => entry.ContainsKey("ResponseBody")).ShouldBeEmpty();
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).ShouldContain("private-password");

        var entry = logs.Entries.Where(entry => entry.ContainsKey("ResponseBody")).ShouldHaveSingleItem();
        ((string)entry["ResponseBody"]!).ShouldContain("visible");
        ((string)entry["ResponseBody"]!).ShouldContain("pri***rd");
    }

    [Fact]
    public async Task NoParentActivity_BodyLogReferencesTheExportedHttpClientSpan()
    {
        Activity.Current.ShouldBeNull();
        await using var server = CreateServer(Task.CompletedTask);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var logs = new Collector();
        using var spans = new SpanExporter();
        using var host = CreateClientHost(logs, spans: spans);
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("slow");

        using var response = await client.GetAsync(server.Urls.Single(), TestContext.Current.CancellationToken);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("visible");

        var span = spans.Spans.Where(span => span.Kind == ActivityKind.Client).ShouldHaveSingleItem();
        var entry = logs.Entries.Where(entry => entry.ContainsKey("ResponseBody")).ShouldHaveSingleItem();
        entry["trace_id"].ShouldBe(span.TraceId.ToString());
        entry["span_id"].ShouldBe(span.SpanId.ToString());
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ClientSpan_DoesNotExportRawRouteParameterOrQueryValues()
    {
        await using var server = CreateServer(Task.CompletedTask);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var logs = new Collector();
        using var spans = new SpanExporter();
        using var host = CreateClientHost(logs, spans: spans);
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("slow");
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Urls.Single() + "/private-path?token=private-query");
        request.Options.Set(new HttpRequestOptionsKey<string>(Refit.HttpRequestMessageOptions.RelativePathTemplate), "/{id}");
        request.Options.Set(new HttpRequestOptionsKey<Type>(Refit.HttpRequestMessageOptions.InterfaceType), typeof(HttpBodyLoggingTests));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var span = spans.Spans.Where(span => span.Kind == ActivityKind.Client).ShouldHaveSingleItem();
        string.Join(" ", span.Tags.Select(tag => $"{tag.Key}={tag.Value}")).ShouldNotContain("private-");
        span.Tags.FirstOrDefault(tag => tag.Key == "http.route").Value.ShouldBe("/{id}");
        span.DisplayName.ShouldNotContain("private-");
        span.Status.ShouldBe(ActivityStatusCode.Error);
        var metadata = logs.Entries.Where(entry => entry.ContainsKey("dependency_client") && !entry.ContainsKey("ResponseBody"))
            .ShouldHaveSingleItem();
        metadata["dependency_http_route"].ShouldBe("/{id}");
        metadata["dependency_address"].ShouldBe("127.0.0.1");
        metadata["dependency_port"].ShouldBe(new Uri(server.Urls.Single()).Port);
        metadata["upstream_status_code"].ShouldBe(404);
        string.Join(" ", metadata.Values).ShouldNotContain("private-");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RealIncomingRequest_LogsAndSpansDoNotExposeRawPathQueryOrBodies()
    {
        using var logs = new Collector();
        using var spans = new SpanExporter();
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Logging:ConsoleEnabled"] = "false",
            ["Sentry:Dsn"] = "",
            ["SENTRY_DSN"] = "",
            ["Logging:LogLevel:Default"] = "Information"
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddOpenTelemetry().WithTracing(tracing =>
            tracing.AddProcessor(new SimpleActivityExportProcessor(spans)));
        builder.AddOpenObservability();
        builder.Logging.AddProvider(logs);
        await using var server = builder.Build();
        server.UseRouting();
        server.UseOpenObservability();
        server.MapPost("/users/{id}", async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            (await reader.ReadToEndAsync(context.RequestAborted)).ShouldContain("private-password");
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"access_token":"private-response-token","safe":"visible"}""", context.RequestAborted);
        });
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient();

        using var response = await client.PostAsync(server.Urls.Single() + "/users/private-path?token=private-query",
            new StringContent("""{"password":"private-password"}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("private-response-token");
        await server.StopAsync(TestContext.Current.CancellationToken);

        logs.Entries.Where(entry => entry.ContainsKey("RequestBody")).ShouldHaveSingleItem();
        foreach (var entry in logs.Entries)
            string.Join(" ", entry.Select(field => $"{field.Key}={field.Value}")).ShouldNotContain("private-");
        var span = spans.Spans.Where(span => span.Kind == ActivityKind.Server).ShouldHaveSingleItem();
        string.Join(" ", span.Tags.Select(tag => $"{tag.Key}={tag.Value}")).ShouldNotContain("private-");
        span.Tags.FirstOrDefault(tag => tag.Key == "http.route").Value.ShouldBe("/users/{id}");
        span.DisplayName.ShouldNotContain("private-");
    }

    [Fact]
    public async Task RegistrationTwice_EmitsOneBodyRecord()
    {
        await using var server = CreateServer(Task.CompletedTask);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var logs = new Collector();
        using var host = CreateClientHost(logs, repeatRegistration: true);
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("slow");

        using var response = await client.GetAsync(server.Urls.Single(), TestContext.Current.CancellationToken);

        logs.Entries.Where(entry => entry.ContainsKey("ResponseBody")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AsyncSend_SynchronousResponseRead_PreservesAndMasksTheBody()
    {
        await using var server = CreateServer(Task.CompletedTask);
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var logs = new Collector();
        using var host = CreateClientHost(logs);
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("slow");
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Urls.Single());

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        using var stream = response.Content.ReadAsStream(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        reader.ReadToEnd().ShouldContain("private-password");

        var entry = logs.Entries.Where(entry => entry.ContainsKey("ResponseBody")).ShouldHaveSingleItem();
        ((string)entry["ResponseBody"]!).ShouldContain("pri***rd");
        ((string)entry["ResponseBody"]!).ShouldNotContain("private-");
    }

    private static WebApplication CreateServer(Task release)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var server = builder.Build();
        server.MapGet("/", async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"safe\":", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await release.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("\"visible\",\"password\":\"private-password\"}", context.RequestAborted);
        });
        return server;
    }

    private static IHost CreateClientHost(Collector logs, BodyExporter? exporter = null, SpanExporter? spans = null, bool repeatRegistration = false)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Observability:Logging:ConsoleEnabled"] = "false", ["Sentry:Dsn"] = "", ["SENTRY_DSN"] = "", ["Logging:LogLevel:Microsoft"] = "Warning" });
        if (spans is not null)
            builder.Services.AddOpenTelemetry().WithTracing(tracing =>
                tracing.AddProcessor(new SimpleActivityExportProcessor(spans)));
        builder.AddOpenObservability();
        if (repeatRegistration)
            builder.AddOpenObservability();
        builder.Logging.AddProvider(logs);
        if (exporter is not null)
            builder.Services.AddOpenTelemetry().WithLogging(logging =>
                logging.AddProcessor(new SimpleLogRecordExportProcessor(exporter)));
        builder.Services.AddHttpClient("slow", client => client.Timeout = TimeSpan.FromSeconds(75));
        return builder.Build();
    }

    private sealed class SpanExporter : BaseExporter<Activity>
    {
        internal sealed record ExportedSpan(ActivityKind Kind, ActivityTraceId TraceId, ActivitySpanId SpanId,
            string DisplayName, KeyValuePair<string, object?>[] Tags, ActivityStatusCode Status);
        internal ConcurrentQueue<ExportedSpan> Spans { get; } = new();
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var span in batch)
                Spans.Enqueue(new ExportedSpan(span.Kind, span.TraceId, span.SpanId, span.DisplayName, span.TagObjects.ToArray(), span.Status));
            return ExportResult.Success;
        }
    }

    private sealed class BodyExporter : BaseExporter<LogRecord>
    {
        internal List<string> TraceIds { get; } = [];
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
                if (record.EventId.Name == "http.client.response.body")
                    TraceIds.Add(record.TraceId.ToString());
            return ExportResult.Success;
        }
    }

    private sealed class Collector : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        internal ConcurrentQueue<Dictionary<string, object?>> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public void Dispose() { }

        private sealed class Logger(Collector owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var fields = new Dictionary<string, object?>();
                if (state is IEnumerable<KeyValuePair<string, object?>> stateValues)
                    foreach (var field in stateValues)
                        fields[field.Key] = field.Value;
                owner._scopes.ForEachScope((scope, attributes) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> values)
                        foreach (var value in values)
                            attributes[value.Key] = value.Value;
                }, fields);
                fields["formatted_message"] = formatter(state, exception);
                owner.Entries.Enqueue(fields);
            }
        }
    }
}
