using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Diagnostics;

public sealed class ConnectionDiagnosticRule : IDiagnosticRule
{
    public string RuleId => "connection";

    public int Order => 100;

    public ValueTask<DiagnosticCheck> EvaluateAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.Instance.IsAvailable)
        {
            return ValueTask.FromResult(DiagnosticRuleResult.Create(
                RuleId,
                DiagnosticCheckStatus.Fail,
                [$"instanceId={context.Instance.InstanceId:D}", $"processId={context.Instance.ProcessId}", "descriptorAvailable=false"],
                "AviUtl2プロセスへ安全に接続できません。",
                "AviUtl2を起動し、Bridgeのinstance descriptorが更新されることを確認してください。",
                canRetry: true));
        }
        if (context.Status is null)
        {
            DiagnosticProbeFailure failure = context.StatusFailure
                ?? new DiagnosticProbeFailure("status_unavailable", "Status data was not returned.", true);
            return ValueTask.FromResult(DiagnosticRuleResult.FromFailure(
                RuleId,
                failure,
                "pipe接続・handshake・protocol互換性を確認できません。",
                "Bridgeログとinstance descriptorを確認してから再試行してください。"));
        }

        StatusData status = context.Status;
        AviUtlInstance? selected = status.Instances.FirstOrDefault(instance =>
            instance.InstanceId == context.Instance.InstanceId);
        List<string> evidence =
        [
            $"connectionState={status.ConnectionState}",
            $"descriptorProcessId={context.Instance.ProcessId}",
            $"descriptorProcessCreationTime={context.Instance.ProcessCreationTime:O}",
            $"selectedInstance={status.SelectedInstance?.ToString("D") ?? "null"}",
        ];
        if (selected is not null)
        {
            evidence.Add($"reportedProcessId={selected.ProcessId}");
        }

        bool hasMatchingInstance = selected is not null
            && selected.ProcessId == context.Instance.ProcessId
            && status.SelectedInstance == context.Instance.InstanceId;
        DiagnosticCheckStatus checkStatus = status.ConnectionState switch
        {
            ConnectionState.Ready when hasMatchingInstance => DiagnosticCheckStatus.Pass,
            ConnectionState.Connecting => DiagnosticCheckStatus.Warning,
            _ => DiagnosticCheckStatus.Fail,
        };
        return ValueTask.FromResult(DiagnosticRuleResult.Create(
            RuleId,
            checkStatus,
            evidence,
            checkStatus == DiagnosticCheckStatus.Pass
                ? "Bridge接続とprocess identityは利用可能です。"
                : "Bridge接続またはprocess identityが一致せず、操作結果を信頼できません。",
            checkStatus == DiagnosticCheckStatus.Pass
                ? "対処は不要です。"
                : "AviUtl2とMCPサーバーを再接続し、同じPIDのinstanceを選択してください。",
            canRetry: checkStatus != DiagnosticCheckStatus.Pass));
    }
}

public sealed class ProjectStateDiagnosticRule : IDiagnosticRule
{
    public string RuleId => "project-state";

    public int Order => 200;

    public ValueTask<DiagnosticCheck> EvaluateAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Status is null)
        {
            DiagnosticProbeFailure failure = context.StatusFailure
                ?? new DiagnosticProbeFailure("status_unavailable", "Status data was not returned.", true);
            return ValueTask.FromResult(DiagnosticRuleResult.FromFailure(
                RuleId,
                failure,
                "project stateとedit stateを確認できません。",
                "Bridge接続を復旧してから再試行してください。"));
        }

        StatusData status = context.Status;
        bool isProjectUsable = status.ProjectState is ProjectState.Saved or ProjectState.Unsaved;
        bool isEditReady = status.EditState == EditState.Edit;
        DiagnosticCheckStatus checkStatus = isProjectUsable && isEditReady
            ? DiagnosticCheckStatus.Pass
            : DiagnosticCheckStatus.Warning;
        return ValueTask.FromResult(DiagnosticRuleResult.Create(
            RuleId,
            checkStatus,
            [$"projectState={status.ProjectState}", $"editState={status.EditState}"],
            checkStatus == DiagnosticCheckStatus.Pass
                ? "projectは編集可能な状態です。"
                : "project未読込または再生・保存中のため、編集toolが拒否される可能性があります。",
            checkStatus == DiagnosticCheckStatus.Pass
                ? "対処は不要です。"
                : "projectを開き、再生・保存を終えてedit stateへ戻してください。",
            canRetry: !isEditReady));
    }
}

