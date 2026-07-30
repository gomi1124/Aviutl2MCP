# AviUtl2 MCP V1 実現性マトリクス

## 1. 目的

Phase 1 で列挙した MCP インターフェースを、AviUtl2 公開SDK、PSDToolKit2、GCMZDrops の具体的な公開機能へ対応付ける。ここでは実装方式を確定せず、V1要件が公開APIだけで実現可能か、追加の制約が必要かを監査する。

## 2. 調査元

- [AviUtl2 SDK `plugin2.h`](https://github.com/aviutl2/aviutl2_sdk_mirror/blob/main/include/aviutl2_sdk/plugin2.h)
- [PSDToolKit2 README](https://github.com/oov/aviutl2_psdtoolkit2/blob/main/README.md)
- [PSDToolKit2 公開ソース](https://github.com/oov/aviutl2_psdtoolkit2)
- [GCMZDrops 外部連携API](https://github.com/oov/aviutl2_gcmzdrops2/blob/main/API.md)

調査対象は2026-07-29時点の公開リポジトリと、ローカルに導入済みの AviUtl ExEdit2 2.1.2、PSDToolKit2、GCMZDrops とする。2026-07-25版の公式SDKと固定submoduleは、文字コード変換に伴う波ダッシュ表現を除き同一であることを確認済み。

## 3. 判定区分

| 区分 | 意味 |
|---|---|
| 直接 | 主処理をAviUtl2公開SDKの単一セクションまたは単一API群で実行できる |
| 複合 | 公開SDK、GCMZDrops、MCPサーバー処理の複数を順序制御する |
| サーバー | MCPサーバー内の変換、集約、ファイル読取だけで実行する |
| 実験 | 公開APIの裏付けがなく、V1必須機能として成功を保証しない |

## 4. MCP Tools

| Tool | 区分 | 公開API・処理根拠 | 制約・実装時の確認事項 |
|---|---|---|---|
| `aviutl_get_status` | 複合 | `get_edit_state`、`get_edit_info`、`enum_module_info`、IPC状態、GCMZDrops共有メモリ | AviUtl2未起動、プロジェクト未作成、再生、出力を別状態として返す |
| `aviutl_get_capabilities` | サーバー | 状態、APIバージョン、モジュール列挙結果から能力表を生成 | 未検証機能を推測で有効化せず、理由と依存先を返す |
| `aviutl_get_project` | 複合 | `get_edit_info`、編集セクション内の `get_project_file` と `PROJECT_FILE::get_project_file_path`、GCMZDrops `ProjectPath` | `get_project_file` は読取セクションで利用不可。未保存時はパスが空なので、空パスだけで未作成と判定しない |
| `aviutl_save_project` | 複合 | host windowの既存「プロジェクトを保存」command、`register_project_save_handler` | command IDは固定せずmenu表示名から解決する。名前付きprojectだけを対象とし、callback未確認時は結果を`unknown`として再実行しない |
| `aviutl_get_timeline` | 直接 | `call_read_section`、`find_object`、`get_object_layer_frame`、レイヤー取得API | `layer_max` はオブジェクト存在範囲。要求範囲、件数、詳細度の上限が必要 |
| `aviutl_find_objects` | 直接 | `call_read_section`、`find_object`、名称・エイリアス・エフェクト取得API | SDKハンドルは応答へ出さず、その場でロケーターへ変換する |
| `aviutl_get_object` | 直接 | オブジェクト、区間、トラック、チェック、エフェクト、項目取得API | ロケーターを毎回再解決し、複数一致は `object_ambiguous` とする |
| `aviutl_list_effects` | 直接 | `enum_effect_name`、`enum_module_info` | 種別・対応映像/音声フラグを保持する |
| `aviutl_list_effect_items` | 直接 | `enum_effect_item` | 項目型を保持し、未知型は書込み不可として公開する |
| `aviutl_create_object` | 直接 | `create_object` | エフェクト名、位置、長さ、衝突を編集前に検証する |
| `aviutl_create_media_object` | 直接 | `is_support_media_file`、`get_media_info`、`create_object_from_media_file` | PSD/PSBはこの経路に含めず、PSDToolKit2のドロップハンドラーを通す |
| `aviutl_create_alias_object` | 直接 | `create_object_from_alias` | UTF-8形式、最大サイズ、複数オブジェクト、衝突範囲を検証する |
| `aviutl_move_object` | 直接 | `move_object` | 編集セクション内で再解決し、移動先衝突を再確認する |
| `aviutl_delete_object` | 直接 | `delete_object` | dry-run、リビジョン、対象再解決を必須にする |
| `aviutl_set_object_name` | 直接 | `set_object_name` | UTF-16変換後の長さと空文字の既定名称化を扱う |
| `aviutl_create_object_section` | 直接 | `create_object_section`、`get_object_section_num`、`get_object_section_frame` | object先頭と重複せず、object範囲内のframeだけを許可する |
| `aviutl_delete_object_section` | 直接 | `delete_object_section`、`get_object_section_num` | 0番区間はobject先頭なので削除せず、1以上の既存indexだけを許可する |
| `aviutl_move_object_section` | 直接 | `move_object_section`、`get_object_section_frame` | 前後の区間開始を跨がないframeだけを許可し、事後状態を再取得する |
| `aviutl_set_effect_item` | 直接 | `get_effect_item_value`、`set_effect_item_value`、項目別トラック・チェック設定API | 列挙した項目型に応じて入力を検証する |
| `aviutl_set_effect_state` | 直接 | `set_effect_enable`、`set_effect_lock` | 既存エフェクトだけを対象とし、追加・削除・並べ替えは行わない |
| `aviutl_set_layer` | 直接 | `set_layer_name`、`set_layer_enable`、`set_layer_lock` | UI表示番号とSDKの0始まり番号を境界で変換する |
| `aviutl_set_cursor` | 直接 | `set_cursor_layer_frame`、`set_display_layer_frame`、`set_select_range` | SDK側で補正された実値を再取得して返す |
| `aviutl_execute_batch` | 複合 | サーバー側事前検証後、1回の `call_edit_section_param` 内で各編集APIを実行 | 1 Undo単位にはできるが、編集開始後のAPI失敗を自動ロールバックする公開APIはない。部分適用を検出して明示する |
| `aviutl_render_preview` | 複合 | `rendering_scene_video`、`wait_rendering_task`、bridge側WIC PNG変換 | 描画は非同期。`wait_rendering_task` を読取・編集ロック中に呼ぶとデッドロックし得るため、必ずロック外で待つ |
| `aviutl_get_logs` | サーバー | AviUtl2、PSDToolKit2、ブリッジ、MCPの既知ログファイルを制限付きで読取 | ファイル全体や秘密情報を返さず、行数、期間、サイズ、マスキングを適用する |
| `aviutl_diagnose` | 複合 | status、capabilities、ログ分類、IPC疎通、GCMZDrops共有メモリを集約 | 診断は原則読取専用とし、修復操作は別の明示的な編集要求にする |
| `aviutl_psd_create` | 複合 | SDKでカーソル設定後、GCMZDrops API v3のMutex・共有メモリ・`WM_COPYDATA` を使用 | GCMZDrops JSONに絶対フレーム指定はない。SDK編集と外部ドロップは単一Undo/トランザクションにできないため、投入後の再検索と事後条件検証が必須 |
| `aviutl_psd_setup` | 複合 | タイムライン検証、`最初に置くやつ@PSDToolKit` definitionを列挙し `create_object` で作成 | PSD関連オブジェクトより上に置く順序と、必要尺・衝突を検証する |
| `aviutl_psd_set_character` | 直接 | PSD関連エフェクトを検出し、公開SDKの `set_object_item_value` / `set_effect_item_value` を使用 | 固定エフェクト番号に依存せず、名称と項目列挙結果を照合する |
| `aviutl_psd_set_layer_state` | 直接 | `PSDファイル@PSDToolKit` の `レイヤー` 項目を公開SDKで取得・更新できることをPSDToolKit2ソースで確認 | 対象PSDファイルとセーフガード値を検証し、状態文字列の上限を設ける |
| `aviutl_psd_create_voice` | 複合 | WAV/TXT/LAB検証、GCMZDrops外部APIによる直接WAV/TXTまたは中間`.object`投入、`セリフ準備@PSDToolKit`生成、字幕エイリアス作成 | `external_wav_txt_pair`または`external_object_audio_text`が必要。必須character IDを生成後にSDK設定し、各生成物を事後検証する |
| `aviutl_psd_validate` | 複合 | タイムライン、エイリアス、エフェクト、項目、ファイル対応を読取り、サーバーで規則判定 | 目パチ、2方式の口パク、パーツ上書き、参照ID、初期化順序を個別結果として返す |

結論として、列挙済み32 toolsはV1の実装候補として維持できる。ただし、バッチ編集、PSD投入、音声・字幕生成は完全な原子性を保証できないため、事前検証、相関ID、事後条件検証、部分適用エラーをAPI契約へ含める。

## 5. Resources と Prompts

| 種別 | 判定 | 実装根拠 |
|---|---|---|
| 5 Resources | サーバー | 対応する読取toolの結果をURI別に整形し、未接続時も構造化状態を返す |
| 4 Prompts | サーバー | MCPサーバーが静的テンプレートと引数スキーマを公開する。AviUtl2固有APIは不要 |

## 6. V1実験機能

公開SDKで直接操作する関数を確認できていないため、次はV1必須にしない。能力検出と実機試験に合格した場合だけ実験機能として有効化する。

- プロジェクトの新規作成、任意ファイルを開く、別名保存
- 再生開始、停止
- 既存オブジェクトへのエフェクト追加、削除、並べ替え
- オブジェクト分割、長さ変更、トラック・キーフレーム編集
- シーン追加、削除、切替
- 出力開始、進捗取得、キャンセル

## 7. Phase 2へ持ち越す設計判断

1. 読取専用のプロジェクト情報取得で編集セクションを使う範囲と、GCMZDrops `ProjectPath` の優先順位
2. オブジェクトロケーターの指紋、編集リビジョン、再解決規則
3. バッチ編集の途中失敗応答と、ユーザーが1回のUndoで復旧する手順
4. GCMZDrops送信前後のカーソル競合防止、タイムアウト後の結果照合、部分生成物の報告
5. 非同期プレビューのバッファ所有権、完了通知、PNG変換、最大応答サイズ
6. PSDToolKit2の日本語名称・設定項目が変化した場合の能力無効化規則
