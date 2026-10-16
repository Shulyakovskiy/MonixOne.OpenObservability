namespace MonixOne.OpenObservability;

/// <summary>
/// Stable attribute names exported to the observability backend.
/// </summary>
public static class LogProperties
{
    public const string CorrelationHeader = "x-correlation-id";
    public const string ServiceName = "service_name";
    public const string ServiceVersion = "service_version";
    public const string Environment = "environment";
    public const string TraceId = "trace_id";
    public const string SpanId = "span_id";
    public const string SourceContext = "source_context";
    public const string Level = "level";
    public const string EventName = "event_name";
    public const string Operation = "operation";
    public const string EntityType = "entity_type";
    public const string EntityId = "entity_id";
    public const string EntityVersion = "entity_version";
    public const string ErrorCode = "error_code";
    public const string ExceptionType = "exception_type";
    public const string ExceptionMessage = "exception_message";
    public const string ExceptionStackTrace = "exception_stack_trace";
}
