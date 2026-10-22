using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Sentry;

namespace MonixOne.OpenObservability;

/// <summary>
/// Passes sanitized HTTP bodies and automatic exception diagnostics to every provider.
/// HTTP capture, filtering, scopes and export remain owned by the existing loggers.
/// </summary>
internal sealed class RedactingLoggerFactory(ILoggerFactory inner, HttpBodySanitizer sanitizer,
    IHttpContextAccessor httpContextAccessor) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName)
    {
        var logger = inner.CreateLogger(categoryName);
        var httpLogger = categoryName == "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"
                         || categoryName.StartsWith("Microsoft.Extensions.Http.Logging.", StringComparison.Ordinal);
        var hostingLogger = categoryName == "Microsoft.AspNetCore.Hosting.Diagnostics";
        return new RedactingLogger(logger, sanitizer, httpLogger, hostingLogger, httpContextAccessor);
    }

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
    public void Dispose() => inner.Dispose();

    private sealed class RedactingLogger(ILogger innerLogger, HttpBodySanitizer bodySanitizer,
        bool httpLogger, bool hostingLogger, IHttpContextAccessor accessor) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (!hostingLogger || state is not IEnumerable<KeyValuePair<string, object?>> fields)
                return innerLogger.BeginScope(state);

            // Hosting scopes start before routing and carry the raw RequestPath.
            // The HTTP completion record provides the useful route template separately.
            return innerLogger.BeginScope(fields.Select(field => IsRawUrlField(field.Key)
                ? new KeyValuePair<string, object?>(field.Key, "[omitted: url]") : field).ToList());
        }
        public bool IsEnabled(LogLevel logLevel) => innerLogger.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var attributes = state as IReadOnlyList<KeyValuePair<string, object?>>
                             ?? (state as IEnumerable<KeyValuePair<string, object?>>)?.ToList();
            var hasBody = httpLogger && attributes?.Any(attribute => IsBody(attribute.Key)) == true;
            var hasRawUrl = hostingLogger && attributes?.Any(attribute => IsRawUrlField(attribute.Key)) == true;
            if (!hasBody && !hasRawUrl && exception is null)
            {
                WriteLog(logLevel, eventId, state, formatter, null, attributes);
                return;
            }

            var safeState = attributes?.ToList() ?? [];
            if (exception is not null)
            {
                var diagnostics = ExceptionDiagnosticsExtensions.CreateAttributes(exception,
                    accessor.HttpContext?.RequestAborted ?? default);
                if (accessor.HttpContext is { } context)
                {
                    diagnostics.Add(new("http_method", context.Request.Method));
                    diagnostics.Add(new("http_route", (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "{unmatched}"));
                    // The exception handler may still change the response after this log.
                    diagnostics.Add(new("http_response_status_code_at_log", context.Response.StatusCode));
                    diagnostics.Add(new("http_response_started", context.Response.HasStarted));
                }
                foreach (var diagnostic in diagnostics)
                {
                    safeState.RemoveAll(attribute => attribute.Key == diagnostic.Key);
                    safeState.Add(diagnostic);
                }
            }

            for (var index = 0; index < safeState.Count; index++)
            {
                var attribute = safeState[index];
                if (hasRawUrl && IsRawUrlField(attribute.Key))
                {
                    safeState[index] = new KeyValuePair<string, object?>(attribute.Key, "[omitted: url]");
                    continue;
                }
                if (!hasBody || !IsBody(attribute.Key))
                    continue;
                var contentType = GetContentType(attributes!, attribute.Key);
                safeState[index] = new KeyValuePair<string, object?>(attribute.Key,
                    attribute.Value is string body
                        ? bodySanitizer.Sanitize(body, contentType)
                        : "[omitted: unsupported_body_state]");
            }

            // The original formatter closes over raw HTTP state in some platform loggers.
            // Build text from the sanitized state so console and OTLP cannot diverge.
            var message = hasBody || hasRawUrl
                ? string.Join(Environment.NewLine, safeState.Where(value => value.Key != "{OriginalFormat}")
                    .Select(value => $"{value.Key}: {value.Value}"))
                : formatter(state, null);
            if (exception is not null && !hasBody)
                message += $" [{safeState.Last(value => value.Key == "failure_summary").Value}]"
                    + Environment.NewLine + safeState.Last(value => value.Key == "exception_stack_trace").Value;

            // ApiException.ToString() can include the raw HTTP response.
            // Providers receive type, classification and stacks instead of that object.
            WriteLog(logLevel, eventId, safeState, (_, _) => message, exception, safeState);
        }

        private void WriteLog<TState>(LogLevel logLevel, EventId eventId, TState state,
            Func<TState, Exception?, string> formatter, Exception? originalException,
            IReadOnlyList<KeyValuePair<string, object?>>? attributes)
        {
            if (!SentrySdk.IsEnabled)
            {
                innerLogger.Log(logLevel, eventId, state, exception: null, formatter);
                return;
            }
            var previous = SentryConfiguration.CurrentLog;
            try
            {
                SentryConfiguration.CurrentLog = new(originalException, eventId, attributes);
                innerLogger.Log(logLevel, eventId, state, exception: null, formatter);
            }
            finally { SentryConfiguration.CurrentLog = previous; }
        }

        private static bool IsBody(string key) =>
            key is "RequestBody" or "ResponseBody" or "http.request.body" or "http.response.body";

        private static bool IsRawUrlField(string key) =>
            key is "Path" or "PathBase" or "RequestPath" or "QueryString";

        private static string? GetContentType(IReadOnlyList<KeyValuePair<string, object?>> attributes, string bodyKey)
        {
            var header = bodyKey is "RequestBody" or "http.request.body"
                ? "http.request.header.content-type" : "http.response.header.content-type";
            return attributes.FirstOrDefault(attribute => attribute.Key == header).Value?.ToString();
        }
    }
}
