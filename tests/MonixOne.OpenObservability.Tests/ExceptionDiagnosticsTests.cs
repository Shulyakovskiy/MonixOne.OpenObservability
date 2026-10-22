using System.Net;
using System.Net.Sockets;
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
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://private-user:private-password@service.test:8443/session/private-id?token=private-query#private-fragment");
        request.Options.Set(new HttpRequestOptionsKey<string>(HttpRequestMessageOptions.RelativePathTemplate), "/session/{id}");
        request.Options.Set(new HttpRequestOptionsKey<Type>(HttpRequestMessageOptions.InterfaceType), typeof(ExceptionDiagnosticsTests));
        request.Headers.Add("Authorization", "Bearer private-authorization");
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
        entry.Attributes.ShouldContain(pair => pair.Key == "upstream_status_code" && Equals(pair.Value, 200));
        entry.Attributes.ShouldContain(pair => pair.Key == "dependency_address" && Equals(pair.Value, "service.test"));
        entry.Attributes.ShouldContain(pair => pair.Key == "dependency_port" && Equals(pair.Value, 8443));
        entry.Attributes.ShouldContain(pair => pair.Key == "dependency_http_route" && Equals(pair.Value, "/session/{id}"));
        entry.Attributes.ShouldContain(pair => pair.Key == "dependency_http_method" && Equals(pair.Value, "POST"));
        entry.Attributes.ShouldContain(pair => pair.Key == "dependency_client" && Equals(pair.Value, typeof(ExceptionDiagnosticsTests).FullName));
        entry.Attributes.ShouldContain(pair => pair.Key == "failure_kind" && Equals(pair.Value, "response_deserialization"));
        entry.Attributes.ShouldContain(pair => pair.Key == "exception_root_type"
            && Equals(pair.Value, nested ? nameof(FormatException) : nameof(JsonException)));
        entry.Attributes.ShouldContain(pair => pair.Key == "json_error_line" && Equals(pair.Value, 2L));
        entry.Attributes.ShouldContain(pair => pair.Key == "json_error_byte_position" && Equals(pair.Value, 17L));
        entry.Attributes.ShouldContain(pair => pair.Key == "failure_summary" && Equals(pair.Value,
            "HTTP response JSON error (HTTP 200)"));
        entry.Attributes.ShouldContain(pair => pair.Key == "request_id" && Equals(pair.Value, "visible-request"));
    }

    [Theory]
    [InlineData("timeout", "timeout", "Timeout")]
    [InlineData("canceled", "canceled", "Canceled: unknown")]
    [InlineData("http", "http_status", "HTTP response error: 503")]
    [InlineData("transport", "transport", "Transport error")]
    public void OrdinaryLogError_ClassifiesFailureWithoutExceptionMessages(string input, string expected, string summary)
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
        entry.Attributes.ShouldContain(pair => pair.Key == "failure_summary" && Equals(pair.Value, summary));
        entry.Text.Split(Environment.NewLine)[0].ShouldBe($"Operation failed [{summary}]");
    }

    [Theory]
    [InlineData(200)]
    [InlineData(409)]
    public void OrdinaryLogError_SeparatesResponseSnapshotFromUpstreamStatus(int status)
    {
        using var logs = new RecordingProvider();
        using var host = CreateHost(logs);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/users/private-user";
        context.Request.QueryString = new QueryString("?token=private-query");
        context.Response.StatusCode = status;
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/users/{id}"), 0, EndpointMetadataCollection.Empty, "users"));
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        try
        {
            host.Services.GetRequiredService<ILogger<ExceptionDiagnosticsTests>>()
                .LogWarning(new HttpRequestException("private-exception", null, HttpStatusCode.ServiceUnavailable), "Update rejected");
            var entry = logs.Entries.ShouldHaveSingleItem();
            entry.Attributes.ShouldContain(pair => pair.Key == "http_route" && Equals(pair.Value, "/users/{id}"));
            entry.Attributes.ShouldContain(pair => pair.Key == "http_response_status_code_at_log" && Equals(pair.Value, status));
            entry.Attributes.ShouldContain(pair => pair.Key == "http_response_started" && Equals(pair.Value, false));
            entry.Attributes.ShouldContain(pair => pair.Key == "upstream_status_code" && Equals(pair.Value, 503));
            entry.Attributes.ShouldNotContain(pair => pair.Key == "http_status_code");
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
        entry.Attributes.ShouldContain(pair => pair.Key == "failure_kind" && Equals(pair.Value, "json"));
        entry.Attributes.ShouldContain(pair => pair.Key == "json_error_line" && Equals(pair.Value, 3L));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonFailure_WithoutResponseEvidence_IsNeutral(bool incomingRequest)
    {
        var json = new JsonException("private-json", "$.private-path", 1, 2);
        Exception error = incomingRequest ? new BadHttpRequestException("private-request", 400, json) : json;
        var attributes = ExceptionDiagnosticsExtensions.CreateAttributes(error,
            TestContext.Current.CancellationToken).ToDictionary();

        attributes["failure_kind"].ShouldBe("json");
        attributes["json_error_line"].ShouldBe(1L);
        attributes.ShouldNotContain(pair => pair.Key == "json_error_path");
        attributes["upstream_status_code"].ShouldBeNull();
    }

    [Fact]
    public async Task AggregateFailure_DoesNotAssociateJsonWithAnUnrelatedHttpResponse()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://service.test/");
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway);
        var api = await ApiException.Create(request, request.Method, response, new RefitSettings());
        var error = new AggregateException(api, new JsonException("private-json"));

        ExceptionDiagnosticsExtensions.CreateAttributes(error,
            TestContext.Current.CancellationToken).ToDictionary()["failure_kind"].ShouldBe("json");
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.ResponseEnded)]
    public void TransportFailure_ReportsTypedReasonWithoutMessages(HttpRequestError reason)
    {
        var error = new HttpRequestException(reason, "private-message", new SocketException((int)SocketError.ConnectionRefused));
        var attributes = ExceptionDiagnosticsExtensions.CreateAttributes(error,
            TestContext.Current.CancellationToken).ToDictionary();

        attributes["failure_kind"].ShouldBe("transport");
        attributes["http_request_error"].ShouldBe(reason.ToString());
        attributes["socket_error_code"].ShouldBe("ConnectionRefused");
        attributes["exception.type"].ShouldBe(typeof(HttpRequestException).FullName);
        string.Join(" ", attributes.Values).ShouldNotContain("private-");
    }

    [Fact]
    public void StreamFailure_ReportsHttpIOExceptionReason()
    {
        var attributes = ExceptionDiagnosticsExtensions.CreateAttributes(
            new HttpIOException(HttpRequestError.ResponseEnded, "private-message"),
            TestContext.Current.CancellationToken).ToDictionary();

        attributes["failure_kind"].ShouldBe("transport");
        attributes["http_request_error"].ShouldBe("ResponseEnded");
    }

    [Fact]
    public void RefitTransportFailure_RetainsDependencyWithoutAResponseStatus()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://service.test/private-path?token=private-query");
        var error = new ApiRequestException(request, request.Method, new RefitSettings(),
            new HttpRequestException(HttpRequestError.NameResolutionError, "private-dns"));

        var attributes = ExceptionDiagnosticsExtensions.CreateAttributes(error,
            TestContext.Current.CancellationToken).ToDictionary();

        attributes["dependency_address"].ShouldBe("service.test");
        attributes["http_request_error"].ShouldBe("NameResolutionError");
        attributes["upstream_status_code"].ShouldBeNull();
        string.Join(" ", attributes.Values).ShouldNotContain("private-");
    }

    [Theory]
    [InlineData("request_aborted")]
    [InlineData("unknown")]
    public void CancellationSource_RequiresTheCanceledTokenToMatch(string source)
    {
        using var request = new CancellationTokenSource();
        using var unrelated = new CancellationTokenSource();
        request.Cancel();
        unrelated.Cancel();
        var token = source == "request_aborted" ? request.Token : unrelated.Token;

        var attributes = ExceptionDiagnosticsExtensions.CreateAttributes(
            new OperationCanceledException("private-cancellation", token), request.Token).ToDictionary();

        attributes["failure_kind"].ShouldBe("canceled");
        attributes["cancellation_source"].ShouldBe(source);
    }

    [Fact]
    public void HttpClientTimeout_WrappedInCancellation_IsATimeout()
    {
        var error = new TaskCanceledException("private-cancellation", new TimeoutException("private-timeout"));
        var attributes = ExceptionDiagnosticsExtensions.CreateAttributes(error,
            TestContext.Current.CancellationToken).ToDictionary();

        attributes["failure_kind"].ShouldBe("timeout");
        attributes["cancellation_source"].ShouldBe("timeout");
    }

    [Fact]
    public async Task MissingRefitResponseStatus_IsNotReportedAsZero()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://service.test/");
        using var response = new HttpResponseMessage((HttpStatusCode)0);
        var error = await ApiException.Create(request, request.Method, response, new RefitSettings());

        error.GetHttpStatusCode().ShouldBeNull();
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
