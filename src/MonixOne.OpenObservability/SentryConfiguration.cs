using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry.Extensions.Logging;
using Sentry.Extensibility;

namespace MonixOne.OpenObservability;

/// <summary>
/// Configures the official SDK once for web applications and generic hosts.
/// ILogger errors arrive with the same sanitized diagnostics as the OTLP records.
/// </summary>
internal static class SentryConfiguration
{
    private static readonly AsyncLocal<LogContext?> _currentLog = new();

    internal sealed record LogContext(Exception? Exception, EventId EventId,
        IReadOnlyList<KeyValuePair<string, object?>>? Attributes);

    internal static LogContext? CurrentLog
    {
        get => _currentLog.Value;
        set => _currentLog.Value = value;
    }

    internal static void AddSentryTelemetry(this IHostApplicationBuilder builder)
    {
        var dsn = builder.Configuration["Sentry:Dsn"];
        if (string.IsNullOrWhiteSpace(dsn))
            dsn = builder.Configuration["SENTRY_DSN"];
        if (string.IsNullOrWhiteSpace(dsn))
            return;
        if (builder is WebApplicationBuilder web)
            web.WebHost.UseSentry(options =>
            {
                Configure(options);
                options.MaxRequestBodySize = RequestSize.None;
            });
        else
            builder.Logging.AddSentry(Configure);

        void Configure(SentryLoggingOptions options)
        {
            builder.Configuration.GetSection("Sentry").Bind(options);
            options.Dsn = dsn;
            options.Environment = builder.Environment.EnvironmentName;
            options.SendDefaultPii = false;
            options.MaxBreadcrumbs = 30;
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
            options.SetBeforeBreadcrumb(SanitizeBreadcrumb);
            options.SetBeforeSend(entry =>
            {
                if (entry.Exception is not null || CurrentLog is not { Exception: { } loggedException } log)
                    return SanitizeEvent(entry);

                // The provider receives sanitized ILogger state and no raw exception.
                // Let the SDK build its native exception interface only inside its send callback.
                var logFields = entry.Extra.ToArray();
                var logTags = entry.Tags.ToArray();
                foreach (var processor in options.GetAllExceptionProcessors())
                    processor.Process(loggedException, entry);
                SanitizeEvent(entry, loggedException, log.Attributes);
                foreach (var field in logFields)
                    entry.SetExtra(field.Key, field.Value);
                foreach (var tag in logTags)
                    entry.SetTag(tag.Key, tag.Value);
                SanitizeRequest(entry);
                return entry;
            });
            options.SetBeforeSendTransaction(SanitizeTransaction);
        }
    }

    internal static SentryEvent SanitizeEvent(SentryEvent entry, Exception? loggedException = null,
        IReadOnlyList<KeyValuePair<string, object?>>? logAttributes = null)
    {
        entry.User = new SentryUser();
        foreach (var error in entry.SentryExceptions ?? [])
        {
            error.Value = "[omitted: exception_message]";
            if (error.Mechanism is { } mechanism)
            {
                mechanism.Data.Clear();
                mechanism.Description = null;
                mechanism.HelpLink = null;
            }
            if (error.Stacktrace is { } stack)
                foreach (var frame in stack.Frames)
                    frame.Vars?.Clear();
        }
        if ((entry.Exception ?? loggedException) is { } exception)
        {
            // The SDK also captures unhandled exceptions outside ILogger.
            // Discard arbitrary Exception.Data and restore only safe diagnostics.
            foreach (var key in entry.Extra.Keys.ToArray())
                entry.SetExtra(key, null);
            // The SDK recognizes reserved Exception.Data keys as tags and contexts.
            // Remove those projections as well as the raw mechanism payload.
            foreach (var error in ExceptionDiagnosticsExtensions.EnumerateExceptions(exception))
                foreach (var key in error.Data.Keys)
                {
                    var name = key?.ToString();
                    if (name?.StartsWith("sentry:tag:", StringComparison.OrdinalIgnoreCase) == true)
                        entry.UnsetTag(name["sentry:tag:".Length..]);
                    if (name?.StartsWith("sentry:context:", StringComparison.OrdinalIgnoreCase) == true)
                        entry.Contexts.Remove(name["sentry:context:".Length..]);
                }
            foreach (var attribute in ExceptionDiagnosticsExtensions.CreateAttributes(exception))
                entry.SetExtra(attribute.Key,
                    logAttributes?.LastOrDefault(field => field.Key == attribute.Key).Value ?? attribute.Value);
        }
        SanitizeRequest(entry);
        return entry;
    }

