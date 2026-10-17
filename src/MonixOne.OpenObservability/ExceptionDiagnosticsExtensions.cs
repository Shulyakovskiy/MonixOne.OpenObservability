using System.Net.Sockets;
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
            if (current is ApiException apiException && (int)apiException.StatusCode is >= 100 and <= 599)
                return (int)apiException.StatusCode;
            if (current is HttpRequestException { StatusCode: { } status } && (int)status is >= 100 and <= 599)
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

    internal static List<KeyValuePair<string, object?>> CreateAttributes(
        Exception exception,
        CancellationToken requestAborted = default)
    {
        var chain = EnumerateExceptions(exception).ToList();

        // A JSON failure elsewhere in an aggregate is not evidence of a failed HTTP response.
        var jsonResponse = chain.OfType<ApiException>().FirstOrDefault(error =>
            EnumerateExceptions(error).Any(inner => inner is JsonException));
        var jsonError = jsonResponse is not null
            ? EnumerateExceptions(jsonResponse).OfType<JsonException>().First()
            : chain.OfType<JsonException>().FirstOrDefault();
        var status = (jsonResponse ?? exception).GetHttpStatusCode();
        var cancellation = chain.OfType<OperationCanceledException>().FirstOrDefault();
        var kind = jsonResponse is not null ? "response_deserialization"
            : jsonError is not null ? "json"
            : chain.Any(error => error is TimeoutException or TimeoutRejectedException) ? "timeout"
            : cancellation is not null ? "canceled"
            : status is >= 400 ? "http_status"
            : chain.Any(error => error is HttpRequestException or HttpIOException or SocketException) ? "transport" : "application";
        var requestError = chain.Select(error => error switch
        {
            HttpRequestException http => (HttpRequestError?)http.HttpRequestError,
            HttpIOException http => http.HttpRequestError,
            _ => null
        }).FirstOrDefault(error => error is not null and not HttpRequestError.Unknown);

        List<KeyValuePair<string, object?>> attributes =
        [
            new("exception_type", exception.GetType().Name),
            new("exception_inner_type", exception.InnerException?.GetType().Name),
            new("exception_root_type", exception.GetBaseException().GetType().Name),
            new("exception.type", exception.GetType().FullName),
            new("failure_kind", kind),
            new("status_code", status),
            new("upstream_status_code", status),
            new("http_request_error", requestError?.ToString()),
            new("socket_error_code", chain.OfType<SocketException>().FirstOrDefault()?.SocketErrorCode.ToString()),
            new("json_error_line", jsonError?.LineNumber),
            new("json_error_byte_position", jsonError?.BytePositionInLine),
            new("exception_stack_trace", string.Join(Environment.NewLine,
                chain.Select(error => $"{error.GetType().FullName}{Environment.NewLine}{error.StackTrace}")))
        ];
        if (kind is "timeout" or "canceled")
            attributes.Add(new("cancellation_source", kind == "timeout" ? "timeout"
                : requestAborted.IsCancellationRequested && cancellation!.CancellationToken == requestAborted ? "request_aborted"
                : "unknown"));
        if ((jsonResponse ?? chain.OfType<ApiExceptionBase>().FirstOrDefault()) is { } apiError)
            attributes.AddRange(ServiceHttpClientLogEnricher.CreateRequestAttributes(apiError.RequestMessage));
        return attributes;
    }
}
