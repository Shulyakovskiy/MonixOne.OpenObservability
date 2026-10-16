using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonixOne.OpenObservability;
using Polly.Timeout;
using Refit;
using Shouldly;
using Xunit;

namespace MonixOne.OpenObservability.Tests;

public sealed class ExceptionDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryLogError_EnrichesHttp200JsonFailureAndNeverPassesRawExceptionToProviders(bool nested)
    {
        using var logs = new RecordingProvider();
        using var host = CreateHost(logs);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://service.test/session");
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"access_token\":\"private-response-token\"}") };
        var exception = await ApiException.Create("private-refit-message", request, HttpMethod.Post,
            response, new RefitSettings(), new JsonException("private-json-message", "$.private-path", 2, 17,
                nested ? new FormatException("private-format-message") : null));
        var logger = host.Services.GetRequiredService<ILogger<ExceptionDiagnosticsTests>>();

        logger.LogError(exception, "Operation failed for {request_id}", "visible-request");

        var entry = logs.Entries.ShouldHaveSingleItem();
        entry.Exception.ShouldBeNull();
        entry.Text.ShouldNotContain("private-");
        entry.Attributes.ShouldContain(pair => pair.Key == "status_code" && Equals(pair.Value, 200));
        entry.Attributes.ShouldContain(pair => pair.Key == "failure_kind" && Equals(pair.Value, "response_deserialization"));
        entry.Attributes.ShouldContain(pair => pair.Key == "exception_root_type"
            && Equals(pair.Value, nested ? nameof(FormatException) : nameof(JsonException)));
        entry.Attributes.ShouldContain(pair => pair.Key == "json_error_line" && Equals(pair.Value, 2L));
        entry.Attributes.ShouldContain(pair => pair.Key == "json_error_byte_position" && Equals(pair.Value, 17L));
        entry.Attributes.ShouldContain(pair => pair.Key == "request_id" && Equals(pair.Value, "visible-request"));
    }

    [Theory]
    [InlineData("timeout", "timeout")]
    [InlineData("canceled", "canceled")]
    [InlineData("http", "http_status")]
    [InlineData("transport", "transport")]
    public void OrdinaryLogError_ClassifiesFailureWithoutExceptionMessages(string input, string expected)
    {
        Exception exception = input switch
        {
            "timeout" => new TimeoutRejectedException("private-timeout-message"),
            "canceled" => new OperationCanceledException("private-cancellation-message"),
            "http" => new HttpRequestException("private-http-message", null, HttpStatusCode.ServiceUnavailable),
            _ => new HttpRequestException("private-transport-message")
        };
        using var logs = new RecordingProvider();
        using var host = CreateHost(logs);

        host.Services.GetRequiredService<ILogger<ExceptionDiagnosticsTests>>()
            .LogWarning(exception, "Operation failed");

        var entry = logs.Entries.ShouldHaveSingleItem();
        entry.Exception.ShouldBeNull();
        entry.Text.ShouldNotContain("private-");
        entry.Attributes.ShouldContain(pair => pair.Key == "failure_kind" && Equals(pair.Value, expected));
    }

    [Fact]
    public void OrdinaryLogError_AddsCurrentHttpRouteAndFinalStatusWithoutManualScope()
    {
        using var logs = new RecordingProvider();
        using var host = CreateHost(logs);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/users/private-user";
        context.Request.QueryString = new QueryString("?token=private-query");
        context.Response.StatusCode = 409;
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/users/{id}"), 0, EndpointMetadataCollection.Empty, "users"));
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        try
        {
            host.Services.GetRequiredService<ILogger<ExceptionDiagnosticsTests>>()
                .LogWarning(new InvalidOperationException("private-exception"), "Update rejected");
            var entry = logs.Entries.ShouldHaveSingleItem();
            entry.Attributes.ShouldContain(pair => pair.Key == "http_route" && Equals(pair.Value, "/users/{id}"));
            entry.Attributes.ShouldContain(pair => pair.Key == "http_status_code" && Equals(pair.Value, 409));
            entry.Text.ShouldNotContain("private-");
        }
        finally { host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = null; }
    }

    [Fact]
    public void BeginExceptionDiagnostics_WorksWithoutHostRegistration()
    {
        using var logs = new RecordingProvider();
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        var logger = factory.CreateLogger("standalone");
        using (logger.BeginExceptionDiagnostics(new HttpRequestException("private-message", null, HttpStatusCode.BadGateway)))
            logger.LogWarning("Operation failed");

        var entry = logs.Entries.ShouldHaveSingleItem();
        entry.Text.ShouldNotContain("private-message");
        entry.Attributes.ShouldContain(pair => pair.Key == "status_code" && Equals(pair.Value, 502));
    }

    [Fact]
    public void Registration_PreservesAnExistingProvider()
    {
        using var logs = new RecordingProvider();
        var builder = CreateBuilder();
        builder.Logging.AddProvider(logs);
        builder.AddOpenObservability();
        using var host = builder.Build();

        host.Services.GetRequiredService<ILogger<ExceptionDiagnosticsTests>>()
            .LogError(new InvalidOperationException("private-error"), "Operation failed");

        logs.Entries.ShouldHaveSingleItem().Text.ShouldNotContain("private-");
    }

    [Fact]
    public void Registration_Twice_EnrichesEachExceptionOnce()
    {
        using var logs = new RecordingProvider();
        var builder = CreateBuilder();
        builder.AddOpenObservability();
        builder.AddOpenObservability();
        builder.Logging.AddProvider(logs);
        using var host = builder.Build();

        host.Services.GetRequiredService<ILogger<ExceptionDiagnosticsTests>>()
            .LogError(new InvalidOperationException("private-error"), "Operation failed");

        var entry = logs.Entries.ShouldHaveSingleItem();
        entry.Attributes.Count(pair => pair.Key == "exception_type").ShouldBe(1);
    }

    [Fact]
    public void AggregateFailure_FindsDiagnosticsInOtherBranches()
    {
        using var logs = new RecordingProvider();
        using var host = CreateHost(logs);
        var error = new AggregateException(new InvalidOperationException("private-first"),
            new JsonException("private-json", "$.private-path", 3, 7));

        host.Services.GetRequiredService<ILogger<ExceptionDiagnosticsTests>>()
            .LogError(error, "Parallel operations failed");

        var entry = logs.Entries.ShouldHaveSingleItem();
        entry.Text.ShouldNotContain("private-");
        entry.Attributes.ShouldContain(pair => pair.Key == "failure_kind" && Equals(pair.Value, "response_deserialization"));
        entry.Attributes.ShouldContain(pair => pair.Key == "json_error_line" && Equals(pair.Value, 3L));
    }

    [Fact]
    public void DictionaryLogState_KeepsStructuredFieldsWithExceptionDiagnostics()
    {
        using var logs = new RecordingProvider();
        using var host = CreateHost(logs);
        var state = new Dictionary<string, object?>
        {
            ["operation_id"] = "visible-operation",
            ["exception_type"] = "stale-type"
        };

        host.Services.GetRequiredService<ILogger<ExceptionDiagnosticsTests>>()
            .Log(LogLevel.Error, new EventId(0, "operation.failed"), state,
                new InvalidOperationException("private-error"), (_, _) => "Operation failed");

        var entry = logs.Entries.ShouldHaveSingleItem();
        entry.Attributes.ShouldContain(pair => pair.Key == "operation_id" && Equals(pair.Value, "visible-operation"));
        entry.Attributes.Where(pair => pair.Key == "exception_type").ShouldHaveSingleItem()
            .Value.ShouldBe(nameof(InvalidOperationException));
        entry.Text.ShouldNotContain("private-");
    }

    private static HostApplicationBuilder CreateBuilder()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        { DisableDefaults = true, EnvironmentName = "Tests" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Observability:Logging:ConsoleEnabled"] = "false", ["Sentry:Dsn"] = "", ["SENTRY_DSN"] = "" });
        return builder;
    }

    private static IHost CreateHost(RecordingProvider logs)
    {
        var builder = CreateBuilder();
        builder.AddOpenObservability();
        builder.Logging.AddProvider(logs);
        return builder.Build();
    }

    private sealed class RecordingProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public List<Entry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Recorder(this);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public void Dispose() { }

        public sealed record Entry(List<KeyValuePair<string, object?>> Attributes, string Message, Exception? Exception)
        {
            public string Text => Message + string.Join(" ", Attributes.Select(pair => $"{pair.Key}={pair.Value}"));
        }

        private sealed class Recorder(RecordingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var attributes = (state as IEnumerable<KeyValuePair<string, object?>>)?.ToList() ?? [];
                owner._scopes.ForEachScope((scope, fields) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> values)
                        fields.AddRange(values);
                }, attributes);
                owner.Entries.Add(new Entry(attributes, formatter(state, exception), exception));
            }
        }
    }
}