public sealed class VersionDiagnosticRule : IDiagnosticRule
{
    public string RuleId => "versions";

    public int Order => 300;

    public ValueTask<DiagnosticCheck> EvaluateAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Capabilities is null)
        {
            DiagnosticProbeFailure failure = context.CapabilitiesFailure
                ?? new DiagnosticProbeFailure("capabilities_unavailable", "Capabilities data was not returned.", true);
            return ValueTask.FromResult(DiagnosticRuleResult.FromFailure(
                RuleId,
                failure,
                "server・Bridge・SDK・AviUtl2のversion互換性を確認できません。",
                "capabilities取得を復旧し、導入済みcomponentの版を確認してください。"));
        }

        CapabilityVersions versions = context.Capabilities.Versions;
        Dictionary<string, string?> coreVersions = new(StringComparer.Ordinal)
        {
            ["server"] = versions.Server,
            ["schema"] = versions.Schema,
            ["protocol"] = versions.Protocol,
            ["bridge"] = versions.Bridge,
            ["aviutl"] = versions.Aviutl,
            ["sdk"] = versions.Sdk,
        };
        string[] missing = coreVersions
            .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();
        List<string> evidence = coreVersions
            .Select(pair => $"{pair.Key}={pair.Value ?? "missing"}")
            .ToList();
        if (context.Status?.ConnectionState == ConnectionState.Incompatible)
        {
            evidence.Add("connectionState=Incompatible");
        }
        bool isCompatible = missing.Length == 0
            && context.Status?.ConnectionState != ConnectionState.Incompatible;
        return ValueTask.FromResult(DiagnosticRuleResult.Create(
            RuleId,
            isCompatible ? DiagnosticCheckStatus.Pass : DiagnosticCheckStatus.Fail,
            evidence,
            isCompatible
                ? "core componentのversion情報とprotocol negotiationは利用可能です。"
                : "core componentのversion情報が欠落またはprotocolが非互換です。",
            isCompatible
                ? "対処は不要です。"
                : "MCP ServerとBridgeを同じreleaseから再導入し、AviUtl2を再起動してください。"));
    }
}

public sealed class GcmzDiagnosticRule : IDiagnosticRule
{
    public string RuleId => "gcmzdrops";

    public int Order => 400;

