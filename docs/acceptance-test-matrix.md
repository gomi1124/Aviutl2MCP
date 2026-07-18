# V1受け入れテスト対応表

## 1. 運用

各Phase 1受け入れ基準を、Phase 4で実装する自動・実機テストへ一対一で対応付ける。`Test ID`はテスト名またはCI job名に含め、完了時に`Evidence`へworkflow URLまたは実機レポートを記録する。

| AC ID | Test ID | 層・環境 | 合格条件 | Evidence |
|---|---|---|---|---|
| AC-BLD-001 | `build.clean-windows` | Windows CI | locked restoreからServer/Bridgeを警告エラーなしでbuild | Phase 4 |
| AC-BLD-002 | `ci.required-jobs` | GitHub Actions | managed/native/contract/integration jobが全成功 | Phase 4 |
| AC-BLD-003 | `real.package-install` | 専用AviUtl2実機 | `.au2pkg.zip`導入後にbridge versionをstatusで取得 | Phase 4 |
| AC-MCP-001 | `stdio.offline-initialize` | stdio black-box | AviUtl2なしでinitializeと28 tools listが成功 | Phase 4 |
| AC-MCP-002 | `pipe.late-connect` | fake bridge統合 | Server維持中にbridge起動しReadyへ遷移 | Phase 4 |
| AC-MCP-003 | `mcp.catalog-snapshot` | MCP contract | 28 tools、5 resources、4 promptsとSchema catalogが一致 | Phase 4 |
| AC-MCP-004 | `stdio.stdout-purity` | stdio black-box | stdoutの全frameが有効MCP message、ログはstderrのみ | Phase 4 |
| AC-MCP-005 | `pipe.instance-selection` | fake bridge統合 | 複数時は曖昧拒否、明示IDだけへ要求送信 | Phase 4 |
| AC-EDT-001 | `real.timeline-read` | 専用AviUtl2実機 | fixtureのscene/layer/object/effect DTOがgolden値と一致 | Phase 4 |
| AC-EDT-002 | `real.object-create-three-ways` | 専用AviUtl2実機 | media/effect/alias各方式の生成物を再取得 | Phase 4 |
| AC-EDT-003 | `real.object-edit-lifecycle` | 専用AviUtl2実機 | move/name/item/state/deleteを再取得で確認 | Phase 4 |
| AC-EDT-004 | `app.dry-run-no-change` | Application単体＋実機 | revision、object snapshot、Undo履歴が不変 | Phase 4 |
| AC-EDT-005 | `app.revision-conflict` | Application/pipe統合 | 旧revisionを拒否し状態不変、連続2編集でも検出 | Phase 4 |
| AC-EDT-006 | `real.batch-single-undo` | 専用AviUtl2実機 | batch全変更が1回のUndoでgolden snapshotへ戻る | Phase 4 |
| AC-EDT-007 | `app.stable-edit-errors` | Application単体 | not-found/collision/play/saveを別codeで返す | Phase 4 |
| AC-EDT-008 | `bridge.batch-partial` | native fake＋実機 | N件目失敗で適用ID/状態/Undo推奨、1 Undo復旧 | Phase 4 |
| AC-PSD-001 | `real.psd-create` | 専用PSD実機 | PSD投入後にprofile一致objectをSDK再検索 | Phase 4 |
| AC-PSD-002 | `real.psd-setup` | 専用PSD実機 | 不足/誤配置を検出し、安全候補へsetupを作成 | Phase 4 |
| AC-PSD-003 | `real.psd-character-layer` | 専用PSD実機 | character IDとcanonical layerStateがround-trip一致 | Phase 4 |
| AC-PSD-004 | `real.psd-voice-subtitle` | 専用PSD実機（中間object）＋2設定contract fixture | 両経路の契約と、必須ID付きvoice prep・字幕の実機再検索 | Phase 4 |
| AC-PSD-005 | `real.psd-lipsync-lab` | 専用PSD実機 | 同basename LABとLipSyncLab構成を検証 | Phase 4 |
| AC-PSD-006 | `app.psd-capability-isolation` | fake profile/GCMZ | GCMZ無効でも基本tool成功、voiceだけ能力エラー | Phase 4 |
| AC-PSD-007 | `bridge.gcmz-partial` | fake GCMZ＋実機 | timeout/一部/誤配置で検出物付きpartialを返す | Phase 4 |
| AC-DIA-001 | `real.preview-image` | 専用AviUtl2実機 | PNG signature、寸法、非空pixel、MCP image content | Phase 4 |
| AC-DIA-002 | `smoke.before-after-diff` | Application＋実機 | 既知変更のrevisionまたはpixel差を検出 | Phase 4 |
| AC-DIA-003 | `diagnostics.known-log-rules` | Diagnostics単体 | 3 fixtureを根拠行/影響/推奨対処へ分類 | Phase 4 |
| AC-DIA-004 | `diagnostics.pipe-recovery` | fake bridge統合 | 切断checkが失敗し、再接続後に正常化 | Phase 4 |
| AC-DIA-005 | `bridge.render-lifetime-stress` | native fake＋実機 | 正常/timeout/late callback反復でhang/UAF/double freeなし | Phase 4 |
| AC-DIA-006 | `ipc.mutation-at-most-once` | IPC contract | 応答消失再送で1編集、payload差はconflict | Phase 4 |
| AC-SAF-001 | `real.fixture-process-guard` | 実機harness | temp fixtureとharness PID以外を開かず、既存aup2 hash不変 | Phase 4 |
| AC-SAF-002 | `pipe.cross-logon-denied` | Windows統合 | 別logon SIDのclientがpipe open/handshake不可 | Phase 4 |
| AC-SAF-003 | `fuzz.input-boundaries` | managed/native fuzz | 過大長/UTF-8/pathを拒否後もstatus応答 | Phase 4 |
| AC-SAF-004 | `bridge.handle-lifetime` | native fake＋sanitizer | callback後handle/pointer利用をguardまたはASanが検出 | Phase 4 |

## 2. 実機ガード

- 実機テストは`--real`、専用fixture root、harnessが起動したAviUtl2 PIDの3条件を必須にする。
- fixture root外の`.aup2`を開く要求はテスト開始前に拒否する。
- テスト前後に既存project inventoryのpath、size、mtime、SHA-256を比較する。
- 失敗時もAviUtl2を強制終了する前にログと診断を回収し、fixture以外を削除しない。
