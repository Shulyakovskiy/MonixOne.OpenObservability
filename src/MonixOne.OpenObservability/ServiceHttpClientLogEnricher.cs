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
        if (request.Options.TryGetValue(new HttpRequestOptionsKey<string>(HttpRequestMessageOptions.RelativePathTemplate), out var route))
            collector.Add("http_route", route);
        if (request.Options.TryGetValue(new HttpRequestOptionsKey<Type>(HttpRequestMessageOptions.InterfaceType), out var contract))
            collector.Add("http_client", contract.Name);

        // Native LogContentHeaders is experimental in the installed Microsoft package.
        // Select only these three safe content headers through the supported enrichment hook.
        AddContentHeaders(collector, "http.request.header", request.Content?.Headers);
        AddContentHeaders(collector, "http.response.header", response?.Content.Headers);
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