    public ValueTask<DiagnosticCheck> EvaluateAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Capabilities is null)
        {
            DiagnosticProbeFailure failure = context.CapabilitiesFailure
                ?? new DiagnosticProbeFailure("capabilities_unavailable", "Capabilities data was not returned.", true);
            return ValueTask.FromResult(DiagnosticRuleResult.FromFailure(
                RuleId,
                failure,
                "GCMZDropsのMutex・FMO・API v3・HWND/PID整合性を確認できません。",
                "Bridge接続を復旧し、GCMZDrops診断を再実行してください。"));
        }

        CapabilityVersions versions = context.Capabilities.Versions;
        ComponentStatus[] components = FindComponents(context.Status, "gcmz");
        CapabilityOperation[] operations = context.Capabilities.Operations
            .Where(operation => operation.Name == "aviutl_psd_create")
            .ToArray();
        List<string> evidence = [$"version={versions.GcmzDrops ?? "missing"}"];
        evidence.AddRange(components.Select(component => $"{component.Name}={component.Status}"));
        evidence.AddRange(operations.Select(operation =>
            $"{operation.Name}={(operation.Available ? "available" : operation.Reason ?? "unavailable")}"));

        bool isMissing = string.IsNullOrWhiteSpace(versions.GcmzDrops);
        bool hasFailure = components.Any(component => IsComponentFailure(component.Status));
        bool hasUnavailableOperation = operations.Length == 0 || operations.Any(operation => !operation.Available);
        bool hasDetailedProbe = HasHealthyComponent(components, "mutex")
            && HasHealthyComponent(components, "fmo")
            && HasHealthyComponent(components, "api")
            && components.Any(component =>
                component.Name.Contains("hwnd", StringComparison.OrdinalIgnoreCase)
                && component.Name.Contains("pid", StringComparison.OrdinalIgnoreCase)
                && IsComponentHealthy(component.Status));
        DiagnosticCheckStatus status = isMissing || hasFailure || hasUnavailableOperation
            ? DiagnosticCheckStatus.Fail
            : hasDetailedProbe
                ? DiagnosticCheckStatus.Pass
                : DiagnosticCheckStatus.Warning;
        return ValueTask.FromResult(DiagnosticRuleResult.Create(
            RuleId,
            status,
            evidence,
            status == DiagnosticCheckStatus.Pass
                ? "GCMZDropsの連携条件を確認できました。"
                : status == DiagnosticCheckStatus.Warning
                    ? "GCMZDropsは検出されましたが、Mutex・FMO・API v3・HWND/PIDの詳細probeがありません。"
                    : "GCMZDropsが未検出または連携条件を満たしていません。",
            status == DiagnosticCheckStatus.Pass
                ? "対処は不要です。"
                : "GCMZDropsを同じAviUtl2 processへ導入し、重複起動・Mutex・FMO/API v3の状態を確認してください。",
            canRetry: status != DiagnosticCheckStatus.Pass));
    }

    private static bool HasHealthyComponent(
        IReadOnlyList<ComponentStatus> components,
        string fragment) =>
        components.Any(component =>
            component.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)
            && IsComponentHealthy(component.Status));

    internal static ComponentStatus[] FindComponents(StatusData? status, string fragment) =>
        status?.Components
            .Where(component => component.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            .ToArray()
        ?? [];

    internal static bool IsComponentFailure(string status) =>
        status.Equals("missing", StringComparison.OrdinalIgnoreCase)
        || status.Equals("incompatible", StringComparison.OrdinalIgnoreCase)
        || status.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
        || status.Equals("error", StringComparison.OrdinalIgnoreCase)
        || status.Equals("faulted", StringComparison.OrdinalIgnoreCase);

    internal static bool IsComponentHealthy(string status) =>
        status.Equals("detected", StringComparison.OrdinalIgnoreCase)
        || status.Equals("ready", StringComparison.OrdinalIgnoreCase)
        || status.Equals("available", StringComparison.OrdinalIgnoreCase)
        || status.Equals("ok", StringComparison.OrdinalIgnoreCase)
        || status.Equals("pass", StringComparison.OrdinalIgnoreCase);
}

public sealed class PsdContractDiagnosticRule : IDiagnosticRule
{
    public string RuleId => "psdtoolkit";

    public int Order => 500;

    public ValueTask<DiagnosticCheck> EvaluateAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Capabilities is null)
        {
            DiagnosticProbeFailure failure = context.CapabilitiesFailure
                ?? new DiagnosticProbeFailure("capabilities_unavailable", "Capabilities data was not returned.", true);
            return ValueTask.FromResult(DiagnosticRuleResult.FromFailure(
                RuleId,
                failure,
                "PSDToolKit2の必須effect・alias contractを確認できません。",
                "Bridge接続を復旧し、PSDToolKit2診断を再実行してください。"));
        }

        CapabilityVersions versions = context.Capabilities.Versions;
        ComponentStatus[] components = GcmzDiagnosticRule.FindComponents(context.Status, "psdtoolkit");
        CapabilityOperation[] operations = context.Capabilities.Operations
            .Where(operation => operation.Name.StartsWith("aviutl_psd_", StringComparison.Ordinal))
            .Where(operation => operation.Name is not "aviutl_psd_create" and not "aviutl_psd_create_voice")
            .ToArray();
        List<string> evidence = [$"version={versions.PsdToolKit ?? "missing"}"];
        evidence.AddRange(components.Select(component => $"{component.Name}={component.Status}"));
        evidence.AddRange(operations.Select(operation =>
            $"{operation.Name}={(operation.Available ? "available" : operation.Reason ?? "unavailable")}"));

        bool isMissing = string.IsNullOrWhiteSpace(versions.PsdToolKit);
        bool hasFailure = components.Any(component => GcmzDiagnosticRule.IsComponentFailure(component.Status));
        bool hasUnavailableOperation = operations.Length == 0 || operations.Any(operation => !operation.Available);
        bool hasContractProbe = HasHealthyContractComponent(components, "effect")
            && HasHealthyContractComponent(components, "alias");
        DiagnosticCheckStatus status = isMissing || hasFailure || hasUnavailableOperation
            ? DiagnosticCheckStatus.Fail
            : hasContractProbe
                ? DiagnosticCheckStatus.Pass
                : DiagnosticCheckStatus.Warning;
        return ValueTask.FromResult(DiagnosticRuleResult.Create(
            RuleId,
            status,
            evidence,
            status == DiagnosticCheckStatus.Pass
                ? "PSDToolKit2の必須effect・alias contractを確認できました。"
                : status == DiagnosticCheckStatus.Warning
                    ? "PSDToolKit2は検出されましたが、必須effect・aliasの詳細probeがありません。"
                    : "PSDToolKit2が未検出または必須operationを利用できません。",
            status == DiagnosticCheckStatus.Pass
                ? "対処は不要です。"
                : "PSDToolKit2本体・必須script/effect・aliasを同じreleaseから再導入してください。",
            canRetry: status != DiagnosticCheckStatus.Pass));
    }

    private static bool HasHealthyContractComponent(
        IReadOnlyList<ComponentStatus> components,
        string fragment) =>
        components.Any(component =>
            component.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)
            && GcmzDiagnosticRule.IsComponentHealthy(component.Status));
}

