# AviUtl2 MCP API設計

## 1. 適用範囲

本書はV1で公開する28 tools、5 resources、4 promptsの名前、入力、構造化出力、エラー、annotationsを固定する。機械可読な必須、enum、条件、nested DTO、tool別input/outputは [V1 Schema catalog](../schemas/mcp/v1/catalog.json) を正とする。

- MCP SDK: 公式C# SDK `ModelContextProtocol` 1.4.1
- transport: stdio
- JSON property: `camelCase`
- 未知の入力property: 拒否
- JSON文字列: UTF-8、NUL禁止
- 時刻: UTCのRFC 3339
- UUID: lowercase canonical form
- tool、resource、promptの一覧はAviUtl2未起動時も変えない

## 2. 共通規則

### 2.1 インスタンス選択

すべてのtool入力は次の共通propertyを持つ。

| Property | 型 | 必須 | 規則 |
|---|---|---:|---|
| `instanceId` | UUID string | いいえ | 指定したAviUtl2だけを対象にする |
| `timeoutMs` | integer | いいえ | 100～120000。省略時は操作種別の既定値 |

`instanceId`省略時は、入力中の全Locatorが示す同一instance、環境変数`AVIUTL2_MCP_INSTANCE`、接続可能な唯一のインスタンスの順に選ぶ。top-level指定とLocatorが違う、または複数Locatorのinstanceが違う場合は `invalid_argument` とする。候補が複数なら `aviutl_get_status` と `aviutl://status` だけは候補一覧を返し、その他は `instance_ambiguous` とする。自動選択や自動切替をしない。

### 2.2 座標系

| 値 | 基準 | 範囲・意味 |
|---|---|---|
| `sceneId` | SDK ID | 0以上のinteger |
| `layer` | 1-based | AviUtl2 UI表示と同じ |
| `frame` | 1-based | AviUtl2 UI表示と同じ |
| `startFrame` / `endFrame` | 1-based、両端含む | `startFrame <= endFrame` |
| `occurrence` | 0-based | 同名effectの出現順 |

座標を含む成功応答は `coordinateSystem` を返す。MCP層とApplication層だけが1-based UI座標を扱い、bridge境界でSDK座標へ変換する。

### 2.3 リビジョンと編集共通入力

プロジェクト内容の版を `revision`、カーソルや表示範囲の版を `viewRevision` とする。値は `{instanceEpoch}:{projectGeneration}:{counter}` 形式のopaque stringで、クライアントは分解しない。

プロジェクトを変更するtoolは次を追加で持つ。

| Property | 型 | 必須 | 規則 |
|---|---|---:|---|
| `expectedRevision` | string | はい | 現在値と不一致なら変更せず `revision_conflict` |
| `dryRun` | boolean | いいえ | 既定`false`。`true`では事前検証結果だけを返す |

`aviutl_set_cursor`は内容を変更しないため、`expectedViewRevision`を任意指定する。`dryRun`は持たない。

### 2.4 共通型

#### ObjectLocator

SDKハンドルを公開・保存せず、次の複合ロケーターを使う。

| Property | 型 | 必須 | 規則 |
|---|---|---:|---|
| `instanceId` | UUID string | はい | 取得元インスタンス |
| `projectGeneration` | UUID string | はい | project load/初期化ごとに変わるopaque UUID |
| `sceneId` | integer | はい | 0以上 |
| `layer` | integer | はい | 1以上 |
| `startFrame` | integer | はい | 1以上 |
| `endFrame` | integer | はい | `startFrame`以上 |
| `name` | string | はい | 最大4096文字 |
| `aliasSha256` | lowercase hex string | はい | 64文字 |
| `effectSignatureSha256` | lowercase hex string | はい | ordered effect名と各itemのname/typeから算出した64文字 |

再解決候補が0件なら `object_not_found`、完全指紋一致が複数件なら `object_ambiguous` とし、列挙順や近い候補から推測しない。

#### Placement

`sceneId`、`layer`、`startFrame`、任意の`endFrame`または`durationFrames`を持つ。`endFrame`と`durationFrames`は同時指定不可。作成toolでは一方を必須とする。

`MovePlacement`は`sceneId`、`layer`、`startFrame`だけを持つ。公開SDKのmoveは長さを変更しないため、V1のmoveへ`endFrame`や`durationFrames`を指定できない。

#### EffectDefinitionSelector / EffectInstanceSelector / EffectItemValue

