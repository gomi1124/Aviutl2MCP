using System.Text.RegularExpressions;

namespace AviUtl2MCP.Server.Logging;

public static partial class LogSecretMasker
{
    private const string RedactedValue = "[REDACTED]";

    public static string? MaskValue(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return IsSensitiveKey(key)
            ? RedactedValue
            : MaskText(value?.ToString());
    }

    public static string? MaskText(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string masked = BearerTokenRegex().Replace(value, "Bearer " + RedactedValue);
        return AssignedSecretRegex().Replace(masked, match => $"{match.Groups[1].Value}={RedactedValue}");
    }

    private static bool IsSensitiveKey(string key) =>
        SensitiveKeyRegex().IsMatch(key);

    [GeneratedRegex("^(?:authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|passwd|secret)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyRegex();

    [GeneratedRegex("\\bBearer\\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("\\b(authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|passwd|secret)\\b\\s*[:=]\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssignedSecretRegex();
}
