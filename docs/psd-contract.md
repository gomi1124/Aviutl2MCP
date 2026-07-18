# PSDToolKit2 / GCMZDrops V1互換契約

## 1. 目的と境界

V1はPSDToolKit2の非公開IPCを使わず、AviUtl2公開SDK、GCMZDrops外部API v3、公開されたeffect/item値だけを使う。本書に一致しない版や構成では基本MCP機能を維持し、該当PSD書込み能力だけを無効化する。

基準profileは、検証環境に導入済みのPSDToolKit2 `2.0.0alpha10` とする。判定は表示versionだけを信用せず、effectとitemの列挙結果も照合する。

## 2. `ptk2-2.0.0alpha10-ja` profile

### 2.1 必須effectとitem

| Role | Effect definition | 必須item | SDK type |
|---|---|---|---|
| setup | `最初に置くやつ@PSDToolKit` | なし | - |
| PSD object | `PSDファイル@PSDToolKit` | `PSDファイル` | `file` |
| PSD object | 同上 | `セーフガード` | `check` |
| PSD object | 同上 | `タグ` | `string` |
| PSD object | 同上 | `シーンID` | `integer` |
| PSD object | 同上 | `キャラクターID` | `string` |
| PSD object | 同上 | `レイヤー` | `string` |
| voice prep | `セリフ準備@PSDToolKit` | `キャラクターID` | `string` |
| voice prep | 同上 | `テキスト` | `text` |
| voice prep | 同上 | `音声ファイル` | `file` |

目パチ・口パク検証は、`PSDToolKit.ini`の `[anm2Editor.AnimationScripts]` をUTF-8で読んで次のcanonical module名をeffect名へ解決する。

| Canonical module | 2.0.0alpha10既定effect |
|---|---|
| `PSDToolKit.Blinker` | `目パチ@PSDToolKit` |
| `PSDToolKit.LipSync` | `口パク 開閉のみ@PSDToolKit` |
| `PSDToolKit.LipSyncLab` | `口パク あいうえお@PSDToolKit` |

`PSDToolKit.user.ini`があれば同sectionだけを上書きmergeする。INI外の任意scriptを実行しない。effect名はmerge後にSDK列挙結果と完全一致した場合だけ採用する。

### 2.2 Profile検出

1. `enum_module_info`のinformationからPSDToolKit versionを取得する。
2. `enum_effect_name`にsetup、PSD、voiceの3 definitionが存在することを確認する。
3. `enum_effect_item`で上表のitem名とtypeを照合する。
4. `GetModuleHandleW`と`GetModuleFileNameW`でロード済み`PSDToolKit.aux2`の実体を特定し、同じdirectoryの`PSDToolKit.json`をUTF-8 JSONとして読む。
5. PSD/GCMZ操作ではGCMZDrops API v3、HWND、PID、project状態も照合する。
6. 1項目でも違えば、汎用timeline readは継続し、`psdWrite`、`psdLayerState`または`psdVoice`の該当能力だけを `available=false` にする。

effect名は2.0.0alpha10のscript annotationに由来する日本語canonical値で、UI localeから翻訳しない。将来版や翻訳名は別profileを追加してfixtureを通すまで推測で有効化しない。

`psdVoice`はprofile一致に加え、設定値を次の順で評価する。

1. `external_wav_txt_pair=true`: `direct-wav-txt`経路を選ぶ。
2. それ以外で`external_object_audio_text=true`: `intermediate-object-audio-text-v1`経路を選ぶ。
3. 両方false、property欠落、JSON不正、設定ファイル不明: `available=false`とし、自動で設定を書き換えない。

検証環境では `C:\ProgramData\aviutl2\Plugin\PSDToolKit\PSDToolKit.json` に `external_wav_txt_pair=false`、`external_object_audio_text=true` が設定されている。この値は環境fixtureであり、実装時は上記module実体から解決する。

## 3. 作成経路

### 3.1 Setup