    private static void SanitizeRequest(SentryEvent entry)
    {
        entry.Request = new SentryRequest
        {
            Method = entry.Tags.GetValueOrDefault("http_method") ?? entry.Request.Method
        };
        if (entry.Tags.TryGetValue("http_route", out var route))
            entry.Request.Other["route"] = route;
    }

    private static Breadcrumb? SanitizeBreadcrumb(Breadcrumb breadcrumb)
    {
        // SDK scope cloning invokes this hook again for breadcrumbs already sanitized here.
        var log = breadcrumb.Category == "diagnostics" ? null : CurrentLog;
        if (log?.EventId.Name is "http.client.request.body" or "http.client.response.body")
            return null;

        var data = new Dictionary<string, string>();
        if (log is not null)
        {
            foreach (var attribute in log.Attributes ?? [])
                Add(attribute.Key == "Duration" ? "duration_ms" : attribute.Key, attribute.Value);
            if (!string.IsNullOrWhiteSpace(log.EventId.Name))
                Add("event_name", log.EventId.Name == "RequestResponseLog" ? "http.request.completed" : log.EventId.Name);
            if (Activity.Current is { } activity)
            {
                Add("trace_id", activity.TraceId.ToString());
                Add("span_id", activity.SpanId.ToString());
            }
        }
        else if (breadcrumb.Data is { } fields)
            foreach (var field in fields)
                Add(field.Key, field.Value);

        // Correlation alone is not a useful step. Never retain formatted messages or raw SDK HTTP data.
        if (!data.Keys.Any(key => key is not "trace_id" and not "span_id"))
            return null;
        // Breadcrumb is immutable; the SDK's JSON factory preserves its original timestamp.
        return Breadcrumb.FromJson(JsonSerializer.SerializeToElement(new
        {
            timestamp = breadcrumb.Timestamp,
            message = data.GetValueOrDefault("event_name") ?? "diagnostic.step",
            type = "default",
            data,
            category = "diagnostics",
            level = breadcrumb.Level.ToString().ToUpperInvariant()
        }));

        void Add(string key, object? value)
        {
            if (!IsBreadcrumbField(key) || value is not (string or bool or int or long or double or float or decimal))
                return;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= 256)
                data[key] = text;
        }
    }

    private static bool IsBreadcrumbField(string key) => key is
        "event_name" or "operation" or "error_code" or "failure_kind" or "http_request_error" or "socket_error_code"
        or "exception.type" or "json_error_line" or "json_error_byte_position"
        or "http_method" or "http_route" or "http_status_code" or "http_response_status_code_at_log" or "http_response_started"
        or "dependency_address" or "dependency_port" or "dependency_http_method" or "dependency_http_route" or "dependency_client"
        or "upstream_status_code" or "duration_ms" or "retry_attempt" or "retry_delay_ms" or "timeout_ms"
        or "cancellation_source" or "circuit_state" or "trace_id" or "span_id";

    internal static SentryTransaction SanitizeTransaction(SentryTransaction transaction)
    {
        transaction.Request = new SentryRequest();
        transaction.User = new SentryUser();
        if (transaction.NameSource == TransactionNameSource.Url)
            ((IEventLike)transaction).TransactionName = transaction.Operation;
        if (transaction.Operation.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            transaction.Description = transaction.Operation;
        foreach (var key in HttpTraceEnrichment.RawUrlFields)
        {
            if (transaction.Data.ContainsKey(key))
                transaction.SetData(key, null);
            transaction.UnsetTag(key);
        }
        foreach (var span in transaction.Spans.Where(span => span.Operation.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
        {
            span.Description = span.Operation;
            foreach (var key in HttpTraceEnrichment.RawUrlFields)
            {
                if (span.Data.ContainsKey(key))
                    span.SetData(key, null);
                span.UnsetTag(key);
            }
        }
        return transaction;
    }
}
