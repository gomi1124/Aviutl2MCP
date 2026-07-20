# V1受け入れテスト対応表

## 1. 運用

各Phase 1受け入れ基準を、実装済みの自動・実機テストへ一対一で対応付ける。`Test ID`はテスト名またはCI job名に含め、`Evidence`へworkflow URLまたは実機レポートを記録する。

2026-07-19時点で33件すべてが合格済み。実機証跡はcommit `53459b3` のクリーンな作業ツリーで生成し、元fixtureのSHA-256不変とharness所有processだけのcleanupを確認した。

| AC ID | Test ID | 層・環境 | 合格条件 | Evidence |
|---|---|---|---|---|
| AC-BLD-001 | `build.clean-windows` | Windows CI | locked restoreからServer/Bridgeを警告エラーなしでbuild | [CI] `managed` / `native` / `contract` / `integration` |
| AC-BLD-002 | `ci.required-jobs` | GitHub Actions | managed/native/contract/integration jobが全成功 | [CI] 全required job成功 |
| AC-BLD-003 | `real.package-install` | 専用AviUtl2実機 | `.au2pkg.zip`導入後にbridge versionをstatusで取得 | `artifacts/real-e2e/019f7a33-8fef-7a2d-8b2b-e162fedfebec/debug-report.json` |
| AC-MCP-001 | `stdio.offline-initialize` | stdio black-box | AviUtl2なしでinitializeと28 tools listが成功 | [CI] `contract` |
| AC-MCP-002 | `pipe.late-connect` | fake bridge統合 | Server維持中にbridge起動しReadyへ遷移 | [CI] `integration` |
| AC-MCP-003 | `mcp.catalog-snapshot` | MCP contract | 28 tools、5 resources、4 promptsとSchema catalogが一致 | [CI] `contract` |
| AC-MCP-004 | `stdio.stdout-purity` | stdio black-box | stdoutの全frameが有効MCP message、ログはstderrのみ | [CI] `contract` |
| AC-MCP-005 | `pipe.instance-selection` | fake bridge統合 | 複数時は曖昧拒否、明示IDだけへ要求送信 | [CI] `integration` |
| AC-EDT-001 | `real.timeline-read` | 専用AviUtl2実機 | fixtureのscene/layer/object/effect DTOがgolden値と一致 | `artifacts/real-e2e/019f79ef-acdd-7f30-b7bd-0f6a9417e1db/debug-report.json` |
| AC-EDT-002 | `real.object-create-three-ways` | 専用AviUtl2実機 | media/effect/alias各方式の生成物を再取得 | `artifacts/real-e2e/019f79ef-7a3a-785f-ad59-2c4c7c30369e/debug-report.json` |
| AC-EDT-003 | `real.object-edit-lifecycle` | 専用AviUtl2実機 | move/name/item/state/deleteを再取得で確認 | `artifacts/real-e2e/019f79ef-7a3a-785f-ad59-2c4c7c30369e/debug-report.json` |
| AC-EDT-004 | `app.dry-run-no-change` | Application単体＋実機 | revision、object snapshot、Undo履歴が不変 | [CI] `managed` + `real-e2e/019f79ef-7a3a-785f-ad59-2c4c7c30369e` |
| AC-EDT-005 | `app.revision-conflict` | Application/pipe統合 | 旧revisionを拒否し状態不変、連続2編集でも検出 | [CI] `integration` + `real-e2e/019f79ef-7a3a-785f-ad59-2c4c7c30369e` |
| AC-EDT-006 | `real.batch-single-undo` | 専用AviUtl2実機 | batch全変更が1回のUndoでgolden snapshotへ戻る | `artifacts/real-e2e/019f79ef-7a3a-785f-ad59-2c4c7c30369e/debug-report.json` |
| AC-EDT-007 | `app.stable-edit-errors` | Application単体 | not-found/collision/play/saveを別codeで返す | [CI] `native` |
| AC-EDT-008 | `bridge.batch-partial` | native fake＋実機 | N件目失敗で適用ID/状態/Undo推奨、1 Undo復旧 | [CI] `native` / `integration` + `real-e2e/019f79ef-7a3a-785f-ad59-2c4c7c30369e` |
| AC-PSD-001 | `real.psd-create` | 専用PSD実機 | PSD投入後にprofile一致objectをSDK再検索 | `artifacts/real-e2e/019f7a32-2164-75ca-abfb-6d4455c25855/debug-report.json` |
| AC-PSD-002 | `real.psd-setup` | 専用PSD実機 | 不足/誤配置を検出し、安全候補へsetupを作成 | `artifacts/real-e2e/019f79ef-ce94-7340-8803-bf6ea373c2d7/debug-report.json` |
| AC-PSD-003 | `real.psd-character-layer` | 専用PSD実機 | character IDとcanonical layerStateがround-trip一致 | `artifacts/real-e2e/019f79ef-ff8e-775b-858a-f5667a48cb81/debug-report.json` |
| AC-PSD-004 | `real.psd-voice-subtitle` | 専用PSD実機（中間object）＋2設定contract fixture | 両経路の契約と、必須ID付きvoice prep・字幕の実機再検索 | `artifacts/real-e2e/019f7a32-7ea3-7d8e-9e9b-501eccb70c5b/debug-report.json` |
| AC-PSD-005 | `real.psd-lipsync-lab` | 専用PSD実機 | 同basename LABとLipSyncLab構成を検証 | `artifacts/real-e2e/019f7a32-7ea3-7d8e-9e9b-501eccb70c5b/debug-report.json` |
| AC-PSD-006 | `app.psd-capability-isolation` | fake profile/GCMZ | GCMZ無効でも基本tool成功、voiceだけ能力エラー | [CI] `managed` / `integration` |
| AC-PSD-007 | `bridge.gcmz-partial` | fake GCMZ＋実機 | timeout/一部/誤配置で検出物付きpartialを返す | [CI] `native` / `integration` + `artifacts/real-e2e/019f7a18-4b10-72c3-9ae8-30f5d887deb6/debug-report.json` |
| AC-DIA-001 | `real.preview-image` | 専用AviUtl2実機 | PNG signature、寸法、非空pixel、MCP image content | `artifacts/real-e2e/019f79ef-b444-7aaf-bce5-d279ca45a700/debug-report.json` |
| AC-DIA-002 | `smoke.before-after-diff` | Application＋実機 | 既知変更のrevisionまたはpixel差を検出 | `artifacts/real-e2e/019f79ef-7a3a-785f-ad59-2c4c7c30369e/debug-report.json` |
| AC-DIA-003 | `diagnostics.known-log-rules` | Diagnostics単体 | 3 fixtureを根拠行/影響/推奨対処へ分類 | [CI] `managed` |
| AC-DIA-004 | `diagnostics.pipe-recovery` | fake bridge統合 | 切断checkが失敗し、再接続後に正常化 | [CI] `integration` |
| AC-DIA-005 | `bridge.render-lifetime-stress` | native fake＋実機 | 正常/timeout/late callback反復でhang/UAF/double freeなし | [CI] `native` + `real-e2e/019f79ef-acdd-7f30-b7bd-0f6a9417e1db` |
| AC-DIA-006 | `ipc.mutation-at-most-once` | IPC contract | 応答消失再送で1編集、payload差はconflict | [CI] `integration` |
| AC-SAF-001 | `real.fixture-process-guard` | 実機harness | temp fixtureとharness PID以外を開かず、既存aup2 hash不変 | 成功・失敗両report、SHA-256 `C2E030F6...66CEA`不変 |
| AC-SAF-002 | `pipe.cross-logon-denied` | Windows統合 | 別logon SIDのclientがpipe open/handshake不可 | [CI] `native` |
| AC-SAF-003 | `fuzz.input-boundaries` | managed/native fuzz | 過大長/UTF-8/pathを拒否後もstatus応答 | [CI] `managed` |
| AC-SAF-004 | `bridge.handle-lifetime` | native fake＋sanitizer | callback後handle/pointer利用をguardまたはASanが検出 | [CI] `native` |

## 2. 実機ガード

- 実機テストは`--real`、専用fixture root、harnessが起動したAviUtl2 PIDの3条件を必須にする。
- fixture root外の`.aup2`を開く要求はテスト開始前に拒否する。
- テスト前後に既存project inventoryのpath、size、mtime、SHA-256を比較する。
- 失敗時もAviUtl2を強制終了する前にログと診断を回収し、fixture以外を削除しない。

[CI]: https://github.com/gomi1124/Aviutl2MCP/actions/workflows/ci.yml