- `EffectDefinitionSelector`: `name`（1～4096文字）。`enum_effect_name`と`create_object`が公開する名称だけを使う。
- `EffectInstanceSelector`: `name`（1～4096文字）と`occurrence`（0以上、既定0）。object内の同名effectを順序で選ぶ。
- `EffectItemValue`: JSON `boolean`、範囲内`integer`、有限`number`、最大64 KiBの`string`のいずれか。`NaN`と無限値は不可。
- codecはitem typeが`integer`なら10進integer、`number`ならinvariant有限小数、`check`なら`0/1`、その他ならSDK alias形式のUTF-8 stringとする。
- `data`、`folder`、未知item typeはV1で読取専用とし、それ以外もSDKが返したtypeと値のround-tripをfixtureで確認できた場合だけ`isWritable=true`にする。
- effect/item名の比較はSDKが返すUTF-8名との完全一致を基本とし、候補一覧をエラー詳細へ含められる。

#### Page

一覧入力は `limit`（1～1000、既定100）と任意のopaque `cursor`を持つ。結果は `items`、`nextCursor`、`isTruncated`を返す。ログだけは上限2000とする。

timeline/object/effect cursorは`instanceId`、`projectGeneration`、`revision`、正規化query SHA-256、最終sort key、有効期限へ束縛し、server process内のephemeral HMAC-SHA-256で改ざんを検出する。ログcursorはlog sourceとring generationへ束縛する。状態・query・server epochが変わったcursorは再開せず `cursor_invalid` とする。

### 2.5 成功・失敗envelope

すべてのtoolは `structuredContent` に次を返す。

```json
{
  "ok": true,
  "correlationId": "019f0000-0000-7000-8000-000000000000",
  "instanceId": "019f0000-0000-7000-8000-000000000001",
  "revision": "epoch:generation:counter",
  "viewRevision": "epoch:generation:counter",
  "data": {},
  "warnings": []
}
```

- 接続前は`instanceId`、プロジェクト未作成時は`revision`を省略できる。
- `warnings`は `{code,message,details}` の配列である。
- 失敗時は `ok=false`、`error={code,message,canRetry,details}` とし、MCP `CallToolResult.isError=true` を設定する。通常は`data`を省略するが、`partial_operation`と結果不明timeoutでは安全に再取得できた適用済み結果を`data`へ含める。
- JSON-RPC不正、未知tool、入力schema不正だけをprotocol errorにする。
- tool errorでも可能な範囲で現在の`revision`、`viewRevision`を返す。
- `aviutl_render_preview`だけはenvelopeに加えてPNGのMCP image contentを1件返す。

## 3. Tool契約

表中の入力は共通propertyを除く。`Locator`は`ObjectLocator`、`Placement`は前節の型を表す。

### 3.1 状態・参照

| Tool | 固有入力 | `data` |
|---|---|---|
| `aviutl_get_status` | なし | `connectionState`、各componentの`version/status`、`projectState`、`editState`、`selectedInstance`、候補`instances[]` |
| `aviutl_get_capabilities` | なし | 28操作ごとの`available/reason/constraints`、protocol/schema/bridge版、V1制限値 |
| `aviutl_get_project` | `includeScenes?: boolean=true` | `path?`、`isSaved`、解像度、frame/sample rate、current scene/frame、選択・表示範囲、`scenes[]` |
| `aviutl_get_timeline` | `sceneId?`、`layerStart?`、`layerEnd?`、`startFrame?`、`endFrame?`、`detail?: "summary"|"effects"="summary"`、Page | `layers[]`、`objects[]`、Page、`coordinateSystem` |
| `aviutl_find_objects` | `sceneId?`、`layerStart?`、`layerEnd?`、`startFrame?`、`endFrame?`、`nameContains?`、`effectName?`、`mediaPath?`、Page | 条件に一致する`objects[]`とPage |
| `aviutl_get_object` | `locator: Locator`、`includeAlias?: boolean=false`、`includeEffectItems?: boolean=true` | `object`、effectごとの`effectItems[]`、選択状態、任意のUTF-8 `alias` |
| `aviutl_list_effects` | `category?`、`nameContains?`、Page | `effects[]`（name/type/flags/isCreatable）、独立した`modules[]`、`fonts[]`、`palettes[]` |
| `aviutl_list_effect_items` | `effect: EffectDefinitionSelector`、`includeChoices?: boolean=true` | `items[]`（name/type/codec/isWritable）。font/select等で公開列挙できる`choices`だけ任意付与 |

検索文字列は1～4096文字、パスは正規化後32767文字以下とする。`get_timeline`で範囲を省略した場合は現在の表示範囲を使う。

