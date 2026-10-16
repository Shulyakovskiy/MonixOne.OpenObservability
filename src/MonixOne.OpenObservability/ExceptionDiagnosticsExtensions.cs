using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly.Timeout;
using Refit;

namespace MonixOne.OpenObservability;

/// <summary>
/// Adds reusable exception diagnostics without recording exception messages,
/// HTTP payloads or request headers carried by client exceptions.
/// </summary>
public static class ExceptionDiagnosticsExtensions
{
    /// <summary>
    /// Adds exception diagnostics to a scope when the exception itself
    /// is not passed to ILogger. Ordinary exception logging is enriched automatically.
    /// </summary>
    public static IDisposable? BeginExceptionDiagnostics(this ILogger logger, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exception);
        return logger.BeginScope(CreateAttributes(exception));
    }

    /// <summary>
    /// Reads an HTTP status from the exception chain
    /// without accessing response bodies or exception messages.
    /// </summary>
    public static int? GetHttpStatusCode(this Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        foreach (var current in EnumerateExceptions(exception))
        {
            if (current is ApiException apiException)
                return (int)apiException.StatusCode;
            if (current is HttpRequestException { StatusCode: { } status })
                return (int)status;
        }
        return null;
    }

    internal static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
                continue;
            yield return current;
            if (current is AggregateException aggregate)
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                    pending.Push(aggregate.InnerExceptions[index]);
            else if (current.InnerException is { } inner)
                pending.Push(inner);
        }
    }

    internal static List<KeyValuePair<string, object?>> CreateAttributes(Exception exception)
    {
        var chain = EnumerateExceptions(exception).ToList();

        var jsonError = chain.OfType<JsonException>().FirstOrDefault();
        var status = exception.GetHttpStatusCode();
        var kind = jsonError is not null ? "response_deserialization"
            : chain.Any(error => error is TimeoutException or TimeoutRejectedException) ? "timeout"
            : chain.Any(error => error is OperationCanceledException) ? "canceled"
            : status is >= 400 ? "http_status"
            : chain.Any(error => error is HttpRequestException) ? "transport" : "application";

        return
        [
            new("exception_type", exception.GetType().Name),
            new("exception_inner_type", exception.InnerException?.GetType().Name),
            new("exception_root_type", exception.GetBaseException().GetType().Name),
            new("failure_kind", kind),
            new("status_code", status),
            new("json_error_line", jsonError?.LineNumber),
            new("json_error_byte_position", jsonError?.BytePositionInLine),
            new("exception_stack_trace", string.Join(Environment.NewLine,
                chain.Select(error => $"{error.GetType().FullName}{Environment.NewLine}{error.StackTrace}")))
        ];
    }
}
