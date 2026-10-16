using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MonixOne.OpenObservability;

/// <summary>
/// Registers standard <see cref="ILogger"/> logging and distributed tracing for a service.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Adds console logging, OpenTelemetry logs and ASP.NET Core/HttpClient tracing.
    /// OTLP export is optional and uses batching; an unavailable endpoint never participates in request execution.
    /// </summary>
    public static IHostApplicationBuilder AddOpenObservability(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Services.Any(service => service.ServiceType == typeof(HttpBodySanitizer)))
            return builder;

        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        var options = ServiceObservabilityOptions.FromConfiguration(
            builder.Configuration,
            builder.Environment);

        ConfigureConsoleLogging(builder, options);
        builder.Services.AddHttpDiagnostics();
        builder.Services.AddHttpContextAccessor();
        builder.AddSentryTelemetry();

        var telemetry = builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: options.ServiceName,
                    serviceVersion: options.ServiceVersion)
                .AddAttributes(
                [
                    new KeyValuePair<string, object>(
                        "deployment.environment.name",
                        options.EnvironmentName)
                ]));

        telemetry.WithLogging(
            logging =>
            {
                logging.AddProcessor(new LogRecordEnrichmentProcessor(options));

                if (options is { OtlpEnabled: true, OtlpEndpoint: not null })
                    logging.AddOtlpExporter(exporter => ConfigureExporter(
                        exporter,
                        options,
                        "v1/logs"));
            },
            loggingOptions =>
            {
                loggingOptions.IncludeFormattedMessage = true;
                loggingOptions.IncludeScopes = true;
                loggingOptions.ParseStateValues = true;
            });

        telemetry.WithTracing(tracing =>
        {
            tracing
                .AddAspNetCoreInstrumentation(opt =>
                {
                    opt.Filter = ShouldTraceAspNetCoreRequest;
                    opt.RecordException = false;
                    opt.EnrichWithHttpRequest = (activity, _) => HttpTraceEnrichment.Sanitize(activity);
                    opt.EnrichWithHttpResponse = (activity, _) => HttpTraceEnrichment.Sanitize(activity);
                    opt.EnrichWithException = (activity, _) => HttpTraceEnrichment.Sanitize(activity);
                })
                .AddHttpClientInstrumentation(opt =>
                {
                    opt.RecordException = false;
                    opt.EnrichWithHttpRequestMessage = HttpTraceEnrichment.EnrichClientRequest;
                    opt.EnrichWithHttpResponseMessage = (activity, _) => HttpTraceEnrichment.Sanitize(activity);
                    opt.EnrichWithException = (activity, _) => HttpTraceEnrichment.Sanitize(activity);
                });

            if (options is { OtlpEnabled: true, OtlpEndpoint: not null })
                tracing.AddOtlpExporter(exporter => ConfigureExporter(
                    exporter,
                    options,
                    "v1/traces"));
        });

        telemetry.WithMetrics(metrics =>
        {
            metrics.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel",
                "System.Net.Http", "System.Net.NameResolution", "System.Runtime");
            if (options is { OtlpEnabled: true, OtlpEndpoint: not null })
                metrics.AddOtlpExporter(exporter => ConfigureExporter(exporter, options, "v1/metrics"));
        });

        builder.Services.AddSingleton<HttpBodySanitizer>();
        builder.Services.Decorate<ILoggerFactory, RedactingLoggerFactory>();

        return builder;
    }

    static bool ShouldTraceAspNetCoreRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return !context.Request.Path.StartsWithSegments("/api/health");
    }

    static void ConfigureConsoleLogging(
        IHostApplicationBuilder builder,
        ServiceObservabilityOptions options)
    {
        if (!options.ConsoleEnabled)
            foreach (var provider in builder.Services.Where(service =>
                         !service.IsKeyedService && service.ServiceType == typeof(ILoggerProvider)
                         && service.ImplementationType == typeof(ConsoleLoggerProvider)).ToArray())
                builder.Services.Remove(provider);
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Services.PostConfigure<LoggerFilterOptions>(filters =>
        {
            string[] categories =
            [
                "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware",
                "Microsoft.Extensions.Http.Logging",
                typeof(HttpBodyLoggingHandler).FullName!
            ];
            foreach (var category in categories)
                if (!filters.Rules.Any(rule => rule.ProviderName is null && rule.CategoryName == category))
                    filters.Rules.Add(new LoggerFilterRule(null, category, LogLevel.Information, null));
        });

        if (!options.ConsoleEnabled)
            return;

        builder.Logging.AddSimpleConsole(console =>
        {
            console.IncludeScopes = true;
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });
    }

    static void ConfigureExporter(
        OtlpExporterOptions exporter,
        ServiceObservabilityOptions options,
        string httpSignalPath)
    {
        exporter.Protocol = options.OtlpProtocol;
        exporter.Endpoint = ResolveEndpoint(
            options.OtlpEndpoint!,
            options.OtlpProtocol,
            httpSignalPath);

        if (!string.IsNullOrWhiteSpace(options.OtlpHeaders))
            exporter.Headers = options.OtlpHeaders;
    }

    /// <summary>
    /// Resolves a per-signal OTLP/HTTP endpoint from the configured base address.
    /// The .NET exporter treats an explicitly assigned endpoint as final, so the
    /// signal path must be supplied by this infrastructure.
    /// </summary>
    static Uri ResolveEndpoint(
        Uri endpoint,
        OtlpExportProtocol protocol,
        string httpSignalPath)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpSignalPath);

        if (protocol != OtlpExportProtocol.HttpProtobuf)
            return endpoint;

        var builder = new UriBuilder(endpoint)
        {
            Path = $"{endpoint.AbsolutePath.TrimEnd('/')}/{httpSignalPath.TrimStart('/')}",
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }
}
