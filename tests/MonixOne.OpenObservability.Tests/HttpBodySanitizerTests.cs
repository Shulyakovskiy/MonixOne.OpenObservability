using System.Text.Json;
using Shouldly;
using Xunit;

namespace MonixOne.OpenObservability.Tests;

public sealed class HttpBodySanitizerTests
{
    [Theory]
    [InlineData("email", "eshulakovsky@example.ru", "esh***ru")]
    [InlineData("ACCESS_TOKEN", "abcdefghijklm", "abc***lm")]
    [InlineData("password", "abcdefghijkl", "[REDACTED]")]
    [InlineData("otp", "123456", "[REDACTED]")]
    [InlineData("email", "a@b.ru", "[REDACTED]")]
    [InlineData("secret", "", "[REDACTED]")]
    [InlineData("safe", "abcdefghijklm", "abcdefghijklm")]
    [InlineData("token", "😀😀😀😀😀😀😀", "[REDACTED]")]
    [InlineData("token", "😀😀😀😀😀😀😀😀😀😀😀😀😀", "😀😀😀***😀😀")]
    public void SensitiveStrings_PreserveOnlyABoundedPreview(string field, string value, string expected)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = value });

        var masked = new HttpBodySanitizer().Sanitize(body, "application/json");

        using var document = JsonDocument.Parse(masked);
        document.RootElement.GetProperty(field).GetString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedFields_HaveTheSamePolicyInObjectsAndRootArrays(bool rootArray)
    {
        const string item = """{"users":[{"Email":"eshulakovsky@example.ru","otp":"123456","safe":"visible"}]}""";
        var masked = new HttpBodySanitizer().Sanitize(rootArray ? "[" + item + "]" : item, "application/problem+json");

        using var document = JsonDocument.Parse(masked);
        var root = rootArray ? document.RootElement[0] : document.RootElement;
        var user = root.GetProperty("users")[0];
        user.GetProperty("Email").GetString().ShouldBe("esh***ru");
        user.GetProperty("otp").GetString().ShouldBe("[REDACTED]");
        user.GetProperty("safe").GetString().ShouldBe("visible");
        masked.ShouldNotContain("eshulakovsky");
    }

    [Fact]
    public void EncodedFormFields_MaskEveryRepeatedValueWithTheSamePolicy()
    {
        var masked = new HttpBodySanitizer().Sanitize(
            "em%61il=eshulakovsky%40example.ru&email=second%40example.ru&otp=123456&safe=visible&safe=another",
            "application/x-www-form-urlencoded; charset=utf-8");

        using var document = JsonDocument.Parse(masked);
        document.RootElement.GetProperty("email").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(["esh***ru", "sec***ru"]);
        document.RootElement.GetProperty("otp")[0].GetString().ShouldBe("[REDACTED]");
        document.RootElement.GetProperty("safe").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(["visible", "another"]);
    }

    [Theory]
    [InlineData("{\"email\":\"first@example.ru\",\"email\":\"second@example.ru\"}")]
    [InlineData("{\"token\":\"incomplete")]
    public void InvalidJson_NeverFallsBackToAPartiallyVisibleRawBody(string body)
    {
        new HttpBodySanitizer().Sanitize(body, "application/json").ShouldBe("[omitted: invalid_json]");
    }
}
