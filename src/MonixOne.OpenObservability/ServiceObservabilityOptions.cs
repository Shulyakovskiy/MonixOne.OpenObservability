using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;

namespace MonixOne.OpenObservability;

internal sealed record ServiceObservabilityOptions(
    string ServiceName,
    string ServiceVersion,
    string EnvironmentName,
    bool OtlpEnabled,
    Uri? OtlpEndpoint,
    OtlpExportProtocol OtlpProtocol,
    string? OtlpHeaders,
    bool ConsoleEnabled)
{
    const string LoggingSection = "Observability:Logging";

    public static ServiceObservabilityOptions FromConfiguration(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var section = configuration.GetSection(LoggingSection);
        var serviceName = configuration["Service:Name"];
        if (string.IsNullOrWhiteSpace(serviceName))
            serviceName = environment.ApplicationName;

        var endpointValue = section["OtlpEndpoint"];
        var endpoint = Uri.TryCreate(endpointValue, UriKind.Absolute, out var parsedEndpoint)
            && (parsedEndpoint.Scheme == Uri.UriSchemeHttp || parsedEndpoint.Scheme == Uri.UriSchemeHttps)
                ? parsedEndpoint
                : null;

        var headers = section["OtlpHeaders"];
        if (string.IsNullOrWhiteSpace(headers))
            headers = configuration["OTEL_EXPORTER_OTLP_HEADERS"];

        var consoleEnabled = bool.TryParse(section["ConsoleEnabled"], out var configuredConsole)
            ? configuredConsole
            : environment.IsDevelopment()
              || environment.IsEnvironment("Local");

        return new ServiceObservabilityOptions(
            serviceName,
            GetServiceVersion(),
            environment.EnvironmentName,
            bool.TryParse(section["Enabled"], out var enabled) && enabled,
            endpoint,
            ParseProtocol(section["OtlpProtocol"]),
            headers,
            consoleEnabled);
    }

    static string GetServiceVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }

    static OtlpExportProtocol ParseProtocol(string? configuredProtocol)
    {
        return configuredProtocol?.Trim().ToLowerInvariant() switch
        {
            "grpc" => OtlpExportProtocol.Grpc,
            "http" or "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
            _ => OtlpExportProtocol.HttpProtobuf
        };
    }
}
