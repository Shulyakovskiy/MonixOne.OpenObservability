using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Routing;

namespace MonixOne.OpenObservability;

/// <summary>
/// Adds route templates to the standard logger
/// without exposing raw paths or query strings.
/// </summary>
internal sealed class ServiceHttpLoggingInterceptor : IHttpLoggingInterceptor
{
    public ValueTask OnRequestAsync(HttpLoggingInterceptorContext context)
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/api/health"))
        {
            context.LoggingFields = HttpLoggingFields.None;
            return ValueTask.CompletedTask;
        }

        context.Disable(HttpLoggingFields.RequestPath | HttpLoggingFields.RequestQuery);
        context.AddParameter("http_route",
            (context.HttpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "{unmatched}");
        context.AddParameter("http_method", context.HttpContext.Request.Method);
        context.AddParameter("http.request.header.content-type", context.HttpContext.Request.ContentType);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnResponseAsync(HttpLoggingInterceptorContext context)
    {
        if (context.LoggingFields == HttpLoggingFields.None)
            return ValueTask.CompletedTask;

        context.AddParameter("http_status_code", context.HttpContext.Response.StatusCode);
        context.AddParameter("http.response.header.content-type", context.HttpContext.Response.ContentType);
        return ValueTask.CompletedTask;
    }
}
