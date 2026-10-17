using System.Net.Http.Headers;
using Microsoft.Extensions.Diagnostics.Enrichment;
using Microsoft.Extensions.Http.Logging;
using Refit;

namespace MonixOne.OpenObservability;

/// <summary>
/// Adds Refit's route template to the standard logger
/// without logging URL parameter values.
/// </summary>
internal sealed class ServiceHttpClientLogEnricher : IHttpClientLogEnricher
{
    public void Enrich(IEnrichmentTagCollector collector, HttpRequestMessage request,
        HttpResponseMessage? response, Exception? exception)
    {
        foreach (var attribute in CreateRequestAttributes(request))
            if (attribute.Value is not null)
                collector.Add(attribute.Key, attribute.Value);
        if (response is not null)
            collector.Add("upstream_status_code", (int)response.StatusCode);
        if (exception is not null)
            foreach (var attribute in ExceptionDiagnosticsExtensions.CreateAttributes(exception))
                if (attribute.Value is not null && attribute.Key is "failure_kind" or "http_request_error" or "socket_error_code")
                    collector.Add(attribute.Key, attribute.Value);
        if (request.Options.TryGetValue(new HttpRequestOptionsKey<string>(HttpRequestMessageOptions.RelativePathTemplate), out var route))
            collector.Add("http_route", route);
        if (request.Options.TryGetValue(new HttpRequestOptionsKey<Type>(HttpRequestMessageOptions.InterfaceType), out var contract))
            collector.Add("http_client", contract.Name);

        // Native LogContentHeaders is experimental in the installed Microsoft package.
        // Select only these three safe content headers through the supported enrichment hook.
        AddContentHeaders(collector, "http.request.header", request.Content?.Headers);
        AddContentHeaders(collector, "http.response.header", response?.Content.Headers);
    }

    internal static List<KeyValuePair<string, object?>> CreateRequestAttributes(HttpRequestMessage request)
    {
        List<KeyValuePair<string, object?>> attributes = [new("dependency_http_method", request.Method.Method)];
        if (request.RequestUri is { IsAbsoluteUri: true } uri)
        {
            attributes.Add(new("dependency_address", uri.Host));
            attributes.Add(new("dependency_port", uri.Port));
        }
        if (request.Options.TryGetValue(new HttpRequestOptionsKey<string>(HttpRequestMessageOptions.RelativePathTemplate), out var route))
            attributes.Add(new("dependency_http_route", route));
        if (request.Options.TryGetValue(new HttpRequestOptionsKey<Type>(HttpRequestMessageOptions.InterfaceType), out var contract))
            attributes.Add(new("dependency_client", contract.FullName));
        return attributes;
    }

    private static void AddContentHeaders(IEnrichmentTagCollector collector, string prefix, HttpContentHeaders? headers)
    {
        if (headers?.ContentType is { } contentType)
            collector.Add($"{prefix}.content-type", contentType.ToString());
        if (headers?.ContentLength is { } length)
            collector.Add($"{prefix}.content-length", length);
        if (headers?.ContentEncoding.Count > 0)
            collector.Add($"{prefix}.content-encoding", string.Join(", ", headers.ContentEncoding));
    }
}
