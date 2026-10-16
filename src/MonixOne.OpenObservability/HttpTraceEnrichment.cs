using System.Diagnostics;
using Refit;

namespace MonixOne.OpenObservability;

/// <summary>
/// Keeps the SDK's HTTP spans correlated with passive body logs.
/// Exports route templates instead of raw URLs and path/query parameter values.
/// </summary>
internal static class HttpTraceEnrichment
{
    internal static readonly string[] RawUrlFields =
        ["url", "url.full", "url.path", "url.query", "http.url", "http.target", "http.query"];

    internal static void EnrichClientRequest(Activity activity, HttpRequestMessage request)
    {
        request.Options.Set(HttpBodyLoggingHandler.ActivityKey, activity);
        if (request.Options.TryGetValue(new HttpRequestOptionsKey<string>(HttpRequestMessageOptions.RelativePathTemplate), out var route))
            activity.SetTag("http.route", route);
    }

    internal static void Sanitize(Activity activity)
    {
        foreach (var key in RawUrlFields)
            activity.SetTag(key, null);
        activity.SetStatus(activity.Status);
        if (activity.GetTagItem("http.request.method") is string method)
            activity.DisplayName = activity.GetTagItem("http.route") is string route ? $"{method} {route}" : method;
    }
}
