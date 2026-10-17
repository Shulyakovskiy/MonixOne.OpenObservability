using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Sentry;

namespace MonixOne.OpenObservability;

/// <summary>
/// Connects the package's correlation and standard HTTP logging middleware.
/// Register after routing and before application error handling and authentication.
/// </summary>
public static class HttpPipelineExtensions
{
    public static IApplicationBuilder UseOpenObservability(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<CorrelationMiddleware>();
        return app.UseHttpLogging();
    }
}

/// <summary>
/// Uses the platform Activity trace for responses, structured logs and Sentry.
/// Creates a fallback activity only when the hosting pipeline has none.
/// </summary>
internal sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        var activity = Activity.Current;
        var ownsActivity = activity is null;
        if (ownsActivity)
            activity = new Activity("http.request").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var sentryScope = SentrySdk.PushScope();
        try
        {
            var traceId = activity!.TraceId.ToString();
            context.TraceIdentifier = traceId;
            context.Items[LogProperties.TraceId] = traceId;
            context.Response.Headers[LogProperties.CorrelationHeader] = traceId;
            SentrySdk.ConfigureScope(scope =>
            {
                scope.ClearBreadcrumbs();
                scope.SetTag(LogProperties.TraceId, traceId);
                scope.SetTag(LogProperties.SpanId, activity.SpanId.ToString());
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "{unmatched}";
                scope.SetTag("endpoint", route);
                scope.SetTag("http_route", route);
                scope.SetTag("http_method", context.Request.Method);
            });
            using var scope = logger.BeginScope(new Dictionary<string, object?> { [LogProperties.TraceId] = traceId });
            await next(context);
        }
        finally
        {
            if (ownsActivity)
                activity!.Stop();
        }
    }
}