`aviutl_get_capabilities`の`constraints[]`は`{name,value,unit}`、`versions`はserver/schema/protocolとbridge/AviUtl2/SDK/PSDToolKit2/GCMZDropsの固定property、`limits`は[設計書の制限値](design.md#13-制限値)に対応する固定propertyとする。未検出componentのversionは`null`で返し、自由形式objectは返さない。

### 3.2 タイムライン編集

次のtoolは `aviutl_set_cursor` を除き、すべて `expectedRevision` と任意の `dryRun` を持つ。

| Tool | 固有入力 | `data` |
|---|---|---|
| `aviutl_create_object` | `effect: EffectDefinitionSelector`、`placement: Placement`、`name?`、`items?: {name,value}[]` | `object`、`plannedChanges[]`または`appliedChanges[]` |
| `aviutl_create_media_object` | `mediaPath: string`、`placement: Placement`、`name?` | `object`、検出media type、適用duration |
| `aviutl_create_alias_object` | `alias: string`（UTF-8最大1 MiB）、`placement: Placement`、`name?` | 作成された`objects[]`。alias内に複数Object sectionを許可 |
| `aviutl_move_object` | `locator: Locator`、`placement: MovePlacement` | 更新後`object`、移動前後。長さは不変 |
| `aviutl_delete_object` | `locator: Locator` | 削除前の`object`、`deleted: true` |
| `aviutl_set_object_name` | `locator: Locator`、`name: string`（0～4096文字） | 更新後`object` |
| `aviutl_set_effect_item` | `locator: Locator`、`effect: EffectInstanceSelector`、`itemName: string`、`value: EffectItemValue` | codec正規化後value、更新後effect item |
| `aviutl_set_effect_state` | `locator: Locator`、`effect: EffectInstanceSelector`、`isEnabled?`、`isLocked?` | 更新後effect state。少なくとも一方必須 |
| `aviutl_set_layer` | `sceneId?`、`layer: integer`、`name?`、`isVisible?`、`isLocked?` | 更新後layer。変更propertyを1つ以上必須 |
| `aviutl_set_cursor` | `sceneId?`、`frame?`、`displayFrame?`、`selection?: {startFrame,endFrame}`、`expectedViewRevision?` | 更新後のcurrent/display/selection、`viewRevision` |

作成・移動は対象範囲、layer lock、object衝突を事前検証する。ファイルtoolは絶対パスへ正規化し、存在、通常ファイル、許可拡張子を検証する。シェルや関連付け実行は行わない。

### 3.3 Batch

`aviutl_execute_batch`は次を入力とする。

| Property | 型 | 必須 | 規則 |
|---|---|---:|---|
| `expectedRevision` | string | はい | batch開始直前に再照合 |
| `dryRun` | boolean | いいえ | 既定`false` |
| `operations` | array | はい | 1～100件 |
| `operations[].op` | enum | はい | 下記9種類 |
| `operations[].clientOperationId` | string | はい | batch内一意、1～128文字 |
| `operations[].args` | object | はい | 対応する単体toolの固有入力 |

許可する`op`は `createObject`、`createMediaObject`、`createAliasObject`、`moveObject`、`deleteObject`、`setObjectName`、`setEffectItem`、`setEffectState`、`setLayer` とする。batch、cursor、preview、logs、diagnose、PSD/GCMZDrops操作は入れない。

V1は前操作の結果を後続`args`から参照する式を持たず、Locatorはbatch開始前に存在する対象だけを指す。全件を計画状態上で事前検証し、1回のSDK edit section、1 Undo単位で順に実行する。`data`は `results[]`、`appliedOperationIds[]`、`undoRecommended` を返す。`results[]`は`clientOperationId/op/status/changes`を必須とし、必要な場合だけ`object`、`objects`、`error`を持つ閉じたDTOとする。開始後の失敗は成功扱いせず `partial_operation` と現在状態を返す。

### 3.4 Preview・ログ・診断

| Tool | 固有入力 | `data` |
|---|---|---|
| `aviutl_render_preview` | `sceneId?`、`frame: integer`、`maxWidth?: 1..4096`、`maxHeight?: 1..4096`、`includeAlpha?: boolean=false` | `mimeType="image/png"`、width/height/frame、`sha256`、byteLength。PNG image contentを併記 |
| `aviutl_get_logs` | `sources?: ("server"|"bridge"|"aviutl")[]`、`levels?`、`since?`、`correlationId?`、`limit?: 1..2000=100`、`cursor?` | マスク済み`entries[]`とPage |
| `aviutl_diagnose` | `includeReadSmoke?: boolean=false`、`includePreviewSmoke?: boolean=false`、`maxLogLines?: 0..2000=100` | `checks[]`、総合status、component graph、既知ログ分類、推奨対処 |

`maxWidth`と`maxHeight`は両方指定または両方省略とし、省略時は1920x1080をbounding boxにする。元画像のaspect ratioを維持してbox内へ縮小し、拡大はしない。`includeAlpha=false`では透明画素をopaque blackへcompositeし、24-bit PNGにする。診断のsmokeは読取とpreviewだけで、自動修復や編集をしない。previewを同時実行できるのは1件で、ロック外で非同期待機する。

診断の`components[]`は`name/status/version/evidence`、`knownLogMatches[]`は`ruleId/source/severity/evidence/impact/recommendation`だけを持つ閉じたDTOとする。

### 3.5 PSDToolKit2 / GCMZDrops

PSD編集toolは `expectedRevision` と任意の `dryRun` を持つ。`aviutl_psd_validate`だけは読取専用で持たない。

| Tool | 固有入力 | `data` |
|---|---|---|
| `aviutl_psd_create` | `psdPath: string`（`.psd`/`.psb`）、`placement: Placement`、`name?` | 作成後`object`、PSDToolKit2 effect、事後検索根拠 |
| `aviutl_psd_setup` | `sceneId?`、`preferredLayer?: integer`、`preferredFrame?: integer`、`createIfMissing?: boolean=true` | 初期化objectの検出・配置・作成結果、警告 |
| `aviutl_psd_set_character` | `locator: Locator`、`characterId: string`（1～256文字） | 更新後character ID、関連effect items、`object` |
| `aviutl_psd_set_layer_state` | `locator: Locator`、`layerState: string`（UTF-8で1～65536 bytes） | round-trip確認済みのcanonical PSD layer state |
| `aviutl_psd_create_voice` | `audioPath: string`（`.wav`）、`textPath?: string`（`.txt`）、`labPath?: string`（`.lab`）、`characterId: string`（1～256文字）、`psdLocator?: Locator`、`placement: Placement` | 音声、セリフ準備、字幕objects、伴随ファイル検出、事後検索結果 |
| `aviutl_psd_validate` | `locator?: Locator`、`scope?: "object"|"scene"="object"`、`checks?: ("setup"|"character"|"blink"|"lipSync"|"subtitle")[]` | checkごとのstatus/evidence/impact/recommendation |

- `scope="object"`では`locator`必須、`scope="scene"`では任意とする。
- `textPath`省略時は`audioPath`と同名の`.txt`を使用し、存在しなければ `invalid_media_file` とする。`labPath`省略時は同名`.lab`があれば使い、なければlip-sync検証へ警告を付ける。
- voice投入はPSDToolKit2設定に応じてWAV/TXT直接経路または音声・テキスト2 objectの中間`.object`経路を使う。両経路とも無効なら `capability_not_available` とする。
- PSD作成とvoice作成はGCMZDropsのreceiptだけで成功とせず、SDK再検索で生成物を確認する。
- GCMZDropsが一部だけ生成した場合は `partial_operation` と検出済み生成物を返す。
- PSDToolKit2固有名だけで対象判定せず、aliasとeffect列挙結果を組み合わせる。
- version profile、layer state codec、setup生成、字幕templateは[PSD互換契約](psd-contract.md)に従う。

## 4. Tool annotations

`openWorldHint`は全toolで`false`とする。ローカルファイルを読むtoolも外部ネットワークへ接続しない。

| Tool | readOnly | destructive | idempotent |
|---|---:|---:|---:|
| `aviutl_get_status` | true | false | true |
| `aviutl_get_capabilities` | true | false | true |
| `aviutl_get_project` | true | false | true |
| `aviutl_get_timeline` | true | false | true |
| `aviutl_find_objects` | true | false | true |
| `aviutl_get_object` | true | false | true |
| `aviutl_list_effects` | true | false | true |
| `aviutl_list_effect_items` | true | false | true |
| `aviutl_create_object` | false | true | false |
| `aviutl_create_media_object` | false | true | false |
| `aviutl_create_alias_object` | false | true | false |
| `aviutl_move_object` | false | true | true |
| `aviutl_delete_object` | false | true | true |
| `aviutl_set_object_name` | false | true | true |
| `aviutl_set_effect_item` | false | true | true |
| `aviutl_set_effect_state` | false | true | true |
| `aviutl_set_layer` | false | true | true |
| `aviutl_set_cursor` | false | false | true |
| `aviutl_execute_batch` | false | true | false |
| `aviutl_render_preview` | true | false | true |
| `aviutl_get_logs` | true | false | true |
| `aviutl_diagnose` | true | false | true |
| `aviutl_psd_create` | false | true | false |
| `aviutl_psd_setup` | false | true | false |
| `aviutl_psd_set_character` | false | true | true |
| `aviutl_psd_set_layer_state` | false | true | true |
| `aviutl_psd_create_voice` | false | true | false |
| `aviutl_psd_validate` | true | false | true |

annotationsはヒントであり認可や再試行の根拠にしない。編集要求の自動再試行はIPC request IDが同じ場合だけ許可する。

## 5. Resources

| URI | MIME | 内容・失敗時 |
|---|---|---|
| `aviutl://status` | `application/json` | `aviutl_get_status`相当。未接続も構造化状態として成功 |
| `aviutl://capabilities` | `application/json` | `aviutl_get_capabilities`相当 |
| `aviutl://project/current` | `application/json` | 現在project概要。未作成は`project_not_open` envelope |
| `aviutl://timeline/current` | `application/json` | 現在表示中のtimeline概要。上限100件 |
| `aviutl://diagnostics/latest` | `application/json` | 最後に完了した診断。未実行なら`data=null`と時刻なし |

resourceは読取専用で副作用を持たない。引数を持てないため、複数インスタンス時は共通選択規則に従い、曖昧なら `instance_ambiguous` envelopeを返す。

## 6. Prompts

prompt取得自体はtoolを呼び出さず、次の手順テンプレートだけを返す。

| Prompt | Arguments | 出力手順 |
|---|---|---|
| `edit_timeline_safely` | `objective: string` | status/capabilities取得、対象読取、dry-run、revision付き編集、再取得、必要時preview |
| `setup_psd_character` | `psdPath: string`、`characterId: string` | PSD能力診断、setup検証、PSD作成、ID設定、レイヤー構成検証 |
| `add_voice_and_subtitle` | `audioPath: string`、`characterId: string` | 伴随TXT/LAB検証、dry-run、voice作成、事後検索、口パク・字幕検証 |
| `diagnose_aviutl` | `includePreview?: boolean=false` | status、capabilities、diagnose、根拠ログ、推奨対処を安全な順で確認 |

## 7. エラーコード

Phase 1の共通エラーに加えて、設計で確定した次を使う。

| Code | canRetry | 意味 |
|---|---:|---|
| `instance_ambiguous` | false | 対象AviUtl2が複数あり選択が必要 |
| `invalid_argument` | false | schemaでは表せない入力間の制約に違反した |
| `cursor_invalid` | false | cursorの対象状態、query、期限または署名が一致しない |
| `bridge_busy` | true | bridgeの接続または待ち行列が上限 |
| `request_id_conflict` | false | 同じIPC request IDへ異なるpayloadが使われた |
| `request_expired` | false | 変更request IDの安全な再送受付期間が終了した |
| `request_result_evicted` | false | 変更は完了済みだが完全応答cacheが失効した |
| `operation_cancelled` | false | commit point前に取消され変更されなかった |
| `protocol_incompatible` | false | MCPサーバーとbridgeのprotocol major範囲が一致しない |

`operation_timeout`と`partial_operation`の`details`は `commitPointReached`、`postconditionChecked`、`observedChanges[]` を返せる。秘密情報、字幕全文、alias全文、ユーザーディレクトリはエラーへ含めない。

## 8. Schema生成と検証

- 入力DTOはDataAnnotationsとApplicationの実行時検証を併用する。
- JSON deserializationは未知propertyを拒否し、整数overflow、無効enum、非有限numberを拒否する。
- Phase 4でDTOから生成した28 input/output schemasを `schemas/mcp/v1/catalog.json` の参照解決済みschemaと比較する。
- `tools/list`のname、description、inputSchema、outputSchema、annotationsをcatalogに対してsnapshot testする。
- schemaの破壊的変更はprotocol/schema major変更なしに行わない。
- 各toolの正常、入力不正、未接続、能力不足、timeoutをMCP contract testで確認する。

## 9. V1非対象

- 任意AviUtl2 SDK関数の呼出し
- ユーザー指定スクリプト、コマンド、正規表現の実行
- PSDToolKit2非公開IPC
- HTTP/SSE/Streamable HTTP transport
- tool動的追加、外部プラグイン機構
- resource subscriptionとserver-originated notification
