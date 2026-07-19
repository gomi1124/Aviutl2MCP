using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Diagnostics;

public static class KnownLogClassifier
{
    private const int MAXIMUM_EVIDENCE_PER_RULE = 3;

    private static readonly IReadOnlyList<KnownLogRule> RULES =
    [
        new(
            "psdtoolkit.cache-missing",
            DiagnosticSeverity.Warning,
            MatchesPsdCacheMissing,
            "PSDToolKit2の画像キャッシュを取得できず、PSDオブジェクトが描画されない可能性があります。",
            "AviUtl2を再描画または再起動し、継続する場合はPSDファイルとPSDToolKit2の配置・権限を確認してください。"),
        new(
            "psdtoolkit.pipe-exited",
            DiagnosticSeverity.Error,
            MatchesPsdPipeExited,
            "PSDToolKit2の補助プロセスとの通信が終了し、PSDの描画・編集が利用できません。",
            "AviUtl2を再起動し、PSDToolKit2本体と依存ファイルが同じ版で導入されているか確認してください。"),
        new(
            "psdtoolkit.effect-missing",
            DiagnosticSeverity.Error,
            MatchesPsdEffectMissing,
            "PSDToolKit2が必要とするエフェクトまたはスクリプトを解決できず、対象オブジェクトを正しく評価できません。",
            "PSDToolKit2の必須スクリプトとエフェクト定義を再導入し、AviUtl2を再起動してください。"),
    ];

    public static IReadOnlyList<KnownLogMatch> Classify(IReadOnlyList<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<KnownLogMatch> matches = [];
        foreach (KnownLogRule rule in RULES)
        {
            LogEntry[] evidenceEntries = entries
                .Where(entry => rule.IsMatch(entry.Message))
                .Take(MAXIMUM_EVIDENCE_PER_RULE)
                .ToArray();
            if (evidenceEntries.Length == 0)
            {
                continue;
            }

            matches.Add(new KnownLogMatch(
                rule.RuleId,
                SelectSource(evidenceEntries),
                rule.Severity,
                evidenceEntries.Select(FormatEvidence).ToArray(),
                rule.Impact,
                rule.Recommendation));
        }

        return matches;
    }

    private static bool MatchesPsdCacheMissing(string message)
    {
        return Contains(message, ".ptkcache")
            && (Contains(message, "can not open file")
                || Contains(message, "cannot open file")
                || Contains(message, "cache miss")
                || Contains(message, "cache not found")
                || Contains(message, "キャッシュ") && Contains(message, "見つかりません"));
    }

    private static bool MatchesPsdPipeExited(string message)
    {
        bool hasPipe = Contains(message, "pipe") || Contains(message, "パイプ");
        bool hasExit = Contains(message, "exit")
            || Contains(message, "closed")
            || Contains(message, "broken")
            || Contains(message, "終了")
            || Contains(message, "切断");
        bool hasPsdContext = Contains(message, "PSDToolKit")
            || Contains(message, "read_thread")
            || Contains(message, "PSDToolKit.exe");
        return hasPipe && hasExit && hasPsdContext;
    }

    private static bool MatchesPsdEffectMissing(string message)
    {
        return Contains(message, "not found movement")
            || Contains(message, "PSDToolKit")
                && (Contains(message, "effect not found")
                    || Contains(message, "missing effect")
                    || Contains(message, "エフェクト") && Contains(message, "見つかりません"));
    }

    private static bool Contains(string message, string value) =>
        message.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static string SelectSource(LogEntry[] entries)
    {
        string source = entries[0].Source;
        return entries.All(entry => string.Equals(entry.Source, source, StringComparison.OrdinalIgnoreCase))
            ? source
            : "multiple";
    }

    private static string FormatEvidence(LogEntry entry) =>
        $"{entry.Timestamp:O} [{entry.Source}] {entry.Message}";

    private sealed record KnownLogRule(
        string RuleId,
        DiagnosticSeverity Severity,
        Func<string, bool> IsMatch,
        string Impact,
        string Recommendation);
}
