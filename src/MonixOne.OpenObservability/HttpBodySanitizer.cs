using System.Globalization;
using System.Text;
using System.Text.Json;
using JsonMasking;
using Microsoft.AspNetCore.WebUtilities;

namespace MonixOne.OpenObservability;

/// <summary>
/// Adapts HTTP JSON and form bodies to JsonMasking.
/// Malformed or incomplete data never falls back to raw text.
/// </summary>
internal sealed class HttpBodySanitizer
{
    private static readonly string[] _sensitiveFields =
    [
        "*password*", "*passwd*", "*pwd*", "*secret*", "*token*", "*api_key*", "*api-key*", "*apikey*",
        "*authorization*", "*cookie*", "*credential*", "*otp*", "*assertion*", "*challenge*", "*signature*",
        "*email*", "*phone*", "*contactvalue*", "*contact_value*", "*private*key*", "*signing*key*", "*hmac*key*",
        "code", "*.code", "*code_verifier*", "*codeverifier*", "*verificationcode*", "*verification_code*",
        "*confirmationcode*", "*confirmation_code*", "*error_description*", "*errordescription*",
        "*message*", "*detail*", "*exception*", "*stacktrace*",
        "text", "*.text", "body", "*.body", "destination", "*.destination", "number", "*.number",
        "*firstname*", "*lastname*", "*fullname*", "*first_name*", "*last_name*", "*full_name*", "*birth*",
        "*cardnumber*", "*card_number*", "*cvv*", "*ssn*", "*iban*"
    ];

    private static readonly Dictionary<string, Func<string, string>> _partialMasks =
        _sensitiveFields.ToDictionary(field => field, _ => (Func<string, string>)MaskValue, StringComparer.OrdinalIgnoreCase);

    internal string Sanitize(string body, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;
        if (Encoding.UTF8.GetByteCount(body) >= HttpLoggingConfiguration.BodyLimitBytes)
            return "[omitted: body_limit]";

        try
        {
            var mediaType = contentType?.Split(';', 2)[0].Trim();
            string masked;
            if (string.Equals(mediaType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                // Decode field names and repeated values with the platform parser.
                // Apply the library's field rules to each value, preserving repeated fields.
                var form = QueryHelpers.ParseQuery(body);
                masked = JsonSerializer.Serialize(form.ToDictionary(field => field.Key,
                    field => field.Value.Select(value =>
                    {
                        var json = JsonSerializer.Serialize(new Dictionary<string, string?> { [field.Key] = value });
                        using var document = JsonDocument.Parse(json.MaskFields(_sensitiveFields, "[REDACTED]", _partialMasks));
                        return document.RootElement.GetProperty(field.Key).GetString();
                    }).ToArray()));
            }
            else if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                     || mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    return "[omitted: unsupported_json_root]";

                // JsonMasking 2.0.0 skips partial callbacks for a root array.
                // A neutral object wrapper uses its existing recursive implementation instead.
                var rootArray = document.RootElement.ValueKind == JsonValueKind.Array;
                masked = (rootArray ? "{\"items\":" + body + "}" : body)
                    .MaskFields(_sensitiveFields, "[REDACTED]", _partialMasks);
                if (rootArray)
                {
                    using var wrapper = JsonDocument.Parse(masked);
                    masked = wrapper.RootElement.GetProperty("items").GetRawText();
                }
            }
            else
                return "[omitted: unsupported_content_type]";

            return Encoding.UTF8.GetByteCount(masked) > HttpLoggingConfiguration.BodyLimitBytes
                ? "[omitted: masked_body_limit]"
                : masked;
        }
        catch (JsonException)
        {
            return "[omitted: invalid_json]";
        }
        catch (InvalidOperationException)
        {
            // JsonMasking only accepts JSON objects and arrays.
            // Duplicate property names are also rejected instead of leaking the payload.
            return "[omitted: invalid_json]";
        }
        catch (ArgumentException)
        {
            return "[omitted: invalid_json]";
        }
    }

    private static string MaskValue(string value)
    {
        var characters = StringInfo.ParseCombiningCharacters(value);
        return characters.Length > 12
            ? value[..characters[3]] + "***" + value[characters[^2]..]
            : "[REDACTED]";
    }
}