`aviutl_psd_setup`は `最初に置くやつ@PSDToolKit` をeffect definition一覧から完全一致で取得し、公開SDK `create_object`で作成する。aliasやPSDToolKitウィンドウ操作は使わない。

- scene内の既存setupをeffect signatureで列挙する。
- setupより上のlayerにPSDToolKit関連objectがない配置を候補にする。
- 既存が複数、範囲不足、配置衝突なら自動移動せず警告または `object_ambiguous` とする。
- 作成する場合はprojectの先頭から末尾までの長さを使い、1 Undo単位にする。

### 3.2 PSD / voice

PSD/PSBとWAV/TXTの投入はGCMZDrops API v3を使い、[IPC契約の複合操作](ipc-protocol.md)に従って事後検索する。生成後に上表のeffect/item構成を満たさないobjectは成功対象にしない。

- voiceでは`.wav`とUTF-8 `.txt`を必須とする。`textPath`省略時は同basenameの`.txt`を使い、なければ `invalid_media_file` とする。
- `characterId`は必須とし、1～256文字、NUL/CR/LF禁止とする。PSDToolKit2がWAVファイル名から推測したIDを成功条件にせず、生成後に公開SDKで指定値へ設定してround-trip検証する。
- `.lab`は任意。同basenameが存在すれば使い、なければあいうえお口パク検証をwarningにする。
- GCMZDropsへ渡すpathは正規化済み絶対pathで、対象AviUtl2 PIDを照合する。

#### 3.2.1 `direct-wav-txt`

`external_wav_txt_pair=true`のときだけ使う。GCMZDrops API v3の1要求へ、同basenameの正規化済みWAVとTXTを各1件、計2件として渡す。TXTは変更せず、PSDToolKit2側のUTF-8読取と改行escapeに委ねる。

#### 3.2.2 `intermediate-object-audio-text-v1`

`external_object_audio_text=true`のときだけ使う。Applicationは `%LOCALAPPDATA%\AviUtl2MCP\v1\temp\{correlationId}\voice.object` をcurrent userだけがアクセスできるdirectoryにUTF-8 BOMなしで作成し、GCMZDrops API v3へこの1ファイルだけを渡す。内容は次の完全な形とし、余分な`[2]`以降やsubsectionを許可しない。

```ini
[0]
frame=0,0
[0.0]
effect.name=音声ファイル
ファイル=__NORMALIZED_WAV_PATH__
[1]
frame=0,0
[1.0]
effect.name=テキスト
テキスト=__ESCAPED_TEXT__
```

- `__NORMALIZED_WAV_PATH__`はNUL/CR/LFを含まない正規化済み絶対pathとする。
- `__ESCAPED_TEXT__`はUTF-8 TXTを厳密decodeし、CRLF、CR、LFを順にliteral `\n`へ置換した1行値とする。NULと64 KiB超過を拒否する。
- 両`frame`のstartは同じ`0`とし、`effect.name`とitem keyは上記canonical値に完全一致させる。
- GCMZDrops送信、生成物の再検索、character ID設定、字幕作成、事後条件判定が完了した後にtemp directoryを再解析して相関ID配下であることを確認し、best-effortで削除する。失敗時も同じcleanupを行い、削除失敗はwarningとしてpathをマスクして記録する。

PSDToolKit2の`wav.lua`が要求する「ちょうど2 object、同一start frame、音声ファイル1件、テキスト1件、WAV拡張子」の条件をこのcodecのcontract testへ固定する。

## 4. PSD layer state codec

V1は人間向け`layerPath + isVisible`からPSDToolKit固有値を推測生成しない。公開APIが返す `PSDファイル@PSDToolKit` の `レイヤー` itemをcanonicalなopaque UTF-8 `layerState`として取得・設定する。

`aviutl_psd_set_layer_state`の入力:

| Property | 型 | 規則 |
|---|---|---|
| `locator` | ObjectLocator | profile一致するPSD object |
| `layerState` | string | 1～65536 bytes、NUL/CR/LF禁止 |

