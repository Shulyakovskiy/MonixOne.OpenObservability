using Microsoft.Extensions.Logging;

namespace MonixOne.OpenObservability;

/// <summary>
/// Small additions to the standard <see cref="ILogger"/> API for stable business events and operation scopes.
/// </summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Adds operation and optional entity identity to all logs written inside the returned scope.
    /// </summary>
    public static IDisposable? BeginOperation(
        this ILogger logger,
        string operation,
        string? entityType = null,
        object? entityId = null,
        object? entityVersion = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var scope = new List<KeyValuePair<string, object?>>(4)
        {
            new(LogProperties.Operation, operation)
        };

        if (!string.IsNullOrWhiteSpace(entityType))
            scope.Add(new KeyValuePair<string, object?>(LogProperties.EntityType, entityType));

        if (entityId is not null)
            scope.Add(new KeyValuePair<string, object?>(LogProperties.EntityId, entityId));

        if (entityVersion is not null)
            scope.Add(new KeyValuePair<string, object?>(LogProperties.EntityVersion, entityVersion));

        return logger.BeginScope(scope);
    }

    /// <summary>
    /// Writes a business event whose stable name is exported as <c>event_name</c>.
    /// </summary>
    public static void LogEvent(
        this ILogger logger,
        LogLevel logLevel,
        string eventName,
        string message,
        params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        logger.Log(logLevel, new EventId(0, eventName), message, args);
    }

    /// <summary>
    /// Writes a business event associated with an exception.
    /// </summary>
    public static void LogEvent(
        this ILogger logger,
        LogLevel logLevel,
        string eventName,
        Exception exception,
        string message,
        params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        logger.Log(logLevel, new EventId(0, eventName), exception, message, args);
    }
}
