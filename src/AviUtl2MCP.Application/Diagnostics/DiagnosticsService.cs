using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;

namespace AviUtl2MCP.Application.Diagnostics;

public sealed class DiagnosticsService
{
    private readonly DiagnosticContextFactory? _contextFactory;
    private readonly IDiagnosticRule[] _rules;

    public DiagnosticsService(IEnumerable<IDiagnosticRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.OrderBy(rule => rule.Order).ToArray();
        if (_rules.Length == 0
            || _rules.Any(rule => string.IsNullOrWhiteSpace(rule.RuleId))
            || _rules.Select(rule => rule.RuleId).Distinct(StringComparer.Ordinal).Count() != _rules.Length
            || _rules.Select(rule => rule.Order).Distinct().Count() != _rules.Length)
        {
            throw new ArgumentException("Diagnostic rules must be non-empty with unique IDs and order values.", nameof(rules));
        }
    }

    public DiagnosticsService(
        DiagnosticContextFactory contextFactory,
        IEnumerable<IDiagnosticRule> rules)
        : this(rules)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    public async ValueTask<ApplicationResult<DiagnoseData>> RunAsync(
        DiagnoseInput input,
        DiagnosticRunContext runContext,
        CancellationToken cancellationToken)
    {
        if (_contextFactory is null)
        {
            throw new InvalidOperationException("This diagnostics service has no context factory.");
        }
        ApplicationResult<DiagnosticContext> context = await _contextFactory.CreateAsync(
            input,
            runContext,
            cancellationToken).ConfigureAwait(false);
        return context.IsSuccess
            ? await RunAsync(context.Value!, cancellationToken).ConfigureAwait(false)
            : ApplicationResult.Failure<DiagnoseData>(context.Error!);
    }

    public async ValueTask<ApplicationResult<DiagnoseData>> RunAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<DiagnosticCheck> checks = [];
        foreach (IDiagnosticRule rule in _rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DiagnosticCheck check = await rule.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(check.CheckId, rule.RuleId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("A diagnostic rule returned a mismatched check ID.");
                }
                checks.Add(check);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                checks.Add(DiagnosticRuleResult.Create(
                    rule.RuleId,
                    DiagnosticCheckStatus.Fail,
                    [$"rule_exception={exception.GetType().Name}"],
                    "この診断ruleを完了できず、対象componentの状態は不明です。",
                    "correlation IDのserver logを確認し、再診断してください。",
                    canRetry: true));
            }
        }

        DiagnosticOverallStatus overallStatus = CalculateOverallStatus(checks);
        DiagnoseData data = new(
            overallStatus,
            checks,
            DiagnosticComponentFactory.CreateComponents(context),
            context.KnownLogMatches);
        return ApplicationResult.Success(data);
    }

    public static IReadOnlyList<IDiagnosticRule> CreateDefaultRules() =>
    [
        new ConnectionDiagnosticRule(),
        new ProjectStateDiagnosticRule(),
        new VersionDiagnosticRule(),
        new GcmzDiagnosticRule(),
        new PsdContractDiagnosticRule(),
        new KnownLogDiagnosticRule(),
        new ReadSmokeDiagnosticRule(),
        new PreviewSmokeDiagnosticRule(),
    ];

    private static DiagnosticOverallStatus CalculateOverallStatus(IReadOnlyList<DiagnosticCheck> checks)
    {
        DiagnosticCheck? connection = checks.FirstOrDefault(check => check.CheckId == "connection");
        if (connection?.Status == DiagnosticCheckStatus.Fail)
        {
            return DiagnosticOverallStatus.Unavailable;
        }
        return checks.Any(check => check.Status is DiagnosticCheckStatus.Warning or DiagnosticCheckStatus.Fail)
            ? DiagnosticOverallStatus.Degraded
            : DiagnosticOverallStatus.Healthy;
    }
}