public sealed class KnownLogDiagnosticRule : IDiagnosticRule
{
    public string RuleId => "known-logs";

    public int Order => 600;

    public ValueTask<DiagnosticCheck> EvaluateAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        List<string> evidence = context.KnownLogMatches
            .SelectMany(match => match.Evidence.Select(line => $"{match.RuleId}: {line}"))
            .Take(9)
            .ToList();
        evidence.AddRange(context.LogWarnings.Select(warning => $"{warning.Code}: {warning.Message}"));
        if (context.LogFailure is not null)
        {
            evidence.Add($"{context.LogFailure.Code}: {context.LogFailure.Message}");
        }

        DiagnosticCheckStatus status = context.KnownLogMatches.Any(match => match.Severity == DiagnosticSeverity.Error)
            ? DiagnosticCheckStatus.Fail
            : context.KnownLogMatches.Count > 0 || context.LogWarnings.Count > 0 || context.LogFailure is not null
                ? DiagnosticCheckStatus.Warning
                : DiagnosticCheckStatus.Pass;
        if (evidence.Count == 0)
        {
            evidence.Add($"scannedLogEntries={context.Logs.Count}");
        }
        return ValueTask.FromResult(DiagnosticRuleResult.Create(
            RuleId,
            status,
            evidence,
            status == DiagnosticCheckStatus.Pass
                ? "既知のPSDToolKit2/GCMZDrops障害パターンは見つかりませんでした。"
                : "既知障害または取得できなかったlog sourceがあります。",
            status == DiagnosticCheckStatus.Pass
                ? "対処は不要です。"
                : "knownLogMatchesのrecommendationを上から確認し、log source障害は接続復旧後に再診断してください。",
            canRetry: context.LogFailure?.CanRetry == true || context.LogWarnings.Count > 0));
    }
}

public sealed class ReadSmokeDiagnosticRule : IDiagnosticRule
{
    public string RuleId => "read-smoke";

    public int Order => 700;

    public ValueTask<DiagnosticCheck> EvaluateAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DiagnosticRuleResult.Smoke(
            RuleId,
            context.ReadSmoke,
            "read-only project queryは成功しました。",
            "read-only project queryが失敗し、query toolを信頼できません。",
            "projectとBridge接続を確認してからread smokeを再実行してください。"));
    }
}

public sealed class PreviewSmokeDiagnosticRule : IDiagnosticRule
{
    public string RuleId => "preview-smoke";

    public int Order => 800;

    public ValueTask<DiagnosticCheck> EvaluateAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DiagnosticRuleResult.Smoke(
            RuleId,
            context.PreviewSmoke,
            "preview PNGの生成と検証は成功しました。",
            "preview生成またはPNG検証が失敗しました。",
            "previewのtimeout・frame・PNG上限とBridgeログを確認して再実行してください。"));
    }
}
