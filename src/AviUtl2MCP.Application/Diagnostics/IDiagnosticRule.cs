using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Diagnostics;

public interface IDiagnosticRule
{
    string RuleId { get; }

    int Order { get; }

    ValueTask<DiagnosticCheck> EvaluateAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken);
}

internal static class DiagnosticRuleResult
{
    private const int MAXIMUM_EVIDENCE_LINES = 16;
    private const int MAXIMUM_EVIDENCE_CHARACTERS = 1024;

    public static DiagnosticCheck Create(
        string checkId,
        DiagnosticCheckStatus status,
        IReadOnlyList<string> evidence,
        string impact,
        string recommendation,
        bool canRetry = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(impact);
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendation);
        string[] normalizedEvidence = evidence
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(MAXIMUM_EVIDENCE_LINES)
            .Select(TruncateEvidence)
            .ToArray();
        if (normalizedEvidence.Length == 0)
        {
            throw new ArgumentException("Diagnostic evidence must contain at least one non-empty line.", nameof(evidence));
        }
        return new DiagnosticCheck(
            checkId,
            status,
            normalizedEvidence,
            impact,
            recommendation,
            canRetry);
    }

    public static DiagnosticCheck FromFailure(
        string checkId,
        DiagnosticProbeFailure failure,
        string impact,
        string recommendation) =>
        Create(
            checkId,
            DiagnosticCheckStatus.Fail,
            [$"{failure.Code}: {failure.Message}"],
            impact,
            recommendation,
            failure.CanRetry);

    public static DiagnosticCheck Smoke(
        string checkId,
        DiagnosticSmokeResult result,
        string successImpact,
        string failureImpact,
        string recommendation)
    {
        if (!result.WasRequested)
        {
            return Create(
                checkId,
                DiagnosticCheckStatus.Skipped,
                ["The smoke check was not requested."],
                successImpact,
                "Set the corresponding include flag to run this read-only smoke check.");
        }
        if (result.Succeeded)
        {
            return Create(
                checkId,
                DiagnosticCheckStatus.Pass,
                result.Evidence,
                successImpact,
                "No action is required.");
        }

        DiagnosticProbeFailure failure = result.Failure
            ?? new DiagnosticProbeFailure("smoke_failed", "The smoke check failed without details.", false);
        List<string> evidence = [.. result.Evidence, $"{failure.Code}: {failure.Message}"];
        return Create(
            checkId,
            DiagnosticCheckStatus.Fail,
            evidence,
            failureImpact,
            recommendation,
            failure.CanRetry);
    }

    private static string TruncateEvidence(string evidence) =>
        evidence.Length <= MAXIMUM_EVIDENCE_CHARACTERS
            ? evidence
            : string.Concat(evidence.AsSpan(0, MAXIMUM_EVIDENCE_CHARACTERS - 3), "...");
}
