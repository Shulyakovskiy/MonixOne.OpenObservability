using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Diagnostics;
using Microsoft.Extensions.Http.Logging;

namespace MonixOne.OpenObservability;

/// <summary>
/// One registration for the platform's incoming and outgoing HTTP loggers.
/// </summary>
internal static class HttpLoggingConfiguration
{
    internal const int BodyLimitBytes = 16 * 1024;

    private static readonly string[] _requestHeaders =
    [
        "Accept", "Content-Type", "Content-Length", "Content-Encoding", "traceparent", "x-correlation-id",
        "x-request-id"
    ];

    private static readonly string[] _responseHeaders =
    [
        "Content-Type", "Content-Length", "Content-Encoding", "Retry-After", "traceparent", "x-correlation-id",
        "x-request-id"
    ];

    internal static void AddHttpDiagnostics(this IServiceCollection services)
    {
        services.AddHttpLogging(options =>
        {
            options.LoggingFields = HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestScheme
                                                                    | HttpLoggingFields.RequestProtocol |
                                                                    HttpLoggingFields.RequestHeaders
                                                                    | HttpLoggingFields.ResponseStatusCode |
                                                                    HttpLoggingFields.ResponseHeaders
                                                                    | HttpLoggingFields.Duration
                                                                    | HttpLoggingFields.RequestBody | HttpLoggingFields.ResponseBody;
            options.CombineLogs = true;
            options.RequestHeaders.Clear();
            options.ResponseHeaders.Clear();
            foreach (var header in _requestHeaders)
                options.RequestHeaders.Add(header);
            foreach (var header in _responseHeaders)
                options.ResponseHeaders.Add(header);

            // The platform captures bounded bodies; JsonMasking redacts them
            // before any ILogger provider receives the record.
            options.RequestBodyLogLimit = BodyLimitBytes;
            options.ResponseBodyLogLimit = BodyLimitBytes;
            options.MediaTypeOptions.Clear();
            options.MediaTypeOptions.AddText("application/json");
            options.MediaTypeOptions.AddText("application/*+json");
            options.MediaTypeOptions.AddText("application/x-www-form-urlencoded");
        });
        services.AddHttpLoggingInterceptor<ServiceHttpLoggingInterceptor>();

        services.AddRedaction();
        services.AddHttpClientLogEnricher<ServiceHttpClientLogEnricher>();
        services.AddTransient<HttpBodyLoggingHandler>();
        services.ConfigureHttpClientDefaults(client => client.AddHttpMessageHandler<HttpBodyLoggingHandler>());
        services.AddExtendedHttpClientLogging(options =>
        {
            options.LogBody = false;
            options.LogRequestStart = false;
            options.RequestPathParameterRedactionMode = HttpRouteParameterRedactionMode.Strict;
            options.RequestPathLoggingMode = OutgoingPathLoggingMode.Structured;
            foreach (var header in _requestHeaders)
                options.RequestHeadersDataClasses.Add(header, DataClassification.None);
            foreach (var header in _responseHeaders)
                options.ResponseHeadersDataClasses.Add(header, DataClassification.None);
        });
    }
}
