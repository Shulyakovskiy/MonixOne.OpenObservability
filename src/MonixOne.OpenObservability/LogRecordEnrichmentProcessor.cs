using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace MonixOne.OpenObservability;

/// <summary>
/// Adds the stable, backend-neutral log attributes missing from the OpenTelemetry log record model.
/// </summary>
internal sealed class LogRecordEnrichmentProcessor(ServiceObservabilityOptions options)
    : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        var attributes = data.Attributes?.ToList() ?? [];

        Set(attributes, LogProperties.ServiceName, options.ServiceName);
        Set(attributes, LogProperties.ServiceVersion, options.ServiceVersion);
        Set(attributes, LogProperties.Environment, options.EnvironmentName);
        Set(attributes, LogProperties.SourceContext, data.CategoryName);
        Set(attributes, LogProperties.Level, data.LogLevel.ToString());

        if (!string.IsNullOrWhiteSpace(data.EventId.Name))
            Set(attributes, LogProperties.EventName,
                data.CategoryName == "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"
                    && data.EventId.Name == "RequestResponseLog"
                    ? "http.request.completed"
                    : data.EventId.Name);

        if (data.CategoryName == "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"
            && attributes.FirstOrDefault(attribute => attribute.Key == "Duration").Value is { } duration)
            Set(attributes, "duration_ms", duration);

        // A streamed response may be consumed after its originating activity stopped.
        // Keep the body event attached to the request rather than the reader's current trace.
        if (data.CategoryName == typeof(HttpBodyLoggingHandler).FullName)
        {
            data.TraceId = default;
            data.SpanId = default;
            data.TraceFlags = default;
            if (attributes.FirstOrDefault(attribute => attribute.Key == LogProperties.TraceId).Value is string traceId)
                data.TraceId = ActivityTraceId.CreateFromString(traceId.AsSpan());
            if (attributes.FirstOrDefault(attribute => attribute.Key == LogProperties.SpanId).Value is string spanId)
                data.SpanId = ActivitySpanId.CreateFromString(spanId.AsSpan());
            if (attributes.FirstOrDefault(attribute => attribute.Key == "trace_flags").Value is int flags)
                data.TraceFlags = (ActivityTraceFlags)flags;
        }

        if (data.TraceId != default)
            Set(attributes, LogProperties.TraceId, data.TraceId.ToString());

        if (data.SpanId != default)
            Set(attributes, LogProperties.SpanId, data.SpanId.ToString());

        data.Attributes = attributes;
    }

    static void Set(
        List<KeyValuePair<string, object?>> attributes,
        string key,
        object? value)
    {
        var index = attributes.FindIndex(attribute => attribute.Key == key);
        var attribute = new KeyValuePair<string, object?>(key, value);

        if (index >= 0)
            attributes[index] = attribute;
        else
            attributes.Add(attribute);
    }
}
