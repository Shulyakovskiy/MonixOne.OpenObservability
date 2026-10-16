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
    private static readonly AsyncLocal<Exception?> _loggedException = new();

    internal static Exception? LoggedException
    {
        get => _loggedException.Value;
        set => _loggedException.Value = value;
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
            options.MaxBreadcrumbs = 0;
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
            options.SetBeforeSend(entry =>
            {
                if (entry.Exception is not null || LoggedException is not { } loggedException)
                    return SanitizeEvent(entry);

                // The provider receives sanitized ILogger state and no raw exception.
                // Let the SDK build its native exception interface only inside its send callback.
                var logFields = entry.Extra.ToArray();
                var logTags = entry.Tags.ToArray();
                foreach (var processor in options.GetAllExceptionProcessors())
                    processor.Process(loggedException, entry);
                SanitizeEvent(entry, loggedException);
                foreach (var field in logFields)
                    entry.SetExtra(field.Key, field.Value);
                foreach (var tag in logTags)
                    entry.SetTag(tag.Key, tag.Value);
                return entry;
            });
            options.SetBeforeSendTransaction(SanitizeTransaction);
        }
    }

    internal static SentryEvent SanitizeEvent(SentryEvent entry, Exception? loggedException = null)
    {
        entry.Request = new SentryRequest();
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
                entry.SetExtra(attribute.Key, attribute.Value);
        }
        return entry;
    }

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