許可する値は初期値`L.0`、またはPSDToolKit2が扱う`v0.`/`v1.`状態を1つ以上含む一行値とする。bridgeはpathのescape、結合、可視・非表示変換を行わず、受け取ったcanonical値をそのままSDK alias形式で設定する。

書込み前後に次を確認する。

1. locator、revision、effect/item名とtypeがprofileに一致する。
2. `PSDファイル`の正規化pathと、そのUTF-8 path文字列のSHA-256がpreflightから変わっておらず、`.psd`/`.psb`が存在する。大きなPSD本体を操作ごとにhashしない。
3. `セーフガード`を変更せず、変更対象が`レイヤー`1項目だけである。
4. 書込み後に同itemを再取得し、UTF-8 byte列が入力と完全一致する。

一致しない場合は成功にせず、変更有無に応じて `invalid_argument` または `partial_operation` とする。未知profile、未知item type、複数PSD effectでは能力を無効化する。

## 5. キャラクターIDと字幕alias

`aviutl_psd_set_character`はprofile一致するPSDまたはvoice effectの `キャラクターID` itemだけを変更し、1～256文字、NUL/CR/LF禁止とする。関連objectを名称だけで一括更新せず、指定locatorだけを変更する。

字幕表示はV1配布物に同梱するversioned template `assets/psdtoolkit2/v1/subtitle.object`から作る。

- build manifestにtemplateのSHA-256を記録し、実行時に一致を確認する。
- UTF-8、最大64 KiB、Object section 1件、effect `テキスト` 1件、placeholder `__AVIUTL2_MCP_CHARACTER_ID__` 1件を必須とする。
- placeholderはcharacter IDをLua double-quoted stringとしてescapeして置換する。`\`、`"`、制御byteをescapeし、生の改行や終端を挿入しない。
- 生成aliasに `require("PSDToolKit").mes` が1回だけ含まれることを検証する。
- 任意のユーザーaliasやscriptを自動探索・実行しない。template不一致では `capability_not_available` とする。

字幕本文はaliasへ埋め込まず、GCMZDropsが作成した `セリフ準備@PSDToolKit` の `テキスト` と `キャラクターID` をPSDToolKit2が参照する。字幕本文、alias全文、audio/text pathは通常ログへ出さない。

## 6. 能力と診断

status/capabilities/diagnoseは次を別々に返す。

- detected PSDToolKit version、selected profile、profile一致根拠
- 必須effect/itemごとのpresent/typeMatch
- GCMZDrops API/Mutex/FMO/HWND/PID/project一致
- subtitle template path/version/hashMatch
- `psdCreate`、`psdSetup`、`psdCharacter`、`psdLayerState`、`psdVoice`、`psdValidate`ごとのavailable/reason

診断はprofileやtemplateを自動更新せず、未知版では検出結果と推奨更新手順だけを返す。

## 7. Contract tests

- 2.0.0alpha10のeffect/item golden fixtureがprofileに一致する
- effect/item欠落、type変更、未知versionで該当write能力だけ無効になる
- `PSDToolKit.user.ini`の上書きと無効UTF-8を安全に処理する
- `layerState`の`L.0`、v0/v1、過大値、NUL/改行、round-trip不一致
- PSD path差替え、セーフガード維持、複数PSD effectを拒否する
- subtitle templateのhash、section、placeholder、Lua escapeを検証する
- `characterId`必須と生成後round-tripを検証する
- `external_wav_txt_pair=true`の直接経路と`external_object_audio_text=true`の中間object経路を個別に検証する
- 両設定false、欠落、不正JSONで`psdVoice`だけが無効になり、設定を変更しないことを検証する
- 中間objectのsection数、frame一致、canonical key、UTF-8、改行escape、NUL、上限、cleanupを検証する
- text必須、LAB任意、GCMZDrops部分生成と誤配置を検証する
