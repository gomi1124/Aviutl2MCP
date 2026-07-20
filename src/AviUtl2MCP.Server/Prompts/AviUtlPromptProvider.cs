using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AviUtl2MCP.Server.Prompts;

[McpServerPromptType]
public sealed class AviUtlPromptProvider
{
    private static readonly JsonSerializerOptions QUOTE_OPTIONS = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerPrompt(
        Name = "edit_timeline_safely",
        Title = "AviUtl2 timeline安全編集")]
    [Description("読取、dry-run、revision付き編集、再確認を順に行う標準手順です。")]
    public static string EditTimelineSafely(
        [Description("実現したい編集内容。")]
        string objective)
    {
        string quotedObjective = QuoteRequiredArgument(objective, nameof(objective), 4096);
        return $$"""
            AviUtl2で次の目的を安全に実行してください: {{quotedObjective}}

            1. `aviutl_get_status`と`aviutl_get_capabilities`で接続、編集可否、対象instanceを確認する。
            2. `aviutl_get_project`、`aviutl_get_timeline`、必要なら`aviutl_find_objects`/`aviutl_get_object`で対象を特定し、最新revisionとLocatorを取得する。
            3. 変更toolを必ず`dryRun=true`と最新`expectedRevision`で呼び、予定差分と衝突を確認する。
            4. 計画が目的と一致するときだけ同じ引数を`dryRun=false`で実行する。stale revisionを受けたら再読取からやり直し、編集要求を推測で再送しない。
            5. 対象を再取得して事後条件を確認する。見た目が重要なら`aviutl_render_preview`も使う。
            6. partial/unknown結果では生成済みLocatorと`undoRecommended`を保持し、`aviutl_get_logs`と`aviutl_diagnose`で根拠を集めてから利用者へ報告する。
            """;
    }

    [McpServerPrompt(
        Name = "setup_psd_character",
        Title = "PSDキャラクター設定")]
    [Description("PSD投入、初期化、キャラクターID、構成検証の安全な手順です。")]
    public static string SetupPsdCharacter(
        [Description("投入する絶対PSD/PSB path。")]
        string psdPath,
        [Description("PSDToolKit2で使用するキャラクターID。")]
        string characterId)
    {
        string quotedPsdPath = QuoteRequiredArgument(psdPath, nameof(psdPath), 32_767);
        string quotedCharacterId = QuoteRequiredArgument(characterId, nameof(characterId), 256);
        return $$"""
            PSDToolKit2キャラクターを設定してください。
            - PSD path: {{quotedPsdPath}}
            - character ID: {{quotedCharacterId}}

            1. `aviutl_get_status`、`aviutl_get_capabilities`、必要なら`aviutl_diagnose`でPSDToolKit2 profileとGCMZDrops能力を確認する。
            2. `aviutl_psd_setup`を最新revisionかつ`dryRun=true`で検証し、必要な場合だけ実行する。
            3. 空きlayer/frameを読取で確認し、`aviutl_psd_create`をdry-run後に実行する。GCMZDrops receiptだけでなく返却objectを確認する。
            4. 返却Locatorを使い、`aviutl_psd_set_character`をdry-run後に実行する。
            5. `aviutl_get_object`と`aviutl_psd_validate`でsetup、character、目パチ、口パク、字幕構成を確認する。
            6. partial結果では自動再送せず、生成済みobjectとUndo推奨を報告する。
            """;
    }

    [McpServerPrompt(
        Name = "add_voice_and_subtitle",
        Title = "PSD音声・字幕追加")]
    [Description("WAV/TXT/LAB、セリフ準備、字幕を安全に追加・検証する手順です。")]
    public static string AddVoiceAndSubtitle(
        [Description("投入する絶対WAV path。")]
        string audioPath,
        [Description("PSDToolKit2で使用するキャラクターID。")]
        string characterId)
    {
        string quotedAudioPath = QuoteRequiredArgument(audioPath, nameof(audioPath), 32_767);
        string quotedCharacterId = QuoteRequiredArgument(characterId, nameof(characterId), 256);
        return $$"""
            PSDToolKit2音声と字幕を追加してください。
            - WAV path: {{quotedAudioPath}}
            - character ID: {{quotedCharacterId}}

            1. WAVと同じdirectory/basenameのTXTを必須確認し、任意LABの有無も確認する。ファイル内容を通常ログへ出さない。
            2. `aviutl_get_capabilities`で`aviutl_psd_create_voice`が利用可能か確認する。利用不可なら設定を自動変更せず理由を報告する。
            3. timelineと最新revisionを取得し、音声・セリフ準備・字幕の3 layerが空くplacementを選ぶ。
            4. `aviutl_psd_create_voice`を`dryRun=true`で呼び、伴随ファイルと3つの予定変更を確認してから実行する。
            5. 返却されたvoice/subtitle objectsを再取得し、`aviutl_psd_validate`でcharacter、lipSync、subtitleを検証する。LABがなければ口パク警告を明示する。
            6. partial/timeoutでは同じ音声を自動再送せず、相関IDで`aviutl_get_logs`と`aviutl_diagnose`を確認する。
            """;
    }

    [McpServerPrompt(
        Name = "diagnose_aviutl",
        Title = "AviUtl2安全診断")]
    [Description("状態、能力、診断、根拠ログを読取専用で確認する手順です。")]
    public static string DiagnoseAviUtl(
        [Description("preview smokeも実行するか。")]
        bool includePreview = false) => $$"""
            AviUtl2を編集せずに診断してください。

            1. `aviutl_get_status`でserver、Bridge、AviUtl2、project、編集状態を分けて確認する。
            2. `aviutl_get_capabilities`で基本操作、PSDToolKit2、GCMZDrops、voice routeを個別に確認する。
            3. `aviutl_diagnose`を`includeReadSmoke=true`、`includePreviewSmoke={{includePreview.ToString().ToLowerInvariant()}}`で実行する。
            4. warning/failのcomponentと既知ログruleを確認し、必要な範囲だけ`aviutl_get_logs`をcorrelation ID付きで取得する。
            5. 観測事実、影響、推奨対処、未確認事項を分けて報告する。自動修復や設定変更は行わない。
            """;

    private static string QuoteRequiredArgument(string value, string parameterName, int maxCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\0') || value.Length > maxCharacters)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Prompt argument exceeds the contract limit.");
        }
        return JsonSerializer.Serialize(value, QUOTE_OPTIONS);
    }
}
